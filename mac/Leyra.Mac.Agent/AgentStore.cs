using System.Runtime.InteropServices;
using System.Text.Json;
using Leyra.Windows.Core;

namespace Leyra.Mac.Agent;

sealed class AgentSettings
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
}
sealed class AgentStore
{
 // Owned by root, mode 0700: device credentials and settings are readable by the service only; the window uses the socket.
 public const string Root="/Library/Application Support/LeyraConnect";
 public uint Owner{get;}
 public AgentSettings Settings{get;private set;}
 public string ProfileFile=>Path.Combine(Root,"device-profile.json");
 public AgentStore()
 {
  Owner=uint.Parse(File.ReadAllText(Path.Combine(Root,"owner.uid")).Trim());
  Settings=File.Exists(Path.Combine(Root,"state.json"))?JsonSerializer.Deserialize<AgentSettings>(File.ReadAllBytes(Path.Combine(Root,"state.json")))??throw new InvalidDataException("本机状态文件无效"):new();
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
   chmod(temp,0x180);File.Move(temp,path,true); // 0600
  }
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
 [DllImport("libc",SetLastError=true)]static extern int chmod(string path,ushort mode);
 [DllImport("libc",SetLastError=true)]static extern int lstat(string path,byte[] buf);
 /// <summary>The data folder must be a real folder owned by root, not a link planted by someone else.</summary>
 public static void VerifyDataPath()
 {
  if(!Directory.Exists(Root))return;
  var info=new DirectoryInfo(Root);
  if(info.LinkTarget is not null)throw new UnauthorizedAccessException("运行目录不能是链接");
  if(File.GetUnixFileMode(Root).HasFlag(UnixFileMode.OtherWrite)||File.GetUnixFileMode(Root).HasFlag(UnixFileMode.GroupWrite))throw new UnauthorizedAccessException("运行目录权限不安全");
  foreach(var f in info.EnumerateFileSystemInfos("*",SearchOption.AllDirectories))if(f.LinkTarget is not null)throw new UnauthorizedAccessException("运行文件不能包含链接");
 }
 public static void Initialize(uint owner)
 {
  VerifyDataPath();Directory.CreateDirectory(Root);chmod(Root,0x1C0); // 0700, root
  var ownerPath=Path.Combine(Root,"owner.uid");
  if(!File.Exists(ownerPath))Atomic(ownerPath,System.Text.Encoding.UTF8.GetBytes(owner.ToString()));
 }
}
