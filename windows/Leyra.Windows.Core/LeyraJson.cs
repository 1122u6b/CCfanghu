using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Leyra.Windows.Core;

/// <summary>Writes Leyra's own objects (mostly anonymous types built for the window, the server and mihomo) as JSON
/// without System.Text.Json's reflection serializer, which needs code generated at run time and so cannot run once
/// the service is compiled to machine code. Public instance properties are written in declaration order, the same
/// output System.Text.Json gave: numbers as numbers, enums as numbers, dates as ISO 8601, byte[] as base64.</summary>
public static class LeyraJson
{
 public static byte[] ToUtf8(object? value,bool indented=false)
 {
  using var buffer=new MemoryStream();
  using(var writer=new Utf8JsonWriter(buffer,new JsonWriterOptions{Indented=indented,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.Default}))Write(writer,value,0);
  return buffer.ToArray();
 }
 public static string ToText(object? value)=>System.Text.Encoding.UTF8.GetString(ToUtf8(value));
 public static JsonElement ToElement(object? value){using var doc=JsonDocument.Parse(ToUtf8(value));return doc.RootElement.Clone();}

 static void Write(Utf8JsonWriter w,object? value,int depth)
 {
  if(depth>64)throw new InvalidOperationException("JSON nesting too deep");
  switch(value)
  {
   case null:w.WriteNullValue();return;
   case string s:w.WriteStringValue(s);return;
   case bool b:w.WriteBooleanValue(b);return;
   case char c:w.WriteStringValue(c.ToString());return;
   case int i:w.WriteNumberValue(i);return;
   case long l:w.WriteNumberValue(l);return;
   case uint ui:w.WriteNumberValue(ui);return;
   case ulong ul:w.WriteNumberValue(ul);return;
   case short sh:w.WriteNumberValue(sh);return;
   case ushort us:w.WriteNumberValue(us);return;
   case byte by:w.WriteNumberValue(by);return;
   case sbyte sb:w.WriteNumberValue(sb);return;
   case double d:w.WriteNumberValue(d);return;
   case float f:w.WriteNumberValue(f);return;
   case decimal m:w.WriteNumberValue(m);return;
   case DateTimeOffset dto:w.WriteStringValue(dto);return;
   case DateTime dt:w.WriteStringValue(dt);return;
   case TimeSpan ts:w.WriteStringValue(ts.ToString("c",CultureInfo.InvariantCulture));return;
   case Guid g:w.WriteStringValue(g);return;
   case Uri u:w.WriteStringValue(u.OriginalString);return;
   case Version v:w.WriteStringValue(v.ToString());return;
   case JsonElement e:e.WriteTo(w);return;
   case JsonDocument jd:jd.WriteTo(w);return;
   case JsonNode n:n.WriteTo(w);return;
   case byte[] bytes:w.WriteBase64StringValue(bytes);return;
   case Enum en:w.WriteNumberValue(Convert.ToInt64(en,CultureInfo.InvariantCulture));return;
   case IDictionary dict:
    w.WriteStartObject();
    foreach(DictionaryEntry entry in dict){w.WritePropertyName(Convert.ToString(entry.Key,CultureInfo.InvariantCulture)??"");Write(w,entry.Value,depth+1);}
    w.WriteEndObject();return;
   case IEnumerable list:
    w.WriteStartArray();foreach(var item in list)Write(w,item,depth+1);w.WriteEndArray();return;
  }
  var properties=Properties(value.GetType());
  // No properties: the type was not kept in the machine-code build (or is not one of Leyra's). Fail loudly rather
  // than send "{}" to the window or the server.
  if(properties.Length==0)throw new NotSupportedException("JSON: cannot write "+value.GetType().FullName);
  w.WriteStartObject();
  foreach(var p in properties){w.WritePropertyName(p.Name);Write(w,p.GetValue(value),depth+1);}
  w.WriteEndObject();
 }
 static readonly System.Collections.Concurrent.ConcurrentDictionary<Type,PropertyInfo[]> cache=new();
 // Leyra's own types keep their properties in the machine-code build (the assemblies are rooted in the project files).
 [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming","IL2070",Justification="Leyra's assemblies are rooted")]
 static PropertyInfo[] Properties(Type type)=>cache.GetOrAdd(type,t=>t.GetProperties(BindingFlags.Instance|BindingFlags.Public)
  .Where(p=>p.CanRead&&p.GetIndexParameters().Length==0&&p.GetMethod is {IsPublic:true}&&p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
  .ToArray());
}
