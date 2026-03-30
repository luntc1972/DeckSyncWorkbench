using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using RestSharp;

namespace MtgDeckStudio.Web.Services;

/// <summary>
/// Looks up official Magic mechanic rules from the current Wizards of the Coast Comprehensive Rules text.
/// </summary>
public interface IMechanicLookupService
{
    /// <summary>
    /// Finds official rules text for a mechanic or related rules term.
    /// </summary>
    /// <param name="mechanicName">Mechanic or rules term to search for.</param>
    /// <param name="cancellationToken">Cancellation token for the request.</param>
    Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a mechanic lookup result from the official rules source.
/// </summary>
/// <param name="Query">Original user query.</param>
/// <param name="Found">Whether a matching mechanic entry was found.</param>
/// <param name="MechanicName">Matched mechanic or rules term name.</param>
/// <param name="RuleReference">Primary rule reference for the result.</param>
/// <param name="MatchType">Explains how the match was found.</param>
/// <param name="RulesText">Official rules text snippet returned for the match.</param>
/// <param name="SummaryText">Short explanatory summary when available.</param>
/// <param name="RulesPageUrl">Official Wizards rules page URL.</param>
/// <param name="RulesTextUrl">Direct URL to the current Comprehensive Rules text file.</param>
public sealed record MechanicLookupResult(
    string Query,
    bool Found,
    string? MechanicName,
    string? RuleReference,
    string? MatchType,
    string? RulesText,
    string? SummaryText,
    string RulesPageUrl,
    string? RulesTextUrl)
{
    /// <summary>
    /// Creates an empty mechanic lookup result.
    /// </summary>
    public static MechanicLookupResult NotFound(string query, string rulesPageUrl, string? rulesTextUrl)
        => new(query, false, null, null, null, null, null, rulesPageUrl, rulesTextUrl);
}

/// <summary>
/// Resolves mechanic rules from the official Wizards rules page and Comprehensive Rules text file.
/// </summary>
public sealed partial class WotcMechanicLookupService : IMechanicLookupService
{
    private const string RulesPageUrl = "https://magic.wizards.com/en/rules";
    private const string RulesCacheKey = "wotc-mechanic-rules-document";
    private static readonly TimeSpan RulesCacheDuration = TimeSpan.FromHours(6);
    private static readonly Regex RulesTextUrlRegex = RulesTextUrlPattern();
    private static readonly Regex SectionHeaderRegex = SectionHeaderPattern();
    private static readonly Regex RuleLineRegex = RuleLinePattern();
    private readonly IMemoryCache _memoryCache;
    private readonly Func<string, CancellationToken, Task<string>> _fetchStringAsync;

    /// <summary>
    /// Creates a lookup service that fetches and caches the current official rules files.
    /// </summary>
    public WotcMechanicLookupService(
        IMemoryCache memoryCache,
        Func<string, CancellationToken, Task<string>>? fetchStringAsync = null)
    {
        _memoryCache = memoryCache;
        _fetchStringAsync = fetchStringAsync ?? FetchStringAsync;
    }

    /// <summary>
    /// Looks up the requested mechanic in the official rules text.
    /// </summary>
    public async Task<MechanicLookupResult> LookupAsync(string mechanicName, CancellationToken cancellationToken = default)
    {
        var trimmedName = mechanicName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new InvalidOperationException("A mechanic name is required.");
        }

        var rulesDocument = await GetRulesDocumentAsync(cancellationToken);
        var normalizedQuery = Normalize(trimmedName);

        var sectionMatch = rulesDocument.Sections.FirstOrDefault(section => Normalize(section.Title) == normalizedQuery);
        if (sectionMatch is not null)
        {
            var glossaryMatch = rulesDocument.GlossaryEntries.FirstOrDefault(entry => Normalize(entry.Title) == normalizedQuery);
            return new MechanicLookupResult(
                trimmedName,
                true,
                sectionMatch.Title,
                sectionMatch.RuleReference,
                "Exact rules section",
                sectionMatch.RulesText,
                glossaryMatch?.Description,
                RulesPageUrl,
                rulesDocument.RulesTextUrl);
        }

