namespace Snail.Toolkit.Nomad.Models;

/// <summary>Which stream of a task to read.</summary>
public enum LogType
{
    /// <summary>Standard output of the task.</summary>
    Stdout,

    /// <summary>Standard error of the task.</summary>
    Stderr
}
