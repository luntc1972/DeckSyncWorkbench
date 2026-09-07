using DeckFlow.Core.Models;
using DeckFlow.Core.Normalization;
using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>Identifies the Deck Modules area that supplied a card.</summary>
public enum ConfigurationModuleKind
{
    /// <summary>No matching module supplied the card.</summary>
    Unknown,

    /// <summary>The card came from the command zone.</summary>
    CommandZone,

    /// <summary>The card came from the shared core.</summary>
    Core,

    /// <summary>The card came from the selected strategy.</summary>
    Strategy,

    /// <summary>The card came from the selected strategy's mana support.</summary>
    ManaSupport,

    /// <summary>More than one module supplied the card.</summary>
    Multiple,
}

/// <summary>Maps cards in a selected configuration to their contributing modules.</summary>
public sealed class ConfigurationModuleMap
{
    private readonly Dictionary<string, ModuleMapping> _mappings;

    private ConfigurationModuleMap(Dictionary<string, ModuleMapping> mappings)
    {
        _mappings = mappings;
    }

    /// <summary>Builds a map from the request's command zone, core, and selected alternative.</summary>
    public static ConfigurationModuleMap Build(DeckModulesCompilationRequest request)
    {
        var mappings = new Dictionary<string, ModuleMapping>(StringComparer.Ordinal);
        var selectedAlternative = request.Alternatives.FirstOrDefault(alternative => alternative.Id == request.SelectedAlternativeId);

        AddEntries(mappings, request.CommandZone, ConfigurationModuleKind.CommandZone, null);
        AddEntries(mappings, request.CoreEntries, ConfigurationModuleKind.Core, "Core");

        if (selectedAlternative is not null)
        {
            AddEntries(mappings, selectedAlternative.MainboardEntries, ConfigurationModuleKind.Strategy, selectedAlternative.Name);
            AddEntries(mappings, selectedAlternative.ManaSupportEntries, ConfigurationModuleKind.ManaSupport, selectedAlternative.ManaSupportName ?? "Mana Support");
        }

        return new ConfigurationModuleMap(mappings);
    }

    /// <summary>Gets the module classification for <paramref name="cardName"/> when the card is mapped.</summary>
    public bool TryResolve(string cardName, out ConfigurationModuleKind kind, out string? moduleDisplayName)
    {
        if (_mappings.TryGetValue(CardNormalizer.Normalize(cardName), out var mapping))
        {
            kind = mapping.Kind;
            moduleDisplayName = mapping.DisplayName;
            return true;
        }

        kind = ConfigurationModuleKind.Unknown;
        moduleDisplayName = null;
        return false;
    }

    private static void AddEntries(
        Dictionary<string, ModuleMapping> mappings,
        IReadOnlyList<DeckEntry> entries,
        ConfigurationModuleKind kind,
        string? displayName)
    {
        foreach (var entry in entries)
        {
            var key = CardNormalizer.Normalize(entry.Name);
            if (mappings.TryGetValue(key, out var existing) && existing.Kind != kind)
            {
                mappings[key] = new ModuleMapping(ConfigurationModuleKind.Multiple, "multiple modules");
                continue;
            }

            mappings.TryAdd(key, new ModuleMapping(kind, displayName));
        }
    }

    private sealed record ModuleMapping(ConfigurationModuleKind Kind, string? DisplayName);
}
