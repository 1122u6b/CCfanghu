using System.Runtime.InteropServices;
[assembly:System.Runtime.Versioning.SupportedOSPlatform("macos")]
namespace Leyra.Mac.Agent;

static class Program
{
 public const string Version=Leyra.Windows.Core.ProductInfo.Version;
 /// <summary>Appends a short diagnostic line (type, message, stack) to agent-error.log in the data folder.</summary>
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
 [DllImport("libc")]static extern uint geteuid();
 static int Main(string[] args)
 {
  if(!OperatingSystem.IsMacOS())return 10;
  try
  {
   // launchd starts this as root (/Library/LaunchDaemons/xin.leyra.agent.plist).
   if(args.SequenceEqual(new[]{"--service"}))
   {
    if(geteuid()!=0)throw new UnauthorizedAccessException("后台服务必须由系统以管理员身份启动");
    return RunService();
   }
   // Called by the installer (as root): records who may talk to the service and sets up the data folder.
   if(args.Length==2&&args[0]=="--install"&&uint.TryParse(args[1],out var owner)){Installation.Install(owner);return 0;}
   if(args.SequenceEqual(new[]{"--recover"})){Installation.Recover();return 0;}
   Console.Error.WriteLine("Use the installed Leyra application.");return 11;
  }
  catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
 }
 static int RunService()
 {
  AgentStore.VerifyDataPath();var store=new AgentStore();using var runtime=new Runtime(store);using var stop=new CancellationTokenSource();
  PosixSignalRegistration.Create(PosixSignal.SIGTERM,c=>{c.Cancel=true;stop.Cancel();});
  PosixSignalRegistration.Create(PosixSignal.SIGINT,c=>{c.Cancel=true;stop.Cancel();});
  var protection=runtime.Run(stop.Token);var listener=new LocalSocket(runtime,store.Owner).Run(stop.Token);
  // Either part ending on its own is a fault: record why and exit, launchd starts the service again.
  foreach(var (part,name) in new[]{(protection,"protection loop"),(listener,"window connection listener")})
   _=part.ContinueWith(t=>{if(stop.IsCancellationRequested)return;Program.Log(name+" stopped unexpectedly",t.Exception??new Exception("ended without error"));Environment.Exit(2);},TaskScheduler.Default);
  try{Task.WhenAll(protection,listener).Wait();}catch(AggregateException)when(stop.IsCancellationRequested){}
  return 0;
 }
}
