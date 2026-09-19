using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
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

    // A click that finds no running program starts it again. The first picture still has to be
    // downloaded before that click can be answered, so the wait for it covers a slow connection.
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly ManualResetEventSlim Shutdown = new(false);
    private static readonly object RetryGate = new();

    private static Mutex? _instanceMutex;
    private static Timer? _retryTimer;
    private static Timer? _commandTimer;
    private static int _retryAttempt;
    private static DateTime _lastNetworkRetryUtc = DateTime.MinValue;

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

        var exePath = Environment.ProcessPath ?? string.Empty;
        var greysOut = EnsureShellExtension(exePath);
        RegistryHelper.RegisterDesktopMenu(exePath, greysOut);

        AppState.Log(greysOut
            ? "started; desktop menu registered (entry greys out while downloading)"
            : "started; desktop menu registered (no shell extension, entry cannot grey out)");

        var wallpapers = new WallpaperManager();
        StartCommandWatcher(wallpapers);
        StartNetworkWatcher(wallpapers);

        // Fill the queue before the daily task, so a click that arrives early can already be
        // answered instantly instead of waiting for a download.
        wallpapers.EnsurePrefetched();
        wallpapers.SyncMenuState();
        RunDailyTaskInBackground(wallpapers);

        using (var midnight = new MidnightScheduler(() => RunDailyTaskAndReschedule(wallpapers)))
        {
            midnight.Start();
            Shutdown.Wait();
        }

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

    #region Daily task scheduling

    /// <summary>
    /// Runs the daily task on a background thread: neither start-up nor a logon may wait on the
    /// network.
    /// </summary>
    private static void RunDailyTaskInBackground(WallpaperManager wallpapers)
    {
        var thread = new Thread(() => RunDailyTaskAndReschedule(wallpapers))
        {
            IsBackground = true,
            Name = "DailyWallpaperTask",
        };

        thread.Start();
    }

    private static void RunDailyTaskAndReschedule(WallpaperManager wallpapers)
    {
        try
        {
            if (wallpapers.RunDailyTask())
            {
                lock (RetryGate)
                {
                    _retryAttempt = 0;
                    _retryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }

                return;
            }
        }
        catch (Exception ex)
        {
            AppState.Log("daily task failed: " + ex.Message);
        }

        ScheduleRetry(wallpapers);
    }

    /// <summary>Schedules the next attempt with a growing delay: 1, 5, 15 and then 30 minutes.</summary>
    private static void ScheduleRetry(WallpaperManager wallpapers)
    {
        lock (RetryGate)
        {
            var delay = RetryBackoff[Math.Min(_retryAttempt, RetryBackoff.Length - 1)];
            _retryAttempt++;

            _retryTimer ??= new Timer(_ => RunDailyTaskAndReschedule(wallpapers));
            _retryTimer.Change(delay, Timeout.InfiniteTimeSpan);

            AppState.Log($"daily task retry #{_retryAttempt} scheduled in {delay.TotalMinutes:0} minute(s)");
        }
    }

    /// <summary>
    /// Retries the moment the network comes back, which at logon is usually seconds after the first
    /// attempt failed. Both events are watched because either can fire first.
    /// </summary>
    private static void StartNetworkWatcher(WallpaperManager wallpapers)
    {
        var thread = new Thread(() =>
        {
            try
            {
                NetworkChange.NetworkAvailabilityChanged += (_, e) => OnNetworkChanged(wallpapers, e.IsAvailable);
                NetworkChange.NetworkAddressChanged += (_, _) =>
                    OnNetworkChanged(wallpapers, NetworkInterface.GetIsNetworkAvailable());
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
        if (!available) return;

        // Availability changes arrive in bursts (adapter up, address bound, ...): act once.
        lock (RetryGate)
        {
            if (DateTime.UtcNow - _lastNetworkRetryUtc < NetworkEventDebounce) return;
            _lastNetworkRetryUtc = DateTime.UtcNow;
        }

        AppState.Log("network became available; retrying the daily task now");
        RunDailyTaskInBackground(wallpapers);
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
            var (width, height) = DisplayHelper.GetPrimaryResolution();
            Check("primary resolution detected", width > 0 && height > 0, $"{width}x{height}");

            Check("windows version readable", DisplayHelper.IsWindows10OrGreater(), DisplayHelper.OSVersionText);
            Check("lock screen support evaluated", true,
                DisplayHelper.SupportsLockScreenWallpaper() ? "supported" : "unsupported");

            var (reachable, lockImage) = WallpaperManager.QueryLockScreenImage();
            Check("winrt lock screen api reachable", reachable, "Windows.System.UserProfile.LockScreen");
            Check("current lock screen image", true, lockImage ?? "<none>");

            var state = new AppState { CurrentPath = @"C:\temp\a.jpg", PendingPath = @"C:\temp\b.jpg" };
            var restored = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state));
            Check("state.json round trip",
                restored != null && restored.CurrentPath == state.CurrentPath && restored.PendingPath == state.PendingPath);

            Check("desktop menu naming", Strings.MenuNextWallpaper.Length > 0 && Strings.MenuSettings.Length > 0,
                $"{Strings.MenuNextWallpaper} / {Strings.MenuSettings}");

            var exePath = Environment.ProcessPath ?? string.Empty;
            Check("startup state readable", exePath.Length > 0,
                RegistryHelper.IsStartupEnabled(exePath) ? "enabled" : "disabled");

            var due = MidnightScheduler.TimeUntilNextMidnight(DateTime.Now);
            Check("next midnight within 24h", due > TimeSpan.Zero && due <= TimeSpan.FromDays(1), due.ToString());
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

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
