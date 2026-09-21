using System.Net.Http;
using System.Text.Json;
using Windows.Storage;
using Windows.System.UserProfile;

namespace SimpleWallpaper;

/// <summary>
/// Owns the wallpaper files and the switch itself. One picture is downloaded ahead of time so a
/// switch can be applied instantly; when a switch happens, a liked wallpaper may win a draw instead
/// (see <see cref="LikeChancePercent"/>) and the freshly downloaded picture stays queued.
/// </summary>
internal sealed class WallpaperManager
{
    private const string Upx8Endpoint =
        "https://wp.upx8.com/api.php?format=json&category={0}&resolution={1}x{2}&count=1";

    private const string DefaultLockScreenImage = @"C:\Windows\Web\Screen\img100.jpg";

    /// <summary>How often the network is asked while it looks unreachable; see Program's cycle check.</summary>
    internal static readonly TimeSpan NetworkCheckInterval = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly object _gate = new();

    /// <summary>Serialises whole switches: a scheduled switch and a click must never fight over the queue.</summary>
    private readonly object _switchGate = new();

    private Task<string?>? _prefetchTask;

    /// <summary>One picture on disk, together with what the API said about it.</summary>
    private sealed record Target(string Path, string Title, string Source, string Url);

    #region Liked wallpapers

    /// <summary>
    /// Chance in percent that a switch lands on a liked wallpaper instead of the new download:
    /// up to 5 liked = 10%, up to 19 = 20%, up to 49 = 30%, 50 and more = 50%.
    /// </summary>
    public static int LikeChancePercent(int likedCount)
    {
        if (likedCount <= 0) return 0;
        if (likedCount <= 5) return 10;
        if (likedCount <= 19) return 20;
        if (likedCount <= 49) return 30;
        return 50;
    }

