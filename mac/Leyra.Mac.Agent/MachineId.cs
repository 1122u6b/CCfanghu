using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Leyra.Mac.Agent;

/// <summary>
/// Hardware identity used to bind one authorization code to one computer: the Mac's IOPlatformUUID, which belongs
/// to the logic board and survives reinstalling Leyra or macOS. Only a salted hash leaves the computer.
/// </summary>
static class MachineId
{
 public static string Get()
 {
  var start=new ProcessStartInfo("/usr/sbin/ioreg",["-rd1","-c","IOPlatformExpertDevice"]){UseShellExecute=false,RedirectStandardOutput=true};
  using var p=Process.Start(start)??throw new InvalidOperationException("无法读取本机硬件标识");
  var text=p.StandardOutput.ReadToEnd();p.WaitForExit(10000);
  var m=Regex.Match(text,"\"IOPlatformUUID\"\\s*=\\s*\"([0-9A-Fa-f-]{36})\"");
  if(!m.Success)throw new InvalidOperationException("无法读取本机硬件标识");
  return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("leyra-machine-v1|macuuid:"+m.Groups[1].Value.ToLowerInvariant()))).ToLowerInvariant();
 }
}
