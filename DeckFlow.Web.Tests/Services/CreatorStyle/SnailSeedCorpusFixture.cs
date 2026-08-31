using DeckFlow.Core.Knowledge.MeasuredStyleExtraction;
using DeckFlow.Core.Models;
using DeckFlow.Web.Services.CreatorStyle;

namespace DeckFlow.Web.Tests.Services.CreatorStyle;

/// <summary>
/// Automated deterministic Snail-representative corpus for extractor invariants.
/// Manual-only validation against the live 39-deck crawl remains in the phase artifacts.
/// </summary>
public static class SnailSeedCorpusFixture
{
    public const string CreatorSlug = "snail-seed";
    public const string Platform = "archidekt";
    public const string Username = "snail-seed";
    public const int CurrentFolderId = 101;
    public const int SecondaryFolderId = 202;
    public const int BudgetFolderId = 303;

    public static IReadOnlyDictionary<int, double> FolderWeights { get; } = new Dictionary<int, double>
    {
        [CurrentFolderId] = 1.0,
        [SecondaryFolderId] = 1.0,
        [BudgetFolderId] = 0.5
    };

    public static IReadOnlyList<CreatorDeckSample> Samples { get; } =
    [
        CreateSample(
            "snail-current-1",
            CurrentFolderId,
            "Current",
            Commander("Teysa Karlov"),
            Mainboard("Command Tower"),
            Mainboard("Sol Ring"),
            Mainboard("Arcane Signet"),
            Mainboard("Skullclamp"),
            Mainboard("Viscera Seer"),
            Mainboard("Lingering Souls"),
            Mainboard("Swords to Plowshares"),
            Land("Plains", 30),
            Land("Swamp", 30)),
        CreateSample(
            "snail-current-2",
            CurrentFolderId,
            "Current",
            Commander("Brago, King Eternal"),
            Mainboard("Command Tower"),
            Mainboard("Sol Ring"),
            Mainboard("Arcane Signet"),
            Mainboard("Rhystic Study"),
            Mainboard("Restoration Angel"),
            Mainboard("Ephemerate"),
            Mainboard("Swords to Plowshares"),
            Land("Island", 31),
            Land("Plains", 30)),
        CreateSample(
            "snail-secondary-1",
            SecondaryFolderId,
            "Secondary",
            Commander("Muldrotha, the Gravetide"),
            Mainboard("Command Tower"),
            Mainboard("Sol Ring"),
            Mainboard("Arcane Signet"),
            Mainboard("Rhystic Study"),
            Mainboard("Eternal Witness"),
            Mainboard("Satyr Wayfinder"),
            Mainboard("Cultivate"),
            Land("Forest", 31),
            Land("Island", 30)),
        CreateSample(
            "snail-secondary-2",
            SecondaryFolderId,
            "Secondary",
            Commander("Teysa Karlov"),
            Mainboard("Command Tower"),
            Mainboard("Sol Ring"),
            Mainboard("Arcane Signet"),
            Mainboard("Skullclamp"),
            Mainboard("Viscera Seer"),
            Mainboard("Lingering Souls"),
            Mainboard("Village Rites"),
            Land("Plains", 31),
            Land("Swamp", 30)),
        CreateSample(
            "snail-budget-1",
            BudgetFolderId,
            "Budget",
            Commander("Feather, the Redeemed"),
            Mainboard("Command Tower"),
            Mainboard("Sol Ring"),
            Mainboard("Arcane Signet"),
            Mainboard("Fellwar Stone"),
            Mainboard("Defiant Strike"),
            Mainboard("Ephemerate"),
            Mainboard("Young Pyromancer"),
            Land("Mountain", 31),
            Land("Plains", 30)),
        CreateSample(
            "snail-budget-2",
            BudgetFolderId,
            "Budget",
            Commander("Alela, Artful Provocateur"),
            Mainboard("Command Tower"),
            Mainboard("Sol Ring"),
            Mainboard("Arcane Signet"),
            Mainboard("Skullclamp"),
            Mainboard("Bident of Thassa"),
            Mainboard("Lingering Souls"),
            Mainboard("Reconnaissance Mission"),
            Land("Island", 31),
            Land("Plains", 30))
    ];

    public static IReadOnlyList<CreatorDeckSample> BelowMinFloorSubset { get; } = Samples.Take(4).ToArray();

    public static IReadOnlyList<ArchidektDeckSummary> DeckSummaries { get; } = Samples
        .Select(sample => new ArchidektDeckSummary
        {
            Id = sample.DeckId,
            Name = sample.DeckId,
            Size = sample.CardCount,
            ParentFolderId = sample.FolderId,
            ParentFolderName = sample.FolderName
        })
        .ToArray();

    private static CreatorDeckSample CreateSample(
        string deckId,
        int folderId,
        string folderName,
        params DeckEntry[] entries)
    {
        return new CreatorDeckSample
        {
            DeckId = deckId,
            Entries = entries,
            CardCount = entries.Sum(entry => entry.Quantity),
            FolderId = folderId,
            FolderName = folderName,
            ConfidenceMarker = "ok"
        };
    }

    private static DeckEntry Commander(string name)
        => new()
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = 1,
            Board = "commander"
        };

    private static DeckEntry Mainboard(string name, int quantity = 1)
        => new()
        {
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = quantity,
            Board = "mainboard"
        };

    private static DeckEntry Land(string name, int quantity)
        => Mainboard(name, quantity);
}
