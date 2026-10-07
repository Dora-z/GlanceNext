using System.ComponentModel;
using System.Runtime.InteropServices;
using GlanceNext.Core;
using Microsoft.Win32;

namespace GlanceNext.Desktop.Services;

/// <summary>Small Win32 host for tray, hotkeys, session events and non-activating overlays.</summary>
public sealed class WindowsHost : IDisposable
{
    private readonly WndProc callback;
    private readonly string className = "GlanceNextHost_" + Environment.ProcessId;
    private readonly nint hwnd;
    private readonly uint taskbarCreated;
    private readonly uint showExisting;
    private readonly nint trayIcon;
    private nint overlay;
    private readonly Dictionary<BystanderDirection, ReminderWindow> reminders = new();
    private string theme = "System";
    private BystanderDirection currentDirection = BystanderDirection.Unknown;
    private bool disposed;
    private ProtectionAction current;
    public event Action? ShowRequested, PauseRequested, ExitRequested, EmergencyRequested;
    public event Action<bool>? SessionLocked, PowerSuspended;
    public string Shortcut { get; private set; } = "";
    public bool HasTrayIcon
    {
        get; private set;
    }

    public WindowsHost()
    {
        callback = WindowProc;
        var wc = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = callback,
            Instance = GetModuleHandle(null),
            ClassName = className,
            Cursor = LoadCursor(0, 32512)
        };
        if (RegisterClassEx(ref wc) == 0)
            throw new Win32Exception();
        hwnd = CreateWindowEx(0, className, "GlanceNext host", 0, 0, 0, 0, 0, 0, 0, wc.Instance, 0);
        if (hwnd == 0)
            throw new Win32Exception();
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        showExisting = RegisterWindowMessage("GlanceNext.ShowExistingWindow");
        if (!WTSRegisterSessionNotification(hwnd, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听 Windows 锁屏与解锁事件。");
        trayIcon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "glancenext.ico"), 1, 32, 32, 0x10);
        AddTray();
    }

    public void RegisterShortcut(string shortcut)
    {
        uint key = shortcut == "Ctrl+Alt+P" ? 0x50u : shortcut == "Ctrl+Shift+F12" ? 0x7Bu : 0x47u;
        uint modifiers = shortcut == "Ctrl+Shift+F12" ? 0x6u : 0x3u;
        UnregisterHotKey(hwnd, 1);
        if (!RegisterHotKey(hwnd, 1, modifiers | 0x4000, key))
        {
            if (Shortcut.Length > 0)
            {
                uint oldKey = Shortcut == "Ctrl+Alt+P" ? 0x50u : Shortcut == "Ctrl+Shift+F12" ? 0x7Bu : 0x47u;
                uint oldModifiers = Shortcut == "Ctrl+Shift+F12" ? 0x6u : 0x3u;
                if (!RegisterHotKey(hwnd, 1, oldModifiers | 0x4000, oldKey))
                    Shortcut = "";
            }
            throw new InvalidOperationException("快捷键已被其他程序占用，请在设置中选择另一个组合。");
        }
        Shortcut = shortcut;
    }
    public static double GetWindowScale(nint window) => Math.Max(1, GetDpiForWindow(window) / 96d);
    public static void ShowExistingWindow() => SendNotifyMessage(new nint(0xffff), RegisterWindowMessage("GlanceNext.ShowExistingWindow"), 0, 0);

    public void Apply(ProtectionAction action, double dimOpacity, BystanderDirection direction = BystanderDirection.Unknown)
    {
        if (action == ProtectionAction.Lock)
        {
            ClearOverlay();
            if (!LockWorkStation())
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 锁屏失败。");
            return;
        }
        if (action == ProtectionAction.None)
        {
            ClearOverlay();
            return;
        }
        if (action == ProtectionAction.Reminder)
        {
            if (current != action || currentDirection != direction)
            {
                ClearOverlay();
                current = action;
                currentDirection = direction;
                var display = Microsoft.UI.Windowing.DisplayArea.Primary;
                var sides = direction == BystanderDirection.Both ? new[] { BystanderDirection.Left, BystanderDirection.Right } :
                    new[] { direction is BystanderDirection.Left or BystanderDirection.Right ? direction : BystanderDirection.Center };
                var reminderScale = GetDpiForWindow(hwnd) / 96d;
                foreach (var side in sides)
                {
                    if (!reminders.TryGetValue(side, out var reminder)) reminders[side] = reminder = new(side);
                    reminder.SetTheme(theme);
                    reminder.ShowAt(display.WorkArea, reminderScale);
                }
            }
            return;
        }
        if (current == action && overlay != 0)
            return;
        ClearOverlay();
        current = action;
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromPoint(new NativePoint(0, 0), 1), ref monitor);
        var bounds = monitor.Monitor;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var left = bounds.Left;
        var top = bounds.Top;
        // Tool window + no-activate + input transparency keep the user's current window focused.
        overlay = CreateWindowEx(0x080800A8, className, "GlanceNext protection", 0x80000000, left, top, width, height, 0, 0, GetModuleHandle(null), 0);
        if (overlay == 0)
            throw new Win32Exception();
        var alpha = action == ProtectionAction.Dim ? (byte)(Math.Clamp(dimOpacity, 0.2, 0.85) * 255) : (byte)255;
        SetLayeredWindowAttributes(overlay, 0, alpha, 2);
        SetWindowPos(overlay, new nint(-1), left, top, width, height, 0x0050);
        InvalidateRect(overlay, 0, true);
    }
    public void ClearOverlay()
    {
        foreach (var reminder in reminders.Values) reminder.Hide();
        if (overlay != 0)
        {
            DestroyWindow(overlay);
            overlay = 0;
        }
        current = ProtectionAction.None;
        currentDirection = BystanderDirection.Unknown;
    }
    public void SetTheme(string value)
    {
        theme = value;
        foreach (var reminder in reminders.Values) reminder.SetTheme(value);
    }
    public async Task VerifyRemindersAsync(string output)
    {
        var savedTheme = theme;
        var foreground = GetForegroundWindow();
        foreach (var value in new[] { "Light", "Dark" })
        {
            SetTheme(value);
            foreach (var side in new[] { BystanderDirection.Left, BystanderDirection.Right, BystanderDirection.Both, BystanderDirection.Unknown })
            {
                Apply(ProtectionAction.Reminder, 0.55, side);
                await Task.Delay(200);
                if (GetForegroundWindow() != foreground) throw new InvalidOperationException("提醒窗口抢占了焦点。");
                var visible = side == BystanderDirection.Both ? new[] { BystanderDirection.Left, BystanderDirection.Right } :
                    new[] { side == BystanderDirection.Unknown ? BystanderDirection.Center : side };
                foreach (var location in visible)
                {
                    var reminder = reminders[location];
                    GetWindowRect(reminder.Handle, out var bounds);
                    var middle = new NativePoint((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
                    var hit = WindowFromPoint(middle);
                    if (hit == reminder.Handle || IsChild(reminder.Handle, hit)) throw new InvalidOperationException("提醒窗口没有穿透鼠标命中测试。");
                    foreach (var scale in new[] { 1d, 1.5, 2 })
                        await reminder.RenderAsync(Path.Combine(output, $"reminder-{value.ToLowerInvariant()}-{location.ToString().ToLowerInvariant()}-{scale * 100:F0}.png"), scale);
                }
                ClearOverlay();
            }
        }
        SetTheme(savedTheme);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("GlanceNext", $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue("GlanceNext", false);
    }

    private void AddTray()
    {
        var data = TrayData();
        HasTrayIcon = ShellNotifyIcon(0, ref data);
    }
    private NotifyIconData TrayData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = hwnd,
        Id = 1,
        Flags = 7,
        CallbackMessage = 0x8001,
        Icon = trayIcon != 0 ? trayIcon : LoadIcon(0, 32512),
        Tip = "GlanceNext · 仅红外 / 本地处理"
    };
    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (window == hwnd && message == taskbarCreated && taskbarCreated != 0)
            {
                AddTray();
                return 0;
            }
            if (window == hwnd && message == showExisting && showExisting != 0)
            {
                ShowRequested?.Invoke();
                return 0;
            }
            switch (message)
            {
                case 0x8001:
                    if ((uint)lParam == 0x203)
                        ShowRequested?.Invoke();
                    if ((uint)lParam == 0x205)
                        ShowMenu();
                    return 0;
                case 0x0111:
                    switch ((uint)wParam & 0xffff)
                    {
                        case 1:
                            ShowRequested?.Invoke();
                            break;
                        case 2:
                            PauseRequested?.Invoke();
                            break;
                        case 3:
                            ExitRequested?.Invoke();
                            break;
                    }
                    return 0;
                case 0x0312:
                    EmergencyRequested?.Invoke();
                    return 0;
                case 0x02B1:
                    if (wParam == 7)
                        SessionLocked?.Invoke(true);
                    else if (wParam == 8)
                        SessionLocked?.Invoke(false);
                    return 0;
                case 0x0218:
                    if (wParam == 4)
                        PowerSuspended?.Invoke(true);
                    else if (wParam == 6 || wParam == 7 || wParam == 18)
                        PowerSuspended?.Invoke(false);
                    return 1;
                case 0x0084:
                    if (window == overlay)
                        return new nint(-1);
                    break;
                case 0x000F:
                    if (window == overlay)
                    {
                        var hdc = BeginPaint(window, out var paint);
                        GetClientRect(window, out var rect);
                        FillRect(hdc, ref rect, GetStockObject(4));
                        if (current is ProtectionAction.PrivacyMask)
                        {
                            SetTextColor(hdc, 0xFFFFFF);
                            SetBkMode(hdc, 1);
                            var textScale = GetWindowScale(window);
                            var font = CreateFont(-(int)(18 * textScale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Microsoft YaHei UI");
                            var old = SelectObject(hdc, font);
                            var text = "隐私保护已开启\n旁观者离开后将自动恢复\n紧急解除：" + Shortcut;
                            rect.Top = Math.Max(0, (rect.Bottom - (int)(120 * textScale)) / 2);
                            rect.Left += (int)(12 * textScale);
                            rect.Right -= (int)(12 * textScale);
                            DrawText(hdc, text, -1, ref rect, 0x0001 | 0x0020 | 0x0800);
                            SelectObject(hdc, old);
                            DeleteObject(font);
                        }
                        EndPaint(window, ref paint);
                        return 0;
                    }
                    break;
            }
        }
        catch { ClearOverlay(); }
        return DefWindowProc(window, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1, "打开 GlanceNext");
        AppendMenu(menu, 0, 2, "暂停 / 恢复检测");
        AppendMenu(menu, 0, 3, "退出");
        GetCursorPos(out var position);
        SetForegroundWindow(hwnd);
        TrackPopupMenu(menu, 0x0002, position.X, position.Y, 0, hwnd, 0);
        DestroyMenu(menu);
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        ClearOverlay();
        foreach (var reminder in reminders.Values) reminder.Close();
        reminders.Clear();
        UnregisterHotKey(hwnd, 1);
        WTSUnRegisterSessionNotification(hwnd);
        var data = TrayData();
        ShellNotifyIcon(2, ref data);
        if (trayIcon != 0)
            DestroyIcon(trayIcon);
        DestroyWindow(hwnd);
        UnregisterClass(className, GetModuleHandle(null));
        GC.KeepAlive(callback);
    }

    private delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public WndProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PaintStruct
    {
        public nint Hdc; public int Erase; public NativeRect Paint; public int Restore, IncUpdate; public fixed byte Reserved[32];
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Timeout; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle; public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint msg, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint instance, int name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SendNotifyMessage(nint hwnd, uint message, nuint wp, nint lp);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint hdc, ref NativeRect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawText(nint hdc, string text, int count, ref NativeRect rect, uint format);
}
