using DeckFlow.CLI;
using Xunit;

namespace DeckFlow.Core.Tests;

/// <summary>
/// Regression tests for the protection-vocabulary disclosure.
/// </summary>
public sealed class ProtectionVocabularyDisclosureTests
{
    [Fact]
    public void BuildProtectionUnderDetectionNotice_UsesInteractionTargetedLowerBoundWording()
    {
        string markdown = RoleFloorResearchCommandRunner.BuildProtectionUnderDetectionNotice();

        Assert.Contains("interaction-targeted", markdown, StringComparison.Ordinal);
    }
}
