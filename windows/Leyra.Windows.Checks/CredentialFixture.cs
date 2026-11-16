using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Leyra.Windows.Platform;

[SupportedOSPlatform("windows")]
static class CredentialFixture
{
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct UserInfo
 {public string Name,Password;public uint Age,Privilege;public string? Home,Comment;public uint Flags;public string? Script;}
 [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]static extern uint NetUserAdd(string? server,uint level,ref UserInfo info,out uint parameter);
 [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]static extern uint NetUserDel(string? server,string name);
 [DllImport("credui.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CredPackAuthenticationBuffer(uint flags,string user,string password,IntPtr output,ref uint size);
 static byte[] Pack(string user,string password)
 {
  uint size=0;CredPackAuthenticationBuffer(0,user,password,IntPtr.Zero,ref size);
  if(size is <1 or >65536)throw new Exception("Unexpected credential packing size");
  var p=Marshal.AllocHGlobal((int)size);
  try
  {
   if(!CredPackAuthenticationBuffer(0,user,password,p,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());
   var result=new byte[size];Marshal.Copy(p,result,0,(int)size);return result;
  }
  finally{for(var i=0;i<size;i++)Marshal.WriteByte(p,i,0);Marshal.FreeHGlobal(p);}
 }
 public static void Run()
 {
  var name="lyrf"+Guid.NewGuid().ToString("N")[..12];var password="Aa1!"+Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
  var info=new UserInfo{Name=name,Password=password,Privilege=1,Flags=0x10201,Comment="Temporary LEYRA credential test; removed by fixture"};
  var error=NetUserAdd(null,1,ref info,out _);if(error!=0)throw new Win32Exception((int)error);
  try
  {
   var account=Environment.MachineName+"\\"+name;
   var sid=(SecurityIdentifier)new NTAccount(Environment.MachineName,name).Translate(typeof(SecurityIdentifier));
   void Verify(byte[] bytes,SecurityIdentifier owner,bool expected)
   {
    if(NativeCredentials.Verify(bytes,owner)!=expected)throw new Exception("Windows credential verification result mismatch");
    if(bytes.Any(x=>x!=0))throw new Exception("Packed credentials were not cleared");
   }
   Verify(Pack(account,password),sid,true);
   Verify(Pack(account,password),WindowsIdentity.GetCurrent().User!,false);
   Verify(Pack(account,password+"-wrong"),sid,false);
   Verify(new byte[]{1,2,3},sid,false);
   Verify(Enumerable.Repeat((byte)65,65537).ToArray(),sid,false);
   Console.WriteLine("PASS real Windows account password and exact owner SID binding, wrong-password/malformed rejection and credential-buffer clearing");
  }
  finally{var deleted=NetUserDel(null,name);if(deleted!=0)throw new Win32Exception((int)deleted,"Temporary test account cleanup failed");}
  Console.WriteLine("PASS temporary fixture account removed");
 }
}
