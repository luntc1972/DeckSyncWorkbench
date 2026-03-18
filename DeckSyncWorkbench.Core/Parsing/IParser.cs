using DeckSyncWorkbench.Core.Models;

namespace DeckSyncWorkbench.Core.Parsing;

public interface IParser
{
    List<DeckEntry> ParseFile(string filePath);

    List<DeckEntry> ParseText(string content);
}
