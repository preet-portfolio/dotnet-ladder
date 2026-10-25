using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ladder.Json.Judge;

/// <summary>One deterministic text form for both trees, so "same document" is a string comparison.</summary>
internal static class Canonical
{
    public static string Of(JsonValue value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    public static string Of(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(sb, element);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonValue value)
    {
        switch (value)
        {
            case JsonObject obj:
                sb.Append('{');
                for (int i = 0; i < obj.Members.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    WriteString(sb, obj.Members[i].Key);
                    sb.Append(':');
                    Write(sb, obj.Members[i].Value);
                }

                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (int i = 0; i < arr.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    Write(sb, arr.Items[i]);
                }

                sb.Append(']');
                break;
            case JsonStringValue s:
                WriteString(sb, s.Value);
                break;
            case JsonNumber n:
                sb.Append(n.Raw);
                break;
            default:
                sb.Append(value.Kind switch
                {
                    JsonTokenKind.True => "true",
                    JsonTokenKind.False => "false",
                    _ => "null",
                });
                break;
        }
    }

    private static void Write(StringBuilder sb, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (JsonProperty p in element.EnumerateObject())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(sb, p.Name);
                    sb.Append(':');
                    Write(sb, p.Value);
                }

                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    Write(sb, item);
                }

                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(sb, element.GetString()!);
                break;
            case JsonValueKind.Number:
                sb.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    // Every char outside printable ASCII becomes \uXXXX, so the comparison sees exact UTF-16 code units.
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (c is >= ' ' and <= '~' and not '"' and not '\\')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        sb.Append('"');
    }
}
