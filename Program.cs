using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;

namespace SimpleWallpaper;

/// <summary>
/// One executable, three roles:
///   (no argument) the background program: desktop menu, daily wallpaper, retries and IPC
///   --next        the desktop menu entry that asks for the next wallpaper
///   --settings    the WPF settings window (its own process, so closing it never stops the program)
/// </summary>
internal static class Program
{
    internal const string CommandNext = "NEXT";
    internal const string CommandExit = "EXIT";
    internal const string CommandPing = "PING";
    /// <summary>The settings window picked another cycle: recompute the next switch from now.</summary>
    internal const string CommandMode = "MODE";
    /// <summary>The settings window changed the categories: fetch and apply a new wallpaper at once.</summary>
    internal const string CommandCategory = "CATEGORY";

    /// <summary>Where a menu entry leaves its command for the running program.</summary>
    private static string CommandFile => Path.Combine(AppState.DataDirectory, "command.txt");

    private const string GlobalMutexName = @"Global\SimpleWallpaperMutex";
    private const string LocalMutexName = @"Local\SimpleWallpaperMutex";

    // A network that is not up yet at logon recovers within seconds, so the first retry is soon
    // and later attempts back off.
    private static readonly TimeSpan[] RetryBackoff =
    {
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    };

    private static readonly TimeSpan NetworkEventDebounce = TimeSpan.FromSeconds(10);

    /// <summary>How often the persisted "next switch" moment is compared with the clock.</summary>
    private static readonly TimeSpan SchedulePollInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long a category change waits for a switch that is already running.</summary>
    private static readonly TimeSpan CategoryChangeWait = TimeSpan.FromSeconds(10);

    // A click that finds no running program starts it again. The first picture still has to be
    // downloaded before that click can be answered, so the wait for it covers a slow connection.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ManualResetEventSlim Shutdown = new(false);
    private static readonly object RetryGate = new();

    private static Mutex? _instanceMutex;
    private static Timer? _commandTimer;
    private static Timer? _scheduleTimer;

    /// <summary>Index into <see cref="RetryBackoff"/>; reset by every successful switch.</summary>
    private static int _retryAttempt;

    /// <summary>1 while a switch attempt is running, so two attempts never overlap.</summary>
    private static int _switchRunning;

    /// <summary>1 while the last attempt failed and is waiting for its retry.</summary>
    private static int _awaitingRetry;

    private static long _lastNetworkRetry;

    /// <summary>Read on the scheduler thread only; true while the internet looked unreachable.</summary>
    private static bool _offline;

    /// <summary>Tick count of the last online probe, so a dead network is asked about ten minutes apart.</summary>
    private static long _lastNetworkCheck;

    [STAThread]
    private static void Main(string[] args)
    {
        AppState.EnsureDataDirectory();

        switch (args.Length > 0 ? args[0].TrimStart('-').ToLowerInvariant() : string.Empty)
        {
            case "next":
                HandleNextCommand();
                return;

            case "settings":
                RunSettingsWindow();
                return;

            case "install-shellext":
                Environment.Exit(InstallShellExtension(args));
                return;

            case "self-test":
                Environment.Exit(SelfTest.Run());
                return;

            default:
                RunBackground();
                return;
        }
    }

    #region Background program

    private static void RunBackground()
    {
        // A second copy (for example a double logon start) has nothing to do.
        if (!AcquireInstanceMutex()) return;

        // A flag left behind by a program that was killed mid-switch would keep the settings window
        // saying "switching"/"retrying" about an attempt nobody is making.
        WallpaperManager.ClearSwitchFlags();

        var exePath = Environment.ProcessPath ?? string.Empty;
        var greysOut = EnsureShellExtension(exePath);
        RegistryHelper.RegisterDesktopMenu(exePath, greysOut);

        AppState.Log(greysOut
            ? "started; desktop menu registered (entry greys out while downloading)"
            : "started; desktop menu registered (no shell extension, entry cannot grey out)");

        var wallpapers = new WallpaperManager();
        StartCommandWatcher(wallpapers);
        StartNetworkWatcher(wallpapers);

        // The cycle starts with a switch right away, whatever mode is in effect, and follows the mode
        // the user picked from then on. Both run on background threads: neither start-up nor a logon
        // may wait on the network.
        RunScheduledSwitchInBackground(wallpapers);
        StartScheduleTimer(wallpapers);

        AppState.Log($"cycle {SwitchSchedule.Normalize(AppState.Load().SwitchMode)}; "
            + $"power {AcPowerOnline() switch { true => "mains", false => "battery" }}");

        Shutdown.Wait();

        _scheduleTimer?.Dispose();
        RegistryHelper.UnregisterDesktopMenu();
        ReleaseInstanceMutex();
        AppState.Log("stopped; desktop menu removed");
    }

    private static bool AcquireInstanceMutex()
    {
        // Global\ needs SeCreateGlobalPrivilege, which a standard user does not hold.
        foreach (var name in new[] { GlobalMutexName, LocalMutexName })
        {
            try
            {
                var mutex = new Mutex(true, name, out var createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }

                _instanceMutex = mutex;
                return true;
            }
            catch (Exception ex)
            {
                AppState.Log($"cannot create {name}: {ex.Message}");
            }
        }

        AppState.Log("no instance mutex available; continuing without a single instance guarantee");
        return true;
    }

