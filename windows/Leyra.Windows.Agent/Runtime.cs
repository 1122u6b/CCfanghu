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
 readonly SystemProxy proxy;readonly AgentStore store;readonly SemaphoreSlim gate=new(1,1);readonly KernelSession kernel=new();readonly WfpEngine filters=new();readonly OwnerProof ownerProof=new();
 ServerProfile? profile;PrivateServerClient? cloud;JsonElement nodes=JsonSerializer.SerializeToElement(Array.Empty<object>());
 string state="starting",reason="route",lastError="",exitIp="",selected="",mode="auto";
 DateTimeOffset heartbeat=DateTimeOffset.MinValue,lastHealth=DateTimeOffset.MinValue,guardSince=DateTimeOffset.MinValue;
 bool configured,dnsVerified,exitVerified;long up,down;ulong tunnel;
 sealed record Snapshot(JsonElement Data,DateTimeOffset Heartbeat,bool Protected);
 volatile Snapshot snapshot=new(JsonSerializer.SerializeToElement(new{version=Program.Version,protection=new{state="starting"}}),DateTimeOffset.MinValue,false);
 DateTimeOffset retryAt=DateTimeOffset.MinValue;int failures;
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
    if(profile is null){state="not_activated";}
    else if(!store.Settings.Enabled || store.Settings.PausedUntil>DateTimeOffset.UtcNow.ToUnixTimeSeconds()){kernel.Stop();if(filters.DefaultGuardsInstalled())filters.RemoveOwned();configured=false;state="paused";}
    else if(!kernel.Alive || !configured){if(DateTimeOffset.UtcNow>=retryAt)await StartProtected(stop);}
    else if(DateTimeOffset.UtcNow-lastHealth>TimeSpan.FromSeconds(5))await Health(stop);
   }
   catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
   catch(Exception e)
   {
    lastError=e.Message;configured=false;dnsVerified=false;exitVerified=false;kernel.Stop();
    try{ApplyGuard(0);state="blocked";reason=e is HttpRequestException h?h.StatusCode switch{HttpStatusCode.Forbidden=>"software_expired",HttpStatusCode.Unauthorized=>"device_unbound",_=>"all_lines_down"}:"all_lines_down";}
    catch{state="unprotected";reason="guard_failed";}
    retryAt=DateTimeOffset.UtcNow.AddSeconds(Math.Min(60,5*Math.Pow(2,Math.Min(failures++,4))));
    Record("blocked",reason);
   }
   finally{Publish();gate.Release();}
   try{await Task.Delay(1000,stop);}catch(OperationCanceledException)when(stop.IsCancellationRequested){break;}
  }
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
 void ApplyGuard(ulong luid)
 {
  if(profile is null)return;
  var executable=File.Exists(kernel.Executable)?kernel.Executable:Environment.ProcessPath!;
  var endpoint=new System.Net.IPEndPoint(IPAddress.Parse(profile.Host),profile.Port);
  filters.Apply(WfpPlan.Create(executable,Environment.ProcessPath!,[new GatewayEndpoint(endpoint,6)],[endpoint],luid,store.Settings.DirectRules));
 }
 async Task StartProtected(CancellationToken stop)
 {
  if(profile is null || cloud is null)return;
  state="starting";dnsVerified=false;exitVerified=false;Publish();ApplyGuard(0);
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(45));var ct=deadline.Token;
  var config=await cloud.SendAsync("/api/v1/config",cancellation:ct);nodes=config.GetProperty("proxies").Clone();configAt=DateTimeOffset.UtcNow;
  if(!config.TryGetProperty("identity",out var identity)||Text(identity,"role")!="device"||Text(identity,"id")!=profile.DeviceId)throw new InvalidDataException("服务器没有返回当前设备的独立身份");
  MihomoConfiguration.ValidateNodes(nodes);
  var remoteRules=await Remote(new{type="direct_rules"},ct);ReadRules(remoteRules);
  var dns=PrivateDns.Parse(await cloud.RpcAsync(new{type="dns_config"},ct),profile);
  await kernel.Start(MihomoConfiguration.Build(nodes,kernel.Secret,true,dns,store.Settings.DirectRules),ct);
  for(var i=0;i<40;i++){tunnel=KernelSession.Tunnel();if(tunnel!=0)break;await Task.Delay(200,ct);}
  if(tunnel==0)throw new InvalidOperationException("没有找到已启动的本机 TUN 接口");
  ApplyGuard(tunnel);await VerifyDns(ct);
  configured=true;lastError="";guardSince=DateTimeOffset.UtcNow;Record("protected");await Health(ct);
 }
 async Task VerifyDns(CancellationToken stop)
 {
  if(cloud is null)throw new InvalidOperationException("尚未连接服务器");
  var probe=await cloud.RpcAsync(new{type="dns_probe_begin"},stop);
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(7));
  var addresses=await Dns.GetHostAddressesAsync(probe.GetProperty("name").GetString()!,deadline.Token);
  var expected=Text(probe,"answer");
  if(!IPAddress.TryParse(expected,out var wanted)||!addresses.Any(x=>x.Equals(wanted)))throw new InvalidDataException("系统 DNS 检测没有返回预期答案");
  var result=await cloud.RpcAsync(new{type="dns_probe_result",nonce=Text(probe,"nonce")},deadline.Token);
  if(!Bool(result,"matched"))throw new InvalidDataException("私有 DNS 检测回执与预期解析来源不符");
  dnsVerified=true;
 }
 async Task<string> ObserveExit(CancellationToken stop)
 {
  if(profile is null)throw new InvalidOperationException("尚未连接服务器");
  // This explicit proxy cannot fall back to a direct socket. The same pinned TLS gateway
  // observes the outgoing connection from the selected upstream back to our server.
  using var probe=new PrivateServerClient(profile,TimeSpan.FromSeconds(8),throughLocalProxy:true);
  var result=await probe.SendAsync("/api/v1/egress-probe",cancellation:stop);var observed=Text(result,"observed_source");
  if(!AddressPolicy.IsLiteral(observed)||!AddressPolicy.IsPublic(observed))throw new InvalidDataException("自有服务器没有观察到有效公网出口");
  return observed;
 }
 async Task Health(CancellationToken stop)
 {
  if(cloud is null)throw new InvalidOperationException("尚未连接服务器");
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(12));var ct=deadline.Token;
  await cloud.SendAsync("/api/v1/status",cancellation:ct);
  if(!kernel.Alive || KernelSession.Tunnel()!=tunnel || !filters.DefaultGuardsInstalled())throw new InvalidOperationException("防护组件或本机规则发生变化");
  await kernel.Get("version",ct);
  exitVerified=false;exitIp=await ObserveExit(ct);exitVerified=true;
  await RefreshExitGeo(ct);await SampleLines(ct);
  var traffic=await kernel.Rates(ct);up=traffic.Up;down=traffic.Down;
  var proxies=await kernel.Get("proxies/"+Uri.EscapeDataString("出口"),ct);selected=Text(proxies,"now");
  heartbeat=DateTimeOffset.UtcNow;lastHealth=heartbeat;
  var evidence=new ProtectionEvidence(kernel.Alive,tunnel!=0,filters.DefaultGuardsInstalled(),dnsVerified,exitVerified,heartbeat);
  if(evidence.Assess(heartbeat)!=ProtectionPhase.Protected)throw new InvalidOperationException("本机防护证据不完整");
  state="protected";failures=0;
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
  return new{version=Program.Version,consent_version=store.Settings.ConsentVersion,protection=new{state=visible,reason,remaining_secs=remaining,auto_resume_on_node=false,quit=store.Settings.QuitPause,blocked=visible=="blocked",since_secs=0},lines,current_line=index==0?"1":index.ToString(),line_mode=new{mode},rate=visible=="protected"?new{up_bps=up,down_bps=down}:null,guard=visible=="protected"?new{since=guardSince.ToUnixTimeSeconds(),blocked=(int?)null}:null,checks=bypassCheck is not null&&visible=="protected"&&JsonSerializer.SerializeToElement(bypassCheck).GetProperty("verdict").GetString()=="fail"?new[]{bypassCheck}:Array.Empty<object>(),conflicts,proxy_undo=proxy.UndoAvailable,last_error=lastError,update=updateReady,last_check=lastCheck is JsonElement lc?new{at=lc.GetProperty("at").GetInt64(),kind=Text(lc,"kind"),problems=lc.GetProperty("problems").GetInt32()}:null};
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
  await gate.WaitAsync(stop);
  try
  {
   switch(type)
   {
    case "pair":
     NeedOwner(request);await PairWith(ServerProfile.Parse(JsonSerializer.SerializeToUtf8Bytes(request.GetProperty("profile"))),stop);return new{saved=true};
    case "activate":case "restore":
     // One authorization code = one computer: the server binds the code to this hardware ID.
     var issued=await RedeemCode(new{code=Text(request,"code"),machine=MachineId.Get(),contact=Text(request,"contact"),order_no=Text(request,"order_no"),label=Environment.MachineName},stop);
     await PairWith(issued,stop);Record("protected");return new{saved=true};
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
    case "add_direct_rule":
     var rule=DirectRule.Parse(Text(request,"rule"),Text(request,"note"),Bool(request,"confirm_public"));if(rule.Public)NeedOwner(request);
     if(store.Settings.DirectRules.Count>=64&&!store.Settings.DirectRules.Any(x=>x.Cidr==rule.Cidr))throw new InvalidOperationException("直连规则最多 64 条");
     var added=await Remote(new{type="add_direct_rule",rule=rule.Cidr,note=rule.Note,confirm_public=rule.Public},stop);ReadRules(added);configured=false;return new{saved=true};
    case "remove_direct_rule":var removed=await Remote(request,stop);ReadRules(removed);configured=false;return new{saved=true};
    case "set_direct_note":
     var at=store.Settings.DirectRules.FindIndex(r=>r.Cidr==Text(request,"cidr"));if(at<0)throw new InvalidOperationException("找不到这条规则");
     var old=store.Settings.DirectRules[at];DirectRule.Parse(old.Cidr,Text(request,"note"),old.Public);var edited=await Remote(request,stop);ReadRules(edited);return new{saved=true};
    case "set_timezone":
     if(store.Settings.ExitTimezone.Length==0)throw new InvalidOperationException("还没有取得出口所在时区，请在防护建立后再试");
     store.Settings.OriginalTimezone??=TimeZoneInfo.Local.Id;store.Save();await SystemClock.SetTimezone(store.Settings.ExitTimezone,stop);return Zone();
    case "restore_timezone":
     if(store.Settings.OriginalTimezone is string original){await SystemClock.SetTimezone(original,stop);store.Settings.OriginalTimezone=null;store.Save();}return Zone();
    case "dismiss_timezone":store.Settings.TimezoneDismissedFor=store.Settings.ExitTimezone;store.Save();return new{saved=true};
    case "sync_time":
     var clock=await Remote(new{type="server_time"},stop);if(!Bool(clock,"synchronized"))throw new InvalidOperationException("服务器时间尚未完成同步");
     SystemClock.SetUtc(clock.GetProperty("unix").GetDouble());return new{saved=true};
    case "shell_action":return await ShellAction(request,stop);
    case "tickets":var tickets=await Remote(request,stop);return new{items=tickets.GetProperty("items").Clone(),images=true};
    case "ai_secrets":if(state!="protected")throw new InvalidOperationException("请先恢复防护，再显示登录信息");return await Remote(request,stop);
    case "new_ticket":case "ticket_reply":case "ticket_rate":case "upload_chunk":case "ticket_image":case "messages":case "mark_read":case "set_contact":case "help":case "ai_accounts":case "ai_report":return await Remote(request,stop);
    case "select_exit":case "delete_exit":var result=await Remote(request,stop);configured=false;return result;
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
 async Task PairWith(ServerProfile imported,CancellationToken stop)
 {
  using(var setup=new PrivateServerClient(imported))
  {
   var paired=await setup.EnrollAsync(Guid.NewGuid(),Environment.MachineName,stop);
   store.SetProfile(paired.Profile);profile=paired.Profile;cloud?.Dispose();cloud=new PrivateServerClient(profile);nodes=paired.Nodes;
  }
  store.Settings.Enabled=true;store.Settings.PausedUntil=0;store.Settings.QuitPause=false;store.Save();configured=false;kernel.Stop();retryAt=DateTimeOffset.MinValue;
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
 async Task<JsonElement> Remote(object request,CancellationToken stop)
 {
  if(cloud is null)throw new InvalidOperationException("请先连接自有服务器");return await cloud.RpcAsync(request,stop);
 }
 public void Dispose(){probe.Dispose();kernel.Dispose();cloud?.Dispose();filters.Dispose();gate.Dispose();}
}