    /// <summary>Picks a liked wallpaper to switch to, or null when the draw says "use the new one".</summary>
    private static Target? DrawLikedWallpaper(AppState state)
    {
        if (state.Likes.Count == 0) return null;

        // The wallpaper that is already on screen is no prize: it would look like nothing happened.
        var candidates = state.Likes
            .Where(liked => !string.Equals(liked.Path, state.CurrentPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;

        var chance = LikeChancePercent(state.Likes.Count);
        if (Random.Shared.Next(100) >= chance)
        {
            AppState.Log($"liked wallpaper draw: no ({chance}% with {state.Likes.Count} liked)");
            return null;
        }

        var pick = candidates[Random.Shared.Next(candidates.Count)];
        AppState.Log($"liked wallpaper draw: yes ({chance}% with {state.Likes.Count} liked) -> {Path.GetFileName(pick.Path)}");
        return new Target(pick.Path, pick.Title, pick.Source, pick.Url);
    }

    #endregion

    #region Switching

    /// <summary>
    /// The desktop menu entry: applies the preloaded picture. The cycle is deliberately left alone -
    /// a manual click must not move the next scheduled switch.
    /// </summary>
    public bool ApplyNext(out string name)
    {
        name = string.Empty;

        if (!Monitor.TryEnter(_switchGate))
        {
            AppState.Log("next wallpaper: another switch is already running, click ignored");
            return false;
        }

        try
        {
            lock (_gate)
            {
                if (!HasPending(AppState.Load()))
                {
                    // The entry already says it is downloading, and a click must not make the user
                    // wait for that download either, so the click is simply ignored.
                    EnsurePrefetched();
                    SyncMenuState();
                    AppState.Log("next wallpaper: nothing ready yet, click ignored");
                    return false;
                }
            }

            return Switch(usePending: true, reschedule: false, out name);
        }
        finally
        {
            Monitor.Exit(_switchGate);
        }
    }

    /// <summary>
    /// A switch the cycle asked for: takes the preloaded picture when there is one and downloads a
    /// new one otherwise. Only a real switch moves the cycle on (see Program.RunScheduledSwitch).
    /// </summary>
    public bool ApplyScheduled(out string name)
    {
        lock (_switchGate)
        {
            return Switch(usePending: true, reschedule: true, out name);
        }
    }

    /// <summary>
    /// The categories changed: the queued picture is from the old ones, so it is dropped and a new
    /// picture for the new selection is downloaded and applied right away.
    /// </summary>
    public bool ApplyCategoryChange(out string name)
    {
        lock (_switchGate)
        {
            DiscardPending();
            return Switch(usePending: false, reschedule: true, out name);
        }
    }

    /// <summary>
    /// Recomputes the next switch when the user picks another cycle. A paused cycle is left paused
    /// (the battery or the network has not changed because of a click), but the moment it would
    /// resume at is recomputed so a later resume cannot fire for a time that has long passed.
    /// The power and network state come from the program's own watchers: assuming the mains here
    /// would drop a flat-battery pause the moment the user touched the cycle list.
    /// </summary>
    public void OnModeChanged(bool acOnline, bool networkDown)
    {
        lock (_gate)
        {
            var state = AppState.Load();
            state.SwitchMode = SwitchSchedule.Normalize(state.SwitchMode);

            // Forced: a new cycle must move the countdown even when the pause reason is unchanged,
            // which is exactly the case a plain RefreshReason would return from without rescheduling.
            state = RefreshReason(state, acOnline, networkDown, force: true);

            // Only now is the settings window allowed to say the change took: its dialog waits for
            // the sequence it wrote to come back applied.
            state.ModeAppliedSeq = state.ModeRequestSeq;
            state.Save();

            AppState.Log($"switch cycle is now {state.SwitchMode}; next switch {state.NextSwitchAt:yyyy-MM-dd HH:mm:ss}");
        }
    }

    /// <summary>
    /// Turns "flat battery / no network / the user picked no cycle" into the reason the settings
    /// window shows, and recomputes the next switch whenever the reason changes - a paused cycle
    /// must not fire for the moment it was paused at, and a resumed one starts from now.
    /// <paramref name="force"/> reschedules even when the reason is unchanged, which is what a new
    /// cycle needs: two intervals share the "running" reason but not the moment they come due.
    /// </summary>
    internal AppState RefreshReason(AppState state, bool acOnline, bool networkDown, bool log = true, bool force = false)
    {
        var reason = SwitchSchedule.PauseReason(!acOnline, networkDown, state.SwitchMode);
        if (reason == state.PauseReason && !force) return state;

        state.PauseReason = reason;
        state.NextSwitchAt = reason.Length > 0
            ? (reason == SwitchSchedule.NetworkReason
                ? DateTime.Now + NetworkCheckInterval   // when the network is asked again
                : DateTime.MaxValue)                    // nothing is due while it stands still
            : SwitchSchedule.NextDue(DateTime.Now, state.SwitchMode);
        state.Save();

        if (log) LogCycle(state);
        return state;
    }

    private static void LogCycle(AppState state)
    {
        AppState.Log(state.PauseReason.Length > 0
            ? $"switch cycle is on hold: {state.PauseReason}; next switch {state.NextSwitchAt:yyyy-MM-dd HH:mm:ss}"
            : $"switch cycle is running: {state.SwitchMode}; next switch {state.NextSwitchAt:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>
    /// The one place a wallpaper is put on screen: decides between the freshly downloaded picture and
    /// a liked one, stores what was applied, cleans up and refills the queue.
    /// </summary>
    private bool Switch(bool usePending, bool reschedule, out string name)
    {
        name = string.Empty;

        Target? fresh = null;
        if (usePending)
        {
            lock (_gate)
            {
                var queued = AppState.Load();
                if (HasPending(queued)) fresh = PendingTarget(queued);
            }
        }

        // Downloading happens outside the gate: that is what keeps a click responsive.
        fresh ??= DownloadNewWallpaper();
        if (fresh == null)
        {
            AppState.Log("no wallpaper could be downloaded");
            return false;
        }

        Target applied;
        try
        {
            lock (_gate)
            {
                var state = AppState.Load();

                // Likes whose file is gone must not be counted or drawn.
                state.Likes.RemoveAll(liked => liked.Path.Length == 0 || !File.Exists(liked.Path));

                var liked = DrawLikedWallpaper(state);
                applied = liked ?? fresh;

                if (liked == null)
                {
                    ClearPending(state);
                }
                else
                {
                    // A liked picture won: the new download is not wasted, it waits for the next switch.
                    SetPending(state, fresh);
                }

                state.CurrentPath = applied.Path;
                state.CurrentTitle = applied.Title;
                state.CurrentSource = applied.Source;
                state.CurrentUrl = applied.Url;

                if (reschedule) state.NextSwitchAt = SwitchSchedule.NextDue(DateTime.Now, state.SwitchMode);
                state.Save();

                ApplyCurrent(state);
                RemoveOtherWallpapers(state);
                SetMenuClickable(false);   // greyed out until the next one arrives
            }
        }
        catch (Exception ex)
        {
            AppState.Log("applying the wallpaper failed: " + ex.Message);
            return false;
        }

        name = Path.GetFileName(applied.Path);
        AppState.Log("applied " + name);
        EnsurePrefetched();
        return true;
    }

    #endregion

    #region Prefetch

    /// <summary>
    /// Starts filling the queue. Called after every switch as well, so that a click can already be
    /// answered from the queue instead of downloading.
    /// </summary>
    public void EnsurePrefetched()
    {
        lock (_gate)
        {
            if (HasPending(AppState.Load()))
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
        var categories = Categories.Key(AppState.Load().Categories);
        var discarded = false;

        try
        {
            AppState.Log("prefetch: downloading the next wallpaper");
            var target = await Task.Run(DownloadNewWallpaper).ConfigureAwait(false);

            lock (_gate)
            {
                if (target == null)
                {
                    AppState.Log("prefetch: no image could be downloaded");
                    return null;
                }

                if (Categories.Key(AppState.Load().Categories) != categories)
                {
                    // The user changed the categories while this download was running: the picture is
                    // from the old selection, so it is thrown away instead of being queued - and the
                    // queue is filled again below, with the new selection.
                    TryDelete(target.Path);
                    AppState.Log("prefetch: categories changed while downloading; picture dropped");
                    _prefetchTask = null;
                    discarded = true;
                    return null;
                }

                var state = AppState.Load();
                SetPending(state, target);
                state.Save();
                RemoveOtherWallpapers(state);
                AppState.Log("prefetched " + Path.GetFileName(target.Path));
            }

            return target.Path;
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
            if (discarded) EnsurePrefetched();
        }
    }

    private static bool HasPending(AppState state)
        => state.PendingPath.Length > 0 && File.Exists(state.PendingPath);

    private static Target PendingTarget(AppState state)
        => new(state.PendingPath, state.PendingTitle, state.PendingSource, state.PendingUrl);

    private static void SetPending(AppState state, Target target)
    {
        state.PendingPath = target.Path;
        state.PendingTitle = target.Title;
        state.PendingSource = target.Source;
        state.PendingUrl = target.Url;
    }

    private static void ClearPending(AppState state)
    {
        state.PendingPath = string.Empty;
        state.PendingTitle = string.Empty;
        state.PendingSource = string.Empty;
        state.PendingUrl = string.Empty;
    }

    /// <summary>Throws the queued picture away, bytes included.</summary>
    private void DiscardPending()
    {
        string path;
        lock (_gate)
        {
            var state = AppState.Load();
            path = state.PendingPath;
            ClearPending(state);
            state.Save();
        }

        if (path.Length > 0) TryDelete(path);
    }

    #endregion

    #region Files

    /// <summary>
    /// Deletes every wallpaper except the one in use, the preloaded one and the liked ones. The
    /// preloaded file must survive, otherwise the work the next switch depends on would be thrown
    /// away; a liked file must survive until the user unlikes it.
    /// </summary>
    private static void RemoveOtherWallpapers(AppState state)
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.CurrentPath.Length > 0) keep.Add(state.CurrentPath);
        if (state.PendingPath.Length > 0) keep.Add(state.PendingPath);
        foreach (var liked in state.Likes)
        {
            if (liked.Path.Length > 0) keep.Add(liked.Path);
        }

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

    private static Target? DownloadNewWallpaper()
    {
        var (width, height) = DisplayHelper.GetPrimaryResolution();
        var picture = FetchWallpaper(width, height);
        if (picture == null) return null;

        // The subject is part of the file name, so what is on disk is always traceable.
        var path = Path.Combine(AppState.DataDirectory,
            $"{picture.Value.Category}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg");

        return DownloadImage(picture.Value.Url, path)
            ? new Target(path, picture.Value.Title, picture.Value.Source, picture.Value.Url)
            : null;
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleWallpaper/1.0");
        return client;
    }

    /// <summary>
    /// Asks the API for a picture of the user's categories. A multi-selection is tried in random
    /// order, and a category that comes back empty backs up to the next one.
    /// </summary>
    private static (string Category, string Url, string Title, string Source)? FetchWallpaper(int width, int height)
    {
        var picked = Categories.Normalize(AppState.Load().Categories);

        foreach (var category in picked.OrderBy(_ => Random.Shared.Next()))
        {
            var hit = FetchFromUpx8(category, width, height);
            if (hit != null) return hit;
        }

        return null;
    }

    private static (string Category, string Url, string Title, string Source)? FetchFromUpx8(
        string category, int width, int height)
    {
        try
        {
            var json = Http.GetStringAsync(string.Format(Upx8Endpoint, category, width, height))
                .GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("data", out var data)) return null;

            // The service answers count=1 with an object and a larger count with an array: accept both.
            var item = data.ValueKind == JsonValueKind.Array
                ? (data.GetArrayLength() > 0 ? data[0] : default)
                : data;
            if (item.ValueKind != JsonValueKind.Object) return null;

            var url = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return null;

            var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? string.Empty : string.Empty;
            var source = item.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? string.Empty : string.Empty;

            return (category, url, title, source);
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
        state.CurrentTitle = string.Empty;
        state.CurrentSource = string.Empty;
        state.CurrentUrl = string.Empty;
        ClearPending(state);
        state.Likes.Clear();
        state.Categories.Clear();
        state.SwitchMode = SwitchSchedule.Default;
        state.NextSwitchAt = DateTime.MinValue;
        state.LockScreenEnabled = false;
        state.Save();

        AppState.Log("restored the Windows defaults");
    }

    #endregion
}
