using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
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
  Application.ThreadException+=(_,e)=>MessageBox.Show(e.Exception.Message,"LEYRA",MessageBoxButtons.OK,MessageBoxIcon.Error);
  Application.Run(new MainWindow());
 }
}

sealed class MainWindow:Form
{
 const string Origin="https://app.leyra.local";
 readonly WebView2 browser=new(){Dock=DockStyle.Fill};
 readonly ConcurrentQueue<object> trayActions=new();
 readonly NotifyIcon tray=new(){Icon=SystemIcons.Shield,Text="LEYRA 连接助手",Visible=true};
 readonly SemaphoreSlim requests=new(1,1);
 bool quitting;
 public MainWindow()
 {
  Text="LEYRA 连接助手";Width=1120;Height=820;MinimumSize=new Size(900,650);StartPosition=FormStartPosition.CenterScreen;
  Controls.Add(browser);
  var menu=new ContextMenuStrip();
  menu.Items.Add("打开主窗口",null,(_,_)=>ShowMain());
  menu.Items.Add("刷新连接",null,(_,_)=>trayActions.Enqueue(new {action="refresh"}));
  menu.Items.Add("恢复防护",null,(_,_)=>trayActions.Enqueue(new {action="resume"}));
  menu.Items.Add("退出界面（后台继续运行）",null,(_,_)=>Quit());
  tray.ContextMenuStrip=menu;tray.DoubleClick+=(_,_)=>ShowMain();
  Shown+=async(_,_)=>await InitializeBrowser();
  FormClosing+=(_,_)=>{quitting=true;tray.Visible=false;tray.Dispose();};
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
   MessageBox.Show("客户端界面无法启动："+e.Message+"\n请确认 Microsoft Edge WebView2 Runtime 已安装。","LEYRA",MessageBoxButtons.OK,MessageBoxIcon.Error);
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
 async Task<JsonElement> Service(object request)
 {
  using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));
  await requests.WaitAsync(deadline.Token);
  try
  {
   using var pipe=new NamedPipeClientStream(".",PipeProtocol.Name,PipeDirection.InOut,PipeOptions.Asynchronous);
   try{await pipe.ConnectAsync(2500,deadline.Token);}
   catch(TimeoutException){throw new InvalidOperationException("无法连接本机防护服务，请检查程序安装状态");}
   NativePeer.Verify(pipe);
   await PipeProtocol.WriteAsync(pipe,new{version=1,request},deadline.Token);
   var result=await PipeProtocol.ReadAsync(pipe,deadline.Token);
   if(!result.TryGetProperty("ok",out var ok)||!ok.GetBoolean())throw new InvalidOperationException(result.TryGetProperty("error",out var error)?error.GetString():"防护服务未返回成功结果");
   return result.TryGetProperty("value",out var value)?value.Clone():JsonSerializer.SerializeToElement(new{});
  }
  finally{requests.Release();}
 }
 static JsonElement Result(object value)=>JsonSerializer.SerializeToElement(value);
 async Task<JsonElement> InvokeCommand(string command,JsonElement args)
 {
  switch(command)
  {
   case "shell_info":return Result(new{glass=false,platform="windows"});
   case "app_version":return Result("0.1.0-dev");
   case "daemon":return await Service(args.GetProperty("request"));
   case "show_window":ShowMain();return Result(new{});
   case "quit_app":BeginInvoke(new Action(Quit));return Result(new{});
   case "tray_take":var actions=new List<object>();while(trayActions.TryDequeue(out var action))actions.Add(action);return Result(actions);
   case "tray_update":return Result(new{});
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
   // Security-sensitive operations stay in the authenticated service boundary.
   case "verify_owner":case "run_rescue":case "install_webrtc_profile":case "test_browser":
   case "open_guide":case "open_web_center":case "open_login_items":case "quit_conflict":
   case "force_quit_conflict":case "ai_login":case "ai_login_poll":case "ai_login_cancel":case "relaunch_new_version":
    return await Service(new{type="shell_action",command,args});
   default:throw new InvalidOperationException("不支持的本机操作："+command);
  }
 }
 static string TextField(JsonElement args,string field,int maximum)
 {
  if(!args.TryGetProperty(field,out var v)||v.ValueKind!=JsonValueKind.String)return "";
  var text=v.GetString()??"";return text.Length>maximum?text[..maximum]:text;
 }
}