        var glossaryEntry = rulesDocument.GlossaryEntries.FirstOrDefault(entry => Normalize(entry.Title) == normalizedQuery);
        if (glossaryEntry is not null)
        {
            return new MechanicLookupResult(
                trimmedName,
                true,
                glossaryEntry.Title,
                glossaryEntry.RuleReference,
                "Glossary entry",
                glossaryEntry.RulesText,
                glossaryEntry.Description,
                RulesPageUrl,
                rulesDocument.RulesTextUrl);
        }

        var ruleLineMatch = rulesDocument.RuleLines.FirstOrDefault(line => ContainsTerm(line.Text, trimmedName));
        if (ruleLineMatch is not null)
        {
            return new MechanicLookupResult(
                trimmedName,
                true,
                trimmedName,
                ruleLineMatch.RuleReference,
                "Referenced in rule text",
                $"{ruleLineMatch.RuleReference} {ruleLineMatch.Text}",
                null,
                RulesPageUrl,
                rulesDocument.RulesTextUrl);
        }

        return MechanicLookupResult.NotFound(trimmedName, RulesPageUrl, rulesDocument.RulesTextUrl);
    }

    /// <summary>
    /// Loads and caches the current Comprehensive Rules document.
    /// </summary>
    private async Task<RulesDocument> GetRulesDocumentAsync(CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue<RulesDocument>(RulesCacheKey, out var cachedDocument) && cachedDocument is not null)
        {
            return cachedDocument;
        }

        var rulesPage = await _fetchStringAsync(RulesPageUrl, cancellationToken);
        var rulesTextUrl = ExtractRulesTextUrl(rulesPage);
        var rulesText = await _fetchStringAsync(rulesTextUrl, cancellationToken);
        var document = ParseRulesDocument(rulesTextUrl, rulesText);

