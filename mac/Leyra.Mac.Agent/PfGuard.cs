using System.Diagnostics;
using System.Net;
using Leyra.Windows.Core;

namespace Leyra.Mac.Agent;

/// <summary>Where the kernel may connect: the gateway's address and port (TCP).</summary>
sealed record GatewayEndpoint(IPEndPoint Endpoint,byte Protocol);

/// <summary>The Mac counterpart of the Windows filtering rules, as pf rules in Leyra's own anchor.</summary>
static class PfPlan
{
 public const string Marker="block drop out quick all label \"leyra-default-block\"";
 public static string Create(IReadOnlyList<GatewayEndpoint> gateways,string tunnel,IReadOnlyList<DirectRule> direct,bool kernelDirect)
 {
  if(gateways.Count is 0 or >64||direct.Count>64)throw new InvalidDataException("过滤例外超过上限");
  var r=new List<string>
  {
   "pass quick on lo0 all",
   "pass quick on "+tunnel+" all",                                  // the confirmed tunnel
   "pass out quick inet proto udp from any port 68 to any port 67",  // DHCP
   "block drop out quick proto { tcp udp } to any port 53",           // no plain DNS on the physical network
  };
  foreach(var n in new[]{"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","169.254.0.0/16","224.0.0.0/4"})r.Add("pass out quick inet to "+n);   // local network, Bonjour
  foreach(var n in new[]{"fe80::/10","fc00::/7","ff00::/8"})r.Add("pass out quick inet6 to "+n);
  foreach(var g in gateways)
   r.Add("pass out quick "+(g.Endpoint.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?"inet6":"inet")+" proto "+(g.Protocol==17?"udp":"tcp")+" to "+g.Endpoint.Address+" port "+g.Endpoint.Port);
  foreach(var d in direct){var item=DirectRule.Parse(d.Cidr,d.Note,d.Public);r.Add("pass out quick inet to "+item.Cidr);}
  // "Only listed sites go through the exit": pf cannot tell programs apart, so the physical network is open;
  // new connections still enter the tunnel through the system routes and the kernel decides per site.
  if(kernelDirect)r.Add("pass out quick all");
  r.Add(Marker);
  return string.Join("\n",r)+"\n";
 }
}

/// <summary>Loads and removes Leyra's pf rules. Uses an anchor under com.apple/ (evaluated by the stock
/// /etc/pf.conf) and a pf enable reference, so other users of pf are left alone.</summary>
sealed class PfGuard:IDisposable
{
 public const string Anchor="com.apple/250.LeyraConnect";
 string? token;
 static (int Code,string Output) Pfctl(params string[] args)
 {
  var start=new ProcessStartInfo("/sbin/pfctl",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var p=Process.Start(start)??throw new InvalidOperationException("无法启动系统防火墙");
  var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();
  if(!p.WaitForExit(15000)){p.Kill();throw new TimeoutException("系统防火墙没有响应");}
  return (p.ExitCode,o.Result+e.Result);
 }
 public void Apply(string rules)
 {
  var file=Path.Combine(AgentStore.Root,"pf-rules.conf");AgentStore.Atomic(file,System.Text.Encoding.UTF8.GetBytes(rules));
  var load=Pfctl("-a",Anchor,"-f",file);
  if(load.Code!=0)throw new InvalidOperationException("系统防火墙拒绝了防护规则："+load.Output.Trim().Split('\n').Last());
  if(token is null)
  {
   var en=Pfctl("-E");
   var m=System.Text.RegularExpressions.Regex.Match(en.Output,@"Token\s*:\s*(\d+)");
   if(en.Code!=0&&!en.Output.Contains("already enabled"))throw new InvalidOperationException("无法开启系统防火墙");
   token=m.Success?m.Groups[1].Value:"";
  }
  if(!DefaultGuardsInstalled())throw new InvalidOperationException("防护规则没有生效");
 }
 public bool DefaultGuardsInstalled()
 {
  try
  {
   var info=Pfctl("-s","info");if(!info.Output.Contains("Status: Enabled"))return false;
   var rules=Pfctl("-a",Anchor,"-s","rules");return rules.Code==0&&rules.Output.Contains("leyra-default-block");
  }
  catch(Exception e)when(e is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception){return false;}
 }
 /// <summary>Firewall rules and the DNS change only; the coexist system proxy stays as it is.</summary>
 public void RemoveRules()
 {
  MacDns.Restore();
  try{Pfctl("-a",Anchor,"-F","rules");}catch(Exception e)when(e is InvalidOperationException or TimeoutException){Program.Log("removing pf rules",e);}
  if(!string.IsNullOrEmpty(token)){try{Pfctl("-X",token);}catch(Exception e)when(e is InvalidOperationException or TimeoutException){}}
  token=null;
 }
 public void RemoveOwned()
 {
  MacDns.Restore();   // protection off: the Mac's own DNS settings come back with the open network
  CoexistProxy.Restore();   // and the customer's own system proxy (coexist mode)
  try{Pfctl("-a",Anchor,"-F","rules");}catch(Exception e)when(e is InvalidOperationException or TimeoutException){Program.Log("removing pf rules",e);}
  if(!string.IsNullOrEmpty(token)){try{Pfctl("-X",token);}catch(Exception e)when(e is InvalidOperationException or TimeoutException){}}
  token=null;
 }
 public void Dispose(){}
}
