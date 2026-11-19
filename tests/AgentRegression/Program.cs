using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

if(args.SequenceEqual(new[]{"--live-speed"})){
 using var handler=new HttpClientHandler{UseProxy=true,Proxy=new System.Net.WebProxy("http://127.0.0.1:17890"),AllowAutoRedirect=false};
 using var http=new HttpClient(handler);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(28));
 using var response=await http.GetAsync("https://speed.cloudflare.com/__down?bytes=1048576",HttpCompletionOption.ResponseHeadersRead,deadline.Token);response.EnsureSuccessStatusCode();
 using var input=await response.Content.ReadAsStreamAsync(deadline.Token);
 var result=await Leyra.Windows.Core.DownloadSample.MeasureAsync(input,512*1024,TimeSpan.FromSeconds(8),deadline.Token);
 Console.WriteLine(JsonSerializer.Serialize(result));return;
}

// Exercise browser result handling without starting a service, TUN, WFP or real network.
var assembly=Assembly.Load("Leyra.Agent");
var type=assembly.GetType("Leyra.Windows.Agent.Runtime",true)!;
object Fresh(string exit)
{
 var runtime=RuntimeHelpers.GetUninitializedObject(type);
 void Set(string name,object value)=>type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(runtime,value);
 Set("exitIp",exit);Set("browserResults",new Dictionary<string,object>());Set("events",new List<object>());
 return runtime;
}
JsonElement Call(object runtime,string name,params object?[] args)=>JsonSerializer.SerializeToElement(type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(runtime,args));
void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS "+message);}
var runtime=Fresh("8.8.8.8");
var incomplete=Call(runtime,"BrowserReport","Chrome",null,Array.Empty<(string Ip,string Type)>());
Check(incomplete.GetProperty("exposure").GetString()=="unknown","missing browser IP is incomplete, never clean");
var checks=Call(runtime,"BrowserChecks");
Check(checks.EnumerateArray().All(x=>x.GetProperty("verdict").GetString()=="skipped"),"incomplete IP/WebRTC results remain unverified");
Call(runtime,"BrowserReport","Chrome","8.8.8.8",Array.Empty<(string Ip,string Type)>());
checks=Call(runtime,"BrowserChecks");
Check(checks[0].GetProperty("detail").GetString()!.Contains("8.8.8.8"),"browser IP is carried into the safety report");
Check(checks[1].GetProperty("verdict").GetString()=="pass","complete matching browser probe updates WebRTC row");
type.GetField("exitIp",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(runtime,"1.1.1.1");
checks=Call(runtime,"BrowserChecks");
Check(checks.EnumerateArray().All(x=>x.GetProperty("verdict").GetString()=="skipped"),"changing exits invalidates the previous browser evidence");
Call(runtime,"BrowserReport","Chrome","8.8.8.8",Array.Empty<(string Ip,string Type)>());
checks=Call(runtime,"BrowserChecks");
Check(checks.EnumerateArray().All(x=>x.GetProperty("verdict").GetString()=="fail"),"a different public IP fails both related rows");

var speed=await Leyra.Windows.Core.DownloadSample.MeasureAsync(new SlowDownload(),1024,TimeSpan.FromMilliseconds(100));
Check(speed.Bytes==128&&speed.BytesPerSecond>0,"a slow download returns measured bytes when the sample window ends");
speed=await Leyra.Windows.Core.DownloadSample.MeasureAsync(new MemoryStream(new byte[1024]),64,TimeSpan.FromSeconds(1));
Check(speed.Bytes==64,"download sampling never reads past its byte budget");
try{await Leyra.Windows.Core.DownloadSample.MeasureAsync(new MemoryStream(),64,TimeSpan.FromSeconds(1));throw new Exception("Empty download accepted");}
catch(InvalidDataException){Console.WriteLine("PASS an empty download cannot produce a speed result");}
using(var canceled=new CancellationTokenSource()){
 canceled.Cancel();
 try{await Leyra.Windows.Core.DownloadSample.MeasureAsync(new SlowDownload(),1024,TimeSpan.FromSeconds(1),canceled.Token);throw new Exception("Canceled sample accepted");}
 catch(OperationCanceledException){Console.WriteLine("PASS external cancellation is never reported as a successful speed test");}
}

var fallbackCalls=0;
Task<string> Backup(CancellationToken ct){fallbackCalls++;return Task.FromResult("8.8.8.8");}
var observed=await Leyra.Windows.Core.EgressProbe.ObserveAsync(_=>Task.FromResult("1.1.1.1"),Backup,default);
Check(observed=="1.1.1.1"&&fallbackCalls==0,"a valid primary observation needs no fallback");
observed=await Leyra.Windows.Core.EgressProbe.ObserveAsync(_=>Task.FromException<string>(new HttpRequestException("TLS EOF",new IOException("unexpected EOF"))),Backup,default);
Check(observed=="8.8.8.8"&&fallbackCalls==1,"a closed monitoring connection can be checked through an independent proxy-only observation");
foreach(var error in new Exception[]{new HttpRequestException("certificate rejected",new System.Security.Authentication.AuthenticationException()),new HttpRequestException("disabled",null,System.Net.HttpStatusCode.Forbidden),new HttpRequestException("revoked",null,System.Net.HttpStatusCode.Unauthorized),new InvalidDataException("invalid server response")}){
 var calls=fallbackCalls;try{await Leyra.Windows.Core.EgressProbe.ObserveAsync(_=>Task.FromException<string>(error),Backup,default);throw new Exception("Security failure accepted");}catch(Exception actual)when(ReferenceEquals(actual,error)){}
 Check(fallbackCalls==calls,"certificate or authorization failure never uses fallback: "+error.Message);
}
try{await Leyra.Windows.Core.EgressProbe.ObserveAsync(_=>Task.FromException<string>(new IOException()),_=>Task.FromResult("127.0.0.1"),default);throw new Exception("Non-public egress accepted");}
catch(InvalidDataException){Console.WriteLine("PASS independent observation cannot accept a local or invalid IP");}
using(var stopped=new CancellationTokenSource()){
 stopped.Cancel();var calls=fallbackCalls;
 try{await Leyra.Windows.Core.EgressProbe.ObserveAsync(_=>Task.FromCanceled<string>(stopped.Token),Backup,stopped.Token);throw new Exception("Stopped observation accepted");}catch(OperationCanceledException){}
 Check(fallbackCalls==calls,"stopping protection cancels observation without starting another check");
}

// 「下次开机时更新」: installs only right after the computer starts, only a newer version.
{
 var decide=type.GetMethod("BootUpdate",BindingFlags.Static|BindingFlags.NonPublic)!;
 string D(string? v,string cur,long ms)=>(string)decide.Invoke(null,[v,cur,ms])!;
 Check(D(null,"0.4.19",1000)=="none","nothing put off: nothing happens at startup");
 Check(D("0.4.20","0.4.19",2*60*1000)=="install","computer just started: the put-off version is installed");
 Check(D("0.4.20","0.4.19",3*60*60*1000)=="wait","service restarted hours after startup: waits for the real next start");
 Check(D("0.4.19","0.4.19",1000)=="drop","already updated by hand: the reminder is dropped");
 Check(D("0.4.18","0.4.19",1000)=="drop","older version is never installed");
}

sealed class SlowDownload:Stream
{
 bool sent;
 public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default){if(sent){await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}await Task.Delay(1,cancellationToken);sent=true;buffer.Span[..128].Clear();return 128;}
 public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
 public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
 public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}

