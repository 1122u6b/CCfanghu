using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;

[assembly:SupportedOSPlatform("windows")]
namespace Leyra.Windows.Agent;

static class Program
{
 public const string Version=Leyra.Windows.Core.ProductInfo.Version;
 static int Main(string[] args)
 {
  if(!OperatingSystem.IsWindows())return 10;
  try
  {
   if(args.SequenceEqual(new[]{"--service"}))
   {
    if(!WindowsIdentity.GetCurrent().IsSystem)throw new UnauthorizedAccessException("后台服务必须由 Windows 服务管理器以系统身份启动");
    ServiceBase.Run(new AgentService());return 0;
   }
   if(args.SequenceEqual(new[]{"--install"})){Installation.Install();return 0;}
   if(args.SequenceEqual(new[]{"--recover"})){Installation.Recover();return 0;}
   if(args.SequenceEqual(new[]{"--remove-service"})){Installation.Recover();Installation.RemoveService();return 0;}
   Console.Error.WriteLine("Use the installed desktop application or network recovery shortcut.");return 11;
  }
  catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
 }
}

sealed class AgentService:ServiceBase
{
 CancellationTokenSource? stop;Task? task;Runtime? runtime;
 public AgentService(){ServiceName=Installation.ServiceName;CanStop=true;CanShutdown=true;AutoLog=false;}
 protected override void OnStart(string[] args)
 {
  AgentStore.VerifyDataPath();var store=new AgentStore();runtime=new Runtime(store);stop=new CancellationTokenSource();
  task=Task.WhenAll(runtime.Run(stop.Token),new LocalPipe(runtime,store.Owner).Run(stop.Token));
  _=task.ContinueWith(t=>{if(t.IsFaulted)Environment.Exit(2);},TaskScheduler.Default);
  _=Task.Run(async()=>{try{await Task.Delay(TimeSpan.FromMinutes(1),stop.Token);Installation.CleanupOldVersions();}catch(OperationCanceledException){}});
 }
 protected override void OnStop()
 {
  stop?.Cancel();RequestAdditionalTime(20000);
  try{if(task is not null && !task.Wait(TimeSpan.FromSeconds(15)))Environment.Exit(3);}catch(AggregateException){}
  runtime?.Dispose();stop?.Dispose();
 }
 protected override void OnShutdown()=>OnStop();
}
