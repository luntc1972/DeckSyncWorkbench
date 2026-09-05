using DeckFlow.Core.Loading;
using DeckFlow.Core.Models;
using DeckFlow.Core.Modular;
using DeckFlow.Core.Normalization;
using DeckFlow.Core.Parsing;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using DeckFlow.Web.Services;

namespace DeckFlow.Web.Services.Modular;

/// <summary>
/// Adapts the shared deck-import pathway and the Phase 1 <see cref="ModularDeckCompiler"/> to the
/// Deck Modules browser session. Imports exactly one baseline, keeps the imported command zone
/// immutable, and compiles the submitted manual state without retaining any server-side project
/// state and without any outbound network call during compilation.
/// </summary>
public sealed class DeckModulesPageService : IDeckModulesPageService
{
    private const int MaxCardNameLength = 200;
    private const int MinEntryQuantity = 1;
    private const int MaxEntryQuantity = 999;

    private readonly IDeckEntryLoader _deckEntryLoader;
    private readonly IModularCardLegalityCatalog? _legalityCatalog;

    /// <summary>
    /// Creates a new service backed by the shared deck loader and an optional injected
    /// card-legality catalog. When no catalog is supplied, every card's legality facts are
    /// reported as unverifiable rather than assumed legal.
    /// </summary>
    /// <param name="deckEntryLoader">Shared loader used to import the baseline deck.</param>
    /// <param name="legalityCatalog">Optional already-resolved card-legality facts.</param>
    public DeckModulesPageService(IDeckEntryLoader deckEntryLoader, IModularCardLegalityCatalog? legalityCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(deckEntryLoader);
        _deckEntryLoader = deckEntryLoader;
        _legalityCatalog = legalityCatalog;
    }

