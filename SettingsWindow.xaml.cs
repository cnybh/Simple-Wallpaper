using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// Aliased: System.Windows.Shapes also has a Path, which would clash with System.IO.Path below.
using Shapes = System.Windows.Shapes;

namespace SimpleWallpaper;

/// <summary>
/// The settings window. It runs in its own process, so closing it never stops the background
/// program, and the window cannot be maximised or minimised (ResizeMode="NoResize").
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>How long the window waits for the picture a category change asked for.</summary>
    private static readonly TimeSpan CategoryRefreshTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the like button stays greyed out at the least. Storing the choice is a state.json
    /// write that is over in milliseconds, so without a floor the feedback could not be seen at all.
    /// </summary>
    private static readonly TimeSpan LikeFeedbackTime = TimeSpan.FromMilliseconds(350);

    /// <summary>How long the little cycle box waits for the program to confirm the new cycle.</summary>
    private static readonly TimeSpan CycleConfirmTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long "switched to ..." stays on screen before the box goes away.</summary>
    private static readonly TimeSpan CycleConfirmHold = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan NoticePollInterval = TimeSpan.FromMilliseconds(200);

    private readonly string _exePath = Environment.ProcessPath ?? string.Empty;
    private readonly bool _lockScreenSupported = DisplayHelper.SupportsLockScreenWallpaper();

    /// <summary>Keeps the "current wallpaper" box and the like button in step with the background program.</summary>
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Filling the boxes must not be mistaken for a user action.
    private bool _loading = true;
    private bool _applyingMode;

    private ComboBoxItem? _modeItem;
    private string _currentPath = string.Empty;
    private string _currentUrl = string.Empty;

    /// <summary>False until the first refresh ran, so an empty state still draws its placeholder.</summary>
    private bool _currentShown;

    /// <summary>True while the like is being stored: the button stays greyed out until it is done.</summary>
    private bool _storingLike;

    /// <summary>True from a click on "next wallpaper" until its new picture is queued.</summary>
    private bool _switching;

    /// <summary>The box reporting a cycle change, and a token that retires its waiting loop.</summary>
    private SwitchNoticeWindow? _notice;
    private long _noticeToken;

    // A category change makes the background program download: the box is refreshed when it lands.
    private bool _awaitingCategoryRefresh;
    private DateTime _categoryRefreshStarted;
    private string _categoryRefreshFrom = string.Empty;

    public SettingsWindow()
    {
        InitializeComponent();

        Title = Strings.WindowTitle;
        StartupCheck.Content = Strings.StartupCheckBox;
        AboutButton.Content = Strings.AboutButton;
        ReleaseButton.Content = Strings.ReleasePageButton;
        RestoreButton.Content = Strings.RestoreButton;
        CycleGroup.Header = Strings.CycleLabel;
        NextButton.Content = Strings.NextButton;
        CategoryButton.Content = Strings.CategoryButton;
        CurrentGroup.Header = Strings.CurrentGroupTitle;
        SourceLabelRun.Text = Strings.SourceLabel;

        LockScreenCheck.Content = Strings.LockScreenCheckBox
            + (_lockScreenSupported ? string.Empty : Strings.NotSupportedSuffix);
        LockScreenCheck.IsEnabled = _lockScreenSupported;

        BuildModeBox();
        RefreshFromSystem();
        _loading = false;

        RefreshCurrentWallpaper();

        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _noticeToken++;   // retires the waiting loop: it must not touch the window being closed
            CloseNotice();
        };
    }

    #region Switch cycle

    /// <summary>Black dashed line between the interval cycles and the two clock cycles.</summary>
    private static readonly Brush CycleDivider = CreateCycleDivider();

    private static Brush CreateCycleDivider()
    {
        // 3 pixels drawn, 3 empty, one pixel high, tiled across the whole width: a Separator cannot
        // be dashed, and its theme line is invisible on the greyed-out row anyway.
        var brush = new DrawingBrush(
            new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 3, 1))))
        {
            TileMode = TileMode.Tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 6, 1),
            Stretch = Stretch.Fill,
        };

        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Fills the cycle list. Each group is introduced by a break line, which is an entry of its own
    /// that cannot be picked: it is greyed out with a transparent background, and a selection
    /// landing on it is put back (see <see cref="ModeBox_SelectionChanged"/>).
    /// </summary>
    private void BuildModeBox()
    {
        foreach (var mode in SwitchSchedule.Modes)
        {
            if (SwitchSchedule.StartsSection(mode))
            {
                ModeBox.Items.Add(new ComboBoxItem
                {
                    Content = new Shapes.Rectangle
                    {
                        Height = 1,
                        MinWidth = 200,
                        Margin = new Thickness(3, 6, 3, 6),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Fill = CycleDivider,
                    },
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent,
                    IsEnabled = false,
                    Focusable = false,
                });
            }

            ModeBox.Items.Add(new ComboBoxItem { Content = Strings.SwitchModeName(mode), Tag = mode });
        }
    }

    private async void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applyingMode) return;

        if (ModeBox.SelectedItem is not ComboBoxItem item || item.Tag is not string mode)
        {
            // The divider was reached after all (keyboard, mostly): restore the cycle in effect.
            _applyingMode = true;
            ModeBox.SelectedItem = _modeItem;
            _applyingMode = false;
            return;
        }

        _modeItem = item;

        // The handshake number has to survive another process writing the file at the same moment,
        // so it is counted up on the current file rather than on a snapshot.
        var request = AppState.Mutate(state =>
        {
            state.SwitchMode = mode;
            state.ModeRequestSeq++;   // the number the program copies back once the new mode is saved
            return state.ModeRequestSeq;
        });
        AppState.Log("switch cycle set to " + mode);

        // Takes effect at once: the background program recomputes the next switch from now.
        Program.SendCommand(Program.CommandMode);

        await ConfirmCycleSwitchAsync(mode, request);
    }

    /// <summary>
    /// Reports the cycle change over the settings window: "switching to X" while the program is
    /// working, then "switched to X" for a second once it has confirmed, and the countdown is
    /// refreshed so it counts to the new cycle. A second pick takes the box over instead of
    /// stacking another one, and a change nobody confirms is closed without being called a success.
    /// </summary>
    private async Task ConfirmCycleSwitchAsync(string mode, long request)
    {
        var token = ++_noticeToken;
        var cycle = Strings.SwitchModeName(mode);

        try
        {
            _notice?.Close();
            var notice = new SwitchNoticeWindow(Strings.SwitchingToCycle(cycle)) { Owner = this };
            _notice = notice;
            notice.Show();

            var started = DateTime.Now;
            while (AppState.Load().ModeAppliedSeq < request && DateTime.Now - started < CycleConfirmTimeout)
            {
                await Task.Delay(NoticePollInterval);
                if (token != _noticeToken) return;   // a newer pick owns the box now
            }

            if (token != _noticeToken) return;

            if (AppState.Load().ModeAppliedSeq < request)
            {
                AppState.Log($"the cycle change to {mode} was not confirmed within "
                    + $"{CycleConfirmTimeout.TotalSeconds:0} seconds");
                CloseNotice();
                return;
            }

            notice.SetText(Strings.SwitchedToCycle(cycle));
            await Task.Delay(CycleConfirmHold);
            if (token != _noticeToken) return;

            CloseNotice();
            RefreshCurrentWallpaper();   // the countdown now counts towards the new cycle
        }
        catch (Exception ex)
        {
            AppState.Log("reporting the cycle change failed: " + ex.Message);
            CloseNotice();
        }
    }

    private void CloseNotice()
    {
        try
        {
            _notice?.Close();
        }
        catch (Exception ex)
        {
            AppState.Log("closing the cycle notice failed: " + ex.Message);
        }

        _notice = null;
    }

    #endregion

    #region Current wallpaper

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        RefreshCurrentWallpaper();

        if (!_awaitingCategoryRefresh) return;

        if (_currentPath.Length > 0
            && !string.Equals(_currentPath, _categoryRefreshFrom, StringComparison.OrdinalIgnoreCase))
        {
            _awaitingCategoryRefresh = false;
            SetStatus(string.Empty);
        }
        else if (DateTime.Now - _categoryRefreshStarted > CategoryRefreshTimeout)
        {
            _awaitingCategoryRefresh = false;
            SetStatus(Strings.FetchTimeout);
        }
    }

    /// <summary>Sets the status line and hides it while there is nothing to say.</summary>
    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What stands where the countdown does: the reason the cycle is on hold, or the time left until
    /// it switches. "No cycling" is read straight from the cycle, because the countdown has to read
    /// "fixed wallpaper" the moment the user picks it - the program writes its reason a moment later.
    /// </summary>
    private void RefreshNextSwitchText(AppState state)
    {
        if (state.PauseReason == SwitchSchedule.BatteryReason)
        {
            NextSwitchText.Text = Strings.BatteryPaused;
        }
        else if (!SwitchSchedule.IsLooping(state.SwitchMode))
        {
            NextSwitchText.Text = Strings.FixedWallpaper;
        }
        else if (state.PauseReason == SwitchSchedule.NetworkReason)
        {
            // The check is due the moment the countdown reaches zero, and it needs a line of its own:
            // a countdown frozen at 0分0秒 would be the one thing on screen that never moves.
            NextSwitchText.Text = state.NextSwitchAt <= DateTime.Now
                ? Strings.CheckingNetwork
                : Strings.OfflineLabel + Strings.Countdown(state.NextSwitchAt - DateTime.Now);
        }
        else if (state.NextSwitchAt > DateTime.Now)
        {
            // A moment in the future is normally the next cycle. While the retry flag is up it is the
            // retry of a failed switch instead, and saying so is what stops a countdown that suddenly
            // reads one minute again from looking like a bug.
            NextSwitchText.Text = File.Exists(WallpaperManager.RetryFlagPath)
                ? Strings.RetryingSwitch + Strings.Countdown(state.NextSwitchAt - DateTime.Now)
                : Strings.NextSwitchLabel + Strings.Countdown(state.NextSwitchAt - DateTime.Now);
        }
        else
        {
            // The moment has passed: either the switch is running right now, or it is a poll away.
            NextSwitchText.Text = Strings.NextSwitchLabel
                + (File.Exists(WallpaperManager.SwitchingFlagPath)
                    ? Strings.SwitchingNow
                    : Strings.NextSwitchDueNow);
        }
    }

    /// <summary>Shows the picture that is on screen, and whether it is one of the liked ones.</summary>
    private void RefreshCurrentWallpaper()
    {
        var state = AppState.Load();

        if (!_currentShown || !string.Equals(state.CurrentPath, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentShown = true;
            _currentPath = state.CurrentPath;
            _currentUrl = state.CurrentUrl;

            ShowThumbnail(state.CurrentPath);
            ShowSource(state.CurrentSource);
        }

        UpdateLikeButton(state);
        RefreshNextSwitchText(state);
        RefreshModeBox(state);
        RefreshNextButton(state);
    }

    /// <summary>Keeps the cycle list showing what is in effect, whoever changed it.</summary>
    private void RefreshModeBox(AppState state)
    {
        var mode = SwitchSchedule.Normalize(state.SwitchMode);
        if ((_modeItem?.Tag as string) == mode) return;

        _modeItem = ModeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (item.Tag as string) == mode);
        if (_modeItem == null) return;

        _applyingMode = true;
        ModeBox.SelectedItem = _modeItem;
        _applyingMode = false;
    }

    /// <summary>
    /// Draws the current picture as a thumbnail. It is read into memory at once and the file is left
    /// closed - the picture is deleted as soon as the next one is applied, and a locked file could
    /// not be removed.
    /// </summary>
    private void ShowThumbnail(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            ThumbnailImage.Source = null;
            NoWallpaperText.Text = Strings.NoWallpaperYet;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // reads the file now, releases it at EndInit
            image.DecodePixelWidth = 640;                   // a thumbnail never needs the full picture
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();

            ThumbnailImage.Source = image;
            NoWallpaperText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            AppState.Log("loading the wallpaper thumbnail failed: " + ex.Message);
            ThumbnailImage.Source = null;
            NoWallpaperText.Text = Strings.NoWallpaperYet;
        }
    }

    /// <summary>
    /// Shows where the picture came from. An answer from the service becomes a link that opens; when
    /// there is nothing to link to the line stays plain text, so nothing pretends to be a link.
    /// </summary>
    private void ShowSource(string source)
    {
        SourceLink.Inlines.Clear();
        SourcePlainRun.Text = string.Empty;

        if (_currentUrl.Length > 0 && source.Length > 0)
        {
            SourceLink.Inlines.Add(new Run(source));
        }
        else
        {
            SourcePlainRun.Text = source.Length > 0 ? source : Strings.UnknownSource;
        }
    }

    private void SourceLink_Click(object sender, RoutedEventArgs e)
    {
        if (_currentUrl.Length == 0) return;

        try
        {
            Process.Start(new ProcessStartInfo(_currentUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppState.Log("opening the wallpaper source page failed: " + ex.Message);
        }
    }

    private void UpdateLikeButton(AppState state)
    {
        // While the like is being stored the button keeps the greyed-out look it was given on click.
        if (_storingLike) return;

        var liked = _currentPath.Length > 0
            && state.Likes.Any(item => string.Equals(item.Path, _currentPath, StringComparison.OrdinalIgnoreCase));

        LikeButton.Content = liked ? Strings.UnlikeButton : Strings.LikeButton;
        LikeButton.IsEnabled = _currentPath.Length > 0 && File.Exists(_currentPath);
    }

    /// <summary>
    /// The next wallpaper button follows the desktop menu entry: it is clickable exactly while a
    /// picture is waiting in the queue, which is what the shell extension reads the ready flag for.
    /// A click greys it out itself, so the button never waits up to a second before reacting.
    /// </summary>
    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switching || !SwitchSchedule.IsLooping(AppState.Load().SwitchMode)) return;
        if (!File.Exists(AppState.ReadyFlagPath)) return;

        _switching = true;
        NextButton.IsEnabled = false;

        Program.SendCommand(Program.CommandNext);

        // The flag goes away with the click and comes back when the new picture is queued. The wait
        // is only what keeps the button grey should that feedback ever fail to arrive.
        var started = DateTime.Now;
        while (File.Exists(AppState.ReadyFlagPath) && DateTime.Now - started < CategoryRefreshTimeout)
        {
            await Task.Delay(250);
        }

        _switching = false;
        RefreshNextButton(AppState.Load());
    }

    /// <summary>Disabled only by the user's own "no cycling": a flat battery or a dead network ends.</summary>
    private void RefreshNextButton(AppState state)
    {
        if (_switching) return;

        NextButton.IsEnabled = SwitchSchedule.IsLooping(state.SwitchMode)
            && File.Exists(AppState.ReadyFlagPath);
    }

    /// <summary>
    /// Marks the picture on screen as liked - its file is kept from then on - or takes that mark back,
    /// in which case the file goes away with the next switch. The button is greyed out and unclickable
    /// while the choice is stored, so a second click can never overtake the first one.
    /// </summary>
    private async void LikeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storingLike || _currentPath.Length == 0 || !File.Exists(_currentPath)) return;

        var path = _currentPath;
        _storingLike = true;
        LikeButton.IsEnabled = false;

        try
        {
            await StoreLikeAsync(path);
        }
        catch (Exception ex)
        {
            AppState.Log("storing the liked wallpaper failed: " + ex.Message);
        }
        finally
        {
            _storingLike = false;
            UpdateLikeButton(AppState.Load());   // back to normal, showing the other action
        }
    }

    /// <summary>
    /// Writes the like on a worker thread, so the greyed-out button is on screen while it happens.
    /// </summary>
    private static async Task StoreLikeAsync(string path)
    {
        var started = DateTime.UtcNow;
        await Task.Run(() => ToggleLike(path));

        var remaining = LikeFeedbackTime - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
    }

    /// <summary>Adds or removes the like and returns true when the picture is liked afterwards.</summary>
    private static bool ToggleLike(string path)
    {
        // Read, change and write as one step: the background program writes this file too, and a like
        // must never be stored on top of a switch it did not see.
        var liked = AppState.Mutate(state =>
        {
            var index = state.Likes.FindIndex(item =>
                string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                state.Likes.RemoveAt(index);
                return false;
            }

            state.Likes.Add(new LikedWallpaper
            {
                Path = path,
                Title = state.CurrentTitle,
                Source = state.CurrentSource,
                Url = state.CurrentUrl,
            });
            return true;
        });

        AppState.Log(liked
            ? $"likes {Path.GetFileName(path)}; the file is kept from now on"
            : $"no longer likes {Path.GetFileName(path)}; the file goes at the next switch");
        return liked;
    }

    #endregion

    #region System settings

    /// <summary>Reads the real state, so the boxes always show what Windows actually has.</summary>
    private void RefreshFromSystem()
    {
        var state = AppState.Load();

        StartupCheck.IsChecked = RegistryHelper.IsStartupEnabled(_exePath);
        LockScreenCheck.IsChecked = _lockScreenSupported && state.LockScreenEnabled;

        // _loading is still true here: this restores the box without passing for a user action.
        RefreshModeBox(state);
    }

    private void StartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = StartupCheck.IsChecked == true;
        var changed = wanted ? RegistryHelper.EnableStartup(_exePath) : RegistryHelper.DisableStartup();

        if (!changed)
        {
            MessageBox.Show(this, Strings.StartupFailed, Strings.AboutTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Always re-read the system instead of trusting the click.
        StartupCheck.IsChecked = RegistryHelper.IsStartupEnabled(_exePath);
    }

    private void LockScreenCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (!_lockScreenSupported)
        {
            LockScreenCheck.IsChecked = false;
            return;
        }

        var enabled = LockScreenCheck.IsChecked == true;

        // The picture in use comes from the file as it is now: the program may have switched the
        // wallpaper since this window drew its last frame.
        var currentPath = AppState.Mutate(state =>
        {
            state.LockScreenEnabled = enabled;
            return state.CurrentPath;
        });

        if (!enabled)
        {
            WallpaperManager.RestoreDefaultLockScreen();
        }
        else if (currentPath.Length > 0 && File.Exists(currentPath))
        {
            WallpaperManager.ApplyLockScreen(currentPath);
        }
    }

    #endregion

    #region Wallpaper categories

    private void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CategoryWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;   // cancelled: nothing changed

        // The background program is downloading the picture for the new selection; the one-second
        // timer above notices when it has landed.
        _categoryRefreshFrom = AppState.Load().CurrentPath;
        _categoryRefreshStarted = DateTime.Now;
        _awaitingCategoryRefresh = true;
        SetStatus(Strings.FetchingNewWallpaper);
    }

    #endregion

    #region About / release / restore

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        // A window of its own: it centres on this one, and a MessageBox would centre on the screen.
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void ReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/cnybh/Simple-Wallpaper") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppState.Log("opening the release page failed: " + ex.Message);
        }

        Close();
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        WallpaperManager.RestoreDefaults();
        Program.RequestExit();   // stop the background program as well

        MessageBox.Show(this, Strings.RestoredMessage, Strings.AboutTitle,
            MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    #endregion
}
