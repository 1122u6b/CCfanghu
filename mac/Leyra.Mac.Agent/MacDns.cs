using System.Diagnostics;
using System.Text.Json;
namespace Leyra.Mac.Agent;

/// <summary>macOS sends name lookups straight to each network service's DNS servers, around the tunnel (Windows
/// sends them into the tunnel). While protected, every service's DNS points to the kernel on 127.0.0.1, which
/// answers through the private resolver. The previous settings are kept and put back exactly on pause, quit and
/// network recovery.</summary>
static class MacDns
{
 static string Saved=>Path.Combine(AgentStore.Root,"dns-before.json");
 static string Run(params string[] args)
 {
  var start=new ProcessStartInfo("/usr/sbin/networksetup",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var p=Process.Start(start)??throw new InvalidOperationException("无法修改网络设置");
  var o=p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();p.WaitForExit(10000);return o;
 }
 static IEnumerable<string> Services()=>Run("-listallnetworkservices").Split('\n').Skip(1).Select(x=>x.Trim()).Where(x=>x.Length>0&&!x.StartsWith('*'));
 static string[] Current(string service)
 {
  var o=Run("-getdnsservers",service);
  return o.Contains("aren't any",StringComparison.OrdinalIgnoreCase)?[]:o.Split('\n').Select(x=>x.Trim()).Where(x=>x.Length>0).ToArray();
 }
 internal static void Flush()
 {
  foreach(var (f,a) in new[]{("/usr/bin/dscacheutil",new[]{"-flushcache"}),("/usr/bin/killall",new[]{"-HUP","mDNSResponder"})})
   try{using var p=Process.Start(new ProcessStartInfo(f,a){UseShellExecute=false});p?.WaitForExit(5000);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){}
 }
 public static void Apply()
 {
  // Remember the customer's own settings once (a second Apply must not save 127.0.0.1 as "before").
  if(!File.Exists(Saved))AgentStore.Atomic(Saved,JsonSerializer.SerializeToUtf8Bytes(Services().ToDictionary(s=>s,Current)));
  foreach(var s in Services())Run("-setdnsservers",s,"127.0.0.1");
  Flush();
 }
 public static void Restore()
 {
  if(!File.Exists(Saved))return;
  try
  {
   var before=JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllBytes(Saved))??[];
   foreach(var s in Services())
   {
    var now=Current(s);if(!(now.Length==1&&now[0]=="127.0.0.1"))continue;   // changed by the customer since: leave it
    var old=before.TryGetValue(s,out var v)&&v.Length>0?v:["Empty"];
    Run(["-setdnsservers",s,..old]);
   }
   File.Delete(Saved);Flush();
  }
  catch(Exception e)when(e is IOException or JsonException or InvalidOperationException){Program.Log("restoring DNS settings",e);}
 }
}
