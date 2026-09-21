using System.Reflection;
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

    /// <summary>Lets the self test check the countdown in the language this run actually uses.</summary>
    public static bool IsChinese => Chinese;

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
    public static string AboutHomePage => "https://github.com/cnybh/Simple-Wallpaper";
    public static string StartupFailed => Chinese
        ? "无法设置开机自启动，请查看日志。"
        : "Could not change the startup setting. See the log file.";
    public static string RestoredMessage => Chinese
        ? "已恢复 Windows 默认壁纸。程序已退出。"
        : "Windows default wallpapers restored. The program has exited.";

    // Switch control
    public static string CycleLabel => Chinese ? "更换周期" : "Switch Cycle";

    /// <summary>
    /// The little untitled box the settings window shows over itself while a new cycle is handed to
    /// the program, and the moment after: "切换至 1 小时 中" then "切换至 1 小时 成功".
    /// </summary>
    public static string SwitchingToCycle(string cycle) => Chinese
        ? $"切换至 {cycle} 中"
        : $"Switching to {cycle}…";

    public static string SwitchedToCycle(string cycle) => Chinese
        ? $"切换至 {cycle} 成功"
        : $"Switched to {cycle}";

    /// <summary>Countdown line above the cycle: "下次切换壁纸：12分20秒" / "Next wallpaper switch: 12m 20s".</summary>
    public static string NextSwitchLabel => Chinese ? "下次切换壁纸：" : "Next wallpaper switch: ";

    /// <summary>Left of the label while the moment counted towards has already passed.</summary>
    public static string NextSwitchDueNow => Chinese ? "即将切换" : "any moment now";

    /// <summary>Shown instead of the countdown while the cycle never loops: the picture just stays.</summary>
    public static string FixedWallpaper => Chinese ? "固定壁纸" : "Fixed wallpaper";

    /// <summary>Shown while the cycle is stopped to save power.</summary>
    public static string BatteryPaused => Chinese
        ? "电池模式停用自动切换以节省电量"
        : "Battery mode: automatic switching is off to save power";

    /// <summary>"无网络，下次检测时间：" - the countdown to the next network check follows it.</summary>
    public static string OfflineLabel => Chinese ? "无网络，下次检测时间：" : "No network, next check in ";

    /// <summary>
    /// A span of time, in seconds. Hours come first and are left out when there are none, so a
    /// half-day cycle reads "12时0分0秒" and a short one "12分20秒".
    /// </summary>
    public static string Countdown(TimeSpan left)
    {
        var seconds = (long)Math.Max(0, left.TotalSeconds);
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var rest = seconds % 60;

        return Chinese
            ? (hours > 0 ? $"{hours}时{minutes}分{rest}秒" : $"{minutes}分{rest}秒")
            : (hours > 0 ? $"{hours}h {minutes}m {rest}s" : $"{minutes}m {rest}s");
    }

    public static string SwitchModeName(string mode) => mode switch
    {
        SwitchSchedule.None => Chinese ? "不循环" : "No cycling",
        SwitchSchedule.Interval30 => Chinese ? "30 分钟" : "Every 30 minutes",
        SwitchSchedule.Interval60 => Chinese ? "1 小时" : "Every hour",
        SwitchSchedule.Interval120 => Chinese ? "2 小时" : "Every 2 hours",
        SwitchSchedule.Interval360 => Chinese ? "6 小时" : "Every 6 hours",
        SwitchSchedule.HalfDay => Chinese ? "按上/下午" : "Every half day (0:00 / 12:00)",
        _ => Chinese ? "按日期每天" : "Every day (0:00)",
    };

    // Wallpaper categories
    public static string CategoryButton => Chinese ? "壁纸分类" : "Wallpaper categories";
    public static string CategoryWindowTitle => Chinese ? "壁纸分类" : "Wallpaper categories";
    public static string CategoryHint => Chinese
        ? "勾选要使用的壁纸分类，可以多选。"
        : "Tick the wallpaper categories to use; more than one is allowed.";
    public static string CategoryRequired => Chinese
        ? "请至少保留一个分类。"
        : "Keep at least one category selected.";
    public static string OkButton => Chinese ? "确定" : "OK";
    public static string CancelButton => Chinese ? "取消" : "Cancel";

    public static string CategoryName(string key) => key switch
    {
        "nature" => Chinese ? "风景" : "Nature",
        "anime" => Chinese ? "动漫" : "Anime",
        "game" => Chinese ? "游戏" : "Game",
        "animal" => Chinese ? "动物" : "Animal",
        "city" => Chinese ? "城市" : "City",
        "abstract" => Chinese ? "抽象" : "Abstract",
        "space" => Chinese ? "宇宙" : "Space",
        "car" => Chinese ? "汽车" : "Car",
        "girl" => Chinese ? "美女" : "Girl",
        "sport" => Chinese ? "运动" : "Sport",
        _ => key,
    };

    // Current wallpaper
    public static string CurrentGroupTitle => Chinese ? "当前壁纸" : "Current wallpaper";
    public static string NoWallpaperYet => Chinese ? "尚未设置壁纸" : "No wallpaper yet";
    public static string SourceLabel => Chinese ? "来源：" : "Source: ";
    public static string UnknownSource => Chinese ? "未知" : "Unknown";
    public static string LikeButton => Chinese ? "喜欢此壁纸" : "Like this wallpaper";
    public static string UnlikeButton => Chinese ? "不再喜欢此壁纸" : "Unlike this wallpaper";
    public static string NextButton => Chinese ? "下一张壁纸" : "Next wallpaper";
    public static string FetchingNewWallpaper => Chinese
        ? "正在按新的分类获取壁纸…"
        : "Fetching a wallpaper for the new categories…";
    public static string FetchTimeout => Chinese
        ? "获取新壁纸超时，稍后可按所选周期自动更换。"
        : "Timed out while fetching a new wallpaper; the selected cycle will switch later.";

    /// <summary>Shown in the about box; the settings window does not repeat it any more.</summary>
    public static string VersionText => (Chinese ? "程序版本：" : "Software Version: ") + AppVersion;

    /// <summary>Taken from the assembly, so the shown version can never drift from the build.</summary>
    private static string AppVersion
    {
        get
        {
            // "1.2.3+<commit>" is what the SDK writes when the source revision is stamped in.
            var informational = typeof(Strings).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var text = informational?.Split('+')[0];
            if (!string.IsNullOrWhiteSpace(text)) return text;

            var version = typeof(Strings).Assembly.GetName().Version;
            if (version == null) return "0.0.0";
            return version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
        }
    }

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
