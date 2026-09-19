using System.Runtime.InteropServices;

namespace SimpleWallpaper;

/// <summary>
/// Every user-visible string, in Simplified Chinese and English. The language is decided once at
/// start-up from the account's Windows display language: Simplified Chinese shows Chinese, every
/// other language shows English.
/// </summary>
internal static class Strings
{
    private const ushort LangChinese = 0x04;
    private const ushort SubLangChineseSimplified = 0x02;

    private static readonly bool Chinese = IsSimplifiedChinese();

    // Desktop context menu
    public static string MenuNextWallpaper => Chinese ? "切换至下一张壁纸" : "Next Wallpaper";
    public static string MenuSettings => Chinese ? "Simple Wallpaper 设置" : "Simple Wallpaper Setting";

    // Settings window
    public static string WindowTitle => Chinese ? "Simple Wallpaper 设置" : "Simple Wallpaper Setting";
    public static string StartupCheckBox => Chinese ? "设置开机自启动" : "Start with Windows";
    public static string LockScreenCheckBox => Chinese ? "同时设置锁屏壁纸" : "Also set the lock screen wallpaper";
    public static string NotSupportedSuffix => Chinese ? "（当前系统不支持）" : " (not supported on this system)";
    public static string AboutButton => Chinese ? "关于" : "About";
    public static string ReleasePageButton => Chinese ? "软件发布页" : "Software release page";
    public static string RestoreButton => Chinese ? "退出并恢复默认壁纸" : "Exit and restore default wallpaper";
    public static string AboutTitle => "Simple Wallpaper";
    public static string AboutText => "Simple Wallpaper by Bohang";
    public static string StartupFailed => Chinese
        ? "无法设置开机自启动，请查看日志。"
        : "Could not change the startup setting. See the log file.";
    public static string RestoredMessage => Chinese
        ? "已恢复 Windows 默认壁纸。程序已退出。"
        : "Windows default wallpapers restored. The program has exited.";

    private static bool IsSimplifiedChinese()
    {
        try
        {
            var languageId = GetUserDefaultUILanguage();
            var primary = languageId & 0x3FF;
            var subLanguage = (languageId >> 10) & 0x3F;
            return primary == LangChinese && subLanguage == SubLangChineseSimplified;
        }
        catch (Exception ex)
        {
            AppState.Log("detecting the display language failed: " + ex.Message);
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}
