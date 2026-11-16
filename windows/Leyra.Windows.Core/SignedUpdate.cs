using System.Security.Cryptography;
using System.Text.Json;
namespace Leyra.Windows.Core;
public sealed record SignedUpdate(string Version,string Sha256,long Size)
{
 public static SignedUpdate Verify(JsonElement envelope,string publicKey)
 {
  var payload=Convert.FromBase64String(envelope.GetProperty("payload").GetString()??"");var signature=Convert.FromBase64String(envelope.GetProperty("signature").GetString()??"");
  if(payload.Length>2048||signature.Length>256)throw new InvalidDataException("更新签名无效");
  using var key=ECDsa.Create();key.ImportFromPem(publicKey);
  if(!key.VerifyData(payload,signature,HashAlgorithmName.SHA256,DSASignatureFormat.Rfc3279DerSequence))throw new InvalidDataException("更新签名校验失败");
  using var json=JsonDocument.Parse(payload);var root=json.RootElement;var version=root.GetProperty("version").GetString()??"";var digest=root.GetProperty("sha256").GetString()??"";var size=root.GetProperty("size").GetInt64();
  if(!System.Version.TryParse(version.Split('-')[0],out _)||digest.Length!=64||digest.Any(x=>!Uri.IsHexDigit(x))||size is <1024 or >536870912)throw new InvalidDataException("更新信息无效");
  return new(version,digest.ToLowerInvariant(),size);
 }
 public bool IsNewer(string current)=>System.Version.Parse(Version.Split('-')[0])>System.Version.Parse(current.Split('-')[0]);
 public bool Matches(string file){using var input=File.OpenRead(file);return input.Length==Size&&CryptographicOperations.FixedTimeEquals(SHA256.HashData(input),Convert.FromHexString(Sha256));}
}
