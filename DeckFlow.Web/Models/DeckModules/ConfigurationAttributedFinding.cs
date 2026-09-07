using DeckFlow.Web.Services.Modular;

namespace DeckFlow.Web.Models.DeckModules;

/// <summary>Describes the confidence of a color-source finding's configuration attribution.</summary>
public enum ConfigurationAttributionStrength
{
    /// <summary>No attribution was available.</summary>
    None,

    /// <summary>Module membership is an inference and must never render with the confidence of <see cref="NamedCard"/>.</summary>
    ModuleMembership,

    /// <summary>A swapped card directly explains the finding.</summary>
    NamedCard,
}

/// <summary>A color-source finding with its strongest available configuration attribution.</summary>
public sealed record ConfigurationAttributedFinding
{
    /// <summary>Gets the color examined.</summary>
    public required string Color { get; init; }
    /// <summary>Gets the display color label.</summary>
    public required string DisplayColor { get; init; }
    /// <summary>Gets effective sources.</summary>
    public required double ActualSources { get; init; }
    /// <summary>Gets required sources.</summary>
    public required int RequiredSources { get; init; }
    /// <summary>Gets required sources minus actual sources.</summary>
    public required double Deficit { get; init; }
    /// <summary>Gets the spell driving the requirement.</summary>
    public required string DrivingSpell { get; init; }
    /// <summary>Gets whether adding sources would help.</summary>
    public required bool NeedsMoreSources { get; init; }
    /// <summary>Gets the directly implicated swap card, when present.</summary>
    public string? AttributedCard { get; init; }
    /// <summary>Gets the inferred module label, when present.</summary>
    public string? AttributedModule { get; init; }
    /// <summary>Gets the inferred module kind.</summary>
    public ConfigurationModuleKind AttributedModuleKind { get; init; }
    /// <summary>Gets whether the named card was added or removed.</summary>
    public string? SwapDirection { get; init; }
    /// <summary>Gets attribution confidence; module membership is an inference, never named-card certainty.</summary>
    public required ConfigurationAttributionStrength Strength { get; init; }
}
