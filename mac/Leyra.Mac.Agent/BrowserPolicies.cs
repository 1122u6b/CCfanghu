using Microsoft.Win32;
using System.Text.Json;

namespace Leyra.Mac.Agent;

static class BrowserPolicies
{
 static readonly string[] Paths={@"SOFTWARE\Policies\Google\Chrome",@"SOFTWARE\Policies\Microsoft\Edge"};
 record Prior(string Path,string Name,bool Present,RegistryValueKind Kind,object? Value);
 static string FileName=>Path.Combine(AgentStore.Root,"browser-policies.json");
 public static object Install()
 {
  if(!File.Exists(FileName))
  {
   var prior=new List<Prior>();
   foreach(var path in Paths)using(var key=Registry.LocalMachine.OpenSubKey(path))foreach(var name in new[]{"WebRtcIPHandling","QuicAllowed"})
   {var present=key?.GetValueNames().Contains(name)==true;prior.Add(new(path,name,present,present?key!.GetValueKind(name):RegistryValueKind.String,present?key!.GetValue(name):null));}
   AgentStore.Atomic(FileName,JsonSerializer.SerializeToUtf8Bytes(prior));
  }
  foreach(var path in Paths)using(var key=Registry.LocalMachine.CreateSubKey(path)){key.SetValue("WebRtcIPHandling","disable_non_proxied_udp",RegistryValueKind.String);key.SetValue("QuicAllowed",0,RegistryValueKind.DWord);}
  return new{saved=true,restart_browser_required=true};
 }
 public static void Restore()
 {
  if(!File.Exists(FileName))return;
  var prior=JsonSerializer.Deserialize<List<Prior>>(System.IO.File.ReadAllBytes(FileName))??throw new InvalidDataException("浏览器策略恢复记录无效");
  foreach(var item in prior)
  {
   if(!Paths.Contains(item.Path)||item.Name is not ("WebRtcIPHandling" or "QuicAllowed"))throw new InvalidDataException("浏览器策略恢复项无效");
   using var key=Registry.LocalMachine.OpenSubKey(item.Path,true);if(key is null)continue;
   var current=key.GetValue(item.Name);var ours=item.Name=="QuicAllowed"?current is int currentNumber&&currentNumber==0:current as string=="disable_non_proxied_udp";
   if(!ours)continue;
   if(!item.Present)key.DeleteValue(item.Name,false);
   else if(item.Value is JsonElement json)
   {
    object value=item.Kind switch{RegistryValueKind.DWord=>json.GetInt32(),RegistryValueKind.QWord=>json.GetInt64(),RegistryValueKind.MultiString=>json.EnumerateArray().Select(x=>x.GetString()??"").ToArray(),RegistryValueKind.Binary=>json.GetBytesFromBase64(),_=>json.GetString()??""};key.SetValue(item.Name,value,item.Kind);
   }
  }
  System.IO.File.Delete(FileName);
 }
}
