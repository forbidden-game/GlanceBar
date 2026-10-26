using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarPulse;

internal readonly record struct TooltipRow(int ProcessId, string Text);

internal sealed class TooltipContent(string[] topLines, string? sectionTitle, TooltipRow[] rows)
{
    internal string[] TopLines { get; } = topLines;
    internal string? SectionTitle { get; } = sectionTitle;
    internal TooltipRow[] Rows { get; } = rows;
}

internal sealed class HoverTooltip : IDisposable
{
    private const string ClassName = "TaskbarPulse.Tooltip";
    private static HoverTooltip? _instance;

    private readonly Native.WindowProc _windowProc;
    private readonly uint _dpi;
    private readonly Dictionary<int, nint> _icons = new();
    private nint _window;
    private nint _font;
    private TooltipContent? _content;
    private bool _lightTheme;
    private bool _visible;

    internal HoverTooltip(nint owner, bool lightTheme)
    {
        _instance = this;
        _windowProc = WindowProcedure;
        _lightTheme = lightTheme;
        _dpi = Math.Max(Native.GetDpiForWindow(owner), 96);

        var module = Native.GetModuleHandleW(null);
        var windowClass = new Native.WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            hInstance = module,
            lpszClassName = ClassName
        };
        var atom = Native.RegisterClassW(ref windowClass);
        var error = Marshal.GetLastWin32Error();
        if (atom == 0 && error != 1410)
        {
            throw new Win32Exception(error, "Cannot register the TaskbarPulse tooltip class.");
        }

        _window = Native.CreateWindowExW(
            Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE |
            Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT,
            ClassName,
            string.Empty,
            Native.WS_POPUP,
            0, 0, 1, 1,
            owner,
            0,
            module,
            0);
        if (_window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the TaskbarPulse tooltip window.");
        }

