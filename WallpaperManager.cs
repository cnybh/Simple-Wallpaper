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
    /// The desktop menu entry: applies the preloaded picture. An interval cycle (30 minutes, 1/2/6
    /// hours) starts counting again from the click, so a picture the user just picked by hand is not
    /// thrown away a moment later by a switch that was already nearly due. The two clock cycles are
    /// left alone, because their next switch belongs to 0:00 / 12:00 and not to a moment of the
    /// user's choosing - and a paused cycle stays paused, with only its moment moved on.
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

            var restartCountdown = SwitchSchedule.CountsFromNow(AppState.Load().SwitchMode);
            return Switch(usePending: true, reschedule: restartCountdown, out name);
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
        var state = AppState.Mutate(current =>
        {
            current.SwitchMode = SwitchSchedule.Normalize(current.SwitchMode);

            // Forced: a new cycle must move the countdown even when the pause reason is unchanged,
            // which is exactly the case a plain RefreshReason would return from without rescheduling.
            current = RefreshReason(current, acOnline, networkDown, force: true);

            // A pause that survived the change holds no wait: the new cycle's length is what it would
            // run for, and the wait the cycle the user replaced had left is not that length. The next
            // resume then counts the new cycle out from its start.
            if (current.PauseReason.Length > 0) current.PausedRemaining = TimeSpan.Zero;

            // Only now is the settings window allowed to say the change took: its dialog waits for
            // the sequence it wrote to come back applied.
            current.ModeAppliedSeq = current.ModeRequestSeq;
            return current;
        });

        AppState.Log($"switch cycle is now {state.SwitchMode}; next switch {state.NextSwitchAt:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>
    /// Turns "flat battery / no network / the user picked no cycle" into the reason the settings
    /// window shows, and keeps the switch moment right through the pause.
    ///
    /// The two kinds of cycle are treated differently, which is the whole point of
    /// <see cref="SwitchSchedule.CountsFromNow"/>:
    ///
    /// * An interval (30 minutes, 1/2/6 hours) counts a wait down. A pause puts that wait - what the
    ///   cycle still had in front of it - into <see cref="AppState.PausedRemaining"/>, and resuming
    ///   waits it out from that moment. Ten minutes off the mains or off the network therefore cost
    ///   the cycle nothing: 26 minutes left before the pause are 26 minutes left after it.
    /// * A clock cycle ("按上/下午", "按日期每天") belongs to 0:00 / 12:00 and does not count anything
    ///   down. Its pause only stops the switching, not the clock, so the moment stays the next anchor
    ///   and is recomputed on every resume: an anchor that passed while the cycle stood still is gone,
    ///   and the cycle waits for the next one instead of switching late.
    ///
    /// Only a new cycle starts over, because its length (or its anchor) is what the user just changed.
    /// <paramref name="force"/> reschedules even when the reason is unchanged, which is what a new
    /// cycle needs: two intervals share the "running" reason but not the moment they come due.
    /// It works on the state it is given and does not write; the caller owns the whole update, so
    /// nothing here can be saved on top of a change another process made in between.
    /// </summary>
    internal AppState RefreshReason(AppState state, bool acOnline, bool networkDown, bool log = true, bool force = false)
    {
        var reason = SwitchSchedule.PauseReason(!acOnline, networkDown, state.SwitchMode);
        if (reason == state.PauseReason && !force) return state;

        var countsFromNow = SwitchSchedule.CountsFromNow(state.SwitchMode);
        state.PauseReason = reason;

        if (reason.Length > 0)
        {
            // Going on hold. An interval has a wait worth keeping, and never a negative one - a cycle
            // whose moment has already passed owes a switch rather than a wait. A clock cycle has no
            // wait to keep: its moment is an anchor the pause does not move.
            if (countsFromNow && state.PausedRemaining <= TimeSpan.Zero && state.NextSwitchAt != DateTime.MaxValue)
            {
                var wait = state.NextSwitchAt - DateTime.Now;
                state.PausedRemaining = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }

            state.NextSwitchAt = reason == SwitchSchedule.NetworkReason
                ? DateTime.Now + NetworkCheckInterval   // when the network is asked again
                : DateTime.MaxValue;                    // nothing is due while it stands still
        }
        else
        {
            // Running again. An interval waits out the time it had left, so the pause - however long
            // it lasted - is simply missing from its countdown. A clock cycle is recomputed to its
            // next anchor, which skips one that passed while the cycle stood still. Nothing set aside
            // means no interval was ever waiting (a first run, or one that went "no cycle"): that one
            // starts a whole cycle from now.
            var wait = state.PausedRemaining;
            state.PausedRemaining = TimeSpan.Zero;
            state.NextSwitchAt = countsFromNow && wait > TimeSpan.Zero
                ? DateTime.Now + wait
                : SwitchSchedule.NextDue(DateTime.Now, state.SwitchMode);
        }

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
    /// The path of the file written while a whole switch is running. The flag itself is written in
    /// <see cref="Switch"/>; the settings window, which runs in another process, reads it to tell
    /// "the picture is being fetched" from "the moment has passed and nothing happened yet".
    /// </summary>
    internal static string SwitchingFlagPath => Path.Combine(AppState.DataDirectory, "switching.flag");

    /// <summary>
    /// The path of the file that is there while a failed switch is waiting for its retry. The waiting
    /// is held in the state's next-switch moment, which the settings window only shows as a countdown;
    /// this flag is what lets it say "retrying" instead, so a countdown that suddenly reads one minute
    /// again is explained rather than mysterious.
    /// </summary>
    internal static string RetryFlagPath => Path.Combine(AppState.DataDirectory, "retry.flag");

    /// <summary>
    /// Records that a failed switch is waiting for its retry, or clears that record once one landed.
    /// Both flags are only ever a hint for the settings window; nothing in the program reads them, so
    /// a lost write can never change what the cycle does.
    /// </summary>
    internal static void SetRetryFlag(bool waiting)
    {
        var flag = RetryFlagPath;

        try
        {
            if (waiting)
            {
                File.WriteAllText(flag, "retrying");
            }
            else if (File.Exists(flag))
            {
                File.Delete(flag);
            }
        }
        catch (Exception ex)
        {
            AppState.Log($"updating the retry flag failed: {ex.Message}");
        }
    }

    /// <summary>Drops both hint files: called once when the program starts, so nothing stale shows.</summary>
    internal static void ClearSwitchFlags()
    {
        SetRetryFlag(false);

        var switching = SwitchingFlagPath;

        try
        {
            if (File.Exists(switching)) File.Delete(switching);
        }
        catch (Exception ex)
        {
            AppState.Log("removing the switching flag failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The one place a wallpaper is put on screen: decides between the freshly downloaded picture and
    /// a liked one, stores what was applied, cleans up and refills the queue. The whole of it is what
    /// the settings window shows "switching" for, however long the download takes - and the flag goes
    /// away again whatever the outcome, so a stale file can never announce a switch that is over.
    /// Whether a failed attempt is waiting for its retry is a separate hint, owned by the cycle that
    /// schedules the retry (see <see cref="RetryFlagPath"/>).
    /// </summary>
    private bool Switch(bool usePending, bool reschedule, out string name)
    {
        var flag = SwitchingFlagPath;

        try
        {
            File.WriteAllText(flag, "switching");
        }
        catch (Exception ex)
        {
            AppState.Log("writing the switching flag failed: " + ex.Message);
        }

        try
        {
            return ApplySwitch(usePending, reschedule, out name);
        }
        finally
        {
            try
            {
                if (File.Exists(flag)) File.Delete(flag);
            }
            catch (Exception ex)
            {
                AppState.Log("removing the switching flag failed: " + ex.Message);
            }
        }
    }

    private bool ApplySwitch(bool usePending, bool reschedule, out string name)
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
                // Read, change and write as one step: the settings window stores likes in this same
                // file, and a like must not be dropped by a switch that never saw it. The state that
                // was written is handed back with the target, so the clean-up below judges the files
                // by what is on disk now rather than by a snapshot from before the write.
                (applied, var state) = AppState.Mutate(current =>
                {
                    // Likes whose file is gone must not be counted or drawn.
                    current.Likes.RemoveAll(liked => liked.Path.Length == 0 || !File.Exists(liked.Path));

                    var liked = DrawLikedWallpaper(current);
                    var target = liked ?? fresh;

                    if (liked == null)
                    {
                        ClearPending(current);
                    }
                    else
                    {
                        // A liked picture won: the new download is not wasted, it waits for the next switch.
                        SetPending(current, fresh);
                    }

                    current.CurrentPath = target.Path;
                    current.CurrentTitle = target.Title;
                    current.CurrentSource = target.Source;
                    current.CurrentUrl = target.Url;

                    if (reschedule) current.NextSwitchAt = SwitchSchedule.NextDue(DateTime.Now, current.SwitchMode);
                    return (target, current);
                });

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

                // The queue is written onto the state as it is now, so a like stored in the meantime
                // survives.
                var queued = AppState.Mutate(current =>
                {
                    SetPending(current, target);
                    return current;
                });
                RemoveOtherWallpapers(queued);
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
            path = AppState.Mutate(state =>
            {
                var queued = state.PendingPath;
                ClearPending(state);
                return queued;
            });
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

    /// <summary>
    /// Lets the self test run a whole switch without touching the real desktop and lock screen, so a
    /// question about the cycle's next moment cannot change what the user is looking at. Null in a
    /// normal run.
    /// </summary>
    internal static bool? ApplyWallpaperForSelfTest { get; set; }

    private static void ApplyCurrent(AppState state)
    {
        if (state.CurrentPath.Length == 0 || !File.Exists(state.CurrentPath)) return;
        if (ApplyWallpaperForSelfTest == false) return;

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SimpleWallpaper/1.0.2");
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

        AppState.Mutate(state =>
        {
            state.CurrentPath = string.Empty;
            state.CurrentTitle = string.Empty;
            state.CurrentSource = string.Empty;
            state.CurrentUrl = string.Empty;
            ClearPending(state);
            state.Likes.Clear();
            state.Categories.Clear();
            state.SwitchMode = SwitchSchedule.Default;
            state.NextSwitchAt = DateTime.MinValue;
            state.PausedRemaining = TimeSpan.Zero;
            state.LockScreenEnabled = false;
            return true;
        });

        AppState.Log("restored the Windows defaults");
    }

    #endregion
}
