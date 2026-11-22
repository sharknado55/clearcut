using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Clearcut;

public partial class MainWindow : Window
{
    private enum Tool { Erase, Repair, Magic, Color, Crop, Smooth }
    private sealed record Version(EditState State, long Number);
    private readonly List<Version> undo = [];
    private readonly List<Version> redo = [];
    private EditState? state;
    private EditState? strokeBefore;
    private WriteableBitmap? preview;
    private BitmapSource? original;
    private byte[]? render;
    private string? sourcePath;
    private long currentVersion, nextVersion, savedVersion;
    private Tool tool;
    private Point? strokePoint, cropStart, panFrom, keyboardPoint;
    private Int32Rect? crop;
    private bool busy, showOriginal;
    private int brushSize = 36, smoothRadius = 2;
    private static readonly Brush IdleDot = Frozen(Color.FromRgb(200, 241, 53));
    private static readonly Brush BusyDot = Frozen(Color.FromRgb(251, 191, 36));
    private int BrushRadius => Math.Max(1, (int)SizeSlider.Value / 2);
    private Point Center => new(Canvas.ActualWidth / 2, Canvas.ActualHeight / 2);

    public MainWindow()
    {
        InitializeComponent();
        Canvas.ZoomChanged += scale => ZoomLabel.Text = $"{scale}%";
        RenderOptions.SetBitmapScalingMode(Canvas, BitmapScalingMode.HighQuality);
        EraseChoice.IsChecked = true;
        UpdateControls();
        // Support "Open with" and dropping a file onto the exe.
        Loaded += async (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) await LoadImage(args[1]);
        };
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private async void OnOpen(object sender, RoutedEventArgs e) => await OpenWithDialog();

    private async Task OpenWithDialog()
    {
        if (busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open an image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.tif;*.tiff;*.gif|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await LoadImage(dialog.FileName);
    }

    private async Task LoadImage(string path)
    {
        if (busy || !DiscardEdits()) return;
        await Work("Opening image…", async () =>
        {
            var loaded = await Task.Run(() => EditState.Start(ImageData.Open(path)));
            sourcePath = path;
            undo.Clear();
            redo.Clear();
            currentVersion = savedVersion = ++nextVersion;
            SetState(loaded, fit: true);
            Select(Tool.Erase);
            Status.Text = "Image opened";
        });
        Canvas.Focus();
    }

    private async void OnExport(object sender, RoutedEventArgs e) => await ExportWithDialog();

    private async Task ExportWithDialog()
    {
        if (state is null || busy) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export transparent PNG",
            FileName = Path.GetFileNameWithoutExtension(sourcePath) + "-clearcut.png",
            DefaultExt = ".png",
            Filter = "PNG image|*.png",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var output = state;
        await Work("Exporting PNG…", async () =>
        {
            await Task.Run(() => output.SavePng(dialog.FileName));
            savedVersion = currentVersion;
            Status.Text = $"Exported {Path.GetFileName(dialog.FileName)}";
        });
    }

    private async Task Work(string message, Func<Task> action)
    {
        if (busy) return;
        Status.Text = message;
        busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception error)
        {
            Status.Text = "Couldn’t complete the operation";
            Ask(error is InvalidDataException ? "Can’t open this file" : "Something went wrong", error.Message, "OK", kind: Dialog.Kind.Warning);
        }
        finally
        {
            busy = false;
            UpdateControls();
        }
    }

    private bool DiscardEdits() => state is null || currentVersion == savedVersion ||
        Ask("Discard unsaved changes?", "Your edits haven’t been exported yet. If you continue, they’ll be lost.",
            "Discard", "Keep editing", Dialog.Kind.Danger, focusSecondary: true);

