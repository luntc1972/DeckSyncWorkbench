using MtgDeckStudio.Web.Services;
using Xunit;

namespace MtgDeckStudio.Web.Tests;

public sealed class ChatGptJsonTextFormatterServiceTests
{
    [Fact]
    public void FormatAsText_ExtractsJsonFromCodeFence()
    {
        var service = new ChatGptJsonTextFormatterService();

        var text = service.FormatAsText("""
```json
{
  "game_plan": "Midrange value",
  "strengths": ["card advantage", "resilience"]
}
```
""");

        Assert.Contains("game_plan: Midrange value", text);
        Assert.Contains("strengths:", text);
        Assert.Contains("- card advantage", text);
        Assert.Contains("- resilience", text);
    }

    [Fact]
    public void FormatAsText_FormatsNestedObjectsAndArrays()
    {
        var service = new ChatGptJsonTextFormatterService();

        var text = service.FormatAsText("""
{
  "weak_slots": [
    {
      "card": "Slot Card",
      "reason": "Too slow"
    }
  ]
}
""");

        Assert.Contains("weak_slots:", text);
        Assert.Contains("Item 1:", text);
        Assert.Contains("card: Slot Card", text);
        Assert.Contains("reason: Too slow", text);
    }
}
