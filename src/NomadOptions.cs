namespace Snail.Toolkit.Nomad;

/// <summary>Connection settings bound from the "Nomad" configuration section.</summary>
public sealed class NomadOptions
{
    /// <summary>Name of the configuration section the options are bound from.</summary>
    public const string SectionName = "Nomad";

    /// <summary>Base address of the Nomad HTTP API, e.g. "http://localhost:4646".</summary>
    /// <remarks>
    /// Required, and deliberately without a default: falling back to localhost would let a
    /// deployment that forgot to configure Nomad talk to whatever answers on the same machine.
    /// </remarks>
    public string Address { get; set; } = "";

    /// <summary>ACL token sent as X-Nomad-Token on every request; none by default.</summary>
    public string? Token { get; set; }

    /// <summary>Namespace every request is scoped to; the server default ("default") when empty.</summary>
    public string? Namespace { get; set; }

    /// <summary>Region every request is routed to; the local region when empty.</summary>
    public string? Region { get; set; }

    /// <summary>Timeout of one HTTP call.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
