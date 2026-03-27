namespace DeckSyncWorkbench.Web.Models;

/// <summary>
/// Represents a mechanic rules lookup request.
/// </summary>
public sealed class MechanicLookupRequest
{
    /// <summary>
    /// Gets or sets the mechanic or rules term to look up.
    /// </summary>
    public string MechanicName { get; set; } = string.Empty;
}
