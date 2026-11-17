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
  // Find the program behind the system proxy (e.g. 127.0.0.1:7890), whatever it is called,
  // so 帮我退出 can end it instead of only switching the proxy off while it keeps turning it back on.
  try
  {
   if(LocalProxyPort() is int port&&ListeningProcess(port) is Process p)
   {
    using(p)
    {
     var path=p.MainModule?.FileName??"";var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
     if(path.Length>0&&!path.StartsWith(windows,StringComparison.OrdinalIgnoreCase)&&p.Id>4&&p.Id!=Environment.ProcessId)
     {
      var info=FileVersionInfo.GetVersionInfo(path);
      var name=new[]{info.ProductName,info.FileDescription,p.ProcessName}.First(x=>!string.IsNullOrWhiteSpace(x))!.Trim();
      // The listener is often a core started by a window app in the same folder; end both so it does not come back.
      var images=new List<string>{p.ProcessName};
      try{if(ParentOf(p) is Process parent)using(parent){var pp=parent.MainModule?.FileName??"";if(pp.Length>0&&string.Equals(Path.GetDirectoryName(pp),Path.GetDirectoryName(path),StringComparison.OrdinalIgnoreCase)&&!images.Contains(parent.ProcessName))images.Insert(0,parent.ProcessName);}}
      catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException){}
      return [new{id="proxy-owner-"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..10].ToLowerInvariant(),severity="warn",name,
       message=name+" 设置了系统代理（127.0.0.1:"+port+"），会和 Leyra 抢网络。请退出它，Leyra 会把系统代理一起关掉。",evidence=path,quit=new{how="windows_image",images=images.ToArray(),owner=true}}];
     }
    }
   }
  }
  catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or IOException){}
  return [new{id="windows-system-proxy",severity="warn",name="Windows 系统代理",message="Windows 仍启用了系统代理或自动代理脚本，可能与当前连接同时生效。",evidence="读取当前桌面用户的 Internet Settings",quit=new{how="clear_proxy"}}];
 }
 /// <summary>Port of a system proxy on this computer, from ProxyServer such as "127.0.0.1:7890" or "http=127.0.0.1:7890;https=...".</summary>
 int? LocalProxyPort()
 {
  using var key=Open(false);
  if(key.GetValue("ProxyEnable") is not int enabled||enabled==0||key.GetValue("ProxyServer") is not string server)return null;
  foreach(var part in server.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
  {
   var value=part.Contains('=')?part[(part.IndexOf('=')+1)..]:part;
   if(value.Contains("://"))value=value[(value.IndexOf("://")+3)..];
   var colon=value.LastIndexOf(':');if(colon<1||!int.TryParse(value[(colon+1)..].TrimEnd('/'),out var port)||port is <1 or >65535)continue;
   var host=value[..colon].Trim('[',']');
   if(host is "127.0.0.1" or "localhost" or "::1")return port;
  }
  return null;
 }
 [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
 /// <summary>The process listening on 127.0.0.1 or 0.0.0.0 at this TCP port (TCP_TABLE_OWNER_PID_LISTENER).</summary>
 static Process? ListeningProcess(int port)
 {
  var size=0;GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);if(size<=0)return null;
  var buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
  try
  {
   if(GetExtendedTcpTable(buffer,ref size,false,2,3,0)!=0)return null;
   var count=System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
   for(var i=0;i<count;i++)
   {
    var row=buffer+4+i*24;
    var local=(System.Runtime.InteropServices.Marshal.ReadByte(row,8)<<8)|System.Runtime.InteropServices.Marshal.ReadByte(row,9);
    if(local==port)return Process.GetProcessById(System.Runtime.InteropServices.Marshal.ReadInt32(row,20));
   }
  }
  finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
  return null;
 }
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct BasicInfo{public IntPtr Exit,Peb,Affinity,Priority,Pid,ParentPid;}
 [System.Runtime.InteropServices.DllImport("ntdll.dll")]static extern int NtQueryInformationProcess(IntPtr process,int infoClass,out BasicInfo info,int size,out int returned);
 static Process? ParentOf(Process p)
 {
  if(NtQueryInformationProcess(p.Handle,0,out var info,System.Runtime.InteropServices.Marshal.SizeOf<BasicInfo>(),out _)!=0)return null;
  var parent=Process.GetProcessById((int)info.ParentPid);
  // Parent ids get reused: only a process that started before its child is really the parent.
  if(parent.StartTime>p.StartTime){parent.Dispose();return null;}
  return parent;
 }
 /// <summary>After ending the program that owned it, switch the system proxy off (no undo: it now points nowhere).</summary>
 public void ClearAfterQuit()
 {
  using var key=Open(true);key.SetValue("ProxyEnable",0,RegistryValueKind.DWord);key.DeleteValue("AutoConfigURL",false);
 }
}
