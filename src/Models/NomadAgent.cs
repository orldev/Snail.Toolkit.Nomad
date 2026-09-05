namespace Snail.Toolkit.Nomad.Models;

/// <summary>The agent behind the address, from /v1/agent/self.</summary>
public sealed record NomadAgent(
    string Name,
    string Version,
    string Datacenter,
    string Region,
    bool ServerEnabled,
    bool ClientEnabled);

/// <summary>What /v1/agent/self answers, as it is written on the wire.</summary>
/// <remarks>
/// Every part is optional: an ACL that hides the configuration still answers, and reading the
/// document by hand turned a missing section into a <see cref="KeyNotFoundException"/> escaping
/// a client that promises only <see cref="NomadException"/>.
/// </remarks>
internal sealed record AgentSelf(AgentConfig? Config = null, AgentMember? Member = null);

/// <summary>The configuration section of /v1/agent/self.</summary>
internal sealed record AgentConfig(
    AgentVersion? Version = null,
    string? Datacenter = null,
    string? Region = null,
    AgentToggle? Server = null,
    AgentToggle? Client = null);

/// <summary>The nested version block of the agent configuration.</summary>
internal sealed record AgentVersion(string? Version = null);

/// <summary>A server or client block, which carries whether that half of the agent runs.</summary>
internal sealed record AgentToggle(bool Enabled = false);

/// <summary>The gossip member behind the agent.</summary>
internal sealed record AgentMember(string? Name = null);
