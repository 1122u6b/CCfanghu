using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
namespace Leyra.Windows.Core;

public sealed class PrivateServerClient : IDisposable
{
    readonly HttpClient http;
    readonly ServerProfile profile;
    readonly TimeSpan timeout;
    public PrivateServerClient(ServerProfile profile,TimeSpan? requestTimeout=null,bool throughLocalProxy=false)
    {
        this.profile=profile;timeout=requestTimeout??TimeSpan.FromSeconds(15);
        if(timeout<TimeSpan.FromMilliseconds(100) || timeout>TimeSpan.FromSeconds(60))throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        var handler=new HttpClientHandler { UseProxy=throughLocalProxy,Proxy=throughLocalProxy?new WebProxy("http://127.0.0.1:17890"):null,AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.None };
        handler.ServerCertificateCustomValidationCallback=(_,cert,_,_)=>profile.AcceptCertificate(cert,DateTimeOffset.UtcNow);
        http=new HttpClient(handler) {Timeout=timeout};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",profile.Token);
    }
    public async Task<JsonElement> SendAsync(string path,object? body=null,CancellationToken cancellation=default)
    {
        if (path!="/api/v1/config" && path!="/api/v1/status" && path!="/api/v1/rpc" && path!="/api/v1/egress-probe") throw new InvalidOperationException("不允许的服务接口");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(timeout);
        var bounded=deadline.Token;
        using var request=new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,profile.Address(path));
        if (body is not null)
        {
            var encoded=JsonSerializer.SerializeToUtf8Bytes(body);
            if (encoded.Length>131072) throw new InvalidDataException("请求超过上限");
            request.Content=new ByteArrayContent(encoded);request.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        }
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,bounded);
        if (!response.IsSuccessStatusCode)
        {
            var detail="";
            if (response.StatusCode==HttpStatusCode.BadRequest)
            {
                try
                {
                    var raw=await response.Content.ReadAsByteArrayAsync(bounded);
                    if (raw.Length<=8192) {using var error=JsonDocument.Parse(raw);if (error.RootElement.TryGetProperty("detail",out var d)&&d.ValueKind==JsonValueKind.String) detail=d.GetString()??"";}
                }
                catch (JsonException) {}
            }
            throw new HttpRequestException(detail.Length>0&&detail.Length<=300&&!detail.Any(char.IsControl)?detail:"自建服务器请求失败，HTTP "+(int)response.StatusCode,null,response.StatusCode);
        }
        if (response.Content.Headers.ContentLength>2*1024*1024) throw new InvalidDataException("响应超过上限");
        await using var input=await response.Content.ReadAsStreamAsync(bounded);using var memory=new MemoryStream();var buffer=new byte[16384];int read;
        while ((read=await input.ReadAsync(buffer,bounded))>0)
        {
            if (memory.Length+read>2*1024*1024) throw new InvalidDataException("响应超过上限");
            memory.Write(buffer,0,read);
        }
        using var result=JsonDocument.Parse(memory.ToArray());
        if (result.RootElement.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("响应格式无效");
        return result.RootElement.Clone();
    }
    public Task<JsonElement> RpcAsync(object request,CancellationToken cancellation=default) => SendAsync("/api/v1/rpc",request,cancellation);
    public async Task<(ServerProfile Profile,JsonElement Nodes)> EnrollAsync(Guid installation,string label,CancellationToken cancellation=default)
    {
        var result=await SendAsync("/api/v1/config",cancellation:cancellation);
        var identity=result.GetProperty("identity");var role=identity.GetProperty("role").GetString();var effective=profile;
        if (role=="owner")
        {
            var enrolled=await RpcAsync(new { type="create_device",name=label },cancellation);
            var id=enrolled.GetProperty("id").GetString();if (!Guid.TryParse(id,out _)) throw new InvalidDataException("设备登记回执无效");
            var nodeProfile=profile with {Token=enrolled.GetProperty("token").GetString() ?? "",Role="device",DeviceId=id};
            effective=ServerProfile.Parse(nodeProfile.Encode());
            using var device=new PrivateServerClient(effective);result=await device.SendAsync("/api/v1/config",cancellation:cancellation);
        }
        else if (role=="device")
        {
            effective=ServerProfile.Parse((profile with {Role="device",DeviceId=identity.GetProperty("id").GetString()}).Encode());
        }
        else throw new InvalidDataException("服务器尚未提供独立设备身份");
        if (result.GetProperty("identity").GetProperty("role").GetString()!="device" || result.GetProperty("identity").GetProperty("id").GetString()!=effective.DeviceId) throw new InvalidDataException("未取得独立设备节点");
        var nodes=result.GetProperty("proxies");if (nodes.ValueKind!=JsonValueKind.Array || nodes.GetArrayLength()==0) throw new InvalidDataException("未取得节点");
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.GetProperty("server").GetString()!=effective.Host || node.GetProperty("port").GetInt32()!=effective.Port || !node.GetProperty("tls").GetBoolean() || node.GetProperty("fingerprint").GetString()!=effective.Fingerprint) throw new InvalidDataException("节点与已固定的服务器身份不符");
        }
        return (effective,nodes.Clone());
    }
    public void Dispose()=>http.Dispose();
}

