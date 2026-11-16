using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Leyra.Windows.Core;

/// <summary>
/// Confirms the other end of the local pipe is the registered Leyra Windows service running the expected program.
/// Uses only service-manager queries that an ordinary (non-administrator) user is allowed to make:
/// opening the SYSTEM service process itself is refused for ordinary users.
/// </summary>
public static class ServicePeer
{
 public const string ServiceName="LeyraConnect";
 [DllImport("kernel32.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
 [StructLayout(LayoutKind.Sequential)]struct ServiceStatus{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,WaitHint,Pid,Flags;}
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string? machine,string? database,uint access);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
 [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out ServiceStatus status,uint size,out uint needed);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryServiceConfig(IntPtr service,IntPtr buffer,uint size,out uint needed);
 [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
 // QUERY_SERVICE_CONFIGW: three DWORDs, then the binary path pointer.
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct ServiceConfig{public uint Type,Start,Error;public IntPtr Binary;}

 /// <summary>The program path from the service's registered command line, e.g. "C:\..\Leyra.Agent.exe" --service.</summary>
 public static string ProgramFromCommandLine(string command)
 {
  command=command.Trim();
  if(command.StartsWith('"')){var end=command.IndexOf('"',1);if(end>1)return command[1..end];}
  var space=command.IndexOf(" --",StringComparison.Ordinal);
  return space>0?command[..space]:command;
 }

 public static void Verify(NamedPipeClientStream pipe,string expectedProgram)
 {
  if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var pid))throw new Win32Exception(Marshal.GetLastWin32Error());
  var manager=OpenSCManager(null,null,1);if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
  try
  {
   var service=OpenService(manager,ServiceName,1|4);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
   try
   {
    if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<ServiceStatus>(),out _))throw new Win32Exception(Marshal.GetLastWin32Error());
    if(status.State!=4||status.Pid!=pid)throw new InvalidOperationException("本机通信端不是已登记的 Leyra 后台服务");
    QueryServiceConfig(service,IntPtr.Zero,0,out var needed);
    var buffer=Marshal.AllocHGlobal((int)Math.Max(needed,64u));
    try
    {
     if(!QueryServiceConfig(service,buffer,Math.Max(needed,64u),out _))throw new Win32Exception(Marshal.GetLastWin32Error());
     var command=Marshal.PtrToStringUni(Marshal.PtrToStructure<ServiceConfig>(buffer).Binary)??"";
     if(!string.Equals(Path.GetFullPath(ProgramFromCommandLine(command)),Path.GetFullPath(expectedProgram),StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("后台服务不是本程序安装的版本，请重新安装");
    }
    finally{Marshal.FreeHGlobal(buffer);}
   }
   finally{CloseServiceHandle(service);}
  }
  finally{CloseServiceHandle(manager);}
 }
}
