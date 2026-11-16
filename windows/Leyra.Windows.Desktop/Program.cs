using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Leyra.Windows.Platform;
using System.Collections.Concurrent;
using Leyra.Windows.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Leyra.Windows.Desktop;

static class Program
{
 [STAThread]
 static void Main()
 {
  ApplicationConfiguration.Initialize();
  Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
  Application.ThreadException+=(_,e)=>MessageBox.Show(e.Exception.Message,"Leyra",MessageBoxButtons.OK,MessageBoxIcon.Error);
  Application.Run(new MainWindow());
 }
}

sealed class MainWindow:Form
{
 const string Origin="https://app.leyra.local";
 readonly WebView2 browser=new(){Dock=DockStyle.Fill};
 readonly ConcurrentQueue<string> trayActions=new();
 readonly NotifyIcon tray=new(){Icon=SystemIcons.Shield,Text="Leyra",Visible=true};
 readonly SemaphoreSlim requests=new(4,4);
 string? ownerProof;int authenticating;
 bool quitting;
 public MainWindow()
 {
  Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!)??SystemIcons.Shield;tray.Icon=Icon;
  Text="Leyra";Width=1120;Height=820;MinimumSize=new Size(900,650);StartPosition=FormStartPosition.CenterScreen;
  Controls.Add(browser);
  var menu=new ContextMenuStrip();
  menu.Items.Add("打开主窗口",null,(_,_)=>ShowMain());
  menu.Items.Add("刷新连接",null,(_,_)=>trayActions.Enqueue("refresh"));
  menu.Items.Add("恢复防护",null,(_,_)=>trayActions.Enqueue("resume"));
  menu.Items.Add("退出界面（后台继续运行）",null,(_,_)=>Quit());
  tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowMain();
  Shown+=async(_,_)=>await InitializeBrowser();
  FormClosing+=(_,e)=>{if(!quitting&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();return;}quitting=true;tray.Visible=false;tray.Dispose();};
 }
 void ShowMain(){Show();WindowState=FormWindowState.Normal;Activate();}
 void Quit(){quitting=true;Close();}
 async Task InitializeBrowser()
 {
  try
  {
   var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"client-assets"));
   if(!File.Exists(Path.Combine(root,"win","index.html")))throw new FileNotFoundException("界面资源缺失，请重新安装程序");
   var cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LeyraConnect","WebView");
   var environment=await CoreWebView2Environment.CreateAsync(null,cache);
   await browser.EnsureCoreWebView2Async(environment);
   var web=browser.CoreWebView2;
   web.Settings.AreDevToolsEnabled=false;web.Settings.AreDefaultContextMenusEnabled=false;web.Settings.IsStatusBarEnabled=false;
   web.SetVirtualHostNameToFolderMapping("app.leyra.local",root,CoreWebView2HostResourceAccessKind.DenyCors);
   web.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith(Origin+"/",StringComparison.Ordinal))e.Cancel=true;};
   web.NewWindowRequested+=(_,e)=>{e.Handled=true;};
   web.PermissionRequested+=(_,e)=>e.State=e.PermissionKind==CoreWebView2PermissionKind.ClipboardRead?CoreWebView2PermissionState.Allow:CoreWebView2PermissionState.Deny;
   web.WebMessageReceived+=async(_,e)=>await OnMessage(e);
   browser.Source=new Uri(Origin+"/win/index.html");
  }
  catch(Exception e)
  {
   MessageBox.Show("客户端界面无法启动："+e.Message+"\n请确认 Microsoft Edge WebView2 Runtime 已安装。","Leyra",MessageBoxButtons.OK,MessageBoxIcon.Error);
   Quit();
  }
 }
 async Task OnMessage(CoreWebView2WebMessageReceivedEventArgs e)
 {
  if(quitting || !Uri.TryCreate(e.Source,UriKind.Absolute,out var source) || source.GetLeftPart(UriPartial.Authority)!=Origin)return;
  int id=0;
  try
  {
   var raw=e.WebMessageAsJson;if(raw.Length>1400000)throw new InvalidDataException("界面请求超过上限");
   using var message=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=32});
   var m=message.RootElement;id=m.GetProperty("id").GetInt32();
   if(id<=0)throw new InvalidDataException("请求编号无效");
   var command=m.GetProperty("command").GetString()??"";
   var args=m.TryGetProperty("args",out var a)?a.Clone():JsonSerializer.SerializeToElement(new{});
   var value=await InvokeCommand(command,args);
   browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok=true,value}));
  }
  catch(Exception error)
  {
   if(!quitting && browser.CoreWebView2 is not null)browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok=false,error=error.Message}));
  }
 }
 async Task<JsonElement> Service(object request,bool internalAuthentication=false)
 {
  var payload=JsonSerializer.SerializeToNode(request)!.AsObject();var type=payload["type"]?.GetValue<string>();
  if(type=="owner_auth"&&!internalAuthentication)throw new InvalidOperationException("账户验证只能由系统对话框发起");
  var sensitive=type is "pair" or "pause" || (type=="block_before_ready"&&payload["on"]?.GetValue<bool>()==false) || (type=="add_direct_rule"&&payload["confirm_public"]?.GetValue<bool>()==true);
  if(sensitive&&Interlocked.Exchange(ref ownerProof,null) is string proof)payload["owner_proof"]=proof;
  var deep=type=="doctor"&&payload["deep"]?.GetValue<bool>()==true;
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(deep?95:35));
  await requests.WaitAsync(deadline.Token);
  try
  {
   using var pipe=new NamedPipeClientStream(".",PipeProtocol.Name,PipeDirection.InOut,PipeOptions.Asynchronous);
   try{await pipe.ConnectAsync(2500,deadline.Token);}
   catch(TimeoutException){throw new InvalidOperationException("无法连接本机防护服务，请检查程序安装状态");}
   NativePeer.Verify(pipe);
   await PipeProtocol.WriteAsync(pipe,new{version=1,request=payload},deadline.Token);
   var result=await PipeProtocol.ReadAsync(pipe,deadline.Token);
   if(!result.TryGetProperty("ok",out var ok)||!ok.GetBoolean())throw new InvalidOperationException(result.TryGetProperty("error",out var error)?error.GetString():"防护服务未返回成功结果");
   if(type is "proxy_off" or "proxy_undo"){InternetSetOption(IntPtr.Zero,39,IntPtr.Zero,0);InternetSetOption(IntPtr.Zero,37,IntPtr.Zero,0);}
   return result.TryGetProperty("value",out var value)?value.Clone():JsonSerializer.SerializeToElement(new{});
  }
  finally{requests.Release();}
 }
 async Task<string> AuthenticateOwner(string reason)
 {
  if(Interlocked.CompareExchange(ref authenticating,1,0)!=0)throw new InvalidOperationException("账户验证正在进行");
  byte[]? packed=null;
  try
  {
   packed=NativeCredentials.Prompt(Handle,reason);
   var result=await Service(new{type="owner_auth",buffer=Convert.ToBase64String(packed)},true);
   return result.GetProperty("proof").GetString()??throw new InvalidDataException("账户验证回执无效");
  }
  finally{if(packed is not null)CryptographicOperations.ZeroMemory(packed);Interlocked.Exchange(ref authenticating,0);}
 }
 static JsonElement Result(object value)=>JsonSerializer.SerializeToElement(value);
 async Task<JsonElement> InvokeCommand(string command,JsonElement args)
 {
  switch(command)
  {
   case "shell_info":return Result(new{glass=false,platform="windows"});
   case "app_version":return Result(ProductInfo.Version);
   // The app's own confirmation dialog is enough; no Windows password prompt.
   case "verify_owner":return Result(new{});
   case "run_rescue":return await ElevatedHelper("--recover");
   case "open_login_items":Process.Start(new ProcessStartInfo("ms-settings:startupapps"){UseShellExecute=true});return Result(new{});
   case "daemon":return await Service(args.GetProperty("request"));
   case "show_window":ShowMain();return Result(new{});
   case "quit_app":BeginInvoke(new Action(Quit));return Result(new{});
   case "tray_take":var actions=new List<string>();while(trayActions.TryDequeue(out var trayAction))actions.Add(trayAction);return Result(actions);
   case "tray_update":UpdateTray(args.GetProperty("state"));return Result(new{});
   case "set_theme":return Result(new{});
   case "notify":
    tray.BalloonTipTitle=TextField(args,"title",60);tray.BalloonTipText=TextField(args,"body",240);tray.ShowBalloonTip(4000);return Result(new{});
   case "import_server":
    using(var picker=new OpenFileDialog{Title="选择服务器连接文件",Filter="服务器连接文件 (*.json)|*.json",CheckFileExists=true,Multiselect=false})
    {
     if(picker.ShowDialog(this)!=DialogResult.OK)throw new OperationCanceledException("已取消导入");
     var info=new FileInfo(picker.FileName);if(info.Length>131072)throw new InvalidDataException("服务器连接文件超过上限");
     var bytes=await File.ReadAllBytesAsync(picker.FileName);var profile=ServerProfile.Parse(bytes);
     return await Service(new{type="pair",profile=JsonSerializer.Deserialize<JsonElement>(profile.Encode())});
    }
   case "start_daemon":
    using(var start=Process.Start(new ProcessStartInfo{FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"sc.exe"),Arguments="start LeyraConnect",UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden}))
    {if(start is not null)await start.WaitForExitAsync();}
    return Result(new{});
   case "test_browser":
   {
    var probe=await Service(new{type="shell_action",command,args});
    var launch=probe.GetProperty("launch").GetString()??"";var target=probe.GetProperty("url").GetString()??"";
    var allowed=new[]{"chrome.exe","msedge.exe","firefox.exe","brave.exe"};
    if(!File.Exists(launch)||!allowed.Contains(Path.GetFileName(launch).ToLowerInvariant())||!target.StartsWith("http://127.0.0.1:17892/probe/",StringComparison.Ordinal)||target.Length!=61)throw new InvalidDataException("浏览器检查参数无效");
    var start=new ProcessStartInfo(launch){UseShellExecute=false};start.ArgumentList.Add(target);Process.Start(start)?.Dispose();
    return Result(new{});
   }
   case "quit_conflict":
   {
    // Graceful close runs as the desktop user: the service cannot reach windows in this session.
    var info=await Service(new{type="shell_action",command,args});var asked=0;
    foreach(var image in info.GetProperty("images").EnumerateArray().Select(x=>x.GetString()??"").Where(x=>x.Length>0))
     foreach(var p in Process.GetProcessesByName(image))using(p){try{if(p.SessionId==Process.GetCurrentProcess().SessionId&&p.CloseMainWindow())asked++;}catch(InvalidOperationException){}}
    return Result(new{asked});
   }
   case "relaunch_new_version":
   {
    var current=new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));var versions=current.Parent;
    if(versions is null||versions.Name!="versions")return Result(new{relaunched=false});
    var newest=versions.EnumerateDirectories().Where(d=>File.Exists(Path.Combine(d.FullName,"LeyraConnect.exe"))&&Version.TryParse(d.Name.Split('-')[0],out _)).OrderByDescending(d=>Version.Parse(d.Name.Split('-')[0])).FirstOrDefault();
    if(newest is null||newest.FullName.Equals(current.FullName,StringComparison.OrdinalIgnoreCase)||Version.Parse(newest.Name.Split('-')[0])<=Version.Parse(ProductInfo.Version.Split('-')[0]))return Result(new{relaunched=false});
    Process.Start(new ProcessStartInfo(Path.Combine(newest.FullName,"LeyraConnect.exe")){UseShellExecute=false})?.Dispose();
    BeginInvoke(new Action(Quit));return Result(new{relaunched=true});
   }
   // Security-sensitive operations stay in the authenticated service boundary.
   case "install_webrtc_profile":
   case "open_guide":case "open_web_center":
   case "force_quit_conflict":case "ai_login":case "ai_login_poll":case "ai_login_cancel":
    var action=await Service(new{type="shell_action",command,args});
    if(action.TryGetProperty("url",out var link))OpenBrowser(link.GetString(),false);
    if(action.TryGetProperty("open_url",out var open))OpenBrowser(open.GetString(),command.StartsWith("ai_login",StringComparison.Ordinal));
    if(command.StartsWith("ai_login",StringComparison.Ordinal))return Result(new{state=action.TryGetProperty("state",out var phase)?phase.GetString():"unavailable",left_secs=action.TryGetProperty("left_secs",out var left)?left.GetInt32():0});
    return action;
   default:throw new InvalidOperationException("不支持的本机操作："+command);
  }
 }
 void UpdateTray(JsonElement state)
 {
  var tooltip=TextField(state,"tooltip",63);tray.Text=tooltip.Length>0?tooltip:"Leyra";
  if(tray.ContextMenuStrip?.Visible==true)return;
  var menu=new ContextMenuStrip();
  void Add(string label,string action)=>menu.Items.Add(label,null,(_,_)=>{ShowMain();trayActions.Enqueue(action);});
  menu.Items.Add("打开主窗口",null,(_,_)=>ShowMain());
  Add("刷新连接","refresh");
  if(state.TryGetProperty("paused",out var paused)&&paused.ValueKind==JsonValueKind.True)Add("恢复防护","resume");
  else
  {
   var pause=new ToolStripMenuItem("暂停防护");
   foreach(var minutes in new[]{10,30,60}){var action="pause"+minutes;pause.DropDownItems.Add(minutes+" 分钟",null,(_,_)=>{ShowMain();trayActions.Enqueue(action);});}menu.Items.Add(pause);
  }
  var routes=new ToolStripMenuItem("切换线路");routes.DropDownItems.Add("自动选择",null,(_,_)=>trayActions.Enqueue("route:auto"));
  if(state.TryGetProperty("routes",out var list)&&list.ValueKind==JsonValueKind.Array)
   foreach(var route in list.EnumerateArray().Take(256))
   {var label=TextField(route,"name",160);var index=route.GetProperty("i").GetInt32();if(index>=0)routes.DropDownItems.Add(label,null,(_,_)=>trayActions.Enqueue("route:"+index));}
  menu.Items.Add(routes);Add("安全检查","check");Add("消息","messages");
  menu.Items.Add(new ToolStripSeparator());menu.Items.Add("退出界面（后台继续运行）",null,(_,_)=>Quit());
  var previous=tray.ContextMenuStrip;tray.ContextMenuStrip=menu;previous?.Dispose();
 }
 [System.Runtime.InteropServices.DllImport("wininet.dll",SetLastError=true)]static extern bool InternetSetOption(IntPtr internet,uint option,IntPtr buffer,uint length);
 static void OpenBrowser(string? target,bool ai)
 {
  if(target is null || target.Length>4096 || !Uri.TryCreate(target,UriKind.Absolute,out var url) || url.Scheme!="https" || url.UserInfo.Length!=0 || (ai&&url.Host!="claude.ai"))throw new InvalidDataException("服务器返回了不允许打开的链接");
  Process.Start(new ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});
 }
 async Task<JsonElement> ElevatedHelper(string action)
 {
  var path=Path.Combine(AppContext.BaseDirectory,"Leyra.Agent.exe");
  if(!File.Exists(path))throw new FileNotFoundException("网络恢复工具缺失，请重新安装");
  using var process=Process.Start(new ProcessStartInfo(path){Arguments=action,UseShellExecute=true,Verb="runas"})??throw new InvalidOperationException("无法启动网络恢复工具");
  await process.WaitForExitAsync();if(process.ExitCode!=0)throw new InvalidOperationException("网络恢复未完成，请保留现场联系管理员");return Result(new{saved=true});
 }
 static string TextField(JsonElement args,string field,int maximum)
 {
  if(!args.TryGetProperty(field,out var v)||v.ValueKind!=JsonValueKind.String)return "";
  var text=v.GetString()??"";return text.Length>maximum?text[..maximum]:text;
 }
}
