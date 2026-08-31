using System.Reflection;
using DeckFlow.Web.Models;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Xunit;

namespace DeckFlow.Web.Tests.Models;

/// <summary>
/// Tests for <see cref="CreatorStyleRequest"/>'s computed <see cref="CreatorStyleRequest.DeckSource"/> projection.
/// </summary>
public sealed class CreatorStyleRequestTests
{
    [Fact]
    public void DeckSource_HasNoSetter_SoModelBindingCannotWriteThroughIt()
    {
        // Why (WR-12): a settable DeckSource on this form-bound DTO would let a posted
        // DeckSource field populate DeckUrl depending on model-binder property visitation order,
        // bypassing whatever validation the caller expected on DeckUrl/DeckText directly.
        PropertyInfo property = typeof(CreatorStyleRequest).GetProperty(nameof(CreatorStyleRequest.DeckSource))!;

        Assert.Null(property.SetMethod);
        Assert.NotNull(property.GetCustomAttribute<BindNeverAttribute>());
    }

    [Fact]
    public void DeckSource_PasteTextSource_ReturnsDeckText()
    {
        var request = new CreatorStyleRequest
        {
            DeckInputSource = DeckInputSource.PasteText,
            DeckText = "1 Sol Ring",
            DeckUrl = "https://archidekt.com/decks/ignored"
        };

        Assert.Equal("1 Sol Ring", request.DeckSource);
    }

    [Fact]
    public void DeckSource_PublicUrlSource_ReturnsDeckUrl()
    {
        var request = new CreatorStyleRequest
        {
            DeckInputSource = DeckInputSource.PublicUrl,
            DeckUrl = "https://archidekt.com/decks/1",
            DeckText = "ignored"
        };

        Assert.Equal("https://archidekt.com/decks/1", request.DeckSource);
    }
}
