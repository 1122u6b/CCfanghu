using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Leyra.Mac.Agent;

/// <summary>"Coexist" mode on the Mac: the customer keeps their own proxy app (ClashX, Clash Verge, Surge...)
/// running as the system proxy. Leyra puts its local proxy (127.0.0.1:17890) in front: the listed sites (Claude)
/// go through the Leyra exit, everything else is handed on to the customer's own proxy. No tunnel and no
/// firewall rules in this mode, so the customer's proxy keeps working. The customer's settings are saved and put
/// back exactly on pause, quit and network recovery.</summary>
static class CoexistProxy
{
 public const int LocalPort=17890;
 record Setting(string Service,string Kind,bool On,string Server,int Port);
 public record Upstream(string Type,int Port);
 static string Saved=>Path.Combine(AgentStore.Root,"proxy-before.json");
 static readonly string[] Kinds=["webproxy","securewebproxy","socksfirewallproxy"];
 static string Run(params string[] args)
 {
  var start=new ProcessStartInfo("/usr/sbin/networksetup",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var p=Process.Start(start)??throw new InvalidOperationException("无法读取网络设置");
  var o=p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();p.WaitForExit(10000);return o;
 }
 static IEnumerable<string> Services()=>Run("-listallnetworkservices").Split('\n').Skip(1).Select(x=>x.Trim()).Where(x=>x.Length>0&&!x.StartsWith('*'));
 static Setting Read(string service,string kind)
 {
  var o=Run("-get"+kind,service);
  int.TryParse(Regex.Match(o,@"^Port:\s*(\d+)",RegexOptions.Multiline).Groups[1].Value,out var port);
  return new(service,kind,Regex.IsMatch(o,@"^Enabled:\s*Yes",RegexOptions.Multiline),Regex.Match(o,@"^Server:\s*(\S*)",RegexOptions.Multiline).Groups[1].Value,port);
 }
 static List<Setting> Current()=>Services().SelectMany(s=>Kinds.Select(k=>Read(s,k))).ToList();
 static bool Local(Setting s)=>s.On&&s.Port is >0 and <65536&&s.Port!=LocalPort&&s.Server is "127.0.0.1" or "localhost" or "::1";
 public static bool Taken=>File.Exists(Saved);
 /// <summary>The customer's own local proxy: from the saved settings while Leyra is in front, else from the system.</summary>
 public static Upstream? Find()
 {
  var list=Taken?JsonSerializer.Deserialize<List<Setting>>(File.ReadAllBytes(Saved))??[]:Current();
  var http=list.FirstOrDefault(s=>Local(s)&&s.Kind!="socksfirewallproxy");
  if(http is not null)return new("http",http.Port);
  var socks=list.FirstOrDefault(s=>Local(s)&&s.Kind=="socksfirewallproxy");
  return socks is null?null:new("socks5",socks.Port);
 }
 /// <summary>Leyra's local proxy becomes the system proxy (HTTP, HTTPS and SOCKS) on every network service.</summary>
 public static void Take()
 {
  if(!Taken)AgentStore.Atomic(Saved,JsonSerializer.SerializeToUtf8Bytes(Current()));
  foreach(var s in Services())
  {
   foreach(var k in Kinds)Run("-set"+k,s,"127.0.0.1",LocalPort.ToString());
   Run("-setautoproxystate",s,"off");
  }
 }
 /// <summary>True when every network service still points at Leyra (another app may have changed it).</summary>
 public static bool Holding()=>Current().Where(s=>s.Kind!="socksfirewallproxy").All(s=>s.On&&s.Server=="127.0.0.1"&&s.Port==LocalPort);
 public static void Restore()
 {
  if(!Taken)return;
  try
  {
   var before=JsonSerializer.Deserialize<List<Setting>>(File.ReadAllBytes(Saved))??[];
   foreach(var s in before)
   {
    if(s.Server.Length>0&&s.Port>0)Run("-set"+s.Kind,s.Service,s.Server,s.Port.ToString());
    Run("-set"+s.Kind+"state",s.Service,s.On?"on":"off");
   }
   File.Delete(Saved);
  }
  catch(Exception e)when(e is IOException or JsonException or InvalidOperationException){Program.Log("restoring proxy settings",e);}
 }
}
