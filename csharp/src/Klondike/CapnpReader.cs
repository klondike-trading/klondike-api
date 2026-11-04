using System.Buffers.Binary;
using System.Text;

namespace Klondike;

/// <summary>
/// The part of Cap'n Proto the API needs: reads a packed message (structs, lists of structs, text).
/// The only .NET runtime of Cap'n Proto does not read the packed encoding and is not maintained.
/// Everything is bounds checked: a malformed frame is a <see cref="FormatException"/>.
/// </summary>
internal sealed class CapnpMessage
{
    // A packed frame is at most 64 MB, but zero runs expand a thousand times.
    private const int MaxUnpackedBytes = 256 << 20;
    private const int MaxSegments = 512;

    private readonly byte[] _bytes;
    private readonly (int Start, int Words)[] _segments;

    private CapnpMessage(byte[] bytes, (int Start, int Words)[] segments)
    {
        _bytes = bytes;
        _segments = segments;
    }

    public static CapnpMessage ParsePacked(ReadOnlySpan<byte> packed) => Parse(Unpack(packed));

    public static CapnpMessage Parse(byte[] bytes)
    {
        if (bytes.Length < 8)
        {
            throw new FormatException("capnp: message too short");
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes) + 1L;
        if (count > MaxSegments)
        {
            throw new FormatException($"capnp: {count} segments");
        }
        // The table is padded to a whole number of words.
        var start = (4 + (count * 4) + 7) / 8 * 8;
        if (start > bytes.Length)
        {
            throw new FormatException("capnp: truncated segment table");
        }
        var segments = new (int, int)[count];
        for (var i = 0; i < count; i++)
        {
            var words = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 + (i * 4)));
            if (start + (words * 8) > bytes.Length)
            {
                throw new FormatException("capnp: truncated segment");
            }
            segments[i] = ((int)start, (int)words);
            start += words * 8;
        }
        return new CapnpMessage(bytes, segments);
    }

    public static byte[] Unpack(ReadOnlySpan<byte> packed)
    {
        var output = new byte[Math.Max(packed.Length * 2, 64)];
        var length = 0;
        var i = 0;
        while (i < packed.Length)
        {
            var tag = packed[i++];
            Reserve(ref output, length, 8);
            for (var bit = 0; bit < 8; bit++)
            {
                if ((tag & (1 << bit)) != 0)
                {
                    if (i >= packed.Length)
                    {
                        throw new FormatException("capnp: truncated packed word");
                    }
                    output[length + bit] = packed[i++];
                }
                else
                {
                    output[length + bit] = 0;
                }
            }
            length += 8;
            if (tag is not (0x00 or 0xff))
            {
                continue;
            }
            if (i >= packed.Length)
            {
                throw new FormatException("capnp: truncated packed run");
            }
            var run = packed[i++] * 8;
            Reserve(ref output, length, run);
            if (tag == 0x00)
            {
                output.AsSpan(length, run).Clear();
            }
            else
            {
                if (i + run > packed.Length)
                {
                    throw new FormatException("capnp: truncated packed run");
                }
                packed.Slice(i, run).CopyTo(output.AsSpan(length));
                i += run;
            }
            length += run;
        }
        Array.Resize(ref output, length);
        return output;
    }

    private static void Reserve(ref byte[] buffer, int length, int extra)
    {
        if (length + extra > MaxUnpackedBytes)
        {
            throw new FormatException("capnp: message too large");
        }
        if (length + extra > buffer.Length)
        {
            Array.Resize(ref buffer, Math.Min(Math.Max(buffer.Length * 2, length + extra), MaxUnpackedBytes));
        }
    }

    public CapnpStruct Root => Pointer(0, 0, out var target) ? AsStruct(target) : default;

    private readonly record struct Target(int Segment, int Word, ulong Pointer);

    private ulong Word(int segment, int word)
    {
        var (start, words) = _segments[segment];
        if (word < 0 || word >= words)
        {
            throw new FormatException("capnp: pointer out of bounds");
        }
        return BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan(start + (word * 8)));
    }

    private void CheckRange(int segment, long word, long words)
    {
        if (word < 0 || words < 0 || word + words > _segments[segment].Words)
        {
            throw new FormatException("capnp: object out of bounds");
        }
    }

    private int SegmentOf(ulong farPointer)
    {
        var id = (uint)(farPointer >> 32);
        if (id >= _segments.Length)
        {
            throw new FormatException("capnp: far pointer to an unknown segment");
        }
        return (int)id;
    }

    // Resolves the pointer at (segment, word), following far pointers. Target.Word is where the object
    // starts and Target.Pointer the struct or list pointer describing it. False for a null pointer.
    private bool Pointer(int segment, int word, out Target target)
    {
        var p = Word(segment, word);
        target = default;
        if (p == 0)
        {
            return false;
        }
        if ((p & 3) != 2)
        {
            target = new Target(segment, word + 1 + Offset(p), p);
            return true;
        }
        var padSegment = SegmentOf(p);
        var pad = (int)((p >> 3) & 0x1FFFFFFF);
        var pointer = Word(padSegment, pad);
        if ((p & 4) == 0)
        {
            if ((pointer & 3) == 2)
            {
                throw new FormatException("capnp: far pointer to a far pointer");
            }
            target = new Target(padSegment, pad + 1 + Offset(pointer), pointer);
            return true;
        }
        // Double far: the pad holds a far pointer to the object and a tag describing it.
        if ((pointer & 7) != 2)
        {
            throw new FormatException("capnp: malformed double far pointer");
        }
        target = new Target(SegmentOf(pointer), (int)((pointer >> 3) & 0x1FFFFFFF), Word(padSegment, pad + 1));
        return true;
    }

    // Offset in words, signed, of a struct or list pointer.
    private static int Offset(ulong pointer) => (int)(uint)pointer >> 2;

    private CapnpStruct AsStruct(Target t)
    {
        if ((t.Pointer & 3) != 0)
        {
            throw new FormatException("capnp: a struct was expected");
        }
        var dataWords = (int)((t.Pointer >> 32) & 0xFFFF);
        var pointerWords = (int)((t.Pointer >> 48) & 0xFFFF);
        CheckRange(t.Segment, t.Word, dataWords + pointerWords);
        return new CapnpStruct(this, t.Segment, t.Word, dataWords, pointerWords);
    }

    internal CapnpStruct ReadStruct(int segment, int word) =>
        Pointer(segment, word, out var target) ? AsStruct(target) : default;

    internal CapnpList ReadList(int segment, int word)
    {
        if (!Pointer(segment, word, out var t))
        {
            return default;
        }
        if ((t.Pointer & 3) != 1 || ((t.Pointer >> 32) & 7) != 7)
        {
            throw new FormatException("capnp: a list of structs was expected");
        }
        var bodyWords = (long)(t.Pointer >> 35);
        CheckRange(t.Segment, t.Word, 1 + bodyWords);
        var tag = Word(t.Segment, t.Word);
        var count = Offset(tag);
        var dataWords = (int)((tag >> 32) & 0xFFFF);
        var pointerWords = (int)((tag >> 48) & 0xFFFF);
        // Elements without a body would let a small frame claim a huge list.
        if (count < 0 || (tag & 3) != 0 || (count > 0 && dataWords + pointerWords == 0) || count * (long)(dataWords + pointerWords) > bodyWords)
        {
            throw new FormatException("capnp: malformed composite list");
        }
        return new CapnpList(this, t.Segment, t.Word + 1, count, dataWords, pointerWords);
    }

    // The bytes of a text, without its terminating NUL; empty for a null pointer.
    internal ReadOnlySpan<byte> ReadTextBytes(int segment, int word)
    {
        if (!Pointer(segment, word, out var t))
        {
            return default;
        }
        if ((t.Pointer & 3) != 1 || ((t.Pointer >> 32) & 7) != 2)
        {
            throw new FormatException("capnp: a text was expected");
        }
        var count = (long)(t.Pointer >> 35);
        CheckRange(t.Segment, t.Word, (count + 7) / 8);
        return count == 0
            ? default
            : _bytes.AsSpan(_segments[t.Segment].Start + (t.Word * 8), (int)count - 1);
    }

    internal ReadOnlySpan<byte> Data(int segment, int word, int words) =>
        _bytes.AsSpan(_segments[segment].Start + (word * 8), words * 8);
}

