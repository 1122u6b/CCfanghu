using System.Diagnostics;
using System.Text.Json;
using Leyra.Windows.Core;
namespace Leyra.Windows.Agent;
sealed class UpdateManager
{
 int active;volatile string? ready;SignedUpdate? manifest;string? path;public string? Ready=>ready;
 public string Error{get;private set;}="";
 public void Check(ServerProfile profile,CancellationToken stop)
 {
  if(profile.UpdateKey is null)throw new InvalidOperationException("请重新导入服务器连接文件，以登记可信更新公钥");
  if(Interlocked.CompareExchange(ref active,1,0)!=0)return;
  _=Task.Run(async()=>
  {
   string? temporary=null;
   try
   {
    Error="";using var client=new PrivateServerClient(profile,TimeSpan.FromSeconds(30));var envelope=await client.RpcAsync(new{type="update_manifest"},stop);
    if(!envelope.GetProperty("available").GetBoolean())return;
    var update=SignedUpdate.Verify(envelope,profile.UpdateKey);if(!update.IsNewer(Program.Version))return;
    var directory=Path.Combine(AgentStore.Root,"updates");Directory.CreateDirectory(directory);temporary=Path.Combine(directory,Guid.NewGuid().ToString("N")+".exe");
    await client.DownloadUpdateAsync(temporary,update.Size,stop);if(!update.Matches(temporary))throw new InvalidDataException("更新包校验失败");
    manifest=update;path=temporary;temporary=null;ready=update.Version;
   }
   catch(Exception error){Error=error is OperationCanceledException?"更新下载已取消":error.Message;}
   finally{if(temporary is not null)File.Delete(temporary);Interlocked.Exchange(ref active,0);}
  },CancellationToken.None);
 }
 public object Install()
 {
  if(ready is null||manifest is null||path is null||!manifest.Matches(path))throw new InvalidOperationException("没有已验证的更新安装包");
  var installer=path;_ =Task.Run(async()=>{await Task.Delay(1500);Process.Start(new ProcessStartInfo(installer){UseShellExecute=false,Arguments="/S"});});
  return new{installing=true};
 }
}
