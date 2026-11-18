using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Leyra.Windows.Core;

namespace Leyra.Mac.Agent;

sealed record GeoInfo(string Ip,string Country,string Region,string City,string Timezone,string Org)
{
 static readonly Dictionary<string,string> Names=new(){{"CN","中国"},{"HK","中国香港"},{"MO","中国澳门"},{"TW","中国台湾"},{"US","美国"},{"JP","日本"},{"KR","韩国"},{"SG","新加坡"},{"GB","英国"},{"DE","德国"},{"FR","法国"},{"NL","荷兰"},{"CA","加拿大"},{"AU","澳大利亚"},{"MY","马来西亚"},{"TH","泰国"},{"VN","越南"},{"PH","菲律宾"},{"ID","印度尼西亚"},{"IN","印度"},{"RU","俄罗斯"},{"TR","土耳其"},{"AE","阿联酋"},{"IT","意大利"},{"ES","西班牙"},{"CH","瑞士"},{"SE","瑞典"},{"BR","巴西"},{"MX","墨西哥"},{"NZ","新西兰"}};
 public string CountryName=>Names.TryGetValue(Country,out var n)?n:Country;
 public string Label=>string.Join(" · ",new[]{CountryName,City}.Where(x=>x.Length>0).Distinct());
 public bool Overseas=>Country!="CN";
}

