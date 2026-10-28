using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace GlanceBar;

internal sealed class PulseWindow : IDisposable
{
    private const string ClassName = "GlanceBar.Window";
    private const string InputClassName = "GlanceBar.Input";
    private const nuint TimerId = 1;
    // Leave room for the hardware glyphs and full-width metric readings at 100% DPI.
    private const int LogicalWidth = 257;
    private const int NetworkIconWidth = 9;
    private const int HardwareIconWidth = 12;
    private const int CommandTaskManager = 1;
    private const int CommandStartup = 2;
    private const int CommandExit = 3;
    private uint _transparentKey;

    private static PulseWindow? _instance;

    private readonly Native.WindowProc _windowProc;
    private readonly Native.WindowProc _inputWindowProc;
    private readonly MetricsSampler _metrics = new();
    private readonly StringBuilder _downloadText = new(12);
    private readonly StringBuilder _uploadText = new(12);
    private readonly StringBuilder _cpuText = new(4);
    private readonly StringBuilder _cpuTemperatureText = new(8);
    private readonly StringBuilder _gpuText = new(4);
    private readonly StringBuilder _memoryText = new(4);

    private HoverTooltip? _tooltip;
    private readonly int? _savedPositionAtStartup = Startup.GetTaskbarPosition();
    private int? _savedPosition;
    private HoverSection _hoverSection;
    private bool _trackingMouse;
    private bool _dragging;
    private bool _dragMoved;
    private Native.POINT _dragStartCursor;
    private Native.RECT _dragStartWindow;
    private int _contentStart;
    private int _networkEnd;
    private int _cpuEnd;
    private int _gpuEnd;
    private int _contentEnd;

    private nint _window;
    private nint _inputWindow;
    private nint _taskbar;
    private nint _font;
    private uint _dpi = 96;
    private uint _taskbarCreatedMessage;
    private int _ticks;
    private bool _lightTheme;
    private bool _disposed;

    internal PulseWindow()
    {
        _instance = this;
        _windowProc = WindowProcedure;
        _inputWindowProc = InputWindowProcedure;
        _savedPosition = _savedPositionAtStartup;
        RefreshTheme();
        _taskbarCreatedMessage = Native.RegisterWindowMessageW("TaskbarCreated");

        var instance = Native.GetModuleHandleW(null);
        var windowClass = new Native.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance = instance,
            hCursor = Native.LoadCursorW(0, new nint(32512)),
            lpszClassName = ClassName
        };

        var atom = Native.RegisterClassW(ref windowClass);
        var error = Marshal.GetLastWin32Error();
        if (atom == 0 && error != 1410)
        {
            throw new Win32Exception(error, "Cannot register the GlanceBar window class.");
        }

