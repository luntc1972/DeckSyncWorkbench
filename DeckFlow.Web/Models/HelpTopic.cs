namespace DeckFlow.Web.Models;

/// <summary>
/// Parsed help topic: metadata from the markdown header plus rendered HTML body.
/// </summary>
public sealed record HelpTopic(string Slug, string Title, string Summary, int Order, string HtmlContent);
