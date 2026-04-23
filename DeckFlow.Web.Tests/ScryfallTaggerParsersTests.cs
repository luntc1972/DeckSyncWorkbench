using DeckFlow.Web.Services;
using Xunit;

namespace DeckFlow.Web.Tests;

public sealed class ScryfallTaggerParsersTests
{
    [Theory]
    [InlineData("spot-removal", "Spot Removal")]
    [InlineData("ramp_land", "Ramp Land")]
    [InlineData("tag", "Tag")]
    [InlineData("  draw-two-cards  ", "Draw Two Cards")]
    [InlineData("multi--dash", "Multi Dash")]
    [InlineData("UPPERCASE", "UPPERCASE")]
    public void NormalizeTagName_ReturnsExpectedResult(string tag, string expected)
    {
        var actual = ScryfallTaggerParsers.NormalizeTagName(tag);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("<meta name=\"csrf-token\" content=\"ABC123\">", "ABC123")]
    [InlineData("<META NAME=\"CSRF-TOKEN\" CONTENT=\"ABC123\">", "ABC123")]
    [InlineData("", null)]
    public void TryExtractCsrfToken_ReturnsExpectedResult(string html, string? expected)
    {
        var actual = ScryfallTaggerParsers.TryExtractCsrfToken(html);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryExtractCsrfToken_ReturnsNullForMissingMeta()
    {
        var actual = ScryfallTaggerParsers.TryExtractCsrfToken("<html></html>");

        Assert.Null(actual);
    }

    [Fact]
    public void TryExtractCsrfToken_ReturnsNullForWrongMetaName()
    {
        var actual = ScryfallTaggerParsers.TryExtractCsrfToken("<meta name=\"viewport\" content=\"width=device-width\">");

        Assert.Null(actual);
    }

    [Fact]
    public void TryExtractCsrfToken_ReturnsNullForReversedAttributeOrder()
    {
        var actual = ScryfallTaggerParsers.TryExtractCsrfToken("<meta content=\"X\" name=\"csrf-token\">");

        Assert.Null(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_ReturnsNormalizedSortedOracleTags()
    {
        var body = """
            {"data":{"card":{"taggings":[
              {"tag":{"name":"spot-removal","type":"ORACLE_CARD_TAG"},"weight":1,"status":"GOOD"},
              {"tag":{"name":"ramp","type":"ORACLE_CARD_TAG"},"weight":1,"status":"GOOD"}
            ]}}}
            """;

        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson(body);

        Assert.Equal(new[] { "Ramp", "Spot Removal" }, actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_SkipsNonOracleTags()
    {
        var body = """
            {"data":{"card":{"taggings":[
              {"tag":{"name":"spot-removal","type":"ORACLE_CARD_TAG"},"weight":1,"status":"GOOD"},
              {"tag":{"name":"art-theme","type":"ILLUSTRATION_TAG"},"weight":1,"status":"GOOD"}
            ]}}}
            """;

        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson(body);

        Assert.Equal(new[] { "Spot Removal" }, actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_ReturnsEmptyListForEmptyTaggingsArray()
    {
        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson("""{"data":{"card":{"taggings":[]}}}""");

        Assert.Empty(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_ReturnsEmptyListForMissingTaggingsPath()
    {
        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson("""{"data":{"card":{}}}""");

        Assert.Empty(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_ReturnsEmptyListForMissingData()
    {
        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson("""{"foo":1}""");

        Assert.Empty(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_ReturnsEmptyListForInvalidJson()
    {
        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson("not json");

        Assert.Empty(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_ReturnsEmptyListForEmptyBody()
    {
        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson(string.Empty);

        Assert.Empty(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_DeduplicatesCaseInsensitiveTags()
    {
        var body = """
            {"data":{"card":{"taggings":[
              {"tag":{"name":"spot-removal","type":"ORACLE_CARD_TAG"},"weight":1,"status":"GOOD"},
              {"tag":{"name":"Spot-Removal","type":"ORACLE_CARD_TAG"},"weight":1,"status":"GOOD"}
            ]}}}
            """;

        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson(body);

        Assert.Equal(new[] { "Spot Removal" }, actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_SkipsEntriesMissingTagName()
    {
        var body = """
            {"data":{"card":{"taggings":[
              {"tag":{"type":"ORACLE_CARD_TAG"},"weight":1,"status":"GOOD"}
            ]}}}
            """;

        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson(body);

        Assert.Empty(actual);
    }

    [Fact]
    public void ParseOracleTagsFromJson_IncludesLowercaseOracleTagType()
    {
        var body = """
            {"data":{"card":{"taggings":[
              {"tag":{"name":"ramp","type":"oracle_card_tag"},"weight":1,"status":"GOOD"}
            ]}}}
            """;

        var actual = ScryfallTaggerParsers.ParseOracleTagsFromJson(body);

        Assert.Equal(new[] { "Ramp" }, actual);
    }
}
