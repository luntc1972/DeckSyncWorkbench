using DeckFlow.Web.Models;
using DeckFlow.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DeckFlow.Web.Controllers;

/// <summary>Renders the About page with app identity, version, repo link, and credits.</summary>
public sealed class AboutController : Controller
{
    private const string Tagline =
        "DeckFlow helps deck builders translate decks between Moxfield and Archidekt without manual editing. " +
        "It also provides ChatGPT prompt-building workflows for single-deck analysis, cEDH meta-gap analysis, " +
        "and head-to-head deck comparison, Commander Spellbook combo lookup, Scryfall card and mechanic references, " +
        "and a cache-backed category suggestion engine.";

    private const string RepositoryUrl = "https://github.com/luntc1972/DeckFlow";

    private static readonly IReadOnlyList<CreditEntry> Credits = new[]
    {
        new CreditEntry("Scryfall", "https://scryfall.com", "card data and rulings"),
        new CreditEntry("Commander Spellbook", "https://commanderspellbook.com", "combo lookup"),
        new CreditEntry("EDH Top 16", "https://edhtop16.com", "cEDH reference lists"),
        new CreditEntry("Wizards of the Coast", "https://magic.wizards.com/en/rules", "mechanic rules text"),
        new CreditEntry("Archidekt", "https://archidekt.com", "category data and deck integration"),
        new CreditEntry("Moxfield", "https://moxfield.com", "deck integration"),
    };

    private readonly IVersionService _versionService;

    public AboutController(IVersionService versionService) => _versionService = versionService;

    [HttpGet("/about")]
    public IActionResult Index()
    {
        var model = new AboutViewModel(Tagline, _versionService.GetVersion(), RepositoryUrl, Credits);
        return View(model);
    }
}
