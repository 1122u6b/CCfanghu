using System.Diagnostics;

namespace Leyra.Mac.Agent;

static class SystemClock
{
 static async Task Run(string file,string[] args,string failure,CancellationToken stop=default)
 {
  var start=new ProcessStartInfo(file,args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var p=Process.Start(start)??throw new InvalidOperationException(failure);
  var o=p.StandardOutput.ReadToEndAsync(stop);var e=p.StandardError.ReadToEndAsync(stop);await p.WaitForExitAsync(stop);await Task.WhenAll(o,e);
  if(p.ExitCode!=0)throw new InvalidOperationException(failure);
 }
 public static void SetUtc(double unix)
 {
  if(!double.IsFinite(unix))throw new InvalidDataException("服务端时间无效");var d=DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(unix*1000))).UtcDateTime;
  if(d.Year<2024 || d.Year>2100)throw new InvalidDataException("服务端时间超出允许范围");
  Run("/bin/date",["-u",d.ToString("MMddHHmmyyyy.ss")],"系统拒绝修改时间").GetAwaiter().GetResult();
 }
 /// <summary>True when the Mac already uses this zone. Zones with the same rules (Asia/Hong_Kong and
 /// Asia/Shanghai) count as the same, as on Windows.</summary>
 public static bool Matches(string iana)
 {
  if(iana.Length==0)return false;
  TimeZoneInfo.ClearCachedData();var local=TimeZoneInfo.Local;
  if(string.Equals(local.Id,iana,StringComparison.OrdinalIgnoreCase))return true;
  if(TimeZoneInfo.TryConvertIanaIdToWindowsId(iana,out var a)&&TimeZoneInfo.TryConvertIanaIdToWindowsId(local.Id,out var b))return string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
  return false;
 }
 public static string CurrentIana(){TimeZoneInfo.ClearCachedData();return TimeZoneInfo.Local.Id;}
 public static async Task SetTimezone(string id,CancellationToken stop)
 {
  if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^[A-Za-z_]+(/[A-Za-z0-9_+-]+){0,2}$")||!File.Exists("/usr/share/zoneinfo/"+id))throw new InvalidDataException("时区无效");
  await Run("/usr/sbin/systemsetup",["-settimezone",id],"macOS 拒绝修改时区",stop);
  TimeZoneInfo.ClearCachedData();
 }
}