    private bool Ask(string heading, string message, string primary, string? secondary = null,
        Dialog.Kind kind = Dialog.Kind.Info, bool focusSecondary = false)
    {
        Scrim.Visibility = Visibility.Visible;
        try { return Dialog.Show(this, heading, message, primary, secondary, kind, focusSecondary); }
        finally { Scrim.Visibility = Visibility.Collapsed; }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnToggleMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    // A borderless window maximizes over the taskbar by default; clamp it to the monitor's work area.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x24;
        if (message != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new NativePoint(info.Work.Left - info.Monitor.Left, info.Work.Top - info.Monitor.Top);
        limits.MaxSize = new NativePoint(info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        Marshal.StructureToPtr(limits, lParam, false);
        return IntPtr.Zero;
    }

    private record struct NativePoint(int X, int Y);
    private record struct NativeRect(int Left, int Top, int Right, int Bottom);
#pragma warning disable CS0649 // filled in by Windows
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
#pragma warning restore CS0649
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private void OnClosing(object? sender, CancelEventArgs e) => e.Cancel = busy || !DiscardEdits();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await LoadImage(files[0]);
        e.Handled = true;
    }

    private void SetState(EditState edited, bool fit)
    {
        bool newImage = !ReferenceEquals(state?.Image, edited.Image);
        state = edited;
        var image = edited.Image;
        if (newImage || preview is null)
        {
            original = null;
            render = (byte[])image.Pixels.Clone();
            preview = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null);
        }
        showOriginal = false;
        ClearCrop();
        Canvas.Hover = keyboardPoint = null;
        Refresh(new Int32Rect(0, 0, image.Width, image.Height));
        Canvas.Show(preview, fit);
        UpdateControls();
    }

    private void Refresh(Int32Rect region)
    {
        if (state is null || region.IsEmpty) return;
        state.ApplyAlpha(render!, region);
        int stride = state.Image.Width * 4;
        preview!.WritePixels(region, render!, stride, checked(region.Y * stride + region.X * 4));
        Canvas.InvalidateVisual();
    }

    private void Commit(EditState before, EditState after, string description, bool refresh = true)
    {
        bool dimensionsChanged = !ReferenceEquals(before.Image, after.Image);
        if (!dimensionsChanged && before.Mask.AsSpan().SequenceEqual(after.Mask))
        {
            Status.Text = "No pixels changed";
            return;
        }
        undo.Add(new Version(before, currentVersion));
        redo.Clear();
        currentVersion = ++nextVersion;
        if (refresh) SetState(after, dimensionsChanged);
        TrimUndo();
        Status.Text = description;
        UpdateControls();
    }

