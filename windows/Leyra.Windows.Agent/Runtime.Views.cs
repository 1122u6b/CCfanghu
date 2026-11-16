using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leyra.Windows.Core;
using Microsoft.Win32;

namespace Leyra.Windows.Agent;

sealed partial class Runtime
{
 object Zone()
 {
  var system=SystemClock.CurrentIana();return new{system,exit=locatedIp==exitIp?store.Settings.ExitTimezone:"",matches=system==store.Settings.ExitTimezone,can_change=locatedIp==exitIp&&exitIp.Length>0,dismissed=store.Settings.DismissedTimezone==store.Settings.ExitTimezone,location=exitLocation,system_offset=TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).ToString()};
 }
 async Task<object> Account(CancellationToken stop)
 {
  if(profile is null)return new{activated=false};
  var account=await Remote(new{type="account"},stop);var ai=await Remote(new{type="ai_accounts"},stop);var messages=await Remote(new{type="messages"},stop);var exits=await Remote(new{type="exits"},stop);
  var aiversion=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ai.GetRawText())))[..16];
  var exitItems=exits.GetProperty("items").EnumerateArray().Select(x=>new{id=x.GetProperty("id").GetInt32(),name=Text(x,"name"),own=true,sub=false,lines=1,region=""}).ToArray();
  return new{activated=true,account_id=account.GetProperty("id").GetInt32(),contact=Text(account,"contact"),plan="自有服务器",device=Environment.MachineName+" · Windows",bound_at=account.GetProperty("created_at").GetInt64(),software_expires_at=253402300799L,node_expires_at=253402300799L,own_exit="connected",suite=new{kind="soft"},exits=exitItems,exits_mode="soft",current_exit=exits.GetProperty("selected").ValueKind==JsonValueKind.Null?(int?)null:exits.GetProperty("selected").GetInt32(),can_add_exit=true,channels=Array.Empty<object>(),announcements=Array.Empty<object>(),messages_unread=messages.GetProperty("unread").GetInt32(),messages_latest=messages.GetProperty("items").GetArrayLength()==0?0:messages.GetProperty("items")[0].GetProperty("id").GetInt32(),ai_accounts=ai.GetProperty("items").GetArrayLength()>0,ai_version=aiversion};
 }
 async Task<object> Doctor(bool deep,CancellationToken stop)
 {
  var items=new List<object>();
  void Add(string id,string verdict,string detail)=>items.Add(new{id,verdict,detail});
  Add("E1","pass",System.Runtime.InteropServices.RuntimeInformation.OSDescription+" / "+System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
  Add("E2",System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem?"pass":"fail","Windows 后台服务身份");
  Add("E5","pass","已打开 Windows 基础过滤接口");
  Add("E8",new DriveInfo(Path.GetPathRoot(AgentStore.Root)!).AvailableFreeSpace>200*1024*1024?"pass":"warn","运行目录所在磁盘的可用空间");
  Add("E9",cloud is null?"fail":"pass",cloud is null?"尚未登记自有服务器":"连接文件和独立设备身份已登记");
  Add("P1",filters.DefaultGuardsInstalled()?"pass":"fail","读取本软件持久拒绝规则，包括连接层和传输层");
  Add("P3",kernel.Alive?"pass":"fail",kernel.Alive?"内置网络进程正在运行":"内置网络进程未运行");
  Add("P4",nodes.GetArrayLength()>0?"pass":"fail","自有服务器下发的设备线路");
  Add("P6",dnsVerified?"pass":"fail",dnsVerified?"系统解析取得私有权威检测回执":"尚未取得系统 DNS 检测回执");
  Add("P8",state=="paused"?"warn":"pass",state=="paused"?"防护正在暂停":"没有主动暂停防护");
  Add("L1",exitVerified?"info":"fail",exitVerified?"经本机代理取得的出口 IP："+exitIp:"没有取得该请求的公网出口");
  Add("C1",SystemClock.CurrentIana()==store.Settings.ExitTimezone?"pass":"warn","电脑时区与配置的出口时区对比");
  Add("S1",profile?.Role=="device"?"pass":"fail","独立设备身份；当前不使用商业激活码");
  Add("P7",BrowserPolicies.Installed()?"pass":"warn","读取 Chrome/Edge 的非代理 UDP 和 QUIC 策略；浏览器需重启后生效");
  var conflicts=ConflictPrograms.Scan();Add("E4",conflicts.Length>0||proxy.Conflicts().Length>0?"warn":"info","已检查已知网络软件进程和当前系统代理；不覆盖未知软件");
  using(var service=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LeyraConnect"))Add("E10",service?.GetValue("Start") is int start&&start==2?"pass":"warn","读取 Windows 服务自动启动设置");
  Add("S3",lineHealth.Count>0?"info":"skipped",lineHealth.Count>0?"已取得内核实际延迟历史；线路质量随网络变化":"尚未取得延迟历史");
  Add("S4",measuredSpeed.HasValue?"info":"skipped",measuredSpeed.HasValue?"最近一次主动测速 "+measuredSpeed+" 字节/秒":"尚未执行主动测速");
  Add("S5",profile is not null&&nodes.GetArrayLength()>0?"info":"skipped","通道配置使用自有 TLS 网关和固定证书；实际出口检查另列");
  Add("S6",lastHealth>DateTimeOffset.MinValue?"info":"skipped",lastHealth>DateTimeOffset.MinValue?"最近一次鉴权及出口检查："+lastHealth.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"尚未同步");
  if(cloud is not null)
  {
   try{var clock=await Remote(new{type="server_time"},stop);var difference=Math.Abs(clock.GetProperty("unix").GetDouble()-DateTimeOffset.UtcNow.ToUnixTimeSeconds());Add("E3",Bool(clock,"synchronized")&&difference<60?"pass":"warn","服务端 NTP 状态及当前时钟差："+(int)difference+" 秒");}
   catch(HttpRequestException){Add("E3","skipped","无法获取服务端同步时钟");}
   try
   {
    var browsers=await Remote(new{type="browser_results"},stop);var results=browsers.GetProperty("items");var exposed=results.EnumerateArray().Any(x=>Text(x,"exposure")=="real");Add("L10",exposed?"fail":results.GetArrayLength()>0?"info":"skipped",results.GetArrayLength()>0?"读取最近一次真实浏览器 HTTPS/WebRTC 测试；只代表当次观察":"请在浏览器实测中选择已安装浏览器开始检查");
   }
   catch(HttpRequestException){Add("L10","skipped","浏览器测试结果暂不可用");}
  }
  else{Add("E3","skipped","尚未连接服务器");Add("L10","skipped","尚未连接服务器");}
  foreach(var id in new[]{"E6","E7","P2","P5","L2","L3","L4","L5","L6","L7","L8","S2"})Add(id,"skipped","此项尚无独立实测证据，未计入通过");
  if(deep)
  {
   var started=DateTimeOffset.UtcNow;var failed=false;
   while(DateTimeOffset.UtcNow-started<TimeSpan.FromMinutes(1))
   {
    stop.ThrowIfCancellationRequested();if(!kernel.Alive || !filters.DefaultGuardsInstalled() || KernelSession.Tunnel()==0){failed=true;break;}await Task.Delay(1000,stop);
   }
   Add("L9",failed?"fail":"info",failed?"观察期间本机组件发生变化":"已观察一分钟的本机组件状态；不等同于全应用泄露测试");
  }
  else Add("L9","skipped","尚未执行一分钟观察");
  var identity="CK-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6];
  var result=new{id=identity,items=items.ToArray(),at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),kind=deep?"deep":"manual",problems=items.Count(x=>JsonSerializer.SerializeToElement(x).GetProperty("verdict").GetString() is "fail" or "warn")};
  lock(checkHistory){checkHistory.Insert(0,result);if(checkHistory.Count>10)checkHistory.RemoveAt(10);}
  return result;
 }
 async Task<object> AddExit(JsonElement request,CancellationToken stop)
 {
  if(request.TryGetProperty("node",out var direct))return await Remote(new{type="store_exit",name=Text(request,"name","我的出口"),node=direct},stop);
  var raw=Text(request,"links").Trim();
  if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri) || uri.Scheme!="socks5" || uri.Port is <1 or >65535 || string.IsNullOrEmpty(uri.Host))throw new InvalidDataException("当前网关出口需提供带用户名密码的 socks5:// 连接，其他节点格式尚未接入");
  var user=uri.UserInfo.Split(':',2);if(user.Length!=2)throw new InvalidDataException("出口连接缺少用户名或密码");
  var result=await Remote(new{type="store_exit",name=Text(request,"name","我的出口"),node=new{type="socks5",server=uri.Host,port=uri.Port,username=Uri.UnescapeDataString(user[0]),password=Uri.UnescapeDataString(user[1])}},stop);
  await Remote(new{type="select_exit",id=result.GetProperty("id").GetInt32()},stop);configured=false;return new{id=result.GetProperty("id").GetInt32(),lines=1,skipped=0};
 }
 async Task<object> MeasureSpeed(CancellationToken stop)
 {
  if(state!="protected")throw new InvalidOperationException("请先建立已验证的线路");
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(20));
  using var handler=new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:17890"),AllowAutoRedirect=false};using var http=new HttpClient(handler);
  var started=Stopwatch.StartNew();using var response=await http.GetAsync("https://speed.cloudflare.com/__down?bytes=1048576",HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
  using var input=await response.Content.ReadAsStreamAsync(deadline.Token);var bytes=new byte[32768];long count=0;int n;
  while((n=await input.ReadAsync(bytes,deadline.Token))>0){count+=n;if(count>1048576)throw new InvalidDataException("测速响应超过上限");}
  if(count==0)throw new InvalidDataException("测速没有收到数据");
  measuredSpeed=(long)(count/Math.Max(.001,started.Elapsed.TotalSeconds));speedAt=DateTimeOffset.UtcNow;speedLine=selected;
  return new{down_bps=measuredSpeed,measured=true,ago_secs=0};
 }
 string? aiSession;int? aiIdentity;
 async Task<object> ShellAction(JsonElement request,CancellationToken stop)
 {
  var command=Text(request,"command");
  if(command=="install_webrtc_profile")return BrowserPolicies.Install();
  if(command=="quit_conflict")return ConflictPrograms.Quit(Text(request.GetProperty("args"),"id"),false);
  if(command=="force_quit_conflict"){NeedOwner(request);return ConflictPrograms.Quit(Text(request.GetProperty("args"),"id"),true);}
  var args=request.GetProperty("args");
  if(command=="test_browser")
  {
   if(state!="protected"||!exitVerified)throw new InvalidOperationException("请先建立已验证的防护线路");
   var name=Text(args,"browser");if(!Leyra.Windows.Platform.BrowserCatalog.Find(store.Owner.Value).ContainsKey(name))throw new InvalidOperationException("没有找到这个浏览器");
   var test=await Remote(new{type="browser_probe_begin",browser=name,expected=exitIp},stop);return new{browser=name,url=Text(test,"url")};
  }
  if(command=="open_web_center")return await Remote(new{type="web_center_begin"},stop);
  if(command=="open_guide")
  {
   var help=await Remote(new{type="help"},stop);var guides=help.GetProperty("guides");var index=args.GetProperty("index").GetInt32();
   if(index<0||index>=guides.GetArrayLength())throw new InvalidOperationException("找不到这篇帮助文档");
   return new{url=Text(guides[index],"url")};
  }
  if(command is "ai_login" or "ai_login_poll")
  {
   if(state!="protected")return new{state="paused"};
   var id=args.GetProperty("id").GetInt32();
   if(command=="ai_login")
   {
    if(aiSession is not null)await Remote(new{type="ai_login_cancel",session=aiSession},stop);
    var started=await Remote(new{type="ai_login_begin",id},stop);aiSession=Text(started,"session");aiIdentity=id;
    return new{state="waiting",left_secs=120,open_url="https://claude.ai/login"};
   }
   if(aiSession is null || aiIdentity!=id)return new{state="timeout"};
   var result=await Remote(new{type="ai_login_poll",session=aiSession},stop);var phase=Text(result,"state");
   if(phase=="waiting")return new{state="waiting",left_secs=result.GetProperty("left_secs").GetInt32()};
   aiSession=null;aiIdentity=null;
   if(phase=="link_ready")return new{state="opened",open_url=Text(result,"url")};
   return new{state=phase=="timeout"?"timeout":"unavailable"};
  }
  if(command=="ai_login_cancel")
  {
   if(aiSession is not null)await Remote(new{type="ai_login_cancel",session=aiSession},stop);aiSession=null;aiIdentity=null;return new{saved=true};
  }
  throw new InvalidOperationException("此系统操作尚未接入："+command);
 }
}

