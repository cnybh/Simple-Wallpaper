using System.Net.Http;
using System.Text.Json;
using Windows.Storage;
using Windows.System.UserProfile;

namespace SimpleWallpaper;

/// <summary>
/// Owns the wallpaper files. One image is downloaded ahead of time so "Next Wallpaper" can be
/// applied instantly, and only the nature and city subjects are ever used.
/// </summary>
internal sealed class WallpaperManager
{
    private const string Upx8Endpoint =
        "https://wp.upx8.com/api.php?format=json&category={0}&resolution={1}x{2}&count=1";

    /// <summary>The only subjects that may be used: landscape and city.</summary>
    private static readonly string[] AllowedCategories = { "nature", "city" };

    private const string DefaultLockScreenImage = @"C:\Windows\Web\Screen\img100.jpg";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly object _gate = new();
    private Task<string?>? _prefetchTask;
    private string _manuallySwitchedDate = string.Empty;

    #region Daily task

    /// <summary>
    /// Runs once a day. Returns false when nothing could be downloaded, so the caller can retry.
    /// </summary>
    public bool RunDailyTask()
    {
        lock (_gate)
        {
            if (AppState.Load().LastRunDate == AppState.Today) return true;
        }

        // Downloading outside the gate is what keeps a click responsive: it never has to wait for
        // the daily download to finish.
        var path = DownloadNewWallpaper();
        if (path == null)
        {
            AppState.Log("daily task: no wallpaper source available; retrying later");
            return false;
        }

        lock (_gate)
        {
            var state = AppState.Load();

            if (state.LastRunDate == AppState.Today)
            {
                TryDelete(path);
                return true;
            }

            if (_manuallySwitchedDate == AppState.Today)
            {
                // The user picked a picture by hand while this download was running. Their choice
                // wins - without this the wallpaper jumps twice in a row.
                TryDelete(path);
                state.LastRunDate = AppState.Today;
                state.Save();
                RemoveOtherWallpapers(state);
                AppState.Log("daily task kept the wallpaper chosen by the user");
                return true;
            }

            state.CurrentPath = path;
            state.LastRunDate = AppState.Today;
            state.Save();
            ApplyCurrent(state);
            RemoveOtherWallpapers(state);
            SetMenuClickable(false);   // greyed out until the next one arrives
            AppState.Log("daily task applied " + Path.GetFileName(path));
        }

        EnsurePrefetched();
        return true;
    }

    #endregion

    #region Next wallpaper

