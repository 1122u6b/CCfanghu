using Microsoft.Win32;
using System.Security.Principal;
using System.Text.Json;
namespace Leyra.Windows.Agent;

/// <summary>"Coexist" mode: the customer keeps their own proxy app (Clash, v2rayN...) running as the system proxy.
/// Leyra's local proxy (127.0.0.1:17890) goes in front of it: the listed sites (Claude) go through the Leyra exit,
/// everything else is handed on to the customer's own proxy. No tunnel and no filtering rules in this mode, so the
/// customer's proxy keeps working and is never asked to quit. Their settings are saved and put back exactly on
/// pause, quit and network recovery.</summary>
sealed class CoexistProxy(SecurityIdentifier owner)
{
 public const int LocalPort=17890;
 [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] public record Upstream(string Type,int Port);
 [System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] record Saved(int? ProxyEnable,string? ProxyServer,string? ProxyOverride,string? AutoConfigURL);
 const string Suffix=@"\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
 static string SavedPath=>Path.Combine(AgentStore.Root,"proxy-before.json");
 RegistryKey Open(bool write)=>Registry.Users.OpenSubKey(owner.Value+Suffix,write)??throw new InvalidOperationException("当前桌面用户的网络设置不可用");
 public static bool Taken=>File.Exists(SavedPath);
 static Upstream? Parse(int? enable,string? server)
 {
  if(enable is not 1||string.IsNullOrWhiteSpace(server))return null;
  Upstream? socks=null;
  foreach(var part in server.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
  {
   var scheme=part.Contains('=')?part[..part.IndexOf('=')].ToLowerInvariant():"";var value=part.Contains('=')?part[(part.IndexOf('=')+1)..]:part;
   var colon=value.LastIndexOf(':');if(colon<0||!int.TryParse(value[(colon+1)..],out var port)||port is <1 or >65535||port==LocalPort)continue;
   var host=value[..colon].Trim('[',']');if(host is not ("127.0.0.1" or "localhost" or "::1"))continue;
   if(scheme is "" or "http" or "https")return new("http",port);
   if(scheme=="socks")socks=new("socks5",port);
  }
  return socks;
 }
 /// <summary>A proxy auto-configuration script served on this computer (Clash's "PAC mode" and the like): the local
 /// proxy it names. Scripts elsewhere are not fetched (they are not the customer's own app).</summary>
 internal static Upstream? FromPac(string? url)
 {
  if(string.IsNullOrWhiteSpace(url)||!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme is not ("http" or "https")||u.Host is not ("127.0.0.1" or "localhost" or "[::1]"))return null;
  try
  {
   using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(3),MaxResponseContentBufferSize=1024*1024};
   return FromPacScript(http.GetStringAsync(u).GetAwaiter().GetResult());
  }
  catch(Exception e)when(e is HttpRequestException or TaskCanceledException or InvalidOperationException){return null;}
 }
 internal static Upstream? FromPacScript(string script)
 {
  // "PROXY 127.0.0.1:7890", "SOCKS5 127.0.0.1:7891", "HTTPS localhost:7890"; HTTP proxies are preferred.
  Upstream? socks=null;
  foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(script,@"\b(PROXY|HTTPS|HTTP|SOCKS5|SOCKS4|SOCKS)\s+(127\.0\.0\.1|localhost):(\d{1,5})\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase))
  {
   if(!int.TryParse(m.Groups[3].Value,out var port)||port is <1 or >65535||port==LocalPort)continue;
   if(m.Groups[1].Value.StartsWith("SOCKS",StringComparison.OrdinalIgnoreCase)){socks??=new("socks5",port);continue;}
   return new("http",port);
  }
  return socks;
 }
 /// <summary>The customer's own local proxy: from the saved settings while Leyra is in front, else from the system.</summary>
 public Upstream? Find()
 {
  try
  {
   if(Taken){var s=JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(SavedPath));return s is null?null:Parse(s.ProxyEnable,s.ProxyServer)??FromPac(s.AutoConfigURL);}
   using var key=Open(false);return Parse(key.GetValue("ProxyEnable") as int?,key.GetValue("ProxyServer") as string)??FromPac(key.GetValue("AutoConfigURL") as string);
  }
  catch(Exception e)when(e is InvalidOperationException or JsonException or IOException){return null;}
 }
 public void Take()
 {
  using var key=Open(true);
  if(!Taken)AgentStore.Atomic(SavedPath,JsonSerializer.SerializeToUtf8Bytes(new Saved(key.GetValue("ProxyEnable") as int?,key.GetValue("ProxyServer") as string,key.GetValue("ProxyOverride") as string,key.GetValue("AutoConfigURL") as string)));
  key.SetValue("ProxyEnable",1,RegistryValueKind.DWord);key.SetValue("ProxyServer","127.0.0.1:"+LocalPort,RegistryValueKind.String);key.DeleteValue("AutoConfigURL",false);
  if(key.GetValue("ProxyOverride") is not string)key.SetValue("ProxyOverride","<local>",RegistryValueKind.String);
 }
 /// <summary>True while the system proxy still points at Leyra (the customer's app may set it back to itself).</summary>
 public bool Holding()
 {
  try{using var key=Open(false);return key.GetValue("ProxyEnable") is 1&&key.GetValue("ProxyServer") as string=="127.0.0.1:"+LocalPort&&key.GetValue("AutoConfigURL") is null;}
  catch(InvalidOperationException){return false;}
 }
 public void Restore()
 {
  if(!Taken)return;
  try
  {
   var s=JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(SavedPath));
   using var key=Open(true);
   void Put(string name,object? value,RegistryValueKind kind){if(value is null)key.DeleteValue(name,false);else key.SetValue(name,value,kind);}
   Put("ProxyEnable",s?.ProxyEnable,RegistryValueKind.DWord);Put("ProxyServer",s?.ProxyServer,RegistryValueKind.String);
   Put("ProxyOverride",s?.ProxyOverride,RegistryValueKind.String);Put("AutoConfigURL",s?.AutoConfigURL,RegistryValueKind.String);
   File.Delete(SavedPath);
  }
  catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException){Program.Log("restoring proxy settings",e);}
 }
}
