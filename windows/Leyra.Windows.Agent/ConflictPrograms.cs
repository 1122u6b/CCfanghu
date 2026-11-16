using System.Diagnostics;
using System.Globalization;

namespace Leyra.Windows.Agent;

static class ConflictPrograms
{
 // Restrict both detection and termination to identified network applications.
 static readonly Dictionary<string,string> Known=new(StringComparer.OrdinalIgnoreCase)
 {
  ["FlClash"]="FlClash",["clash-verge"]="Clash Verge",["Clash for Windows"]="Clash for Windows",
  ["v2rayN"]="v2rayN",["v2ray"]="V2Ray",["sing-box"]="sing-box",["mihomo"]="Mihomo",
  ["clash"]="Clash",["Clash.Meta"]="Clash Meta",["UU"]="UU 加速器",["leigod"]="雷神加速器"
 };
 static bool Allowed(Process process)
 {
  if(!Known.ContainsKey(process.ProcessName))return false;
  var path=process.MainModule?.FileName;
  if(path is null)return false;
  var root=Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  return !Path.GetFullPath(path).StartsWith(root,StringComparison.OrdinalIgnoreCase);
 }
 static string Identity(Process process)=>process.Id.ToString(CultureInfo.InvariantCulture)+":"+process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
 public static object[] Scan()
 {
  var items=new List<object>();
  foreach(var process in Process.GetProcesses())using(process)
  {
   try
   {
    if(!Allowed(process))continue;
    var name=Known[process.ProcessName];items.Add(new{id=Identity(process),name,severity="warn",message=name+" 正在运行，可能同时修改代理、路由或虚拟网卡。",evidence="已核对运行进程和程序路径",quit=new{how="windows_image"}});
   }
   catch(Exception error)when(error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException){}
  }
  return items.ToArray();
 }
 public static object Quit(string identity,bool force)
 {
  var parts=identity.Split(':');
  if(parts.Length!=2||!int.TryParse(parts[0],out var pid)||!long.TryParse(parts[1],out _))throw new InvalidDataException("冲突程序标识无效");
  Process process;
  try{process=Process.GetProcessById(pid);}catch(ArgumentException){return new{left=Array.Empty<string>()};}
  using(process)
  {
   if(Identity(process)!=identity||!Allowed(process))throw new InvalidOperationException("冲突程序已变化，请重新检查");
   var name=Known[process.ProcessName];
   if(force){process.Kill(entireProcessTree:true);process.WaitForExit(5000);}
   else process.CloseMainWindow();
   return new{requested=true,left=process.HasExited?Array.Empty<string>():new[]{name}};
  }
 }
}