        var inputClass = new Native.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_inputWindowProc),
            hInstance = instance,
            hCursor = Native.LoadCursorW(0, new nint(32512)),
            lpszClassName = InputClassName
        };
        atom = Native.RegisterClassW(ref inputClass);
        error = Marshal.GetLastWin32Error();
        if (atom == 0 && error != 1410)
        {
            throw new Win32Exception(error, "Cannot register the GlanceBar input window class.");
        }

        _taskbar = Native.FindWindowW("Shell_TrayWnd", null);
        const uint extendedStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED |
                                   Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
        _window = Native.CreateWindowExW(
            extendedStyle,
            ClassName,
            "GlanceBar",
            Native.WS_POPUP,
            0, 0, 1, 1,
            _taskbar,
            0,
            instance,
            0);

        if (_window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the GlanceBar window.");
        }

        _inputWindow = Native.CreateWindowExW(
            extendedStyle,
            InputClassName,
            string.Empty,
            Native.WS_POPUP,
            0, 0, 1, 1,
            _taskbar,
            0,
            instance,
            0);
        if (_inputWindow == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the GlanceBar input window.");
        }
        Native.SetLayeredWindowAttributes(_inputWindow, 0, 1, Native.LWA_ALPHA);

        Native.SetLayeredWindowAttributes(_window, _transparentKey, 255, Native.LWA_COLORKEY);
        RecreateFont(_taskbar != 0 ? Native.GetDpiForWindow(_taskbar) : 96);
        _tooltip = new HoverTooltip(_window, _lightTheme);
        _metrics.Sample();
        BuildDisplayText();
        PlaceWindow();
        Native.SetTimer(_window, TimerId, 1000, 0);
        Native.ShowWindow(_window, Native.SW_SHOWNOACTIVATE);
        Native.ShowWindow(_inputWindow, Native.SW_SHOWNOACTIVATE);
    }

    internal int Run()
    {
        while (true)
        {
            var result = Native.GetMessageW(out var message, 0, 0, 0);
            if (result == 0)
            {
                return unchecked((int)message.wParam);
            }
            if (result < 0)
            {
                return 1;
            }

            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
        }
    }

    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is null)
        {
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        try
        {
            return instance.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlanceBar-error.log"),
                $"{DateTimeOffset.Now:O} Window message 0x{message:X}: {exception}\r\n");
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private static nint InputWindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is null)
        {
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        try
        {
            return message switch
            {
                Native.WM_MOUSEACTIVATE or
                Native.WM_LBUTTONDOWN or
                Native.WM_MOUSEMOVE or
                Native.WM_LBUTTONUP or
                Native.WM_CAPTURECHANGED or
                Native.WM_MOUSELEAVE or
                Native.WM_RBUTTONUP => instance.HandleMessage(hwnd, message, wParam, lParam),
                Native.WM_ERASEBKGND => 1,
                _ => Native.DefWindowProcW(hwnd, message, wParam, lParam)
            };
        }
        catch (Exception exception)
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlanceBar-error.log"),
                $"{DateTimeOffset.Now:O} Input message 0x{message:X}: {exception}\r\n");
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            _taskbar = 0;
            PlaceWindow();
            return 0;
        }

        switch (message)
        {
            case Native.WM_TIMER:
                PlaceWindow();
                if (++_ticks % 2 == 0)
                {
                    _metrics.Sample();
                    BuildDisplayText();
                    Native.InvalidateRect(hwnd, 0, erase: false);
                    UpdateTooltip(showImmediately: false);
                }
                return 0;

            case Native.WM_PAINT:
                Paint(hwnd);
                return 0;

            case Native.WM_ERASEBKGND:
                return 1;

            case Native.WM_MOUSEACTIVATE:
                return Native.MA_NOACTIVATE;

            case Native.WM_LBUTTONDOWN:
                BeginDrag();
                return 0;

            case Native.WM_MOUSEMOVE:
                HandleMouseMove(hwnd, lParam);
                return 0;

            case Native.WM_LBUTTONUP:
                EndDrag(openTaskManagerOnClick: true);
                return 0;

            case Native.WM_CAPTURECHANGED:
                if (_dragging)
                {
                    EndDrag(openTaskManagerOnClick: false);
                }
                return 0;

            case Native.WM_MOUSELEAVE:
                _trackingMouse = false;
                _hoverSection = HoverSection.None;
                _tooltip?.Hide();
                return 0;

            case Native.WM_RBUTTONUP:
                ShowContextMenu();
                return 0;

            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                PlaceWindow();
                return 0;

            case Native.WM_SETTINGCHANGE:
                RefreshTheme();
                _tooltip?.RefreshTheme(_lightTheme);
                Native.InvalidateRect(hwnd, 0, erase: false);
                PlaceWindow();
                return 0;

            case Native.WM_DESTROY:
                Native.KillTimer(hwnd, TimerId);
                Native.PostQuitMessage(0);
                return 0;

            default:
                return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void BuildDisplayText()
    {
        FormatRate(_downloadText, _metrics.DownloadBytesPerSecond);
        FormatRate(_uploadText, _metrics.UploadBytesPerSecond);
        FormatPercent(_cpuText, _metrics.CpuPercent);
        _cpuTemperatureText.Clear().Append(' ');
        if (_metrics.CpuTemperature is double temperature)
        {
            _cpuTemperatureText.Append(temperature.ToString("0", CultureInfo.InvariantCulture));
        }
        else
        {
            _cpuTemperatureText.Append('–');
        }
        _cpuTemperatureText.Append("°C");
        FormatPercent(_gpuText, _metrics.GpuPercent);
        _memoryText.Clear().Append(Math.Min(_metrics.MemoryPercent, 100));
        PadLeft(_memoryText, 3);
    }

    private static void FormatPercent(StringBuilder target, double value)
    {
        target.Clear();
        if (value < 0 || !double.IsFinite(value))
        {
            target.Append('–');
            PadLeft(target, 3);
            return;
        }
        target.Append(Math.Clamp((int)Math.Round(value), 0, 100));
        PadLeft(target, 3);
    }

    private static void FormatRate(StringBuilder target, double bytesPerSecond)
    {
        target.Clear();
        var unitsPerSecond = Math.Max(0, bytesPerSecond);
        double value;
        char suffix;

        if (unitsPerSecond >= 1_000_000_000)
        {
            value = unitsPerSecond / 1_000_000_000;
            suffix = 'G';
        }
        else if (unitsPerSecond >= 1_000_000)
        {
            value = unitsPerSecond / 1_000_000;
            suffix = 'M';
        }
        else
        {
            value = unitsPerSecond / 1_000;
            suffix = 'K';
        }

        Span<char> buffer = stackalloc char[16];
        var format = value >= 100 ? "0" : "0.0";
        if (value.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture))
        {
            target.Append(buffer[..written]);
        }
        else
        {
            target.Append('0');
        }
        target.Append(suffix);
        PadLeft(target, 5);
    }

    private static void PadLeft(StringBuilder target, int width)
    {
        if (target.Length < width)
        {
            while (target.Length < width)
            {
                target.Insert(0, ' ');
            }
        }
    }

    private void PlaceWindow()
    {
        var taskbar = Native.FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0 || !Native.GetWindowRect(taskbar, out var taskbarRect))
        {
            Native.ShowWindow(_window, Native.SW_HIDE);
            Native.ShowWindow(_inputWindow, Native.SW_HIDE);
            return;
        }

        if (_taskbar != taskbar)
        {
            _taskbar = taskbar;
            Native.SetWindowLongPtr(_window, Native.GWLP_HWNDPARENT, taskbar);
            Native.SetWindowLongPtr(_inputWindow, Native.GWLP_HWNDPARENT, taskbar);
        }

        if (_dragging)
        {
            return;
        }

        if (ShouldHideForPresentation())
        {
            Native.ShowWindow(_window, Native.SW_HIDE);
            Native.ShowWindow(_inputWindow, Native.SW_HIDE);
            return;
        }

        var dpi = Native.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            dpi = 96;
        }
        if (dpi != _dpi)
        {
            RecreateFont(dpi);
        }

        var gap = Scale(6);
        int x;
        int y;
        int width;
        int height;

        if (taskbarRect.Width >= taskbarRect.Height)
        {
            width = Scale(LogicalWidth);
            height = taskbarRect.Height;
            y = taskbarRect.Top;

            var minimumX = taskbarRect.Left + gap;
            var maximumX = taskbarRect.Right - width - gap;
            if (_savedPosition is int savedPosition)
            {
                x = minimumX + (int)((long)(maximumX - minimumX) * savedPosition / 10_000);
            }
            else
            {
                var notificationArea = Native.FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
                if (notificationArea != 0 && Native.GetWindowRect(notificationArea, out var notificationRect) &&
                    notificationRect.Left - width - gap > taskbarRect.Left)
                {
                    x = notificationRect.Left - width - gap;
                }
                else
                {
                    x = taskbarRect.Right - width - Scale(180);
                }
                x = Math.Clamp(x, minimumX, maximumX);
            }
        }
        else
        {
            width = taskbarRect.Width;
            height = Scale(180);
            x = taskbarRect.Left;
            var minimumY = taskbarRect.Top + gap;
            var maximumY = taskbarRect.Bottom - height - gap;
            y = _savedPosition is int savedPosition
                ? minimumY + (int)((long)(maximumY - minimumY) * savedPosition / 10_000)
                : maximumY;
        }

        Native.SetWindowPos(
            _window,
            Native.HWND_TOPMOST,
            x, y, width, height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.SetWindowPos(
            _inputWindow,
            Native.HWND_TOPMOST,
            x, y, width, height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void BeginDrag()
    {
        if (!Native.GetCursorPos(out _dragStartCursor) || !Native.GetWindowRect(_window, out _dragStartWindow))
        {
            return;
        }

        _dragging = true;
        _dragMoved = false;
        _tooltip?.Hide();
        Native.SetCapture(_window);
    }

    private void HandleMouseMove(nint trackingWindow, nint lParam)
    {
        if (_dragging)
        {
            MoveDrag();
            return;
        }

        if (!_trackingMouse)
        {
            var tracking = new Native.TRACKMOUSEEVENT
            {
                size = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                flags = Native.TME_LEAVE,
                hwndTrack = trackingWindow
            };
            _trackingMouse = Native.TrackMouseEvent(ref tracking);
        }

        var x = unchecked((short)(long)lParam);
        var section = HitTest(x);
        if (section != _hoverSection)
        {
            _hoverSection = section;
            UpdateTooltip(showImmediately: true);
        }
    }

    private void MoveDrag()
    {
        if (!Native.GetCursorPos(out var cursor) ||
            !Native.GetWindowRect(_taskbar, out var taskbarRect))
        {
            return;
        }

        var deltaX = cursor.X - _dragStartCursor.X;
        var deltaY = cursor.Y - _dragStartCursor.Y;
        if (Math.Abs(deltaX) >= Scale(2) || Math.Abs(deltaY) >= Scale(2))
        {
            _dragMoved = true;
        }

        var gap = Scale(6);
        var width = _dragStartWindow.Width;
        var height = _dragStartWindow.Height;
        int x;
        int y;
        if (taskbarRect.Width >= taskbarRect.Height)
        {
            x = Math.Clamp(
                _dragStartWindow.Left + deltaX,
                taskbarRect.Left + gap,
                taskbarRect.Right - width - gap);
            y = taskbarRect.Top;
        }
        else
        {
            x = taskbarRect.Left;
            y = Math.Clamp(
                _dragStartWindow.Top + deltaY,
                taskbarRect.Top + gap,
                taskbarRect.Bottom - height - gap);
        }

        Native.SetWindowPos(
            _window,
            Native.HWND_TOPMOST,
            x, y, width, height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void EndDrag(bool openTaskManagerOnClick)
    {
        if (!_dragging)
        {
            return;
        }

        var moved = _dragMoved;
        _dragging = false;
        _dragMoved = false;
        if (Native.GetCapture() == _window)
        {
            Native.ReleaseCapture();
        }

        if (moved)
        {
            SaveCurrentPosition();
            PlaceWindow();
        }
        else if (openTaskManagerOnClick)
        {
            Native.ShellExecuteW(_window, "open", "taskmgr.exe", null, null, 1);
        }
    }

    private void SaveCurrentPosition()
    {
        if (!Native.GetWindowRect(_taskbar, out var taskbarRect) ||
            !Native.GetWindowRect(_window, out var windowRect))
        {
            return;
        }

        var gap = Scale(6);
        int offset;
        int span;
        if (taskbarRect.Width >= taskbarRect.Height)
        {
            offset = windowRect.Left - taskbarRect.Left - gap;
            span = taskbarRect.Width - windowRect.Width - 2 * gap;
        }
        else
        {
            offset = windowRect.Top - taskbarRect.Top - gap;
            span = taskbarRect.Height - windowRect.Height - 2 * gap;
        }

        if (span <= 0)
        {
            return;
        }
        _savedPosition = Math.Clamp((int)Math.Round(offset * 10_000.0 / span), 0, 10_000);
        Startup.SetTaskbarPosition(_savedPosition.Value);
    }

    private HoverSection HitTest(int x)
    {
        if (x < _contentStart || x > _contentEnd)
        {
            return HoverSection.None;
        }
        if (x < _networkEnd)
        {
            return HoverSection.Network;
        }
        if (x < _cpuEnd)
        {
            return HoverSection.Cpu;
        }
        if (x < _gpuEnd)
        {
            return HoverSection.Gpu;
        }
        return HoverSection.Memory;
    }

    private void UpdateTooltip(bool showImmediately)
    {
        if (_hoverSection == HoverSection.None)
        {
            _tooltip?.Hide();
            return;
        }
        _tooltip?.Update(BuildTooltipContent(_hoverSection), showImmediately);
    }

    private TooltipContent BuildTooltipContent(HoverSection section)
    {
        switch (section)
        {
            case HoverSection.Network:
            {
                var topLines = new[]
                {
                    $"网速 · {_metrics.NetworkInterfaceName}",
                    $"下载 {FormatDetailedRate(_metrics.DownloadBytesPerSecond)}    上传 {FormatDetailedRate(_metrics.UploadBytesPerSecond)}"
                };
                if (!_metrics.NetworkDetailsAvailable)
                {
                    return new TooltipContent(topLines, "占用网速",
                        [new TooltipRow(0, "网络采样服务未运行，请重新运行安装程序修复")]);
                }
                if (_metrics.TopNetwork.Length == 0)
                {
                    return new TooltipContent(topLines, "占用网速",
                        [new TooltipRow(0, "暂无明显网络活动")]);
                }

                var rows = new TooltipRow[_metrics.TopNetwork.Length];
                for (var index = 0; index < rows.Length; index++)
                {
                    var process = _metrics.TopNetwork[index];
                    rows[index] = new TooltipRow(
                        process.ProcessId,
                        $"{ShortName(process.Name)}    ↓ {FormatDetailedRate(process.DownloadBytesPerSecond)}    ↑ {FormatDetailedRate(process.UploadBytesPerSecond)}");
                }
                return new TooltipContent(topLines, "占用网速", rows);
            }

            case HoverSection.Cpu:
            {
                var rows = new List<TooltipRow>(5);
                foreach (var process in _metrics.TopCpu)
                {
                    if (process.Percent >= 0.05)
                    {
                        rows.Add(new TooltipRow(
                            process.ProcessId,
                            $"{ShortName(process.Name)}    {process.Percent.ToString("0.0", CultureInfo.InvariantCulture)}%"));
                    }
                }
                if (rows.Count == 0)
                {
                    rows.Add(new TooltipRow(0, "暂无明显 CPU 活动"));
                }
                return new TooltipContent(
                    [$"CPU  {Math.Round(_metrics.CpuPercent)}%    温度  {_cpuTemperatureText.ToString().TrimStart()}"],
                    "占用 CPU",
                    rows.ToArray());
            }

            case HoverSection.Gpu:
                return new TooltipContent(
                    [_metrics.GpuPercent < 0
                        ? "GPU 总利用率  不可用"
                        : $"GPU 总利用率  {Math.Round(_metrics.GpuPercent)}%"],
                    null,
                    []);

            case HoverSection.Memory:
            {
                var rows = new TooltipRow[_metrics.TopMemory.Length];
                for (var index = 0; index < rows.Length; index++)
                {
                    var process = _metrics.TopMemory[index];
                    rows[index] = new TooltipRow(
                        process.ProcessId,
                        $"{ShortName(process.Name)}    {FormatMemory((ulong)Math.Max(0, process.WorkingSetBytes))}");
                }
                return new TooltipContent(
                    [$"内存  {_metrics.MemoryPercent}%    {FormatMemory(_metrics.UsedMemoryBytes)} / {FormatMemory(_metrics.TotalMemoryBytes)}"],
                    "占用内存",
                    rows);
            }

            default:
                return new TooltipContent([], null, []);
        }
    }

    private static string FormatDetailedRate(double bytesPerSecond)
    {
        var rate = Math.Max(0, bytesPerSecond);
        return rate switch
        {
            >= 1_000_000_000 => $"{rate / 1_000_000_000:0.00} GB/s",
            >= 1_000_000 => $"{rate / 1_000_000:0.00} MB/s",
            >= 1_000 => $"{rate / 1_000:0.0} KB/s",
            _ => $"{rate:0} B/s"
        };
    }

    private static string FormatMemory(ulong bytes) =>
        bytes >= 1_000_000_000
            ? $"{bytes / 1_000_000_000.0:0.00} GB"
            : $"{bytes / 1_000_000.0:0} MB";

    private static string ShortName(string name) =>
        name.Length <= 24 ? name : string.Concat(name.AsSpan(0, 23), "…");

    private static bool ShouldHideForPresentation()
    {
        if (Native.SHQueryUserNotificationState(out var state) != 0)
        {
            return false;
        }

        const int runningDirect3DFullScreen = 3;
        const int presentationMode = 4;
        return state is runningDirect3DFullScreen or presentationMode;
    }

    private void RefreshTheme()
    {
        _lightTheme = Startup.SystemUsesLightTheme();
        _transparentKey = _lightTheme
            ? Native.Color(243, 243, 243)
            : Native.Color(32, 32, 32);

        if (_window != 0)
        {
            Native.SetLayeredWindowAttributes(_window, _transparentKey, 255, Native.LWA_COLORKEY);
        }
    }

    private void RecreateFont(uint dpi)
    {
        _dpi = dpi;
        if (_font != 0)
        {
            Native.DeleteObject(_font);
        }

        var height = -Native.MulDiv(8, (int)dpi, 72);
        _font = Native.CreateFontW(
            height, 0, 0, 0, Native.FW_SEMIBOLD,
            0, 0, 0, 1, 0, 0, 4, 0,
            "Bahnschrift");
    }

    private int Scale(int logicalPixels) => Native.MulDiv(logicalPixels, (int)_dpi, 96);

    private void Paint(nint hwnd)
    {
        var target = Native.BeginPaint(hwnd, out var paint);
        try
        {
            if (!Native.GetClientRect(hwnd, out var client) || client.Width <= 0 || client.Height <= 0)
            {
                return;
            }

            var memoryDc = Native.CreateCompatibleDC(target);
            var bitmap = Native.CreateCompatibleBitmap(target, client.Width, client.Height);
            var oldBitmap = Native.SelectObject(memoryDc, bitmap);

            try
            {
                var keyBrush = Native.CreateSolidBrush(_transparentKey);
                Native.FillRect(memoryDc, ref client, keyBrush);
                Native.DeleteObject(keyBrush);

                var textColor = _lightTheme
                    ? Native.Color(31, 31, 31)
                    : Native.Color(235, 235, 237);
                var mutedColor = _lightTheme
                    ? Native.Color(102, 102, 107)
                    : Native.Color(174, 174, 181);
                var downloadColor = _lightTheme
                    ? Native.Color(0, 103, 192)
                    : Native.Color(126, 200, 246);
                var uploadColor = _lightTheme
                    ? Native.Color(119, 72, 168)
                    : Native.Color(198, 160, 232);

                var oldFont = Native.SelectObject(memoryDc, _font);
                Native.SetBkMode(memoryDc, Native.TRANSPARENT);
                Native.GetTextMetricsW(memoryDc, out var textMetrics);

                var networkIconWidth = Scale(NetworkIconWidth);
                var hardwareIconWidth = Scale(HardwareIconWidth);
                var tinyGap = Scale(2);
                var sectionGap = Scale(5);
                var totalWidth =
                    networkIconWidth + tinyGap + Measure(memoryDc, _downloadText) + sectionGap +
                    networkIconWidth + tinyGap + Measure(memoryDc, _uploadText) + sectionGap +
                    hardwareIconWidth + tinyGap + Measure(memoryDc, _cpuText) + sectionGap +
                    Measure(memoryDc, _cpuTemperatureText) +
                    hardwareIconWidth + tinyGap + Measure(memoryDc, _gpuText) + sectionGap +
                    hardwareIconWidth + tinyGap + Measure(memoryDc, _memoryText);

                var x = Math.Max(Scale(5), (client.Width - totalWidth) / 2);
                var y = Math.Max(0, (client.Height - textMetrics.height) / 2);
                _contentStart = x;

                x = DrawStatusIcon(memoryDc, x, y, textMetrics.height, StatusIcon.Download, downloadColor) + tinyGap;
                x = Draw(memoryDc, x, y, _downloadText, textColor) + sectionGap;
                x = DrawStatusIcon(memoryDc, x, y, textMetrics.height, StatusIcon.Upload, uploadColor) + tinyGap;
                x = Draw(memoryDc, x, y, _uploadText, textColor) + sectionGap;
                _networkEnd = x - sectionGap / 2;
                x = DrawStatusIcon(memoryDc, x, y, textMetrics.height, StatusIcon.Cpu, mutedColor) + tinyGap;
                x = Draw(memoryDc, x, y, _cpuText, textColor);
                x = Draw(memoryDc, x, y, _cpuTemperatureText, textColor) + sectionGap;
                _cpuEnd = x - sectionGap / 2;
                x = DrawStatusIcon(memoryDc, x, y, textMetrics.height, StatusIcon.Gpu, mutedColor) + tinyGap;
                x = Draw(memoryDc, x, y, _gpuText, textColor) + sectionGap;
                _gpuEnd = x - sectionGap / 2;
                x = DrawStatusIcon(memoryDc, x, y, textMetrics.height, StatusIcon.Memory, mutedColor) + tinyGap;
                _contentEnd = Draw(memoryDc, x, y, _memoryText, textColor);

                Native.SelectObject(memoryDc, oldFont);
                Native.BitBlt(target, 0, 0, client.Width, client.Height, memoryDc, 0, 0, Native.SRCCOPY);
            }
            finally
            {
                Native.SelectObject(memoryDc, oldBitmap);
                Native.DeleteObject(bitmap);
                Native.DeleteDC(memoryDc);
            }
        }
        finally
        {
            Native.EndPaint(hwnd, ref paint);
        }
    }

    // Draw compact, DPI-scaled status glyphs and return the start of the following gap.
    private int DrawStatusIcon(nint hdc, int x, int y, int textHeight, StatusIcon icon, uint color)
    {
        var hardware = icon is StatusIcon.Cpu or StatusIcon.Gpu or StatusIcon.Memory;
        // Scale grid coordinates independently so fractional DPI keeps the geometry aligned.
        Span<int> unit = stackalloc int[HardwareIconWidth];
        if (hardware)
        {
            for (var coordinate = 1; coordinate < unit.Length; coordinate++)
            {
                unit[coordinate] = Scale(coordinate);
            }
        }
        var one = hardware ? unit[1] : Scale(1);
        var size = hardware ? unit[HardwareIconWidth - 1] : Scale(NetworkIconWidth - 1);
        var left = x;
        var top = y + Math.Max(0, (textHeight - size) / 2);
        var right = left + size;
        var bottom = top + size;
        var centerX = (left + right) / 2;
        var centerY = (top + bottom) / 2;
        var pen = Native.CreatePen(Native.PS_SOLID, Math.Max(1, one), color);
        var oldPen = Native.SelectObject(hdc, pen);

        switch (icon)
        {
            case StatusIcon.Download:
                DrawLine(hdc, centerX, top, centerX, bottom - one);
                DrawLine(hdc, centerX, bottom - one, left + one, centerY);
                DrawLine(hdc, centerX, bottom - one, right - one, centerY);
                break;
            case StatusIcon.Upload:
                DrawLine(hdc, centerX, bottom, centerX, top + one);
                DrawLine(hdc, centerX, top + one, left + one, centerY);
                DrawLine(hdc, centerX, top + one, right - one, centerY);
                break;
            case StatusIcon.Cpu:
                // A square package, central die, and two pins on each side.
                DrawBox(hdc, left + unit[2], top + unit[2], left + unit[8], top + unit[8]);
                DrawBox(hdc, left + unit[4], top + unit[4], left + unit[6], top + unit[6]);
                for (var pin = 4; pin <= 6; pin += 2)
                {
                    var offset = unit[pin];
                    DrawLine(hdc, left + offset, top, left + offset, top + unit[2]);
                    DrawLine(hdc, left + offset, top + unit[8], left + offset, top + unit[11]);
                    DrawLine(hdc, left, top + offset, left + unit[2], top + offset);
                    DrawLine(hdc, left + unit[8], top + offset, left + unit[11], top + offset);
                }
                break;
            case StatusIcon.Gpu:
                // The bracket and edge contacts distinguish the card from a monitor.
                DrawLine(hdc, left, top + unit[1], left, top + unit[10]);
                DrawBox(hdc, left + unit[1], top + unit[2], left + unit[10], top + unit[8]);
                DrawLine(hdc, left + unit[6], top + unit[3], left + unit[8], top + unit[5]);
                DrawLine(hdc, left + unit[8], top + unit[5], left + unit[6], top + unit[7]);
                DrawLine(hdc, left + unit[6], top + unit[7], left + unit[4], top + unit[5]);
                DrawLine(hdc, left + unit[4], top + unit[5], left + unit[6], top + unit[3]);
                DrawLine(hdc, left + unit[4], top + unit[8], left + unit[4], top + unit[11]);
                DrawLine(hdc, left + unit[7], top + unit[8], left + unit[7], top + unit[11]);
                break;
            case StatusIcon.Memory:
                // Three memory chips sit above the contacts of a DIMM.
                DrawBox(hdc, left, top + unit[2], left + unit[10], top + unit[7]);
                for (var chip = 2; chip <= 8; chip += 3)
                {
                    DrawLine(hdc, left + unit[chip], top + unit[4], left + unit[chip], top + unit[6]);
                }
                for (var pin = 2; pin <= 8; pin += 2)
                {
                    DrawLine(hdc, left + unit[pin], top + unit[7], left + unit[pin], top + unit[10]);
                }
                break;
        }

        Native.SelectObject(hdc, oldPen);
        Native.DeleteObject(pen);
        return x + Scale(hardware ? HardwareIconWidth : NetworkIconWidth);
    }

    private static void DrawLine(nint hdc, int x1, int y1, int x2, int y2)
    {
        Native.MoveToEx(hdc, x1, y1, 0);
        Native.LineTo(hdc, x2, y2);
    }

    private static void DrawBox(nint hdc, int left, int top, int right, int bottom)
    {
        DrawLine(hdc, left, top, right, top);
        DrawLine(hdc, right, top, right, bottom);
        DrawLine(hdc, right, bottom, left, bottom);
        DrawLine(hdc, left, bottom, left, top);
    }

    private static int Measure(nint hdc, string text)
    {
        Native.MeasureString(hdc, text, text.Length, out var size);
        return size.Width;
    }

    private static int Measure(nint hdc, StringBuilder text)
    {
        Native.MeasureBuffer(hdc, text, text.Length, out var size);
        return size.Width;
    }

    private static int Draw(nint hdc, int x, int y, string text, uint color)
    {
        Native.SetTextColor(hdc, color);
        Native.TextOutString(hdc, x, y, text, text.Length);
        return x + Measure(hdc, text);
    }

    private static int Draw(nint hdc, int x, int y, StringBuilder text, uint color)
    {
        Native.SetTextColor(hdc, color);
        Native.TextOutBuffer(hdc, x, y, text, text.Length);
        return x + Measure(hdc, text);
    }

    private void ShowContextMenu()
    {
        if (!Native.GetCursorPos(out var point))
        {
            return;
        }

        var menu = Native.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, CommandTaskManager, "打开任务管理器");
            var startupFlags = Native.MF_STRING | (Startup.IsEnabled() ? Native.MF_CHECKED : 0);
            Native.AppendMenuW(menu, startupFlags, CommandStartup, "开机启动");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, CommandExit, "退出 GlanceBar");
            Native.SetForegroundWindow(_window);

            var command = Native.TrackPopupMenu(
                menu,
                Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                point.X,
                point.Y,
                0,
                _window,
                0);

            switch (command)
            {
                case CommandTaskManager:
                    Native.ShellExecuteW(_window, "open", "taskmgr.exe", null, null, 1);
                    break;
                case CommandStartup:
                    Startup.SetEnabled(!Startup.IsEnabled());
                    break;
                case CommandExit:
                    Native.DestroyWindow(_window);
                    break;
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tooltip?.Dispose();
        _tooltip = null;
        if (_inputWindow != 0)
        {
            Native.DestroyWindow(_inputWindow);
            _inputWindow = 0;
        }
        if (_window != 0)
        {
            Native.DestroyWindow(_window);
            _window = 0;
        }
        if (_font != 0)
        {
            Native.DeleteObject(_font);
            _font = 0;
        }
        _metrics.Dispose();
        _instance = null;
    }
}

internal enum HoverSection
{
    None,
    Network,
    Cpu,
    Gpu,
    Memory
}

internal enum StatusIcon
{
    Download,
    Upload,
    Cpu,
    Gpu,
    Memory
}
