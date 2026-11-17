using System.Diagnostics;
using System.Text.Json;

namespace Leyra.Mac.Agent;

/// <summary>Chrome and Edge on the Mac read policies from /Library/Managed Preferences. Same two policies as on
/// Windows: WebRTC only through the proxy (the real IP is not leaked by web pages), and no QUIC.</summary>
static class BrowserPolicies
{
 static readonly string[] Domains={"/Library/Managed Preferences/com.google.Chrome","/Library/Managed Preferences/com.microsoft.Edge"};
 record Prior(string Domain,string Name,string? Value);
 static string FileName=>Path.Combine(AgentStore.Root,"browser-policies.json");
 static (int Code,string Out) Defaults(params string[] args)
 {
  var start=new ProcessStartInfo("/usr/bin/defaults",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var p=Process.Start(start)??throw new InvalidOperationException("无法设置浏览器");
  var o=p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();p.WaitForExit(10000);return (p.ExitCode,o.Trim());
 }
 public static string? Read(string domain,string name){var r=Defaults("read",domain,name);return r.Code==0?r.Out:null;}
 public static bool Active()=>Domains.All(d=>Read(d,"WebRtcIPHandling")=="disable_non_proxied_udp");
 public static object Install()
 {
  Directory.CreateDirectory("/Library/Managed Preferences");
  if(!File.Exists(FileName))
   AgentStore.Atomic(FileName,JsonSerializer.SerializeToUtf8Bytes(Domains.SelectMany(d=>new[]{"WebRtcIPHandling","QuicAllowed"}.Select(n=>new Prior(d,n,Read(d,n)))).ToList()));
  foreach(var d in Domains)
  {
   if(Defaults("write",d,"WebRtcIPHandling","-string","disable_non_proxied_udp").Code!=0||Defaults("write",d,"QuicAllowed","-bool","false").Code!=0)throw new IOException("无法设置浏览器策略");
   File.SetUnixFileMode(d+".plist",UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.OtherRead);
  }
  return new{saved=true,restart_browser_required=true};
 }
 public static void Restore()
 {
  if(!File.Exists(FileName))return;
  var prior=JsonSerializer.Deserialize<List<Prior>>(File.ReadAllBytes(FileName))??throw new InvalidDataException("浏览器策略恢复记录无效");
  foreach(var item in prior)
  {
   if(!Domains.Contains(item.Domain)||item.Name is not ("WebRtcIPHandling" or "QuicAllowed"))throw new InvalidDataException("浏览器策略恢复项无效");
   var now=Read(item.Domain,item.Name);var ours=item.Name=="QuicAllowed"?now=="0":now=="disable_non_proxied_udp";
   if(!ours)continue;   // changed by someone else since: leave it
   if(item.Value is null)Defaults("delete",item.Domain,item.Name);
   else if(item.Name=="QuicAllowed")Defaults("write",item.Domain,item.Name,"-bool",item.Value=="1"?"true":"false");
   else Defaults("write",item.Domain,item.Name,"-string",item.Value);
  }
  File.Delete(FileName);
 }
}
