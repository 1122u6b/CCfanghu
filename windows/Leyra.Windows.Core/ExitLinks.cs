using System.Text.RegularExpressions;
namespace Leyra.Windows.Core;

/// <summary>One upstream the private gateway dials for this device's exit.</summary>
public sealed record ExitNode(string Type,string Server,int Port,string? Username,string? Password)
{
 public object Wire()=>Username is null?new{type=Type,server=Server,port=Port}:new{type=Type,server=Server,port=Port,username=Username,password=Password};
}

/// <summary>
/// Parses what residential proxy sellers hand out. The gateway relays through SOCKS5 or
/// HTTP CONNECT upstreams; anything else is reported back as skipped instead of guessed.
/// Accepted per line: socks5://[user:pass@]host:port, socks5h://, socks://, http://[user:pass@]host:port,
/// host:port:user:pass, user:pass@host:port, host:port.
/// </summary>
public static class ExitLinks
{
 public const int MaxNodes=32;
 static readonly Regex Host=new("^(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*|\\[[0-9A-Fa-f:.]+\\])$",RegexOptions.CultureInvariant);
 public static (List<ExitNode> Nodes,List<string> Skipped) Parse(string input)
 {
  if(input.Length>65536)throw new InvalidDataException("粘贴的内容太长");
  var nodes=new List<ExitNode>();var skipped=new List<string>();
  foreach(var raw in input.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
  {
   if(raw.StartsWith('#'))continue;
   var label=raw.Length>40?raw[..40]+"…":raw;
   var scheme=raw.Contains("://")?raw[..raw.IndexOf("://",StringComparison.Ordinal)].ToLowerInvariant():"";
   try
   {
    if(nodes.Count>=MaxNodes){skipped.Add(Mask(label)+"（超过 "+MaxNodes+" 条）");continue;}
    var node=scheme switch
    {
     "socks5" or "socks5h" or "socks" => FromUri(raw,"socks5"),
     "http" => FromUri(raw,"http"),
     "" => FromPlain(raw),
     "https" => throw new FormatException("暂不支持 HTTPS 代理"),
     _ => throw new FormatException("自有网关暂不支持 "+scheme+" 协议，请向卖家要 SOCKS5 或 HTTP 格式")
    };
    if(nodes.Any(x=>x==node)){skipped.Add(Mask(label)+"（重复）");continue;}
    nodes.Add(node);
   }
   catch(FormatException e){skipped.Add(Mask(label)+"（"+e.Message+"）");}
  }
  return (nodes,skipped);
 }
 static string Mask(string label)
 {
  // Never echo passwords back into toasts or tickets.
  var at=label.LastIndexOf('@');if(at>0){var start=label.IndexOf("://",StringComparison.Ordinal);start=start<0?0:start+3;return label[..start]+"***@"+label[(at+1)..];}
  var parts=label.Split(':');return parts.Length==4?parts[0]+":"+parts[1]+":***":label;
 }
 static ExitNode FromUri(string raw,string type)
 {
  if(!Regex.IsMatch(raw,@":[0-9]{1,5}/?$",RegexOptions.CultureInvariant))throw new FormatException("链接需写明端口");
  if(!Uri.TryCreate(raw,UriKind.Absolute,out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Port is <1 or >65535)throw new FormatException("地址或端口无效");
  if(uri.AbsolutePath is not ("/" or "") || uri.Query.Length>0)throw new FormatException("链接中不能带路径或参数");
  string? user=null,pass=null;
  if(uri.UserInfo.Length>0)
  {
   var info=uri.UserInfo.Split(':',2);if(info.Length!=2||info[0].Length==0||info[1].Length==0)throw new FormatException("缺少用户名或密码");
   user=Uri.UnescapeDataString(info[0]);pass=Uri.UnescapeDataString(info[1]);
  }
  return Build(type,uri.Host,uri.Port,user,pass);
 }
 static ExitNode FromPlain(string raw)
 {
  string? user=null,pass=null;var rest=raw;
  var at=raw.LastIndexOf('@');
  if(at>0){var info=raw[..at].Split(':',2);if(info.Length!=2)throw new FormatException("缺少用户名或密码");user=info[0];pass=info[1];rest=raw[(at+1)..];}
  var parts=rest.Split(':');
  if(at<0 && parts.Length==4){user=parts[2];pass=parts[3];parts=parts[..2];}
  if(parts.Length!=2 || !int.TryParse(parts[1],out var port))throw new FormatException("请用 主机:端口:用户名:密码 或 socks5:// 链接格式");
  return Build("socks5",parts[0],port,user,pass);
 }
 static ExitNode Build(string type,string host,int port,string? user,string? pass)
 {
  host=host.Trim('[',']');
  if(!(Host.IsMatch(host)||AddressPolicy.IsLiteral(host)) || host.Length>253)throw new FormatException("主机名无效");
  if(port is <1 or >65535)throw new FormatException("端口无效");
  if(AddressPolicy.IsLiteral(host) && !AddressPolicy.IsPublic(host))throw new FormatException("出口必须是公网地址");
  if(user is not null && (user.Length is 0 or >255 || pass is null || pass.Length is 0 or >255 || (user+pass).Any(char.IsControl)))throw new FormatException("用户名或密码无效");
  return new(type,host,port,user,pass);
 }
}
