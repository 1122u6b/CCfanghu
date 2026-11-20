using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leyra.Windows.Core;
using Leyra.Windows.Platform;

namespace Leyra.Windows.Agent;

sealed partial class Runtime:IDisposable
{
 readonly SystemProxy proxy;readonly CoexistProxy coexistProxy;readonly AgentStore store;readonly SemaphoreSlim gate=new(1,1);readonly KernelSession kernel=new();readonly WfpEngine filters=new();readonly OwnerProof ownerProof=new();
 ServerProfile? profile;PrivateServerClient? cloud;JsonElement nodes=JsonSerializer.SerializeToElement(Array.Empty<object>());
 string state="starting",reason="route",lastError="",exitIp="",selected="",mode="auto";
 int? exitCount;DateTimeOffset exitAskAt=DateTimeOffset.MinValue;
 DateTimeOffset heartbeat=DateTimeOffset.MinValue,lastHealth=DateTimeOffset.MinValue,guardSince=DateTimeOffset.MinValue;
 bool configured,dnsVerified,exitVerified;long up,down;ulong tunnel;
 sealed record Snapshot(JsonElement Data,DateTimeOffset Heartbeat,bool Protected);
 volatile Snapshot snapshot=new(JsonSerializer.SerializeToElement(new{version=Program.Version,protection=new{state="starting"}}),DateTimeOffset.MinValue,false);
 DateTimeOffset retryAt=DateTimeOffset.MinValue;int failures;
 long? licenseExpiresAt; // unix seconds; null = bought out (never expires) or not known yet
 DateTimeOffset expiryCheckAt=DateTimeOffset.MinValue;
 // This computer's clock says the code has run out: the server decides (a clock set ahead must not cut
 // protection). Server says expired (403) -> stop now; server still serves this device -> check again in a minute.
 async Task ConfirmExpiry(CancellationToken stop)
 {
  expiryCheckAt=DateTimeOffset.UtcNow.AddMinutes(1);
  if(cloud is null)return;
  using var limit=CancellationTokenSource.CreateLinkedTokenSource(stop);limit.CancelAfter(TimeSpan.FromSeconds(10));
  try{NoteExpiry(await cloud.RpcAsync(new{type="account"},limit.Token));}
  catch(HttpRequestException e)when(e.StatusCode==HttpStatusCode.Forbidden){throw new LicenseExpiredException();}
  catch(Exception e)when(e is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException&&!stop.IsCancellationRequested){/* 网络问题：下次再确认 */}
 }
 void NoteExpiry(JsonElement account)
 {
  licenseExpiresAt=account.TryGetProperty("expires_at",out var ex)&&ex.ValueKind==JsonValueKind.Number&&ex.GetInt64()<253402300799L?ex.GetInt64():null;
 }
 readonly List<object> events=[];readonly List<object> checkHistory=[];
 readonly BrowserProbe probe;DateTimeOffset configAt=DateTimeOffset.MinValue;
 public Runtime(AgentStore store)
 {
  this.store=store;proxy=new SystemProxy(store.Owner);coexistProxy=new CoexistProxy(store.Owner);profile=store.Profile();if(profile is not null)cloud=new PrivateServerClient(profile);
  probe=new BrowserProbe(BrowserReport);Publish();
 }
 static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
 static string Text(JsonElement value,string field,string fallback="")=>value.TryGetProperty(field,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??fallback:fallback;
 static bool Bool(JsonElement value,string field,bool fallback=false)=>value.TryGetProperty(field,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():fallback;
 void Record(string kind,string detail="")
 {
  lock(events){events.Add(new{at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),kind,detail});if(events.Count>100)events.RemoveAt(0);}
 }
 void NeedOwner(JsonElement request)
 {
  if(!store.Settings.RequireOwnerPassword)return;
  if(!ownerProof.Consume(Text(request,"owner_proof")))throw new UnauthorizedAccessException("请先验证当前电脑账户后重试");
 }
 public async Task Run(CancellationToken stop)
 {
  InstallAtBoot();
  _=Task.Run(()=>UpdateLoop(stop),CancellationToken.None);
  while(!stop.IsCancellationRequested)
  {
   // Helper checks must never take the protection loop down with them.
   try{RefreshConflicts();RefreshBypass();}
   catch(Exception e)when(e is not OperationCanceledException){Program.Log("helper check failed",e);}
   await gate.WaitAsync(stop);
   try
   {
    if(failoverRequested&&profile is not null)
    {
     failoverRequested=false;
     try{await Failover(stop);}
     catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested)
     {
      // Could not reach the server to switch (the whole network may be down): no switching for a minute,
      // the normal cut-off and retry takes over.
      failoverPausedUntil=DateTimeOffset.UtcNow.AddMinutes(1);triedExits.Clear();Program.Log("switching exit failed",e);
     }
    }
    // Not activated: nothing of Leyra may block the network (blocking before activation is never installed).
    // Rules left by an earlier installation name the old program path and would block this service too:
    // the activation then times out. Remove them.
    if(profile is null){state="not_activated";if(filters.DefaultGuardsInstalled()){filters.RemoveOwned();Program.Log("removed blocking rules left by an earlier installation",new Exception("not activated"));}}
    else if(await OwnExitMissing(stop)){kernel.Stop();Unguard();configured=false;state="provisioning";reason="own_exit";}
    else if(!store.Settings.Enabled || store.Settings.PausedUntil>DateTimeOffset.UtcNow.ToUnixTimeSeconds()){kernel.Stop();Unguard();configured=false;state="paused";}
    else if(!kernel.Alive || !configured){if(DateTimeOffset.UtcNow>=retryAt)await StartProtected(stop);}
    else
    {
     if(licenseExpiresAt is long until&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()>=until&&DateTimeOffset.UtcNow>=expiryCheckAt)await ConfirmExpiry(stop);
     if(DateTimeOffset.UtcNow-lastHealth>TimeSpan.FromSeconds(5))await Health(stop);
    }
   }
   catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
   catch(OperationCanceledException)when(startInterrupted){startInterrupted=false;kernel.Stop();configured=false;}
   catch(Exception e){Block(e);}
   finally{Publish();gate.Release();}
   // The check through the customer's exit runs without holding the lock, so pausing,
   // quitting and the window's other requests are never queued behind a slow exit.
   if(state=="protected"&&configured&&DateTimeOffset.UtcNow>=nextNetProbe&&(networkTask is null||networkTask.IsCompleted))
   {
    networkTask=Task.Run(async()=>{try{await NetworkProbe(stop);}catch(OperationCanceledException)when(stop.IsCancellationRequested){}catch(Exception e){Program.Log("network probe",e);}},CancellationToken.None);
   }
   try{await Task.Delay(1000,stop);}catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
  }
  if(networkTask is not null)await networkTask;
 }
 async Task UpdateLoop(CancellationToken stop)
 {
  try{await Task.Delay(TimeSpan.FromMinutes(2),stop);}catch(OperationCanceledException){return;}
  while(!stop.IsCancellationRequested)
  {
   try{if(state=="protected")await CheckForUpdate(false,stop);}
   catch(Exception e)when(e is not OperationCanceledException){lastUpdateCheck=DateTimeOffset.UtcNow.AddHours(-5);}
   try{await Task.Delay(TimeSpan.FromMinutes(10),stop);}catch(OperationCanceledException){return;}
  }
 }
 // Coexist mode: the customer's own proxy app stays the way out for everything but the listed sites.
 string? realIp; // coexist mode: this computer's own address (asked directly), to tell real leaks from the customer's proxy
 bool coexist;CoexistProxy.Upstream? upstream;
 void Unguard(){filters.RemoveOwned();appGuardKey="";coexistProxy.Restore();}
 // Coexist mode: the Claude desktop app is locked to 127.0.0.1 (Leyra) and browsers may not send UDP to the internet.
 string appGuardKey="";
 void ApplyAppGuard(bool force=false)
 {
  var (claude,browsers)=GuardedApps();var key=string.Join("|",claude)+"#"+string.Join("|",browsers);
  if(!force&&key==appGuardKey)return;
  filters.ApplyAppGuard(claude,browsers);appGuardKey=key;
 }
 /// <summary>Coexist mode while the line is down or being rebuilt: Leyra stays the system proxy with a holding
 /// configuration — Claude (and the customer's list) is refused, everything else goes on to the customer's own
 /// proxy. Claude therefore never leaves through anything but the exit. If even this cannot start, the system
 /// proxy still points at Leyra: Claude stays unreachable rather than using another address.</summary>
 void HoldCoexist()
 {
  if(upstream is null){Unguard();return;}
  // Coming from normal protection: its blocking rules would cut the customer's own proxy. The per-program rules
  // replace them (Claude locked to Leyra, no browser UDP to the internet); the system proxy stays on Leyra.
  try{ApplyAppGuard(true);}catch(Exception e)when(e is not OutOfMemoryException){Program.Log("coexist program rules",e);}
  try
  {
   var reject=MihomoConfiguration.ClaudeAlways.Concat(MihomoConfiguration.Expand(store.Settings.ProxyDomains)).Distinct().Select(d=>MihomoConfiguration.Rule(d,"REJECT"));
   var hold=new Dictionary<string,object>{{"mixed-port",17890},{"allow-lan",false},{"bind-address","127.0.0.1"},{"mode","rule"},{"log-level","warning"},
    {"external-controller","127.0.0.1:19090"},{"secret",kernel.Secret},{"ipv6",false},
    {"proxies",new object[]{new Dictionary<string,object>{{"name","本机原代理"},{"type",upstream.Type},{"server","127.0.0.1"},{"port",upstream.Port},{"udp",false}}}},
    {"rules",reject.Append("MATCH,本机原代理").ToArray()},{"dns",new{enable=false}},{"tun",new{enable=false}}};
   using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
   kernel.Start(JsonSerializer.SerializeToUtf8Bytes(hold),limit.Token).GetAwaiter().GetResult();
   coexistProxy.Take();
  }
  catch(Exception e)when(e is not OutOfMemoryException){Program.Log("coexist holding configuration",e);}
 }
 void ApplyGuard(ulong luid)
 {
  if(profile is null)return;
  // No filtering rules in coexist mode: they would cut the customer's own proxy. While the line is down the
  // customer's own proxy settings are back, so their network keeps working (never the bare real network).
  if(coexist){HoldCoexist();return;}
  var executable=File.Exists(kernel.Executable)?kernel.Executable:Environment.ProcessPath!;
  var endpoint=new System.Net.IPEndPoint(IPAddress.Parse(profile.Host),profile.Port);
  filters.Apply(WfpPlan.Create(executable,Environment.ProcessPath!,[new GatewayEndpoint(endpoint,6),..directEndpoints],[endpoint],luid,store.Settings.DirectRules,store.Settings.ProxyOnlyListed));
 }
 async Task StartProtected(CancellationToken stop)
 {
  if(profile is null || cloud is null)return;
  upstream=coexistProxy.Find();coexist=upstream is not null;
  state="starting";dnsVerified=false;exitVerified=false;Publish();ApplyGuard(0);
  if(startCancel.IsCancellationRequested){startCancel.Dispose();startCancel=new();}
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop,startCancel.Token);deadline.CancelAfter(TimeSpan.FromSeconds(90));var ct=deadline.Token;
  var config=await cloud.SendAsync("/api/v1/config",cancellation:ct);nodes=config.GetProperty("proxies").Clone();configAt=DateTimeOffset.UtcNow;
  // 「本机直连」: the server handed this exit's own nodes; the firewall lets the kernel reach exactly their addresses.
  directExit=config.TryGetProperty("direct_exit",out var de)&&de.ValueKind==JsonValueKind.True;
  directEndpoints=directExit?nodes.EnumerateArray().Select(n=>new GatewayEndpoint(new System.Net.IPEndPoint(IPAddress.Parse(Text(n,"server")),n.GetProperty("port").GetInt32()),(byte)(Text(n,"type") is "hysteria2" or "tuic"?17:6))).Distinct().Take(32).ToArray():[];
  if(!config.TryGetProperty("identity",out var identity)||Text(identity,"role")!="device"||Text(identity,"id")!=profile.DeviceId)throw new InvalidDataException("服务器没有返回当前设备的独立身份");
  MihomoConfiguration.ValidateNodes(nodes);
  try{NoteExpiry(await cloud.RpcAsync(new{type="account"},ct));}catch(Exception e)when(e is not OperationCanceledException){Program.Log("reading the authorization expiry",e);}
  var remoteRules=await Remote(new{type="direct_rules"},ct);ReadRules(remoteRules);
  var dns=PrivateDns.Parse(await cloud.RpcAsync(new{type="dns_config"},ct),profile);
  // The gateway accepts multiplexed connections: new connections then cost one round trip to Hong Kong.
  var mux=config.TryGetProperty("mux",out var muxKind)&&muxKind.ValueKind==JsonValueKind.String&&muxKind.GetString()=="yamux";
  if(coexist&&upstream is not null)
  {
   // Local proxy only: listed sites through the exit, everything else to the customer's own proxy.
   var built=System.Text.Json.Nodes.JsonNode.Parse(MihomoConfiguration.Build(nodes,kernel.Secret,false,null,store.Settings.DirectRules,mux,store.Settings.ProxyDomains))!.AsObject();
   ((System.Text.Json.Nodes.JsonArray)built["proxies"]!).Add(new System.Text.Json.Nodes.JsonObject{["name"]="本机原代理",["type"]=upstream.Type,["server"]="127.0.0.1",["port"]=upstream.Port,["udp"]=false});
   var rules=(System.Text.Json.Nodes.JsonArray)built["rules"]!;rules[rules.Count-1]="MATCH,本机原代理";
   await kernel.Start(System.Text.Encoding.UTF8.GetBytes(built.ToJsonString()),ct);
   coexistProxy.Take();ApplyAppGuard(true);dnsVerified=true;tunnel=0;
   try{using var direct=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(10)};var ip=(await direct.GetStringAsync("https://api.ipify.org",ct)).Trim();realIp=System.Net.IPAddress.TryParse(ip,out _)?ip:null;}
   catch(Exception e)when(e is HttpRequestException or TaskCanceledException){realIp=null;}
  }
  else
  {
  await kernel.Start(MihomoConfiguration.Build(nodes,kernel.Secret,true,dns,store.Settings.DirectRules,mux,store.Settings.ProxyOnlyListed?store.Settings.ProxyDomains:null),ct);
  for(var i=0;i<40;i++){tunnel=KernelSession.Tunnel();if(tunnel!=0)break;await Task.Delay(200,ct);}
  if(tunnel==0)throw new InvalidOperationException("没有找到已启动的本机 TUN 接口");
  ApplyGuard(tunnel);await VerifyDns(ct);
  }
  for(var attempt=1;;attempt++)
  {
   try{exitIp=await ObserveExit(ct);exitVerified=true;break;}
   catch(Exception e)when(attempt<3&&e is not OperationCanceledException){await Task.Delay(1500,ct);}
  }
  netFailures=0;nextNetProbe=DateTimeOffset.UtcNow;
  configured=true;lastError="";guardSince=DateTimeOffset.UtcNow;Record("protected");
  // Chrome / Edge WebRTC only through the protected line; restored on uninstall.
  try{if(!BrowserPolicyActive())BrowserPolicies.Install();}catch(Exception e)when(e is UnauthorizedAccessException or IOException or System.Security.SecurityException){Program.Log("browser policy",e);}
  await Health(ct);
 }
 // Right after the kernel starts, the first lookup through the private resolver (over the exit and
 // the server) is often slow. Up to 3 tries of 12 s, each with a fresh probe name. Never skipped,
 // never reported as passed without the server's receipt; each failure names the stage (no secrets).
 async Task VerifyDns(CancellationToken stop)
 {
  if(cloud is null)throw new InvalidOperationException("尚未连接服务器");
  for(var attempt=1;;attempt++)
  {
   var stage="begin";
   try
   {
    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(12));
    var probe=await cloud.RpcAsync(new{type="dns_probe_begin"},deadline.Token);
    stage="system lookup";
    var addresses=await Dns.GetHostAddressesAsync(probe.GetProperty("name").GetString()!,deadline.Token);
    var expected=Text(probe,"answer");
    if(!IPAddress.TryParse(expected,out var wanted)||!addresses.Any(x=>x.Equals(wanted)))throw new InvalidDataException("系统 DNS 检测没有返回预期答案");
    stage="server receipt";
    var result=await cloud.RpcAsync(new{type="dns_probe_result",nonce=Text(probe,"nonce")},deadline.Token);
    if(!Bool(result,"matched"))throw new InvalidDataException("私有 DNS 检测回执与预期解析来源不符");
    dnsVerified=true;return;
   }
   catch(Exception e)when(!stop.IsCancellationRequested&&e is OperationCanceledException or System.Net.Sockets.SocketException or HttpRequestException or InvalidDataException or IOException)
   {
    Program.Log("dns check attempt "+attempt+"/3 failed at "+stage,e);
    if(attempt>=3)throw new InvalidOperationException("系统 DNS 检测 3 次都没有通过（最后卡在："+stage+"）",e);
    await Task.Delay(1000,stop);
   }
  }
 }
 async Task<string> ObserveExit(CancellationToken stop)
 {
  if(profile is null)throw new InvalidOperationException("尚未连接服务器");
  // This explicit proxy cannot fall back to a direct socket. The same pinned TLS gateway
  // observes the outgoing connection from the selected upstream back to our server.
  var current=profile;
  return await EgressProbe.ObserveAsync(async ct=>
  {
   using var probe=new PrivateServerClient(current,TimeSpan.FromSeconds(8),throughLocalProxy:true);
   var result=await probe.SendAsync("/api/v1/egress-probe",cancellation:ct);
   return Text(result,"observed_source");
  },ObservePublicExit,stop);
 }
 void Block(Exception e)
 {
  lastError=e.Message;configured=false;dnsVerified=false;exitVerified=false;netFailures=0;kernel.Stop();
  // The authorization code ran out: protection stops at once, the blocking rules stay (the real IP is not
  // exposed) and the window asks to renew. Not a line fault, so no switching and no fast retries.
  var expired=e is LicenseExpiredException;
  var fatal=expired||e is HttpRequestException hr&&hr.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
  // Another of the customer's own exits not tried yet: switch to it instead of cutting the network.
  // The blocking rules stay on while switching, so the real IP is never used.
  var switching=!fatal&&store.Settings.AutoSwitchExit&&blockReason is null&&exitCount is int count&&count>1&&triedExits.Count<count&&DateTimeOffset.UtcNow>=failoverPausedUntil;
  try{ApplyGuard(0);state=switching?"starting":"blocked";reason=expired?"software_expired":e is HttpRequestException h?h.StatusCode switch{HttpStatusCode.Forbidden=>"software_expired",HttpStatusCode.Unauthorized=>"device_unbound",_=>"all_lines_down"}:"all_lines_down";}
  catch{state="unprotected";reason="guard_failed";switching=false;}
  // The customer has more than one exit: say whether none of them works or switching was turned off.
  if(!fatal&&!switching&&reason=="all_lines_down")reason=blockReason??(exitCount is int n&&n>1?(store.Settings.AutoSwitchExit?"all_exits_down":"exit_down"):"all_lines_down");
  blockReason=null;
  if(switching){reason="switching_exit";failoverRequested=true;retryAt=DateTimeOffset.UtcNow;Program.Log("exit down, switching to the next exit",e);return;}
  retryAt=DateTimeOffset.UtcNow.AddSeconds(Math.Min(60,5*Math.Pow(2,Math.Min(failures++,4))));
  triedExits.Clear();
  Record("blocked",reason);Program.Log("protection blocked",e);
 }
 // ---------- 「本机直连」的流量：网关看不到，本机数好了报给服务器（「本月流量」照样显示） ----------
 bool directExit;GatewayEndpoint[] directEndpoints=[];
 long trafficLastTotal=-1,trafficPending;Dictionary<string,long> directConns=new();DateTimeOffset trafficReadAt,trafficSentAt;Task? trafficSend;
 // A number field, 0 when missing or not a number (null, text): reading a JSON null as a number would throw.
 static long Num(JsonElement value,string field)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(field,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out var n)?n:0;
 // Every 5 s: everything the kernel moved, minus what went straight out (local network, sites not on the list).
 // Sent once a minute in the background; what could not be sent is kept for the next time.
 async Task CountDirectTraffic(CancellationToken stop)
 {
  if(!directExit){trafficLastTotal=-1;directConns.Clear();return;}
  var now=DateTimeOffset.UtcNow;if(now<trafficReadAt)return;trafficReadAt=now.AddSeconds(5);
  var snap=await kernel.Get("connections",stop);
  var total=Num(snap,"uploadTotal")+Num(snap,"downloadTotal");long straight=0;var seen=new Dictionary<string,long>();
  if(snap.TryGetProperty("connections",out var list)&&list.ValueKind==JsonValueKind.Array)
   foreach(var c in list.EnumerateArray())
   {
    if(!c.TryGetProperty("chains",out var chains)||chains.ValueKind!=JsonValueKind.Array||!chains.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String&&x.GetString() is "DIRECT" or "REJECT"))continue;
    var id=Text(c,"id");var bytes=Num(c,"upload")+Num(c,"download");seen[id]=bytes;straight+=bytes-(directConns.TryGetValue(id,out var was)?Math.Min(was,bytes):0);
   }
  directConns=seen;
  if(trafficLastTotal>=0)Interlocked.Add(ref trafficPending,Math.Max(0,(total>=trafficLastTotal?total-trafficLastTotal:total)-straight)); // the kernel restarted: its count began again
  trafficLastTotal=total;
  if(Interlocked.Read(ref trafficPending)>0&&now>=trafficSentAt&&(trafficSend is null||trafficSend.IsCompleted))
  {
   trafficSentAt=now.AddSeconds(60);var send=Math.Min(Interlocked.Read(ref trafficPending),100L*1024*1024*1024);
   trafficSend=Task.Run(async()=>{try{await Remote(new{type="report_traffic",bytes=send},CancellationToken.None);Interlocked.Add(ref trafficPending,-send);}catch(Exception e){Program.Log("traffic report",e);}});
  }
 }
 // Connections through the exit that sent something and got nothing back for 4 s or more, while no connection
 // through the exit receives anything: the exit takes connections but passes nothing on (an expired node does
 // exactly that). Checked every 5 s; the exit is then checked at once, and a failed check counts right away.
 DateTimeOffset stallProbeAt,stallAt=DateTimeOffset.MinValue;Dictionary<string,long> exitDownloads=new();
 async Task NoteStall(CancellationToken stop)
 {
  if(state!="protected"){exitDownloads.Clear();return;}
  var snap=await kernel.Get("connections",stop);var now=DateTimeOffset.UtcNow;var stuck=0;var progress=false;var seen=new Dictionary<string,long>();
  if(snap.TryGetProperty("connections",out var list)&&list.ValueKind==JsonValueKind.Array)
   foreach(var c in list.EnumerateArray())
   {
    if(!c.TryGetProperty("chains",out var chains)||chains.ValueKind!=JsonValueKind.Array||chains.EnumerateArray().Any(x=>x.ValueKind==JsonValueKind.String&&x.GetString() is "DIRECT" or "REJECT"))continue;
    var id=Text(c,"id");var got=Num(c,"download");seen[id]=got;
    if(got>(exitDownloads.TryGetValue(id,out var was)?was:0))progress=true;
    if(got==0&&Num(c,"upload")>0&&DateTimeOffset.TryParse(Text(c,"start"),out var started)&&now-started>TimeSpan.FromSeconds(4))stuck++;
   }
  exitDownloads=seen;
  if(stuck>=2&&!progress){stallAt=now;if(now>=stallProbeAt){stallProbeAt=now.AddSeconds(15);nextNetProbe=now;}}
 }
 // ---------- 换出口：不重建防护 ----------
 // Every exit is reached through the same Leyra gateway, which picks the customer's selected exit for each new
 // connection, so nothing on this computer has to be rebuilt: close the connections opened before the switch
 // (they still use the old exit) and confirm the new exit. Blocking rules stay on throughout; the gateway never
 // falls back to a direct connection. If the new exit does not answer, rebuild as before (failover takes over).
 async Task ExitChanged(CancellationToken stop)
 {
  exitAskAt=DateTimeOffset.MinValue; // the number of exits is asked again soon (an exit may just have been added)
  if(!configured||state!="protected"){configured=false;retryAt=DateTimeOffset.MinValue;return;}
  // Switching between an exit reached through Hong Kong and one this computer dials itself changes the line: rebuild.
  try
  {
   var fresh=await cloud!.SendAsync("/api/v1/config",cancellation:stop);
   if(fresh.GetProperty("proxies").GetRawText()!=nodes.GetRawText()){configured=false;retryAt=DateTimeOffset.MinValue;return;}
  }
  catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested){Program.Log("reading the line for the new exit",e);configured=false;retryAt=DateTimeOffset.MinValue;return;}
  var generation=DateTimeOffset.UtcNow;configAt=generation; // checks still running for the old exit are discarded
  exitVerified=false;
  try
  {
   await kernel.CloseConnections(stop);
   using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(20));
   var observed=await ObserveExit(deadline.Token);
   if(configAt!=generation||!configured)return;
   exitIp=observed;exitVerified=true;netFailures=0;nextNetProbe=DateTimeOffset.UtcNow.AddSeconds(30);triedExits.Clear();
   Record("exit_switched",observed);Publish();
  }
  catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested)
  {
   if(configAt!=generation)return;
   Program.Log("new exit did not answer, rebuilding the line",e);configured=false;retryAt=DateTimeOffset.MinValue;
  }
 }
 // ---------- 出口自动切换：当前出口连不上时换到客户自己添加的下一个出口 ----------
 bool failoverRequested;readonly HashSet<int> triedExits=new();DateTimeOffset failoverPausedUntil=DateTimeOffset.MinValue;
 // The next exit to use: working ones first (checked by the server in the last 6 minutes, fastest first), then
 // the not yet checked ones in the order the customer added them; ones known to be down right now are skipped.
 async Task<(int Id,string Name,string From)?> PickExit(CancellationToken stop)
 {
  var list=await Remote(new{type="exits"},stop);
  exitCount=list.GetProperty("items").GetArrayLength();
  var current=list.GetProperty("selected").ValueKind==JsonValueKind.Number?list.GetProperty("selected").GetInt32():(int?)null;
  if(current is int c)triedExits.Add(c);
  return ChooseExit(list,triedExits,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
 }
 // Pure choice over the server's exit list (tested on its own): exits checked in the last 6 minutes and working
 // first, fastest first; then the unchecked ones (a new exit has no health yet: every field null) in the order
 // they were added; exits known to be down right now, the one in use and ones already tried are left out.
 static (int Id,string Name,string From)? ChooseExit(JsonElement list,IReadOnlyCollection<int> tried,long now)
 {
  var items=list.GetProperty("items").EnumerateArray().Select((x,i)=>
  {
   var h=x.TryGetProperty("health",out var hv)&&hv.ValueKind==JsonValueKind.Object?hv:default;
   bool? ok=h.ValueKind==JsonValueKind.Object&&h.TryGetProperty("ok",out var o)&&o.ValueKind is JsonValueKind.True or JsonValueKind.False?o.GetBoolean():null;
   var fresh=Num(h,"at") is long at&&at>0&&now-at<360;
   return (Id:x.GetProperty("id").GetInt32(),Name:Text(x,"name"),Index:i,Good:ok==true&&fresh,Bad:ok==false&&fresh,Ms:Num(h,"ms"));
  }).ToList();
  var current=list.GetProperty("selected").ValueKind==JsonValueKind.Number?list.GetProperty("selected").GetInt32():(int?)null;
  var from=items.FirstOrDefault(x=>x.Id==current).Name??"当前出口";
  var next=items.Where(x=>x.Id!=current&&!tried.Contains(x.Id)&&!x.Bad).OrderBy(x=>x.Good?0:1).ThenBy(x=>x.Good?x.Ms:0).ThenBy(x=>x.Index).FirstOrDefault();
  return next.Name is null?null:(next.Id,next.Name.Length>0?next.Name:"出口",from);
 }
 object? lastFailover;string? blockReason;
 // The line was cut while switching (it could not be built on the exit in use): move to the next working exit.
 async Task Failover(CancellationToken stop)
 {
  var pick=await PickExit(stop);
  if(pick is not {} next)
  {
   // None of the customer's exits works right now: stay cut off (the real IP is never used), say so, try again shortly.
   triedExits.Clear();state="blocked";reason="all_exits_down";retryAt=DateTimeOffset.UtcNow.AddSeconds(30);Record("blocked",reason);return;
  }
  await Remote(new{type="select_exit",id=next.Id},stop);triedExits.Add(next.Id);
  configured=false;retryAt=DateTimeOffset.UtcNow;
  Record("exit_failover",next.From+" → "+next.Name);lastFailover=new{at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),from=next.From,to=next.Name};
 }
 // The exit in use stopped passing traffic while protected: move straight to a working exit without rebuilding
 // the line (all exits go through the same gateway), or cut off and say why.
 async Task ExitDown(Exception e,DateTimeOffset generation,CancellationToken stop)
 {
  await gate.WaitAsync(stop);
  try
  {
   if(state!="protected"||!configured||configAt!=generation)return;
   var fatal=e is HttpRequestException h&&h.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
   if(!fatal&&store.Settings.AutoSwitchExit&&DateTimeOffset.UtcNow>=failoverPausedUntil)
   {
    (int Id,string Name,string From)? pick;
    try{pick=await PickExit(stop);}
    catch(Exception ex)when(ex is not OperationCanceledException||!stop.IsCancellationRequested){pick=null;failoverPausedUntil=DateTimeOffset.UtcNow.AddMinutes(1);Program.Log("could not ask which exits work",ex);}
    if(pick is {} next)
    {
     await Remote(new{type="select_exit",id=next.Id},stop);triedExits.Add(next.Id);
     Record("exit_failover",next.From+" → "+next.Name);lastFailover=new{at=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),from=next.From,to=next.Name};
     Program.Log("exit down, moved to "+next.Name,e);
     await ExitChanged(stop);return;
    }
    if(exitCount is int n&&n>1)blockReason="all_exits_down";
   }
   else if(!fatal&&!store.Settings.AutoSwitchExit&&exitCount is int m&&m>1)blockReason="exit_down";
   Block(e);
  }
  catch(Exception ex)when(ex is not OperationCanceledException||!stop.IsCancellationRequested){Program.Log("handling a broken exit",ex);if(state=="protected"&&configured)Block(e);}
  finally{Publish();gate.Release();}
 }
 Task? networkTask;
 int netFailures;DateTimeOffset nextNetProbe=DateTimeOffset.MinValue;DateTimeOffset lastTraffic=DateTimeOffset.MinValue;
 // Pause and quit interrupt a line that is still being built instead of waiting up to a minute.
 CancellationTokenSource startCancel=new();bool startInterrupted;
 // Every 30 s: server reachable and traffic really leaves through the customer's exit.
 // Real exits (home broadband, residential proxies) drop single requests now and then; the
 // local rules keep blocking direct traffic meanwhile, so only 3 failures in a row (about
 // 1.5 minutes) count as the line being down. Expired or unbound devices stop at once.
 async Task NetworkProbe(CancellationToken stop)
 {
  if(cloud is null)return;
  var generation=configAt;
  var ok=false;
  try
  {
   using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(12));var ct=deadline.Token;
   await cloud.SendAsync("/api/v1/status",cancellation:ct);
   var observed=await ObserveExit(ct);
   if(configAt!=generation||!configured||state!="protected")return;
   exitIp=observed;exitVerified=true;netFailures=0;nextNetProbe=DateTimeOffset.UtcNow.AddSeconds(30);ok=true;
  }
  catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested)
  {
   if(configAt!=generation||!configured||state!="protected")return;
   var fatal=e is HttpRequestException h&&h.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
   netFailures++;nextNetProbe=DateTimeOffset.UtcNow.AddSeconds(3);
   // Pages are still loading: everything has to go through the customer's exit (direct traffic is
   // blocked), so data flowing means the exit works and only the check was refused, as busy
   // residential exits do. Keep the customer online; only a check failing with nothing flowing cuts off.
   // Only in "everything through the exit" mode does downloading prove the exit works (in the other modes most
   // traffic does not use it), and only for the last 10 s.
   var flowing=!coexist&&!store.Settings.ProxyOnlyListed&&DateTimeOffset.UtcNow-lastTraffic<TimeSpan.FromSeconds(10);
   // Programs are waiting on the exit with nothing coming back as well: no need to wait for a second check.
   var stalled=DateTimeOffset.UtcNow-stallAt<TimeSpan.FromSeconds(30);
   Program.Log("line check failed ("+netFailures+"/2"+(flowing?", traffic still flowing":"")+(stalled?", connections get nothing back":"")+")",e);
   if(!fatal&&flowing&&!stalled){netFailures=Math.Min(netFailures,1);return;}
   if(!fatal&&netFailures<2&&!stalled)return;
   await ExitDown(e,generation,stop);
  }
  if(!ok)return;
  // Location, timezone and line samples are extras: being slow or failing never counts as the line being down.
  try
  {
   using var extra=CancellationTokenSource.CreateLinkedTokenSource(stop);extra.CancelAfter(TimeSpan.FromSeconds(45));
   await Task.WhenAll(RefreshExitGeo(extra.Token),SampleLines(extra.Token));
   if(configAt==generation&&configured&&state=="protected")await AutoTimezone(extra.Token);
  }
  catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested){Program.Log("exit details",e);}
 }
 // Websites read the computer's timezone through the browser; keep it matching the exit's location.
 // The original zone is remembered for 恢复原来的时区 and restored on uninstall.
 DateTimeOffset tzRetryAt=DateTimeOffset.MinValue;
 async Task AutoTimezone(CancellationToken ct)
 {
  var exit=exitGeoFor==exitIp&&exitGeo is not null?exitGeo.Timezone:"";
  if(exit.Length==0||store.Settings.TimezoneDismissedFor==exit||DateTimeOffset.UtcNow<tzRetryAt||SystemClock.Matches(exit))return;
  try{store.Settings.OriginalTimezone??=TimeZoneInfo.Local.Id;store.Save();await SystemClock.SetTimezone(exit,ct);Record("timezone",exitGeo is not null&&exitGeoFor==exitIp?exitGeo.Label:exit);}
  catch(Exception e)when(e is not OperationCanceledException){tzRetryAt=DateTimeOffset.UtcNow.AddMinutes(10);Program.Log("automatic timezone",e);}
 }
 // Every 5 s, local only and fast: the kernel, the TUN interface and the blocking rules are in place.
 async Task Health(CancellationToken stop)
 {
  if(cloud is null)throw new InvalidOperationException("尚未连接服务器");
  if(coexist)
  {
   if(!kernel.Alive)throw new InvalidOperationException("网络组件已退出");
   // The customer's proxy app may set the system proxy back to itself, or clear it when it quits: take it again
   // at once, so the listed sites never leave through anything but the exit.
   if(!coexistProxy.Holding()){coexistProxy.Take();Record("repair","系统代理被其他软件改动，已接回 Leyra");}
   // A Claude update (new folder) or a newly started browser gets its rule within seconds.
   ApplyAppGuard();
  }
  else if(!kernel.Alive || KernelSession.Tunnel()!=tunnel || !filters.DefaultGuardsInstalled())throw new InvalidOperationException("防护组件或本机规则发生变化");
  using(var local=CancellationTokenSource.CreateLinkedTokenSource(stop))
  {
   local.CancelAfter(TimeSpan.FromSeconds(4));
   try{var traffic=await kernel.Rates(local.Token);up=traffic.Up;down=traffic.Down;if(down>=2048)lastTraffic=DateTimeOffset.UtcNow;await NoteStall(local.Token);var proxies=await kernel.Get("proxies/"+Uri.EscapeDataString("出口"),local.Token);selected=Text(proxies,"now");await CountDirectTraffic(local.Token);}
   catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException){/* 读流量只是显示用，下次再读 */}
  }
  heartbeat=DateTimeOffset.UtcNow;lastHealth=heartbeat;
  if(coexist){if(!exitVerified)throw new InvalidOperationException("还没有确认 Claude 走你的出口");}
  else
  {
  var evidence=new ProtectionEvidence(kernel.Alive,tunnel!=0,filters.DefaultGuardsInstalled(),dnsVerified,exitVerified,heartbeat);
  if(evidence.Assess(heartbeat)!=ProtectionPhase.Protected)throw new InvalidOperationException("本机防护证据不完整");
  }
  state="protected";failures=0;if(triedExits.Count>0)triedExits.Clear();
 }
 object BuildStatus()
 {
  var visible=state;
  if(visible=="protected"&&(DateTimeOffset.UtcNow-heartbeat>TimeSpan.FromSeconds(12)||!kernel.Alive))visible="starting";
  var region=exitGeo is not null&&exitGeoFor==exitIp?exitGeo.Label:"";var speed=lastSpeed is {} s?new{down_bps=s.Down,ago_secs=(long)(DateTimeOffset.UtcNow-s.At).TotalSeconds,measured=true}:null;
  var lines=nodes.ValueKind==JsonValueKind.Array?nodes.EnumerateArray().Select((node,i)=>{var name=Text(node,"name");var q=Quality(name);return new{id=(i+1).ToString(),label=(i+1)+" · "+(name.Length>0?name:"自有线路"),region,exit_ip=exitIp,latency_ms=q.Latency,success_pct=q.Success,signal=q.Signal,speed=name==selected||nodes.GetArrayLength()==1?speed:null};}).ToArray():[];
  object? last;lock(checkHistory)last=checkHistory.FirstOrDefault();
  var lastCheck=last is null?(JsonElement?)null:JsonSerializer.SerializeToElement(last);
  var index=nodes.ValueKind==JsonValueKind.Array?nodes.EnumerateArray().Select((x,i)=>(Name:Text(x,"name"),Index:i+1)).FirstOrDefault(x=>x.Name==selected).Index:0;
  var remaining=Math.Max(0,store.Settings.PausedUntil-DateTimeOffset.UtcNow.ToUnixTimeSeconds());
  return new{version=Program.Version,coexist,consent_version=store.Settings.ConsentVersion,protection=new{state=visible,reason,remaining_secs=remaining,auto_resume_on_node=false,quit=store.Settings.QuitPause,off=visible=="paused"&&!store.Settings.Enabled,blocked=visible=="blocked",since_secs=0},lines,current_line=index==0?"1":index.ToString(),line_mode=new{mode},rate=visible=="protected"?new{up_bps=up,down_bps=down}:null,guard=visible=="protected"?new{since=guardSince.ToUnixTimeSeconds(),blocked=(int?)null}:null,checks=bypassCheck is not null&&visible=="protected"&&JsonSerializer.SerializeToElement(bypassCheck).GetProperty("verdict").GetString()=="fail"?new[]{bypassCheck}:Array.Empty<object>(),conflicts,proxy_undo=proxy.UndoAvailable,last_error=lastError,update=updateReady,update_at_boot=store.Settings.UpdateAtBoot,auto_exit=store.Settings.AutoSwitchExit,last_failover=lastFailover,last_check=lastCheck is JsonElement lc?new{at=lc.GetProperty("at").GetInt64(),kind=Text(lc,"kind"),problems=lc.GetProperty("problems").GetInt32()}:null};
 }
 string writtenState="";
 void Publish()
 {
  snapshot=new Snapshot(Json(BuildStatus()),heartbeat,state=="protected");
  // A tiny state file for support and automated checks (no credentials, written only on change).
  var now=state+" "+reason;
  if(now!=writtenState){writtenState=now;try{AgentStore.Atomic(Path.Combine(AgentStore.Root,"agent-state.txt"),System.Text.Encoding.UTF8.GetBytes(Program.Version+" "+now));}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}}
 }
 object Status()
 {
  var value=snapshot;var now=DateTimeOffset.UtcNow;
  if(value.Protected&&(now-value.Heartbeat>TimeSpan.FromSeconds(12)||value.Heartbeat>now+TimeSpan.FromSeconds(5)))
  {var changed=JsonNode.Parse(value.Data.GetRawText())!;changed["protection"]!["state"]="starting";return changed;}
  return value.Data;
 }
 public async Task<object> Handle(JsonElement request,CancellationToken stop)
 {
  var type=Text(request,"type");
  if(type=="status")return Status();
  if(type=="owner_auth")
  {
   var encoded=Text(request,"buffer");if(encoded.Length>90000)throw new InvalidDataException("账户验证数据过大");
   var packed=Convert.FromBase64String(encoded);
   if(!NativeCredentials.Verify(packed,store.Owner))throw new UnauthorizedAccessException("无法验证当前 Windows 账户密码");
   return new{proof=ownerProof.Grant()};
  }
  if(type=="history"){object[] recent;lock(events)recent=events.AsEnumerable().Reverse().ToArray();return new{lines=HistoryLines(),events=recent};}
  if(type=="check_history"){lock(checkHistory)return new{items=checkHistory.ToArray()};}
  if(type=="timezone")return Zone();
  if(type=="doctor")return await Doctor(Bool(request,"deep"),stop);
  if(type=="browser_results")return BrowserResults();
  if(type=="check_conflicts"){RefreshConflicts(true);return new{items=conflicts,complete=true};}
  if(type=="geo")return await Geo(request,stop);
  if(type=="check_update"){await CheckForUpdate(true,stop);return new{version=updateReady,current=Program.Version};}
  // Requests that only read or talk to the server don't wait behind protection changes.
  switch(type)
  {
   case "account":return await Account(stop);
   case "direct_rules":return new{items=store.Settings.DirectRules.Select(x=>new{cidr=x.Cidr,note=x.Note,public_ip=x.Public}).ToArray()};
   case "tickets":var tickets=await Remote(request,stop);return new{items=tickets.GetProperty("items").Clone(),images=true};
   case "new_ticket":case "ticket_reply":case "ticket_rate":case "upload_chunk":case "ticket_image":case "messages":case "mark_read":case "set_contact":case "help":case "ai_accounts":case "ai_report":return await Remote(request,stop);
   case "report_bad_line":return await Remote(new{type="new_ticket",kind=1,title="线路问题",body="客户反馈当前线路不可用，请检查服务端出口。"},stop);
   case "speed_test":return await MeasureSpeed(stop);
  }
  if(type is "pause" or "trial_quit"){startInterrupted=true;startCancel.Cancel();}
  await gate.WaitAsync(stop);
  startInterrupted=false;
  try
  {
   switch(type)
   {
    case "pair":
     NeedOwner(request);await PairWith(ServerProfile.Parse(JsonSerializer.SerializeToUtf8Bytes(request.GetProperty("profile"))),stop);return new{saved=true};
    case "activate":case "restore":
     // One authorization code = one computer: the server binds the code to this hardware ID.
     var watch=System.Diagnostics.Stopwatch.StartNew();
     // Each step says where it failed (and is logged), instead of one "operation timed out" for the whole thing.
     ServerProfile issued;
     try{issued=await RedeemCode(new{code=Text(request,"code"),machine=MachineId.Get(),contact=Text(request,"contact"),order_no=Text(request,"order_no"),label=Environment.MachineName},stop);}
     catch(Exception e)when(e is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException&&!stop.IsCancellationRequested)
     {Program.Log("activation step 1 (code check at www.leyra.xin) failed after "+watch.Elapsed.TotalSeconds.ToString("N1")+" s",e);throw new InvalidOperationException("核对授权码失败：连不上 leyra.xin（"+e.GetType().Name+"），请检查网络后重试");}
     var redeemed=watch.Elapsed;
     // Like the original: after activation the overview asks for the customer's own exit (IP);
     // protection starts by itself once one is connected.
     try{await PairWith(issued,stop);}
     catch(Exception e)when(e is HttpRequestException or TaskCanceledException or IOException or System.Net.Sockets.SocketException or InvalidDataException&&!stop.IsCancellationRequested)
     {Program.Log("activation step 2 (registering at "+issued.Host+":"+issued.Port+") failed after "+(watch.Elapsed-redeemed).TotalSeconds.ToString("N1")+" s",e);throw new InvalidOperationException("授权码已通过，但登记这台电脑失败：连不上服务器 "+issued.Host+":"+issued.Port+"（"+e.GetType().Name+"），请检查网络后重试");}
     if(watch.Elapsed>TimeSpan.FromSeconds(10))Program.Log("slow activation",new TimeoutException($"code check {redeemed.TotalSeconds:N1} s, registering this computer {(watch.Elapsed-redeemed).TotalSeconds:N1} s"));exitCount=null;exitAskAt=DateTimeOffset.MinValue;Record("activated");return new{saved=true};
    case "switch_code":
     var renewed=await Remote(new{type="license_renew",code=Text(request,"code"),machine=MachineId.Get()},stop);
     retryAt=DateTimeOffset.MinValue;configured=false;state="starting";return new{saved=true,suite=false,expires_at=renewed.GetProperty("expires_at").GetInt64()};
    case "access_code":throw new InvalidOperationException("暂不支持接入码，请在售后群联系客服");
    case "trial_quit":
     // Trial over and the customer chose to quit: stop taking over the network until a code is entered.
     store.Settings.Enabled=false;store.Settings.QuitPause=true;store.Settings.PausedUntil=0;store.Save();
     kernel.Stop();Unguard();configured=false;state="paused";Record("paused","quit");return new{saved=true};
    case "consent":store.Settings.ConsentVersion=request.GetProperty("version").GetInt32();store.Save();return new{saved=true};
    case "proxy_off":return proxy.Disable();
    case "proxy_undo":return proxy.Undo();
    case "resume":
     if(profile is null)throw new InvalidOperationException("请先导入服务器连接文件");
     store.Settings.Enabled=true;store.Settings.PausedUntil=0;store.Settings.QuitPause=false;store.Save();configured=false;retryAt=DateTimeOffset.MinValue;state="starting";return new{saved=true};
    case "pause":
     NeedOwner(request);var minutes=request.GetProperty("minutes").GetInt32();if(minutes is <1 or >1440)throw new InvalidDataException("暂停时间无效");
     store.Settings.QuitPause=Text(request,"reason")=="quit";store.Settings.Enabled=!store.Settings.QuitPause;store.Settings.PausedUntil=DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();store.Save();
     kernel.Stop();Unguard();configured=false;state="paused";Record("paused",store.Settings.QuitPause?"quit":minutes.ToString());return new{saved=true};
    case "refresh":case "restart_kernel":retryAt=DateTimeOffset.MinValue;configured=false;kernel.Stop();ApplyGuard(0);state="starting";return new{saved=true};
    case "pick_line":
     var id=int.Parse(Text(request,"id"));if(id<1||id>nodes.GetArrayLength())throw new InvalidDataException("线路编号无效");
     await kernel.Select(Text(nodes[id-1],"name"),stop);mode="manual";selected=Text(nodes[id-1],"name");Record("switch","线路 "+id);return new{saved=true};
    case "auto_line":await kernel.Select("自动选择",stop);mode="auto";return new{saved=true};
    case "doctor":return await Doctor(Bool(request,"deep"),stop);
    case "account":return await Account(stop);
    case "direct_rules":return new{items=store.Settings.DirectRules.Select(x=>new{cidr=x.Cidr,note=x.Note,public_ip=x.Public}).ToArray()};
    case "route_mode":return new{listed=store.Settings.ProxyOnlyListed,domains=store.Settings.ProxyDomains,defaults=MihomoConfiguration.DefaultProxyDomains};
    case "set_route_mode":
     {
      var listed=Bool(request,"listed");
      var domains=request.TryGetProperty("domains",out var ds)&&ds.ValueKind==JsonValueKind.Array?MihomoConfiguration.ProxyDomains(ds.EnumerateArray().Select(x=>x.GetString()??"")).ToList():store.Settings.ProxyDomains;
      store.Settings.ProxyOnlyListed=listed;store.Settings.ProxyDomains=domains;store.Save();
      // Rebuild the line with the new routing (blocking rules stay on while it does).
      configured=false;retryAt=DateTimeOffset.MinValue;return new{saved=true,listed,domains};
     }
    case "add_direct_rule":
     var rule=DirectRule.Parse(Text(request,"rule"),Text(request,"note"),Bool(request,"confirm_public"));if(rule.Public)NeedOwner(request);
     if(store.Settings.DirectRules.Count>=64&&!store.Settings.DirectRules.Any(x=>x.Cidr==rule.Cidr))throw new InvalidOperationException("直连规则最多 64 条");
     var added=await Remote(new{type="add_direct_rule",rule=rule.Cidr,note=rule.Note,confirm_public=rule.Public},stop);ReadRules(added);configured=false;return new{saved=true};
    case "remove_direct_rule":var removed=await Remote(request,stop);ReadRules(removed);configured=false;return new{saved=true};
    case "set_direct_note":
     var at=store.Settings.DirectRules.FindIndex(r=>r.Cidr==Text(request,"cidr"));if(at<0)throw new InvalidOperationException("找不到这条规则");
     var old=store.Settings.DirectRules[at];DirectRule.Parse(old.Cidr,Text(request,"note"),old.Public);var edited=await Remote(request,stop);ReadRules(edited);return new{saved=true};
    case "set_timezone":
     if(exitGeoFor!=exitIp||exitGeo is null||exitGeo.Timezone.Length==0)throw new InvalidOperationException("还没有取得当前出口所在时区，请在防护建立后再试");
     store.Settings.OriginalTimezone??=TimeZoneInfo.Local.Id;store.Settings.TimezoneDismissedFor="";store.Save();await SystemClock.SetTimezone(store.Settings.ExitTimezone,stop);return Zone();
    case "restore_timezone":
     if(store.Settings.OriginalTimezone is string original){await SystemClock.SetTimezone(original,stop);store.Settings.OriginalTimezone=null;}
     store.Settings.TimezoneDismissedFor=store.Settings.ExitTimezone;store.Save();return Zone();
    case "dismiss_timezone":store.Settings.TimezoneDismissedFor=store.Settings.ExitTimezone;store.Save();return new{saved=true};
    case "sync_time":
     var clock=await Remote(new{type="server_time"},stop);if(!Bool(clock,"synchronized"))throw new InvalidOperationException("服务器时间尚未完成同步");
     SystemClock.SetUtc(clock.GetProperty("unix").GetDouble());return new{saved=true};
    case "shell_action":return await ShellAction(request,stop);
    case "tickets":var tickets=await Remote(request,stop);return new{items=tickets.GetProperty("items").Clone(),images=true};
    case "ai_secrets":if(state!="protected")throw new InvalidOperationException("请先恢复防护，再显示登录信息");return await Remote(request,stop);
    case "new_ticket":case "ticket_reply":case "ticket_rate":case "upload_chunk":case "ticket_image":case "messages":case "mark_read":case "set_contact":case "help":case "ai_accounts":case "ai_report":return await Remote(request,stop);
    case "select_exit":{var result=await Remote(request,stop);await ExitChanged(stop);return result;}
    case "set_exit_direct":{var result=await Remote(request,stop);if(Bool(result,"current"))await ExitChanged(stop);return result;}
    case "delete_exit":
     {
      // Only deleting the exit in use changes where traffic goes.
      var before=await Remote(new{type="exits"},stop);var wasCurrent=before.GetProperty("selected").ValueKind==JsonValueKind.Number&&request.TryGetProperty("id",out var del)&&del.TryGetInt32(out var delId)&&before.GetProperty("selected").GetInt32()==delId;
      var result=await Remote(request,stop);exitCount=null;exitAskAt=DateTimeOffset.MinValue;
      if(wasCurrent)await ExitChanged(stop);
      return result;
     }
    case "report_bad_line":return await Remote(new{type="new_ticket",kind=1,title="线路问题",body="客户反馈当前线路不可用，请检查服务端出口。"},stop);
    case "install_update":return InstallUpdate();
    case "update_at_boot":return UpdateAtBoot();
    case "set_auto_exit":store.Settings.AutoSwitchExit=Bool(request,"on");store.Save();return new{saved=true,on=store.Settings.AutoSwitchExit};
    case "own_exit":case "store_exit":return await AddExit(request,stop);
    case "block_before_ready":
     if(!Bool(request,"on"))NeedOwner(request);
     if(Bool(request,"on")){ApplyGuard(0);state="blocked";}else{Unguard();state="unprotected";}return new{saved=true};
    case "speed_test":return await MeasureSpeed(stop);
    default:throw new InvalidOperationException("此后台功能尚未接入："+type);
   }
  }
  finally{Publish();gate.Release();}
 }
 async Task PairWith(ServerProfile imported,CancellationToken stop,bool enable=true)
 {
  using(var setup=new PrivateServerClient(imported))
  {
   var paired=await setup.EnrollAsync(Guid.NewGuid(),Environment.MachineName,stop);
   store.SetProfile(paired.Profile);profile=paired.Profile;cloud?.Dispose();cloud=new PrivateServerClient(profile);nodes=paired.Nodes;
  }
  store.Settings.Enabled=enable;store.Settings.PausedUntil=0;store.Settings.QuitPause=false;store.Save();configured=false;kernel.Stop();retryAt=DateTimeOffset.MinValue;
  state="provisioning";reason="own_exit";Publish();
 }
 const string ActivationUrl="https://leyra.xin/api/v1/activate";
 async Task<ServerProfile> RedeemCode(object body,CancellationToken stop)
 {
  JsonElement result;
  if(profile is not null)
  {
   // Already paired once (renewal after unbinding, reinstall): the protection rules only let this
   // service reach its own pinned server, so ask that server directly.
   using var pinned=new PrivateServerClient(profile);result=await pinned.SendAsync("/api/v1/activate",body,stop);
  }
  else
  {
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(20)};
   using var response=await http.PostAsync(ActivationUrl,new StringContent(JsonSerializer.Serialize(body),System.Text.Encoding.UTF8,"application/json"),stop);
   var bytes=await response.Content.ReadAsByteArrayAsync(stop);if(bytes.Length>2*1024*1024)throw new InvalidDataException("服务器响应过大");
   using var doc=JsonDocument.Parse(bytes);
   if(!response.IsSuccessStatusCode)throw new InvalidOperationException(doc.RootElement.TryGetProperty("detail",out var d)&&d.ValueKind==JsonValueKind.String&&d.GetString()!.Length<=200?d.GetString():"激活失败，请稍后再试");
   result=doc.RootElement.Clone();
  }
  return ServerProfile.Parse(JsonSerializer.SerializeToUtf8Bytes(result.GetProperty("profile")));
 }
 void ReadRules(JsonElement result)
 {
  var items=result.GetProperty("items");if(items.ValueKind!=JsonValueKind.Array||items.GetArrayLength()>64)throw new InvalidDataException("服务端直连规则无效");
  store.Settings.DirectRules=items.EnumerateArray().Select(x=>DirectRule.Parse(Text(x,"cidr"),Text(x,"note"),Bool(x,"public_ip"))).ToList();store.Save();
 }
 async Task<bool> OwnExitMissing(CancellationToken stop)
 {
  if(exitCount is null&&DateTimeOffset.UtcNow>=exitAskAt)
  {
   try{using var t=CancellationTokenSource.CreateLinkedTokenSource(stop);t.CancelAfter(TimeSpan.FromSeconds(10));exitCount=(await Remote(new{type="exits"},t.Token)).GetProperty("items").GetArrayLength();}
   catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested){exitAskAt=DateTimeOffset.UtcNow.AddSeconds(30);}
  }
  return exitCount==0;
 }
 async Task<JsonElement> Remote(object request,CancellationToken stop)
 {
  if(cloud is null)throw new InvalidOperationException("请先连接自有服务器");return await cloud.RpcAsync(request,stop);
 }
 public void Dispose(){probe.Dispose();kernel.Dispose();cloud?.Dispose();filters.Dispose();gate.Dispose();}
}
sealed class LicenseExpiredException():Exception("授权已到期，续费后自动恢复防护");
