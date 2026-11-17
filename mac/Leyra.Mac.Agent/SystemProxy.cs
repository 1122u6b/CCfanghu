using System.Diagnostics;
using System.Text.RegularExpressions;
namespace Leyra.Mac.Agent;

/// <summary>macOS system proxy (System Settings > Network > each service > Proxies), read and switched with
/// networksetup. Same behaviour as the Windows version: find the program behind it, switch it off, undo for 2 minutes.</summary>
sealed class SystemProxy(uint owner)
{
 static readonly string[] Kinds=["webproxy","securewebproxy","socksfirewallproxy"];
 readonly List<(string Service,string Kind)> disabled=[];long disabledAt;bool available;
 public bool UndoAvailable=>available&&Stopwatch.GetElapsedTime(disabledAt)<TimeSpan.FromMinutes(2);
 static string Run(params string[] args)
 {
  var start=new ProcessStartInfo("/usr/sbin/networksetup",args){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  using var p=Process.Start(start)??throw new InvalidOperationException("无法读取网络设置");
  var o=p.StandardOutput.ReadToEnd();p.WaitForExit(10000);return o;
 }
 static IEnumerable<string> Services()=>Run("-listallnetworkservices").Split('\n').Skip(1).Select(x=>x.Trim()).Where(x=>x.Length>0&&!x.StartsWith('*'));
 static (bool On,string Server,int Port) Get(string service,string kind)
 {
  var o=Run("-get"+kind,service);
  var on=Regex.IsMatch(o,@"^Enabled:\s*Yes",RegexOptions.Multiline);
  var server=Regex.Match(o,@"^Server:\s*(\S*)",RegexOptions.Multiline).Groups[1].Value;
  int.TryParse(Regex.Match(o,@"^Port:\s*(\d+)",RegexOptions.Multiline).Groups[1].Value,out var port);
  return (on,server,port);
 }
 IEnumerable<(string Service,string Kind,string Server,int Port)> Active()
 {
  foreach(var s in Services())foreach(var k in Kinds){var g=Get(s,k);if(g.On)yield return (s,k,g.Server,g.Port);}
  foreach(var s in Services())if(Regex.IsMatch(Run("-getautoproxyurl",s),@"^Enabled:\s*Yes",RegexOptions.Multiline))yield return (s,"autoproxy","",0);
 }
 public bool Enabled(){try{return Active().Any();}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){return false;}}
 public object Disable()
 {
  if(UndoAvailable)throw new InvalidOperationException("已关闭系统代理，可先使用撤销恢复原设置");
  disabled.Clear();
  foreach(var a in Active().ToList()){Run(a.Kind=="autoproxy"?"-setautoproxystate":"-set"+a.Kind+"state",a.Service,"off");disabled.Add((a.Service,a.Kind));}
  disabledAt=Stopwatch.GetTimestamp();available=true;return new{saved=true};
 }
 public object Undo()
 {
  if(!UndoAvailable){available=false;disabled.Clear();throw new InvalidOperationException("撤销已超过两分钟，请在系统设置的网络里调整");}
  if(Active().Any())throw new InvalidOperationException("系统代理已被其他操作修改，无法自动撤销");
  foreach(var (s,k) in disabled)Run(k=="autoproxy"?"-setautoproxystate":"-set"+k+"state",s,"on");
  available=false;disabled.Clear();return new{saved=true};
 }
 public object[] Conflicts()
 {
  try
  {
   var local=Active().FirstOrDefault(a=>a.Port>0&&a.Server is "127.0.0.1" or "localhost" or "::1");
   if(local.Port==0)return Active().Any()?[new{id="proxy-system",severity="warn",name="系统代理",message="系统设置里开着代理，会和 Leyra 抢网络。可以一键关掉。",evidence="",quit=(object?)null}]:[];
   if(ListeningProcess(local.Port) is not Process p)return [];
   using(p)
   {
    var path=p.MainModule?.FileName??"";if(path.Length==0||p.Id==Environment.ProcessId)return [];
    var app=Regex.Match(path,@"([^/]+)\.app/").Groups[1].Value;var name=app.Length>0?app:p.ProcessName;
    return [new{id="proxy-owner-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..10].ToLowerInvariant(),severity="warn",name,
     message=name+" 设置了系统代理（127.0.0.1:"+local.Port+"），会和 Leyra 抢网络。请退出它，Leyra 会把系统代理一起关掉。",evidence=path,quit=new{how="mac_process",images=new[]{p.ProcessName},owner=true}}];
   }
  }
  catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException){return [];}
 }
 static Process? ListeningProcess(int port)
 {
  var start=new ProcessStartInfo("/usr/sbin/lsof",["-nP","-iTCP:"+port,"-sTCP:LISTEN","-Fp"]){UseShellExecute=false,RedirectStandardOutput=true};
  using var l=Process.Start(start);if(l is null)return null;
  var o=l.StandardOutput.ReadToEnd();l.WaitForExit(10000);
  var m=Regex.Match(o,@"^p(\d+)",RegexOptions.Multiline);
  return m.Success?Process.GetProcessById(int.Parse(m.Groups[1].Value)):null;
 }
 /// <summary>After ending the program that owned it, switch the system proxy off (no undo: it now points nowhere).</summary>
 public void ClearAfterQuit(){foreach(var a in Active().ToList())Run(a.Kind=="autoproxy"?"-setautoproxystate":"-set"+a.Kind+"state",a.Service,"off");}
}
