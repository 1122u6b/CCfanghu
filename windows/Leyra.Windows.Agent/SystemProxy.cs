using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;
namespace Leyra.Windows.Agent;

sealed class SystemProxy(SecurityIdentifier owner)
{
 const string Suffix=@"\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
 readonly Dictionary<string,(object Value,RegistryValueKind Kind)> previous=new();long disabledAt;bool available;
 RegistryKey Open(bool write)=>Registry.Users.OpenSubKey(owner.Value+Suffix,write)??throw new InvalidOperationException("当前桌面用户的网络设置不可用");
 public bool UndoAvailable=>available&&Stopwatch.GetElapsedTime(disabledAt)<TimeSpan.FromMinutes(2);
 public bool Enabled()
 {
  using var key=Open(false);return key.GetValue("ProxyEnable") is int enabled&&enabled!=0 || key.GetValue("AutoConfigURL") is string url&&url.Length>0;
 }
 public object Disable()
 {
  if(UndoAvailable)throw new InvalidOperationException("已关闭系统代理，可先使用撤销恢复原设置");
  using var key=Open(true);previous.Clear();
  foreach(var name in new[]{"ProxyEnable","AutoConfigURL"})if(key.GetValue(name) is object value)previous[name]=(value,key.GetValueKind(name));
  key.SetValue("ProxyEnable",0,RegistryValueKind.DWord);key.DeleteValue("AutoConfigURL",false);disabledAt=Stopwatch.GetTimestamp();available=true;return new{saved=true};
 }
 public object Undo()
 {
  if(!UndoAvailable){available=false;previous.Clear();throw new InvalidOperationException("撤销已超过两分钟，请在 Windows 网络设置中调整");}
  using var key=Open(true);
  // Do not overwrite settings changed by the user or another program after this action.
  if(key.GetValue("ProxyEnable") is not int enabled || enabled!=0 || key.GetValue("AutoConfigURL") is not null)throw new InvalidOperationException("系统代理已被其他操作修改，无法自动撤销");
  foreach(var name in new[]{"ProxyEnable","AutoConfigURL"})
   if(previous.TryGetValue(name,out var prior))key.SetValue(name,prior.Value,prior.Kind);else key.DeleteValue(name,false);
  available=false;previous.Clear();return new{saved=true};
 }
 public object[] Conflicts()
 {
  try{if(!Enabled())return [];}
  catch(InvalidOperationException){return [];}
  return [new{id="windows-system-proxy",severity="warn",name="Windows 系统代理",message="Windows 仍启用了系统代理或自动代理脚本，可能与当前连接同时生效。",evidence="读取当前桌面用户的 Internet Settings",quit=new{how="clear_proxy"}}];
 }
}
