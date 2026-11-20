using System.Buffers.Binary;
using System.Net;
namespace Leyra.Windows.Core;
[System.Reflection.Obfuscation(Exclude=true,ApplyToMembers=true)] public sealed record DirectRule(string Cidr,string Note,bool Public)
{
    public static DirectRule Parse(string cidr,string note,bool confirmPublic)
    {
        if (note.Length>200 || note.Any(c=>char.IsControl(c)))throw new InvalidDataException("备注无效");
        var pieces=cidr.Split('/');if (pieces.Length>2 || !AddressPolicy.IsLiteral(pieces[0]) || pieces[0].Contains(':'))throw new InvalidDataException("仅接受明确的 IPv4 地址或网段");
        var prefix=32;
        if (pieces.Length==2 && (!int.TryParse(pieces[1],out prefix) || prefix<8 || prefix>32))throw new InvalidDataException("网段须为 /8 到 /32");
        var raw=BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(pieces[0]).GetAddressBytes());var mask=uint.MaxValue<<(32-prefix);var network=raw&mask;
        var bytes=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(bytes,network);var address=new IPAddress(bytes).ToString();
        if (bytes[0] is 0 or 127 || bytes[0]>=224 || (bytes[0]==169 && bytes[1]==254))throw new InvalidDataException("不能放开保留地址范围");
        var local=(network>>24)==10 || (prefix>=12 && network>=0xac100000 && network<=0xac1fffff) || (prefix>=16 && (network>>16)==0xc0a8);
        if (!local && !confirmPublic)throw new InvalidDataException("公网直连需要明确确认");
        return new(address+"/"+prefix,note,!local);
    }
}