/// <summary>A struct of a message. Fields beyond its size read as zero: the schema may grow.</summary>
internal readonly struct CapnpStruct(CapnpMessage message, int segment, int word, int dataWords, int pointerWords)
{
    private ReadOnlySpan<byte> Data => message is null ? default : message.Data(segment, word, dataWords);

    public ushort UInt16(int offset) =>
        offset + 2 <= dataWords * 8 ? BinaryPrimitives.ReadUInt16LittleEndian(Data[offset..]) : (ushort)0;

    public long Int64(int offset) =>
        offset + 8 <= dataWords * 8 ? BinaryPrimitives.ReadInt64LittleEndian(Data[offset..]) : 0;

    public double Float64(int offset) =>
        offset + 8 <= dataWords * 8 ? BinaryPrimitives.ReadDoubleLittleEndian(Data[offset..]) : 0;

    public CapnpStruct Struct(int pointer) =>
        pointer < pointerWords ? message.ReadStruct(segment, word + dataWords + pointer) : default;

    public CapnpList List(int pointer) =>
        pointer < pointerWords ? message.ReadList(segment, word + dataWords + pointer) : default;

    public ReadOnlySpan<byte> TextBytes(int pointer) =>
        pointer < pointerWords ? message.ReadTextBytes(segment, word + dataWords + pointer) : default;

    public string Text(int pointer) => Encoding.UTF8.GetString(TextBytes(pointer));
}

/// <summary>A list of structs of a message.</summary>
internal readonly struct CapnpList(CapnpMessage message, int segment, int word, int count, int dataWords, int pointerWords)
{
    public int Count => count;

    public CapnpStruct this[int index] =>
        new(message, segment, word + (index * (dataWords + pointerWords)), dataWords, pointerWords);
}
