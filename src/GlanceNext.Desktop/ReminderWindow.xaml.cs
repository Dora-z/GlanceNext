using System.Runtime.InteropServices;
using GlanceNext.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;

namespace GlanceNext.Desktop;

/// <summary>A reusable, non-activating WinUI alert. It never owns keyboard or pointer input.</summary>
public sealed partial class ReminderWindow : Window
{
    private readonly nint hwnd;
    private readonly SubclassProc subclass;
    private readonly List<nint> subclassed = new();
    public BystanderDirection Side { get; }
    public nint Handle => hwnd;
    public ReminderWindow(BystanderDirection side)
    {
        InitializeComponent();
        Side = side;
        Title = "GlanceNext 隐私提醒";
        AlertTitle.Text = side switch { BystanderDirection.Left => "左侧检测到旁观者", BystanderDirection.Right => "右侧检测到旁观者", _ => "检测到旁观者" };
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        subclass = FilterInput;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.IsShownInSwitchers = false;
        var styles = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new nint((styles | 0x080000A0L) & ~0x00040000L)); // NOACTIVATE, TOOLWINDOW, TRANSPARENT; no taskbar button.
        var corner = 2;
        DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        var border = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        AddInputFilter(hwnd);
        Card.Loaded += (_, _) => FilterChildWindows();
        Closed += (_, _) =>
        {
            foreach (var window in subclassed) RemoveWindowSubclass(window, subclass, 1);
            subclassed.Clear();
        };
    }
    public void SetTheme(string theme) => Card.RequestedTheme = theme switch
    { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };

    public void ShowAt(RectInt32 workArea, double scale)
    {
        var width = Math.Min((int)Math.Round(360 * scale), workArea.Width - (int)(48 * scale));
        var height = (int)Math.Round(112 * scale);
        var gap = (int)Math.Round(24 * scale);
        var left = Side switch
        {
            BystanderDirection.Left => workArea.X + gap,
            BystanderDirection.Right => workArea.X + workArea.Width - width - gap,
            _ => workArea.X + (workArea.Width - width) / 2
        };
        AppWindow.MoveAndResize(new RectInt32(left, workArea.Y + gap, width, height));
        AppWindow.Show(false);
        SetWindowPos(hwnd, new nint(-1), left, workArea.Y + gap, width, height, 0x0050);
        FilterChildWindows();
    }
    public void Hide() => AppWindow.Hide();
    private void FilterChildWindows()
    {
        EnumChildWindows(hwnd, (child, _) => { AddInputFilter(child); return true; }, 0);
    }
    private void AddInputFilter(nint window)
    {
        if (subclassed.Contains(window)) return;
        var style = GetWindowLongPtr(window, -20).ToInt64();
        SetWindowLongPtr(window, -20, new nint(style | 0x20));
        SetWindowSubclass(window, subclass, 1, 0);
        subclassed.Add(window);
    }
    private nint FilterInput(nint window, uint message, nuint wp, nint lp, nuint id, nuint data) => message switch
    {
        0x0084 => new nint(-1), // HTTRANSPARENT
        0x0021 => new nint(3), // MA_NOACTIVATE
        _ => DefSubclassProc(window, message, wp, lp)
    };

    // Export only the synthetic card, with the camera stopped in UI verification mode.
    public async Task RenderAsync(string path, double scale)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Card, (int)(Card.ActualWidth * scale), (int)(Card.ActualHeight * scale));
        var pixels = await bitmap.GetPixelsAsync();
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels.ToArray());
        await encoder.FlushAsync();
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint data);
    private delegate bool EnumProc(nint hwnd, nint data);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, EnumProc proc, nint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wp, nint lp);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
