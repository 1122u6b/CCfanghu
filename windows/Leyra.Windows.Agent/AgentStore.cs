using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Leyra.Windows.Core;

namespace Leyra.Windows.Agent;

[System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] sealed class AgentSettings
{
 public int Schema{get;set;}=1;
 public bool Enabled{get;set;}
 public long PausedUntil{get;set;}
 public bool QuitPause{get;set;}
 public int ConsentVersion{get;set;}
 public string? OriginalTimezone{get;set;}
 // 由出口 IP 归属地自动取得；还没取得时为空，界面显示「还不知道」。
 public string ExitTimezone{get;set;}="";
 public string? TimezoneDismissedFor{get;set;}
 // Off by default: pausing or quitting asks for an in-app confirmation only, not the Windows password.
 public bool RequireOwnerPassword{get;set;}
 public List<DirectRule> DirectRules{get;set;}=[];
 // Off: everything goes through the exit. On: only ProxyDomains (and subdomains) do; other sites connect directly.
 public bool ProxyOnlyListed{get;set;}
 public List<string> ProxyDomains{get;set;}=[..Leyra.Windows.Core.MihomoConfiguration.DefaultProxyDomains];
 // 「下次开机时更新」：已下载并核对过的新版本，下次开机本服务启动时自动安装（不再提示）。
 public string? UpdateAtBoot{get;set;}
 public string UpdateAtBootDigest{get;set;}="";
 // 出口连不上时自动换到其他能用的出口（默认开）。关了：直接断网并提醒，出口 IP 不会自己变。
 public bool AutoSwitchExit{get;set;}=true;
 // 只让 Claude 走出口 / 全部走出口 选过没有。新装默认只让 Claude 走出口（已选）；老客户升级后为空，界面问一次。
 public bool? ModeChosen{get;set;}
}
sealed class AgentStore
{
 public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"LeyraConnect");
 public SecurityIdentifier Owner{get;}
 public AgentSettings Settings{get;private set;}
 public string ProfileFile=>Path.Combine(Root,"device-profile.json");
 public AgentStore()
 {
  Owner=new SecurityIdentifier(File.ReadAllText(Path.Combine(Root,"owner.sid")).Trim());
  Settings=File.Exists(Path.Combine(Root,"state.json"))?JsonSerializer.Deserialize<AgentSettings>(File.ReadAllBytes(Path.Combine(Root,"state.json")))??throw new InvalidDataException("本机状态文件无效"):new(){ProxyOnlyListed=true,ModeChosen=true}; // a new install: only Claude through the exit
  if(Settings.Schema!=1 || Settings.DirectRules.Count>64)throw new InvalidDataException("本机状态版本无效");
  Settings.DirectRules=Settings.DirectRules.Select(r=>DirectRule.Parse(r.Cidr,r.Note,r.Public)).ToList();
  try{Settings.ProxyDomains=Leyra.Windows.Core.MihomoConfiguration.ProxyDomains(Settings.ProxyDomains??[]).ToList();}
  catch(InvalidDataException){Settings.ProxyDomains=[..Leyra.Windows.Core.MihomoConfiguration.DefaultProxyDomains];}
 }
 public ServerProfile? Profile()=>File.Exists(ProfileFile)?ServerProfile.Parse(File.ReadAllBytes(ProfileFile)):null;
 public void Save()=>Atomic(Path.Combine(Root,"state.json"),JsonSerializer.SerializeToUtf8Bytes(Settings));
 public void SetProfile(ServerProfile profile)=>Atomic(ProfileFile,profile.Encode());
 public static void Atomic(string path,byte[] bytes)
 {
  var temp=path+".new-"+Guid.NewGuid().ToString("N");
  try
  {
   using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){f.Write(bytes);f.Flush(true);}
   File.Move(temp,path,true);
  }
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public static void VerifyDataPath()
 {
  if(!Directory.Exists(Root))return;
  var directory=new DirectoryInfo(Root);
  var allowed=new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)};
  var owner=directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
  if(!allowed.Contains(owner))throw new UnauthorizedAccessException("已有运行目录不是系统或管理员所有，请勿直接接管未知目录");
  void Inspect(DirectoryInfo current)
  {
   if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("运行目录不能包含链接");
   foreach(var file in current.EnumerateFileSystemInfos())
   {
    if((file.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("运行文件不能包含链接");
    if(file is DirectoryInfo child)Inspect(child);
   }
  }
  Inspect(directory);
 }
 public static void Initialize(SecurityIdentifier owner)
 {
  VerifyDataPath();Directory.CreateDirectory(Root);
  var acl=new DirectorySecurity();acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));acl.SetAccessRuleProtection(true,false);
  var inherit=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
  foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
   acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,inherit,PropagationFlags.None,AccessControlType.Allow));
  // Runtime secrets are readable only by SYSTEM and administrators; UI uses the owner-scoped pipe.
  void Secure(DirectoryInfo directory)
  {
   directory.SetAccessControl(acl);
   foreach(var file in directory.EnumerateFiles())
   {
    var access=new FileSecurity();access.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));access.SetAccessRuleProtection(true,false);
    foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})access.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
    file.SetAccessControl(access);
   }
   foreach(var child in directory.EnumerateDirectories())Secure(child);
  }
  Secure(new DirectoryInfo(Root));
  var ownerPath=Path.Combine(Root,"owner.sid");
  if(!File.Exists(ownerPath))Atomic(ownerPath,System.Text.Encoding.UTF8.GetBytes(owner.Value));
 }
}
