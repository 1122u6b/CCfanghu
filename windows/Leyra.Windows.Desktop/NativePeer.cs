using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Leyra.Windows.Desktop;

static class NativePeer
{
 [DllImport("kernel32.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
 [DllImport("kernel32.dll",SetLastError=true)]
 static extern SafeProcessHandle OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint id);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool QueryFullProcessImageName(SafeProcessHandle process,uint flags,StringBuilder path,ref uint size);
 [StructLayout(LayoutKind.Sequential)]struct ServiceStatus{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,uint size,out uint needed);
 [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
 static void VerifyServicePid(uint pid)
 {
  var manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
  try
  {
   var service=OpenService(manager,"LeyraConnect",4);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
   try{if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<ServiceStatus>(),out _))throw new Win32Exception(Marshal.GetLastWin32Error());if(status.State!=4||status.Pid!=pid)throw new InvalidOperationException("本机通信端不是已登记的 Windows 后台服务");}
   finally{CloseServiceHandle(service);}
  }
  finally{CloseServiceHandle(manager);}
 }
 public static void Verify(NamedPipeClientStream pipe)
 {
  var expected=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"Leyra.Agent.exe"));
  if(!File.Exists(expected))throw new InvalidOperationException("已安装的防护服务文件缺失");
  if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var id))throw new Win32Exception(Marshal.GetLastWin32Error());
  VerifyServicePid(id);
  using var process=OpenProcess(0x1000,false,id);
  if(process.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
  var buffer=new StringBuilder(32768);uint length=32768;
  if(!QueryFullProcessImageName(process,0,buffer,ref length))throw new Win32Exception(Marshal.GetLastWin32Error());
  if(!string.Equals(Path.GetFullPath(buffer.ToString()),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("拒绝连接来源不明的本机服务");
 }
}
