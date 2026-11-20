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
 // The automatic proxy configuration (PAC) of a service: kept so it is put back exactly, and read as the customer's proxy.
 static Setting ReadAuto(string service)
 {
  var o=Run("-getautoproxyurl",service);
  var url=Regex.Match(o,@"^URL:\s*(\S*)",RegexOptions.Multiline).Groups[1].Value;if(url=="(null)")url="";
  return new(service,"autoproxy",Regex.IsMatch(o,@"^Enabled:\s*Yes",RegexOptions.Multiline),url,0);
 }
 static List<Setting> Current()=>Services().SelectMany(s=>Kinds.Select(k=>Read(s,k)).Append(ReadAuto(s))).ToList();
 /// <summary>The local proxy a PAC script served on this Mac names ("PROXY 127.0.0.1:7890", "SOCKS5 127.0.0.1:7891").</summary>
 internal static Upstream? FromPacScript(string script)
 {
  Upstream? socks=null;
  foreach(Match m in Regex.Matches(script,@"\b(PROXY|HTTPS|HTTP|SOCKS5|SOCKS4|SOCKS)\s+(127\.0\.0\.1|localhost):(\d{1,5})\b",RegexOptions.IgnoreCase))
  {
   if(!int.TryParse(m.Groups[3].Value,out var port)||port is <1 or >65535||port==LocalPort)continue;
   if(m.Groups[1].Value.StartsWith("SOCKS",StringComparison.OrdinalIgnoreCase)){socks??=new("socks5",port);continue;}
   return new("http",port);
  }
  return socks;
 }
 static Upstream? FromPac(Setting s)
 {
  if(!s.On||!Uri.TryCreate(s.Server,UriKind.Absolute,out var u)||u.Scheme is not ("http" or "https")||u.Host is not ("127.0.0.1" or "localhost" or "[::1]"))return null;
  try
  {
   using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(3),MaxResponseContentBufferSize=1024*1024};
   return FromPacScript(http.GetStringAsync(u).GetAwaiter().GetResult());
  }
  catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException){return null;}
 }
 static bool Local(Setting s)=>s.Kind!="autoproxy"&&s.On&&s.Port is >0 and <65536&&s.Port!=LocalPort&&s.Server is "127.0.0.1" or "localhost" or "::1";
 public static bool Taken=>File.Exists(Saved);
 /// <summary>The customer's own local proxy: from the saved settings while Leyra is in front, else from the system.</summary>
 public static Upstream? Find()
 {
  var list=Taken?JsonSerializer.Deserialize<List<Setting>>(File.ReadAllBytes(Saved))??[]:Current();
  var http=list.FirstOrDefault(s=>Local(s)&&s.Kind is "webproxy" or "securewebproxy");
  if(http is not null)return new("http",http.Port);
  var socks=list.FirstOrDefault(s=>Local(s)&&s.Kind=="socksfirewallproxy");
  if(socks is not null)return new("socks5",socks.Port);
  // Clash's "PAC mode" and the like: the system proxy is a script on this Mac naming the customer's local proxy.
  foreach(var auto in list.Where(s=>s.Kind=="autoproxy"&&s.On))if(FromPac(auto) is Upstream fromScript)return fromScript;
  return null;
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
 public static bool Holding()
 {
  var now=Current();
  return now.Where(s=>s.Kind is "webproxy" or "securewebproxy").All(s=>s.On&&s.Server=="127.0.0.1"&&s.Port==LocalPort)&&now.Where(s=>s.Kind=="autoproxy").All(s=>!s.On);
 }
 public static void Restore()
 {
  if(!Taken)return;
  try
  {
   var before=JsonSerializer.Deserialize<List<Setting>>(File.ReadAllBytes(Saved))??[];
   foreach(var s in before)
   {
    if(s.Kind=="autoproxy")
    {
     // The customer's automatic proxy configuration (PAC) comes back exactly as it was.
     if(s.Server.Length>0)Run("-setautoproxyurl",s.Service,s.Server);
     Run("-setautoproxystate",s.Service,s.On?"on":"off");continue;
    }
    if(s.Server.Length>0&&s.Port>0)Run("-set"+s.Kind,s.Service,s.Server,s.Port.ToString());
    Run("-set"+s.Kind+"state",s.Service,s.On?"on":"off");
   }
   File.Delete(Saved);
  }
  catch(Exception e)when(e is IOException or JsonException or InvalidOperationException){Program.Log("restoring proxy settings",e);}
 }
}
