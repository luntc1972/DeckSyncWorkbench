using System.Reflection;
using DeckFlow.Web.Models;
using DeckFlow.Web.Models.DeckModules;
using Xunit;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Guards ANL-04: the analysis path is advisory by construction and must never acquire a validity verdict.
/// Deleting this test re-opens ANL-04.
/// </summary>
public sealed class DeckModulesAnalysisAdvisoryInvariantTests
{
    [Fact]
    public void AnalysisModels_DoNotExposeValidityProperties()
    {
        var prohibitedNames = new[] { "IsValid", "IsLegal", "IsVerifiedLegal", "IsStructurallyValid" };
        var properties = typeof(ConfigurationAnalysisResult).GetProperties()
            .Concat(typeof(ConfigurationAttributedFinding).GetProperties());

        Assert.DoesNotContain(properties, property => prohibitedNames.Contains(property.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void ConfigurationAnalysisResult_DoesNotContainCompilationViewModel()
    {
        Assert.DoesNotContain(
            typeof(ConfigurationAnalysisResult).GetProperties(),
            property => property.PropertyType == typeof(DeckModulesCompilationViewModel));
    }

    [Fact]
    public void ConfigurationAnalysisRequest_DeclaresDeckModulesCompilationRequestPayload()
    {
        var configuration = typeof(ConfigurationAnalysisRequest).GetProperty(nameof(ConfigurationAnalysisRequest.Configuration));

        Assert.NotNull(configuration);
        Assert.Equal(typeof(DeckModulesCompilationRequest), configuration!.PropertyType);
    }
}
