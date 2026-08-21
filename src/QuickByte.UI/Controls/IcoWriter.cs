using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QuickByte.UI.Controls;

/// <summary>
/// Packs a set of bitmaps into a multi-resolution Windows .ICO stream.
///
/// This exists because <see cref="Icon"/> can only ever be <em>read</em> from
/// the BCL, never written: <c>Icon.FromHandle(bitmap.GetHicon())</c> produces a
/// single-size icon, so Windows ends up scaling one bitmap down for the title
/// bar and the result is visibly soft. Writing the container ourselves lets
/// <see cref="BrandIcon"/> render a purpose-drawn bitmap per size — and lets
/// the checked-in <c>quickbyte.ico</c> be generated from the exact same code
/// that draws the window icon at runtime.
///
/// Entries up to 128 px are written as 32bpp BMP (universally understood,
/// including by <see cref="Icon"/> itself); 256 px is written as PNG, which is
/// the only form the shell accepts at that size without a 256 KB payload.
/// </summary>
public static class IcoWriter
{
    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    /// <summary>
    /// Writes every bitmap in <paramref name="images"/> as one icon directory.
    /// The bitmaps must be square and 256 px or smaller; they are not disposed.
    /// </summary>
    public static void Write(Stream output, IReadOnlyList<Bitmap> images)
    {
        if (images.Count is 0 or > ushort.MaxValue)
            throw new ArgumentException("An icon needs between 1 and 65535 images.", nameof(images));

        var encoded = images.Select(Encode).ToList();

        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: 1 = icon
        writer.Write((ushort)encoded.Count);

        // Image data starts after the directory and all of its entries.
        int offset = IconDirSize + IconDirEntrySize * encoded.Count;
        foreach (var (image, bytes) in encoded)
        {
            // 0 means 256 in a single byte — the reason .ico tops out there.
            writer.Write((byte)(image.Width >= 256 ? 0 : image.Width));
            writer.Write((byte)(image.Height >= 256 ? 0 : image.Height));
            writer.Write((byte)0);            // palette size: 0 = truecolor
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // color planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(bytes.Length);
            writer.Write(offset);
            offset += bytes.Length;
        }

        foreach (var (_, bytes) in encoded)
            writer.Write(bytes);

        writer.Flush();
    }

    private static (Bitmap Image, byte[] Bytes) Encode(Bitmap image)
    {
        if (image.Width != image.Height)
            throw new ArgumentException($"Icon images must be square; got {image.Width}x{image.Height}.", nameof(image));
        if (image.Width > 256)
            throw new ArgumentException($"Icon images cannot exceed 256 px; got {image.Width}.", nameof(image));

        return (image, image.Width >= 256 ? EncodePng(image) : EncodeBmp(image));
    }

    private static byte[] EncodePng(Bitmap image)
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the BMP form of an icon image: a BITMAPINFOHEADER whose height is
    /// doubled (it covers the colour bitmap plus the AND mask), bottom-up BGRA
    /// rows, then the mask itself. The mask is left all-zero — with a real alpha
    /// channel present every pixel is "opaque" as far as the mask is concerned,
    /// and the alpha does the actual shaping.
    /// </summary>
    private static byte[] EncodeBmp(Bitmap image)
    {
        int width = image.Width;
        int height = image.Height;
        int colorBytes = width * height * 4;
        int maskStride = (width + 31) / 32 * 4;   // 1 bpp, rows padded to 4 bytes
        int maskBytes = maskStride * height;

        var buffer = new byte[BitmapInfoHeaderSize + colorBytes + maskBytes];
        using (var stream = new MemoryStream(buffer))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(BitmapInfoHeaderSize);
            writer.Write(width);
            writer.Write(height * 2);         // colour bitmap + AND mask
            writer.Write((ushort)1);          // planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(0);                  // BI_RGB, uncompressed
            writer.Write(colorBytes + maskBytes);
            writer.Write(0);                  // horizontal resolution (unused)
            writer.Write(0);                  // vertical resolution (unused)
            writer.Write(0);                  // palette entries used
            writer.Write(0);                  // palette entries required

            CopyPixelsBottomUp(image, buffer, BitmapInfoHeaderSize);
        }

        return buffer;
    }

    private static void CopyPixelsBottomUp(Bitmap image, byte[] destination, int destinationOffset)
    {
        var rect = new Rectangle(0, 0, image.Width, image.Height);
        var data = image.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = image.Width * 4;
            for (int y = 0; y < image.Height; y++)
            {
                // Icon rows run bottom-up, GDI+ hands them to us top-down.
                nint source = data.Scan0 + (image.Height - 1 - y) * data.Stride;
                Marshal.Copy(source, destination, destinationOffset + y * rowBytes, rowBytes);
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
    }
}
