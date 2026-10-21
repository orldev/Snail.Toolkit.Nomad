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

    /// <summary>How long a watch asks Nomad to hold a request open while nothing changes.</summary>
    /// <remarks>
    /// Has to stay under <see cref="Timeout"/> with room to spare: Nomad adds jitter of up to a
    /// sixteenth of this on top, and the client's own timeout aborts the call regardless.
    /// </remarks>
    public TimeSpan WatchWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a watch pauses before asking again after a transport failure; none, and the watch throws instead.</summary>
    /// <remarks>
    /// A blocking query resumes from the index it last saw: Nomad answers at once with whatever changed in
    /// the pause, and a job purged in the pause ends the watch as it would have. So a follower that is
    /// meant to live as long as its token survives a dropped connection, a timeout or a server answering
    /// 5xx while it elects a leader, and tries again every <see cref="WatchRetry"/> until the token is
    /// cancelled. Off by default: a caller that wants to see the failure sees it.
    /// </remarks>
    public TimeSpan? WatchRetry { get; set; }
}
