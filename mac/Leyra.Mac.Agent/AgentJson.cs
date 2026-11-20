using System.Text.Json.Serialization;
namespace Leyra.Mac.Agent;
/// <summary>Files this service reads back (settings, saved browser, proxy and DNS settings). Generated at build time:
/// the machine-code build has no run-time JSON reflection. Everything it writes goes through LeyraJson.</summary>
[JsonSerializable(typeof(AgentSettings))]
[JsonSerializable(typeof(List<BrowserPolicies.Prior>),TypeInfoPropertyName="ListPrior")]
[JsonSerializable(typeof(List<CoexistProxy.Setting>),TypeInfoPropertyName="ListSetting")]
[JsonSerializable(typeof(Dictionary<string,string[]>),TypeInfoPropertyName="DictionaryStringStringArray")]
internal partial class AgentJson:JsonSerializerContext{}
