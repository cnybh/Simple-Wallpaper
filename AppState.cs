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
    [JsonPropertyName("last_run_date")] public string LastRunDate { get; set; } = string.Empty;
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

    public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

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

/// <summary>One-shot timer that runs a callback at the next local midnight and reschedules itself.</summary>
internal sealed class MidnightScheduler : IDisposable
{
    private readonly Action _onMidnight;
    private Timer? _timer;

    public MidnightScheduler(Action onMidnight) => _onMidnight = onMidnight;

    public void Start()
    {
        _timer = new Timer(_ => Tick(), null, TimeUntilNextMidnight(DateTime.Now), Timeout.InfiniteTimeSpan);
    }

    public static TimeSpan TimeUntilNextMidnight(DateTime now)
    {
        var due = now.Date.AddDays(1) - now;
        return due < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : due;
    }

    private void Tick()
    {
        try
        {
            _onMidnight();
        }
        catch (Exception ex)
        {
            AppState.Log("midnight task failed: " + ex.Message);
        }
        finally
        {
            try
            {
                _timer?.Change(TimeUntilNextMidnight(DateTime.Now), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
