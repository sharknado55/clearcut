using System.Collections;
using System.Windows;

namespace Clearcut;

internal static class MaskTools
{
    public static Int32Rect Brush(EditState state, byte[] originalMask, Point start, Point end, int radius, bool repair)
    {
        var bounds = new Int32Rect();
        double dx = end.X - start.X, dy = end.Y - start.Y;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / Math.Max(1, radius / 3d)));
        for (int step = 1; step <= steps; step++)
        {
            var point = new Point(start.X + dx * step / steps, start.Y + dy * step / steps);
            var area = Around(state.Image, point, radius);
            for (int y = area.Y; y < area.Y + area.Height; y++)
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                double xDistance = x + .5 - point.X, yDistance = y + .5 - point.Y;
                double distance = Math.Sqrt(xDistance * xDistance + yDistance * yDistance);
                byte amount = (byte)Math.Round(Math.Clamp(radius + .5 - distance, 0, 1) * 255);
                int i = y * state.Image.Width + x;
                state.Mask[i] = repair
                    ? (byte)Math.Max(state.Mask[i], Math.Min(255, originalMask[i] + amount))
                    : (byte)Math.Min(state.Mask[i], Math.Max(0, originalMask[i] - amount));
            }
            bounds = Union(bounds, area);
        }
        return bounds;
    }

    public static Int32Rect Magic(EditState state, Point start, Point end, int radius, int tolerance)
    {
        var delta = end - start;
        int steps = Math.Max(1, (int)Math.Ceiling(delta.Length / Math.Max(1, radius * .7)));
        var bounds = new Int32Rect();
        for (int step = 1; step <= steps; step++)
            bounds = Union(bounds, MagicStamp(state, start + delta * (step / (double)steps), radius, tolerance));
        return bounds;
    }

    private static Int32Rect MagicStamp(EditState state, Point point, int radius, int tolerance)
    {
        var image = state.Image;
        var area = Around(image, point, radius);
        int seedX = (int)point.X, seedY = (int)point.Y;
        if (image.Pixels[(seedY * image.Width + seedX) * 4 + 3] == 0) return Int32Rect.Empty;
        float threshold = .004f * tolerance;
        float stepThreshold = Math.Min(threshold, .018f + .0009f * tolerance);
        float thresholdSquared = threshold * threshold, stepSquared = stepThreshold * stepThreshold;
        int radiusSquared = radius * radius;
        var colors = new PerceptualColor[area.Width * area.Height];
        var visited = new bool[colors.Length];
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            int offset = ((area.Y + y) * image.Width + area.X + x) * 4;
            colors[y * area.Width + x] = PerceptualColor.Read(image.Pixels, offset);
            visited[y * area.Width + x] = image.Pixels[offset + 3] == 0;
        }
        int seedIndex = (seedY - area.Y) * area.Width + seedX - area.X;
        var sample = colors[seedIndex];
        var queue = new int[colors.Length];
        int count = 1;
        visited[seedIndex] = true;
        queue[0] = seedIndex;
        for (int position = 0; position < count; position++)
        {
            int index = queue[position];
            int x = area.X + index % area.Width, y = area.Y + index / area.Width;
            int pixel = y * image.Width + x;
            state.Mask[pixel] = Math.Min(state.Mask[pixel], Remaining(colors[index].DifferenceSquared(sample), threshold));
            Visit(x - 1, y, colors[index]);
            Visit(x + 1, y, colors[index]);
            Visit(x, y - 1, colors[index]);
            Visit(x, y + 1, colors[index]);
        }
        return area;

        void Visit(int x, int y, PerceptualColor previous)
        {
            int localX = x - area.X, localY = y - area.Y;
            if ((uint)localX >= (uint)area.Width || (uint)localY >= (uint)area.Height) return;
            int i = localY * area.Width + localX;
            if (visited[i]) return;
            int dx = x - seedX, dy = y - seedY;
            if (dx * dx + dy * dy > radiusSquared || colors[i].DifferenceSquared(sample) > thresholdSquared)
                visited[i] = true;
            else if (colors[i].DifferenceSquared(previous) <= stepSquared)
            {
                visited[i] = true;
                queue[count++] = i;
            }
        }
    }

    public static EditState Color(EditState original, Point point, int tolerance)
    {
        var image = original.Image;
        var pixels = image.Pixels;
        int width = image.Width;
        int seed = (int)point.Y * width + (int)point.X;
        if (original.Mask[seed] == 0 || pixels[seed * 4 + 3] == 0) return original;

        var state = original.Snapshot();
        var sample = PerceptualColor.Read(pixels, seed * 4);
        float threshold = .004f * tolerance, thresholdSquared = threshold * threshold;
        var visited = new BitArray(state.Mask.Length);
        var pending = new Queue<int>();
        Visit(seed);
        while (pending.TryDequeue(out int index))
        {
            int x = index % width;
            if (x > 0) Visit(index - 1);
            if (x < width - 1) Visit(index + 1);
            if (index >= width) Visit(index - width);
            if (index < state.Mask.Length - width) Visit(index + width);
        }
        return state;

        void Visit(int index)
        {
            if (visited[index]) return;
            visited[index] = true;
            int offset = index * 4;
            if (original.Mask[index] == 0 || pixels[offset + 3] == 0) return;
            float difference = PerceptualColor.Read(pixels, offset).DifferenceSquared(sample);
            if (difference > thresholdSquared) return;
            state.Mask[index] = Math.Min(original.Mask[index], Remaining(difference, threshold));
            pending.Enqueue(index);
        }
    }

    public static EditState Smooth(EditState state, int radius)
    {
        int width = state.Image.Width, height = state.Image.Height;
        var intermediate = new byte[state.Mask.Length];
        var mask = new byte[state.Mask.Length];
        Blur(state.Mask, intermediate, height, width, width, 1);
        Blur(intermediate, mask, width, height, 1, width);
        return new EditState(state.Image, mask);

        void Blur(byte[] source, byte[] destination, int lines, int length, int lineStep, int pixelStep)
        {
            int window = 2 * radius + 1;
            for (int line = 0; line < lines; line++)
            {
                int start = line * lineStep, sum = 0;
                for (int position = -radius; position <= radius; position++)
                    sum += source[start + Math.Clamp(position, 0, length - 1) * pixelStep];
                for (int position = 0; position < length; position++)
                {
                    destination[start + position * pixelStep] = (byte)((sum + window / 2) / window);
                    sum += source[start + Math.Min(position + radius + 1, length - 1) * pixelStep]
                         - source[start + Math.Max(position - radius, 0) * pixelStep];
                }
            }
        }
    }

    private static byte Remaining(float differenceSquared, float threshold)
    {
        float solid = threshold * .75f;
        if (differenceSquared <= solid * solid) return 0;
        float fraction = (MathF.Sqrt(differenceSquared) - solid) / (threshold - solid);
        return (byte)Math.Clamp(MathF.Round(fraction * 255), 0, 255);
    }

    private static Int32Rect Around(ImageData image, Point point, int radius)
    {
        int x = Math.Max(0, (int)Math.Floor(point.X - radius));
        int y = Math.Max(0, (int)Math.Floor(point.Y - radius));
        int right = Math.Min(image.Width, (int)Math.Ceiling(point.X + radius));
        int bottom = Math.Min(image.Height, (int)Math.Ceiling(point.Y + radius));
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (a.IsEmpty) return b;
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Int32Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }
}