    /// <summary>
    /// Applies the preloaded wallpaper - without waiting for the network - and refills the queue.
    /// </summary>
    public bool ApplyNext(out string name)
    {
        name = string.Empty;

        var next = TakePending();

        if (next == null)
        {
            // Nothing is ready yet. The menu entry already says it is downloading, and a click must
            // not make the user wait for that download either, so the click is simply ignored.
            EnsurePrefetched();
            SyncMenuState();
            AppState.Log("next wallpaper: nothing ready yet, click ignored");
            return false;
        }

        try
        {
            lock (_gate)
            {
                var state = AppState.Load();
                state.CurrentPath = next;
                state.PendingPath = string.Empty;
                state.Save();
                ApplyCurrent(state);
                RemoveOtherWallpapers(state);
                _manuallySwitchedDate = AppState.Today;   // today nothing may overwrite this choice
                SetMenuClickable(false);                  // greyed out until the next one arrives
            }

            name = Path.GetFileName(next);
            AppState.Log("applied the next wallpaper " + name);

            EnsurePrefetched();
            return true;
        }
        catch (Exception ex)
        {
            AppState.Log("applying the next wallpaper failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Takes the preloaded picture out of the queue, if there is a usable one.</summary>
    private string? TakePending()
    {
        lock (_gate)
        {
            var state = AppState.Load();
            if (!HasPending(state)) return null;

            var path = state.PendingPath;
            state.PendingPath = string.Empty;
            state.Save();
            return path;
        }
    }

    #endregion

    #region Prefetch

    /// <summary>
    /// Starts filling the queue. Called at start-up as well, so that an early click can already be
    /// answered from the queue instead of downloading.
    /// </summary>
    public void EnsurePrefetched()
    {
        lock (_gate)
        {
            var state = AppState.Load();

            if (HasPending(state))
            {
                // A picture is waiting, so the entry is usable as it stands.
                SetMenuClickable(true);
                return;
            }

            if (_prefetchTask is { IsCompleted: false }) return;

            _prefetchTask = PrefetchAsync();
            SetMenuClickable(false);   // a download is running: the entry greys out
        }
    }

    /// <summary>
    /// Matches the menu entry to the queue: clickable while a picture is waiting, and also when the
    /// last download failed - so a failed attempt never leaves the entry greyed out for good. While
    /// a download is running the entry is grey, which is what stops accidental double clicks.
    /// </summary>
    public void SyncMenuState()
    {
        lock (_gate)
        {
            var downloading = _prefetchTask is { IsCompleted: false };
            SetMenuClickable(HasPending(AppState.Load()) || !downloading);
        }
    }

    /// <summary>
    /// Publishes whether the entry may be clicked. The shell extension reads this flag to choose
    /// between ECS_ENABLED and ECS_DISABLED, which is what greys the menu entry out.
    /// </summary>
    internal static void SetMenuClickable(bool clickable)
    {
        var flag = AppState.ReadyFlagPath;

        try
        {
            if (clickable)
            {
                File.WriteAllText(flag, "ready");
            }
            else if (File.Exists(flag))
            {
                File.Delete(flag);
            }
        }
        catch (Exception ex)
        {
            AppState.Log("updating the menu flag failed: " + ex.Message);
        }
    }

    /// <summary>Downloads one picture and stores it as the next one to use.</summary>
    private async Task<string?> PrefetchAsync()
    {
        try
        {
            AppState.Log("prefetch: downloading the next wallpaper");
            var path = await Task.Run(DownloadNewWallpaper).ConfigureAwait(false);

            lock (_gate)
            {
                if (path == null)
                {
                    AppState.Log("prefetch: no image could be downloaded");
                    return null;
                }

                var state = AppState.Load();
                state.PendingPath = path;
                state.Save();
                RemoveOtherWallpapers(state);
                AppState.Log("prefetched " + Path.GetFileName(path));
            }

            return path;
        }
        catch (Exception ex)
        {
            AppState.Log("prefetch failed: " + ex.Message);
            return null;
        }
        finally
        {
            // The queue is filled again - or the attempt failed: reflect that in the entry.
            SyncMenuState();
        }
    }

    private static bool HasPending(AppState state)
        => state.PendingPath.Length > 0 && File.Exists(state.PendingPath);

    #endregion

    #region Files

    /// <summary>
    /// Deletes every wallpaper except the one in use and the preloaded one. The preloaded file
    /// must survive, otherwise the work the next click depends on would be thrown away.
    /// </summary>
    private static void RemoveOtherWallpapers(AppState state)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.CurrentPath.Length > 0) keep.Add(state.CurrentPath);
        if (state.PendingPath.Length > 0) keep.Add(state.PendingPath);

        // A download that is still running is not in the state yet, and deleting it would throw
        // away work in progress (or a picture the next click is waiting for), so fresh files are
        // left alone and picked up by a later clean-up.
        var fresh = DateTime.UtcNow.AddMinutes(-2);

        try
        {
            foreach (var file in Directory.EnumerateFiles(AppState.DataDirectory, "*.jpg"))
            {
                if (keep.Contains(file)) continue;
                if (File.GetCreationTimeUtc(file) > fresh) continue;

                if (TryDelete(file)) AppState.Log("removed an unused wallpaper " + Path.GetFileName(file));
            }
        }
        catch (Exception ex)
        {
            AppState.Log("cleaning up wallpapers failed: " + ex.Message);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            AppState.Log($"removing {path} failed: {ex.Message}");
            return false;
        }
    }

    private static string? DownloadNewWallpaper()
    {
        var (width, height) = DisplayHelper.GetPrimaryResolution();
        var picture = FetchWallpaper(width, height);
        if (picture == null) return null;

        // The subject is part of the file name, so what is on disk is always traceable.
        var path = Path.Combine(AppState.DataDirectory,
            $"{picture.Value.Category}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg");

        return DownloadImage(picture.Value.Url, path) ? path : null;
    }

    private static void ApplyCurrent(AppState state)
    {
        if (state.CurrentPath.Length == 0 || !File.Exists(state.CurrentPath)) return;

        RegistryHelper.ApplyDesktopWallpaper(state.CurrentPath);

        if (state.LockScreenEnabled && DisplayHelper.SupportsLockScreenWallpaper())
        {
            ApplyLockScreen(state.CurrentPath);
        }
    }

    #endregion

    #region Sources

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleWallpaper/2.0");
        return client;
    }

