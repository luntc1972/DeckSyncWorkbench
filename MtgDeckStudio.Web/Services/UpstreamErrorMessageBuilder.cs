using System.Net;
using MtgDeckStudio.Web.Models;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Builds user-facing error messages for failures coming from third-party upstream services.
/// </summary>
public static class UpstreamErrorMessageBuilder
{
    /// <summary>
    /// Builds a deck-sync error message that highlights the upstream site when possible.
    /// </summary>
    /// <param name="request">Original deck sync request.</param>
    /// <param name="exception">Failure to translate.</param>
    public static string BuildDeckSyncMessage(DeckDiffRequest request, Exception exception)
    {
        if (IsMoxfieldForbidden(request, exception))
        {
            return "Moxfield blocked the deck URL request from this local web app with HTTP 403. Paste the Moxfield export text into the form instead, or run the compare from the CLI/WSL environment where URL fetches succeed.";
        }

        return BuildSiteSpecificMessage(exception) ?? exception.Message;
    }

    /// <summary>
    /// Builds a category suggestion error message that highlights the failing site when possible.
    /// </summary>
    /// <param name="exception">Failure to translate.</param>
    public static string BuildSuggestionMessage(Exception exception)
        => BuildSiteSpecificMessage(exception) ?? exception.Message;

    /// <summary>
    /// Builds a commander category error message that highlights the failing site when possible.
    /// </summary>
    /// <param name="exception">Failure to translate.</param>
    public static string BuildCommanderMessage(Exception exception)
        => BuildSiteSpecificMessage(exception) ?? "Archidekt could not be reached right now. Try again shortly.";

    /// <summary>
    /// Builds a Scryfall-specific message for autocomplete failures.
    /// </summary>
    /// <param name="exception">Failure to translate.</param>
    public static string BuildScryfallMessage(Exception exception)
        => BuildSiteSpecificMessage(exception) ?? "Scryfall could not be reached right now. Try again shortly.";

    private static string? BuildSiteSpecificMessage(Exception exception)
    {
        var site = DetectSite(exception);
        if (site is null)
        {
            return null;
        }

        var statusCode = TryGetStatusCode(exception);
        if (statusCode is not null)
        {
            return $"{site} returned HTTP {(int)statusCode.Value}. Try again shortly.";
        }

        return $"{site} could not be reached right now. Try again shortly.";
    }

    private static string? DetectSite(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("moxfield", StringComparison.OrdinalIgnoreCase))
        {
            return "Moxfield";
        }

        if (message.Contains("archidekt", StringComparison.OrdinalIgnoreCase))
        {
            return "Archidekt";
        }

        if (message.Contains("edhrec", StringComparison.OrdinalIgnoreCase))
        {
            return "EDHREC";
        }

        if (message.Contains("scryfall", StringComparison.OrdinalIgnoreCase))
        {
            return "Scryfall";
        }

        return null;
    }

    private static HttpStatusCode? TryGetStatusCode(Exception exception)
    {
        if (exception is HttpRequestException httpRequestException && httpRequestException.StatusCode is not null)
        {
            return httpRequestException.StatusCode.Value;
        }

        return null;
    }

    private static bool IsMoxfieldForbidden(DeckDiffRequest request, Exception exception)
    {
        return request.MoxfieldInputSource == DeckInputSource.PublicUrl
            && !string.IsNullOrWhiteSpace(request.MoxfieldUrl)
            && exception is HttpRequestException httpException
            && httpException.StatusCode == HttpStatusCode.Forbidden;
    }
}
