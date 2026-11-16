using System.Diagnostics;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Leyra.Windows.Platform;

namespace Leyra.Windows.Agent;

static class Installation
{
 public const string ServiceName="LeyraConnect";
 [DllImport("user32.dll")]static extern IntPtr GetShellWindow();
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint id);
 [DllImport("kernel32.dll",SetLastError=true)]static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint id);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
 static void Admin(){if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("此操作需要 Windows 管理员权限");}
 static SecurityIdentifier InteractiveOwner()
 {
  var shell=GetShellWindow();
  if(shell!=IntPtr.Zero)
  {
   GetWindowThreadProcessId(shell,out var id);using var process=OpenProcess(0x1000,false,id);
   if(!process.IsInvalid && OpenProcessToken(process,8,out var token))using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))if(identity.User is not null)return identity.User;
  }
  var current=WindowsIdentity.GetCurrent();
  if(current.IsSystem || current.User is null)throw new InvalidOperationException("找不到当前桌面用户");
  return current.User;
 }
 static int Sc(params string[] args)
 {
  var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"sc.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
  foreach(var arg in args)start.ArgumentList.Add(arg);
  using var p=Process.Start(start)??throw new InvalidOperationException("Windows 服务管理器无法启动");
  var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(30000)){p.Kill();throw new System.TimeoutException("服务管理操作超时");}Task.WaitAll(output,error);return p.ExitCode;
 }
 static bool Exists()=>ServiceController.GetServices().Any(s=>s.ServiceName==ServiceName);
 static void Stop()
 {
  if(!Exists())return;
  using var s=new ServiceController(ServiceName);
  if(s.Status!=ServiceControllerStatus.Stopped){s.Stop();s.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(25));}
 }
 static void SecureInstallation()
 {
  var programFiles=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
  var root=Path.Combine(programFiles,"LEYRAConnect");
  var version=Path.Combine(root,"versions",Program.Version);
  var binary=Path.GetFullPath(Environment.ProcessPath??throw new InvalidOperationException("安装程序路径无效"));
  if(!string.Equals(binary,Path.Combine(version,"Leyra.Agent.exe"),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请使用标准安装程序安装到 Program Files，不能从下载目录注册系统服务");
  // Reject redirected ancestors before granting SYSTEM a persistent executable path.
  for(var current=new DirectoryInfo(version);current is not null;current=current.Parent)
   if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("安装路径不能包含目录链接");
  var directories=new List<DirectoryInfo>{new(root)};
  void Collect(DirectoryInfo directory)
  {
   foreach(var entry in directory.EnumerateFileSystemInfos())
   {
    if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("安装内容不能包含文件链接");
    if(entry is DirectoryInfo child){directories.Add(child);Collect(child);}
   }
  }
  Collect(directories[0]);
  var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
  var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
  var users=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
  foreach(var directory in directories)
  {
   var acl=new DirectorySecurity();acl.SetOwner(admin);acl.SetAccessRuleProtection(true,false);
   foreach(var sid in new[]{admin,system})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   directory.SetAccessControl(acl);
   foreach(var file in directory.EnumerateFiles())
   {
    var access=new FileSecurity();access.SetOwner(admin);access.SetAccessRuleProtection(true,false);
    foreach(var sid in new[]{admin,system})access.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
    access.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.ReadAndExecute,AccessControlType.Allow));file.SetAccessControl(access);
   }
  }
 }
 public static void Install()
 {
  Admin();SecureInstallation();AgentStore.VerifyDataPath();var owner=File.Exists(Path.Combine(AgentStore.Root,"owner.sid"))?new SecurityIdentifier(File.ReadAllText(Path.Combine(AgentStore.Root,"owner.sid")).Trim()):InteractiveOwner();
  AgentStore.Initialize(owner);
  var binary=Path.GetFullPath(Environment.ProcessPath??throw new InvalidOperationException("安装程序路径无效"));
  var existed=Exists();string? old=null;
  if(existed){old=Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\"+ServiceName,"ImagePath",null) as string;Stop();}
  try
  {
   var command=existed?"config":"create";
   if(Sc(command,ServiceName,"binPath=","\""+binary+"\" --service","start=","auto","obj=","LocalSystem","DisplayName=","LEYRA 连接助手后台")!=0)throw new InvalidOperationException("无法注册后台服务");
   if(Sc("failure",ServiceName,"reset=","86400","actions=","restart/5000/restart/10000/restart/30000")!=0)throw new InvalidOperationException("无法设置后台服务恢复策略");
   if(Sc("start",ServiceName)!=0)throw new InvalidOperationException("后台服务启动失败");
   using var service=new ServiceController(ServiceName);service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20));
  }
  catch
  {
   if(existed && old is not null){Sc("config",ServiceName,"binPath=",old);Sc("start",ServiceName);}
   else if(Exists())Sc("delete",ServiceName);
   throw;
  }
 }
 public static void Recover()
 {
  Admin();
  if(File.Exists(Path.Combine(AgentStore.Root,"owner.sid"))){var store=new AgentStore();store.Settings.Enabled=false;store.Settings.PausedUntil=0;store.Settings.QuitPause=false;store.Save();}
  Stop();
  using(var engine=new WfpEngine())engine.RemoveOwned();

 }
 public static void RemoveService(){Admin();Stop();BrowserPolicies.Restore();if(Exists()&&Sc("delete",ServiceName)!=0)throw new InvalidOperationException("无法卸载后台服务");}
}