    private static void ReleaseInstanceMutex()
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (Exception ex)
        {
            AppState.Log("releasing the instance mutex failed: " + ex.Message);
        }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }
    }

    #endregion

    #region Command line roles

    /// <summary>Directory holding the running executable; the shell extension DLL sits next to it.</summary>
    internal static string ExecutableDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? string.Empty;

    /// <summary>
    /// True while a background program is running. It holds this mutex for its whole lifetime, so
    /// asking the mutex is reliable - unlike a pipe connection, which can legitimately fail for a
    /// moment when two clicks arrive at once.
    /// </summary>
    internal static bool IsRunning()
    {
        foreach (var name in new[] { GlobalMutexName, LocalMutexName })
        {
            try
            {
                using var mutex = Mutex.OpenExisting(name);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Not created at all: try the next name.
            }
            catch (UnauthorizedAccessException)
            {
                return true;   // it exists, we are simply not allowed to open it
            }
            catch (Exception ex)
            {
                AppState.Log($"checking for a running program failed ({name}): {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>Handles the "Next Wallpaper" menu entry.</summary>
    private static void HandleNextCommand()
    {
        if (IsRunning())
        {
            SendCommand(CommandNext);
            return;
        }

        // Nothing is running: the entry outlived the program, which was closed or killed from
        // outside. Clicking means the user wants a wallpaper, so start the program again and hand
        // the click over once the first picture is there. Taking the entry down instead, as this
        // used to, made a program that had merely ended look as if it had crashed.
        if (!StartProgramAgain("--next")) return;

        if (WaitUntilReady())
        {
            SendCommand(CommandNext);
            return;
        }

        // It never came ready: keep the entry while the program is at least running, and take the
        // leftovers down only when it never appeared.
        if (!IsRunning()) TakeStaleMenuDown();
    }

    /// <summary>Runs the settings window in its own process.</summary>
    private static void RunSettingsWindow()
    {
        if (IsRunning())
        {
            SendCommand(CommandPing);
        }
        else
        {
            // The settings window works without the program - the startup entry and the lock screen
            // are its own - but the menu entry promises a running program, so bring it back.
            StartProgramAgain("--settings");
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        application.Run(new SettingsWindow());
    }

    /// <summary>
    /// Starts the background program when a menu entry finds it gone. Returns false when it cannot
    /// be started: the entries only exist while the program runs, so by then they are stale.
    /// </summary>
    private static bool StartProgramAgain(string source)
    {
        var exePath = Environment.ProcessPath;

        try
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) throw new FileNotFoundException(exePath);

            // Drop the flag left behind by the instance that is gone. Keeping it would make
            // WaitUntilReady answer at once for a picture that is not there, and the click would be
            // dropped by the download that has not even started yet.
            WallpaperManager.SetMenuClickable(false);

            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,     // keep the privileges of the click: no elevation here
                CreateNoWindow = true,
                WorkingDirectory = ExecutableDirectory,
            });

            AppState.Log($"{source}: no running program found; starting it again");
            return true;
        }
        catch (Exception ex)
        {
            AppState.Log($"{source}: no running program found and starting it again failed: {ex.Message}");
            TakeStaleMenuDown();
            return false;
        }
    }

    /// <summary>
    /// Waits for the freshly started program to publish a usable picture. The flag is what greys
    /// the menu entry out, so its appearance means the click can be answered from the queue instead
    /// of being dropped by the download that is still running.
    /// </summary>
    private static bool WaitUntilReady()
    {
        for (var waited = TimeSpan.Zero; waited < ReadyTimeout; waited += ReadyPollInterval)
        {
            if (File.Exists(AppState.ReadyFlagPath)) return true;
            Thread.Sleep(ReadyPollInterval);
        }

        return File.Exists(AppState.ReadyFlagPath);
    }

    /// <summary>Takes down menu entries whose program is not coming back, and the command nobody would read.</summary>
    private static void TakeStaleMenuDown()
    {
        RegistryHelper.UnregisterDesktopMenu();
        DiscardPendingCommand();
    }

    /// <summary>Asks the background program to restore the defaults and stop.</summary>
    internal static void RequestExit()
    {
        SendCommand(CommandExit);
    }

    #endregion

    #region Shell extension

    /// <summary>
    /// Registers the shell extension machine-wide when it is not registered yet. This is the only
    /// operation that ever needs administrator rights, and it happens once - without it the menu
    /// entry still works, it just cannot be greyed out.
    /// </summary>
    private static bool EnsureShellExtension(string exePath)
    {
        var dllPath = Path.Combine(ExecutableDirectory, "SimpleWallpaperShell.dll");

        if (!File.Exists(dllPath))
        {
            AppState.Log("shell extension not found next to the program: " + dllPath);
            return false;
        }

        if (RegistryHelper.IsShellExtensionRegistered(dllPath)) return true;

        AppState.Log("installing the shell extension (one administrator prompt)");
        if (StartElevated($"--install-shellext \"{dllPath}\" \"{exePath}\"", out var exitCode) && exitCode == 0)
        {
            return RegistryHelper.IsShellExtensionRegistered(dllPath);
        }

        AppState.Log("shell extension not installed; the menu entry will not grey out");
        return false;
    }

    /// <summary>Elevated helper: writes the machine-wide COM registration and exits.</summary>
    private static int InstallShellExtension(string[] args)
    {
        if (args.Length < 3)
        {
            AppState.Log("--install-shellext needs the DLL path and the executable path");
            return 2;
        }

        try
        {
            RegistryHelper.RegisterShellExtensionInMachine(args[1], args[2]);
            AppState.Log("shell extension registered machine-wide");
            return 0;
        }
        catch (Exception ex)
        {
            AppState.Log("registering the shell extension failed: " + ex.Message);
            return 1;
        }
    }

    private static bool StartElevated(string arguments, out int exitCode)
    {
        exitCode = -1;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            AppState.Log("cannot determine the executable path needed for elevation");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process == null) return false;

            process.WaitForExit();
            exitCode = process.ExitCode;
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            AppState.Log("the administrator prompt was declined");
            return false;
        }
        catch (Exception ex)
        {
            AppState.Log("starting the elevated helper failed: " + ex.Message);
            return false;
        }
    }

    #endregion

    #region Command file

    /// <summary>
    /// Watches for a command left by a menu entry. A file is used instead of a named pipe on
    /// purpose: a pipe owned by an elevated process refuses connections from a normal-privilege
    /// menu click, which made the desktop entries look as if they did nothing.
    /// </summary>
    private static void StartCommandWatcher(WallpaperManager wallpapers)
    {
        _commandTimer = new Timer(_ => PollCommand(wallpapers), null, 250, Timeout.Infinite);
    }

    private static void PollCommand(WallpaperManager wallpapers)
    {
        try
        {
            if (File.Exists(CommandFile))
            {
                var command = File.ReadAllText(CommandFile).Trim();
                File.Delete(CommandFile);
                HandleCommand(command, wallpapers);
            }
        }
        catch (Exception ex)
        {
            AppState.Log("reading the command file failed: " + ex.Message);
        }
        finally
        {
            // Only poll again once this round is finished, so two rounds can never overlap.
            _commandTimer?.Change(250, Timeout.Infinite);
        }
    }

    private static void HandleCommand(string? command, WallpaperManager wallpapers)
    {
        switch (command?.ToUpperInvariant())
        {
            case CommandNext:
                wallpapers.ApplyNext(out _);   // ApplyNext logs its own outcome
                break;

            case CommandMode:
                wallpapers.OnModeChanged(AcPowerOnline(), _offline);
                break;

            case CommandCategory:
                // Downloading takes a while and this thread also reads the command file, so the
                // category change gets its own thread.
                RunScheduledSwitchInBackground(wallpapers, freshDownload: true);
                break;

            case CommandExit:
                AppState.Log("exit requested");
                Shutdown.Set();
                break;

            case CommandPing:
                break;   // the program answering at all is the answer
        }
    }

    /// <summary>
    /// Hands a command to the running program. Writing it as a temp file and moving it into place
    /// means the reader never sees a half-written command.
    /// </summary>
    internal static void SendCommand(string command)
    {
        try
        {
            Directory.CreateDirectory(AppState.DataDirectory);
            var temp = CommandFile + ".tmp";
            File.WriteAllText(temp, command);
            File.Move(temp, CommandFile, true);
        }
        catch (Exception ex)
        {
            AppState.Log($"sending the '{command}' command failed: {ex.Message}");
        }
    }

    /// <summary>Drops a command that nobody is left to consume.</summary>
    private static void DiscardPendingCommand()
    {
        try
        {
            if (File.Exists(CommandFile)) File.Delete(CommandFile);
        }
        catch (Exception ex)
        {
            AppState.Log("discarding a stale command failed: " + ex.Message);
        }
    }

    #endregion

    #region Switch scheduling

    /// <summary>
    /// The switch runs on a background thread: neither start-up nor a logon may wait on the network.
    /// Called once at start-up (a new run always switches once) and for every category change.
    /// </summary>
    private static void RunScheduledSwitchInBackground(WallpaperManager wallpapers, bool freshDownload = false)
    {
        var thread = new Thread(() => RunScheduledSwitch(wallpapers, freshDownload))
        {
            IsBackground = true,
            Name = "WallpaperSwitch",
        };

        thread.Start();
    }

    /// <summary>
    /// Compares the persisted "next switch" moment with the clock every few seconds. Polling instead
    /// of arming one long timer is what covers sleep, hibernation, a changed system time and a missed
    /// logon: the moment is simply due when it has passed.
    /// </summary>
    private static void StartScheduleTimer(WallpaperManager wallpapers)
    {
        _scheduleTimer = new Timer(_ => EvaluateSchedule(wallpapers), null,
            SchedulePollInterval, SchedulePollInterval);
    }

    /// <summary>
    /// One tick: keeps the reason the cycle stands still up to date, and switches when no reason is
    /// left. Reading the power state on the same tick costs one call and makes a flat battery behave
    /// exactly as it does when the logon is on a laptop.
    /// </summary>
    private static void EvaluateSchedule(WallpaperManager wallpapers)
    {
        try
        {
            var state = RefreshCycleState(wallpapers);
            if (state.PauseReason.Length > 0) return;   // the cycle is on hold: nothing is due
            if (DateTime.Now < state.NextSwitchAt) return;

            RunScheduledSwitch(wallpapers);
        }
        catch (Exception ex)
        {
            AppState.Log("evaluating the switch cycle failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Reads the power state and the network, and turns both into the reason the cycle stands still.
    /// The network is what a paused cycle depends on most (it has to be asked again to know it came
    /// back), so while the program is offline the check keeps running even though the switches do not.
    /// </summary>
    private static AppState RefreshCycleState(WallpaperManager wallpapers)
    {
        // Asked on every tick while the network looks fine - a local adapter scan, so losing it is
        // noticed within seconds - and ten minutes apart once it is gone.
        CheckOnline();

        return AppState.Mutate(state => wallpapers.RefreshReason(state, AcPowerOnline(), _offline));
    }

    /// <summary>
    /// Whether the network is worth asking about now. While it looks fine that is every tick: this is
    /// a local adapter scan, and losing the network has to be noticed within seconds. Once it is gone
    /// two answers are at least ten minutes apart - a pause that waits for the network waits for the
    /// countdown the settings window shows, and a pause that has nothing to do with the network (a
    /// flat battery, "no cycling") still asks every ten minutes, so the answer cannot go stale just
    /// because the cycle is stopped for another reason.
    /// </summary>
    private static bool IsNetworkCheckDue()
    {
        if (!_offline) return true;

        var state = AppState.Load();
        return state.PauseReason == SwitchSchedule.NetworkReason
            ? DateTime.Now >= state.NextSwitchAt
            : Environment.TickCount64 - _lastNetworkCheck
                >= (long)WallpaperManager.NetworkCheckInterval.TotalMilliseconds;
    }

    /// <summary>
    /// Writes down the wait until the network is worth asking about again, which is also what the
    /// settings window counts down. Doing it whenever the check came back offline is what stops the
    /// countdown from sitting at zero: the next check always has its own ten minutes ahead of it.
    /// </summary>
    private static void EnsureNetworkCheckInterval()
    {
        AppState.Mutate(state =>
        {
            state.NextSwitchAt = DateTime.Now + WallpaperManager.NetworkCheckInterval;
            return true;
        });
    }

    /// <summary>
    /// Asks whether the internet is reachable. Losing it and regaining it are both put in the log, and
    /// both are carried into the state the settings window reads: coming back empty handed writes the
    /// next ten minute wait down at once, which is what keeps the countdown off zero, while coming
    /// back with an answer lets <see cref="WallpaperManager.RefreshReason"/> resume the cycle.
    /// </summary>
    private static void CheckOnline()
    {
        if (!IsNetworkCheckDue()) return;

        // Asked now, so the ten minute gap counts from this answer.
        _lastNetworkCheck = Environment.TickCount64;

        var reachable = (HasInternetConnectionForSelfTest ?? HasInternetConnection)();
        if (reachable != !_offline)
        {
            _offline = !reachable;
            AppState.Log(reachable
                ? "network became reachable again"
                : "network is unreachable; checking again every ten minutes");
        }

        // Whether it just went away or was still away: the next check gets its own ten minutes, which
        // is what keeps the countdown the settings window shows off zero.
        if (!reachable) EnsureNetworkCheckInterval();
    }

    /// <summary>The internet probe and the reachability the self test drives by hand.</summary>
    internal static Func<bool>? HasInternetConnectionForSelfTest { get; set; }

    /// <summary>The self test's handle on the scheduler tick it has to move by hand.</summary>
    internal static Action<WallpaperManager> ScheduleTick => EvaluateSchedule;

    internal static bool OfflineFlag { get => _offline; set => _offline = value; }

    /// <summary>
    /// Whether an adapter that can actually reach the internet is up. NetworkInterface's own
    /// GetIsNetworkAvailable answers yes as soon as any adapter is up, and a VMware or Wi-Fi Direct
    /// adapter holding nothing but a link-local address is up all the time - which is why the cycle
    /// would never see a dead network. An address that is neither loopback nor link-local is what a
    /// real connection has.
    /// </summary>
    private static bool HasInternetConnection()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (!address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) return true;
                    }
                    else if (address.Address.AddressFamily == AddressFamily.InterNetworkV6
                        && !address.Address.IsIPv6LinkLocal)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppState.Log("checking the network failed: " + ex.Message);
            return true;   // unknown: a pause over an unreadable answer helps nobody
        }

        return false;
    }

    /// <summary>
    /// AC power through GetSystemPowerStatus: the same Win32 call Windows uses for the battery
    /// symbol, so the program needs neither SystemEvents nor the WinForms stack for it. When the
    /// answer cannot be read, the program stays on the safe side and assumes the mains.
    /// </summary>
    private static bool AcPowerOnline()
    {
        try
        {
            if (GetSystemPowerStatus(out var status) && status.ACLineStatus != 255)
            {
                return status.ACLineStatus == 1;
            }
        }
        catch (Exception ex)
        {
            AppState.Log("reading the power state failed: " + ex.Message);
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>
    /// One switch attempt. Only an applied wallpaper moves the cycle on; a failed attempt backs off,
    /// and the delay is carried in the state, so the next tick simply waits it out. Returns false when
    /// another attempt is already running - a category change then waits for its turn instead of being
    /// dropped. A manual click never comes through here, which is what keeps it away from the cycle.
    /// </summary>
    private static bool RunScheduledSwitch(WallpaperManager wallpapers, bool freshDownload = false)
    {
        if (!TryTakeSwitchSlot(waitForIt: freshDownload)) return false;

        try
        {
            var applied = false;
            try
            {
                applied = freshDownload
                    ? wallpapers.ApplyCategoryChange(out _)
                    : wallpapers.ApplyScheduled(out _);
            }
            catch (Exception ex)
            {
                AppState.Log("the switch failed: " + ex.Message);
            }

            if (applied)
            {
                Volatile.Write(ref _retryAttempt, 0);
                Volatile.Write(ref _awaitingRetry, 0);
                WallpaperManager.SetRetryFlag(false);
                return true;
            }

            lock (RetryGate)
            {
                var attempt = _retryAttempt;
                var delay = RetryBackoff[Math.Min(attempt, RetryBackoff.Length - 1)];
                _retryAttempt = attempt + 1;

                AppState.Mutate(state =>
                {
                    state.NextSwitchAt = DateTime.Now + delay;
                    return true;
                });

                Volatile.Write(ref _awaitingRetry, 1);
                WallpaperManager.SetRetryFlag(true);   // the countdown below counts to a retry, not a cycle
                AppState.Log($"switch attempt failed; retry #{attempt + 1} in {delay.TotalMinutes:0} minute(s)");
            }

            return true;
        }
        finally
        {
            Volatile.Write(ref _switchRunning, 0);
        }
    }

    /// <summary>
    /// Takes the single switch slot. Returns false when another attempt holds it - or, for a category
    /// change, when it held it for longer than <see cref="CategoryChangeWait"/>.
    /// </summary>
    private static bool TryTakeSwitchSlot(bool waitForIt)
    {
        if (Interlocked.Exchange(ref _switchRunning, 1) == 0) return true;   // the slot is ours
        if (!waitForIt) return false;

        for (var waited = TimeSpan.Zero; waited < CategoryChangeWait; waited += ReadyPollInterval)
        {
            Thread.Sleep(ReadyPollInterval);
            if (Interlocked.Exchange(ref _switchRunning, 1) == 0) return true;   // that one finished
        }

        return false;
    }

    /// <summary>
    /// Retries the moment the network comes back, which at logon is usually seconds after the first
    /// attempt failed. Both events are watched because either can fire first, and a switch that is
    /// merely waiting for a later cycle is left alone.
    /// </summary>
    private static void StartNetworkWatcher(WallpaperManager wallpapers)
    {
        var thread = new Thread(() =>
        {
            try
            {
                NetworkChange.NetworkAvailabilityChanged += (_, e) => OnNetworkChanged(wallpapers, e.IsAvailable);
                NetworkChange.NetworkAddressChanged += (_, _) =>
                    OnNetworkChanged(wallpapers, HasInternetConnection());
            }
            catch (Exception ex)
            {
                AppState.Log("cannot watch network changes: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "NetworkWatcher",
        };

        thread.Start();
    }

    private static void OnNetworkChanged(WallpaperManager wallpapers, bool available)
    {
        // The scheduler owns the decision: it recomputes the reason on its next tick and re-arms the
        // cycle, but only switches when the cycle actually runs. Switching here would put a new
        // picture up through a "no cycling" or a flat battery, which are exactly the states the
        // user asked to be left alone.
        if (available && _offline)
        {
            _offline = false;
            AppState.Log("network became available; the cycle picks it up on its next check");
            return;
        }

        if (!available) return;
        if (Volatile.Read(ref _awaitingRetry) == 0) return;   // nothing is waiting on the network
        if (AppState.Load().PauseReason.Length > 0) return;   // the cycle stands still: nothing is due

        // Availability changes arrive in bursts (adapter up, address bound, ...): act once.
        if (Environment.TickCount64 - _lastNetworkRetry < (long)NetworkEventDebounce.TotalMilliseconds) return;
        _lastNetworkRetry = Environment.TickCount64;

        lock (RetryGate)
        {
            _retryAttempt = 0;   // the backoff is over: try as if it were the first attempt
        }

        AppState.Log("network became available; retrying the switch now");
        RunScheduledSwitchInBackground(wallpapers);
    }

    #endregion
}

/// <summary>
/// Minimal runnable check for the non-trivial helpers: "SimpleWallpaper.exe --self-test".
/// The program is a WinExe, so the result also goes to %APPDATA%\Wallpaper\selftest.log.
/// </summary>
internal static class SelfTest
{
    private const int AttachParentProcess = -1;
    private static readonly List<string> Lines = new();

    public static int Run()
    {
        AttachConsole(AttachParentProcess);   // best effort: works when started from a shell

        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            var line = $"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " -> " + detail : string.Empty)}";
            Lines.Add(line);
            Console.WriteLine(line);
            if (!ok) failures++;
        }

        try
        {
            // A real manager, so the flow below drives the same object the program uses. Its own
            // prefetch never keeps a thread alive; the watcher is not started here.
            var wallpapers = new WallpaperManager();

            var (width, height) = DisplayHelper.GetPrimaryResolution();
            Check("primary resolution detected", width > 0 && height > 0, $"{width}x{height}");

            var dpi = DisplayHelper.DpiAwarenessText();
            Check("display scaling mode", dpi == "per-monitor v2", dpi);

            Check("windows version readable", DisplayHelper.IsWindows10OrGreater(), DisplayHelper.OSVersionText);
            Check("lock screen support evaluated", true,
                DisplayHelper.SupportsLockScreenWallpaper() ? "supported" : "unsupported");

            var (reachable, lockImage) = WallpaperManager.QueryLockScreenImage();
            Check("winrt lock screen api reachable", reachable, "Windows.System.UserProfile.LockScreen");
            Check("current lock screen image", true, lockImage ?? "<none>");

            var state = new AppState
            {
                CurrentPath = @"C:\temp\a.jpg",
                CurrentTitle = "山间云海",
                CurrentSource = "birdpaper",
                SwitchMode = SwitchSchedule.Interval120,
                NextSwitchAt = new DateTime(2030, 1, 2, 3, 4, 5),
                ModeRequestSeq = 7,
                ModeAppliedSeq = 6,
                Categories = new List<string> { "nature", "city" },
                Likes = new List<LikedWallpaper> { new() { Path = @"C:\temp\a.jpg", Title = "山间云海" } },
            };
            var restored = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state));
            Check("state.json round trip",
                restored != null
                && restored.CurrentPath == state.CurrentPath
                && restored.CurrentTitle == state.CurrentTitle
                && restored.CurrentSource == state.CurrentSource
                && restored.SwitchMode == state.SwitchMode
                && restored.NextSwitchAt == state.NextSwitchAt
                && restored.ModeRequestSeq == 7
                && restored.ModeAppliedSeq == 6
                && string.Join(",", restored.Categories) == "nature,city"
                && restored.Likes.Count == 1 && restored.Likes[0].Title == "山间云海");

            Check("desktop menu naming", Strings.MenuNextWallpaper.Length > 0 && Strings.MenuSettings.Length > 0,
                $"{Strings.MenuNextWallpaper} / {Strings.MenuSettings}");

            var exePath = Environment.ProcessPath ?? string.Empty;
            Check("startup state readable", exePath.Length > 0,
                RegistryHelper.IsStartupEnabled(exePath) ? "enabled" : "disabled");

            var now = DateTime.Now;
            var in30 = SwitchSchedule.NextDue(now, SwitchSchedule.Interval30);
            Check("30 minute cycle counts from now",
                Math.Abs((in30 - now - TimeSpan.FromMinutes(30)).TotalSeconds) < 1, in30.ToString("HH:mm:ss"));

            var in6h = SwitchSchedule.NextDue(now, SwitchSchedule.Interval360);
            Check("6 hour cycle counts from now",
                Math.Abs((in6h - now - TimeSpan.FromHours(6)).TotalSeconds) < 1, in6h.ToString("HH:mm:ss"));

            var daily = SwitchSchedule.NextDue(now, SwitchSchedule.Daily);
            Check("daily cycle waits for the next midnight",
                daily.Hour == 0 && daily.Minute == 0 && daily > now && daily <= now.AddDays(1),
                daily.ToString("yyyy-MM-dd HH:mm"));

            var half = SwitchSchedule.NextDue(now, SwitchSchedule.HalfDay);
            Check("half day cycle waits for 0:00 or 12:00",
                (half.Hour == 0 || half.Hour == 12) && half.Minute == 0 && half > now && half <= now.AddHours(13),
                half.ToString("yyyy-MM-dd HH:mm"));

            Check("unknown cycle falls back to daily", SwitchSchedule.Normalize("nonsense") == SwitchSchedule.Daily,
                SwitchSchedule.Default);

            Check("no cycling never comes due",
                SwitchSchedule.NextDue(now, SwitchSchedule.None) == DateTime.MaxValue
                && !SwitchSchedule.IsLooping(SwitchSchedule.None)
                && SwitchSchedule.IsLooping(SwitchSchedule.Interval30),
                SwitchSchedule.None);

            Check("cycle pause reason",
                SwitchSchedule.PauseReason(onBattery: true, networkDown: false, SwitchSchedule.Interval30) == SwitchSchedule.BatteryReason
                && SwitchSchedule.PauseReason(onBattery: false, networkDown: true, SwitchSchedule.Interval30) == SwitchSchedule.NetworkReason
                && SwitchSchedule.PauseReason(onBattery: false, networkDown: false, SwitchSchedule.None) == SwitchSchedule.FixedReason
                && SwitchSchedule.PauseReason(onBattery: false, networkDown: true, SwitchSchedule.None) == SwitchSchedule.FixedReason
                && SwitchSchedule.PauseReason(onBattery: false, networkDown: false, SwitchSchedule.Interval30).Length == 0,
                $"battery>{SwitchSchedule.BatteryReason}, network>{SwitchSchedule.NetworkReason}, no cycle>{SwitchSchedule.FixedReason}");

            Check("countdown format",
                Strings.Countdown(TimeSpan.FromSeconds(740)) == (Strings.IsChinese ? "12分20秒" : "12m 20s")
                && Strings.Countdown(TimeSpan.FromSeconds(43200)) == (Strings.IsChinese ? "12时0分0秒" : "12h 0m 0s")
                && Strings.Countdown(TimeSpan.FromSeconds(-5)) == (Strings.IsChinese ? "0分0秒" : "0m 0s"),
                $"{Strings.Countdown(TimeSpan.FromSeconds(740))} / {Strings.Countdown(TimeSpan.FromSeconds(43200))}");

            // Checked in whichever language this run uses, so the words that end up on screen are the
            // ones actually in the build being tested.
            Check("battery pause line",
                Strings.BatteryPaused == (Strings.IsChinese
                    ? "电池模式停用自动切换以节省电量"
                    : "Battery mode: automatic switching is OFF"),
                Strings.BatteryPaused);

            Check("liked wallpaper chance ladder",
                WallpaperManager.LikeChancePercent(0) == 0
                && WallpaperManager.LikeChancePercent(5) == 10
                && WallpaperManager.LikeChancePercent(6) == 20
                && WallpaperManager.LikeChancePercent(19) == 20
                && WallpaperManager.LikeChancePercent(20) == 30
                && WallpaperManager.LikeChancePercent(49) == 30
                && WallpaperManager.LikeChancePercent(50) == 50,
                "1-5:10% 6-19:20% 20-49:30% 50+:50%");

            Check("category selection normalised",
                string.Join(",", Categories.Normalize(new[] { "city", "nature" })) == "nature,city"
                && string.Join(",", Categories.Normalize(new[] { "bogus" })) == "nature"
                && Categories.Normalize(new[] { "space" }).Count == 1,
                string.Join(",", Categories.All));

            CheckNetworkCheckFlow(Check, wallpapers);
            CheckStateUpdatesAreAtomic(Check);
            CheckManualSwitchRestartsInterval(Check, wallpapers);
        }
        catch (Exception ex)
        {
            Check("self-test crashed", false, ex.Message);
        }

        var summary = failures == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({failures} failure(s))";
        Lines.Add(summary);
        Console.WriteLine(summary);

        try
        {
            AppState.EnsureDataDirectory();
            File.WriteAllLines(AppState.SelfTestLogPath, Lines);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cannot write the self test log: " + ex.Message);
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Drives the network part of the cycle by hand: a wait that has run out must ask again, and the
    /// countdown must start over when the answer is still "no", so that the settings window never
    /// shows a countdown frozen at zero. The whole flow runs against a throwaway folder, so the state
    /// of a program that is running at the same time is left untouched.
    /// </summary>
    private static void CheckNetworkCheckFlow(Action<string, bool, string> check, WallpaperManager wallpapers)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SimpleWallpaperSelfTest-" + Guid.NewGuid().ToString("N"));
        var wasOffline = Program.OfflineFlag;

        try
        {
            AppState.DataDirectoryForSelfTest = folder;

            AppState.Mutate(state =>
            {
                state.SwitchMode = SwitchSchedule.Interval30;
                state.PauseReason = SwitchSchedule.NetworkReason;
                state.NextSwitchAt = DateTime.Now.AddSeconds(-1);   // the countdown has just run out
                return true;
            });

            Program.HasInternetConnectionForSelfTest = () => false;
            Program.OfflineFlag = true;
            Program.ScheduleTick(wallpapers);

            var stillOffline = AppState.Load();
            check("no network: the wait starts over instead of sticking at zero",
                stillOffline.PauseReason == SwitchSchedule.NetworkReason
                && stillOffline.NextSwitchAt > DateTime.Now.AddMinutes(9)
                && stillOffline.NextSwitchAt <= DateTime.Now.AddMinutes(11),
                $"next check in {Strings.Countdown(stillOffline.NextSwitchAt - DateTime.Now)}");

            // The line the settings window shows for that state: the wait is counting towards the
            // next check, and the moment it is over is read as "checking" instead of as a zero.
            check("no network: the line counts to the next check and then says checking",
                stillOffline.NextSwitchAt > DateTime.Now
                && Strings.CheckingNetwork.Length > 0,
                $"{Strings.OfflineLabel}{Strings.Countdown(stillOffline.NextSwitchAt - DateTime.Now)}"
                + $" / {Strings.CheckingNetwork}");

            // The check is due again: with the network back, the cycle has to resume from now.
            Program.HasInternetConnectionForSelfTest = () => true;
            stillOffline = AppState.Mutate(state =>
            {
                state.NextSwitchAt = DateTime.Now.AddSeconds(-1);
                return state;
            });
            Program.ScheduleTick(wallpapers);

            var backOnline = AppState.Load();
            check("network back: the cycle resumes from now",
                backOnline.PauseReason.Length == 0
                && backOnline.NextSwitchAt > DateTime.Now.AddMinutes(29)
                && backOnline.NextSwitchAt <= DateTime.Now.AddMinutes(31),
                $"next switch in {Strings.Countdown(backOnline.NextSwitchAt - DateTime.Now)}");
        }
        catch (Exception ex)
        {
            check("network check flow", false, ex.Message);
        }
        finally
        {
            Program.HasInternetConnectionForSelfTest = null;
            AppState.DataDirectoryForSelfTest = null;
            Program.OfflineFlag = wasOffline;

            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex)
            {
                AppState.Log("removing the self test folder failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Shows that two writers cannot lose each other's work: every concurrent update must survive.
    /// It runs against a throwaway folder, and it is the check that fails if a caller ever goes back
    /// to "read a snapshot, change it, write it back".
    /// </summary>
    private static void CheckStateUpdatesAreAtomic(Action<string, bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SimpleWallpaperSelfTest-" + Guid.NewGuid().ToString("N"));

        try
        {
            AppState.DataDirectoryForSelfTest = folder;

            const int writers = 8;
            const int updatesEach = 20;

            var threads = new List<Thread>();
            for (var writer = 0; writer < writers; writer++)
            {
                var thread = new Thread(() =>
                {
                    for (var i = 0; i < updatesEach; i++)
                    {
                        AppState.Mutate(state =>
                        {
                            state.ModeRequestSeq++;
                            return true;
                        });
                    }
                })
                {
                    IsBackground = true,
                };

                threads.Add(thread);
            }

            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join();

            var kept = AppState.Load().ModeRequestSeq;
            check("concurrent state updates all survive",
                kept == writers * updatesEach,
                $"{kept} of {writers * updatesEach} updates kept");
        }
        catch (Exception ex)
        {
            check("concurrent state updates", false, ex.Message);
        }
        finally
        {
            AppState.DataDirectoryForSelfTest = null;

            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex)
            {
                AppState.Log("removing the self test folder failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// A click on "next wallpaper" must start an interval cycle over, so the picture the user just
    /// chose is not replaced a moment later by a switch that was already nearly due - while the two
    /// clock cycles keep their fixed 0:00 / 12:00 moment. It drives the real command against a
    /// throwaway folder, with the wallpaper itself left alone (see ApplyWallpaperForSelfTest), so
    /// asking the question cannot change what is on the desktop.
    /// </summary>
    private static void CheckManualSwitchRestartsInterval(Action<string, bool, string> check, WallpaperManager wallpapers)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SimpleWallpaperSelfTest-" + Guid.NewGuid().ToString("N"));
        var picture = Path.Combine(folder, "queued.jpg");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(picture, new byte[4096]);
            AppState.DataDirectoryForSelfTest = folder;
            WallpaperManager.ApplyWallpaperForSelfTest = false;   // the desktop itself stays untouched

            // The interval cycles all count from the click.
            RestartsCountdown(check, wallpapers, SwitchSchedule.Interval30, TimeSpan.FromMinutes(30), picture, restarts: true);
            RestartsCountdown(check, wallpapers, SwitchSchedule.Interval60, TimeSpan.FromHours(1), picture, restarts: true);
            RestartsCountdown(check, wallpapers, SwitchSchedule.Interval120, TimeSpan.FromHours(2), picture, restarts: true);
            RestartsCountdown(check, wallpapers, SwitchSchedule.Interval360, TimeSpan.FromHours(6), picture, restarts: true);

            // The clock cycles keep their moment: it belongs to 0:00 / 12:00, not to a click.
            RestartsCountdown(check, wallpapers, SwitchSchedule.HalfDay, TimeSpan.FromMinutes(5), picture, restarts: false);
            RestartsCountdown(check, wallpapers, SwitchSchedule.Daily, TimeSpan.FromMinutes(5), picture, restarts: false);
        }
        catch (Exception ex)
        {
            check("manual switch restarts the countdown", false, ex.Message);
        }
        finally
        {
            WallpaperManager.ApplyWallpaperForSelfTest = null;
            AppState.DataDirectoryForSelfTest = null;

            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception ex)
            {
                AppState.Log("removing the self test folder failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// One cycle tried on its own: the next switch is put five minutes away, a manual switch is run,
    /// and the moment afterwards has to be either "now + the cycle" or exactly the moment from before.
    /// </summary>
    private static void RestartsCountdown(Action<string, bool, string> check, WallpaperManager wallpapers,
        string mode, TimeSpan interval, string picture, bool restarts)
    {
        var soon = DateTime.Now.AddMinutes(5);
        AppState.Mutate(state =>
        {
            state.SwitchMode = mode;
            state.PauseReason = string.Empty;
            state.NextSwitchAt = soon;
            state.CurrentPath = string.Empty;
            state.PendingPath = picture;
            state.PendingTitle = string.Empty;
            state.PendingSource = string.Empty;
            state.PendingUrl = string.Empty;
            return true;
        });

        var applied = wallpapers.ApplyNext(out _);
        var next = AppState.Load().NextSwitchAt;
        var away = next - DateTime.Now;

        var ok = applied
            && (restarts
                ? away > interval - TimeSpan.FromSeconds(10) && away <= interval + TimeSpan.FromSeconds(10)
                : next == soon);

        check($"manual switch on {mode}: countdown {(restarts ? "starts over" : "stays put")}",
            ok,
            restarts
                ? $"{Strings.Countdown(away)} until the next switch"
                : $"next switch kept at {soon:HH:mm:ss}");
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