    private void TrimUndo()
    {
        const long budget = 128 * 1024 * 1024;
        var images = new HashSet<ImageData>(ReferenceEqualityComparer.Instance) { state!.Image };
        long used = 0;
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            var entry = undo[i].State;
            used += entry.Mask.LongLength;
            if (images.Add(entry.Image)) used += entry.Image.Pixels.LongLength;
            if (i < undo.Count - 1 && used > budget)
            {
                undo.RemoveRange(0, i + 1);
                break;
            }
        }
    }

    private void OnUndo(object sender, RoutedEventArgs e) => History(undo, redo, "Undid edit");
    private void OnRedo(object sender, RoutedEventArgs e) => History(redo, undo, "Redid edit");

    private void History(List<Version> from, List<Version> to, string message)
    {
        if (state is null || busy || strokeBefore is not null || from.Count == 0) return;
        to.Add(new Version(state, currentVersion));
        var previous = from[^1];
        from.RemoveAt(from.Count - 1);
        currentVersion = previous.Number;
        SetState(previous.State, !ReferenceEquals(state.Image, previous.State.Image));
        Status.Text = message;
    }

    private void OnOriginal(object sender, RoutedEventArgs e) => ToggleOriginal();

    private void ToggleOriginal()
    {
        if (state is null || busy || strokeBefore is not null) return;
        showOriginal = !showOriginal;
        Canvas.Hover = null;
        Canvas.Show(showOriginal ? original ??= state.Image.Bitmap() : preview, fit: false);
        UpdateControls();
    }

    private void OnBackdrop(object sender, RoutedEventArgs e) => BackdropMenu.IsOpen = !BackdropMenu.IsOpen;

    private void OnBackdropChosen(object sender, RoutedEventArgs e)
    {
        if (BackdropLabel is null || sender is not RadioButton { Tag: string tag, Content: StackPanel row }) return;
        Canvas.Backdrop = int.Parse(tag);
        BackdropSwatch.Background = ((Border)row.Children[0]).Background;
        BackdropLabel.Text = ((TextBlock)row.Children[1]).Text;
        BackdropMenu.IsOpen = false;
        Canvas.InvalidateVisual();
    }

    private void CycleBackdrop()
    {
        RadioButton[] choices = [LightCheckerChoice, DarkCheckerChoice, BlackChoice, WhiteChoice];
        choices[(Canvas.Backdrop + 1) % choices.Length].IsChecked = true;
    }

    private void OnFit(object sender, RoutedEventArgs e) => Canvas.Fit();
    private void OnZoomIn(object sender, RoutedEventArgs e) => Canvas.Zoom(1.25, Center);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Canvas.Zoom(.8, Center);

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if (strokePoint is not null || cropStart is not null) return;
        Canvas.Zoom(Math.Pow(1.2, e.Delta / 120d), e.GetPosition(Canvas));
        e.Handled = true;
    }

    private void Select(Tool choice)
    {
        RadioButton button = choice switch
        {
            Tool.Repair => RepairChoice, Tool.Magic => MagicChoice, Tool.Color => ColorChoice,
            Tool.Crop => CropChoice, Tool.Smooth => SmoothChoice, _ => EraseChoice
        };
        button.IsChecked = true;
    }

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (SizeValue is null || sender is not RadioButton { Tag: string tag }) return;
        if (tool == Tool.Smooth) smoothRadius = (int)SizeSlider.Value;
        else if (tool is Tool.Erase or Tool.Magic or Tool.Repair) brushSize = (int)SizeSlider.Value;
        tool = Enum.Parse<Tool>(tag);
        ClearCrop();
        Canvas.Hover = null;
        bool smoothing = tool == Tool.Smooth;
        SizeSlider.Minimum = smoothing ? 1 : 2;
        SizeSlider.Maximum = smoothing ? 12 : 400;
        SizeSlider.Value = smoothing ? smoothRadius : brushSize;
        OnSettingChanged(SizeSlider, null!);
        (ToolTitle.Text, ToolHelp.Text) = tool switch
        {
            Tool.Erase => ("Erase brush", "Paint over pixels to make them transparent."),
            Tool.Repair => ("Repair brush", "Paint over an erased area to restore the original pixels."),
            Tool.Magic => ("Magic brush", "Click or paint to erase connected colors inside the brush. Lower tolerance to follow subtle edges."),
            Tool.Color => ("Auto Color", "Click to erase one connected area of similar color. Lower tolerance to respect subtle edges."),
            Tool.Crop => ("Crop", "Drag a rectangle over the area you want to keep, then apply the crop."),
            _ => ("Smooth edges", "Soften the transition along the cutout edges.")
        };
        SizeLabel.Text = smoothing ? "Smoothing radius" : "Brush size";
        SizeGroup.Visibility = tool is Tool.Color or Tool.Crop ? Visibility.Collapsed : Visibility.Visible;
        ToleranceGroup.Visibility = tool is Tool.Color or Tool.Magic ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.Visibility = tool is Tool.Crop or Tool.Smooth ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.Content = smoothing ? "Apply smoothing" : "Apply crop";
        UpdateControls();
    }

    private void OnSettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SizeValue is null || ToleranceValue is null || SizeSlider is null || ToleranceSlider is null) return;
        SizeValue.Text = $"{SizeSlider.Value:0} px";
        ToleranceValue.Text = $"{ToleranceSlider.Value:0}%";
        Canvas.CursorRadius = tool is Tool.Erase or Tool.Repair or Tool.Magic ? BrushRadius : 0;
        Canvas.InvalidateVisual();
    }

    private void UpdateControls()
    {
        bool imageOpen = state is not null;
        bool ready = imageOpen && !busy;
        OpenButton.IsEnabled = !busy;
        UndoButton.IsEnabled = ready && undo.Count > 0;
        RedoButton.IsEnabled = ready && redo.Count > 0;
        ExportButton.IsEnabled = ready;
        OriginalButton.IsEnabled = ready;
        OriginalLabel.Text = showOriginal ? "View cutout" : "View original";
        ViewBar.IsEnabled = ready;
        ToolsPanel.IsEnabled = ready && !showOriginal;
        ApplyButton.IsEnabled = ready && (tool == Tool.Smooth || crop.HasValue);
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Fill = busy ? BusyDot : IdleDot;
        EmptyState.Visibility = imageOpen ? Visibility.Collapsed : Visibility.Visible;
        Canvas.IsEnabled = ready;
        Canvas.Cursor = showOriginal ? Cursors.Arrow : Cursors.Cross;
        FileLabel.Text = Path.GetFileName(sourcePath) ?? "No image";
        SizeInfo.Text = imageOpen ? $"{state!.Image.Width:N0} × {state.Image.Height:N0} px" : "";
    }

    private async void OnAutoPhoto(object sender, RoutedEventArgs e) => await AutoRemove(anime: false);

    private async void OnAutoAnime(object sender, RoutedEventArgs e) => await AutoRemove(anime: true);

    private async Task AutoRemove(bool anime)
    {
        if (state is null || busy || showOriginal) return;
        var before = state;
        await Work(anime ? "Finding the anime character…" : "Finding the subject…", async () =>
        {
            var after = await Task.Run(() => AutoClearcut.Remove(before, anime));
            Commit(before, after, "Background removed");
        });
    }

    private async void OnApply(object sender, RoutedEventArgs e) => await ApplyTool();

    private async Task ApplyTool()
    {
        if (state is null || busy || showOriginal) return;
        var before = state;
        if (tool == Tool.Crop && crop is { } area)
        {
            if (area.Width == before.Image.Width && area.Height == before.Image.Height)
            {
                ClearCrop();
                UpdateControls();
                return;
            }
            await Work("Cropping image…", async () =>
            {
                var after = await Task.Run(() => before.Crop(area));
                Commit(before, after, "Image cropped");
            });
        }
        else if (tool == Tool.Smooth)
        {
            smoothRadius = (int)SizeSlider.Value;
            await Work("Smoothing edges…", async () =>
            {
                var after = await Task.Run(() => MaskTools.Smooth(before, smoothRadius));
                Commit(before, after, "Edges smoothed");
            });
        }
    }

    private void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (busy || state is null) return;
        var position = e.GetPosition(Canvas);
        Canvas.Focus();
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            if (strokeBefore is not null || cropStart is not null) return;
            panFrom = position;
            Canvas.CaptureMouse();
            Canvas.Hover = null;
            Canvas.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }
        if (showOriginal || e.ChangedButton != MouseButton.Left || Canvas.ImagePoint(position) is not { } point) return;
        keyboardPoint = point;
        if (tool == Tool.Crop) cropStart = null; // start a fresh rectangle, even after a keyboard crop
        if (tool is Tool.Erase or Tool.Repair or Tool.Magic or Tool.Crop) Canvas.CaptureMouse();
        ActivateAt(point);
        e.Handled = true;
    }

    private async void ActivateAt(Point point)
    {
        if (state is null || busy) return;
        if (tool is Tool.Erase or Tool.Repair or Tool.Magic)
        {
            brushSize = (int)SizeSlider.Value;
            strokeBefore = state.Snapshot();
            strokePoint = point;
            DrawStroke(point, point);
        }
        else if (tool == Tool.Crop)
        {
            cropStart ??= point;
            SetCrop(cropStart!.Value, point);
        }
        else if (tool == Tool.Color)
        {
            var before = state;
            int tolerance = (int)ToleranceSlider.Value;
            await Work("Finding connected colors…", async () =>
            {
                var after = await Task.Run(() => MaskTools.Color(before, point, tolerance));
                Commit(before, after, "Connected colors erased");
                keyboardPoint = point;
            });
            Canvas.Focus();
        }
    }

    private void DrawStroke(Point from, Point to) =>
        Refresh(tool == Tool.Magic
            ? MaskTools.Magic(state!, from, to, BrushRadius, (int)ToleranceSlider.Value)
            : MaskTools.Brush(state!, strokeBefore!.Mask, from, to, BrushRadius, tool == Tool.Repair));

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(Canvas);
        if (panFrom is { } previous)
        {
            Canvas.Pan(position - previous);
            panFrom = position;
            return;
        }
        if (busy || showOriginal || state is null) return;
        var cursor = Canvas.ImagePoint(position, clamp: Canvas.IsMouseCaptured);
        Canvas.Hover = tool is Tool.Smooth or Tool.Crop ? null : cursor;
        if (cursor is { } point)
        {
            keyboardPoint = point;
            if (strokePoint is { } lastPoint &&
                (tool != Tool.Magic || (point - lastPoint).Length >= Math.Max(1, BrushRadius * .2)))
            {
                DrawStroke(lastPoint, point);
                strokePoint = point;
            }
            if (cropStart is { } start) SetCrop(start, point);
        }
        Canvas.InvalidateVisual();
    }

    private void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (!Canvas.IsMouseCaptured) return;
        if (panFrom is not null && e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            Canvas.ReleaseMouseCapture();
            return;
        }
        if (e.ChangedButton != MouseButton.Left || panFrom is not null) return;
        if (Canvas.ImagePoint(e.GetPosition(Canvas), clamp: true) is { } point)
        {
            if (cropStart is { } start) SetCrop(start, point);
            if (strokePoint is { } last && point != last) DrawStroke(last, point);
        }
        Canvas.ReleaseMouseCapture();
    }

    private void OnCanvasLostCapture(object sender, MouseEventArgs e)
    {
        panFrom = null;
        cropStart = null;
        EndStroke();
        UpdateControls();
    }

    private void EndStroke()
    {
        if (strokeBefore is not { } before || state is null) return;
        strokeBefore = null;
        strokePoint = null;
        string description = tool switch { Tool.Repair => "Pixels restored", Tool.Magic => "Connected colors erased", _ => "Pixels erased" };
        Commit(before, state, description, refresh: false);
    }

    private void OnCanvasLeave(object sender, MouseEventArgs e)
    {
        if (Canvas.IsMouseCaptured) return;
        Canvas.Hover = null;
        Canvas.InvalidateVisual();
    }

    private void ClearCrop()
    {
        crop = Canvas.Crop = null;
        cropStart = null;
        Canvas.InvalidateVisual();
    }

    private void SetCrop(Point a, Point b)
    {
        int x = (int)Math.Min(a.X, b.X), y = (int)Math.Min(a.Y, b.Y);
        int width = (int)Math.Max(a.X, b.X) - x + 1, height = (int)Math.Max(a.Y, b.Y) - y + 1;
        crop = Canvas.Crop = new Int32Rect(x, y, width, height);
        Canvas.InvalidateVisual();
        ApplyButton.IsEnabled = true;
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (busy || Canvas.IsMouseCaptured) return;
        if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.O: await OpenWithDialog(); break;
                case Key.S: await ExportWithDialog(); break;
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): History(redo, undo, "Redid edit"); break;
                case Key.Z: History(undo, redo, "Undid edit"); break;
                case Key.Y: History(redo, undo, "Redid edit"); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (state is null || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var choice = e.Key switch
        {
            Key.E => Tool.Erase, Key.R => Tool.Repair, Key.M => Tool.Magic, Key.G => Tool.Color,
            Key.C => Tool.Crop, Key.S => Tool.Smooth, _ => (Tool?)null
        };
        if (choice is { } next && !showOriginal) Select(next);
        else if (e.Key == Key.A) await AutoRemove(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        else if (e.Key == Key.V) ToggleOriginal();
        else if (e.Key == Key.B) CycleBackdrop();
        else if (e.Key is Key.D0 or Key.NumPad0) Canvas.Fit();
        else if (e.Key == Key.Escape && crop is not null) { ClearCrop(); UpdateControls(); }
        else if (e.Key == Key.Enter && Canvas.IsKeyboardFocused && tool is Tool.Crop or Tool.Smooth) await ApplyTool();
        else if (Canvas.IsKeyboardFocused && !showOriginal)
        {
            var image = state.Image;
            var point = keyboardPoint ?? new Point(image.Width / 2d, image.Height / 2d);
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var direction = e.Key switch
            {
                Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0),
                Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => default
            };
            if (direction != default)
            {
                point += direction;
                point = new Point(Math.Clamp(point.X, 0, image.Width - 1), Math.Clamp(point.Y, 0, image.Height - 1));
                keyboardPoint = Canvas.Hover = point;
                Canvas.Reveal(point);
                if (cropStart is { } start) SetCrop(start, point);
            }
            else if (e.Key == Key.Space)
            {
                if (tool == Tool.Crop && cropStart is not null) cropStart = null;
                else ActivateAt(point);
                EndStroke();
            }
            else return;
        }
        else return;
        e.Handled = true;
    }
}
