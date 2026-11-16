using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Leyra.Windows.Platform;

[SupportedOSPlatform("windows")]
public sealed class NativeJob:IDisposable
{
 readonly SafeFileHandle job;
 [StructLayout(LayoutKind.Sequential)]struct BasicLimit{public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWork,MaxWork;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)]struct Io{public ulong A,B,C,D,E,F;}
 [StructLayout(LayoutKind.Sequential)]struct Limits{public BasicLimit Basic;public Io Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
 [StructLayout(LayoutKind.Sequential)]struct Startup{public uint Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;public ushort Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;}
 [StructLayout(LayoutKind.Sequential)]struct StartupEx{public Startup Startup;public IntPtr Attributes;}
 [StructLayout(LayoutKind.Sequential)]struct ProcessInfo{public IntPtr Process,Thread;public uint Pid,Tid;}
 [StructLayout(LayoutKind.Sequential)]struct Security{public uint Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateJobObject(IntPtr security,string? name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(SafeFileHandle job,int kind,in Limits value,uint size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateJobObject(SafeFileHandle job,uint code);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref UIntPtr size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,UIntPtr attribute,IntPtr value,UIntPtr size,IntPtr previous,IntPtr returned);
 [DllImport("kernel32.dll")]static extern void DeleteProcThreadAttributeList(IntPtr list);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string name,uint access,uint share,ref Security security,uint disposition,uint attributes,IntPtr template);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string application,System.Text.StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo info);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 public NativeJob()
 {
  if(IntPtr.Size!=8)throw new PlatformNotSupportedException("仅支持 Windows x64");
  job=CreateJobObject(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
  var limits=new Limits{Basic=new BasicLimit{Flags=0x2000}};
  if(!SetInformationJobObject(job,9,in limits,(uint)Marshal.SizeOf<Limits>())){var error=Marshal.GetLastWin32Error();job.Dispose();throw new Win32Exception(error);}
 }
 public NativeChild Launch(string executable,IReadOnlyList<string> arguments,string directory)
 {
  if(!Path.IsPathFullyQualified(executable)||!File.Exists(executable)||arguments.Any(x=>x.Contains('\0')))throw new InvalidDataException("内核启动参数无效");
  static string Quote(string value)=>"\""+value+"\"";
  // All paths are generated internally and end in a filename/directory component.
  if(executable.Contains('"')||arguments.Any(x=>x.Contains('"')||x.EndsWith('\\')))throw new InvalidDataException("不接受含引号或末尾分隔符的启动参数");
  UIntPtr size=UIntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,2,0,ref size);
  var attributes=Marshal.AllocHGlobal(checked((int)size.ToUInt64()));var jobValue=Marshal.AllocHGlobal(IntPtr.Size);var handles=Marshal.AllocHGlobal(IntPtr.Size);bool initialized=false;
  try
  {
   if(!InitializeProcThreadAttributeList(attributes,2,0,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());initialized=true;
   Marshal.WriteIntPtr(jobValue,job.DangerousGetHandle());
   if(!UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x2000D,jobValue,(UIntPtr)IntPtr.Size,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
   var security=new Security{Length=(uint)Marshal.SizeOf<Security>(),Inherit=true};
   using var nul=CreateFile("NUL",0xC0000000,3,ref security,3,0,IntPtr.Zero);if(nul.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
   Marshal.WriteIntPtr(handles,nul.DangerousGetHandle());
   if(!UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x20002,handles,(UIntPtr)IntPtr.Size,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
   var startup=new StartupEx{Startup=new Startup{Size=(uint)Marshal.SizeOf<StartupEx>(),Flags=0x100,Input=nul.DangerousGetHandle(),Output=nul.DangerousGetHandle(),Error=nul.DangerousGetHandle()},Attributes=attributes};
   var command=new System.Text.StringBuilder(Quote(executable)+" "+string.Join(" ",arguments.Select(Quote)));
   if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,true,0x08080000,IntPtr.Zero,directory,ref startup,out var info))throw new Win32Exception(Marshal.GetLastWin32Error());
   CloseHandle(info.Thread);return new NativeChild(new SafeProcessHandle(info.Process,true));
  }
  finally{if(initialized)DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);Marshal.FreeHGlobal(jobValue);Marshal.FreeHGlobal(handles);}
 }
 public void Stop(){if(!job.IsClosed && !job.IsInvalid && !TerminateJobObject(job,1))throw new Win32Exception(Marshal.GetLastWin32Error());}
 public void Dispose()=>job.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class NativeChild(SafeProcessHandle handle):IDisposable
{
 [DllImport("kernel32.dll",SetLastError=true)]static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(SafeProcessHandle handle,uint code);
 public bool HasExited {get{if(handle.IsClosed)return true;var result=WaitForSingleObject(handle,0);if(result==0)return true;if(result==258)return false;throw new Win32Exception(Marshal.GetLastWin32Error());}}
 public int ExitCode {get{if(!HasExited)throw new InvalidOperationException("进程尚未退出");if(!GetExitCodeProcess(handle,out var code))throw new Win32Exception(Marshal.GetLastWin32Error());return unchecked((int)code);}}
 public async Task WaitForExitAsync(CancellationToken stop){while(!HasExited)await Task.Delay(50,stop);}
 public bool WaitForExit(int milliseconds){var result=WaitForSingleObject(handle,(uint)milliseconds);if(result==0)return true;if(result==258)return false;throw new Win32Exception(Marshal.GetLastWin32Error());}
 public void Kill(){if(!HasExited&&!TerminateProcess(handle,1))throw new Win32Exception(Marshal.GetLastWin32Error());}
 public void Dispose()=>handle.Dispose();
}
