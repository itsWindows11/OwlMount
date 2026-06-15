using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OwlMount.WinUI.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupService
{
    private const string ShortcutFileName = "OwlMount.lnk";
    private static readonly string StartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    private static readonly ComWrappers ComWrappersInstance = new StrategyBasedComWrappers();

    public bool IsEnabled => File.Exists(GetShortcutPath());

    public void SetEnabled(bool enabled)
    {
        string shortcutPath = GetShortcutPath();
        if (enabled)
            CreateShortcut(shortcutPath);
        else if (File.Exists(shortcutPath))
            File.Delete(shortcutPath);
    }

    private string GetShortcutPath() => Path.Combine(StartupFolder, ShortcutFileName);

    private void CreateShortcut(string shortcutPath)
    {
        try
        {
            var clsid = new Guid("00021401-0000-0000-C000-000000000046");
            var iid = typeof(IShellLinkW).GUID;

            int hr = NativeMethods.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr ptr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            var wrapper = ComWrappersInstance.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);

            if (wrapper is IShellLinkW shortcut)
            {
                shortcut.SetPath(Environment.ProcessPath ?? AppContext.BaseDirectory);
                shortcut.SetWorkingDirectory(AppContext.BaseDirectory);
                shortcut.SetDescription("Launch OwlMount when you sign in.");

                if (wrapper is IPersistFile persistFile)
                    persistFile.Save(shortcutPath, true);
            }

            // Release the COM pointer
            Marshal.Release(ptr);
        }
        catch { /* best-effort */ }
    }
}

// Interfaces remain as [GeneratedComInterface] for AOT support
[GeneratedComInterface]
[Guid("00021401-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
}

[GeneratedComInterface]
[Guid("0000010b-0000-0000-C000-000000000046")]
internal partial interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string? ppszFileName);
}