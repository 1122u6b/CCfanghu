using Microsoft.Win32;
namespace Leyra.Windows.Platform;

public static class BrowserCatalog
{
 public static Dictionary<string,string> Find(string? ownerSid=null)
 {
  var result=new Dictionary<string,string>();
  var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  if(ownerSid is not null)
  {
   using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\"+ownerSid);
   if(key?.GetValue("ProfileImagePath") is string profile)local=Path.Combine(Environment.ExpandEnvironmentVariables(profile),"AppData","Local");
  }
  var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),local};
  foreach(var (name,relative) in new[]{("Google Chrome",@"Google\Chrome\Application\chrome.exe"),("Microsoft Edge",@"Microsoft\Edge\Application\msedge.exe"),("Mozilla Firefox",@"Mozilla Firefox\firefox.exe")})
   foreach(var root in roots){var path=Path.Combine(root,relative);if(File.Exists(path)){result[name]=path;break;}}
  return result;
 }
}
