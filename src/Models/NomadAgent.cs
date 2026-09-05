namespace Snail.Toolkit.Nomad.Models;

/// <summary>The agent behind the address, from /v1/agent/self.</summary>
public sealed record NomadAgent(
    string Name,
    string Version,
    string Datacenter,
    string Region,
    bool ServerEnabled,
    bool ClientEnabled);
