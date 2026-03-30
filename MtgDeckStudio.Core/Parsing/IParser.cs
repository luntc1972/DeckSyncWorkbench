using MtgDeckStudio.Core.Models;

namespace MtgDeckStudio.Core.Parsing;

public interface IParser
{
    List<DeckEntry> ParseFile(string filePath);

    List<DeckEntry> ParseText(string content);
}
