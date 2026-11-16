using System.Security.Cryptography;
using System.Text;
namespace Leyra.Windows.Platform;
public static class WfpOwnership
{
 public static readonly Guid Provider=Key("provider-v1"),SubLayer=Key("sublayer-v1");
 public static readonly Guid Layer4=new("c38d57d1-05a7-4c33-904f-7fbceee60e82"),Layer6=new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
 public static Guid Filter(bool ipv6,int slot){if(slot<0||slot>=512)throw new ArgumentOutOfRangeException(nameof(slot));return Key("filter-v1-"+(ipv6?"6":"4")+"-"+slot);}
 static Guid Key(string name)=>new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("local.leyraconnect.windows/"+name)).AsSpan(0,16));
}

