using System.Runtime.InteropServices;

namespace OwlMount.WinUI;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SHObjectProperties(nint hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);

    internal const uint SHOP_FILEPATH = 0x2;
}
