using System.Runtime.CompilerServices;

namespace Ladder.Json;

/// <summary>One bit per nesting level (set = object, clear = array), stored inline so the tokenizer never allocates.</summary>
internal struct DepthBits
{
    public const int Capacity = Words.Length * 64;

    private Words _words;

    public bool this[int depth]
    {
        readonly get => (_words[depth >> 6] & (1UL << (depth & 63))) != 0;
        set
        {
            if (value)
            {
                _words[depth >> 6] |= 1UL << (depth & 63);
            }
            else
            {
                _words[depth >> 6] &= ~(1UL << (depth & 63));
            }
        }
    }

    [InlineArray(Length)]
    private struct Words
    {
        public const int Length = 8;

        private ulong _element;
    }
}
