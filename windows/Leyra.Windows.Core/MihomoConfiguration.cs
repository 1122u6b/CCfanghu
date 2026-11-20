using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Leyra.Windows.Core;
[System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] public sealed record PrivateDns(string Zone,Uri Resolver,string Address)
{
 public static PrivateDns Parse(JsonElement value,ServerProfile server)
 {
  var zone=value.GetProperty("zone").GetString() ?? "";var address=value.GetProperty("resolver_ip").GetString() ?? "";var input=value.GetProperty("resolver_url").GetString() ?? "";
  if(!zone.StartsWith("probe.",StringComparison.Ordinal) || zone.Length>253 || !Regex.IsMatch(zone,"^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$",RegexOptions.CultureInvariant) || zone.Contains(".."))throw new InvalidDataException("私有 DNS 区域无效");
  var host="api."+zone[6..];
  if(!Uri.TryCreate(input,UriKind.Absolute,out var url) || url.Scheme!="https" || url.Host!=host || url.Port!=443 || url.UserInfo.Length!=0 || url.Query.Length!=0 || url.Fragment.Length!=0 || !Regex.IsMatch(url.AbsolutePath,"^/private-dns/[A-Za-z0-9_.-]{32,256}$",RegexOptions.CultureInvariant))throw new InvalidDataException("私有 DNS 地址与自有区域不符");
  if(server.Role=="device" && url.AbsolutePath!="/private-dns/"+server.Token)throw new InvalidDataException("私有 DNS 凭据必须属于当前设备");
  if(!AddressPolicy.IsLiteral(address) || !IPAddress.Parse(address).Equals(IPAddress.Parse(server.Host)))throw new InvalidDataException("私有 DNS 引导地址必须与自有服务器一致");
  return new(zone,url,address);
 }
}
public static class MihomoConfiguration
{
 static readonly HashSet<string> Types=["http","socks5","ss","vmess","vless","trojan","hysteria2","tuic"];
 static readonly HashSet<string> Forbidden=["dialer-proxy","proxy","certificate","private-key","ca","ca-str","plugin","plugin-opts","smux"];
 public static void ValidateNodes(JsonElement nodes)
 {
  if(nodes.ValueKind!=JsonValueKind.Array || nodes.GetArrayLength() is <1 or >256)throw new InvalidDataException("节点数量无效");
  void Inspect(JsonElement e)
  {
   if(e.ValueKind==JsonValueKind.Object)foreach(var p in e.EnumerateObject()){if(Forbidden.Contains(p.Name))throw new InvalidDataException("节点不能引用本地文件或其他代理链");Inspect(p.Value);}
   else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())Inspect(v);
  }
  foreach(var node in nodes.EnumerateArray())
  {
   if(node.ValueKind!=JsonValueKind.Object || !node.TryGetProperty("type",out var type) || !Types.Contains(type.GetString()??"") || !node.TryGetProperty("server",out var host) || !AddressPolicy.IsLiteral(host.GetString()??"") || !node.TryGetProperty("port",out var port) || !port.TryGetInt32(out var number) || number is <1 or >65535)throw new InvalidDataException("节点需提供受支持的协议、明确 IP 与有效端口");
   Inspect(node);
  }
 }
 /// <param name="mux">The gateway said it accepts multiplexing ("yamux"): each TLS gateway node becomes a fallback
 /// group named like the node, first a multiplexed copy (one round trip per new connection instead of 5-6),
 /// then the plain copy. The kernel checks both every 10 s with a 3 s limit, so a multiplexed connection that
 /// silently died is left within about 13 s and new connections go the plain way until it works again.</param>
 public static readonly string[] DefaultProxyDomains=["claude.ai","claude.com","anthropic.com","claudeusercontent.com","challenges.cloudflare.com"];
 static readonly System.Text.RegularExpressions.Regex DomainPattern=new("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$",System.Text.RegularExpressions.RegexOptions.CultureInvariant);
 /// <summary>Checked, lower-case domains for "only these sites go through the exit"; throws on anything else.</summary>
 public static IReadOnlyList<string> ProxyDomains(IEnumerable<string> input)
 {
  var list=input.Select(x=>(x??"").Trim().TrimStart('.').ToLowerInvariant()).Where(x=>x.Length>0).Select(x=>Cidr(x) ?? x).Distinct().ToList();
  if(list.Count is <1 or >64)throw new InvalidDataException("走出口的网站要有 1 到 64 个");
  foreach(var d in list)if(!DomainPattern.IsMatch(d)&&Cidr(d) is null)throw new InvalidDataException("网址或 IP 格式不对："+d);
  return list;
 }
 /// <summary>IP check sites test from other addresses than their own page (Cloudflare trace on 1.1.1.1, Chinese IP
 /// sites, DNS leak hosts). A customer who lists the check site expects every row on it to show the exit, so its
 /// helper addresses come along automatically.</summary>
 static readonly Dictionary<string,string[]> Companions=new()
 {
  ["ip.net.coffee"]=["1.1.1.1/32","ip138.com","ip.cn","ipleak.net"],
 };
 /// <summary>The customer's list plus the helper addresses of any check site on it.</summary>
 public static IReadOnlyList<string> Expand(IEnumerable<string> list)
 {
  var all=list.ToList();
  foreach(var (site,extra) in Companions)if(all.Any(d=>d==site||d.EndsWith("."+site,StringComparison.Ordinal)))all.AddRange(extra);
  return all.Distinct().ToList();
 }
 /// <summary>A list line that is an IP address or range ("1.1.1.1", "1.1.1.0/24", IPv6 too), normalised to
 /// "address/prefix"; null for a site name. Pages often check addresses directly (e.g. https://1.1.1.1/cdn-cgi/trace),
 /// which name-based rules cannot catch.</summary>
 public static string? Cidr(string line)
 {
  var slash=line.IndexOf('/');var host=slash<0?line:line[..slash];
  if(!System.Net.IPAddress.TryParse(host,out var ip)||!AddressPolicy.IsLiteral(host))return null;
  var max=ip.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6?128:32;
  if(slash<0)return ip+"/"+max;
  return int.TryParse(line[(slash+1)..],out var prefix)&&prefix>=8&&prefix<=max?System.Net.IPNetwork.Parse(ip+"/"+prefix).ToString():null;
 }
 /// <summary>Kernel rule for one list line (site name: it and its subdomains; IP/range: those addresses).</summary>
 public static string Rule(string line,string target)
 {
  var cidr=Cidr(line);
  return cidr is null?"DOMAIN-SUFFIX,"+line+","+target:(cidr.Contains(':')?"IP-CIDR6,":"IP-CIDR,")+cidr+","+target+",no-resolve";
 }
 /// <param name="proxyOnly">Null: everything goes through the exit (default). A list: only those sites (and their
 /// subdomains) go through the exit, everything else connects directly from this computer. This software's own
 /// checks (exit IP, DNS check, speed test) always go through the exit.</param>
 public static byte[] Build(JsonElement nodes,string controllerSecret,bool protectedMode,PrivateDns? dns,IReadOnlyList<DirectRule> direct,bool mux=false,IReadOnlyList<string>? proxyOnly=null)
 {
  if(proxyOnly is not null)proxyOnly=Expand(ProxyDomains(proxyOnly));
  ValidateNodes(nodes);
  if(!Guid.TryParse(controllerSecret,out _))throw new InvalidDataException("控制接口密钥无效");
  if(protectedMode && dns is null)throw new InvalidDataException("完整防护需要已核对的私有 DNS 配置");
  if(direct.Count>64 || direct.Any(x=>DirectRule.Parse(x.Cidr,x.Note,x.Public)!=x))throw new InvalidDataException("直连规则无效");
  var names=new HashSet<string>();var list=new JsonArray();var muxGroups=new List<object>();var endpoints=new HashSet<string>();var labelList=new List<string>();
  foreach(var node in nodes.EnumerateArray())
  {
   var copy=JsonNode.Parse(node.GetRawText())!.AsObject();var label=copy["name"]?.GetValue<string>()??"自有节点";
   if(label.Length>160 || label.Any(char.IsControl))throw new InvalidDataException("节点名称无效");
   if(new[]{"出口","自动选择","DIRECT","REJECT","GLOBAL"}.Contains(label)||!names.Add(label)){label+=" "+(list.Count+1);while(!names.Add(label))label+="_";}
   labelList.Add(label);
   if(mux && copy["type"] is JsonValue kind && kind.TryGetValue<string>(out var k) && k=="socks5" && copy["tls"] is JsonValue tls && tls.TryGetValue<bool>(out var secure) && secure)
   {
    string Unique(string n){while(!names.Add(n))n+="_";return n;}
    var fast=copy.DeepClone().AsObject();var plain=copy;
    fast["name"]=Unique(label+" · 复用");plain["name"]=Unique(label+" · 标准");
    fast["smux"]=new JsonObject{["enabled"]=true,["protocol"]="yamux",["max-connections"]=4,["min-streams"]=4,["only-tcp"]=true};
    list.Add(fast);list.Add(plain);
    muxGroups.Add(new Dictionary<string,object>{{"name",label},{"type","fallback"},{"proxies",new[]{fast["name"]!.GetValue<string>(),plain["name"]!.GetValue<string>()}},{"url","https://www.gstatic.com/generate_204"},{"interval",10},{"timeout",3000},{"lazy",false}});
   }
   else{copy["name"]=label;list.Add(copy);}
   var ip=node.GetProperty("server").GetString()!;endpoints.Add(ip+(ip.Contains(':')?"/128":"/32"));
  }
  var groups=new object[]{new Dictionary<string,object>{{"name","出口"},{"type","select"},{"proxies",new[]{"自动选择"}.Concat(labelList).ToArray()}},new Dictionary<string,object>{{"name","自动选择"},{"type","url-test"},{"proxies",labelList},{"url","https://www.gstatic.com/generate_204"},{"interval",300},{"tolerance",100}}}.Concat(muxGroups).ToArray();
  var config=new Dictionary<string,object>{{"mixed-port",17890},{"allow-lan",false},{"bind-address","127.0.0.1"},{"mode","rule"},{"log-level","warning"},{"external-controller","127.0.0.1:19090"},{"secret",controllerSecret},{"ipv6",false},{"proxies",list},{"proxy-groups",groups},{"rules",proxyOnly is null?RoutingPolicy.Rules(direct):ListedRules(endpoints,direct,proxyOnly,dns)},{"profile",new Dictionary<string,object>{{"store-selected",true},{"store-fake-ip",true}}}};
  config["tun"]=new Dictionary<string,object>{{"enable",protectedMode},{"device","LeyraConnect"},{"stack","gvisor"},{"auto-route",true},{"auto-detect-interface",true},{"dns-hijack",new[]{"any:53","tcp://any:53"}},{"route-exclude-address",new[]{"127.0.0.0/8","::1/128"}.Concat(direct.Select(x=>x.Cidr)).Concat(endpoints).ToArray()}};
  if(protectedMode && dns is not null)
  {
   config["hosts"]=new Dictionary<string,string>{{dns.Resolver.Host,dns.Address}};
   var dnsConfig=new Dictionary<string,object>{{"enable",true},{"listen","127.0.0.1:53"},{"ipv6",false},{"enhanced-mode","fake-ip"},{"fake-ip-range","198.18.0.1/16"},{"default-nameserver",new[]{dns.Address}},{"nameserver",new[]{dns.Resolver.AbsoluteUri+"#出口"}},{"proxy-server-nameserver",new[]{dns.Resolver.AbsoluteUri}},{"respect-rules",true},{"fake-ip-filter",new[]{"+."+dns.Zone}}};
   if(proxyOnly is not null)
   {
    // Sites going direct are looked up over encrypted public DNS (fastest answer of Alibaba and Cloudflare);
    // the listed sites and this software's own check names still use the private resolver through the exit.
    dnsConfig["nameserver"]=new[]{"https://223.5.5.5/dns-query","https://1.1.1.1/dns-query"};
    var policy=new Dictionary<string,object>();
    foreach(var d in ClaudeAlways.Concat(proxyOnly).Concat(SelfChecks).Append(dns.Zone).Distinct().Where(d=>Cidr(d) is null))policy["+."+d]=new[]{dns.Resolver.AbsoluteUri+"#出口"};
    dnsConfig["nameserver-policy"]=policy;
   }
   config["dns"]=dnsConfig;
  }
  else config["dns"]=new {enable=false};
  return JsonSerializer.SerializeToUtf8Bytes(config,new JsonSerializerOptions{WriteIndented=true});
 }
 /// <summary>Claude's web and desktop app (sign-in check, feature flags, error reports included) always go through
 /// the exit in list and coexist modes, whatever the customer's list says: a list edit can never move Claude off it.</summary>
 public static readonly string[] ClaudeAlways=["claude.ai","claude.com","anthropic.com","claudeusercontent.com","challenges.cloudflare.com",
  "statsig.com","statsigapi.net","featuregates.org","sentry.io","intercom.io","intercomcdn.com","segment.io","segment.com",
  "http-intake.logs.us5.datadoghq.com"];
 // The browser check reads the browser's address from ipify: it must see the exit in every mode.
 static readonly string[] SelfChecks=["speed.cloudflare.com","gstatic.com","ipify.org"];
 static string[] ListedRules(IEnumerable<string> gateways,IReadOnlyList<DirectRule> direct,IReadOnlyList<string> proxyOnly,PrivateDns? dns)
 {
  var rules=new List<string>();
  // This software's own checks reach the server or the speed test through the exit in every mode.
  rules.AddRange(gateways.Select(g=>"IP-CIDR"+(g.Contains(':')?"6":"")+","+g+",出口,no-resolve"));
  rules.AddRange(SelfChecks.Select(d=>"DOMAIN-SUFFIX,"+d+",出口"));
  if(dns is not null)rules.Add("DOMAIN-SUFFIX,"+dns.Zone+",出口");
  rules.AddRange(ClaudeAlways.Concat(proxyOnly).Distinct().Select(d=>Rule(d,"出口")));
  rules.AddRange(RoutingPolicy.Rules(direct).Where(r=>!r.StartsWith("MATCH,",StringComparison.Ordinal)));
  rules.Add("MATCH,DIRECT");
  return rules.ToArray();
 }
}