sealed partial class Runtime
{
 // ---------- IP 归属地：经已验证的本机代理查询，看到的是出口本身 ----------
 readonly Dictionary<string,(GeoInfo Info,DateTimeOffset At)> geoCache=new();
 GeoInfo? exitGeo;string exitGeoFor="",geoError="";bool geoLoading;
 static HttpClient ProxiedClient(TimeSpan timeout)=>new(new HttpClientHandler{UseProxy=true,Proxy=new WebProxy("http://127.0.0.1:17890"),AllowAutoRedirect=false}){Timeout=timeout};
 async Task<string> ObservePublicExit(CancellationToken stop)
 {
  // No device token is sent to public services. TLS uses the ordinary trusted certificate chain.
  // An explicit proxy is mandatory here; this client has no direct-connection fallback.
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(8));
  using var http=ProxiedClient(TimeSpan.FromSeconds(8));http.MaxResponseContentBufferSize=4096;
  async Task<string> Query(string host)
  {
   using var doc=JsonDocument.Parse(await http.GetStringAsync("https://"+host+"?format=json",deadline.Token));
   var ip=Text(doc.RootElement,"ip");if(!AddressPolicy.IsLiteral(ip)||!AddressPolicy.IsPublic(ip))throw new InvalidDataException("出口核验返回无效地址");return ip;
  }
  var pending=new List<Task<string>>{Query("api.ipify.org"),Query("api64.ipify.org")};
  Exception? last=null;
  while(pending.Count>0)
  {
   var completed=await Task.WhenAny(pending);pending.Remove(completed);
   try{var ip=await completed;deadline.Cancel();foreach(var other in pending)_=other.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);return ip;}
   catch(Exception error)when(!stop.IsCancellationRequested){last=error;}
  }
  stop.ThrowIfCancellationRequested();throw new HttpRequestException("独立出口核验未成功",last);
 }
 async Task<GeoInfo> LookupGeo(string ip,CancellationToken stop)
 {
  if(!AddressPolicy.IsLiteral(ip))throw new InvalidDataException("请输入明确的 IP 地址");
  if(!AddressPolicy.IsPublic(ip))return new GeoInfo(ip,"","局域网","局域网地址","","");
  lock(geoCache)if(geoCache.TryGetValue(ip,out var hit)&&DateTimeOffset.UtcNow-hit.At<TimeSpan.FromHours(6))return hit.Info;
  if(state!="protected")throw new InvalidOperationException("防护建立后才能查询归属地");
  using var http=ProxiedClient(TimeSpan.FromSeconds(6));
  GeoInfo? info=null;
  try
  {
   using var doc=JsonDocument.Parse(await http.GetStringAsync("https://ipinfo.io/"+ip+"/json",stop));var r=doc.RootElement;
   if(Text(r,"ip")==ip&&!r.TryGetProperty("bogon",out _))info=new(ip,Text(r,"country"),Text(r,"region"),Text(r,"city"),Text(r,"timezone"),Text(r,"org"));
  }
  catch(Exception e)when(e is HttpRequestException or TaskCanceledException or JsonException){}
  if(info is null)
  {
   // A slow customer exit makes the lookup through it time out; our server looks it up directly instead.
   try{var r=await Remote(new{type="ip_geo",ip},stop);if(Text(r,"ip")==ip)info=new(ip,Text(r,"country"),Text(r,"region"),Text(r,"city"),Text(r,"timezone"),Text(r,"org"));}
   catch(Exception e)when(e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or InvalidDataException){Program.Log("exit location via server",e);}
  }
  if(info is null)
  {
   using var doc=JsonDocument.Parse(await http.GetStringAsync("https://ipwho.is/"+ip,stop));var r=doc.RootElement;
   if(!Bool(r,"success"))throw new InvalidDataException("归属地服务没有这个地址的数据");
   var tz=r.TryGetProperty("timezone",out var t)&&t.ValueKind==JsonValueKind.Object?Text(t,"id"):"";
   var org=r.TryGetProperty("connection",out var c)&&c.ValueKind==JsonValueKind.Object?Text(c,"org"):"";
   info=new(ip,Text(r,"country_code"),Text(r,"region"),Text(r,"city"),tz,org);
  }
  if(info.Country.Length is 0 or >3)throw new InvalidDataException("归属地数据无效");
  if(info.Timezone.Length>0)lock(geoCache){geoCache[ip]=(info,DateTimeOffset.UtcNow);if(geoCache.Count>256)geoCache.Remove(geoCache.MinBy(x=>x.Value.At).Key);}
  return info;
 }
 async Task<object> Geo(JsonElement request,CancellationToken stop)
 {
  var info=await LookupGeo(Text(request,"ip").Trim(),stop);
  return new{ip=info.Ip,label=info.Label,overseas=AddressPolicy.IsPublic(info.Ip)&&info.Overseas,country=info.Country,region=info.Region,city=info.City,timezone=info.Timezone,org=info.Org};
 }
 async Task RefreshExitGeo(CancellationToken stop)
 {
  if(exitIp.Length==0 || exitGeoFor==exitIp&&exitGeo?.Timezone.Length>0)return;
  var ip=exitIp;geoLoading=true;
  try
  {
   using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(20));
   var info=await LookupGeo(ip,deadline.Token);
   if(exitIp!=ip||!configured||state!="protected")return;
   exitGeo=info;exitGeoFor=ip;geoError=info.Timezone.Length==0?"归属地服务未返回出口时区":"";
   if(info.Timezone.Length>0&&TimeZoneInfo.TryConvertIanaIdToWindowsId(info.Timezone,out _)&&store.Settings.ExitTimezone!=info.Timezone)
   {store.Settings.ExitTimezone=info.Timezone;store.Save();Record("exit",info.Label);}
  }
  catch(Exception e)when(e is not OperationCanceledException || !stop.IsCancellationRequested){geoError="出口时区查询失败，将自动重试";Program.Log("exit location",e);}
  finally{geoLoading=false;}
 }

 // ---------- 线路质量：内核对每条线路的实际延迟测试 ----------
 sealed record Sample(DateTimeOffset At,int? Ms);
 readonly Dictionary<string,List<Sample>> samples=new();DateTimeOffset lastSample=DateTimeOffset.MinValue;
 (long Down,DateTimeOffset At)? lastSpeed;
 async Task SampleLines(CancellationToken stop)
 {
  if(DateTimeOffset.UtcNow-lastSample<TimeSpan.FromSeconds(60) || nodes.ValueKind!=JsonValueKind.Array)return;
  lastSample=DateTimeOffset.UtcNow;
  foreach(var name in nodes.EnumerateArray().Select(x=>Text(x,"name")).Where(x=>x.Length>0).Take(16))
  {
   int? ms=null;
   try{var r=await kernel.Get("proxies/"+Uri.EscapeDataString(name)+"/delay?timeout=3000&url="+Uri.EscapeDataString("https://www.gstatic.com/generate_204"),stop);if(r.TryGetProperty("delay",out var d)&&d.TryGetInt32(out var v)&&v>0)ms=v;}
   catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException){}
   lock(samples){if(!samples.TryGetValue(name,out var list))samples[name]=list=[];list.Add(new(DateTimeOffset.UtcNow,ms));list.RemoveAll(x=>DateTimeOffset.UtcNow-x.At>TimeSpan.FromHours(1));}
  }
 }
 (int? Latency,int? Success,int Signal) Quality(string name)
 {
  lock(samples)
  {
   if(!samples.TryGetValue(name,out var list)||list.Count==0)return (null,null,0);
   var last=list[^1].Ms;var ok=list.Count(x=>x.Ms is not null);var pct=ok*100/list.Count;
   var signal=last is null?0:last<150?4:last<300?3:last<600?2:1;
   return (last??list.LastOrDefault(x=>x.Ms is not null)?.Ms,pct,signal);
  }
 }
 object[] HistoryLines()
 {
  if(nodes.ValueKind!=JsonValueKind.Array)return [];
  lock(samples)return nodes.EnumerateArray().Select((n,i)=>(Name:Text(n,"name"),Id:(i+1).ToString())).Select(x=>(object)new{id=x.Id,points=(samples.TryGetValue(x.Name,out var l)?l:[]).Select(p=>new{ago_secs=(int)(DateTimeOffset.UtcNow-p.At).TotalSeconds,ms=p.Ms}).ToArray()}).ToArray();
 }

 // ---------- 自动更新：只认自有服务器（证书固定通道）发布的版本，下载后按 SHA-256 核对 ----------
 static string UpdateDir=>Path.Combine(AgentStore.Root,"updates");
 string? updateReady;string updateDigest="";DateTimeOffset lastUpdateCheck=DateTimeOffset.MinValue;readonly SemaphoreSlim updating=new(1,1);
 async Task CheckForUpdate(bool force,CancellationToken stop)
 {
  if(cloud is null || (!force&&DateTimeOffset.UtcNow-lastUpdateCheck<TimeSpan.FromHours(6)))return;
  if(!await updating.WaitAsync(0,stop))return;
  try
  {
   lastUpdateCheck=DateTimeOffset.UtcNow;
   var manifest=await cloud.RpcAsync(new{type="update_manifest",platform="mac"},stop);var version=Text(manifest,"version");
   if(version.Length==0 || !System.Text.RegularExpressions.Regex.IsMatch(version,"^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,6}(-[A-Za-z0-9.]{1,24})?$") || ProductInfo.Compare(version,Program.Version)<=0){updateReady=null;return;}
   var digest=Text(manifest,"sha256");var size=manifest.GetProperty("size").GetInt64();
   if(digest.Length!=64 || size is <1 or >400L*1024*1024)throw new InvalidDataException("更新清单无效");
   Directory.CreateDirectory(UpdateDir);var target=Path.Combine(UpdateDir,"Leyra-"+version+".pkg");
   if(File.Exists(target)&&Verify(target,digest,size)){updateReady=version;updateDigest=digest;return;}
   var part=target+".part";
   await using(var output=new FileStream(part,FileMode.Create,FileAccess.Write,FileShare.None))
   {
    while(output.Length<size)
    {
     var chunk=await cloud.RpcAsync(new{type="update_chunk",platform="mac",version,offset=output.Length},stop);
     var data=Convert.FromBase64String(Text(chunk,"data"));
     if(chunk.GetProperty("offset").GetInt64()!=output.Length || data.Length==0 || output.Length+data.Length>size)throw new InvalidDataException("更新数据与清单不符");
     await output.WriteAsync(data,stop);
    }
   }
   if(!Verify(part,digest,size)){File.Delete(part);throw new InvalidDataException("更新包校验失败，已删除");}
   File.Move(part,target,true);
   foreach(var old in Directory.EnumerateFiles(UpdateDir).Where(x=>x!=target))try{File.Delete(old);}catch(IOException){}
   updateReady=version;updateDigest=digest;Record("update_ready",version);
  }
  finally{updating.Release();}
 }
 static bool Verify(string path,string digest,long size)
 {
  using var file=File.OpenRead(path);if(file.Length!=size)return false;
  return CryptographicOperations.FixedTimeEquals(SHA256.HashData(file),Convert.FromHexString(digest));
 }
 object InstallUpdate()
 {
  var version=updateReady??throw new InvalidOperationException("没有已下载并核对的新版本");
  var path=Path.Combine(UpdateDir,"Leyra-"+version+".pkg");
  if(!File.Exists(path) || !Verify(path,updateDigest,new FileInfo(path).Length))throw new InvalidDataException("更新包已变化，请重新检查更新");
  // The installer restarts this service, so it runs as its own launchd job, not as a child that would be ended with it.
  var start=new ProcessStartInfo("/bin/launchctl",["submit","-l","xin.leyra.update."+DateTimeOffset.UtcNow.ToUnixTimeSeconds(),"--","/usr/sbin/installer","-pkg",path,"-target","/"]){UseShellExecute=false};
  Process.Start(start)?.Dispose();Record("update",version);return new{saved=true,version};
 }

 // ---------- 冲突软件：其他代理/VPN 客户端和虚拟网卡 ----------
 // Process names of proxy / VPN apps on the Mac.
 static readonly (string Name,string[] Images)[] KnownConflicts=[
  ("ClashX",["ClashX","ClashX Pro","ClashX Meta"]),("Clash Verge",["clash-verge","Clash Verge","verge-mihomo"]),("Clash",["clash","clash-meta"]),("FlClash",["FlClash","FlClashCore"]),
  ("Mihomo Party",["mihomo-party","Mihomo Party"]),("Surge",["Surge","Surge 5"]),("Shadowrocket",["Shadowrocket"]),("Quantumult X",["Quantumult X"]),("Stash",["Stash"]),("Loon",["Loon"]),
  ("V2rayU",["V2rayU","v2ray","xray"]),("V2RayXS",["V2RayXS"]),("sing-box",["sing-box","SFM"]),("Hiddify",["Hiddify","HiddifyCli"]),("ShadowsocksX-NG",["ShadowsocksX-NG","ss-local"]),
  ("Tunnelblick",["Tunnelblick","openvpn"]),("OpenVPN Connect",["OpenVPN Connect"]),("WireGuard",["WireGuard"]),("NordVPN",["NordVPN","NordVPN IKE"]),("ExpressVPN",["ExpressVPN","expressvpnd"]),
  ("Surfshark",["Surfshark"]),("Proton VPN",["ProtonVPN","Proton VPN"]),("雷神加速器",["leigod","LeiGod"])];
 object[] conflicts=[];DateTimeOffset lastConflictScan=DateTimeOffset.MinValue;
 object[] ScanConflicts()
 {
  // Coexist mode: the customer's own proxy app is wanted, not a conflict.
  if(coexist)return [];
  var items=new List<object>(proxy.Conflicts());var own=Path.GetFullPath(kernel.Executable);
  var running=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
  foreach(var p in Process.GetProcesses())
  {
   using(p)
   {
    var hit=KnownConflicts.FirstOrDefault(c=>c.Images.Contains(p.ProcessName,StringComparer.OrdinalIgnoreCase));
    if(hit.Name is null && !p.ProcessName.Equals("mihomo",StringComparison.OrdinalIgnoreCase))continue;
    string? path=null;try{path=p.MainModule?.FileName;}catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){}
    if(path is not null&&string.Equals(Path.GetFullPath(path),own,StringComparison.OrdinalIgnoreCase))continue;
    var name=hit.Name??"Mihomo 内核（其他软件）";
    if(!running.TryGetValue(name,out var images))running[name]=images=[];
    if(!images.Contains(p.ProcessName,StringComparer.OrdinalIgnoreCase))images.Add(p.ProcessName);
   }
  }
  foreach(var (name,images) in running)
   items.Add(new{id="app-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..10].ToLowerInvariant(),severity="warn",name,message=name+" 正在运行，可能与防护争用网络或接管系统代理。",evidence="正在运行的进程："+string.Join("、",images.Select(x=>x+".exe")),quit=new{how="windows_image",images=images.ToArray()}});
  return items.ToArray();
 }
 void RefreshConflicts(bool force=false)
 {
  if(!force&&DateTimeOffset.UtcNow-lastConflictScan<TimeSpan.FromSeconds(15))return;
  lastConflictScan=DateTimeOffset.UtcNow;try{conflicts=ScanConflicts();}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception or NetworkInformationException){}
 }
 string[] ConflictImages(string id)
 {
  RefreshConflicts(true);
  var item=conflicts.Select(x=>JsonSerializer.SerializeToElement(x)).FirstOrDefault(x=>Text(x,"id")==id);
  if(item.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("它已经退出了");
  if(!item.TryGetProperty("quit",out var quit)||quit.ValueKind!=JsonValueKind.Object||!quit.TryGetProperty("images",out var images))throw new InvalidOperationException("这一项不能自动退出，请手动处理");
  var owner=quit.TryGetProperty("owner",out var o)&&o.ValueKind==JsonValueKind.True;
  return images.EnumerateArray().Select(x=>x.GetString()??"").Where(x=>owner&&x.Length>0||KnownConflicts.Any(c=>c.Images.Contains(x,StringComparer.OrdinalIgnoreCase))||x.Equals("mihomo",StringComparison.OrdinalIgnoreCase)).ToArray();
 }
 object ForceQuit(string id)
 {
  var images=ConflictImages(id);var own=Path.GetFullPath(kernel.Executable);
  // The program behind the system proxy is matched by its folder too, so another program that merely
  // shares a name (python, node...) is never ended.
  string? folder=null;
  if(id.StartsWith("proxy-owner-",StringComparison.Ordinal))
   folder=conflicts.Select(x=>JsonSerializer.SerializeToElement(x)).Where(x=>Text(x,"id")==id).Select(x=>Path.GetDirectoryName(Text(x,"evidence"))).FirstOrDefault();
  foreach(var image in images)foreach(var p in Process.GetProcessesByName(image))using(p)
  {
   try{var path=p.MainModule?.FileName;if(path is not null&&string.Equals(Path.GetFullPath(path),own,StringComparison.OrdinalIgnoreCase))continue;
    if(folder is not null&&(path is null||!string.Equals(Path.GetDirectoryName(path),folder,StringComparison.OrdinalIgnoreCase)))continue;
    p.Kill(true);p.WaitForExit(3000);}
   catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){}
  }
  var left=images.Where(x=>Process.GetProcessesByName(x).Any(p=>{using(p)try{return folder is null||string.Equals(Path.GetDirectoryName(p.MainModule?.FileName),folder,StringComparison.OrdinalIgnoreCase);}catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){return false;}})).Select(x=>x+".exe").ToArray();
  // It was the program behind the system proxy: the proxy now points to nothing, switch it off too.
  if(id.StartsWith("proxy-owner-",StringComparison.Ordinal)&&left.Length==0){try{proxy.ClearAfterQuit();}catch(Exception e)when(e is InvalidOperationException or UnauthorizedAccessException or IOException){Program.Log("clear system proxy",e);}}
  RefreshConflicts(true);Record("repair","结束冲突软件 "+string.Join("、",images));return new{left};
 }

 // ---------- 浏览器实测：本机检测页收集 WebRTC 候选地址和浏览器看到的出口 ----------
 // On the Mac "Exe" is the application bundle under /Applications (or ~/Applications of the owner).
 static readonly (string Name,string Exe)[] Browsers=[("Google Chrome","Google Chrome.app"),("Microsoft Edge","Microsoft Edge.app"),("Firefox","Firefox.app"),("Brave","Brave Browser.app"),("Safari","Safari.app")];
 readonly Dictionary<string,object> browserResults=new();
 static string? BrowserPath(string app)
 {
  foreach(var dir in new[]{"/Applications","/System/Applications","/System/Volumes/Preboot/Cryptexes/App/System/Applications"})
  {var path=Path.Combine(dir,app);if(Directory.Exists(path))return path;}
  return null;
 }
 string[] InstalledBrowsers()=>Browsers.Where(b=>BrowserPath(b.Exe) is not null).Select(b=>b.Name).ToArray();
 object BrowserResults(){lock(browserResults)return new{installed=InstalledBrowsers(),items=browserResults.Values.Where(x=>Text(Json(x),"exit_ip")==exitIp).ToArray(),checks=BrowserChecks()};}
 object[] BrowserChecks()
 {
  JsonElement[] tested;lock(browserResults)tested=browserResults.Values.Select(Json).Where(x=>Text(x,"exit_ip")==exitIp).ToArray();
  if(tested.Length==0)return [new{id="L5",verdict="skipped",detail="还没有做当前出口的浏览器实测"},new{id="L6",verdict="skipped",detail="还没有做当前出口的浏览器实测"}];
  var real=tested.Any(x=>Text(x,"exposure")=="real");var lan=tested.Any(x=>Text(x,"exposure")=="lan");var unknown=tested.Any(x=>Text(x,"seen_ip").Length==0||Text(x,"exposure")=="unknown");
  var detail=string.Join("；",tested.Select(x=>Text(x,"browser")+"："+Text(x,"detail")));
  return [new{id="L5",verdict=real?"fail":unknown?"skipped":"pass",detail},new{id="L6",verdict=real?"fail":lan?"warn":unknown?"skipped":"pass",detail}];
 }
 object StartBrowserTest(string browser)
 {
  var entry=Browsers.FirstOrDefault(b=>b.Name==browser);var path=entry.Exe is null?null:BrowserPath(entry.Exe);
  if(path is null)throw new InvalidOperationException("没有找到这个浏览器");
  if(state!="protected")throw new InvalidOperationException("请先恢复防护再检查浏览器");
  lock(browserResults)browserResults.Remove(browser);
  return new{launch=path,url=probe.Begin(browser)};
 }
 object BrowserReport(string browser,string? seenIp,IReadOnlyList<(string Ip,string Type)> candidates)
 {
  string exposure="clean";var detail=new List<string>();
  var leaked=candidates.Where(c=>c.Type is "srflx" or "prflx" or "relay"&&AddressPolicy.IsPublic(c.Ip)&&c.Ip!=exitIp).Select(c=>c.Ip).Distinct().ToList();
  if(seenIp is not null&&AddressPolicy.IsPublic(seenIp)&&seenIp!=exitIp)leaked.Add(seenIp);
  // Coexist mode: addresses of the customer's own proxy are expected; only this computer's real address is a leak.
  var bypassed=false;
  if(coexist){bypassed=seenIp is not null&&seenIp!=exitIp&&seenIp!=realIp;leaked=leaked.Where(ip=>realIp is not null&&ip==realIp).ToList();}
  if(leaked.Count>0){exposure="real";detail.Add("看到了你的真实地址："+string.Join("、",leaked.Distinct()));}
  else if(bypassed){exposure="unknown";detail.Add("这个浏览器没有经过 Leyra（可能装了代理插件或单独设了代理），在它里面 Claude 不会走你的出口");}
  else if(candidates.Any(c=>AddressPolicy.IsLiteral(c.Ip)&&!AddressPolicy.IsPublic(c.Ip)&&!IPAddress.IsLoopback(IPAddress.Parse(c.Ip)))){exposure="lan";detail.Add("WebRTC 暴露了局域网地址");}
  else detail.Add("WebRTC 没有暴露其他地址");
  if(exposure=="clean"&&(seenIp is null||exitIp.Length==0))exposure="unknown";
  // What the customer wants to see: the address websites get from this browser, and whether it is their exit.
  var match=seenIp is not null&&exitIp.Length>0&&seenIp==exitIp;
  var ipLine=seenIp is null?"没能取得浏览器的出口 IP":"浏览器出口 IP "+seenIp+(exitIp.Length==0?"":match?"，和你的出口 IP 一致":"，和你的出口 IP "+exitIp+" 不一致");
  detail.Insert(0,ipLine);
  lock(browserResults)browserResults[browser]=new{browser,at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),exposure,detail=string.Join("；",detail),seen_ip=seenIp,exit_ip=exitIp,match};
  Record("check",exposure=="clean"?"0":"1");
  return new{exposure,seen_ip=seenIp,exit_ip=exitIp,match,detail=string.Join("；",detail)};
 }

 // ---------- 安全检查里有实际探测的项目 ----------
 static NetworkInterface[] Physical()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.Name!="LeyraConnect"&&n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)&&n.GetIPProperties().GatewayAddresses.Count>0).ToArray();
 static async Task<bool> DirectTcpReachable(AddressFamily family,IPAddress target,CancellationToken stop)
 {
  var local=Physical().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(x=>x.Address).FirstOrDefault(a=>a.AddressFamily==family&&!IPAddress.IsLoopback(a)&&!(family==AddressFamily.InterNetworkV6&&a.IsIPv6LinkLocal));
  if(local is null)return false;
  using var socket=new Socket(family,SocketType.Stream,ProtocolType.Tcp);socket.Bind(new IPEndPoint(local,0));
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(3));
  try{await socket.ConnectAsync(new IPEndPoint(target,443),deadline.Token);return true;}
  catch(Exception e)when(e is SocketException or OperationCanceledException&&!stop.IsCancellationRequested){return false;}
 }
 static async Task<bool> DirectDnsAnswered(CancellationToken stop)
 {
  var local=Physical().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(x=>x.Address).FirstOrDefault(a=>a.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a));
  if(local is null)return false;
  using var udp=new UdpClient(new IPEndPoint(local,0));
  var query=new byte[]{0x4c,0x59,1,0,0,1,0,0,0,0,0,0,7,(byte)'e',(byte)'x',(byte)'a',(byte)'m',(byte)'p',(byte)'l',(byte)'e',3,(byte)'c',(byte)'o',(byte)'m',0,0,1,0,1};
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(2));
  try{await udp.SendAsync(query,new IPEndPoint(IPAddress.Parse("8.8.8.8"),53),deadline.Token);var r=await udp.ReceiveAsync(deadline.Token);return r.Buffer.Length>=12&&r.Buffer[0]==0x4c;}
  catch(Exception e)when(e is SocketException or OperationCanceledException&&!stop.IsCancellationRequested){return false;}
 }
 static bool HasGlobalIpv6()=>Physical().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Any(a=>a.Address.AddressFamily==AddressFamily.InterNetworkV6&&!a.Address.IsIPv6LinkLocal&&!a.Address.IsIPv6SiteLocal&&(a.Address.GetAddressBytes()[0]&0xe0)==0x20);
 static readonly (string Name,string[] Images)[] Filters=[("360 安全卫士",["360Tray","ZhuDongFangYu","360rps"]),("火绒安全",["HipsDaemon","HipsTray","usysdiag"]),("腾讯电脑管家",["QQPCTray","QQPCRTP"]),("卡巴斯基",["avp"]),("ESET",["ekrn"]),("诺顿",["NortonSecurity"]),("迈克菲",["mcshield"])];
 static string[] NetworkFilterSoftware()
 {
  var names=new HashSet<string>(Process.GetProcesses().Select(p=>{using(p)return p.ProcessName;}),StringComparer.OrdinalIgnoreCase);
  return Filters.Where(f=>f.Images.Any(names.Contains)).Select(f=>f.Name).ToArray();
 }
 static bool BrowserPolicyActive()=>BrowserPolicies.Active();
 // ---------- L8：绕过独享线路的连接（读取系统 TCP 连接表及所属进程） ----------
 /// <summary>Established TCP connections from netstat (macOS has no per-row delete like Windows, so Row is null:
 /// the pf rules already drop the packets of connections made before protection started).</summary>
 static IEnumerable<(IPAddress Local,IPAddress Remote,int RemotePort,int Pid,byte[]? Row)> EstablishedTcp()
 {
  var start=new System.Diagnostics.ProcessStartInfo("/usr/sbin/netstat",["-an","-p","tcp"]){UseShellExecute=false,RedirectStandardOutput=true};
  string text;using(var p=System.Diagnostics.Process.Start(start)){if(p is null)yield break;text=p.StandardOutput.ReadToEnd();p.WaitForExit(10000);}
  static bool Split(string s,out IPAddress a,out int port)
  {
   a=IPAddress.None;port=0;var k=s.LastIndexOf('.');if(k<0)return false;
   if(!int.TryParse(s[(k+1)..],out port))return false;var host=s[..k];var pct=host.IndexOf('%');if(pct>=0)host=host[..pct];
   return IPAddress.TryParse(host,out a!);
  }
  foreach(var line in text.Split('\n'))
  {
   var f=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
   if(f.Length<6||!f[0].StartsWith("tcp")||f[5]!="ESTABLISHED")continue;
   if(Split(f[3],out var local,out _)&&Split(f[4],out var remote,out var rport))yield return (local,remote,rport,0,null);
  }
 }
 static uint SetTcpEntry(byte[] row)=>1;
 object? bypassCheck;DateTimeOffset lastBypassScan=DateTimeOffset.MinValue;int cutTotal;
 // Connections that existed before protection started keep their entry in the system table, but the
 // transport-layer guard already drops their packets (see WfpPlan). Close them so programs reconnect
 // through the tunnel at once instead of hanging, and do not report them as a leak.
 object ScanBypass()
 {
  var physical=new HashSet<IPAddress>(Physical().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address));
  var gateway=profile is null?null:IPAddress.Parse(profile.Host);
  var direct=store.Settings.DirectRules.Select(r=>System.Net.IPNetwork.Parse(r.Cidr)).ToArray();
  var stale=0;
  foreach(var c in EstablishedTcp().ToArray())
  {
   var remote=c.Remote.IsIPv4MappedToIPv6?c.Remote.MapToIPv4():c.Remote;
   if(!physical.Contains(c.Local)||!AddressPolicy.IsPublic(remote.ToString()))continue;
   if(gateway is not null&&remote.Equals(gateway))continue; // own server: gateway, private DNS
   if(direct.Any(n=>n.Contains(remote)))continue;
   stale++;
   if(c.Row is byte[] row){BitConverter.GetBytes(12u).CopyTo(row,0);if(SetTcpEntry(row)==0)cutTotal++;} // MIB_TCP_STATE_DELETE_TCB
  }
  return new{id="L8",verdict="pass",detail=stale==0?"没有绕开独享线路的连接":"防护开启前建立的 "+stale+" 条直连已被拦截"+(cutTotal>0?"，已断开 "+cutTotal+" 条让它们改走独享线路":"")};
 }
 void RefreshBypass()
 {
  if(state!="protected"||DateTimeOffset.UtcNow-lastBypassScan<TimeSpan.FromSeconds(30))return;
  lastBypassScan=DateTimeOffset.UtcNow;
  try
  {
   var result=ScanBypass();var before=bypassCheck is null?"":JsonSerializer.Serialize(bypassCheck);bypassCheck=result;
   var element=JsonSerializer.SerializeToElement(result);
   if(Text(element,"verdict")=="fail"&&JsonSerializer.Serialize(result)!=before)Record("bypass",Text(element,"detail").Split('：').Last());
  }
  catch(Exception e)when(e is System.ComponentModel.Win32Exception or NetworkInformationException or InvalidOperationException){}
 }
 static bool ServiceAutoStart()=>Installation.AutoStart();
}

