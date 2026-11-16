using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Leyra.Windows.Core;

namespace Leyra.Windows.Agent;

sealed class KernelSession:IDisposable
{
 readonly HttpClient controller;readonly Leyra.Windows.Platform.NativeJob job=new();Leyra.Windows.Platform.NativeChild? process;
 public string Secret{get;}=Guid.NewGuid().ToString();
 public string Executable=>Path.Combine(AppContext.BaseDirectory,"mihomo.exe");
 public bool Alive {get{var p=process;try{return p is not null&&!p.HasExited;}catch(ObjectDisposedException){return false;}}}
 public KernelSession()
 {
  controller=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){BaseAddress=new Uri("http://127.0.0.1:19090/"),Timeout=TimeSpan.FromSeconds(4)};
  controller.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Secret);
 }
 public void VerifyBinary()
 {
  var manifest=Path.Combine(AppContext.BaseDirectory,"kernel-origin.json");
  if(!File.Exists(Executable)||!File.Exists(manifest))throw new InvalidOperationException("内置网络组件缺失，请重新安装");
  using var doc=JsonDocument.Parse(File.ReadAllBytes(manifest));var expected=doc.RootElement.GetProperty("binary_sha256").GetString()??"";
  if(expected.Length!=64)throw new InvalidDataException("网络组件校验信息无效");
  using var file=File.OpenRead(Executable);var actual=SHA256.HashData(file);
  if(!CryptographicOperations.FixedTimeEquals(actual,Convert.FromHexString(expected)))throw new InvalidDataException("内置网络组件校验失败，拒绝启动");
 }
 public async Task Start(byte[] config,CancellationToken stop)
 {
  Stop();VerifyBinary();var dir=Path.Combine(AgentStore.Root,"kernel");Directory.CreateDirectory(dir);
  var path=Path.Combine(dir,"runtime.json");AgentStore.Atomic(path,config);
  using(var check=job.Launch(Executable,["-t","-d",dir,"-f",path],dir))
  {
   using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(10));
   try{await check.WaitForExitAsync(deadline.Token);}catch{check.Kill();throw;}
   if(check.ExitCode!=0)throw new InvalidDataException("网络配置未通过内核校验，已保留断网保护");
  }
  process=job.Launch(Executable,["-d",dir,"-f",path],dir);
  for(var i=0;i<30;i++)
  {
   stop.ThrowIfCancellationRequested();if(!Alive)throw new InvalidOperationException("网络组件启动后退出");
   try{await Get("version",stop);return;}catch(HttpRequestException){}catch(TaskCanceledException)when(!stop.IsCancellationRequested){}
   await Task.Delay(200,stop);
  }
  throw new TimeoutException("网络控制接口尚未就绪");
 }
 static async Task Drain(StreamReader reader){try{while(await reader.ReadLineAsync() is not null){}}catch(IOException){}catch(ObjectDisposedException){}}
 public async Task<JsonElement> Get(string path,CancellationToken stop)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(4));stop=deadline.Token;
  using var response=await controller.GetAsync(path,HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
  if(response.Content.Headers.ContentLength>2*1024*1024)throw new InvalidDataException("网络控制响应过大");
  await using var stream=await response.Content.ReadAsStreamAsync(stop);using var memory=new MemoryStream();var bytes=new byte[16384];int n;
  while((n=await stream.ReadAsync(bytes,stop))>0){if(memory.Length+n>2*1024*1024)throw new InvalidDataException("网络控制响应过大");memory.Write(bytes,0,n);}
  using var doc=JsonDocument.Parse(memory.ToArray());return doc.RootElement.Clone();
 }
 public async Task Select(string name,CancellationToken stop)
 {
  using var request=new HttpRequestMessage(HttpMethod.Put,"proxies/"+Uri.EscapeDataString("出口")){Content=new StringContent(JsonSerializer.Serialize(new{name}),System.Text.Encoding.UTF8,"application/json")};
  using var response=await controller.SendAsync(request,stop);response.EnsureSuccessStatusCode();
 }
 public async Task<(long Up,long Down)> Rates(CancellationToken stop)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(4));stop=deadline.Token;
  using var response=await controller.GetAsync("traffic",HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
  using var stream=await response.Content.ReadAsStreamAsync(stop);
  var bytes=new List<byte>();
  while(bytes.Count<4096){var next=new byte[1];if(await stream.ReadAsync(next,stop)==0)break;if(next[0]==10)break;bytes.Add(next[0]);}
  using var doc=JsonDocument.Parse(bytes.ToArray());return (doc.RootElement.GetProperty("up").GetInt64(),doc.RootElement.GetProperty("down").GetInt64());
 }
 public static async Task<string> ExitAddress(CancellationToken stop)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(8));stop=deadline.Token;
  using var handler=new HttpClientHandler{Proxy=new WebProxy("http://127.0.0.1:17890"),UseProxy=true,AllowAutoRedirect=false};
  using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(8)};
  using var response=await http.GetAsync("https://api.ipify.org?format=json",HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
  if(response.Content.Headers.ContentLength>1024)throw new InvalidDataException("出口响应无效");
  await using var body=await response.Content.ReadAsStreamAsync(stop);using var memory=new MemoryStream();var b=new byte[1025];var read=await body.ReadAsync(b,stop);
  if(read==1025)throw new InvalidDataException("出口响应过大");memory.Write(b,0,read);
  // Read a potentially fragmented small response to EOF, within the same bound.
  while((read=await body.ReadAsync(b.AsMemory(0,1025-(int)memory.Length),stop))>0){memory.Write(b,0,read);if(memory.Length>1024)throw new InvalidDataException("出口响应过大");}
  using var doc=JsonDocument.Parse(memory.ToArray());var ip=doc.RootElement.GetProperty("ip").GetString()??"";
  if(!AddressPolicy.IsLiteral(ip) || !AddressPolicy.IsPublic(ip))throw new InvalidDataException("未取得有效公网出口地址");return ip;
 }
 [DllImport("iphlpapi.dll")]static extern uint ConvertInterfaceIndexToLuid(uint index,out ulong luid);
 public static ulong Tunnel()
 {
  foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
  {
   if(nic.Name!="LeyraConnect" || nic.OperationalStatus!=OperationalStatus.Up)continue;
   var index=nic.GetIPProperties().GetIPv4Properties()?.Index;
   if(index is int value && ConvertInterfaceIndexToLuid((uint)value,out var luid)==0)return luid;
  }
  return 0;
 }
 public void Stop()
 {
  if(process is null)return;
  try{if(!process.HasExited){job.Stop();process.WaitForExit(5000);}}catch(InvalidOperationException){}
  finally{process.Dispose();process=null;}
 }
 public void Dispose(){Stop();job.Dispose();controller.Dispose();}
}
