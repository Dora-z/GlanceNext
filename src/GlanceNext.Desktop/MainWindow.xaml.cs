using System.Diagnostics;
using GlanceNext.Desktop.Services;
using GlanceNext.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GlanceNext.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly WindowsHost host;
    private readonly LocalStore store;
    private readonly MainViewModel viewModel;
    private bool exiting;
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var scale = WindowsHost.GetWindowScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
        AppWindow.Resize(new SizeInt32(Math.Min(display.WorkArea.Width - 48, (int)(1160 * scale)), Math.Min(display.WorkArea.Height - 48, (int)(820 * scale))));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "glancenext.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
        host = new WindowsHost();
        store = new LocalStore();
        viewModel = new(store, host, DispatcherQueue);
        Root.DataContext = viewModel;
        viewModel.ThemeChanged += ApplyTheme;
        ApplyTheme();
        Root.SizeChanged += (_, _) =>
        {
            Navigation.PaneDisplayMode = Root.ActualWidth < 880 ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
            Navigation.IsPaneToggleButtonVisible = Root.ActualWidth < 880;
            Navigation.IsPaneOpen = Root.ActualWidth >= 880;
        };
        Navigation.SelectedItem = Navigation.MenuItems[0];
        AppWindow.Closing += (_, args) => { if (!exiting && host.HasTrayIcon) { args.Cancel = true; AppWindow.Hide(); } };
        host.ShowRequested += () => { AppWindow.Show(); Activate(); };
        host.PauseRequested += () => _ = viewModel.TogglePauseAsync();
        host.EmergencyRequested += () => _ = viewModel.EmergencyAsync();
        host.ExitRequested += () => _ = ExitAsync();
        Root.Loaded += Loaded;
    }
    private async void Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Loaded;
        await viewModel.RefreshDevicesAsync();
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--ui-check"))
        {
            await VerifyUiAsync();
            return;
        }
        if (arguments.Contains("--smoke"))
        {
            await VerifyHardwareAsync();
            return;
        }
        if (arguments.Contains("--recovery-check"))
        {
            await VerifyRecoveryAsync();
            return;
        }
        if (arguments.Contains("--diagnose") || viewModel.Settings.PresenceEnabled || viewModel.Settings.BystanderEnabled || viewModel.Settings.AttentionEnabled)
        {
            Navigation.SelectedItem = Navigation.MenuItems[2];
            await viewModel.StartAsync();
        }
    }
    private void ApplyTheme()
    {
        Root.RequestedTheme = viewModel.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        host.SetTheme(viewModel.Theme);
    }
    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "overview";
        if (OverviewPage is null)
            return;
        OverviewPage.Visibility = tag == "overview" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        DevicePage.Visibility = tag == "device" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageScroller?.ChangeView(null, 0, null, true);
    }
    private void OpenDiagnostics(object sender, RoutedEventArgs e) => Navigation.SelectedItem = Navigation.MenuItems[2];
    private async void StartClicked(object sender, RoutedEventArgs e) => await viewModel.StartAsync();
    private async void StopClicked(object sender, RoutedEventArgs e) => await viewModel.StopAsync();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await viewModel.RefreshDevicesAsync();
    private async void PauseClicked(object sender, RoutedEventArgs e) => await viewModel.TogglePauseAsync();
    private async void EmergencyClicked(object sender, RoutedEventArgs e) => await viewModel.EmergencyAsync();
    private void CalibrateClicked(object sender, RoutedEventArgs e) => viewModel.BeginCalibration();
    private void DirectionClicked(object sender, RoutedEventArgs e) => viewModel.BeginDirectionCalibration();
    private void OpenDataClicked(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", store.DirectoryPath) { UseShellExecute = true });
    }
    private async void ExitClicked(object sender, RoutedEventArgs e) => await ExitAsync();
    private async Task ExitAsync()
    {
        if (exiting)
            return;
        exiting = true;
        await viewModel.DisposeAsync();
        Close();
        Application.Current.Exit();
    }
    public void ClearProtection() => host.ClearOverlay();

    private async Task VerifyHardwareAsync()
    {
        // --smoke makes MainViewModel's ready gate false for every protection action.
        var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification"));
        Directory.CreateDirectory(output);
        Navigation.SelectedItem = Navigation.MenuItems[2];
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var elapsed = Stopwatch.StartNew();
        try
        {
            await viewModel.StartAsync();
            await Task.Delay(12000);
            process.Refresh();
            var report = new
            {
                Timestamp = DateTimeOffset.Now,
                Device = viewModel.SelectedDevice?.Name,
                viewModel.DeviceFormat,
                viewModel.IsRunning,
                viewModel.IsStable,
                viewModel.TotalDiagnosticFrames,
                viewModel.MaximumObservedFaces,
                viewModel.FpsText,
                viewModel.StatusTitle,
                viewModel.StatusDetail,
                AutomationSuppressed = viewModel.DiagnosticOnly,
                WorkingSetMb = process.WorkingSet64 / 1048576d,
                CpuPercent = (process.TotalProcessorTime - cpu).TotalSeconds / elapsed.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
                HumanScenarioVerification = "Pending: weak light, glasses, departure/return, second person, calibrated turn/forward"
            };
            await File.WriteAllTextAsync(Path.Combine(output, "hardware-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "hardware-error.txt"), ex.ToString()); }
        finally { await ExitAsync(); }
    }

    private async Task VerifyRecoveryAsync()
    {
        var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification"));
        Directory.CreateDirectory(output);
        var observations = new List<object>();
        try
        {
            var originalSettings = JsonSerializer.Serialize(viewModel.Settings);
            await viewModel.StartAsync();
            await WaitForReadyAsync();
            observations.Add(new { Stage = "initial", viewModel.IsRunning, viewModel.IsStable });
            for (var cycle = 0; cycle < 2; cycle++)
            {
                await viewModel.DiagnosticSessionAsync(true);
                if (viewModel.IsRunning || viewModel.IsStable) throw new InvalidOperationException("锁屏事件没有释放检测。");
                await viewModel.DiagnosticSessionAsync(true); // Duplicate event must not erase the resume intent.
                await viewModel.DiagnosticSessionAsync(false);
                await WaitForReadyAsync();
                observations.Add(new { Stage = $"resumed-{cycle + 1}", viewModel.IsRunning, viewModel.IsStable });
            }
            await viewModel.TogglePauseAsync();
            await viewModel.DiagnosticSessionAsync(true);
            await viewModel.DiagnosticSessionAsync(false);
            if (viewModel.IsRunning || !viewModel.IsPaused) throw new InvalidOperationException("手动暂停后被错误恢复。");
            if (JsonSerializer.Serialize(viewModel.Settings) != originalSettings) throw new InvalidOperationException("恢复流程改变了用户设置。");
            await File.WriteAllTextAsync(Path.Combine(output, "recovery-report.json"), JsonSerializer.Serialize(new
            {
                Passed = true, AutomationSuppressed = viewModel.DiagnosticOnly, Observations = observations,
                SettingsPreserved = true, RealWindowsLock = "Pending manual sign-in verification; this test injects session events and reopens the real IR device."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "recovery-error.txt"), ex.ToString()); }
        finally { await ExitAsync(); }
    }
    private async Task WaitForReadyAsync()
    {
        var timeout = Stopwatch.StartNew();
        while (!viewModel.IsStable && timeout.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(100);
        if (!viewModel.IsRunning || !viewModel.IsStable) throw new InvalidOperationException("红外设备未恢复：" + viewModel.StatusDetail);
    }

    private async Task VerifyUiAsync()
    {
        // UI screenshots are only allowed in this mode, with capture stopped and preview empty.
        var output = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "verification"));
        Directory.CreateDirectory(output);
        var results = new List<object>();
        try
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                Root.RequestedTheme = theme;
                for (var page = 0; page < 4; page++)
                {
                    Navigation.SelectedItem = Navigation.MenuItems[page];
                    await Task.Delay(250);
                    await RenderUiAsync(Path.Combine(output, $"{theme.ToString().ToLowerInvariant()}-{page}.png"), 1);
                    PageScroller.ChangeView(null, PageScroller.ScrollableHeight, null, true);
                    await Task.Delay(150);
                    await RenderUiAsync(Path.Combine(output, $"{theme.ToString().ToLowerInvariant()}-{page}-bottom.png"), 1);
                    PageScroller.ChangeView(null, 0, null, true);
                    results.Add(new
                    {
                        Theme = theme.ToString(),
                        Page = page,
                        Width = Root.ActualWidth,
                        Height = Root.ActualHeight
                    });
                }
                Navigation.SelectedItem = Navigation.MenuItems[0];
                await Task.Delay(250);
                await RenderUiAsync(Path.Combine(output, $"{theme.ToString().ToLowerInvariant()}-150.png"), 1.5);
                await RenderUiAsync(Path.Combine(output, $"{theme.ToString().ToLowerInvariant()}-200.png"), 2);
            }
            AppWindow.Resize(new SizeInt32(820, 760));
            await Task.Delay(300);
            await RenderUiAsync(Path.Combine(output, "compact-overview.png"), 1);
            await host.VerifyRemindersAsync(output);
            await File.WriteAllTextAsync(Path.Combine(output, "ui-report.json"), JsonSerializer.Serialize(new
            {
                Views = results,
                CameraStarted = false,
                ReminderFocusPreserved = true,
                ReminderPointerHitTestPassed = true,
                Scaling = "Rendered at 100/150/200 percent; live Windows DPI switching requires manual verification"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "ui-error.txt"), ex.ToString()); }
        finally { await ExitAsync(); }
    }
    private async Task RenderUiAsync(string path, double scale)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(Root, (int)(Root.ActualWidth * scale), (int)(Root.ActualHeight * scale));
        var pixels = await target.GetPixelsAsync();
        var file = await StorageFile.GetFileFromPathAsync(await CreateEmptyAsync(path));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)target.PixelWidth, (uint)target.PixelHeight, 96 * scale, 96 * scale, pixels.ToArray());
        await encoder.FlushAsync();
    }
    private static async Task<string> CreateEmptyAsync(string path)
    {
        await File.WriteAllBytesAsync(path, Array.Empty<byte>());
        return path;
    }
}