    /// <summary>
    /// Picks either a landscape or a city picture; the one that is not tried first backs the other
    /// up. No other subject is ever requested.
    /// </summary>
    private static (string Category, string Url)? FetchWallpaper(int width, int height)
    {
        var first = AllowedCategories[Random.Shared.Next(AllowedCategories.Length)];
        var second = first == "nature" ? "city" : "nature";

        var url = FetchFromUpx8(first, width, height);
        if (url != null) return (first, url);

        url = FetchFromUpx8(second, width, height);
        return url != null ? (second, url) : null;
    }

    private static string? FetchFromUpx8(string category, int width, int height)
    {
        try
        {
            var json = Http.GetStringAsync(string.Format(Upx8Endpoint, category, width, height))
                .GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("data", out var data)) return null;

            var url = data.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex)
        {
            AppState.Log($"upx8 request failed for {category}: " + ex.Message);
            return null;
        }
    }

    private static bool DownloadImage(string url, string targetPath)
    {
        try
        {
            using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            using var source = response.Content.ReadAsStream();
            using var target = File.Create(targetPath);
            source.CopyTo(target);
            target.Flush();

            return new FileInfo(targetPath).Length > 1024;
        }
        catch (Exception ex)
        {
            AppState.Log($"downloading {url} failed: {ex.Message}");
            TryDelete(targetPath);
            return false;
        }
    }

    #endregion

    #region Lock screen

    /// <summary>
    /// Sets the lock screen image with Windows.System.UserProfile.LockScreen, the same user-level
    /// API the Settings app uses, so no elevation and no UAC prompt is involved.
    /// </summary>
    public static bool ApplyLockScreen(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            AppState.Log("lock screen image not found: " + imagePath);
            return false;
        }

        var applied = false;
        var worker = new Thread(() =>
        {
            try
            {
                var file = StorageFile.GetFileFromPathAsync(imagePath).GetAwaiter().GetResult();
                LockScreen.SetImageFileAsync(file).GetAwaiter().GetResult();
                applied = true;
            }
            catch (Exception ex)
            {
                AppState.Log("setting the lock screen image failed: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "LockScreenSetter",
        };

        // WinRT UI-adjacent APIs belong on an STA thread; there is no sync context here, so
        // blocking cannot deadlock.
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(30)))
        {
            AppState.Log("setting the lock screen image timed out");
            return false;
        }

        return applied;
    }

    public static bool RestoreDefaultLockScreen()
    {
        var restored = File.Exists(DefaultLockScreenImage) && ApplyLockScreen(DefaultLockScreenImage);
        if (!restored) AppState.Log("could not restore the default lock screen image");
        return restored;
    }

    /// <summary>Read-only probe for the self test: is the WinRT API reachable, and what is in use?</summary>
    public static (bool Reachable, string? Path) QueryLockScreenImage()
    {
        var reachable = false;
        string? path = null;

        var worker = new Thread(() =>
        {
            try
            {
                path = LockScreen.OriginalImageFile?.LocalPath;
                reachable = true;
            }
            catch (Exception ex)
            {
                AppState.Log("lock screen API probe failed: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "LockScreenProbe",
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join(TimeSpan.FromSeconds(10));
        return (reachable, path);
    }

    #endregion

    #region Restore defaults

    /// <summary>
    /// Puts Windows back to its defaults and stops advertising the program: desktop wallpaper,
    /// lock screen, startup entry, desktop menu and the remembered state.
    /// </summary>
    public static void RestoreDefaults()
    {
        RegistryHelper.RestoreDefaultDesktopWallpaper();
        RestoreDefaultLockScreen();
        RegistryHelper.DisableStartup();
        RegistryHelper.UnregisterDesktopMenu();

        var state = AppState.Load();
        state.CurrentPath = string.Empty;
        state.PendingPath = string.Empty;
        state.LastRunDate = string.Empty;
        state.LockScreenEnabled = false;
        state.Save();

        AppState.Log("restored the Windows defaults");
    }

    #endregion
}