    /// <inheritdoc />
    public async Task<DeckModulesServiceResult<DeckModulesViewModel>> ImportAsync(
        DeckModulesImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceError = ValidateImportRequest(request);
        if (sourceError is not null)
        {
            return DeckModulesServiceResult<DeckModulesViewModel>.Failure(sourceError);
        }

        var source = request.ActiveSource == DeckInputSource.PublicUrl
            ? request.Url!.Trim()
            : request.PasteText!;

        DeckSourceLoadResult loadResult;
        try
        {
            loadResult = await _deckEntryLoader.LoadFromSourceAsync(source, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return DeckModulesServiceResult<DeckModulesViewModel>.Failure(UpstreamErrorMessageBuilder.BuildSuggestionMessage(exception));
        }
        catch (DeckParseException exception)
        {
            return DeckModulesServiceResult<DeckModulesViewModel>.Failure(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return DeckModulesServiceResult<DeckModulesViewModel>.Failure(exception.Message);
        }

        var commandZone = loadResult.Entries.Where(entry => IsBoard(entry, "commander")).ToArray();
        var baselineMainboard = loadResult.Entries.Where(entry => IsBoard(entry, "mainboard")).ToArray();

        var viewModel = new DeckModulesViewModel
        {
            CommandZone = Array.AsReadOnly(commandZone),
            BaselineMainboardEntries = Array.AsReadOnly(baselineMainboard),
            ImportNotice = loadResult.FallbackNotice,
        };

        return DeckModulesServiceResult<DeckModulesViewModel>.Success(viewModel);
    }

    /// <inheritdoc />
    public DeckModulesServiceResult<DeckModulesCompilationViewModel> Compile(DeckModulesCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validationError = ValidateCompilationRequest(request);
        if (validationError is not null)
        {
            return DeckModulesServiceResult<DeckModulesCompilationViewModel>.Failure(validationError);
        }

        var project = BuildProject(request);
        var selection = new ModularDeckSelection { StrategyId = request.SelectedAlternativeId };
        var compiler = new ModularDeckCompiler(_legalityCatalog);
        var compilation = compiler.Compile(project, selection);

        var viewModel = new DeckModulesCompilationViewModel
        {
            IsStructurallyValid = compilation.IsStructurallyValid,
            IsVerifiedLegal = compilation.IsVerifiedLegal,
            Diagnostics = compilation.Diagnostics,
            SelectedStrategyId = compilation.SelectedStrategyId,
            SelectedStrategyName = compilation.SelectedStrategyName,
            SelectedManaSupportModuleName = compilation.SelectedManaSupportModuleName,
            CommandZoneEntries = compilation.CommandZoneEntries,
            MainboardEntries = compilation.MainboardEntries,
            Entries = compilation.Entries,
            TotalCardCount = compilation.TotalCardCount,
            SwapPlan = compilation.SwapPlan,
        };

        return DeckModulesServiceResult<DeckModulesCompilationViewModel>.Success(viewModel);
    }

    private static string? ValidateImportRequest(DeckModulesImportRequest request)
    {
        if (request.ActiveSource == DeckInputSource.PublicUrl)
        {
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return "A public deck URL is required.";
            }

            if (request.Url.Length > DeckModulesImportRequest.MaxUrlLength)
            {
                return $"The deck URL exceeds the maximum accepted length of {DeckModulesImportRequest.MaxUrlLength} characters.";
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(request.PasteText))
        {
            return "Pasted decklist text is required.";
        }

        if (request.PasteText.Length > DeckModulesImportRequest.MaxPasteTextLength)
        {
            return $"The pasted decklist exceeds the maximum accepted length of {DeckModulesImportRequest.MaxPasteTextLength} characters.";
        }

        return null;
    }

    private static string? ValidateCompilationRequest(DeckModulesCompilationRequest request)
    {
        if (request.Alternatives.Count is < DeckModulesCompilationRequest.MinAlternativeCount or > DeckModulesCompilationRequest.MaxAlternativeCount)
        {
            return $"Deck Modules requires between {DeckModulesCompilationRequest.MinAlternativeCount} and {DeckModulesCompilationRequest.MaxAlternativeCount} alternatives.";
        }

        if (string.IsNullOrWhiteSpace(request.SelectedAlternativeId))
        {
            return "A selected alternative is required.";
        }

        var entryListError =
            ValidateEntryList(request.OriginalCommandZone, "imported command zone") ??
            ValidateEntryList(request.CommandZone, "command zone") ??
            ValidateEntryList(request.BaselineMainboardEntries, "baseline mainboard entries") ??
            ValidateEntryList(request.CoreEntries, "core entries");
        if (entryListError is not null)
        {
            return entryListError;
        }

        if (!CommandZonesMatch(request.OriginalCommandZone, request.CommandZone))
        {
            return "The imported command zone cannot be changed and no longer matches the original import.";
        }

        var seenAlternativeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alternative in request.Alternatives)
        {
            var alternativeError = ValidateAlternative(alternative, seenAlternativeIds);
            if (alternativeError is not null)
            {
                return alternativeError;
            }
        }

        return null;
    }

    private static string? ValidateAlternative(DeckModulesAlternativeInput alternative, HashSet<string> seenAlternativeIds)
    {
        if (string.IsNullOrWhiteSpace(alternative.Id))
        {
            return "Each alternative requires an identifier.";
        }

        if (!seenAlternativeIds.Add(alternative.Id))
        {
            return $"Alternative identifier \"{alternative.Id}\" is used more than once.";
        }

        if (string.IsNullOrWhiteSpace(alternative.Name))
        {
            return "Each alternative requires a user-entered name.";
        }

        if (alternative.Name.Length > DeckModulesAlternativeInput.MaxNameLength)
        {
            return $"Alternative name exceeds the maximum accepted length of {DeckModulesAlternativeInput.MaxNameLength} characters.";
        }

        if (!Enum.IsDefined(alternative.Profile))
        {
            return "Each alternative requires one of the declared table profiles.";
        }

        if (string.IsNullOrWhiteSpace(alternative.PlayPlan))
        {
            return "Each alternative requires a one-sentence play-plan disclosure.";
        }

        if (alternative.PlayPlan.Length > DeckModulesAlternativeInput.MaxPlayPlanLength)
        {
            return $"Alternative play plan exceeds the maximum accepted length of {DeckModulesAlternativeInput.MaxPlayPlanLength} characters.";
        }

        if (!string.IsNullOrEmpty(alternative.ManaSupportName) && alternative.ManaSupportName.Length > DeckModulesAlternativeInput.MaxNameLength)
        {
            return $"Mana support name exceeds the maximum accepted length of {DeckModulesAlternativeInput.MaxNameLength} characters.";
        }

        return ValidateEntryList(alternative.MainboardEntries, $"\"{alternative.Name}\" mainboard entries", DeckModulesAlternativeInput.MaxEntriesPerList) ??
            ValidateEntryList(alternative.ManaSupportEntries, $"\"{alternative.Name}\" mana support entries", DeckModulesAlternativeInput.MaxEntriesPerList);
    }

    private static string? ValidateEntryList(IReadOnlyList<DeckEntry> entries, string listName, int maxCount = DeckModulesCompilationRequest.MaxEntriesPerList)
    {
        if (entries.Count > maxCount)
        {
            return $"The {listName} list exceeds the maximum accepted count of {maxCount} entries.";
        }

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                return $"An entry in the {listName} list is missing a card name.";
            }

            if (entry.Name.Length > MaxCardNameLength)
            {
                return $"An entry in the {listName} list exceeds the maximum accepted card-name length of {MaxCardNameLength} characters.";
            }

            if (entry.Quantity is < MinEntryQuantity or > MaxEntryQuantity)
            {
                return $"An entry in the {listName} list has a quantity outside the accepted range of {MinEntryQuantity}-{MaxEntryQuantity}.";
            }
        }

