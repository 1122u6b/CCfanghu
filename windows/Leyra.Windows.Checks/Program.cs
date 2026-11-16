using Leyra.Windows.Core;
using Leyra.Windows.Platform;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
// Controlled child modes exercise the same ordinary argv launch used by the kernel.
if(args.Length>0 && args[0]=="--fixture-job-exit")Environment.Exit(args.Length==2 && args[1]=="有 空格"?7:9);
if(args.Length==1 && args[0]=="--fixture-job-wait"){await Task.Delay(TimeSpan.FromSeconds(30));Environment.Exit(0);}
var passed=0;
void Check(string name,Action test){test();passed++;Console.WriteLine("PASS "+name);}
void Require(bool value){if(!value)throw new Exception("Check failed");}
void Reject(Action test){try{test();}catch(InvalidDataException){return;}throw new Exception("Invalid input accepted");}
Check("WFP native ABI and owned cleanup identifiers",()=>{
 WfpLayout.Verify64Bit();var keys=new HashSet<Guid>();
 foreach(var family in new[]{false,true})for(var slot=0;slot<1024;slot++)Require(keys.Add(WfpOwnership.Filter(family,slot)));
 Require(!keys.Contains(WfpOwnership.Provider) && !keys.Contains(WfpOwnership.SubLayer));
});
Check("WFP plans scope kernel exceptions and retain IPv6 and DNS rejection",()=>{
 var plan=WfpPlan.Create("kernel.exe","service.exe",[new GatewayEndpoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.9"),24443),6)],[new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.9"),24443)],123,[]);
 Require(plan.Filters.Count(x=>(x.Slot==0 || x.Slot==512) && !x.Permit)==4);
 Require(plan.Filters.Where(x=>x.Transport).All(x=>x.Application is null && x.Slot>=512));
 Require(plan.Filters.Any(x=>x.Transport && x.Name=="数据包 · 内核到加密通道" && x.Protocol==6 && x.RemotePort==24443));
 Require(plan.Filters.Where(x=>x.Name=="内核到加密通道").All(x=>x.Application=="kernel.exe" && x.RemotePort==24443 && x.Protocol==6));
 Require(plan.Filters.Single(x=>!x.Ipv6 && x.Name=="禁止物理 UDP DNS").Weight>plan.Filters.Single(x=>!x.Ipv6 && x.Slot==10).Weight);
 Require(plan.Filters.Single(x=>!x.Ipv6 && !x.Transport && x.Interface==123).Weight>900);
 var cold=WfpPlan.Create("kernel.exe","service.exe",[new GatewayEndpoint(new System.Net.IPEndPoint(System.Net.IPAddress.Parse("203.0.113.9"),24443),6)],[],0,[]);
 Require(cold.Filters.All(x=>x.Interface is null));
});
Check("strict endpoints reject legacy numeric and injection formats",()=>{
 foreach(var value in new[]{"2130706433","127.1","0177.0.0.1","example.com","1.2.3.999","1.2.3.4\npass","fe80::1%12","::ffff:127.0.0.1"})Require(!AddressPolicy.IsLiteral(value));
 Require(AddressPolicy.IsLiteral("203.0.113.1"));Require(AddressPolicy.IsLiteral("2001:db8::1"));
});
Check("direct CIDR normalization and public consent",()=>{
 Require(DirectRule.Parse("192.168.3.79/24","printer",false).Cidr=="192.168.3.0/24");
 Reject(()=>DirectRule.Parse("192.168.3.79/8","wide public range",false));Reject(()=>DirectRule.Parse("0.0.0.0/0","",true));Reject(()=>DirectRule.Parse("127.0.0.1","",true));Reject(()=>DirectRule.Parse("203.0.113.8","",false));
 Require(DirectRule.Parse("203.0.113.8","own exception",true).Public);
});
Check("unsupported UDP cannot fall through to implicit direct",()=>{
 var rules=RoutingPolicy.Rules();Require(rules[^2]=="MATCH,出口");Require(rules[^1]=="MATCH,REJECT");Require(!rules.Any(x=>x=="MATCH,DIRECT"));
});
Check("stale service or missing filters never shows protected",()=>{
 var now=DateTimeOffset.UtcNow;var ready=new ProtectionEvidence(true,true,true,true,true,now);Require(ready.Assess(now)==ProtectionPhase.Protected);
 Require((ready with {Filters=false}).Assess(now)==ProtectionPhase.UnprotectedFailure);
 Require((ready with {Heartbeat=now-TimeSpan.FromSeconds(15)}).Assess(now)==ProtectionPhase.UnprotectedFailure);
 Require((ready with {Kernel=false}).Assess(now)==ProtectionPhase.GuardedFailure);
 Require((ready with {ExitMatches=false}).Assess(now)==ProtectionPhase.GuardedFailure);
});
Check("skipped audits are counted independently",()=>{
 var count=AuditSummary.Count([new("A","DNS","source",AuditVerdict.Skipped,"no receipt"),new("B","exit","HTTPS",AuditVerdict.Passed,"controlled")]);Require(count==(1,0,0,1));
});
using var rsa=RSA.Create(2048);var req=new CertificateRequest("CN=controlled",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);using var cert=req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-2),DateTimeOffset.UtcNow.AddHours(1));
var profile=new ServerProfile("127.0.0.1",24443,new string('t',40),cert.ExportCertificatePem(),Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant(),"owner",null);
Check("certificate binding and dates fail closed",()=>{
 var parsed=ServerProfile.Parse(profile.Encode());Require(parsed.AcceptCertificate(cert,DateTimeOffset.UtcNow));Require(!parsed.AcceptCertificate(cert,DateTimeOffset.UtcNow.AddDays(1)));
 Reject(()=>ServerProfile.Parse((profile with {Fingerprint=new string('0',64)}).Encode()));Reject(()=>ServerProfile.Parse((profile with {Token="bad\r\nheader"}).Encode()));
});
Check("generated Windows TUN and DNS are scoped to paired infrastructure",()=>{
 using var caps=JsonDocument.Parse(JsonSerializer.Serialize(new {zone="probe.leyra.xin",resolver_url="https://api.leyra.xin/private-dns/"+new string('d',40),resolver_ip="127.0.0.1"}));
 var dns=PrivateDns.Parse(caps.RootElement,profile);
 using var nodes=JsonDocument.Parse("[{\"type\":\"socks5\",\"server\":\"127.0.0.1\",\"port\":24443,\"name\":\"出口\",\"password\":\"controlled-password\"}]");
 using var built=JsonDocument.Parse(MihomoConfiguration.Build(nodes.RootElement,Guid.NewGuid().ToString(),true,dns,[]));
 Require(built.RootElement.GetProperty("tun").GetProperty("device").GetString()=="LeyraConnect");
 Require(built.RootElement.GetProperty("dns").GetProperty("nameserver")[0].GetString()!.EndsWith("#出口"));
 Require(built.RootElement.GetProperty("proxies")[0].GetProperty("password").GetString()=="controlled-password");
 Require(built.RootElement.GetProperty("proxies")[0].GetProperty("name").GetString()!="出口");
 Reject(()=>MihomoConfiguration.Build(nodes.RootElement,Guid.NewGuid().ToString(),true,null,[]));
 using var hostile=JsonDocument.Parse("[{\"type\":\"trojan\",\"server\":\"127.0.0.1\",\"port\":443,\"tls-opts\":{\"private-key\":\"C:/private.pem\"}}]");
 Reject(()=>MihomoConfiguration.ValidateNodes(hostile.RootElement));
 using var other=JsonDocument.Parse(JsonSerializer.Serialize(new {zone="probe.leyra.xin",resolver_url="https://evil.example/private-dns/"+new string('d',40),resolver_ip="127.0.0.1"}));Reject(()=>PrivateDns.Parse(other.RootElement,profile));
});
Check("bounded local IPC preserves JSON and rejects malformed frames",()=>{
 using var stream=new MemoryStream();PipeProtocol.WriteAsync(stream,new {message="中文与设备凭据",number=42}).GetAwaiter().GetResult();stream.Position=0;
 var decoded=PipeProtocol.ReadAsync(stream).GetAwaiter().GetResult();Require(decoded.GetProperty("message").GetString()=="中文与设备凭据" && decoded.GetProperty("number").GetInt32()==42);
 foreach(var length in new[]{0,-1,PipeProtocol.MaximumFrame+1}){using var invalid=new MemoryStream(BitConverter.GetBytes(length));Reject(()=>PipeProtocol.ReadAsync(invalid).GetAwaiter().GetResult());}
 using var array=new MemoryStream();PipeProtocol.WriteAsync(array,new[]{1,2}).GetAwaiter().GetResult();array.Position=0;Reject(()=>PipeProtocol.ReadAsync(array).GetAwaiter().GetResult());
 using var truncated=new MemoryStream(new byte[]{10,0,0,0,123});var failed=false;try{PipeProtocol.ReadAsync(truncated).GetAwaiter().GetResult();}catch(EndOfStreamException){failed=true;}Require(failed);
});
Check("independent device token and DNS credentials stay bound to the paired device",()=>{
 var identity=Guid.NewGuid().ToString("N");var device=ServerProfile.Parse((profile with {Role="device",DeviceId=identity,Token=identity+"."+new string('d',48)}).Encode());
 using var caps=JsonDocument.Parse(JsonSerializer.Serialize(new{zone="probe.leyra.xin",resolver_url="https://api.leyra.xin/private-dns/"+device.Token,resolver_ip="127.0.0.1"}));
 Require(PrivateDns.Parse(caps.RootElement,device).Resolver.AbsolutePath.EndsWith(device.Token));
 using var wrong=JsonDocument.Parse(JsonSerializer.Serialize(new{zone="probe.leyra.xin",resolver_url="https://api.leyra.xin/private-dns/"+new string('x',48),resolver_ip="127.0.0.1"}));Reject(()=>PrivateDns.Parse(wrong.RootElement,device));
});
Check("owner consent proof expires monotonically and is consumed exactly once",()=>{
 var clock=new ControlledClock();var proof=new OwnerProof(clock);var first=proof.Grant();
 Require(!proof.Consume("wrong"));Require(proof.Consume(first));Require(!proof.Consume(first));
 var expired=proof.Grant();clock.Tick(TimeSpan.FromSeconds(31));Require(!proof.Consume(expired));
 var replaced=proof.Grant();var valid=proof.Grant();Require(!proof.Consume(replaced));
 var successes=0;Parallel.For(0,16,_=>{if(proof.Consume(valid))Interlocked.Increment(ref successes);});Require(successes==1);
});
Console.WriteLine($"{passed} controlled policy checks passed. Windows integration has not run.");

