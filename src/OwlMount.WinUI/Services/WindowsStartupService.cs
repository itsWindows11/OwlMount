using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace OwlMount.WinUI.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupService
{
    private const string ShortcutFileName = "OwlMount.lnk";
    private static readonly string StartupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public bool IsEnabled => File.Exists(GetShortcutPath());

    public void SetEnabled(bool enabled)
    {
        string shortcutPath = GetShortcutPath();
        if (enabled)
            CreateShortcut(shortcutPath);
        else if (File.Exists(shortcutPath))
            File.Delete(shortcutPath);
    }

    public static string GetShortcutTargetDescription()
        => Environment.ProcessPath ?? AppContext.BaseDirectory;

    private string GetShortcutPath() => Path.Combine(StartupFolder, ShortcutFileName);

    private void CreateShortcut(string shortcutPath)
    {
        try
        {
            Directory.CreateDirectory(StartupFolder);

            string targetPath = GetShortcutTargetDescription();
            string workingDirectory = AppContext.BaseDirectory;

            object shellLink = new ShellLink();
            var shortcut = (IShellLinkW)shellLink;
            shortcut.SetPath(targetPath);
            shortcut.SetWorkingDirectory(workingDirectory);
            shortcut.SetDescription("Launch OwlMount when you sign in.");

            ((IPersistFile)shortcut).Save(shortcutPath, true);
        }
        catch
        {
            // best-effort startup integration
        }
    }
}

[GeneratedComClass]
[Guid("00021401-0000-0000-C000-000000000046")]
[ClassInterface(ClassInterfaceType.None)]
file sealed partial class ShellLink : IShellLinkW, IPersistFile;

[GeneratedComInterface]
[Guid("00021401-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
file partial interface IShellLinkW
{
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
}

[GeneratedComInterface]
[Guid("0000010b-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
file partial interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string? ppszFileName);
}
