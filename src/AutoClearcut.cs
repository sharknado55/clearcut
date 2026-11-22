using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Clearcut;

internal static class AutoClearcut
{
    private const int Side = 1024;
    private static readonly object ModelLock = new();
    private static InferenceSession? model;
    private static bool modelIsAnime;

    public static EditState Remove(EditState state, bool anime = false)
    {
        var image = state.Image;
        double scale = Side / (double)Math.Max(image.Width, image.Height);
        int width = anime ? Math.Clamp((int)Math.Round(image.Width * scale), 1, Side) : Side;
        int height = anime ? Math.Clamp((int)Math.Round(image.Height * scale), 1, Side) : Side;
        int left = (Side - width) / 2, top = (Side - height) / 2;
        var rgb = new float[3 * Side * Side];
        float[] mean = [.485f, .456f, .406f], deviation = [.229f, .224f, .225f];
        for (int channel = 0; channel < 3; channel++)
        {
            int pixelOffset = 2 - channel;
            Func<int, float> read = index => image.Pixels[index * 4 + pixelOffset];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double sourceX = (x + .5) * image.Width / width - .5;
                double sourceY = (y + .5) * image.Height / height - .5;
                float value = Sample(image.Width, image.Height, sourceX, sourceY, read) / 255f;
                rgb[(channel * Side + top + y) * Side + left + x] = anime ? value : (value - mean[channel]) / deviation[channel];
            }
        }

        var (prediction, maskWidth, maskHeight) = Predict(rgb, anime);
        if (!anime)
            for (int i = 0; i < prediction.Length; i++)
                prediction[i] = 1 / (1 + MathF.Exp(-prediction[i]));
        float peak = prediction.Max();
        if (!float.IsFinite(peak) || peak < .45f || peak - prediction.Min() < .1f)
            throw new InvalidOperationException("Automatic removal couldn't identify a subject in this image.");

        var mask = new byte[state.Mask.Length];
        Func<int, float> readMask = index => prediction[index];
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            int i = y * image.Width + x;
            double resizedX = Math.Clamp((x + .5) * width / image.Width - .5, 0, width - 1) + left;
            double resizedY = Math.Clamp((y + .5) * height / image.Height - .5, 0, height - 1) + top;
            float value = Sample(maskWidth, maskHeight, (resizedX + .5) * maskWidth / Side - .5,
                (resizedY + .5) * maskHeight / Side - .5, readMask);
            int alpha = (int)Math.Round(Math.Clamp((value - .025f) / .95f, 0, 1) * 255);
            mask[i] = (byte)Math.Min(state.Mask[i], alpha);
        }
        return new EditState(image, mask);
    }

    private static (float[] Mask, int Width, int Height) Predict(float[] rgb, bool anime)
    {
        lock (ModelLock)
        {
            if (model is null || modelIsAnime != anime)
            {
                model?.Dispose();
                model = null;
                string resource = anime ? "Clearcut.isnet-anime.onnx" : "Clearcut.birefnet-lite.onnx";
                using var weights = typeof(AutoClearcut).Assembly.GetManifestResourceStream(resource)!;
                var bytes = new byte[checked((int)weights.Length)];
                weights.ReadExactly(bytes);
                using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount, 1, 4) };
                model = new InferenceSession(bytes, options);
                modelIsAnime = anime;
            }

            var input = NamedOnnxValue.CreateFromTensor(model.InputNames[0], new DenseTensor<float>(rgb, [1, 3, Side, Side]));
            using var output = model.Run([input], [model.OutputNames[0]]);
            var tensor = output[0].AsTensor<float>();
            return (tensor.ToArray(), tensor.Dimensions[^1], tensor.Dimensions[^2]);
        }
    }

    private static float Sample(int width, int height, double x, double y, Func<int, float> read)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int left = (int)x, top = (int)y;
        int right = Math.Min(left + 1, width - 1), bottom = Math.Min(top + 1, height - 1);
        float fx = (float)(x - left), fy = (float)(y - top);
        float a = read(top * width + left) * (1 - fx) + read(top * width + right) * fx;
        float b = read(bottom * width + left) * (1 - fx) + read(bottom * width + right) * fx;
        return a * (1 - fy) + b * fy;
    }
}
