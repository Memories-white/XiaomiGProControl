using System.Runtime.InteropServices;

namespace GProControl.Services;

/// <summary>A small native notification icon hosted on the WinUI thread.</summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint LeftButtonUp = 0x0202;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint RightButtonUp = 0x0205;
    private readonly WindowProcedure _windowProcedure;
    private readonly string _className = $"GProControl.Tray.{Environment.ProcessId}";
    private readonly IntPtr _instance = GetModuleHandle(null);
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly uint _showMainWindow = RegisterWindowMessage("GProControl.ShowMainWindow");
    private IntPtr _window;
    private IntPtr _icon;
    private bool _disposed;
    public bool IsVisible { get; private set; }
    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public TrayIconService(string iconPath)
    {
        _windowProcedure = ProcessMessage;
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = _windowProcedure,
            Instance = _instance,
            ClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0, _className, "G Pro Control Tray", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            UnregisterClass(_className, _instance);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        _icon = LoadImage(IntPtr.Zero, iconPath, 1, 32, 32, 0x0010);
        if (_icon == IntPtr.Zero)
        {
            Dispose();
            throw new IOException("无法加载通知区域图标。");
        }
    }

    public bool Show()
    {
        if (_disposed) return false;
        if (IsVisible) return true;
        var data = CreateData();
        IsVisible = ShellNotifyIcon(0, ref data);
        return IsVisible;
    }

    public void Hide()
    {
        if (!IsVisible) return;
        var data = CreateData();
        ShellNotifyIcon(2, ref data);
        IsVisible = false;
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = 0x1 | 0x2 | 0x4,
        Callback = CallbackMessage,
        Icon = _icon,
        Tip = "G Pro Control — 点击显示，右键退出",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private IntPtr ProcessMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (message == _showMainWindow) { ShowRequested?.Invoke(); return IntPtr.Zero; }
            if (message == _taskbarCreated && IsVisible)
            {
                IsVisible = false;
                Show();
            }
            if (message == CallbackMessage)
            {
                var action = unchecked((uint)lParam.ToInt64());
                if (action is LeftButtonUp or LeftButtonDoubleClick) ShowRequested?.Invoke();
                else if (action == RightButtonUp) ShowMenu();
                return IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("Notification icon callback failed.", exception);
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, new UIntPtr(1), "显示主界面");
            AppendMenu(menu, 0x800, UIntPtr.Zero, null);
            AppendMenu(menu, 0, new UIntPtr(2), "退出软件");
            if (!GetCursorPos(out var point)) return;
            SetForegroundWindow(_window);
            var command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, IntPtr.Zero);
            PostMessage(_window, 0, IntPtr.Zero, IntPtr.Zero);
            if (command == 1) ShowRequested?.Invoke();
            else if (command == 2) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        UnregisterClass(_className, _instance);
        GC.KeepAlive(_windowProcedure);
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rectangle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
