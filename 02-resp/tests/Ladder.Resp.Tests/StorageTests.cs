using System.Text;
using CsCheck;
using Ladder.Resp.Storage;

namespace Ladder.Resp.Tests;

public class StorageTests
{
    [Theory]
    [InlineData("0", true, 0L)]
    [InlineData("-1", true, -1L)]
    [InlineData("9223372036854775807", true, long.MaxValue)]
    [InlineData("-9223372036854775808", true, long.MinValue)]
    [InlineData("9223372036854775808", false, 0L)]
    [InlineData("-9223372036854775809", false, 0L)]
    [InlineData("007", false, 0L)]
    [InlineData("-0", false, 0L)]
    [InlineData("+1", false, 0L)]
    [InlineData(" 1", false, 0L)]
    [InlineData("1 ", false, 0L)]
    [InlineData("", false, 0L)]
    [InlineData("-", false, 0L)]
    [InlineData("99999999999999999999", false, 0L)]
    public void Strict_integer_parsing_matches_string2ll(string text, bool ok, long expected)
    {
        Assert.Equal(ok, RString.TryParseStrict(Encoding.ASCII.GetBytes(text), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("12345", StringEncoding.Int)]
    [InlineData("0123", StringEncoding.Embstr)]
    [InlineData("hello", StringEncoding.Embstr)]
    [InlineData("12345678901234567890123456789012345678901234", StringEncoding.Embstr)]
    [InlineData("123456789012345678901234567890123456789012345", StringEncoding.Raw)]
    public void Picks_valkey_string_encodings(string value, StringEncoding encoding)
    {
        Assert.Equal(encoding, RString.FromValue(Encoding.ASCII.GetBytes(value)).Encoding);
    }

    [Fact]
    public void Raw_writes_convert_int_encoding_and_zero_fill()
    {
        RString s = RString.FromInt(42);
        Span<byte> bytes = s.MakeRawWritable(5);
        Assert.Equal(StringEncoding.Raw, s.Encoding);
        Assert.Equal(new byte[] { (byte)'4', (byte)'2', 0, 0, 0 }, bytes.ToArray());
    }

    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("h?llo", "hello", true)]
    [InlineData("h?llo", "hllo", false)]
    [InlineData("h*llo", "heeeello", true)]
    [InlineData("h[ae]llo", "hallo", true)]
    [InlineData("h[ae]llo", "hillo", false)]
    [InlineData("h[^e]llo", "hallo", true)]
    [InlineData("h[^e]llo", "hello", false)]
    [InlineData("h[a-b]llo", "hbllo", true)]
    [InlineData("h[b-a]llo", "hallo", true)]
    [InlineData("h\\*llo", "h*llo", true)]
    [InlineData("h\\*llo", "hello", false)]
    [InlineData("key:*", "key:", true)]
    [InlineData("*a*", "bbb", false)]
    [InlineData("", "", true)]
    [InlineData("a**", "a", true)]
    public void Glob_matching_follows_stringmatchlen(string pattern, string text, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(Encoding.ASCII.GetBytes(pattern), Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Nested_star_patterns_fail_fast_instead_of_backtracking_exponentially()
    {
        // Valkey's keyspace.tcl regressions: before skipLongerMatches these ran for minutes holding the keyspace lock.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(GlobMatcher.IsMatch(Encoding.ASCII.GetBytes("a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*a*b"), Encoding.ASCII.GetBytes(new string('a', 40))));
        Assert.False(GlobMatcher.IsMatch(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*?", 50_000))), Encoding.ASCII.GetBytes(new string('a', 50_000))));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Expired_keys_disappear_on_lookup()
    {
        var db = new Database();
        db.Upsert("k"u8, RString.FromInt(1), now: 1000, expireAt: 2000);
        Assert.NotNull(db.Lookup("k"u8, 1999));
        Assert.Null(db.Lookup("k"u8, 2000));
        Assert.Equal(0, db.Count);
        Assert.Equal(0, db.VolatileCount);
    }

    [Fact]
    public void Upsert_clears_or_keeps_ttl()
    {
        var db = new Database();
        db.Upsert("k"u8, RString.FromInt(1), now: 0, expireAt: 5000);
        db.Upsert("k"u8, RString.FromInt(2), now: 0, keepTtl: true);
        Assert.Equal(5000, db.Lookup("k"u8, 0)!.ExpiresAt);
        db.Upsert("k"u8, RString.FromInt(3), now: 0);
        Assert.Equal(-1, db.Lookup("k"u8, 0)!.ExpiresAt);
        Assert.Equal(0, db.VolatileCount);
    }

    [Fact]
    public void Active_expiry_removes_expired_keys_only()
    {
        var db = new Database();
        for (int i = 0; i < 1000; i++)
        {
            db.Upsert(Encoding.ASCII.GetBytes($"k{i}"), RString.FromInt(i), now: 0, expireAt: i < 900 ? 10 : 100_000);
        }

        while (db.ActiveExpireCycle(now: 50, new Random(1), deadlineTicks: long.MaxValue) > 0)
        {
        }

        Assert.InRange(db.Count, 100, 120);
        Assert.Equal(100, db.Keys.Count(k => db.Lookup(k, 50) is not null));
    }

    [Fact]
    public void Scan_returns_every_key_present_throughout_despite_concurrent_changes()
    {
        Gen.Select(Gen.Int[1, 300], Gen.Int[1, 20], Gen.Int[0, 1000].Array[0, 200]).Sample((initial, count, churn) =>
        {
            var db = new Database();
            for (int i = 0; i < initial; i++)
            {
                db.Upsert(Encoding.ASCII.GetBytes($"stable:{i}"), RString.FromInt(i), 0);
            }

            var seen = new HashSet<string>();
            ulong cursor = 0;
            int step = 0;
            do
            {
                var batch = new List<byte[]>();
                cursor = db.Scan(cursor, count, batch);
                seen.UnionWith(batch.Select(Encoding.ASCII.GetString));

                // Between calls, add and remove keys that are not part of the guarantee.
                if (step < churn.Length)
                {
                    byte[] key = Encoding.ASCII.GetBytes($"churn:{churn[step]}");
                    if (db.Lookup(key, 0) is null)
                    {
                        db.Upsert(key, RString.FromInt(0), 0);
                    }
                    else
                    {
                        db.Delete(key, 0);
                    }
                }

                step++;
            }
            while (cursor != 0);

            for (int i = 0; i < initial; i++)
            {
                Assert.Contains($"stable:{i}", seen);
            }
        });
    }
}
