using System.Diagnostics;
using System.IO;
using System.Windows;
using Clearcut;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = args.Length > 0 ? args[0] : Path.GetTempPath();
        var bytes = new byte[12 * 8 * 4];
        for (int i = 0; i < bytes.Length / 4; i++)
        {
            bool barrier = i % 12 is 5 or 6;
            bytes[i * 4] = barrier ? (byte)15 : (byte)238;
            bytes[i * 4 + 1] = barrier ? (byte)20 : (byte)237;
            bytes[i * 4 + 2] = barrier ? (byte)24 : (byte)239;
            bytes[i * 4 + 3] = i == 0 ? (byte)123 : (byte)255;
        }

        var image = new ImageData(12, 8, bytes);
        var state = EditState.Start(image);
        var point = new Point(1.5, 3.5);
        var snapshot = state.Snapshot();
        MaskTools.Brush(state, snapshot.Mask, point, new Point(10.5, 3.5), 1, repair: false);
        Require(state.Mask[3 * 12 + 8] == 0 && snapshot.Mask[3 * 12 + 8] == 255, "Continuous brush or snapshot failed.");
        MaskTools.Brush(state, state.Snapshot().Mask, point, new Point(10.5, 3.5), 1, repair: true);
        Require(state.Mask.All(alpha => alpha == 255), "Repair did not restore the mask.");

        MaskTools.Magic(state, point, point, 20, 75);
        Require(state.Mask[3 * 12 + 3] == 0 && state.Mask[3 * 12 + 9] == 255, "Magic crossed a disconnected edge.");
        var color = MaskTools.Color(snapshot, point, 22);
        Require(color.Mask[3 * 12 + 3] == 0 && color.Mask[3 * 12 + 9] == 255 && color.Mask[3 * 12 + 6] == 255,
            "Auto Color crossed a different color into a disconnected matching area.");
        Require(MaskTools.Color(color, point, 22).Mask.SequenceEqual(color.Mask),
            "Auto Color erased through an already transparent seed.");
        var diagonal = EditState.Start(new ImageData(2, 2, [255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 0, 255, 255, 255, 255]));
        Require(MaskTools.Color(diagonal, new Point(0, 0), 0).Mask.SequenceEqual(new byte[] { 0, 255, 255, 255 }),
            "Auto Color leaked through original transparency or a diagonal corner.");
        var smooth = MaskTools.Smooth(state, 1);
        Require(smooth.Mask.Any(alpha => alpha is > 0 and < 255), "Smoothing created no intermediate alpha.");
        var crop = color.Crop(new Int32Rect(2, 1, 8, 6));
        Require(crop.Image.Width == 8 && crop.Image.Height == 6 && crop.Mask[0] == 0 && crop.Mask[3] == 255,
            "Crop lost the selected image region or mask.");

        string output = Path.Combine(directory, "pixel-check.png");
        snapshot.SavePng(output);
        var roundTrip = ImageData.Open(output);
        Require(roundTrip.Pixels[3] == 123, "PNG export lost pre-existing alpha.");
        crop.SavePng(output);
        roundTrip = ImageData.Open(output);
        Require(roundTrip.Width == 8 && roundTrip.Height == 6 && roundTrip.Pixels[3] == 0 && roundTrip.Pixels[15] == 255,
            "PNG export lost edited alpha or crop dimensions.");
        Console.WriteLine("Brush, Repair, Magic, Auto Color, Smooth, Crop, PNG and input alpha passed.");

        bool anime = args.Skip(1).FirstOrDefault() == "--anime";
        foreach (string input in args.Skip(anime ? 2 : 1))
        {
            var photo = ImageData.Open(input);
            var clock = Stopwatch.StartNew();
            var removed = AutoClearcut.Remove(EditState.Start(photo), anime);
            string suffix = anime ? "-anime.png" : "-automatic.png";
            string destination = Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + suffix);
            removed.SavePng(destination);
            var check = ImageData.Open(destination);
            int transparent = 0, opaque = 0, partial = 0;
            for (int i = 3; i < check.Pixels.Length; i += 4)
            {
                if (check.Pixels[i] == 0) transparent++;
                else if (check.Pixels[i] == 255) opaque++;
                else partial++;
            }
            Require(check.Width == photo.Width && check.Height == photo.Height && transparent > 0 && opaque > 0 && partial > 0,
                $"Automatic removal did not preserve the photo size and produce all three alpha classes for {input}.");
            Console.WriteLine($"{Path.GetFileName(input)} ({(anime ? "Anime" : "Photo")}): {clock.Elapsed.TotalSeconds:F2}s including PNG verification, " +
                              $"{transparent:N0} transparent, {partial:N0} partial, {opaque:N0} opaque.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