        return null;
    }

    private static bool CommandZonesMatch(IReadOnlyList<DeckEntry> original, IReadOnlyList<DeckEntry> current)
    {
        var originalCounts = AggregateByNameAndBoard(original);
        var currentCounts = AggregateByNameAndBoard(current);
        if (originalCounts.Count != currentCounts.Count)
        {
            return false;
        }

        foreach (var (key, quantity) in originalCounts)
        {
            if (!currentCounts.TryGetValue(key, out var currentQuantity) || currentQuantity != quantity)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<(string NormalizedName, string Board), int> AggregateByNameAndBoard(IReadOnlyList<DeckEntry> entries)
    {
        var result = new Dictionary<(string, string), int>();
        foreach (var entry in entries)
        {
            var key = (CardNormalizer.Normalize(entry.Name), entry.Board.Trim().ToLowerInvariant());
            result[key] = result.GetValueOrDefault(key) + entry.Quantity;
        }

        return result;
    }

    private static ModularDeckProject BuildProject(DeckModulesCompilationRequest request)
    {
        var strategyModules = new List<ModularStrategyModule>(request.Alternatives.Count);
        var manaSupportModules = new List<ModularManaSupportModule>(request.Alternatives.Count);
        foreach (var alternative in request.Alternatives)
        {
            var manaSupportModuleId = alternative.Id + ":mana-support";
            strategyModules.Add(new ModularStrategyModule
            {
                Id = alternative.Id,
                DisplayName = alternative.Name,
                MainboardEntries = WithRecomputedNormalizedNames(alternative.MainboardEntries),
                ManaSupportModuleId = manaSupportModuleId,
            });
            manaSupportModules.Add(new ModularManaSupportModule
            {
                Id = manaSupportModuleId,
                DisplayName = alternative.ManaSupportName ?? string.Empty,
                MainboardEntries = WithRecomputedNormalizedNames(alternative.ManaSupportEntries),
            });
        }

        return new ModularDeckProject
        {
            CommandZone = WithRecomputedNormalizedNames(request.CommandZone),
            BaselineMainboardEntries = WithRecomputedNormalizedNames(request.BaselineMainboardEntries),
            CoreEntries = WithRecomputedNormalizedNames(request.CoreEntries),
            StrategyModules = strategyModules,
            ManaSupportModules = manaSupportModules,
        };
    }

    /// <summary>
    /// Recomputes each entry's normalized name from its display name rather than trusting the
    /// browser-submitted value, so a tampered <see cref="DeckEntry.NormalizedName"/> cannot desync
    /// singleton, color-identity, or overlap grouping from what is actually displayed.
    /// </summary>
    private static IReadOnlyList<DeckEntry> WithRecomputedNormalizedNames(IReadOnlyList<DeckEntry> entries)
        => Array.AsReadOnly(entries.Select(entry => entry with { NormalizedName = CardNormalizer.Normalize(entry.Name) }).ToArray());

    private static bool IsBoard(DeckEntry entry, string board)
        => string.Equals(entry.Board, board, StringComparison.OrdinalIgnoreCase);
}