        _memoryCache.Set(RulesCacheKey, document, RulesCacheDuration);
        return document;
    }

    /// <summary>
    /// Extracts the direct text download URL for the current Comprehensive Rules document.
    /// </summary>
    private static string ExtractRulesTextUrl(string rulesPageHtml)
    {
        var match = RulesTextUrlRegex.Match(rulesPageHtml ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidOperationException("Could not find the current Comprehensive Rules text file on the Wizards rules page.");
        }

        return match.Value.Replace(" ", "%20", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses the downloaded Comprehensive Rules text into searchable structures.
    /// </summary>
    private static RulesDocument ParseRulesDocument(string rulesTextUrl, string rulesText)
    {
        var normalizedText = (rulesText ?? string.Empty).Replace("\r\n", "\n");
        var lines = normalizedText.Split('\n');
        var sections = ParseSections(lines);
        var glossaryEntries = ParseGlossary(lines);
        var ruleLines = ParseRuleLines(lines);
        return new RulesDocument(rulesTextUrl, sections, glossaryEntries, ruleLines);
    }

    /// <summary>
    /// Parses keyword section headers and their matching subrules.
    /// </summary>
    private static IReadOnlyList<MechanicSection> ParseSections(IReadOnlyList<string> lines)
    {
        var sections = new List<MechanicSection>();
        for (var index = 0; index < lines.Count; index++)
        {
            var match = SectionHeaderRegex.Match(lines[index].Trim());
            if (!match.Success)
            {
                continue;
            }

            var ruleReference = match.Groups["rule"].Value;
            var title = match.Groups["title"].Value.Trim();
            var prefix = $"{ruleReference}";
            var collectedLines = new List<string> { lines[index].Trim() };

            for (var nextIndex = index + 1; nextIndex < lines.Count; nextIndex++)
            {
                var nextLine = lines[nextIndex].TrimEnd();
                if (string.IsNullOrWhiteSpace(nextLine))
                {
                    break;
                }

                if (SectionHeaderRegex.IsMatch(nextLine))
                {
                    break;
                }

                if (RuleLineRegex.IsMatch(nextLine) && !nextLine.StartsWith(prefix, StringComparison.Ordinal))
                {
                    break;
                }

                collectedLines.Add(nextLine.Trim());
            }

            sections.Add(new MechanicSection(title, ruleReference, string.Join(Environment.NewLine, collectedLines)));
        }

        return sections;
    }

    /// <summary>
    /// Parses the glossary section from the official rules text.
    /// </summary>
    private static IReadOnlyList<GlossaryEntry> ParseGlossary(IReadOnlyList<string> lines)
    {
        var glossaryStart = -1;
        for (var index = lines.Count - 1; index >= 0; index--)
        {
            if (string.Equals(lines[index].Trim(), "Glossary", StringComparison.Ordinal))
            {
                glossaryStart = index + 1;
                break;
            }
        }

        if (glossaryStart < 0)
        {
            return Array.Empty<GlossaryEntry>();
        }

        var entries = new List<GlossaryEntry>();
        var currentTitle = string.Empty;
        var currentLines = new List<string>();

        void CommitCurrent()
        {
            if (string.IsNullOrWhiteSpace(currentTitle) || currentLines.Count == 0)
            {
                return;
            }

            var description = string.Join(Environment.NewLine, currentLines);
            var referenceMatch = Regex.Match(description, @"rule\s(?<rule>\d+\.\d+[a-z]?)", RegexOptions.IgnoreCase);
            var ruleReference = referenceMatch.Success ? referenceMatch.Groups["rule"].Value : null;
            entries.Add(new GlossaryEntry(currentTitle, description, ruleReference));
        }

        for (var index = glossaryStart; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                CommitCurrent();
                currentTitle = string.Empty;
                currentLines.Clear();
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentTitle))
            {
                currentTitle = trimmed;
                continue;
            }

            currentLines.Add(trimmed);
        }

        CommitCurrent();
        return entries;
    }

    /// <summary>
    /// Parses individual rule lines so non-section terms can still be found.
    /// </summary>
    private static IReadOnlyList<RuleLineEntry> ParseRuleLines(IReadOnlyList<string> lines)
    {
        var ruleLines = new List<RuleLineEntry>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var match = RuleLineRegex.Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            ruleLines.Add(new RuleLineEntry(match.Groups["rule"].Value, match.Groups["text"].Value.Trim()));
        }

        return ruleLines;
    }

    /// <summary>
    /// Checks whether the provided text contains the requested search term as a whole word.
    /// </summary>
    private static bool ContainsTerm(string text, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(searchTerm))
        {
            return false;
        }

        return Regex.IsMatch(text, $@"\b{Regex.Escape(searchTerm.Trim())}\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Normalizes mechanic names for case-insensitive exact matching.
    /// </summary>
    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Downloads text content from the provided Wizards URL.
    /// </summary>
    private static async Task<string> FetchStringAsync(string url, CancellationToken cancellationToken)
    {
        var client = new RestClient(new RestClientOptions
        {
            ThrowOnAnyError = false,
        });

        client.AddDefaultHeader("User-Agent", "MtgDeckStudio/1.0 (+https://github.com/luntc1972/MtgDeckStudio)");
        client.AddDefaultHeader("Accept", "text/plain, text/html, application/xhtml+xml, */*;q=0.8");

        var request = new RestRequest(url, Method.Get);
        var response = await client.ExecuteAsync(request, cancellationToken);
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300 || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new HttpRequestException(
                $"Wizards of the Coast rules lookup returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        return response.Content;
    }

    private sealed record RulesDocument(
        string RulesTextUrl,
        IReadOnlyList<MechanicSection> Sections,
        IReadOnlyList<GlossaryEntry> GlossaryEntries,
        IReadOnlyList<RuleLineEntry> RuleLines);

    private sealed record MechanicSection(string Title, string RuleReference, string RulesText);

    private sealed record GlossaryEntry(string Title, string Description, string? RuleReference)
    {
        public string RulesText => string.IsNullOrWhiteSpace(RuleReference)
            ? $"{Title}{Environment.NewLine}{Environment.NewLine}{Description}"
            : $"{Title}{Environment.NewLine}{Environment.NewLine}{Description}";
    }

    private sealed record RuleLineEntry(string RuleReference, string Text);

    [GeneratedRegex(@"https://media\.wizards\.com/\d{4}/downloads/MagicCompRules[^""']+?\.txt", RegexOptions.IgnoreCase)]
    private static partial Regex RulesTextUrlPattern();

    [GeneratedRegex(@"^(?<rule>\d+\.\d+)\.\s(?<title>.+)$", RegexOptions.Compiled)]
    private static partial Regex SectionHeaderPattern();

    [GeneratedRegex(@"^(?<rule>\d+\.\d+[a-z]?)\s(?<text>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RuleLinePattern();
}
