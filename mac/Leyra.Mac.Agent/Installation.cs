using System.Diagnostics;
namespace Leyra.Mac.Agent;

/// <summary>Called by the .pkg installer scripts (as root) and by the network recovery tool.</summary>
static class Installation
{
 public const string ServiceName="xin.leyra.agent";
 public const string Plist="/Library/LaunchDaemons/xin.leyra.agent.plist";
 /// <summary>Records which macOS user may talk to the service and prepares the data folder.</summary>
 public static void Install(uint owner)
 {
  if(owner==0)throw new InvalidOperationException("请在普通用户登录时安装");
  AgentStore.Initialize(owner);
  // A reinstall by a different user takes over the window connection.
  AgentStore.Atomic(Path.Combine(AgentStore.Root,"owner.uid"),System.Text.Encoding.UTF8.GetBytes(owner.ToString()));
 }
 /// <summary>Network recovery: removes Leyra's firewall rules, stops the kernel and restores the timezone.</summary>
 public static void Recover()
 {
  using(var guard=new PfGuard())guard.RemoveOwned();
  foreach(var p in Process.GetProcessesByName("mihomo")){try{p.Kill(true);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){}finally{p.Dispose();}}
  try
  {
   var state=Path.Combine(AgentStore.Root,"state.json");
   if(File.Exists(state))
   {
    var s=System.Text.Json.JsonSerializer.Deserialize<AgentSettings>(File.ReadAllBytes(state));
    if(s?.OriginalTimezone is { Length: >0 } tz)SystemClock.SetTimezone(tz,CancellationToken.None).GetAwaiter().GetResult();
   }
  }
  catch(Exception e)when(e is IOException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException){Program.Log("recover timezone",e);}
 }
 public static void CleanupOldVersions(){}
 public static bool AutoStart()=>File.Exists(Plist);
}
