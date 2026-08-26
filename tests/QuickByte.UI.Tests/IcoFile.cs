namespace QuickByte.UI.Tests;

/// <summary>
/// A reader for the .ICO container, so the tests can check what
/// <see cref="QuickByte.UI.Controls.IcoWriter"/> wrote without trusting the
/// thing that wrote it.
///
/// Deliberately not <see cref="System.Drawing.Icon"/>: that would only prove
/// GDI+ can open the file, and it silently picks one image out of the set. The
/// point here is the directory — how many images, at what sizes, at what
/// offsets — which is exactly what Windows reads when it needs a 16 px icon for
/// a title bar and what nothing else in the repo can check.
/// </summary>
internal static class IcoFile
{
    public const int DirectorySize = 6;
    public const int EntrySize = 16;

    public static IcoDirectory Read(byte[] bytes)
    {
        ushort reserved = BitConverter.ToUInt16(bytes, 0);
        ushort type = BitConverter.ToUInt16(bytes, 2);
        ushort count = BitConverter.ToUInt16(bytes, 4);

        var entries = new List<IcoEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int at = DirectorySize + EntrySize * i;

            // 0 means 256: the size is one byte, which is why .ico stops there.
            int width = bytes[at] == 0 ? 256 : bytes[at];
            int height = bytes[at + 1] == 0 ? 256 : bytes[at + 1];
            int byteCount = BitConverter.ToInt32(bytes, at + 8);
            int offset = BitConverter.ToInt32(bytes, at + 12);

            entries.Add(new IcoEntry(
                width, height,
                PaletteSize: bytes[at + 2],
                Planes: BitConverter.ToUInt16(bytes, at + 4),
                BitsPerPixel: BitConverter.ToUInt16(bytes, at + 6),
                ByteCount: byteCount,
                Offset: offset,
                Data: bytes.Skip(offset).Take(byteCount).ToArray()));
        }

        return new IcoDirectory(reserved, type, count, entries);
    }

    public static IcoDirectory Read(string path) => Read(File.ReadAllBytes(path));
}

internal sealed record IcoDirectory(int Reserved, int Type, int Count, IReadOnlyList<IcoEntry> Entries)
{
    public IEnumerable<int> Sizes => Entries.Select(entry => entry.Width);
}

internal sealed record IcoEntry(
    int Width, int Height, int PaletteSize, int Planes, int BitsPerPixel, int ByteCount, int Offset, byte[] Data)
{
    private static readonly byte[] PngSignature = { 0x89, (byte)'P', (byte)'N', (byte)'G' };

    public bool IsPng => Data.Length >= 4 && Data.Take(4).SequenceEqual(PngSignature);

    /// <summary>The BITMAPINFOHEADER of a BMP-encoded entry.</summary>
    public (int HeaderSize, int Width, int Height, int Planes, int BitsPerPixel, int Compression) BitmapHeader => (
        BitConverter.ToInt32(Data, 0),
        BitConverter.ToInt32(Data, 4),
        BitConverter.ToInt32(Data, 8),
        BitConverter.ToUInt16(Data, 12),
        BitConverter.ToUInt16(Data, 14),
        BitConverter.ToInt32(Data, 16));
}
