using DeckFlow.Web.Models.DeckModules;

namespace DeckFlow.Web.Services.Modular;

/// <summary>Pure numeric comparer for analyzed Deck Modules configurations.</summary>
public sealed class ConfigurationDeltaService : IConfigurationDeltaService
{
    /// <inheritdoc />
    public ConfigurationComparisonDeltaModel ComputeDelta(IReadOnlyList<ConfigurationAnalysisResult?> analyses, int referenceIndex)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        if (analyses.Count < 2)
        {
            throw new ArgumentException("At least two configurations are required.", nameof(analyses));
        }

        if ((uint)referenceIndex >= (uint)analyses.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceIndex));
        }

        ConfigurationAnalysisResult? reference = analyses[referenceIndex];
        IReadOnlyList<ConfigurationComparisonColumn> columns = analyses
            .Select((analysis, index) => new { analysis, index })
            .Where(item => item.index != referenceIndex)
            .Select(item => ToColumn(item.analysis, reference))
            .ToArray();

        return new ConfigurationComparisonDeltaModel
        {
            Reference = ToColumn(reference, null),
            Columns = columns,
            ColorRows = BuildColorRows(analyses, referenceIndex, reference),
            InteractionRows = BuildInteractionRows(analyses, referenceIndex, reference),
        };
    }

    private static ConfigurationComparisonColumn ToColumn(ConfigurationAnalysisResult? analysis, ConfigurationAnalysisResult? reference)
    {
        if (analysis is null)
        {
            return new ConfigurationComparisonColumn { IsAnalyzed = false, IsCoreOnly = false, HasLandCountChange = false };
        }

        return new ConfigurationComparisonColumn
        {
            ConfigurationId = analysis.ConfigurationId,
            ConfigurationName = analysis.ConfigurationName,
            IsAnalyzed = true,
            IsCoreOnly = analysis.IsCoreOnly,
            Health = analysis.Health,
            LandCount = analysis.LandCount,
            TargetLandCount = analysis.TargetLandCount,
            LandTargetDelta = analysis.LandDelta,
            RampSourceCount = analysis.RampSourceCount,
            HardToCastCount = analysis.HardToCastCount,
            LandCountDelta = reference is null ? null : analysis.LandCount - reference.LandCount,
            RampSourceCountDelta = reference is null ? null : analysis.RampSourceCount - reference.RampSourceCount,
            HardToCastCountDelta = reference is null ? null : analysis.HardToCastCount - reference.HardToCastCount,
            HasLandCountChange = reference is not null && analysis.LandCount != reference.LandCount,
        };
    }

    private static IReadOnlyList<ConfigurationColorSourceDeltaRow> BuildColorRows(IReadOnlyList<ConfigurationAnalysisResult?> analyses, int referenceIndex, ConfigurationAnalysisResult? reference)
    {
        var rows = new List<ConfigurationColorSourceDeltaRow>();
        var colors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddColorRows(reference?.AttributedFindings ?? [], analyses, referenceIndex, reference, colors, rows);
        foreach (ConfigurationAnalysisResult? analysis in analyses.Where((_, index) => index != referenceIndex))
        {
            AddColorRows(analysis?.AttributedFindings ?? [], analyses, referenceIndex, reference, colors, rows);
        }

        return rows;
    }

    private static void AddColorRows(IReadOnlyList<ConfigurationAttributedFinding> findings, IReadOnlyList<ConfigurationAnalysisResult?> analyses, int referenceIndex, ConfigurationAnalysisResult? reference, ISet<string> colors, ICollection<ConfigurationColorSourceDeltaRow> rows)
    {
        foreach (ConfigurationAttributedFinding finding in findings)
        {
            if (!colors.Add(finding.Color))
            {
                continue;
            }

            ConfigurationAttributedFinding? referenceFinding = FindColor(reference?.AttributedFindings, finding.Color);
            rows.Add(new ConfigurationColorSourceDeltaRow
            {
                Color = finding.Color,
                DisplayColor = finding.DisplayColor,
                ReferenceActualSources = referenceFinding?.ActualSources,
                ReferenceRequiredSources = referenceFinding?.RequiredSources,
                Values = analyses.Where((_, index) => index != referenceIndex).Select(analysis => ToColorValue(analysis, finding.Color, referenceFinding)).ToArray(),
            });
        }
    }

    private static ConfigurationColorSourceDeltaValue ToColorValue(ConfigurationAnalysisResult? analysis, string color, ConfigurationAttributedFinding? reference)
    {
        ConfigurationAttributedFinding? finding = FindColor(analysis?.AttributedFindings, color);
        return new ConfigurationColorSourceDeltaValue
        {
            ConfigurationId = analysis?.ConfigurationId,
            ActualSources = finding?.ActualSources,
            RequiredSources = finding?.RequiredSources,
            ActualSourcesDelta = finding is null || reference is null ? null : finding.ActualSources - reference.ActualSources,
            IsPresent = finding is not null,
        };
    }

    private static IReadOnlyList<ConfigurationInteractionDeltaRow> BuildInteractionRows(IReadOnlyList<ConfigurationAnalysisResult?> analyses, int referenceIndex, ConfigurationAnalysisResult? reference)
    {
        var rows = new List<ConfigurationInteractionDeltaRow>();
        var kinds = new HashSet<ConfigurationModuleKind>();
        foreach (ConfigurationAnalysisResult? analysis in analyses)
        {
            foreach (ConfigurationModuleInteractionCount interaction in analysis?.Signals?.InteractionsByModule ?? [])
            {
                if (!kinds.Add(interaction.ModuleKind))
                {
                    continue;
                }

                ConfigurationModuleInteractionCount? referenceInteraction = FindInteraction(reference?.Signals?.InteractionsByModule, interaction.ModuleKind);
                rows.Add(new ConfigurationInteractionDeltaRow
                {
                    ModuleKind = interaction.ModuleKind,
                    ModuleName = interaction.ModuleName,
                    Values = analyses.Select((analysis, index) => ToInteractionValue(analysis, interaction.ModuleKind, referenceInteraction, index == referenceIndex)).ToArray(),
                });
            }
        }

        return rows;
    }

    private static ConfigurationInteractionDeltaValue ToInteractionValue(ConfigurationAnalysisResult? analysis, ConfigurationModuleKind kind, ConfigurationModuleInteractionCount? reference, bool isReference)
    {
        ConfigurationModuleInteractionCount? interaction = FindInteraction(analysis?.Signals?.InteractionsByModule, kind);
        return new ConfigurationInteractionDeltaValue
        {
            ConfigurationId = analysis?.ConfigurationId,
            InteractionCount = interaction?.InteractionCount,
            Delta = isReference || interaction is null || reference is null ? null : interaction.InteractionCount - reference.InteractionCount,
            IsPresent = interaction is not null,
        };
    }

    private static ConfigurationAttributedFinding? FindColor(IReadOnlyList<ConfigurationAttributedFinding>? findings, string color) => findings?.FirstOrDefault(finding => string.Equals(finding.Color, color, StringComparison.OrdinalIgnoreCase));

    private static ConfigurationModuleInteractionCount? FindInteraction(IReadOnlyList<ConfigurationModuleInteractionCount>? interactions, ConfigurationModuleKind kind) => interactions?.FirstOrDefault(interaction => interaction.ModuleKind == kind);
}
