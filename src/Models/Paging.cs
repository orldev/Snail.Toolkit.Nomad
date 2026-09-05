namespace Snail.Toolkit.Nomad.Models;

/// <summary>How much of a list to ask for, and where to continue from.</summary>
/// <param name="PageSize">Entries per page; Nomad decides for itself when this is not positive.</param>
/// <param name="Token">Continues an earlier page, from <see cref="NomadPage{T}.NextToken"/>.</param>
public readonly record struct NomadPaging(int PageSize, string? Token = null);

/// <summary>One page of a Nomad list, with what it takes to read the next.</summary>
/// <remarks>
/// Nomad answers a paged list with the continuation in the <c>X-Nomad-NextToken</c> header rather
/// than in the body, so the token cannot be recovered from the items alone.
/// </remarks>
public sealed record NomadPage<T>(IReadOnlyList<T> Items, string? NextToken = null)
{
    /// <summary>Another page is waiting behind <see cref="NextToken"/>.</summary>
    public bool HasMore => NextToken is not null;
}
