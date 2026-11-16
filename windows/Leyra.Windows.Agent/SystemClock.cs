using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace Leyra.Windows.Agent;

static class SystemClock
{
 [StructLayout(LayoutKind.Sequential)]struct SystemTime{public ushort Year,Month,DayOfWeek,Day,Hour,Minute,Second,Milliseconds;}
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetSystemTime(in SystemTime time);
 public static void SetUtc(double unix)
 {
  if(!double.IsFinite(unix))throw new InvalidDataException("服务端时间无效");var d=DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(unix*1000))).UtcDateTime;
  if(d.Year<2024 || d.Year>2100)throw new InvalidDataException("服务端时间超出允许范围");
  var value=new SystemTime{Year=(ushort)d.Year,Month=(ushort)d.Month,Day=(ushort)d.Day,DayOfWeek=(ushort)d.DayOfWeek,Hour=(ushort)d.Hour,Minute=(ushort)d.Minute,Second=(ushort)d.Second,Milliseconds=(ushort)d.Millisecond};
  if(!SetSystemTime(in value))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
 }
 public static string CurrentIana()
 {
  TimeZoneInfo.ClearCachedData();var id=TimeZoneInfo.Local.Id;
  return TimeZoneInfo.TryConvertWindowsIdToIanaId(id,out var iana)?iana:id;
 }
 public static async Task SetTimezone(string id,CancellationToken stop)
 {
  if(!TimeZoneInfo.TryConvertIanaIdToWindowsId(id,out var windows))windows=TimeZoneInfo.FindSystemTimeZoneById(id).Id;
  var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"tzutil.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("/s");start.ArgumentList.Add(windows);
  using var process=Process.Start(start)??throw new InvalidOperationException("无法启动系统时区设置");
  var output=process.StandardOutput.ReadToEndAsync(stop);var error=process.StandardError.ReadToEndAsync(stop);await process.WaitForExitAsync(stop);await Task.WhenAll(output,error);
  if(process.ExitCode!=0)throw new InvalidOperationException("Windows 拒绝修改时区");TimeZoneInfo.ClearCachedData();
 }
}
