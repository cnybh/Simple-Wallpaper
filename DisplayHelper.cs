using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SimpleWallpaper;

/// <summary>
/// Primary display resolution and real Windows version / edition detection.
/// </summary>
internal static class DisplayHelper
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int HORZRES = 8;
    private const int VERTRES = 10;

    #region Native interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public short dmOrientation;
        public short dmPaperSize;
        public short dmPaperLength;
        public short dmPaperWidth;
        public short dmScale;
        public short dmCopies;
        public short dmDefaultSource;
        public short dmPrintQuality;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW")]
    private static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RTL_OSVERSIONINFOW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref RTL_OSVERSIONINFOW lpVersionInformation);

    #endregion

    /// <summary>Physical resolution of the primary display, with a GetDeviceCaps fallback.</summary>
    public static (int Width, int Height) GetPrimaryResolution()
    {
        try
        {
            var mode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref mode)
                && mode.dmPelsWidth > 0 && mode.dmPelsHeight > 0)
            {
                return ((int)mode.dmPelsWidth, (int)mode.dmPelsHeight);
            }
        }
        catch (Exception ex)
        {
            AppState.Log("EnumDisplaySettings failed: " + ex.Message);
        }

        return GetResolutionFromDeviceCaps();
    }

    private static (int Width, int Height) GetResolutionFromDeviceCaps()
    {
        var hdc = IntPtr.Zero;
        try
        {
            hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                var width = GetDeviceCaps(hdc, HORZRES);
                var height = GetDeviceCaps(hdc, VERTRES);
                if (width > 0 && height > 0) return (width, height);
            }
        }
        catch (Exception ex)
        {
            AppState.Log("GetDeviceCaps failed: " + ex.Message);
        }
        finally
        {
            if (hdc != IntPtr.Zero) DeleteDC(hdc);
        }

        AppState.Log("display resolution unavailable; falling back to 1920x1080");
        return (1920, 1080);
    }

    /// <summary>Real OS version via ntdll!RtlGetVersion (GetVersionEx lies on manifest-less or shimmed builds).</summary>
    public static bool TryGetRealOsVersion(out uint major, out uint minor, out uint build)
    {
        major = minor = build = 0;
        try
        {
            var info = new RTL_OSVERSIONINFOW { dwOSVersionInfoSize = (uint)Marshal.SizeOf<RTL_OSVERSIONINFOW>() };
            if (RtlGetVersion(ref info) == 0)
            {
                major = info.dwMajorVersion;
                minor = info.dwMinorVersion;
                build = info.dwBuildNumber;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppState.Log("RtlGetVersion failed: " + ex.Message);
        }
        return false;
    }

    public static bool IsWindows10OrGreater()
    {
        if (!TryGetRealOsVersion(out var major, out _, out var build)) return false;
        return major > 10 || (major == 10 && build >= 10240);
    }

    public static string OSVersionText =>
        TryGetRealOsVersion(out var major, out var minor, out var build)
            ? $"{major}.{minor}.{build} ({EditionId()})"
            : "unknown";

    public static string EditionId()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("EditionID") is string edition && edition.Length > 0) return edition;
            if (key?.GetValue("ProductName") is string product && product.Length > 0) return product;
        }
        catch (Exception ex)
        {
            AppState.Log("reading Windows edition failed: " + ex.Message);
        }
        return "unknown";
    }

    private static readonly Lazy<bool> LockScreenSupport = new(EvaluateLockScreenSupport);

    /// <summary>
    /// The PersonalizationCSP lock screen policy is only honoured on Windows 10+ client
    /// editions above Home/Starter (Professional, Enterprise, Education and derivatives).
    /// Evaluated once, because the menu asks for this on every redraw.
    /// </summary>
    public static bool SupportsLockScreenWallpaper() => LockScreenSupport.Value;

    private static bool EvaluateLockScreenSupport()
    {
        if (!IsWindows10OrGreater()) return false;

        var edition = EditionId();
        return edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase)
            || edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
            || edition.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
            || edition.Contains(" Pro", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || edition.Contains("Education", StringComparison.OrdinalIgnoreCase);
    }
}
