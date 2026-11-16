using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Leyra.Windows.Core;

public sealed record ServerProfile(string Host, int Port, string Token, string Certificate, string Fingerprint, string Role, string? DeviceId,string? UpdateKey=null)
{
    public Uri Address(string path) => new UriBuilder("https",Host,Port,path).Uri;
    public static ServerProfile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length>131072) throw new InvalidDataException("服务器连接文件超过上限");
        using var document=JsonDocument.Parse(data.ToArray());
        var value=document.RootElement.GetProperty("leyra_server");
        var host=value.GetProperty("host").GetString() ?? "";
        if (!AddressPolicy.IsLiteral(host)) throw new InvalidDataException("服务器必须提供明确的 IP 地址");
        var port=value.GetProperty("port").GetInt32();
        if (port is <1 or >65535) throw new InvalidDataException("服务器端口无效");
        var token=value.GetProperty("token").GetString() ?? "";
        if (!Regex.IsMatch(token,"^(?:[A-Za-z0-9_-]{32,128}|[a-f0-9]{32}\\.[A-Za-z0-9_-]{32,128})$",RegexOptions.CultureInvariant)) throw new InvalidDataException("管理凭据无效");
        var certificate=value.GetProperty("certificate").GetString() ?? "";
        var fingerprint=(value.GetProperty("fingerprint").GetString() ?? "").Replace(":","").ToLowerInvariant();
        if (!Regex.IsMatch(fingerprint,"^[a-f0-9]{64}$",RegexOptions.CultureInvariant)) throw new InvalidDataException("证书指纹无效");
        using var cert=X509Certificate2.CreateFromPem(certificate);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData),Convert.FromHexString(fingerprint))) throw new InvalidDataException("证书与指纹不符");
        var role=value.TryGetProperty("role",out var r) ? r.GetString() ?? "owner" : "owner";
        var id=value.TryGetProperty("device_id",out var d) ? d.GetString() : null;
        if (role!="owner" && role!="device") throw new InvalidDataException("服务器身份无效");
        if (role=="device" && !Guid.TryParse(id,out _)) throw new InvalidDataException("设备编号无效");
        var updateKey=value.TryGetProperty("update_key",out var update)?update.GetString():null;
        if(updateKey is not null){if(updateKey.Length>2048)throw new InvalidDataException("更新公钥无效");using var signing=ECDsa.Create();signing.ImportFromPem(updateKey);if(signing.KeySize!=256)throw new InvalidDataException("更新公钥无效");}
        return new(host,port,token,certificate,fingerprint,role,id,updateKey);
    }
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new {leyra_server=new {host=Host,port=Port,token=Token,certificate=Certificate,fingerprint=Fingerprint,role=Role,device_id=DeviceId,update_key=UpdateKey}});
    public bool AcceptCertificate(X509Certificate2? cert,DateTimeOffset now) => cert is not null && now>=cert.NotBefore.ToUniversalTime() && now<=cert.NotAfter.ToUniversalTime() && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData),Convert.FromHexString(Fingerprint));
}

public static class AddressPolicy
{
    public static bool IsLiteral(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Contains('%') || !IPAddress.TryParse(input,out var address)) return false;
        if (address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6) return input.Contains(':') && !address.IsIPv4MappedToIPv6;
        var parts=input.Split('.');
        return parts.Length==4 && parts.All(x=>Regex.IsMatch(x,"^(0|[1-9][0-9]{0,2})$",RegexOptions.CultureInvariant) && byte.TryParse(x,out _));
    }
    public static bool IsPublic(string input)
    {
        if (!IsLiteral(input)) return false;
        var address=IPAddress.Parse(input);if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6) return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.Equals(IPAddress.IPv6Any) && (address.GetAddressBytes()[0]&0xfe)!=0xfc;
        var b=address.GetAddressBytes();
        return b[0]!=0 && b[0]!=10 && b[0]!=127 && b[0]<224 && !(b[0]==172 && b[1]>=16 && b[1]<=31) && !(b[0]==192 && b[1]==168) && !(b[0]==169 && b[1]==254) && !(b[0]==100 && b[1]>=64 && b[1]<=127);
    }
}


