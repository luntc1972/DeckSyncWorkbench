using System.Text;
using System.Text.Json;

namespace MtgDeckStudio.Web.Services;

public interface IChatGptJsonTextFormatterService
{
    string FormatAsText(string input);
}

public sealed class ChatGptJsonTextFormatterService : IChatGptJsonTextFormatterService
{
    public string FormatAsText(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("Paste ChatGPT JSON before converting it to text.");
        }

        var json = ExtractJsonPayload(input);
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        AppendElement(builder, document.RootElement, null, 0);
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    internal static string ExtractJsonPayload(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            trimmed = trimmed[..closingFence];
        }

        return trimmed.Trim();
    }

    private static void AppendElement(StringBuilder builder, JsonElement element, string? propertyName, int depth)
    {
        var indent = new string(' ', depth * 2);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (!string.IsNullOrWhiteSpace(propertyName))
                {
                    builder.AppendLine($"{indent}{propertyName}:");
                }

                foreach (var property in element.EnumerateObject())
                {
                    AppendElement(builder, property.Value, property.Name, depth + (propertyName is null ? 0 : 1));
                }
                break;

            case JsonValueKind.Array:
                builder.AppendLine($"{indent}{propertyName}:");
                if (element.GetArrayLength() == 0)
                {
                    builder.AppendLine($"{indent}  (none)");
                    break;
                }

                var index = 1;
                foreach (var item in element.EnumerateArray())
                {
                    if (IsSimpleValue(item))
                    {
                        builder.AppendLine($"{indent}  - {FormatSimpleValue(item)}");
                    }
                    else
                    {
                        builder.AppendLine($"{indent}  Item {index}:");
                        AppendElement(builder, item, null, depth + 2);
                    }

                    index++;
                }
                break;

            default:
                builder.AppendLine($"{indent}{propertyName}: {FormatSimpleValue(element)}");
                break;
        }
    }

    private static bool IsSimpleValue(JsonElement element)
        => element.ValueKind is JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False
            or JsonValueKind.Null;

    private static string FormatSimpleValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "(null)",
            _ => element.GetRawText()
        };
    }
}
