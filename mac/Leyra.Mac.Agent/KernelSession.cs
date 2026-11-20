using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leyra.Windows.Core;

namespace Leyra.Mac.Agent;

sealed class KernelSession:IDisposable
{
 readonly HttpClient controller;Process? process;
 // macOS names tunnel interfaces utunN; a fixed number lets the service find its own.
 public const string TunnelName="utun225";
 public string Secret{get;}=Guid.NewGuid().ToString();
 public string Executable=>Path.Combine(AppContext.BaseDirectory,"mihomo");
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
  Stop();KillStale();VerifyBinary();var dir=Path.Combine(AgentStore.Root,"kernel");Directory.CreateDirectory(dir);
  // Shared configuration names the Windows adapter; on the Mac the tunnel must be called utunN.
  var json=JsonNode.Parse(config)!.AsObject();if(json["tun"] is JsonObject tun)tun["device"]=TunnelName;
  var path=Path.Combine(dir,"runtime.json");AgentStore.Atomic(path,System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
  using(var check=Launch(["-t","-d",dir,"-f",path],dir))
  {
   using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(10));
   try{await check.WaitForExitAsync(deadline.Token);}catch{check.Kill(true);throw;}
   if(check.ExitCode!=0)throw new InvalidDataException("网络配置未通过内核校验，已保留断网保护");
  }
  process=Launch(["-d",dir,"-f",path],dir);
  for(var i=0;i<30;i++)
  {
   stop.ThrowIfCancellationRequested();if(!Alive)throw new InvalidOperationException("网络组件启动后退出");
   try{await Get("version",stop);return;}catch(HttpRequestException){}catch(TaskCanceledException)when(!stop.IsCancellationRequested){}
   await Task.Delay(200,stop);
  }
  throw new TimeoutException("网络控制接口尚未就绪");
 }
 public async Task<JsonElement> Get(string path,CancellationToken stop)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(4));stop=deadline.Token;
  using var response=await controller.GetAsync(path,HttpCompletionOption.ResponseHeadersRead,stop);response.EnsureSuccessStatusCode();
  if(response.Content.Headers.ContentLength>2*1024*1024)throw new InvalidDataException("网络控制响应过大");
  await using var stream=await response.Content.ReadAsStreamAsync(stop);using var memory=new MemoryStream();var bytes=new byte[16384];int n;
  while((n=await stream.ReadAsync(bytes,stop))>0){if(memory.Length+n>2*1024*1024)throw new InvalidDataException("网络控制响应过大");memory.Write(bytes,0,n);}
  using var doc=JsonDocument.Parse(memory.ToArray());return doc.RootElement.Clone();
 }
 // Ends every open connection (new ones are opened again by the programs, through the current exit).
 public async Task CloseConnections(CancellationToken stop)
 {
  using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop);deadline.CancelAfter(TimeSpan.FromSeconds(4));
  using var response=await controller.DeleteAsync("connections",deadline.Token);response.EnsureSuccessStatusCode();
 }
 public async Task Select(string name,CancellationToken stop)
 {
  using var request=new HttpRequestMessage(HttpMethod.Put,"proxies/"+Uri.EscapeDataString("出口")){Content=new StringContent(Leyra.Windows.Core.LeyraJson.ToText(new{name}),System.Text.Encoding.UTF8,"application/json")};
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
 Process Launch(string[] args,string dir)
 {
  var start=new ProcessStartInfo(Executable,args){UseShellExecute=false,WorkingDirectory=dir,RedirectStandardOutput=false,RedirectStandardError=false};
  return Process.Start(start)??throw new InvalidOperationException("无法启动网络组件");
 }
 /// <summary>A kernel left running by a service that crashed would hold the tunnel: end it first.</summary>
 void KillStale()
 {
  foreach(var p in Process.GetProcessesByName("mihomo"))
  {
   try{if(string.Equals(p.MainModule?.FileName,Executable,StringComparison.Ordinal)){p.Kill(true);p.WaitForExit(3000);}}
   catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException){}
   finally{p.Dispose();}
  }
 }
 [DllImport("libc")]static extern uint if_nametoindex(string name);
 /// <summary>Interface index of this service's tunnel, 0 when it does not exist.</summary>
 public static ulong Tunnel()
 {
  var up=NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name==TunnelName&&n.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown);
  return up?if_nametoindex(TunnelName):0;
 }
 public void Stop()
 {
  if(process is null)return;
  try{if(!process.HasExited){process.Kill(true);process.WaitForExit(5000);}}catch(InvalidOperationException){}
  finally{process.Dispose();process=null;}
 }
 public void Dispose(){Stop();controller.Dispose();}
}