if(args.Length==3 && args[0]=="--tls-fixture")
{
 var fixture=ServerProfile.Parse(await File.ReadAllBytesAsync(args[1]));
 if(fixture.Host!="127.0.0.1")throw new Exception("Controlled TLS fixtures must be local");
 using var bootstrap=new PrivateServerClient(fixture);var (device,nodes)=await bootstrap.EnrollAsync(Guid.Parse(args[2]),"Controlled Windows fixture");
 Require(device.Role=="device" && device.Token!=fixture.Token && nodes.GetArrayLength()>0);
 using var client=new PrivateServerClient(device);var account=await client.RpcAsync(new {type="account"});Require(account.GetProperty("id").GetInt32()>0);
 await client.RpcAsync(new {type="set_contact",contact="controlled-windows@example.test"});
 try {await client.RpcAsync(new {type="admin_message",title="forbidden",body="forbidden"});throw new Exception("Device escalated owner permission");}
 catch(HttpRequestException e) when (e.StatusCode==System.Net.HttpStatusCode.BadRequest){}
 var bad=fixture with {Fingerprint=new string('0',64)};using var rejected=new PrivateServerClient(bad);
 try {await rejected.SendAsync("/api/v1/config");throw new Exception("Wrong server fingerprint accepted");}catch(HttpRequestException){}
 Console.WriteLine("PASS actual controlled TLS: device enrollment, owner-token separation, authenticated account, scoped mutation, admin rejection and certificate mismatch rejection");
}

