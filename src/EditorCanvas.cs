using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clearcut;

public sealed class EditorCanvas : FrameworkElement
{
    private static readonly DrawingBrush LightChecker = CreateChecker(Paint(222, 224, 232), Paint(197, 200, 212));
    private static readonly DrawingBrush DarkChecker = CreateChecker(Paint(58, 58, 58), Paint(30, 30, 30));
    private static readonly Brush Workspace = Brushes.Transparent; // the host card paints the rounded background
    private static readonly Brush BlackBackdrop = Paint(0, 0, 0);
    private static readonly Brush WhiteBackdrop = Paint(255, 255, 255);
    private static readonly Brush CropShade = Paint(9, 10, 14, 165);
    private static readonly Pen Outline = new(Brushes.Black, 2.5);
    private static readonly Pen CursorLine = new(Brushes.White, 1);
    private static readonly Pen CropLine = new(Paint(200, 241, 53), 1.5);
    private static readonly Pen Guides = new(Paint(255, 255, 255, 145), .8) { DashStyle = DashStyles.Dash };
    private BitmapSource? image;
    private double zoom = 1;
    private Vector offset;

    internal Point? Hover { get; set; }
    internal Int32Rect? Crop { get; set; }
    internal int CursorRadius { get; set; }
    /// <summary>0 light checker, 1 dark checker, 2 solid black, 3 solid white.</summary>
    internal int Backdrop { get; set; }
    internal event Action<int>? ZoomChanged;
    internal double Scale => image is null ? 1 : FitScale * zoom;

    private double FitScale => image is null ? 1 : Math.Max(.0001, Math.Min(
        Math.Min((ActualWidth - 64) / image.PixelWidth, (ActualHeight - 136) / image.PixelHeight), 1));

    private Rect ImageRect
    {
        get
        {
            if (image is null) return Rect.Empty;
            double width = image.PixelWidth * Scale, height = image.PixelHeight * Scale;
            return new Rect((ActualWidth - width) / 2 + offset.X, (ActualHeight - height) / 2 + offset.Y, width, height);
        }
    }

    internal void Show(BitmapSource? bitmap, bool fit)
    {
        image = bitmap;
        if (fit) Fit();
        else InvalidateVisual();
    }

    internal void Fit()
    {
        zoom = 1;
        offset = default;
        Changed();
    }

    internal void Pan(Vector delta)
    {
        offset += delta;
        Changed();
    }

    internal void Zoom(double factor, Point anchor)
    {
        if (image is null) return;
        var oldRect = ImageRect;
        double oldScale = Scale;
        zoom = Math.Clamp(zoom * factor, .25, Math.Max(1, 12 / FitScale));
        Point imagePoint = new((anchor.X - oldRect.X) / oldScale, (anchor.Y - oldRect.Y) / oldScale);
        var newRect = ImageRect;
        offset += anchor - new Point(newRect.X + imagePoint.X * Scale, newRect.Y + imagePoint.Y * Scale);
        Changed();
    }

    internal void Reveal(Point point)
    {
        Rect rect = ImageRect;
        Point screen = new(rect.X + point.X * Scale, rect.Y + point.Y * Scale);
        offset += new Vector(Math.Clamp(screen.X, 12, Math.Max(12, ActualWidth - 12)) - screen.X,
            Math.Clamp(screen.Y, 12, Math.Max(12, ActualHeight - 12)) - screen.Y);
        InvalidateVisual();
    }

    internal Point? ImagePoint(Point point, bool clamp = false)
    {
        if (image is null) return null;
        var rect = ImageRect;
        Point pixel = new((point.X - rect.X) / Scale, (point.Y - rect.Y) / Scale);
        bool inside = pixel.X >= 0 && pixel.Y >= 0 && pixel.X < image.PixelWidth && pixel.Y < image.PixelHeight;
        if (!inside && !clamp) return null;
        return new Point(Math.Clamp(pixel.X, 0, image.PixelWidth - .001), Math.Clamp(pixel.Y, 0, image.PixelHeight - .001));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Changed();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Workspace, null, new Rect(RenderSize));
        if (image is null) return;
        var rect = ImageRect;
        Brush backdrop = Backdrop switch { 1 => DarkChecker, 2 => BlackBackdrop, 3 => WhiteBackdrop, _ => LightChecker };
        drawing.DrawRectangle(backdrop, null, rect);
        drawing.DrawImage(image, rect);
        if (Crop is { } area)
        {
            var box = new Rect(rect.X + area.X * Scale, rect.Y + area.Y * Scale, area.Width * Scale, area.Height * Scale);
            var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
            outside.Children.Add(new RectangleGeometry(rect));
            outside.Children.Add(new RectangleGeometry(box));
            drawing.DrawGeometry(CropShade, null, outside);
            drawing.DrawRectangle(null, CropLine, box);
            for (int i = 1; i < 3; i++)
            {
                double x = box.X + box.Width * i / 3, y = box.Y + box.Height * i / 3;
                drawing.DrawLine(Guides, new Point(x, box.Top), new Point(x, box.Bottom));
                drawing.DrawLine(Guides, new Point(box.Left, y), new Point(box.Right, y));
            }
        }

        if (Hover is { } point)
        {
            var center = new Point(rect.X + point.X * Scale, rect.Y + point.Y * Scale);
            double radius = Math.Max(CursorRadius * Scale, 4);
            drawing.DrawEllipse(null, Outline, center, radius, radius);
            drawing.DrawEllipse(null, CursorLine, center, radius, radius);
        }
    }

    private void Changed()
    {
        ZoomChanged?.Invoke((int)Math.Round(Scale * 100));
        InvalidateVisual();
    }

    private static SolidColorBrush Paint(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static DrawingBrush CreateChecker(Brush light, Brush alternate)
    {
        var tiles = new DrawingGroup();
        using (var drawing = tiles.Open())
        {
            drawing.DrawRectangle(light, null, new Rect(0, 0, 24, 24));
            drawing.DrawRectangle(alternate, null, new Rect(0, 0, 12, 12));
            drawing.DrawRectangle(alternate, null, new Rect(12, 12, 12, 12));
        }
        var brush = new DrawingBrush(tiles)
        {
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile
        };
        brush.Freeze();
        return brush;
    }
}
