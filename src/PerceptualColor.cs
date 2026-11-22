namespace Clearcut;

// OKLab transformation by Björn Ottosson, https://bottosson.github.io/posts/oklab/
internal readonly record struct PerceptualColor(float Lightness, float A, float B)
{
    private static readonly float[] Linear = Enumerable.Range(0, 256).Select(value =>
    {
        float srgb = value / 255f;
        return srgb <= .04045f ? srgb / 12.92f : MathF.Pow((srgb + .055f) / 1.055f, 2.4f);
    }).ToArray();
    private const int Intervals = 8192;
    private static readonly float[] Roots = Enumerable.Range(0, Intervals + 1).Select(i => MathF.Cbrt(i / (float)Intervals)).ToArray();

    public static PerceptualColor Read(byte[] pixels, int offset)
    {
        float b = Linear[pixels[offset]], g = Linear[pixels[offset + 1]], r = Linear[pixels[offset + 2]];
        float l = Root(.4122214708f * r + .5363325363f * g + .0514459929f * b);
        float m = Root(.2119034982f * r + .6806995451f * g + .1073969566f * b);
        float s = Root(.0883024619f * r + .2817188376f * g + .6299787005f * b);
        return new PerceptualColor(
            .2104542553f * l + .7936177850f * m - .0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + .4505937099f * s,
            .0259040371f * l + .7827717662f * m - .8086757660f * s);
    }

    public float DifferenceSquared(PerceptualColor other)
    {
        float l = Lightness - other.Lightness, a = A - other.A, b = B - other.B;
        return l * l + a * a + b * b;
    }

    private static float Root(float value)
    {
        float index = Math.Clamp(value, 0, 1) * Intervals;
        int floor = (int)index;
        return floor < 8 ? MathF.Cbrt(value)
            : floor == Intervals ? 1
            : Roots[floor] + (Roots[floor + 1] - Roots[floor]) * (index - floor);
    }
}
