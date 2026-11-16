using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

namespace Leyra.Windows.Platform;

[SupportedOSPlatform("windows")]
public static class NativeCredentials
{
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
 struct Info{public int Size;public IntPtr Parent;public string Message;public string Caption;public IntPtr Banner;}
 [DllImport("credui.dll",CharSet=CharSet.Unicode)]
 static extern uint CredUIPromptForWindowsCredentials(ref Info info,uint error,ref uint package,IntPtr input,uint inputSize,out IntPtr output,out uint outputSize,ref bool save,uint flags);
 [DllImport("credui.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool CredUnPackAuthenticationBuffer(uint flags,IntPtr buffer,uint size,IntPtr user,ref uint userSize,IntPtr domain,ref uint domainSize,IntPtr password,ref uint passwordSize);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool LogonUser(IntPtr user,IntPtr domain,IntPtr password,int type,int provider,out SafeAccessTokenHandle token);
 public static byte[] Prompt(IntPtr parent,string message)
 {
  var info=new Info{Size=Marshal.SizeOf<Info>(),Parent=parent,Caption="Leyra · 验证电脑账户",Message=message+"\n请输入 Windows 账户密码，不是 PIN。"};
  uint package=0;bool save=false;IntPtr output=IntPtr.Zero;uint size=0;
  try
  {
   var error=CredUIPromptForWindowsCredentials(ref info,0,ref package,IntPtr.Zero,0,out output,out size,ref save,1);
   if(error==1223)throw new OperationCanceledException("已取消账户验证");
   if(error!=0)throw new Win32Exception((int)error);
   if(size==0 || size>65536)throw new InvalidDataException("账户验证数据无效");
   var bytes=new byte[size];Marshal.Copy(output,bytes,0,(int)size);return bytes;
  }
  finally{if(output!=IntPtr.Zero){Clear(output,(int)size);Marshal.FreeCoTaskMem(output);}}
 }
 public static bool Verify(byte[] packed,SecurityIdentifier expected)
 {
  if(packed.Length is <1 or >65536){CryptographicOperations.ZeroMemory(packed);return false;}
  var input=Marshal.AllocHGlobal(packed.Length);IntPtr user=IntPtr.Zero,domain=IntPtr.Zero,password=IntPtr.Zero,splitUser=IntPtr.Zero,splitDomain=IntPtr.Zero;
  uint u=0,d=0,p=0;int ua=0,da=0,pa=0;
  try
  {
   Marshal.Copy(packed,0,input,packed.Length);
   CredUnPackAuthenticationBuffer(0,input,(uint)packed.Length,IntPtr.Zero,ref u,IntPtr.Zero,ref d,IntPtr.Zero,ref p);
   if(u is <1 or >4096 || d>4096 || p is <1 or >4096)return false;
   ua=(int)u*2;da=(int)Math.Max(d,1)*2;pa=(int)p*2;
   user=Marshal.AllocHGlobal(ua);domain=Marshal.AllocHGlobal(da);password=Marshal.AllocHGlobal(pa);
   Clear(user,(int)u*2);Clear(domain,(int)Math.Max(d,1)*2);Clear(password,(int)p*2);
   if(!CredUnPackAuthenticationBuffer(0,input,(uint)packed.Length,user,ref u,domain,ref d,password,ref p))return false;
   var userPtr=user;var domainPtr=Marshal.ReadInt16(domain)==0?IntPtr.Zero:domain;
   var name=Marshal.PtrToStringUni(user)??"";
   if(domainPtr==IntPtr.Zero && name.Contains('\\'))
   {var parts=name.Split('\\',2);splitDomain=Marshal.StringToHGlobalUni(parts[0]);splitUser=Marshal.StringToHGlobalUni(parts[1]);domainPtr=splitDomain;userPtr=splitUser;}
   if(!LogonUser(userPtr,domainPtr,password,2,0,out var token))return false;
   using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))return identity.User?.Equals(expected)==true;
  }
  finally
  {
   Clear(input,packed.Length);Marshal.FreeHGlobal(input);
   if(user!=IntPtr.Zero){Clear(user,ua);Marshal.FreeHGlobal(user);}
   if(domain!=IntPtr.Zero){Clear(domain,da);Marshal.FreeHGlobal(domain);}
   if(password!=IntPtr.Zero){Clear(password,pa);Marshal.FreeHGlobal(password);}
   if(splitUser!=IntPtr.Zero)Marshal.FreeHGlobal(splitUser);
   if(splitDomain!=IntPtr.Zero)Marshal.FreeHGlobal(splitDomain);
   CryptographicOperations.ZeroMemory(packed);
  }
 }
 static void Clear(IntPtr address,int size){for(var i=0;i<size;i++)Marshal.WriteByte(address,i,0);}
}
