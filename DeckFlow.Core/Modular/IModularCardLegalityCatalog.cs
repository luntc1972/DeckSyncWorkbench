namespace DeckFlow.Core.Modular;

/// <summary>
/// Supplies already-resolved card legality facts for modular deck compilation.
/// </summary>
public interface IModularCardLegalityCatalog
{
    /// <summary>
    /// Gets the facts for a normalized card name, or <see langword="null"/> when they are unavailable.
    /// </summary>
    /// <param name="normalizedCardName">The normalized card name used as the lookup key.</param>
    /// <returns>Known legality facts, or <see langword="null"/>.</returns>
    ModularCardLegalityFacts? GetFacts(string normalizedCardName);
}
