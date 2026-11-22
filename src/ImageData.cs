using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearcut;

internal sealed record ImageData(int Width, int Height, byte[] Pixels)
{
    public static ImageData Open(string path)
    {
        using var input = File.OpenRead(path);
        BitmapDecoder decoder;
        try { decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None); }
        catch (Exception error) when (error is NotSupportedException or FileFormatException or ArgumentException)
        {
            throw new InvalidDataException("This file isn't an image Clearcut can open. Try a PNG, JPG, BMP, TIFF or GIF.", error);
        }
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;
        if ((long)width * height > 40_000_000)
            throw new InvalidDataException("Choose an image under 40 megapixels.");

        int orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                foreach (var key in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    if (metadata.GetQuery(key) is ushort value)
                    {
                        orientation = value;
                        break;
                    }
                }
            }
        }
        catch (Exception) { } // Orientation is best effort; some formats reject these metadata queries.

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        if (orientation is < 2 or > 8)
            return new ImageData(width, height, pixels);

        int outputWidth = orientation >= 5 ? height : width;
        int outputHeight = orientation >= 5 ? width : height;
        var output = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            (int ox, int oy) = orientation switch
            {
                2 => (width - 1 - x, y),
                3 => (width - 1 - x, height - 1 - y),
                4 => (x, height - 1 - y),
                5 => (y, x),
                6 => (height - 1 - y, x),
                7 => (height - 1 - y, width - 1 - x),
                _ => (y, width - 1 - x)
            };
            Buffer.BlockCopy(pixels, 4 * (y * width + x), output, 4 * (oy * outputWidth + ox), 4);
        }
        return new ImageData(outputWidth, outputHeight, output);
    }

    public BitmapSource Bitmap() => BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, Width * 4);
}

internal sealed record EditState(ImageData Image, byte[] Mask)
{
    public static EditState Start(ImageData image)
    {
        var mask = new byte[image.Width * image.Height];
        Array.Fill(mask, byte.MaxValue);
        return new EditState(image, mask);
    }

    public EditState Snapshot() => new(Image, (byte[])Mask.Clone());

    public EditState Crop(Int32Rect crop)
    {
        var pixels = new byte[crop.Width * crop.Height * 4];
        var mask = new byte[crop.Width * crop.Height];
        for (int y = 0; y < crop.Height; y++)
        {
            int source = (crop.Y + y) * Image.Width + crop.X;
            int destination = y * crop.Width;
            Buffer.BlockCopy(Image.Pixels, source * 4, pixels, destination * 4, crop.Width * 4);
            Buffer.BlockCopy(Mask, source, mask, destination, crop.Width);
        }
        return new EditState(new ImageData(crop.Width, crop.Height, pixels), mask);
    }

    public void ApplyAlpha(byte[] pixels, Int32Rect region)
    {
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            int end = y * Image.Width + region.X + region.Width;
            for (int i = y * Image.Width + region.X; i < end; i++)
            {
                int alpha = i * 4 + 3;
                pixels[alpha] = (byte)((Image.Pixels[alpha] * Mask[i] + 127) / 255);
            }
        }
    }

    public void SavePng(string path)
    {
        var pixels = (byte[])Image.Pixels.Clone();
        ApplyAlpha(pixels, new Int32Rect(0, 0, Image.Width, Image.Height));
        var bitmap = BitmapSource.Create(Image.Width, Image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, Image.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".clearcut-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew)) encoder.Save(file);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