if(args.Length==2 && args[0]=="--timeout-fixture")
{
 var fixture=ServerProfile.Parse(await File.ReadAllBytesAsync(args[1]));
 if(fixture.Host!="127.0.0.1")throw new Exception("Controlled TLS fixtures must be local");
 using var client=new PrivateServerClient(fixture,TimeSpan.FromMilliseconds(300));
 try {await client.SendAsync("/api/v1/config");throw new Exception("Hanging response body accepted");}
 catch(OperationCanceledException){Console.WriteLine("PASS response body deadline cancels an authenticated server that never completes its body");}
}


if(args.Length==1 && args[0]=="--native-job-fixture")
{
 if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Disposable Windows runner required");
 await NativeFixture.Processes();
}
if(args.Length==1 && args[0]=="--native-wfp-fixture")
{
 if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Disposable Windows runner required");
 await NativeFixture.Run();
}
if(args.Length==1 && args[0]=="--cleanup-wfp-fixture")
{
 if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Disposable Windows runner required");
 NativeFixture.Cleanup();
}

if(args.Length==3 && args[0]=="--kernel-config")
{
 var fixture=ServerProfile.Parse(await File.ReadAllBytesAsync(args[1]));
 if(fixture.Host!="127.0.0.1")throw new Exception("Kernel fixture must be loopback");
 using var client=new PrivateServerClient(fixture);var configuration=await client.SendAsync("/api/v1/config");
 await File.WriteAllBytesAsync(args[2],MihomoConfiguration.Build(configuration.GetProperty("proxies"),Guid.NewGuid().ToString(),false,null,[]));
}

sealed class ControlledClock:TimeProvider
{
 long stamp;
 public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
 public override long GetTimestamp()=>stamp;
 public void Tick(TimeSpan duration)=>stamp+=duration.Ticks;
}
