using System.Runtime.InteropServices;
using System.Text;

namespace GlanceBar;

internal static unsafe class Native
{
    internal const uint WS_POPUP = 0x80000000;
    internal const uint WS_EX_TOPMOST = 0x00000008;
    internal const uint WS_EX_TOOLWINDOW = 0x00000080;
    internal const uint WS_EX_LAYERED = 0x00080000;
    internal const uint WS_EX_NOACTIVATE = 0x08000000;
    internal const uint WS_EX_TRANSPARENT = 0x00000020;

    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_PAINT = 0x000F;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_MOUSEACTIVATE = 0x0021;
    internal const uint WM_NCHITTEST = 0x0084;
    internal const uint WM_MOUSEMOVE = 0x0200;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_CAPTURECHANGED = 0x0215;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_MOUSELEAVE = 0x02A3;
    internal const uint WM_DPICHANGED = 0x02E0;

    internal const int MA_NOACTIVATE = 3;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint LWA_ALPHA = 0x00000002;
    internal const int GWLP_HWNDPARENT = -8;
    internal static readonly nint HWND_TOPMOST = new(-1);

    internal const uint LWA_COLORKEY = 0x00000001;
    internal const int TRANSPARENT = 1;
    internal const int SRCCOPY = 0x00CC0020;
    internal const int FW_SEMIBOLD = 600;
    internal const uint DT_LEFT = 0x00000000;
    internal const uint DT_NOPREFIX = 0x00000800;
    internal const uint DT_CALCRECT = 0x00000400;
    internal const int HTTRANSPARENT = -1;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint SHGFI_ICON = 0x00000100;
    internal const uint SHGFI_SMALLICON = 0x00000001;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint DI_NORMAL = 0x0003;

    internal const uint MF_STRING = 0x0000;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint MF_CHECKED = 0x0008;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;

    internal const uint TME_LEAVE = 0x00000002;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;

