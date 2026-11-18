using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leyra.Windows.Core;

namespace Leyra.Mac.Agent;

sealed partial class Runtime:IDisposable
{
 readonly SystemProxy proxy;readonly AgentStore store;readonly SemaphoreSlim gate=new(1,1);readonly KernelSession kernel=new();readonly PfGuard filters=new();readonly OwnerProof ownerProof=new();
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
  this.store=store;proxy=new SystemProxy(store.Owner);profile=store.Profile();if(profile is not null)cloud=new PrivateServerClient(profile);
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
    if(profile is null){state="not_activated";}
    else if(await OwnExitMissing(stop)){kernel.Stop();if(filters.DefaultGuardsInstalled())filters.RemoveOwned();configured=false;state="provisioning";reason="own_exit";}
    else if(!store.Settings.Enabled || store.Settings.PausedUntil>DateTimeOffset.UtcNow.ToUnixTimeSeconds()){kernel.Stop();if(filters.DefaultGuardsInstalled())filters.RemoveOwned();configured=false;state="paused";}
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
 // Coexist mode (Mac): the customer's own proxy app stays the way out for everything but the listed sites.
 string? realIp; // coexist mode: this computer's own address (asked directly), to tell real leaks from the customer's proxy
 bool coexist;CoexistProxy.Upstream? upstream;
 /// <summary>Coexist mode while the line is down or being rebuilt: Leyra stays the system proxy with a holding
 /// configuration — Claude (and the customer's list) is refused, everything else goes on to the customer's own
 /// proxy. Claude therefore never leaves through anything but the exit. If even this cannot start, the system
 /// proxy still points at Leyra: Claude stays unreachable rather than using another address.</summary>
 void HoldCoexist()
 {
  if(upstream is null){filters.RemoveOwned();return;}
  // Coming from normal protection: its firewall rules and DNS change would cut the customer's own proxy. Remove
  // them (the system proxy stays on Leyra).
  filters.RemoveRules();
  try
  {
   var reject=MihomoConfiguration.ClaudeAlways.Concat(store.Settings.ProxyDomains).Distinct().Select(d=>"DOMAIN-SUFFIX,"+d+",REJECT");
   var hold=new Dictionary<string,object>{{"mixed-port",17890},{"allow-lan",false},{"bind-address","127.0.0.1"},{"mode","rule"},{"log-level","warning"},
    {"external-controller","127.0.0.1:19090"},{"secret",kernel.Secret},{"ipv6",false},
    {"proxies",new object[]{new Dictionary<string,object>{{"name","本机原代理"},{"type",upstream.Type},{"server","127.0.0.1"},{"port",upstream.Port},{"udp",false}}}},
    {"rules",reject.Append("MATCH,本机原代理").ToArray()},{"dns",new{enable=false}},{"tun",new{enable=false}}};
   using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
   kernel.Start(JsonSerializer.SerializeToUtf8Bytes(hold),limit.Token).GetAwaiter().GetResult();
   CoexistProxy.Take();
  }
  catch(Exception e)when(e is not OutOfMemoryException){Program.Log("coexist holding configuration",e);}
 }
 void ApplyGuard(ulong luid)
 {
  if(profile is null)return;
  // No firewall rules in coexist mode: they would cut the customer's own proxy. While the line is down the
  // customer's own proxy settings are back, so their network keeps working (never the bare real network).
  if(coexist){HoldCoexist();return;}
  var executable=File.Exists(kernel.Executable)?kernel.Executable:Environment.ProcessPath!;
  var endpoint=new System.Net.IPEndPoint(IPAddress.Parse(profile.Host),profile.Port);
  filters.Apply(PfPlan.Create([new GatewayEndpoint(endpoint,6)],KernelSession.TunnelName,store.Settings.DirectRules,store.Settings.ProxyOnlyListed));
 }
 async Task StartProtected(CancellationToken stop)
 {
  if(profile is null || cloud is null)return;
  upstream=CoexistProxy.Find();coexist=upstream is not null;
  state="starting";dnsVerified=false;exitVerified=false;Publish();ApplyGuard(0);
  if(startCancel.IsCancellationRequested){startCancel.Dispose();startCancel=new();}
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop,startCancel.Token);deadline.CancelAfter(TimeSpan.FromSeconds(90));var ct=deadline.Token;
  var config=await cloud.SendAsync("/api/v1/config",cancellation:ct);nodes=config.GetProperty("proxies").Clone();configAt=DateTimeOffset.UtcNow;
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
   CoexistProxy.Take();dnsVerified=true;tunnel=0;
   try{using var direct=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(10)};var ip=(await direct.GetStringAsync("https://api.ipify.org",ct)).Trim();realIp=System.Net.IPAddress.TryParse(ip,out _)?ip:null;}
   catch(Exception e)when(e is HttpRequestException or TaskCanceledException){realIp=null;}
  }
  else
  {
  await kernel.Start(MihomoConfiguration.Build(nodes,kernel.Secret,true,dns,store.Settings.DirectRules,mux,store.Settings.ProxyOnlyListed?store.Settings.ProxyDomains:null),ct);
  for(var i=0;i<40;i++){tunnel=KernelSession.Tunnel();if(tunnel!=0)break;await Task.Delay(200,ct);}
  if(tunnel==0)throw new InvalidOperationException("没有找到已启动的本机 TUN 接口");
  ApplyGuard(tunnel);MacDns.Apply();await VerifyDns(ct);
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
  var switching=!fatal&&exitCount is int count&&count>1&&triedExits.Count<count&&DateTimeOffset.UtcNow>=failoverPausedUntil;
  try{ApplyGuard(0);state=switching?"starting":"blocked";reason=expired?"software_expired":e is HttpRequestException h?h.StatusCode switch{HttpStatusCode.Forbidden=>"software_expired",HttpStatusCode.Unauthorized=>"device_unbound",_=>"all_lines_down"}:"all_lines_down";}
  catch{state="unprotected";reason="guard_failed";switching=false;}
  if(switching){reason="switching_exit";failoverRequested=true;retryAt=DateTimeOffset.UtcNow;Program.Log("exit down, switching to the next exit",e);return;}
  retryAt=DateTimeOffset.UtcNow.AddSeconds(Math.Min(60,5*Math.Pow(2,Math.Min(failures++,4))));
  triedExits.Clear();
  Record("blocked",reason);Program.Log("protection blocked",e);
 }
 // ---------- 出口自动切换：当前出口连不上时换到客户自己添加的下一个出口 ----------
 bool failoverRequested;readonly HashSet<int> triedExits=new();DateTimeOffset failoverPausedUntil=DateTimeOffset.MinValue;
 async Task Failover(CancellationToken stop)
 {
  var list=await Remote(new{type="exits"},stop);
  var items=list.GetProperty("items").EnumerateArray().Select(x=>(Id:x.GetProperty("id").GetInt32(),Name:Text(x,"name"))).ToList();
  exitCount=items.Count;
  var current=list.GetProperty("selected").ValueKind==JsonValueKind.Number?list.GetProperty("selected").GetInt32():(int?)null;
  if(current is int c)triedExits.Add(c);
  var from=items.FirstOrDefault(x=>x.Id==current).Name;
  // Next exit after the current one, in the order the customer added them.
  var start=items.FindIndex(x=>x.Id==current);
  var next=items.Skip(start+1).Concat(items.Take(Math.Max(0,start+1))).FirstOrDefault(x=>!triedExits.Contains(x.Id));
  if(next.Name is null){triedExits.Clear();return;}
  await Remote(new{type="select_exit",id=next.Id},stop);triedExits.Add(next.Id);
  configured=false;retryAt=DateTimeOffset.UtcNow;
  Record("exit_failover",(from??"当前出口")+" → "+next.Name);
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
   using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(20));var ct=deadline.Token;
   await cloud.SendAsync("/api/v1/status",cancellation:ct);
   var observed=await ObserveExit(ct);
   if(configAt!=generation||!configured||state!="protected")return;
   exitIp=observed;exitVerified=true;netFailures=0;nextNetProbe=DateTimeOffset.UtcNow.AddSeconds(30);ok=true;
  }
  catch(Exception e)when(e is not OperationCanceledException||!stop.IsCancellationRequested)
  {
   if(configAt!=generation||!configured||state!="protected")return;
   var fatal=e is HttpRequestException h&&h.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
   netFailures++;nextNetProbe=DateTimeOffset.UtcNow.AddSeconds(10);
   // Pages are still loading: everything has to go through the customer's exit (direct traffic is
   // blocked), so data flowing means the exit works and only the check was refused, as busy
   // residential exits do. Keep the customer online; only a check failing with nothing flowing cuts off.
   var flowing=DateTimeOffset.UtcNow-lastTraffic<TimeSpan.FromSeconds(30);
   Program.Log("line check failed ("+netFailures+"/3"+(flowing?", traffic still flowing":"")+")",e);
   if(!fatal&&flowing){netFailures=Math.Min(netFailures,2);return;}
   if(!fatal&&netFailures<3)return;
   await gate.WaitAsync(stop);
   try{if(state=="protected"&&configured&&configAt==generation)Block(e);}
   finally{Publish();gate.Release();}
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
   // The customer's proxy app may set the system proxy back to itself (or clear it when it quits): take it again
   // at once, so the listed sites never leave through anything but the exit.
   if(!CoexistProxy.Holding()){CoexistProxy.Take();Record("repair","系统代理被其他软件改动，已接回 Leyra");}
  }
  else if(!kernel.Alive || KernelSession.Tunnel()!=tunnel || !filters.DefaultGuardsInstalled())throw new InvalidOperationException("防护组件或本机规则发生变化");
  using(var local=CancellationTokenSource.CreateLinkedTokenSource(stop))
  {
   local.CancelAfter(TimeSpan.FromSeconds(4));
   try{var traffic=await kernel.Rates(local.Token);up=traffic.Up;down=traffic.Down;if(down>=2048)lastTraffic=DateTimeOffset.UtcNow;var proxies=await kernel.Get("proxies/"+Uri.EscapeDataString("出口"),local.Token);selected=Text(proxies,"now");}
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
  return new{version=Program.Version,coexist,consent_version=store.Settings.ConsentVersion,protection=new{state=visible,reason,remaining_secs=remaining,auto_resume_on_node=false,quit=store.Settings.QuitPause,off=visible=="paused"&&!store.Settings.Enabled,blocked=visible=="blocked",since_secs=0},lines,current_line=index==0?"1":index.ToString(),line_mode=new{mode},rate=visible=="protected"?new{up_bps=up,down_bps=down}:null,guard=visible=="protected"?new{since=guardSince.ToUnixTimeSeconds(),blocked=(int?)null}:null,checks=bypassCheck is not null&&visible=="protected"&&JsonSerializer.SerializeToElement(bypassCheck).GetProperty("verdict").GetString()=="fail"?new[]{bypassCheck}:Array.Empty<object>(),conflicts,proxy_undo=proxy.UndoAvailable,last_error=lastError,update=updateReady,last_check=lastCheck is JsonElement lc?new{at=lc.GetProperty("at").GetInt64(),kind=Text(lc,"kind"),problems=lc.GetProperty("problems").GetInt32()}:null};
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
  if(type=="owner_auth")throw new InvalidOperationException("Mac 版暂不支持用系统密码确认，请在设置里关闭「需要系统密码」后再操作");
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
     var issued=await RedeemCode(new{code=Text(request,"code"),machine=MachineId.Get(),contact=Text(request,"contact"),order_no=Text(request,"order_no"),label=Environment.MachineName},stop);
     var redeemed=watch.Elapsed;
     // Like the original: after activation the overview asks for the customer's own exit (IP);
     // protection starts by itself once one is connected.
     await PairWith(issued,stop);
     if(watch.Elapsed>TimeSpan.FromSeconds(10))Program.Log("slow activation",new TimeoutException($"code check {redeemed.TotalSeconds:N1} s, registering this computer {(watch.Elapsed-redeemed).TotalSeconds:N1} s"));exitCount=null;exitAskAt=DateTimeOffset.MinValue;Record("activated");return new{saved=true};
    case "switch_code":
     var renewed=await Remote(new{type="license_renew",code=Text(request,"code"),machine=MachineId.Get()},stop);
     retryAt=DateTimeOffset.MinValue;configured=false;state="starting";return new{saved=true,suite=false,expires_at=renewed.GetProperty("expires_at").GetInt64()};
    case "access_code":throw new InvalidOperationException("暂不支持接入码，请在售后群联系客服");
    case "trial_quit":
     // Trial over and the customer chose to quit: stop taking over the network until a code is entered.
     store.Settings.Enabled=false;store.Settings.QuitPause=true;store.Settings.PausedUntil=0;store.Save();
     kernel.Stop();filters.RemoveOwned();configured=false;state="paused";Record("paused","quit");return new{saved=true};
    case "consent":store.Settings.ConsentVersion=request.GetProperty("version").GetInt32();store.Save();return new{saved=true};
    case "proxy_off":return proxy.Disable();
    case "proxy_undo":return proxy.Undo();
    case "resume":
     if(profile is null)throw new InvalidOperationException("请先导入服务器连接文件");
     store.Settings.Enabled=true;store.Settings.PausedUntil=0;store.Settings.QuitPause=false;store.Save();configured=false;retryAt=DateTimeOffset.MinValue;state="starting";return new{saved=true};
    case "pause":
     NeedOwner(request);var minutes=request.GetProperty("minutes").GetInt32();if(minutes is <1 or >1440)throw new InvalidDataException("暂停时间无效");
     store.Settings.QuitPause=Text(request,"reason")=="quit";store.Settings.Enabled=!store.Settings.QuitPause;store.Settings.PausedUntil=DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();store.Save();
     kernel.Stop();filters.RemoveOwned();configured=false;state="paused";Record("paused",store.Settings.QuitPause?"quit":minutes.ToString());return new{saved=true};
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
    case "select_exit":case "delete_exit":var result=await Remote(request,stop);configured=false;exitCount=null;exitAskAt=DateTimeOffset.MinValue;return result;
    case "report_bad_line":return await Remote(new{type="new_ticket",kind=1,title="线路问题",body="客户反馈当前线路不可用，请检查服务端出口。"},stop);
    case "install_update":return InstallUpdate();
    case "own_exit":case "store_exit":return await AddExit(request,stop);
    case "block_before_ready":
     if(!Bool(request,"on"))NeedOwner(request);
     if(Bool(request,"on")){ApplyGuard(0);state="blocked";}else{filters.RemoveOwned();state="unprotected";}return new{saved=true};
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
   using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(20)};
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