        Native.SetLayeredWindowAttributes(_window, 0, 244, Native.LWA_ALPHA);
        var fontHeight = -Native.MulDiv(9, (int)_dpi, 72);
        _font = Native.CreateFontW(
            fontHeight, 0, 0, 0, Native.FW_SEMIBOLD,
            0, 0, 0, 1, 0, 0, 4, 0,
            "Segoe UI Variable Text");
    }

    internal void Update(TooltipContent content, bool showImmediately)
    {
        if (_window == 0)
        {
            return;
        }

        _content = content;
        if (showImmediately || _visible)
        {
            LayoutAndShow();
        }
    }

    private void LayoutAndShow()
    {
        if (_content is null || !Native.GetCursorPos(out var cursor))
        {
            return;
        }

        var paddingX = Scale(12);
        var paddingY = Scale(9);
        var iconSize = Scale(16);
        var iconGap = Scale(7);
        var sectionGap = Scale(7);
        var dc = Native.GetDC(_window);
        var oldFont = Native.SelectObject(dc, _font);
        Native.GetTextMetricsW(dc, out var metrics);
        var lineHeight = metrics.height + Scale(2);
        var rowHeight = Math.Max(lineHeight, iconSize) + Scale(3);
        var contentWidth = 0;
        foreach (var line in _content.TopLines)
        {
            contentWidth = Math.Max(contentWidth, Measure(dc, line));
        }
        if (!string.IsNullOrEmpty(_content.SectionTitle))
        {
            contentWidth = Math.Max(contentWidth, Measure(dc, _content.SectionTitle));
        }
        foreach (var row in _content.Rows)
        {
            var iconWidth = row.ProcessId > 0 ? iconSize + iconGap : 0;
            contentWidth = Math.Max(contentWidth, iconWidth + Measure(dc, row.Text));
        }
        Native.SelectObject(dc, oldFont);
        Native.ReleaseDC(_window, dc);

        var width = Math.Max(Scale(190), contentWidth + paddingX * 2);
        var height = paddingY * 2 + _content.TopLines.Length * lineHeight;
        if (!string.IsNullOrEmpty(_content.SectionTitle))
        {
            height += sectionGap + lineHeight;
        }
        height += _content.Rows.Length * rowHeight;
        height = Math.Max(height, Scale(42));

        var screenWidth = Native.GetSystemMetrics(0);
        var screenHeight = Native.GetSystemMetrics(1);
        var x = Math.Clamp(cursor.X + Scale(8), Scale(8), Math.Max(Scale(8), screenWidth - width - Scale(8)));
        var y = cursor.Y + Scale(12);
        if (y + height > screenHeight - Scale(8))
        {
            y = cursor.Y - height - Scale(12);
        }
        y = Math.Clamp(y, Scale(8), Math.Max(Scale(8), screenHeight - height - Scale(8)));

        var region = Native.CreateRoundRectRgn(0, 0, width + 1, height + 1, Scale(10), Scale(10));
        if (Native.SetWindowRgn(_window, region, redraw: false) == 0)
        {
            Native.DeleteObject(region);
        }
        Native.SetWindowPos(
            _window,
            Native.HWND_TOPMOST,
            x, y, width, height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.InvalidateRect(_window, 0, erase: false);
        _visible = true;
    }

    internal void Hide()
    {
        if (_window != 0 && _visible)
        {
            Native.ShowWindow(_window, Native.SW_HIDE);
            _visible = false;
        }
    }

    internal void RefreshTheme(bool lightTheme)
    {
        _lightTheme = lightTheme;
        if (_visible)
        {
            Native.InvalidateRect(_window, 0, erase: false);
        }
    }

    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var instance = _instance;
        if (instance is null)
        {
            return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        switch (message)
        {
            case Native.WM_PAINT:
                instance.Paint(hwnd);
                return 0;
            case Native.WM_ERASEBKGND:
                return 1;
            case Native.WM_NCHITTEST:
                return Native.HTTRANSPARENT;
            default:
                return Native.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private void Paint(nint hwnd)
    {
        var dc = Native.BeginPaint(hwnd, out var paint);
        try
        {
            if (_content is null || !Native.GetClientRect(hwnd, out var client))
            {
                return;
            }

            var background = _lightTheme
                ? Native.Color(246, 246, 246)
                : Native.Color(35, 35, 38);
            var foreground = _lightTheme
                ? Native.Color(28, 28, 30)
                : Native.Color(242, 242, 245);
            var muted = _lightTheme
                ? Native.Color(94, 94, 98)
                : Native.Color(174, 174, 181);
            var brush = Native.CreateSolidBrush(background);
            Native.FillRect(dc, ref client, brush);
            Native.DeleteObject(brush);

            var oldFont = Native.SelectObject(dc, _font);
            Native.SetBkMode(dc, Native.TRANSPARENT);
            Native.GetTextMetricsW(dc, out var metrics);
            var paddingX = Scale(12);
            var paddingY = Scale(9);
            var lineHeight = metrics.height + Scale(2);
            var iconSize = Scale(16);
            var iconGap = Scale(7);
            var rowHeight = Math.Max(lineHeight, iconSize) + Scale(3);
            var y = paddingY;

            Native.SetTextColor(dc, foreground);
            foreach (var line in _content.TopLines)
            {
                Native.TextOutString(dc, paddingX, y, line, line.Length);
                y += lineHeight;
            }

            if (!string.IsNullOrEmpty(_content.SectionTitle))
            {
                y += Scale(7);
                Native.SetTextColor(dc, muted);
                Native.TextOutString(dc, paddingX, y, _content.SectionTitle, _content.SectionTitle.Length);
                y += lineHeight;
            }

            Native.SetTextColor(dc, foreground);
            foreach (var row in _content.Rows)
            {
                var textX = paddingX;
                if (row.ProcessId > 0)
                {
                    var icon = GetProcessIcon(row.ProcessId);
                    if (icon != 0)
                    {
                        Native.DrawIconEx(dc, paddingX, y + (rowHeight - iconSize) / 2, icon,
                            iconSize, iconSize, 0, 0, Native.DI_NORMAL);
                    }
                    textX += iconSize + iconGap;
                }
                var textY = y + Math.Max(0, (rowHeight - metrics.height) / 2);
                Native.TextOutString(dc, textX, textY, row.Text, row.Text.Length);
                y += rowHeight;
            }
            Native.SelectObject(dc, oldFont);
        }
        finally
        {
            Native.EndPaint(hwnd, ref paint);
        }
    }

    private nint GetProcessIcon(int processId)
    {
        if (_icons.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var path = GetProcessPath(processId);
        var flags = Native.SHGFI_ICON | Native.SHGFI_SMALLICON;
        var attributes = 0u;
        if (string.IsNullOrEmpty(path))
        {
            path = ".exe";
            flags |= Native.SHGFI_USEFILEATTRIBUTES;
            attributes = Native.FILE_ATTRIBUTE_NORMAL;
        }

        var result = Native.SHGetFileInfoW(
            path,
            attributes,
            out var fileInfo,
            (uint)Marshal.SizeOf<Native.SHFILEINFO>(),
            flags);
        var icon = result != 0 ? fileInfo.icon : 0;
        _icons[processId] = icon;
        return icon;
    }

    private static string? GetProcessPath(int processId)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, inheritHandle: false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return Native.QueryFullProcessImageNameW(process, 0, path, ref size)
                ? path.ToString()
                : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static int Measure(nint dc, string text)
    {
        Native.MeasureString(dc, text, text.Length, out var size);
        return size.Width;
    }

    private int Scale(int logicalPixels) => Native.MulDiv(logicalPixels, (int)_dpi, 96);

    public void Dispose()
    {
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
        foreach (var icon in _icons.Values)
        {
            if (icon != 0)
            {
                Native.DestroyIcon(icon);
            }
        }
        _icons.Clear();
        _instance = null;
    }
}
