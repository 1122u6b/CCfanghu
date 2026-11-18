using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Leyra.Windows.Core;
namespace Leyra.Mac.Agent;
/// <summary>The window talks to the service over a Unix socket. Same frames as the Windows pipe (4-byte length +
/// JSON {version:1, request}). Only the user recorded at install time is answered: the kernel reports the peer's
/// uid (getpeereid), which a program cannot fake.</summary>
sealed class LocalSocket(Runtime runtime,uint owner)
{
 public const string Path="/var/run/leyra/agent.sock";
 [DllImport("libc",SetLastError=true)]static extern int getpeereid(int fd,out uint euid,out uint egid);
 [DllImport("libc",SetLastError=true)]static extern int chown(string path,uint owner,uint group);
 [DllImport("libc",SetLastError=true)]static extern int chmod(string path,ushort mode);
 public async Task Run(CancellationToken stop)
 {
  Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
  chmod(System.IO.Path.GetDirectoryName(Path)!,0x1ED); // 0755
  if(File.Exists(Path))File.Delete(Path);
  using var server=new Socket(AddressFamily.Unix,SocketType.Stream,ProtocolType.Unspecified);
  server.Bind(new UnixDomainSocketEndPoint(Path));
  // Readable/writable by root and the owner only.
  if(chown(Path,owner,0)!=0||chmod(Path,0x180)!=0)throw new IOException("无法设置本机通信的权限"); // 0600
  server.Listen(16);
  using var slots=new SemaphoreSlim(6,6);var tasks=new List<Task>();
  try
  {
   while(!stop.IsCancellationRequested)
   {
    await slots.WaitAsync(stop);
    Socket client;
    try{client=await server.AcceptAsync(stop);}
    catch(OperationCanceledException)when(stop.IsCancellationRequested){slots.Release();throw;}
    catch(SocketException e){slots.Release();Program.Log("socket accept failed, listening again",e);await Task.Delay(200,stop);continue;}
    tasks.RemoveAll(t=>t.IsCompleted);
    tasks.Add(Task.Run(async()=>{try{await Client(client,stop);}finally{client.Dispose();slots.Release();}},CancellationToken.None));
   }
  }
  catch(OperationCanceledException)when(stop.IsCancellationRequested){}
  finally{await Task.WhenAll(tasks);try{File.Delete(Path);}catch(IOException){}}
 }
 async Task Client(Socket socket,CancellationToken stop)
 {
  try
  {
   if(getpeereid((int)socket.Handle,out var uid,out _)!=0)throw new UnauthorizedAccessException("无法确认连接方身份");
   if(uid!=owner&&uid!=0)throw new UnauthorizedAccessException("caller uid "+uid+" is not owner "+owner);
   await using var stream=new NetworkStream(socket,ownsSocket:false);
   using var read=CancellationTokenSource.CreateLinkedTokenSource(stop);read.CancelAfter(TimeSpan.FromSeconds(5));
   var envelope=await PipeProtocol.ReadAsync(stream,read.Token);
   if(envelope.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("本机通信版本不匹配");
   var request=envelope.GetProperty("request");var deep=request.TryGetProperty("type",out var type)&&type.GetString()=="doctor"&&request.TryGetProperty("deep",out var d)&&d.ValueKind==JsonValueKind.True;
   var slow=type.ValueKind==JsonValueKind.String&&type.GetString() is "activate" or "restore" or "own_exit" or "switch_code";
   using var operation=CancellationTokenSource.CreateLinkedTokenSource(stop);operation.CancelAfter(TimeSpan.FromSeconds(deep?90:slow?95:32));
   object result;
   try{result=new{ok=true,value=await runtime.Handle(request,operation.Token)};}
   catch(OperationCanceledException e)when(!stop.IsCancellationRequested){Program.Log("request timed out: "+(type.ValueKind==JsonValueKind.String?type.GetString():"?"),e);result=new{ok=false,error="操作超时，请检查网络后重试；已激活的授权可直接恢复"};}
   catch(Exception e)when(e is not OperationCanceledException){result=new{ok=false,error=e.Message};}
   using var write=CancellationTokenSource.CreateLinkedTokenSource(stop);write.CancelAfter(TimeSpan.FromSeconds(3));
   await PipeProtocol.WriteAsync(stream,result,write.Token);
  }
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or InvalidDataException or SocketException)
  {
   // Request contents and credentials are never logged; only why a connection was refused.
   if(e is not OperationCanceledException)Program.Log("socket connection refused",new Exception(e.GetType().Name+": "+e.Message));
  }
 }
}
