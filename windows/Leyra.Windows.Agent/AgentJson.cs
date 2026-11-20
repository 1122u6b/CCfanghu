using System.Text.Json.Serialization;
namespace Leyra.Windows.Agent;
/// <summary>Files this service reads back (settings, saved browser and proxy settings). Generated at build time:
/// the machine-code build has no run-time JSON reflection. Everything it writes goes through LeyraJson.</summary>
[JsonSerializable(typeof(AgentSettings))]
[JsonSerializable(typeof(List<BrowserPolicies.Prior>),TypeInfoPropertyName="ListPrior")]
[JsonSerializable(typeof(CoexistProxy.Saved))]
internal partial class AgentJson:JsonSerializerContext{}
