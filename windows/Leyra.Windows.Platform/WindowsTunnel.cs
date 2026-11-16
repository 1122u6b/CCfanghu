using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Leyra.Windows.Platform;

[SupportedOSPlatform("windows")]
public static class WindowsTunnel
{
 public const string Name="LeyraConnect";
 [DllImport("iphlpapi.dll")]static extern uint ConvertInterfaceIndexToLuid(uint index,out ulong luid);
 public static ulong Current()
 {
  foreach(var nic in NetworkInterface.GetAllNetworkInterfaces())
  {
   if(nic.Name!=Name || nic.OperationalStatus!=OperationalStatus.Up)continue;
   try
   {
    var index=nic.GetIPProperties().GetIPv4Properties()?.Index;
    if(index is int value&&ConvertInterfaceIndexToLuid((uint)value,out var luid)==0)return luid;
   }
   catch(NetworkInformationException){/* Adapter may disappear between enumeration and lookup. */}
  }
  return 0;
 }
}
