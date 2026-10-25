namespace Ladder.Resp.Storage;

/// <summary>
/// Compares byte-array keys by content, and lets dictionaries be probed with a <see cref="ReadOnlySpan{T}"/>
/// so lookups from the input buffer never allocate a key. Hashes are randomized per process (HashCode), which
/// keeps hash-flooding inputs from degrading the table.
/// </summary>
public sealed class ByteKeyComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static readonly ByteKeyComparer Instance = new();

    private ByteKeyComparer()
    {
    }

    public bool Equals(byte[]? x, byte[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

    public int GetHashCode(byte[] obj) => Hash(obj);

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

    public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = default(HashCode);
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }
}

/// <summary>ASCII case-insensitive variant, used for the command-name table.</summary>
public sealed class AsciiCaseInsensitiveComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static readonly AsciiCaseInsensitiveComparer Instance = new();

    private AsciiCaseInsensitiveComparer()
    {
    }

    public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && System.Text.Ascii.EqualsIgnoreCase(x, y);

    public int GetHashCode(byte[] obj) => Hash(obj);

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => System.Text.Ascii.EqualsIgnoreCase(alternate, other);

    public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = default(HashCode);
        foreach (byte b in bytes)
        {
            hash.Add((byte)(b | 0x20));
        }

        return hash.ToHashCode();
    }
}
