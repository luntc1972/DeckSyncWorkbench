namespace DeckFlow.Web.Services;

/// <summary>Resolves the running application's version string for display.</summary>
public interface IVersionService
{
    string GetVersion();
}
