using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Snail.Toolkit.Nomad.Models;

namespace Snail.Toolkit.Nomad;

/// <summary>The serializer metadata for everything this client reads and writes.</summary>
/// <remarks>
/// Generated rather than reflected so the models survive trimming, and so the two Respect settings
/// are stated once next to the types they judge.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AgentSelf))]
[JsonSerializable(typeof(JobRegistration))]
[JsonSerializable(typeof(JobSummary))]
[JsonSerializable(typeof(NomadJob))]
[JsonSerializable(typeof(List<JobStub>))]
[JsonSerializable(typeof(List<Allocation>))]
[JsonSerializable(typeof(List<Evaluation>))]
[JsonSerializable(typeof(List<NodeStub>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(string))]
internal sealed partial class NomadJson : JsonSerializerContext;
