using System.Buffers.Binary;
using System.Text.Json;
namespace Leyra.Windows.Core;

public static class PipeProtocol
{
 public const string Name="LeyraConnect.Product.v1";
 public const int MaximumFrame=1024*1024;
 public static async Task WriteAsync(Stream stream,object value,CancellationToken cancellation=default)
 {
  var payload=JsonSerializer.SerializeToUtf8Bytes(value);
  if(payload.Length==0 || payload.Length>MaximumFrame)throw new InvalidDataException("本机通信内容超过上限");
  var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,payload.Length);
  await stream.WriteAsync(header,cancellation);await stream.WriteAsync(payload,cancellation);await stream.FlushAsync(cancellation);
 }
 public static async Task<JsonElement> ReadAsync(Stream stream,CancellationToken cancellation=default)
 {
  var header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);
  var size=BinaryPrimitives.ReadInt32LittleEndian(header);
  if(size<=0 || size>MaximumFrame)throw new InvalidDataException("本机通信帧长度无效");
  var payload=new byte[size];await stream.ReadExactlyAsync(payload,cancellation);
  using var document=JsonDocument.Parse(payload,new JsonDocumentOptions{MaxDepth=32});
  if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("本机通信响应必须为对象");
  return document.RootElement.Clone();
 }
}
