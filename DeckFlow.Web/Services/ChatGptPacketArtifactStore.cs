using System.Text;
using DeckFlow.Web.Models;

namespace DeckFlow.Web.Services;

/// <summary>
/// Writes ChatGPT analysis artifacts to disk under a timestamped folder, and reads them back
/// for the Import saved session workflow. Pure filesystem I/O, no prompt building.
/// </summary>
internal sealed class ChatGptPacketArtifactStore
{
    private readonly string _rootPath;

    public ChatGptPacketArtifactStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath => _rootPath;

    public async Task<string> SaveAsync(
        ChatGptDeckRequest request,
        string? commanderName,
        string inputSummary,
        string? requestContextText,
        string? referenceText,
        string? analysisPromptText,
        string deckProfileSchemaJson,
        string? setUpgradePromptText,
        CancellationToken cancellationToken)
    {
        var commanderSegment = CreateSafePathSegment(commanderName, "unknown-commander");
        var timestampSegment = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outputDirectory = Path.Combine(_rootPath, commanderSegment, timestampSegment);
        Directory.CreateDirectory(outputDirectory);

        var promptSections = new List<(string FileName, string Label, string? Content)>
        {
            ("01-request-context.txt", "REQUEST CONTEXT", requestContextText),
            ("00-input-summary.txt", "INPUT SUMMARY", inputSummary),
            ("30-reference.txt", "REFERENCE TEXT", referenceText),
            ("31-analysis-prompt.txt", "ANALYSIS PROMPT", analysisPromptText),
            ("41-deck-profile-schema.json", "DECK PROFILE JSON SCHEMA", deckProfileSchemaJson),
            ("50-set-upgrade-prompt.txt", "SET UPGRADE PROMPT", setUpgradePromptText)
        };

        var responseSections = new List<(string FileName, string Label, string? Content)>
        {
            ("40-deck-profile.json", "DECK PROFILE JSON", request.DeckProfileJson),
            ("51-set-upgrade-response.json", "SET UPGRADE RESPONSE JSON", request.SetUpgradeResponseJson)
        };

        foreach (var section in promptSections.Where(s => !string.IsNullOrWhiteSpace(s.Content)))
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, section.FileName),
                section.Content!.Trim() + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (var section in responseSections.Where(s => !string.IsNullOrWhiteSpace(s.Content)))
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, section.FileName),
                ExtractJsonObject(section.Content!).Trim() + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "all-prompts.txt"),
            BuildCombinedArtifactText(promptSections),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "all-responses.txt"),
            BuildCombinedArtifactText(responseSections),
            cancellationToken).ConfigureAwait(false);

        return outputDirectory;
    }

    /// <summary>
    /// Populates DeckProfileJson / SetUpgradeResponseJson on the request from files inside the
    /// import folder. Sets WorkflowStep so the matching standalone short-circuit fires.
    /// Throws if the folder is outside the artifacts root, missing, or contains neither JSON file.
    /// </summary>
    public void LoadInto(ChatGptDeckRequest request)
    {
        var path = request.ImportArtifactsPath.Trim();
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_rootPath, path));

        var isUnderRoot = fullPath.StartsWith(
            _rootPath + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, _rootPath, StringComparison.OrdinalIgnoreCase);
        if (!isUnderRoot)
        {
            throw new InvalidOperationException(
                $"Import folder must be under the ChatGPT Analysis artifacts directory ({_rootPath}).");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Import folder not found: {fullPath}");
        }

        var deckProfilePath = Path.Combine(fullPath, "40-deck-profile.json");
        var setUpgradeResponsePath = Path.Combine(fullPath, "51-set-upgrade-response.json");

        var loadedDeckProfile = TryLoadInto(deckProfilePath, value => request.DeckProfileJson = value);
        var loadedSetUpgrade = TryLoadInto(setUpgradeResponsePath, value => request.SetUpgradeResponseJson = value);

        if (!loadedDeckProfile && !loadedSetUpgrade)
        {
            throw new InvalidOperationException(
                $"Import folder did not contain 40-deck-profile.json or 51-set-upgrade-response.json: {fullPath}");
        }

        request.DeckUrl = string.Empty;
        request.DeckText = string.Empty;
        request.WorkflowStep = loadedSetUpgrade ? 5 : 3;
    }

    private static bool TryLoadInto(string filePath, Action<string> assign)
    {
        if (!File.Exists(filePath)) return false;
        var content = File.ReadAllText(filePath);
        if (string.IsNullOrWhiteSpace(content)) return false;
        assign(content);
        return true;
    }

    private static string BuildCombinedArtifactText(IEnumerable<(string FileName, string Label, string? Content)> sections)
    {
        var builder = new StringBuilder();
        foreach (var section in sections.Where(s => !string.IsNullOrWhiteSpace(s.Content)))
        {
            builder.AppendLine($"===== {section.Label} ({section.FileName}) =====");
            builder.AppendLine(section.Content!.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string CreateSafePathSegment(string? value, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Select(ch => invalidChars.Contains(ch) ? '-' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized.Replace(' ', '-').ToLowerInvariant();
    }

    private static string ExtractJsonObject(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                trimmed = trimmed[..closingFence];
            }
        }

        return trimmed.Trim();
    }
}
