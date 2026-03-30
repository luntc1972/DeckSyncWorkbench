using MtgDeckStudio.Core.Integration;

namespace MtgDeckStudio.Core.Tests;

public sealed class EdhrecLookupTests
{
    [Fact]
    public void EdhrecCardLookup_SlugifiesNames()
    {
        Assert.Equal("wandering-archaic-explore-the-vastlands", EdhrecCardLookup.Slugify("Wandering Archaic // Explore the Vastlands"));
        Assert.Equal("bello-bard-of-the-brambles", EdhrecCardLookup.Slugify("Bello, Bard of the Brambles"));
    }
}
