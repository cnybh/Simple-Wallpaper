using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleWallpaper;

/// <summary>
/// Persisted state in %APPDATA%\Wallpaper\state.json, plus the shared error log.
/// The preloaded wallpaper is part of the state so a restart can hand it out immediately.
/// </summary>
internal sealed class AppState
{
    [JsonPropertyName("current_path")] public string CurrentPath { get; set; } = string.Empty;
    [JsonPropertyName("pending_path")] public string PendingPath { get; set; } = string.Empty;

    // What the API said about the two pictures, so the settings window can show the name and where
    // the picture came from without asking the network again.
    [JsonPropertyName("current_title")] public string CurrentTitle { get; set; } = string.Empty;
    [JsonPropertyName("current_source")] public string CurrentSource { get; set; } = string.Empty;
    [JsonPropertyName("current_url")] public string CurrentUrl { get; set; } = string.Empty;
    [JsonPropertyName("pending_title")] public string PendingTitle { get; set; } = string.Empty;
    [JsonPropertyName("pending_source")] public string PendingSource { get; set; } = string.Empty;
    [JsonPropertyName("pending_url")] public string PendingUrl { get; set; } = string.Empty;

    /// <summary>The cycle the user picked in the settings window; see <see cref="SwitchSchedule"/>.</summary>
    [JsonPropertyName("switch_mode")] public string SwitchMode { get; set; } = SwitchSchedule.Default;

    /// <summary>
    /// Handshake for a cycle change: the settings window counts its request up, the program copies
    /// the number back once the new mode is saved. The dialog the settings window shows "switching
    /// to ..." in waits for that copy, so it never claims a change that was not applied.
    /// </summary>
    [JsonPropertyName("mode_request_seq")] public long ModeRequestSeq { get; set; }
    [JsonPropertyName("mode_applied_seq")] public long ModeAppliedSeq { get; set; }

    /// <summary>When the next switch is due, local time. The scheduler only compares it with now.</summary>
    [JsonPropertyName("next_switch_at")] public DateTime NextSwitchAt { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Why the cycle stands still (see <see cref="SwitchSchedule.PauseReason"/>), empty while it runs.
    /// It lives in the state so a restart keeps showing flat battery or no network instead of a
    /// countdown that nothing is counting down to.
    /// </summary>
    [JsonPropertyName("pause_reason")] public string PauseReason { get; set; } = string.Empty;

    /// <summary>The wallpaper subjects the user ticked; empty means "never picked" (see Categories).</summary>
    [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new();

    /// <summary>Wallpapers the user liked: their files are kept and are drawn from on later switches.</summary>
    [JsonPropertyName("likes")] public List<LikedWallpaper> Likes { get; set; } = new();

    [JsonPropertyName("lock_screen_enabled")] public bool LockScreenEnabled { get; set; } = true;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wallpaper");

    public static string StateFilePath => Path.Combine(DataDirectory, "state.json");
    public static string ErrorLogPath => Path.Combine(DataDirectory, "error.log");
    public static string SelfTestLogPath => Path.Combine(DataDirectory, "selftest.log");

    /// <summary>
    /// Written while a preloaded picture is waiting. The shell extension reads it to grey the menu
    /// entry out, and a menu entry that starts the program again waits for it before handing the
    /// click over - so both sides must agree on one path.
    /// </summary>
    public static string ReadyFlagPath => Path.Combine(DataDirectory, "next-ready.flag");

    public static void EnsureDataDirectory()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Simple Wallpaper: cannot create {DataDirectory}: {ex.Message}");
        }
    }

    public static AppState Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(StateFilePath))
                {
                    var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFilePath));
                    if (state != null) return state;
                }
            }
            catch (Exception ex)
            {
                Log("failed to read state.json: " + ex.Message);
            }

            return new AppState();
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var temp = StateFilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions), Encoding.UTF8);
                File.Move(temp, StateFilePath, true);
            }
            catch (Exception ex)
            {
                Log("failed to write state.json: " + ex.Message);
            }
        }
    }

    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.AppendAllText(ErrorLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the program down.
            }
        }
    }
}

/// <summary>One wallpaper the user liked. The file is kept on disk and used as a switch target.</summary>
internal sealed class LikedWallpaper
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("liked_at")] public DateTime LikedAt { get; set; } = DateTime.Now;
}
