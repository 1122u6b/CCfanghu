using System.Net;
using Leyra.Windows.Core;
namespace Leyra.Windows.Platform;
[System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] public sealed record GatewayEndpoint(IPEndPoint Endpoint,byte Protocol);
[System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] public sealed record FilterSpec(bool Ipv6,int Slot,string Name,bool Permit,ulong Weight,string? Network=null,int? RemotePort=null,int? LocalPort=null,byte? Protocol=null,bool Loopback=false,ulong? Interface=null,string? Application=null,bool Transport=false);
[System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] public sealed record WfpPlan(IReadOnlyList<FilterSpec> Filters)
{
 /// <param name="kernelDirect">"Only listed sites go through the exit": the kernel connects the other sites
 /// directly, so it alone may use the physical network. Other programs stay blocked as always.</param>
 public static WfpPlan Create(string kernel,string service,IReadOnlyList<GatewayEndpoint> gateways,IReadOnlyList<IPEndPoint> management,ulong tunnel,IReadOnlyList<DirectRule> direct,bool kernelDirect=false)
 {
  if(gateways.Count>64 || management.Count>64 || direct.Count>64)throw new InvalidDataException("过滤例外超过上限");
  if(gateways.Count==0 || string.IsNullOrWhiteSpace(kernel) || string.IsNullOrWhiteSpace(service))throw new InvalidDataException("后台通道与程序身份必须明确");
  if(gateways.Any(x=>x.Protocol is not (6 or 17) || x.Endpoint.Port<1) || management.Any(x=>x.Port<1))throw new InvalidDataException("通道协议与端口必须明确");
  var filters=new List<FilterSpec>();
  foreach(var v6 in new[]{false,true})
  {
   filters.Add(new(v6,0,"默认阻止物理出口",false,10));
   filters.Add(new(v6,1,"本机通信",true,1000,Loopback:true));
   if(tunnel!=0)filters.Add(new(v6,2,"已确认的 TUN 接口",true,950,Interface:tunnel));
   filters.Add(new(v6,3,"禁止物理 UDP DNS",false,900,RemotePort:53,Protocol:17));
   filters.Add(new(v6,4,"禁止物理 TCP DNS",false,900,RemotePort:53,Protocol:6));
   if(!v6)
   {
    filters.Add(new(false,5,"DHCP",true,850,Network:"255.255.255.255/32",RemotePort:67,LocalPort:68,Protocol:17));
    var networks=new[]{"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","169.254.0.0/16"};
    for(var i=0;i<networks.Length;i++)filters.Add(new(false,10+i,"局域网",true,700,Network:networks[i]));
   }
   else
   {
    filters.Add(new(true,10,"IPv6 链路本地",true,700,Network:"fe80::/10"));
    filters.Add(new(true,11,"IPv6 本地地址",true,700,Network:"fc00::/7"));
    filters.Add(new(true,12,"IPv6 链路邻居发现",true,850,Network:"ff02::/16",Protocol:58));
   }
   // Its packet-layer copy below cannot name the program, so in this mode the packet layer lets packets through;
   // new connections are still decided by the program-aware layer (only the kernel gets out).
   if(kernelDirect)filters.Add(new(v6,90,"分流：内核直连名单外网站",true,800,Application:kernel));
   for(var i=0;i<gateways.Count;i++)
   {
    var gateway=gateways[i];var e=gateway.Endpoint;if(e.Address.AddressFamily==(v6?System.Net.Sockets.AddressFamily.InterNetworkV6:System.Net.Sockets.AddressFamily.InterNetwork))
     filters.Add(new(v6,100+i,"内核到加密通道",true,800,Network:e.Address+"/"+(v6?128:32),RemotePort:e.Port,Protocol:gateway.Protocol,Application:kernel));
   }
   for(var i=0;i<management.Count;i++)
   {
    var e=management[i];if(e.Address.AddressFamily==(v6?System.Net.Sockets.AddressFamily.InterNetworkV6:System.Net.Sockets.AddressFamily.InterNetwork))
     filters.Add(new(v6,200+i,"后台到自有服务",true,800,Network:e.Address+"/"+(v6?128:32),RemotePort:e.Port,Protocol:6,Application:service));
   }
   if(!v6)for(var i=0;i<direct.Count;i++)
   {
    var item=DirectRule.Parse(direct[i].Cidr,direct[i].Note,direct[i].Public);
    filters.Add(new(false,300+i,"已确认直连例外",true,650,Network:item.Cidr));
   }
  }
  // Transport-layer guards cover packets from connections established before activation.
  // Application identity remains enforced by ALE; transport allows only the same endpoints.
  filters.AddRange(filters.ToArray().Select(f=>f with {Slot=f.Slot+512,Name="数据包 · "+f.Name,Application=null,Transport=true}));
  return new(filters.AsReadOnly());
 }
}

