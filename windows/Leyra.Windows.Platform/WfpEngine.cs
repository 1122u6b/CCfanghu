using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Leyra.Windows.Platform;

[SupportedOSPlatform("windows")]
public sealed class WfpEngine:IDisposable
{
 IntPtr engine;bool transaction;
 const uint Exists=0x80320009,FilterMissing=0x80320003,ProviderMissing=0x80320005,SubLayerMissing=0x80320007;
 static readonly Guid Flags=new("632ce23b-5167-435c-86d7-e903684aa80c"),RemoteAddress=new("b235ae9a-1d64-49b8-a44c-5ff3d9095045"),RemotePort=new("c35a604d-d22b-4e1a-91b4-68f674ee674b"),LocalPort=new("0c1ba1af-5765-453f-af22-a8f791ac775b"),Protocol=new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7"),LocalInterface=new("4cd62a49-59c3-4969-b7f3-bda5d32890a4"),AppId=new("d78e1e87-8644-4ea5-9437-d809ecefc971");
 public WfpEngine(){WfpLayout.Verify64Bit();Native.Check(Native.FwpmEngineOpen0(null,10,IntPtr.Zero,IntPtr.Zero,out engine));}
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
 public void Apply(WfpPlan plan)
 {
  if(plan.Filters.Count>1024 || plan.Filters.Select(x=>(x.Ipv6,x.Slot)).Distinct().Count()!=plan.Filters.Count)throw new InvalidDataException("过滤计划无效");
  foreach(var v6 in new[]{false,true})foreach(var transport in new[]{false,true})
   if(!plan.Filters.Any(x=>x.Ipv6==v6 && x.Transport==transport && x.Slot==(transport?512:0) && !x.Permit && x.Network is null && x.Application is null && x.Interface is null))throw new InvalidDataException("缺少默认拒绝规则");
  Begin();
  try
  {
   using var arena=new NativeArena();
   var provider=new WfpProvider{Key=WfpOwnership.Provider,Flags=1,Display=new DisplayData{Name=arena.Text("LeyraConnect private protection"),Description=arena.Text("Owned outbound policies; recovery removes only this provider")}};
   var error=Native.FwpmProviderAdd0(engine,in provider,IntPtr.Zero);if(error!=Exists)Native.Check(error);
   var layer=new WfpSubLayer{Key=WfpOwnership.SubLayer,Flags=1,ProviderKey=arena.Struct(WfpOwnership.Provider),Weight=0x7000,Display=provider.Display};
   error=Native.FwpmSubLayerAdd0(engine,in layer,IntPtr.Zero);if(error!=Exists)Native.Check(error);
   RemoveOwnedFilters();
   foreach(var spec in plan.Filters)Add(spec);
   Commit();
  }
  catch {Abort();throw;}
 }
 /// <summary>Coexist mode (no default deny: the customer's own proxy must keep working). Per program instead:
 /// the Claude desktop app may only reach this computer (127.0.0.1, where Leyra listens) and nothing else at all;
 /// browsers may not send UDP to the internet (WebRTC would reveal the real address). Loopback is allowed for all,
 /// local-network UDP for everyone but Claude. Slots 400-511, connection layer only.</summary>
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
 public void ApplyAppGuard(IReadOnlyList<string> lockedToLoopback,IReadOnlyList<string> noInternetUdp)
 {
  if(lockedToLoopback.Count>40||noInternetUdp.Count>60)throw new InvalidDataException("程序过滤规则超过上限");
  var specs=new List<FilterSpec>();
  foreach(var v6 in new[]{false,true})
  {
   specs.Add(new(v6,400,"共存：本机回环",true,1000,Loopback:true));
   var lan=v6?new[]{"fe80::/10","fc00::/7","ff00::/8"}:new[]{"10.0.0.0/8","172.16.0.0/12","192.168.0.0/16","169.254.0.0/16","224.0.0.0/4"};
   for(var i=0;i<lan.Length;i++)specs.Add(new(v6,401+i,"共存：局域网 UDP",true,900,Network:lan[i],Protocol:17));
   for(var i=0;i<lockedToLoopback.Count;i++)specs.Add(new(v6,410+i,"共存：Claude 只能连本机 Leyra",false,950,Application:lockedToLoopback[i]));
   for(var i=0;i<noInternetUdp.Count;i++)specs.Add(new(v6,450+i,"共存：浏览器不发外网 UDP",false,800,Protocol:17,Application:noInternetUdp[i]));
  }
  Begin();
  try
  {
   using var arena=new NativeArena();
   var provider=new WfpProvider{Key=WfpOwnership.Provider,Flags=1,Display=new DisplayData{Name=arena.Text("LeyraConnect private protection"),Description=arena.Text("Owned outbound policies; recovery removes only this provider")}};
   var error=Native.FwpmProviderAdd0(engine,in provider,IntPtr.Zero);if(error!=Exists)Native.Check(error);
   var layer=new WfpSubLayer{Key=WfpOwnership.SubLayer,Flags=1,ProviderKey=arena.Struct(WfpOwnership.Provider),Weight=0x7000,Display=provider.Display};
   error=Native.FwpmSubLayerAdd0(engine,in layer,IntPtr.Zero);if(error!=Exists)Native.Check(error);
   RemoveOwnedFilters();
   foreach(var spec in specs)Add(spec);
   Commit();
  }
  catch {Abort();throw;}
 }
 void Add(FilterSpec spec)
 {
  using var arena=new NativeArena();var conditions=new List<WfpCondition>();IntPtr app=IntPtr.Zero;
  WfpCondition Number(Guid field,uint type,ulong value,uint match=0)=>new(){Field=field,Match=match,Value=type==4?new WfpValue{Type=4,Value=new ValueUnion{Pointer=arena.Struct(value)}}:new WfpValue{Type=type,Value=new ValueUnion{U32=checked((uint)value)}}};
  try
  {
   if(spec.Loopback)conditions.Add(Number(Flags,3,1,6));
   if(spec.Interface is ulong luid)conditions.Add(Number(LocalInterface,4,luid));
   if(spec.RemotePort is int rp)conditions.Add(Number(RemotePort,2,checked((ushort)rp)));
   if(spec.LocalPort is int lp)conditions.Add(Number(LocalPort,2,checked((ushort)lp)));
   if(spec.Protocol is byte protocol)conditions.Add(Number(Protocol,1,protocol));
   if(spec.Network is string cidr)
   {
    var parts=cidr.Split('/');var address=IPAddress.Parse(parts[0]);var prefix=int.Parse(parts[1]);var bytes=address.GetAddressBytes();
    IntPtr mask;uint type;
    if(spec.Ipv6)
    {
     if(bytes.Length!=16 || prefix<0 || prefix>128)throw new InvalidDataException("IPv6 过滤地址无效");
     var raw=new byte[17];bytes.CopyTo(raw,0);raw[16]=(byte)prefix;mask=arena.Bytes(raw);type=0x101;
    }
    else
    {
     if(bytes.Length!=4 || prefix<1 || prefix>32)throw new InvalidDataException("IPv4 过滤地址无效");
     var raw=new byte[8];BinaryPrimitives.WriteUInt32LittleEndian(raw,BinaryPrimitives.ReadUInt32BigEndian(bytes));BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4),uint.MaxValue<<(32-prefix));mask=arena.Bytes(raw);type=0x100;
    }
    conditions.Add(new WfpCondition{Field=RemoteAddress,Value=new WfpValue{Type=type,Value=new ValueUnion{Pointer=mask}}});
   }
   if(spec.Application is string path)
   {
    if(!Path.IsPathFullyQualified(path) || !File.Exists(path))throw new InvalidDataException("过滤程序路径必须存在且为绝对路径");
    Native.Check(Native.FwpmGetAppIdFromFileName0(path,out app));conditions.Add(new WfpCondition{Field=AppId,Value=new WfpValue{Type=12,Value=new ValueUnion{Pointer=app}}});
   }
   var filter=new WfpFilter{Key=WfpOwnership.Filter(spec.Ipv6,spec.Slot),Display=new DisplayData{Name=arena.Text("LeyraConnect: "+spec.Name)},Flags=1,ProviderKey=arena.Struct(WfpOwnership.Provider),Layer=spec.Transport?(spec.Ipv6?WfpOwnership.Transport6:WfpOwnership.Transport4):(spec.Ipv6?WfpOwnership.Layer6:WfpOwnership.Layer4),SubLayer=WfpOwnership.SubLayer,Weight=new WfpValue{Type=4,Value=new ValueUnion{Pointer=arena.Struct(spec.Weight)}},ConditionCount=(uint)conditions.Count,Conditions=arena.Conditions(conditions),Action=new WfpAction{Type=spec.Permit?0x1002u:0x1001u}};
   Native.Check(Native.FwpmFilterAdd0(engine,in filter,IntPtr.Zero,out _));
  }
  finally {if(app!=IntPtr.Zero)Native.FwpmFreeMemory0(ref app);}
 }
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
 public bool DefaultGuardsInstalled()
 {
  foreach(var v6 in new[]{false,true})foreach(var transport in new[]{false,true})
  {
   var key=WfpOwnership.Filter(v6,transport?512:0);var error=Native.FwpmFilterGetByKey0(engine,in key,out var pointer);
   if(error==FilterMissing)return false;Native.Check(error);
   try {var filter=Marshal.PtrToStructure<WfpFilter>(pointer);if(filter.Action.Type!=0x1001 || filter.SubLayer!=WfpOwnership.SubLayer || filter.ConditionCount!=0 || (filter.Flags&1)==0 || filter.Layer!=(transport?(v6?WfpOwnership.Transport6:WfpOwnership.Transport4):(v6?WfpOwnership.Layer6:WfpOwnership.Layer4)) || filter.ProviderKey==IntPtr.Zero || Marshal.PtrToStructure<Guid>(filter.ProviderKey)!=WfpOwnership.Provider)return false;}
   finally {Native.FwpmFreeMemory0(ref pointer);}
  }
  return true;
 }
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
 public void RemoveOwned()
 {
  Begin();try{RemoveOwnedFilters();var sub=WfpOwnership.SubLayer;var provider=WfpOwnership.Provider;var error=Native.FwpmSubLayerDeleteByKey0(engine,in sub);if(error!=SubLayerMissing)Native.Check(error);error=Native.FwpmProviderDeleteByKey0(engine,in provider);if(error!=ProviderMissing)Native.Check(error);Commit();}catch{Abort();throw;}
 }
 void RemoveOwnedFilters(){foreach(var v6 in new[]{false,true})for(var slot=0;slot<1024;slot++){var key=WfpOwnership.Filter(v6,slot);var error=Native.FwpmFilterDeleteByKey0(engine,in key);if(error!=FilterMissing)Native.Check(error);}}
 void Begin(){Native.Check(Native.FwpmTransactionBegin0(engine,0));transaction=true;}
 void Commit(){Native.Check(Native.FwpmTransactionCommit0(engine));transaction=false;}
 void Abort(){if(transaction){Native.FwpmTransactionAbort0(engine);transaction=false;}}
 [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]
 public void Dispose(){Abort();if(engine!=IntPtr.Zero){Native.FwpmEngineClose0(engine);engine=IntPtr.Zero;}}
}

