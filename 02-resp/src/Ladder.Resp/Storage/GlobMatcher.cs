namespace Ladder.Resp.Storage;

/// <summary>Valkey's stringmatchlen: glob patterns with *, ?, [abc], [^a-z] and backslash escapes, over bytes.</summary>
public static class GlobMatcher
{
    public static bool IsMatch(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text)
    {
        bool skipLongerMatches = false;
        return Match(pattern, text, 0, ref skipLongerMatches);
    }

    private static bool Match(ReadOnlySpan<byte> p, ReadOnlySpan<byte> s, int depth, ref bool skipLongerMatches)
    {
        // Valkey caps recursion at 1000 levels so a pattern like "*?*?*?..." cannot exhaust the stack.
        if (depth > 1000)
        {
            return false;
        }

        while (!p.IsEmpty)
        {
            switch (p[0])
            {
                case (byte)'*':
                    while (p.Length > 1 && p[1] == (byte)'*')
                    {
                        p = p[1..];
                    }

                    if (p.Length == 1)
                    {
                        return true;
                    }

                    // If the rest of the pattern matches nowhere in the rest of the string, letting an earlier '*'
                    // swallow more characters cannot help either: stop instead of backtracking (Valkey's
                    // skipLongerMatches, which keeps patterns like "a*a*a*...b" from taking exponential time).
                    while (!s.IsEmpty)
                    {
                        if (Match(p[1..], s, depth + 1, ref skipLongerMatches))
                        {
                            return true;
                        }

                        if (skipLongerMatches)
                        {
                            return false;
                        }

                        s = s[1..];
                    }

                    skipLongerMatches = true;
                    return false;
                case (byte)'?':
                    if (s.IsEmpty)
                    {
                        return false;
                    }

                    s = s[1..];
                    break;
                case (byte)'[':
                    if (s.IsEmpty)
                    {
                        return false;
                    }

                    p = p[1..];
                    bool not = !p.IsEmpty && p[0] == (byte)'^';
                    if (not)
                    {
                        p = p[1..];
                    }

                    bool matched = false;
                    while (!p.IsEmpty && p[0] != (byte)']')
                    {
                        if (p[0] == (byte)'\\' && p.Length >= 2)
                        {
                            p = p[1..];
                            matched |= p[0] == s[0];
                        }
                        else if (p.Length >= 3 && p[1] == (byte)'-')
                        {
                            byte start = p[0], end = p[2];
                            if (start > end)
                            {
                                (start, end) = (end, start);
                            }

                            matched |= s[0] >= start && s[0] <= end;
                            p = p[2..];
                        }
                        else
                        {
                            matched |= p[0] == s[0];
                        }

                        p = p[1..];
                    }

                    if (not)
                    {
                        matched = !matched;
                    }

                    if (!matched)
                    {
                        return false;
                    }

                    s = s[1..];
                    break;
                case (byte)'\\' when p.Length >= 2:
                    p = p[1..];
                    if (s.IsEmpty || p[0] != s[0])
                    {
                        return false;
                    }

                    s = s[1..];
                    break;
                default:
                    if (s.IsEmpty || p[0] != s[0])
                    {
                        return false;
                    }

                    s = s[1..];
                    break;
            }

            if (p.IsEmpty)
            {
                break;
            }

            p = p[1..];
            if (s.IsEmpty)
            {
                while (!p.IsEmpty && p[0] == (byte)'*')
                {
                    p = p[1..];
                }

                break;
            }
        }

        return p.IsEmpty && s.IsEmpty;
    }
}
