namespace SimpleWallpaper;

/// <summary>
/// When the next wallpaper is due. The mode strings are the values stored in state.json, so they
/// must not be renamed; the user picks one in the settings window and it is remembered.
/// </summary>
internal static class SwitchSchedule
{
    /// <summary>The user picked no cycle at all: the picture on screen stays until it is changed by hand.</summary>
    public const string None = "none";

    public const string Interval30 = "interval_30";
    public const string Interval60 = "interval_60";
    public const string Interval120 = "interval_120";
    public const string Interval360 = "interval_360";
    public const string HalfDay = "half_day";
    public const string Daily = "daily";

    /// <summary>
    /// The order the settings window shows. A section break is drawn above "不循环", above the
    /// interval cycles and above the two clock cycles (see <see cref="StartsSection"/>).
    /// </summary>
    public static readonly string[] Modes =
    {
        None, Interval30, Interval60, Interval120, Interval360, HalfDay, Daily,
    };

    /// <summary>Until the user picks something else: switch at midnight, which is what the program always did.</summary>
    public const string Default = Daily;

    public static string Normalize(string? mode) =>
        mode != null && Array.IndexOf(Modes, mode) >= 0 ? mode : Default;

    /// <summary>
    /// True for the first entry of a group: the settings window puts a break line above those, which
    /// is what keeps "不循环", the intervals and the two clock cycles apart.
    /// </summary>
    public static bool StartsSection(string mode) =>
        mode == None || mode == Interval30 || mode == HalfDay;

    /// <summary>True while the cycle moves the wallpaper by itself.</summary>
    public static bool IsLooping(string? mode) => Normalize(mode) != None;

    /// <summary>
    /// True for the cycles measured from a moment (30 minutes, 1/2/6 hours) rather than from the
    /// clock (0:00 and 12:00). Only these can start counting again, because only they have a
    /// beginning that "now" can replace; the clock cycles would be moved off their fixed times.
    /// </summary>
    public static bool CountsFromNow(string? mode) => Normalize(mode) switch
    {
        Interval30 or Interval60 or Interval120 or Interval360 => true,
        _ => false,
    };

    // Reasons the cycle stands still; they are also what the settings window shows instead of the
    // countdown. Battery is checked first because it is what the user asked to be told about.
    public const string BatteryReason = "battery";
    public const string NetworkReason = "network";
    public const string FixedReason = "fixed";

    /// <summary>
    /// Why the cycle is not running, or an empty string while it is. "Fixed" is the user's own
    /// choice, which is why it loses against a flat battery or a dead network: those two can end
    /// on their own, and what they show is the more useful thing to know.
    /// </summary>
    public static string PauseReason(bool onBattery, bool networkDown, string? mode)
    {
        if (onBattery) return BatteryReason;
        if (!IsLooping(mode)) return FixedReason;
        if (networkDown) return NetworkReason;
        return string.Empty;
    }

    /// <summary>
    /// The moment the switch after <paramref name="now"/> is due: the interval modes count from now,
    /// "按上/下午" waits for the next 0:00 or 12:00 and "按日期每天" for the next 0:00. The cycle
    /// that never loops has no moment, so the far future is returned and nothing can ever be due.
    /// </summary>
    public static DateTime NextDue(DateTime now, string? mode)
    {
        switch (Normalize(mode))
        {
            case None: return DateTime.MaxValue;
            case Interval30: return now + TimeSpan.FromMinutes(30);
            case Interval60: return now + TimeSpan.FromHours(1);
            case Interval120: return now + TimeSpan.FromHours(2);
            case Interval360: return now + TimeSpan.FromHours(6);
            case HalfDay: return now.Hour < 12 ? now.Date.AddHours(12) : now.Date.AddDays(1);
            default: return now.Date.AddDays(1);
        }
    }
}
