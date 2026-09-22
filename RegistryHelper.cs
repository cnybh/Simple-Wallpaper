using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SimpleWallpaper;

/// <summary>
/// Every registry side effect: the desktop wallpaper, the per-user startup entry (including the
/// "switched off" flag Windows keeps in StartupApproved) and the two desktop context menu verbs.
/// </summary>
internal static class RegistryHelper
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "Wallpaper";
    private const string StartupApprovedPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string DesktopShellPath = @"Software\Classes\DesktopBackground\Shell";
    private const string NextVerbName = "SimpleWallpaperNext";
    private const string SettingsVerbName = "SimpleWallpaperSettings";

    /// <summary>Matches the CLSID compiled into SimpleWallpaperShell.dll.</summary>
    public const string ShellExtensionClsid = "{7A2E5C31-9B4D-4E8A-9F2C-3D5E7A1B8C40}";

    private const int SPI_SETDESKWALLPAPER = 20;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;
    private const byte StartupStateEnabled = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, string? pvParam, int fWinIni);

    #region Desktop wallpaper

    public static bool ApplyDesktopWallpaper(string imagePath)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop", true))
            {
                key?.SetValue("Wallpaper", imagePath, RegistryValueKind.String);
                key?.SetValue("WallpaperStyle", "10", RegistryValueKind.String);
                key?.SetValue("TileWallpaper", "0", RegistryValueKind.String);
            }

            return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch (Exception ex)
        {
            AppState.Log("applying the desktop wallpaper failed: " + ex.Message);
            return false;
        }
    }

    public static void RestoreDefaultDesktopWallpaper()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop", true))
            {
                key?.SetValue("Wallpaper", string.Empty, RegistryValueKind.String);
                key?.SetValue("WallpaperStyle", "0", RegistryValueKind.String);
                key?.SetValue("TileWallpaper", "0", RegistryValueKind.String);
            }
        }
        catch (Exception ex)
        {
            AppState.Log("clearing the desktop wallpaper registry values failed: " + ex.Message);
        }

        try
        {
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch (Exception ex)
        {
            AppState.Log("clearing the desktop wallpaper failed: " + ex.Message);
        }
    }

    #endregion

    #region Startup entry

    /// <summary>
    /// True when the Run entry exists, points at this executable and is not switched off.
    /// Windows (or a startup manager) records "switched off" in StartupApproved, and an entry
    /// stays switched off even after the Run value is written again - so both are checked.
    /// </summary>
    public static bool IsStartupEnabled(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(StartupValueName) as string;

            // The Run value is an executable path that may also carry a command line. Comparing whole
            // strings keeps "another program in the same folder" from passing for this one; matching
            // on "contains" made SimpleWallpaper2.exe look like SimpleWallpaper.exe.
            return SameProgram(value, exePath) && !IsStartupSwitchedOff();
        }
        catch (Exception ex)
        {
            AppState.Log("reading the startup entry failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// True when a Run value starts this very program: its path is either written plainly, or quoted
    /// with the command line following. The two paths are compared in full, case-insensitively, the
    /// way Windows names files.
    /// </summary>
    private static bool SameProgram(string? runValue, string exePath)
    {
        if (string.IsNullOrWhiteSpace(runValue) || string.IsNullOrWhiteSpace(exePath)) return false;

        var expected = NormalizePath(exePath);
        var registered = runValue.Trim();

        if (registered.StartsWith('"'))
        {
            var closing = registered.IndexOf('"', 1);
            if (closing < 0) return false;
            return NormalizePath(registered[1..closing]) == expected;
        }

        // Unquoted, the path cannot contain a space, so everything up to the first one is the program.
        var space = registered.IndexOf(' ');
        return NormalizePath(space < 0 ? registered : registered[..space]) == expected;
    }

    /// <summary>
    /// One spelling for a path: environment names resolved, the rest of the path made absolute. A
    /// path that cannot be resolved is compared as it stands, which is still exact.
    /// </summary>
    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception ex)
        {
            AppState.Log("cannot normalise a path for comparison: " + ex.Message);
            return path.Trim();
        }
    }

    public static bool EnableStartup(string exePath)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
            {
                if (key == null) return false;
                key.SetValue(StartupValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }

            // Without clearing this flag the entry stays switched off although the Run value is
            // present - the reason the program sometimes did not start with Windows.
            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, true))
            {
                approved?.DeleteValue(StartupValueName, false);
            }

            AppState.Log("startup entry enabled");
            return true;
        }
        catch (Exception ex)
        {
            AppState.Log("writing the startup entry failed: " + ex.Message);
            return false;
        }
    }

    public static bool DisableStartup()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                key?.DeleteValue(StartupValueName, false);
            }

            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, true))
            {
                approved?.DeleteValue(StartupValueName, false);
            }

            AppState.Log("startup entry disabled");
            return true;
        }
        catch (Exception ex)
        {
            AppState.Log("removing the startup entry failed: " + ex.Message);
            return false;
        }
    }

    private static bool IsStartupSwitchedOff()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath);
        if (approved?.GetValue(StartupValueName) is not byte[] state || state.Length == 0) return false;
        return state[0] != StartupStateEnabled;
    }

    #endregion

    #region Desktop background context menu

    /// <summary>
    /// Reflects the "Next Wallpaper" entry. A static menu entry cannot be greyed out - Explorer
    /// only knows "shown" or "hidden" - so the registerable shell extension is used when it is
    /// available, and the plain command is used as a fallback when it is not.
    /// </summary>
    public static bool RegisterDesktopMenu(string exePath, bool greyOutWithShellExtension)
    {
        try
        {
            using var shell = Registry.CurrentUser.CreateSubKey(DesktopShellPath, true);
            if (shell == null) return false;

            WriteVerb(shell, NextVerbName, Strings.MenuNextWallpaper, "--next", exePath,
                shiftOnly: false, useShellExtension: greyOutWithShellExtension);
            WriteVerb(shell, SettingsVerbName, Strings.MenuSettings, "--settings", exePath,
                shiftOnly: true, useShellExtension: false);
            return true;
        }
        catch (Exception ex)
        {
            AppState.Log("registering the desktop menu failed: " + ex.Message);
            return false;
        }
    }

    public static void UnregisterDesktopMenu()
    {
        try
        {
            using var shell = Registry.CurrentUser.OpenSubKey(DesktopShellPath, true);
            shell?.DeleteSubKeyTree(NextVerbName, false);
            shell?.DeleteSubKeyTree(SettingsVerbName, false);
        }
        catch (Exception ex)
        {
            AppState.Log("removing the desktop menu failed: " + ex.Message);
        }
    }

    /// <summary>
    /// True when the machine-wide COM registration exists and points at exactly this DLL. A class
    /// registered only under HKEY_CURRENT_USER is not found by the shell here, so the extension has
    /// to live in HKEY_LOCAL_MACHINE - which is the one operation that needs administrator rights,
    /// and only once.
    /// </summary>
    public static bool IsShellExtensionRegistered(string dllPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Classes\CLSID\{ShellExtensionClsid}\InprocServer32");

            var registered = key?.GetValue(null) as string;
            return !string.IsNullOrEmpty(registered)
                && string.Equals(registered, dllPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppState.Log("reading the shell extension registration failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Writes the machine-wide registration; runs inside the elevated helper process.</summary>
    public static void RegisterShellExtensionInMachine(string dllPath, string exePath)
    {
        using var key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Classes\CLSID\{ShellExtensionClsid}", true)
            ?? throw new InvalidOperationException(@"cannot open HKLM\SOFTWARE\Classes\CLSID");

        key.SetValue(null, "Simple Wallpaper Next", RegistryValueKind.String);
        key.SetValue("ExePath", exePath, RegistryValueKind.String);

        using var server = key.CreateSubKey("InprocServer32", true)
            ?? throw new InvalidOperationException("cannot create InprocServer32");

        server.SetValue(null, dllPath, RegistryValueKind.String);
        server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
    }

    private static void WriteVerb(RegistryKey shell, string name, string text, string arguments,
        string exePath, bool shiftOnly, bool useShellExtension)
    {
        using var verb = shell.CreateSubKey(name, true);
        if (verb == null) return;

        verb.SetValue(null, text, RegistryValueKind.String);
        verb.DeleteValue("Icon", false);       // the program deliberately has no icon
        verb.DeleteValue("Position", false);   // default spot: below Paste, above the terminal entry

        if (shiftOnly)
        {
            // "extended" is the documented way to show a verb only for Shift + right click.
            verb.SetValue("Extended", string.Empty, RegistryValueKind.String);
        }
        else
        {
            verb.DeleteValue("Extended", false);
        }

        if (useShellExtension)
        {
            // The extension supplies the title and greys the entry out while a download is running.
            verb.DeleteSubKeyTree("command", false);
            verb.SetValue("ExplorerCommandHandler", ShellExtensionClsid, RegistryValueKind.String);
        }
        else
        {
            verb.DeleteValue("ExplorerCommandHandler", false);
            using var command = verb.CreateSubKey("command", true);
            command?.SetValue(null, $"\"{exePath}\" {arguments}", RegistryValueKind.String);
        }
    }

    #endregion
}
