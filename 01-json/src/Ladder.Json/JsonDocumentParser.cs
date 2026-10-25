using System.Buffers;

namespace Ladder.Json;

public static class JsonDocumentParser
{
    /// <summary>Parses a whole document into a <see cref="JsonValue"/> tree using an explicit stack, so input depth cannot overflow the call stack.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out JsonValue? value, out JsonParseError error, int maxDepth = JsonTokenizer.MaxSupportedDepth)
    {
        var tokenizer = new JsonTokenizer(utf8Json, maxDepth);
        var containers = new Stack<JsonValue>();
        string? pendingName = null;
        value = null;
        char[]? scratch = null;

        try
        {
            while (tokenizer.Read())
            {
                JsonValue? completed;
                switch (tokenizer.Kind)
                {
                    case JsonTokenKind.StartObject:
                        containers.Push(Attach(new JsonObject(), containers, ref pendingName));
                        continue;
                    case JsonTokenKind.StartArray:
                        containers.Push(Attach(new JsonArray(), containers, ref pendingName));
                        continue;
                    case JsonTokenKind.EndObject:
                    case JsonTokenKind.EndArray:
                        completed = containers.Pop();
                        if (containers.Count == 0)
                        {
                            value = completed;
                        }

                        continue;
                    case JsonTokenKind.PropertyName:
                        if (!TryDecode(ref tokenizer, ref scratch, out pendingName, out error))
                        {
                            return false;
                        }

                        continue;
                    case JsonTokenKind.String:
                        if (!TryDecode(ref tokenizer, ref scratch, out string? s, out error))
                        {
                            return false;
                        }

                        completed = new JsonStringValue(s!);
                        break;
                    case JsonTokenKind.Number:
                        completed = new JsonNumber(System.Text.Encoding.ASCII.GetString(tokenizer.ValueSpan));
                        break;
                    case JsonTokenKind.True:
                        completed = JsonLiteral.True;
                        break;
                    case JsonTokenKind.False:
                        completed = JsonLiteral.False;
                        break;
                    default:
                        completed = JsonLiteral.Null;
                        break;
                }

                Attach(completed, containers, ref pendingName);
                if (containers.Count == 0)
                {
                    value = completed;
                }
            }

            error = tokenizer.Error;
            if (error.IsError)
            {
                value = null;
                return false;
            }

            return true;
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<char>.Shared.Return(scratch);
            }
        }
    }

    private static JsonValue Attach(JsonValue child, Stack<JsonValue> containers, ref string? pendingName)
    {
        if (containers.TryPeek(out JsonValue? parent))
        {
            if (parent is JsonObject obj)
            {
                obj.Members.Add(new(pendingName!, child));
                pendingName = null;
            }
            else
            {
                ((JsonArray)parent).Items.Add(child);
            }
        }

        return child;
    }

    private static bool TryDecode(ref JsonTokenizer tokenizer, ref char[]? scratch, out string? text, out JsonParseError error)
    {
        ReadOnlySpan<byte> raw = tokenizer.ValueSpan;
        if (scratch is null || scratch.Length < raw.Length)
        {
            if (scratch is not null)
            {
                ArrayPool<char>.Shared.Return(scratch);
            }

            scratch = ArrayPool<char>.Shared.Rent(Math.Max(raw.Length, 64));
        }

        OperationStatus status = tokenizer.TryGetString(scratch, out int written, out JsonErrorReason reason);
        if (status != OperationStatus.Done)
        {
            text = null;
            error = new JsonParseError(tokenizer.TokenOffset, reason == JsonErrorReason.None ? JsonErrorReason.InvalidUtf8 : reason);
            return false;
        }

        text = new string(scratch, 0, written);
        error = default;
        return true;
    }
}
