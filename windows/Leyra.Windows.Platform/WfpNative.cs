using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Leyra.Windows.Platform;

[StructLayout(LayoutKind.Sequential)] public struct DisplayData {public IntPtr Name,Description;}
[StructLayout(LayoutKind.Sequential)] public struct ByteBlob {public uint Size;public IntPtr Data;}
[StructLayout(LayoutKind.Explicit,Size=8)] public struct ValueUnion {[FieldOffset(0)]public byte U8;[FieldOffset(0)]public ushort U16;[FieldOffset(0)]public uint U32;[FieldOffset(0)]public IntPtr Pointer;}
[StructLayout(LayoutKind.Sequential)] public struct WfpValue {public uint Type;public ValueUnion Value;}
[StructLayout(LayoutKind.Sequential)] public struct WfpCondition {public Guid Field;public uint Match;public WfpValue Value;}
[StructLayout(LayoutKind.Sequential)] public struct WfpAction {public uint Type;public Guid Key;}
[StructLayout(LayoutKind.Explicit,Size=16)] public struct FilterContext {[FieldOffset(0)]public ulong Raw;[FieldOffset(0)]public Guid ProviderContext;}
[StructLayout(LayoutKind.Sequential)] public struct WfpFilter {public Guid Key;public DisplayData Display;public uint Flags;public IntPtr ProviderKey;public ByteBlob ProviderData;public Guid Layer,SubLayer;public WfpValue Weight;public uint ConditionCount;public IntPtr Conditions;public WfpAction Action;public FilterContext Context;public IntPtr Reserved;public ulong Id;public WfpValue EffectiveWeight;}
[StructLayout(LayoutKind.Sequential)] public struct WfpProvider {public Guid Key;public DisplayData Display;public uint Flags;public ByteBlob Data;public IntPtr ServiceName;}
[StructLayout(LayoutKind.Sequential)] public struct WfpSubLayer {public Guid Key;public DisplayData Display;public uint Flags;public IntPtr ProviderKey;public ByteBlob Data;public ushort Weight;}
public static class WfpLayout
{
 public static void Verify64Bit()
 {
  if(IntPtr.Size!=8)throw new PlatformNotSupportedException("仅支持 64 位 Windows");
  foreach(var pair in new Dictionary<Type,int>{{typeof(WfpValue),16},{typeof(WfpCondition),40},{typeof(WfpFilter),200},{typeof(WfpProvider),64},{typeof(WfpSubLayer),72}})
   if(Marshal.SizeOf(pair.Key)!=pair.Value)throw new InvalidOperationException("WFP ABI 布局异常："+pair.Key.Name);
  if(Marshal.OffsetOf<WfpFilter>(nameof(WfpFilter.Context)).ToInt32()!=152)throw new InvalidOperationException("WFP union 对齐异常");
 }
}
[SupportedOSPlatform("windows")]
internal static class Native
{
 const string Dll="fwpuclnt.dll";
 [DllImport(Dll,CharSet=CharSet.Unicode,ExactSpelling=true)]internal static extern uint FwpmEngineOpen0(string? server,uint auth,IntPtr identity,IntPtr session,out IntPtr engine);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmEngineClose0(IntPtr engine);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmTransactionBegin0(IntPtr engine,uint flags);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmTransactionCommit0(IntPtr engine);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmTransactionAbort0(IntPtr engine);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmProviderAdd0(IntPtr engine,in WfpProvider provider,IntPtr descriptor);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmSubLayerAdd0(IntPtr engine,in WfpSubLayer subLayer,IntPtr descriptor);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmFilterAdd0(IntPtr engine,in WfpFilter filter,IntPtr descriptor,out ulong id);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmFilterGetByKey0(IntPtr engine,in Guid key,out IntPtr filter);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmFilterDeleteByKey0(IntPtr engine,in Guid key);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine,in Guid key);
 [DllImport(Dll,ExactSpelling=true)]internal static extern uint FwpmProviderDeleteByKey0(IntPtr engine,in Guid key);
 [DllImport(Dll,CharSet=CharSet.Unicode,ExactSpelling=true)]internal static extern uint FwpmGetAppIdFromFileName0(string file,out IntPtr blob);
 [DllImport(Dll,ExactSpelling=true)]internal static extern void FwpmFreeMemory0(ref IntPtr memory);
 internal static void Check(uint error){if(error!=0)throw new Win32Exception(unchecked((int)error),"WFP 操作失败：0x"+error.ToString("X8"));}
}
internal sealed class NativeArena:IDisposable
{
 readonly List<IntPtr> pointers=[];
 public IntPtr Struct<T>(T value) where T:struct {var p=Marshal.AllocHGlobal(Marshal.SizeOf<T>());pointers.Add(p);Marshal.StructureToPtr(value,p,false);return p;}
 public IntPtr Text(string text){var p=Marshal.StringToHGlobalUni(text);pointers.Add(p);return p;}
 public IntPtr Bytes(byte[] bytes){var p=Marshal.AllocHGlobal(bytes.Length);pointers.Add(p);Marshal.Copy(bytes,0,p,bytes.Length);return p;}
 public IntPtr Conditions(IReadOnlyList<WfpCondition> conditions){if(conditions.Count==0)return IntPtr.Zero;var size=Marshal.SizeOf<WfpCondition>();var p=Marshal.AllocHGlobal(size*conditions.Count);pointers.Add(p);for(int i=0;i<conditions.Count;i++)Marshal.StructureToPtr(conditions[i],p+i*size,false);return p;}
 public void Dispose(){foreach(var p in pointers)Marshal.FreeHGlobal(p);pointers.Clear();}
}

