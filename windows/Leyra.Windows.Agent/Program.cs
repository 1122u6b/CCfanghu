using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;

[assembly:SupportedOSPlatform("windows")]
namespace Leyra.Windows.Agent;

static class Program
{
 public const string Version=Leyra.Windows.Core.ProductInfo.Version;
 /// <summary>Appends a short diagnostic line (type, message, stack) to ProgramData\LeyraConnect\agent-error.log.</summary>
 public static void Log(string what,Exception e)
 {
  try
  {
   var path=Path.Combine(AgentStore.Root,"agent-error.log");
   if(File.Exists(path)&&new FileInfo(path).Length>512*1024)File.Delete(path);
   File.AppendAllText(path,DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+Version+" "+what+": "+e+Environment.NewLine);
  }
  catch(Exception){}
 }
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
   if(args.SequenceEqual(new[]{"--install"})){Installation.Install(false);return 0;}
   // Fresh install (nothing installed before): forget activation data left by an earlier installation.
   if(args.SequenceEqual(new[]{"--install","--reset"})){Installation.Install(true);return 0;}
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
  try{AgentStore.VerifyDataPath();var store=new AgentStore();runtime=new Runtime(store);stop=new CancellationTokenSource();
  task=Task.WhenAll(runtime.Run(stop.Token),new LocalPipe(runtime,store.Owner).Run(stop.Token));}
  catch(Exception e){Program.Log("service start failed",e);throw;}
  _=task.ContinueWith(t=>{if(t.IsFaulted){Program.Log("service stopped unexpectedly",t.Exception!);Environment.Exit(2);}},TaskScheduler.Default);
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
