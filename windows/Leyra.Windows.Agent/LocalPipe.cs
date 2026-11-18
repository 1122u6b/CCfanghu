using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Leyra.Windows.Core;
namespace Leyra.Windows.Agent;
sealed class LocalPipe(Runtime runtime,SecurityIdentifier owner)
{
 public async Task Run(CancellationToken stop)
 {
  var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
  security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
  security.AddAccessRule(new PipeAccessRule(owner,PipeAccessRights.ReadWrite,AccessControlType.Allow));
  NamedPipeServerStream Create(bool first)=>NamedPipeServerStreamAcl.Create(PipeProtocol.Name,PipeDirection.InOut,8,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|(first?PipeOptions.FirstPipeInstance:PipeOptions.None),65536,65536,security);
  async Task<NamedPipeServerStream> CreateAgain(CancellationToken token)
  {
   for(var attempt=1;;attempt++)
   {
    try{return Create(false);}
    catch(IOException e)when(attempt<50){if(attempt==1)Program.Log("pipe instance busy, retrying",e);await Task.Delay(200,token);}
   }
  }
  using var slots=new SemaphoreSlim(6,6);var tasks=new List<Task>();var current=Create(true);
  try
  {
   while(!stop.IsCancellationRequested)
   {
    await slots.WaitAsync(stop);
    try{await current.WaitForConnectionAsync(stop);}
    catch(OperationCanceledException)when(stop.IsCancellationRequested){slots.Release();throw;}
    catch(Exception e)when(e is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
    {
     // A window that closed its connection before it was accepted must not end the listener:
     // drop that instance and keep listening, otherwise the app could never reach the service again.
     slots.Release();Program.Log("pipe accept failed, listening again",e);
     current.Dispose();current=await CreateAgain(stop);continue;
    }
    var accepted=current;
    try{current=await CreateAgain(stop);}catch{accepted.Dispose();slots.Release();throw;}
    tasks.RemoveAll(t=>t.IsCompleted);
    tasks.Add(Task.Run(async()=>{try{await Client(accepted,stop);}finally{accepted.Dispose();slots.Release();}},CancellationToken.None));
   }
  }
  catch(OperationCanceledException)when(stop.IsCancellationRequested){}
  finally{current.Dispose();await Task.WhenAll(tasks);}
 }
 async Task Client(NamedPipeServerStream pipe,CancellationToken stop)
 {
  try
  {
   using var read=CancellationTokenSource.CreateLinkedTokenSource(stop);read.CancelAfter(TimeSpan.FromSeconds(5));
   var envelope=await PipeProtocol.ReadAsync(pipe,read.Token);
   // Windows only allows checking who is on the other end after data has been read; nothing is acted on before this check.
   SecurityIdentifier? caller=null;pipe.RunAsClient(()=>caller=WindowsIdentity.GetCurrent(true)?.User);
   if(caller is null||!caller.Equals(owner))throw new UnauthorizedAccessException("caller "+(caller?.Value??"(none)")+" is not owner "+owner.Value);
   if(envelope.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("本机通信版本不匹配");
   var request=envelope.GetProperty("request");var deep=request.TryGetProperty("type",out var type)&&type.GetString()=="doctor"&&request.TryGetProperty("deep",out var d)&&d.ValueKind==JsonValueKind.True;
   var slow=type.ValueKind==JsonValueKind.String&&type.GetString() is "activate" or "restore" or "own_exit" or "switch_code";
   using var operation=CancellationTokenSource.CreateLinkedTokenSource(stop);operation.CancelAfter(TimeSpan.FromSeconds(deep?90:slow?95:32));
   object result;
   try{result=new{ok=true,value=await runtime.Handle(request,operation.Token)};}
   catch(OperationCanceledException e)when(!stop.IsCancellationRequested){Program.Log("request timed out: "+(type.ValueKind==JsonValueKind.String?type.GetString():"?"),e);result=new{ok=false,error="操作超时，请检查网络后重试；已激活的授权可直接恢复"};}
   catch(Exception e)when(e is not OperationCanceledException){result=new{ok=false,error=e.Message};}
   // The operation deadline may have elapsed; use a fresh, bounded write to return its error.
   using var write=CancellationTokenSource.CreateLinkedTokenSource(stop);write.CancelAfter(TimeSpan.FromSeconds(3));
   await PipeProtocol.WriteAsync(pipe,result,write.Token);
  }
  catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or InvalidDataException or System.ComponentModel.Win32Exception)
  {
   // Request contents and credentials are never logged; only why a connection was refused.
   if(e is not OperationCanceledException)Program.Log("pipe connection refused",new Exception(e.GetType().Name+": "+e.Message));
  }
 }
}