    internal delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASS
    {
        internal uint style;
        internal nint lpfnWndProc;
        internal int cbClsExtra;
        internal int cbWndExtra;
        internal nint hInstance;
        internal nint hIcon;
        internal nint hCursor;
        internal nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] internal string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        internal int Width;
        internal int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        internal nint hwnd;
        internal uint message;
        internal nuint wParam;
        internal nint lParam;
        internal uint time;
        internal POINT point;
        internal uint privateValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PAINTSTRUCT
    {
        internal nint hdc;
        [MarshalAs(UnmanagedType.Bool)] internal bool erase;
        internal RECT paint;
        [MarshalAs(UnmanagedType.Bool)] internal bool restore;
        [MarshalAs(UnmanagedType.Bool)] internal bool incUpdate;
        internal fixed byte reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACKMOUSEEVENT
    {
        internal uint size;
        internal uint flags;
        internal nint hwndTrack;
        internal uint hoverTime;
    }



    [StructLayout(LayoutKind.Sequential)]
    internal struct TEXTMETRIC
    {
        internal int height;
        internal int ascent;
        internal int descent;
        internal int internalLeading;
        internal int externalLeading;
        internal int aveCharWidth;
        internal int maxCharWidth;
        internal int weight;
        internal int overhang;
        internal int digitizedAspectX;
        internal int digitizedAspectY;
        internal char firstChar;
        internal char lastChar;
        internal char defaultChar;
        internal char breakChar;
        internal byte italic;
        internal byte underlined;
        internal byte struckOut;
        internal byte pitchAndFamily;
        internal byte charSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        internal uint low;
        internal uint high;
        internal readonly ulong Value => ((ulong)high << 32) | low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        internal uint length;
        internal uint memoryLoad;
        internal ulong totalPhysical;
        internal ulong availablePhysical;
        internal ulong totalPageFile;
        internal ulong availablePageFile;
        internal ulong totalVirtual;
        internal ulong availableVirtual;
        internal ulong availableExtendedVirtual;
    }

    // Keep the complete native row layout, including fixed buffers and 64-bit alignment.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IF_ROW2
    {
        internal ulong interfaceLuid;
        internal uint interfaceIndex;
        internal Guid interfaceGuid;
        internal fixed char alias[257];
        internal fixed char description[257];
        internal uint physicalAddressLength;
        internal fixed byte physicalAddress[32];
        internal fixed byte permanentPhysicalAddress[32];
        internal uint mtu;
        internal uint type;
        internal uint tunnelType;
        internal uint mediaType;
        internal uint physicalMediumType;
        internal uint accessType;
        internal uint directionType;
        internal byte interfaceAndOperStatusFlags;
        internal uint operStatus;
        internal uint adminStatus;
        internal uint mediaConnectState;
        internal Guid networkGuid;
        internal uint connectionType;
        internal ulong transmitLinkSpeed;
        internal ulong receiveLinkSpeed;
        internal ulong inOctets;
        internal ulong inUcastPkts;
        internal ulong inNUcastPkts;
        internal ulong inDiscards;
        internal ulong inErrors;
        internal ulong inUnknownProtos;
        internal ulong inUcastOctets;
        internal ulong inMulticastOctets;
        internal ulong inBroadcastOctets;
        internal ulong outOctets;
        internal ulong outUcastPkts;
        internal ulong outNUcastPkts;
        internal ulong outDiscards;
        internal ulong outErrors;
        internal ulong outUcastOctets;
        internal ulong outMulticastOctets;
        internal ulong outBroadcastOctets;
        internal ulong outQLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MIB_IF_TABLE2
    {
        internal uint numEntries;
        internal MIB_IF_ROW2 first;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        internal nint icon;
        internal int iconIndex;
        internal uint attributes;
        internal fixed char displayName[260];
        internal fixed char typeName[80];
    }


    [StructLayout(LayoutKind.Explicit)]
    internal struct PDH_FORMATTED_COUNTER_VALUE
    {
        [FieldOffset(0)] internal uint status;
        [FieldOffset(8)] internal double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM
    {
        internal nint name;
        internal PDH_FORMATTED_COUNTER_VALUE value;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassW(ref WNDCLASS windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    internal static extern int GetMessageW(out MSG message, nint hwnd, uint minMessage, uint maxMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref MSG message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    internal static extern nuint SetTimer(nint hwnd, nuint timerId, uint intervalMilliseconds, nint callback);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(nint hwnd, nuint timerId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowExW(nint parent, nint after, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out RECT rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hwnd, out RECT rectangle);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string messageName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InvalidateRect(nint hwnd, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    internal static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint LoadCursorW(nint instance, nint cursorName);


    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(nint hdc, string text, int length, ref RECT rectangle, uint format);

    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);


    [DllImport("user32.dll")]
    internal static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern nint GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tracking);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(nint hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenuW(nint menu, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(nint menu);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint hdc, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int rasterOperation);

    [DllImport("user32.dll")]
    internal static extern int FillRect(nint hdc, ref RECT rectangle, nint brush);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateSolidBrush(uint color);


    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TextOutString(nint hdc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TextOutBuffer(nint hdc, int x, int y, StringBuilder text, int length);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MeasureString(nint hdc, string text, int length, out SIZE size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MeasureBuffer(nint hdc, StringBuilder text, int length, out SIZE size);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextMetricsW(nint hdc, out TEXTMETRIC metrics);


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("kernel32.dll")]
    internal static extern int MulDiv(int number, int numerator, int denominator);

    // Enumerate physical and logical interfaces with hardware flags and 64-bit byte counters.
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetIfTable2(out nint table);

    // Release the table allocated by GetIfTable2.
    [DllImport("iphlpapi.dll")]
    internal static extern void FreeMibTable(nint table);


    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nint query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounterW(nint query, string fullCounterPath, nuint userData, out nint counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(nint query);

    // Read wildcard instance names and values into a caller-owned buffer sized by the initial null-buffer call.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize,
        out uint itemCount, nint buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder fileName, ref uint size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern nuint SHGetFileInfoW(string path, uint fileAttributes, out SHFILEINFO fileInfo,
        uint fileInfoSize, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int width, int height,
        uint stepIfAnimated, nint flickerFreeBrush, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int state);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint ShellExecuteW(nint hwnd, string? operation, string file, string? parameters,
        string? directory, int showCommand);

    internal static uint Color(byte red, byte green, byte blue) =>
        red | ((uint)green << 8) | ((uint)blue << 16);
}
