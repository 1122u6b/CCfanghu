using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Leyra.Windows.Core;
public sealed record PrivateDns(string Zone,Uri Resolver,string Address)
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
 public static byte[] Build(JsonElement nodes,string controllerSecret,bool protectedMode,PrivateDns? dns,IReadOnlyList<DirectRule> direct,bool mux=false)
 {
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
  var config=new Dictionary<string,object>{{"mixed-port",17890},{"allow-lan",false},{"bind-address","127.0.0.1"},{"mode","rule"},{"log-level","warning"},{"external-controller","127.0.0.1:19090"},{"secret",controllerSecret},{"ipv6",false},{"proxies",list},{"proxy-groups",groups},{"rules",RoutingPolicy.Rules(direct)},{"profile",new Dictionary<string,object>{{"store-selected",true},{"store-fake-ip",true}}}};
  config["tun"]=new Dictionary<string,object>{{"enable",protectedMode},{"device","LeyraConnect"},{"stack","gvisor"},{"auto-route",true},{"auto-detect-interface",true},{"dns-hijack",new[]{"any:53","tcp://any:53"}},{"route-exclude-address",new[]{"127.0.0.0/8","::1/128"}.Concat(direct.Select(x=>x.Cidr)).Concat(endpoints).ToArray()}};
  if(protectedMode && dns is not null)
  {
   config["hosts"]=new Dictionary<string,string>{{dns.Resolver.Host,dns.Address}};
   config["dns"]=new Dictionary<string,object>{{"enable",true},{"listen","127.0.0.1:53"},{"ipv6",false},{"enhanced-mode","fake-ip"},{"fake-ip-range","198.18.0.1/16"},{"default-nameserver",new[]{dns.Address}},{"nameserver",new[]{dns.Resolver.AbsoluteUri+"#出口"}},{"proxy-server-nameserver",new[]{dns.Resolver.AbsoluteUri}},{"respect-rules",true},{"fake-ip-filter",new[]{"+."+dns.Zone}}};
  }
  else config["dns"]=new {enable=false};
  return JsonSerializer.SerializeToUtf8Bytes(config,new JsonSerializerOptions{WriteIndented=true});
 }
}

