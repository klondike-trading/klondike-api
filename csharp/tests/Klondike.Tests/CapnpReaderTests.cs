using System.Buffers.Binary;

namespace Klondike.Tests;

public class CapnpReaderTests
{
    private static ulong StructPointer(int offset, int data, int pointers) =>
        ((ulong)(uint)(offset << 2)) | ((ulong)data << 32) | ((ulong)pointers << 48);

    private static ulong ListPointer(int offset, int size, long count) =>
        ((ulong)(uint)(offset << 2)) | 1UL | ((ulong)size << 32) | ((ulong)count << 35);

    private static ulong FarPointer(int offset, int segment, bool doubleFar = false) =>
        2UL | (doubleFar ? 4UL : 0) | ((ulong)offset << 3) | ((ulong)segment << 32);

    private static byte[] Message(params ulong[][] segments)
    {
        var tableWords = ((4 + (segments.Length * 4) + 7) / 8);
        var bytes = new byte[(tableWords + segments.Sum(s => s.Length)) * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)segments.Length - 1);
        var at = tableWords * 8;
        for (var i = 0; i < segments.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (i * 4)), (uint)segments[i].Length);
            foreach (var word in segments[i])
            {
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), word);
                at += 8;
            }
        }
        return bytes;
    }

    [Fact]
    public void UnpacksTheExamplesOfTheSpecification()
    {
        // capnproto.org/encoding.html#packing
        Assert.Equal(
            Convert.FromHexString("08000000030002001900000" + "0AA010000"),
            CapnpMessage.Unpack(Convert.FromHexString("510803023119AA01")));
        Assert.Equal(new byte[8 * 4], CapnpMessage.Unpack([0x00, 0x03]));
        var literal = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(literal, CapnpMessage.Unpack([0xFF, 1, 2, 3, 4, 5, 6, 7, 8, 0x01, 9, 10, 11, 12, 13, 14, 15, 16]));
    }

    [Theory]
    [InlineData("FF01")] // a word of literals cut short
    [InlineData("00")] // no run count
    [InlineData("FF0102030405060708")] // no run count
    [InlineData("FF010203040506070802AA")] // a run cut short
    public void TruncatedPackedDataIsRefused(string hex) =>
        Assert.Throws<FormatException>(() => CapnpMessage.Unpack(Convert.FromHexString(hex)));

    [Fact]
    public void ZeroRunsCannotExhaustMemory()
    {
        var bomb = new byte[400_000];
        for (var i = 0; i < bomb.Length; i += 2)
        {
            bomb[i] = 0x00;
            bomb[i + 1] = 0xFF;
        }
        Assert.Throws<FormatException>(() => CapnpMessage.Unpack(bomb));
    }

    [Fact]
    public void FollowsFarPointers()
    {
        var message = CapnpMessage.Parse(Message(
            [FarPointer(0, 1)],
            [StructPointer(0, 1, 0), 0x1234]));
        Assert.Equal(0x1234, message.Root.UInt16(0));
    }

    [Fact]
    public void FollowsDoubleFarPointers()
    {
        var message = CapnpMessage.Parse(Message(
            [FarPointer(0, 1, doubleFar: true)],
            [FarPointer(0, 2), StructPointer(0, 1, 0)],
            [0x5678]));
        Assert.Equal(0x5678, message.Root.UInt16(0));
    }

    [Fact]
    public void FieldsBeyondTheStructReadAsZero()
    {
        var root = CapnpMessage.Parse(Message([StructPointer(0, 1, 0), 0xFFFFFFFFFFFFFFFF])).Root;
        Assert.Equal(0xFFFF, root.UInt16(6));
        Assert.Equal(0, root.UInt16(8));
        Assert.Equal(0, root.Int64(8));
        Assert.Equal(0d, root.Float64(8));
        Assert.Equal("", root.Text(0));
        Assert.Equal(0, root.List(0).Count);
    }

    [Fact]
    public void NullRootReadsAsEmpty()
    {
        var root = CapnpMessage.Parse(Message([0UL])).Root;
        Assert.Equal(0, root.UInt16(0));
        Assert.Equal("", root.Text(0));
    }

    [Fact]
    public void ReadsTextAndCompositeLists()
    {
        var root = CapnpMessage.Parse(Message(
        [
            StructPointer(0, 0, 2),
            ListPointer(1, 2, 3), // "hi\0"
            ListPointer(1, 7, 2), // two structs of one word
            0x0000000000006968, // "hi"
            StructPointer(2, 1, 0), // tag: two elements
            11,
            22,
        ])).Root;
        Assert.Equal("hi", root.Text(0));
        var list = root.List(1);
        Assert.Equal(2, list.Count);
        Assert.Equal(11, list[0].Int64(0));
        Assert.Equal(22, list[1].Int64(0));
    }

    public static TheoryData<byte[]> Malformed => new()
    {
        Array.Empty<byte>(),
        new byte[4],
        // A second segment whose size is not in the frame.
        new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 },
        // A segment larger than the frame.
        new byte[] { 0, 0, 0, 0, 5, 0, 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void TooShortIsRefused(byte[] bytes) =>
        Assert.Throws<FormatException>(() => CapnpMessage.Parse(bytes));

    [Fact]
    public void PointersOutOfBoundsAreRefused()
    {
        // A struct past the end of the segment.
        Assert.Throws<FormatException>(() => CapnpMessage.Parse(Message([StructPointer(100, 1, 0)])).Root);
        // A negative offset before its start.
        Assert.Throws<FormatException>(() => CapnpMessage.Parse(Message([StructPointer(-100, 1, 0)])).Root);
        // A far pointer to a segment that does not exist.
        Assert.Throws<FormatException>(() => CapnpMessage.Parse(Message([FarPointer(0, 9)])).Root);
        // A text longer than the segment.
        var text = CapnpMessage.Parse(Message([StructPointer(0, 0, 1), ListPointer(0, 2, 1000)])).Root;
        Assert.Throws<FormatException>(() => text.Text(0));
    }

    [Fact]
    public void CompositeListsThatLieAboutTheirSizeAreRefused()
    {
        // The tag claims more elements than the body holds.
        var tooMany = CapnpMessage.Parse(Message([StructPointer(0, 0, 1), ListPointer(0, 7, 1), StructPointer(1000, 1, 0), 0])).Root;
        Assert.Throws<FormatException>(() => tooMany.List(0));
        // Elements without a body: millions of elements in a few bytes.
        var empty = CapnpMessage.Parse(Message([StructPointer(0, 0, 1), ListPointer(0, 7, 0), StructPointer(1 << 20, 0, 0)])).Root;
        Assert.Throws<FormatException>(() => empty.List(0));
    }

    [Fact]
    public void WrongPointerKindsAreRefused()
    {
        var root = CapnpMessage.Parse(Message([StructPointer(0, 0, 2), StructPointer(1, 1, 0), ListPointer(1, 7, 0), 0, StructPointer(0, 0, 0)])).Root;
        Assert.Throws<FormatException>(() => root.List(0)); // a struct, not a list
        Assert.Throws<FormatException>(() => root.Struct(1)); // a list, not a struct
    }

    [Fact]
    public void UnknownUnionMemberIsSkipped()
    {
        // Root with discriminant 99, packed by hand: a word of zeros but for one byte.
        var message = Message([StructPointer(0, 1, 1), 99, 0]);
        Assert.Null(MessageDecoder.DecodeCapnp(Pack(message)));
    }

    [Fact]
    public void LevelOfAnUnknownVenueIsRefused()
    {
        // An order book with no venue and one bid pointing at venue 5.
        var message = Message(
        [
            StructPointer(0, 1, 1), // ServerMessage
            1, // orderbook
            StructPointer(0, 1, 4), // Orderbook
            7, // ts
            0, // symbol
            0, // venues
            ListPointer(1, 7, 3), // bids
            0, // asks
            StructPointer(1, 3, 0), // tag: one Level
            0, 0, 5,
        ]);
        Assert.Throws<FormatException>(() => MessageDecoder.DecodeCapnp(Pack(message)));
    }

    // Every word as literals: valid packing, without the compression.
    private static byte[] Pack(byte[] message)
    {
        using var packed = new MemoryStream();
        for (var i = 0; i < message.Length; i += 8)
        {
            packed.WriteByte(0xFF);
            packed.Write(message, i, 8);
            packed.WriteByte(0);
        }
        return packed.ToArray();
    }
}
