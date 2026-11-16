using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Leyra.Windows.Agent;

/// <summary>
/// Hardware identity used to bind one authorization code to one computer. The SMBIOS system
/// UUID belongs to the motherboard, so it survives reinstalling Leyra or Windows; the Windows
/// MachineGuid is only a fallback for firmware without a usable UUID. Only a salted hash leaves
/// the computer.
/// </summary>
static class MachineId
{
 [DllImport("kernel32.dll",SetLastError=true)]static extern uint GetSystemFirmwareTable(uint provider,uint id,byte[]? buffer,uint size);
 const uint RSMB=0x52534D42;
 public static string Get()
 {
  var source=SmbiosUuid() is string uuid?"smbios:"+uuid:"machineguid:"+(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography","MachineGuid",null) as string??throw new InvalidOperationException("无法识别这台电脑"));
  return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("leyra-machine-v1|"+source.ToLowerInvariant()))).ToLowerInvariant();
 }
 static string? SmbiosUuid()
 {
  try
  {
   var size=GetSystemFirmwareTable(RSMB,0,null,0);if(size<8||size>1<<20)return null;
   var raw=new byte[size];if(GetSystemFirmwareTable(RSMB,0,raw,size)!=size)return null;
   var length=BitConverter.ToInt32(raw,4);var offset=8;var end=Math.Min(raw.Length,8+length);
   while(offset+4<=end)
   {
    var type=raw[offset];var formatted=raw[offset+1];
    if(type==127||formatted<4)break;
    if(type==1&&formatted>=0x19&&offset+0x18<=end)
    {
     var bytes=raw.AsSpan(offset+8,16).ToArray();
     // All-zero or all-FF means the vendor did not set one.
     if(bytes.All(b=>b==0)||bytes.All(b=>b==0xFF))return null;
     return new Guid(bytes).ToString("N");
    }
    // Skip the formatted area and the string table, which ends with a double NUL.
    var next=offset+formatted;while(next+1<end&&!(raw[next]==0&&raw[next+1]==0))next++;
    offset=next+2;
   }
  }
  catch(Exception e)when(e is EntryPointNotFoundException or DllNotFoundException or ArgumentException){}
  return null;
 }
}
