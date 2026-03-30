namespace MtgDeckStudio.Core.Parsing;

public sealed class DeckParseException : Exception
{
    public DeckParseException(string message)
        : base(message)
    {
    }
}
