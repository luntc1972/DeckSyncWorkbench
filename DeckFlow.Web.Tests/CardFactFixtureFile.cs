using System.Text.Json;
using DeckFlow.Core.Manabase;

namespace DeckFlow.Web.Tests;

/// <summary>
/// Deserializes a <c>.manabase-*-facts.json</c> CardFact fixture/cache file. Every manabase harness
/// and regression test that reads one shares this instead of re-typing the same
/// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions?)"/> call.
/// </summary>
internal static class CardFactFixtureFile
{
    public static List<CardFact> Load(string path) =>
        JsonSerializer.Deserialize<List<CardFact>>(File.ReadAllText(path))!;

    public static async Task<List<CardFact>> LoadAsync(string path) =>
        JsonSerializer.Deserialize<List<CardFact>>(await File.ReadAllTextAsync(path))!;
}
