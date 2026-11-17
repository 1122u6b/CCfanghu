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
  var system=SystemClock.CurrentIana();var exit=exitGeoFor==exitIp&&exitGeo is not null?exitGeo.Timezone:"";
  var offset=TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);var text="UTC"+(offset<TimeSpan.Zero?"-":"+")+offset.ToString(@"hh\:mm");
  return new{system,exit,matches=exit.Length>0&&SystemClock.Matches(exit),can_change=exit.Length>0,dismissed=exit.Length>0&&store.Settings.TimezoneDismissedFor==exit,location=exitGeo is not null&&exitGeoFor==exitIp?exitGeo.Label:"",system_offset=text,original=store.Settings.OriginalTimezone is not null,lookup_loading=geoLoading,lookup_error=geoError};
 }
 async Task<object> Account(CancellationToken stop)
 {
  if(profile is null)return new{activated=false};
  var account=await Remote(new{type="account"},stop);var ai=await Remote(new{type="ai_accounts"},stop);var messages=await Remote(new{type="messages"},stop);var exits=await Remote(new{type="exits"},stop);
  var help=await Remote(new{type="help"},stop);
  var channels=help.TryGetProperty("channels",out var ch)&&ch.ValueKind==JsonValueKind.Array?ch.Clone():JsonSerializer.SerializeToElement(Array.Empty<object>());
  // Code-activated computers expire; computers set up by the owner with a connection file do not.
  long? expires=account.TryGetProperty("expires_at",out var ex)&&ex.ValueKind==JsonValueKind.Number&&ex.GetInt64()<253402300799L?ex.GetInt64():null; // bought-out codes never expire
  var daysLeft=expires is long e?(int?)Math.Max(0,(int)Math.Ceiling((e-DateTimeOffset.UtcNow.ToUnixTimeSeconds())/86400.0)):null;
  var aiversion=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ai.GetRawText())))[..16];
  int? selectedExit=exits.GetProperty("selected").ValueKind==JsonValueKind.Number?exits.GetProperty("selected").GetInt32():null;
  // Same shape the interface reads: {list:[{id,name,own,sub,lines,current}],can_add,max_own}.
  var exitItems=exits.GetProperty("items").EnumerateArray().Select(x=>new{id=x.GetProperty("id").GetInt32(),name=Text(x,"name"),own=true,sub=false,lines=x.TryGetProperty("lines",out var n)&&n.TryGetInt32(out var c)?c:1,current=x.GetProperty("id").GetInt32()==selectedExit,location="",exit_ip=""}).ToArray();
  return new{activated=true,account_id=account.GetProperty("id").GetInt32(),contact=Text(account,"contact"),plan="自有服务器",device=Environment.MachineName+" · Windows",bound_at=account.GetProperty("created_at").GetInt64(),software_expires_at=expires??253402300799L,node_expires_at=expires??253402300799L,days_left=daysLeft,own_exit=exitItems.Length==0?"waiting":"connected",suite=new{kind=Text(account,"license_kind")=="trial"?"trial":"soft"},exits=new{list=exitItems,can_add=true,max_own=5},exits_mode="soft",current_exit=selectedExit,can_add_exit=true,channels,announcements=Array.Empty<object>(),messages_unread=messages.GetProperty("unread").GetInt32(),messages_latest=messages.GetProperty("items").GetArrayLength()==0?0:messages.GetProperty("items")[0].GetProperty("id").GetInt32(),ai_accounts=ai.GetProperty("items").GetArrayLength()>0,ai_version=aiversion};
 }
 async Task<object> Doctor(bool deep,CancellationToken stop)
 {
  var items=new List<object>();
  void Add(string id,string verdict,string detail)=>items.Add(new{id,verdict,detail});
  var guarded=filters.DefaultGuardsInstalled();var live=state=="protected";
  Add("E1",Environment.OSVersion.Version.Build>=17763?"pass":"warn",System.Runtime.InteropServices.RuntimeInformation.OSDescription+" / "+System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
  Add("E2",System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem?"pass":"fail","Windows 后台服务身份");
  if(cloud is null)Add("E3","skipped","尚未连接自有服务器，无法对时");
  else try
  {
   var clock=await Remote(new{type="server_time"},stop);var drift=Math.Abs(clock.GetProperty("unix").GetDouble()-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0);
   Add("E3",!Bool(clock,"synchronized")?"info":drift<=60?"pass":drift<=300?"warn":"fail",!Bool(clock,"synchronized")?"服务器自身尚未完成时间同步，无法判断":drift<=60?"与服务器标准时间相差 "+(int)drift+" 秒":"电脑时间与标准时间相差 "+(int)drift+" 秒，建议校准");
  }
  catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidDataException or KeyNotFoundException){Add("E3","skipped","暂时取不到服务器时间："+e.Message);}
  RefreshConflicts(true);
  var conflictList=conflicts.Select(x=>JsonSerializer.SerializeToElement(x)).ToArray();
  Add("E4",conflictList.Length==0?"pass":"fail",conflictList.Length==0?"没有发现其他代理、VPN 或虚拟网卡":string.Join("；",conflictList.Select(x=>Text(x,"message"))));
  Add("E5","pass","已打开 Windows 基础过滤接口");
  var filtersFound=NetworkFilterSoftware();
  Add("E6",filtersFound.Length==0?"pass":"warn",filtersFound.Length==0?"没有发现带网络过滤功能的安全软件":"已发现带网络过滤功能的安全软件："+string.Join("、",filtersFound));
  Add("E7",KernelSession.Tunnel()!=0?"pass":live?"fail":"skipped",KernelSession.Tunnel()!=0?"独享线路虚拟网卡已建立":"虚拟网卡尚未建立");
  Add("E8",new DriveInfo(Path.GetPathRoot(AgentStore.Root)!).AvailableFreeSpace>200*1024*1024?"pass":"warn","运行目录所在磁盘的可用空间");
  Add("E9",cloud is null?"fail":"pass",cloud is null?"尚未登记自有服务器":"连接文件和独立设备身份已登记");
  Add("E10",ServiceAutoStart()?"pass":"warn",ServiceAutoStart()?"后台服务设置为开机自动运行":"后台服务没有设置为开机自动运行，请重新安装");
  Add("P1",guarded?"pass":"fail","读取本软件持久拒绝规则，包括连接层和传输层");
  bool firewall;try{using var mpssvc=new System.ServiceProcess.ServiceController("mpssvc");firewall=mpssvc.Status==System.ServiceProcess.ServiceControllerStatus.Running;}catch(InvalidOperationException){firewall=false;}
  Add("P2",firewall?"pass":"warn",firewall?"Windows 防火墙服务正在运行":"Windows 防火墙服务没有运行；防护规则仍由过滤平台执行");
  Add("P3",kernel.Alive?"pass":"fail",kernel.Alive?"内置网络进程正在运行":"内置网络进程未运行");
  Add("P4",nodes.ValueKind==JsonValueKind.Array&&nodes.GetArrayLength()>0?"pass":"fail","自有服务器下发的设备线路");
  Add("P5",live&&exitVerified&&KernelSession.Tunnel()==tunnel&&guarded?"pass":"fail",live&&exitVerified?"经虚拟网卡的请求由自有服务器观察到出口 "+exitIp:"尚未确认流量经独享线路");
  Add("P6",dnsVerified?"pass":"fail",dnsVerified?"系统解析取得私有权威检测回执":"尚未取得系统 DNS 检测回执");
  Add("P7",BrowserPolicyActive()?"pass":"warn",BrowserPolicyActive()?"Chrome 和 Edge 已限制 WebRTC 只走代理":"尚未设置浏览器保护（WebRTC 限制）");
  Add("P8",state=="paused"?"warn":"pass",state=="paused"?"防护正在暂停":"没有主动暂停防护");
  Add("L1",exitVerified?"info":"fail",exitVerified?"经本机代理取得的出口 IP："+exitIp+(exitGeo is not null&&exitGeoFor==exitIp?"（"+exitGeo.Label+"）":""):"没有取得该请求的公网出口");
  if(guarded)
  {
   var direct=await DirectTcpReachable(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.IPAddress.Parse("1.0.0.1"),stop);
   Add("L2",direct?"fail":"pass",direct?"绕开虚拟网卡、直接从物理网卡连到了公网，防护规则未生效":"从物理网卡直接连接公网被拒绝");
   var dnsLeak=await DirectDnsAnswered(stop);
   Add("L3",dnsLeak?"fail":"pass",dnsLeak?"从物理网卡发出的 DNS 查询得到了回应":"从物理网卡直接发出的 DNS 查询被拒绝");
  }
  else{Add("L2","skipped","防护规则未安装，未做直连实测");Add("L3","skipped","防护规则未安装，未做直连实测");}
  Add("L4",dnsVerified?"pass":"skipped",dnsVerified?"系统解析经自有递归服务器到达权威服务器":"尚未取得私有 DNS 回执");
  items.AddRange(BrowserChecks());
  if(guarded&&HasGlobalIpv6())
  {
   var v6=await DirectTcpReachable(System.Net.Sockets.AddressFamily.InterNetworkV6,System.Net.IPAddress.Parse("2606:4700:4700::1001"),stop);
   Add("L7",v6?"fail":"pass",v6?"IPv6 可以绕开独享线路直连公网":"电脑有公网 IPv6，直接连接已被拒绝");
  }
  else Add("L7",guarded?"pass":"skipped",guarded?"电脑没有公网 IPv6 地址":"防护规则未安装，未做 IPv6 实测");
  if(live){lastBypassScan=DateTimeOffset.MinValue;RefreshBypass();var l8=JsonSerializer.SerializeToElement(bypassCheck??ScanBypass());Add("L8",Text(l8,"verdict"),Text(l8,"detail"));}
  else Add("L8","skipped","防护建立后才检查绕开独享线路的程序");
  var exitZone=exitGeoFor==exitIp&&exitGeo is not null?exitGeo.Timezone:"";var zoneMatches=exitZone.Length>0&&SystemClock.Matches(exitZone);
  Add("C1",exitZone.Length==0?"skipped":zoneMatches?"pass":"warn",exitZone.Length==0?(geoLoading?"正在查询出口所在地的时区":geoError.Length>0?geoError:"尚未取得出口时区，暂时无法比对"):zoneMatches?"电脑时区已和出口所在地一致（"+exitZone+"）":"电脑时区和出口所在地（"+exitZone+"）不一致");
  Add("S1",profile?.Role=="device"?"pass":"fail","独立设备身份；当前不使用商业激活码");
  Add("S2",configAt>DateTimeOffset.UtcNow.AddHours(-24)?"pass":"warn",configAt==DateTimeOffset.MinValue?"本次启动后还没有取得服务器配置":"线路配置取得于 "+configAt.ToLocalTime().ToString("MM-dd HH:mm"));
  var quality=nodes.ValueKind==JsonValueKind.Array?nodes.EnumerateArray().Select(n=>Quality(Text(n,"name"))).Where(q=>q.Success is not null).ToArray():[];
  if(quality.Length==0)Add("S3","skipped","线路质量数据还在收集（每分钟测一次）");
  else{var worst=quality.Min(q=>q.Success!.Value);Add("S3",worst>=90?"pass":worst>=60?"warn":"fail","最近一小时成功率 "+worst+"%"+(quality[0].Latency is int ms?"，当前延迟 "+ms+" 毫秒":""));}
  Add("S4",lastSpeed is null?"skipped":"info",lastSpeed is {} sp?"最近测速 "+(sp.Down*8/1_000_000.0).ToString("0.0")+" Mbps":"还没有测速");
  Add("S5",profile is not null&&live?"pass":"fail",profile is not null&&live?"证书固定的加密通道已连通":"加密通道未连通");
  var age=DateTimeOffset.UtcNow-heartbeat;
  Add("S6",age<TimeSpan.FromSeconds(30)?"pass":heartbeat==DateTimeOffset.MinValue?"fail":"warn",heartbeat==DateTimeOffset.MinValue?"还没有与服务器完成同步":"最近一次与服务器同步在 "+(int)age.TotalSeconds+" 秒前");
  if(deep)
  {
   var started=DateTimeOffset.UtcNow;var failed=false;
   while(DateTimeOffset.UtcNow-started<TimeSpan.FromMinutes(1))
   {
    stop.ThrowIfCancellationRequested();if(!kernel.Alive || !filters.DefaultGuardsInstalled() || KernelSession.Tunnel()==0){failed=true;break;}await Task.Delay(1000,stop);
   }
   var leak=!failed&&await DirectTcpReachable(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.IPAddress.Parse("1.0.0.1"),stop);
   Add("L9",failed||leak?"fail":"pass",failed?"观察期间本机组件发生变化":leak?"观察结束时物理网卡可以直连公网":"一分钟内防护组件持续在线，结束时直连仍被拒绝");
  }
  else Add("L9","skipped","尚未执行一分钟观察");
  var identity="CK-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6];
  var result=new{id=identity,items=items.ToArray(),at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),kind=deep?"deep":"manual",problems=items.Count(x=>JsonSerializer.SerializeToElement(x).GetProperty("verdict").GetString() is "fail" or "warn")};
  lock(checkHistory){checkHistory.Insert(0,result);if(checkHistory.Count>10)checkHistory.RemoveAt(10);}
  Record("check",result.problems.ToString());
  return result;
 }
 async Task<object> AddExit(JsonElement request,CancellationToken stop)
 {
  if(request.TryGetProperty("node",out var direct))return await Remote(new{type="store_exit",name=Text(request,"name","我的出口"),node=direct},stop);
  var links=Text(request,"links");if(links.Trim().Length==0)throw new InvalidDataException("请粘贴出口链接或订阅地址");
  if(links.Length>200000)throw new InvalidDataException("粘贴的内容太长");
  // The server parses every format (SOCKS5/HTTP, ss, vmess, vless, trojan, hysteria2, tuic and
  // https subscriptions) because it is the side that dials them.
  int? target=request.TryGetProperty("exit_id",out var existing)&&existing.TryGetInt32(out var t)?t:null;
  var result=await Remote(new{type="import_exit_links",links,name=Text(request,"name","我的出口"),exit_id=target},stop);
  configured=false;exitCount=null;exitAskAt=DateTimeOffset.MinValue;Record("exit",Text(request,"name","我的出口"));
  store.Settings.Enabled=true;store.Settings.PausedUntil=0;store.Settings.QuitPause=false;store.Save();retryAt=DateTimeOffset.MinValue;
  return new{id=result.GetProperty("id").GetInt32(),lines=result.GetProperty("lines").GetInt32(),skipped=result.GetProperty("skipped").EnumerateArray().Select(x=>x.GetString()??"").ToArray()};
 }
 int speedActive;
 async Task<object> MeasureSpeed(CancellationToken stop)
 {
  if(state!="protected")throw new InvalidOperationException("请先建立已验证的线路");
  if(Interlocked.CompareExchange(ref speedActive,1,0)!=0)throw new InvalidOperationException("测速正在进行，请稍等");
  var generation=configAt;var line=selected;
  try
  {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(28));
  using var handler=new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:17890"),AllowAutoRedirect=false};using var http=new HttpClient(handler);
  using var headers=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);headers.CancelAfter(TimeSpan.FromSeconds(17));
  using var response=await http.GetAsync("https://speed.cloudflare.com/__down?bytes=1048576",HttpCompletionOption.ResponseHeadersRead,headers.Token);response.EnsureSuccessStatusCode();
  headers.CancelAfter(Timeout.InfiniteTimeSpan);
  using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
  var sample=await DownloadSample.MeasureAsync(input,512*1024,TimeSpan.FromSeconds(8),deadline.Token);
  if(state!="protected"||!configured||configAt!=generation||selected!=line)throw new InvalidOperationException("测速期间线路已变化，请在连接稳定后重试");
  lastSpeed=(sample.BytesPerSecond,DateTimeOffset.UtcNow);
  return new{down_bps=sample.BytesPerSecond,measured=true,ago_secs=0,sample_bytes=sample.Bytes,sample_seconds=sample.Seconds};
  }
  catch(OperationCanceledException)when(!stop.IsCancellationRequested){throw new InvalidOperationException("测速连接超时，请检查当前出口后重试");}
  finally{Interlocked.Exchange(ref speedActive,0);Publish();}
 }
 string? aiSession;int? aiIdentity;
 async Task<object> ShellAction(JsonElement request,CancellationToken stop)
 {
  var command=Text(request,"command");
  if(command=="install_webrtc_profile")return BrowserPolicies.Install();
  var args=request.GetProperty("args");
  if(command=="open_web_center")
  {
   var help=await Remote(new{type="help"},stop);var center=Text(help,"web_center");
   if(!Uri.TryCreate(center,UriKind.Absolute,out var url)||url.Scheme!="https")throw new InvalidOperationException("管理员还没有设置网页个人中心地址");
   return new{url=url.AbsoluteUri};
  }
  if(command=="test_browser")return StartBrowserTest(Text(args,"browser"));
  if(command=="quit_conflict")return new{images=ConflictImages(Text(args,"id"))};
  if(command=="force_quit_conflict")return ForceQuit(Text(args,"id"));
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

