using System.Diagnostics;
using System.Windows;

namespace SimpleWallpaper;

/// <summary>
/// The settings window. It runs in its own process, so closing it never stops the background
/// program, and the window cannot be maximised or minimised (ResizeMode="NoResize").
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly string _exePath = Environment.ProcessPath ?? string.Empty;
    private readonly bool _lockScreenSupported = DisplayHelper.SupportsLockScreenWallpaper();

    // Filling the boxes must not be mistaken for a user action.
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        Title = Strings.WindowTitle;
        StartupCheck.Content = Strings.StartupCheckBox;
        AboutButton.Content = Strings.AboutButton;
        ReleaseButton.Content = Strings.ReleasePageButton;
        RestoreButton.Content = Strings.RestoreButton;

        LockScreenCheck.Content = Strings.LockScreenCheckBox
            + (_lockScreenSupported ? string.Empty : Strings.NotSupportedSuffix);
        LockScreenCheck.IsEnabled = _lockScreenSupported;

        RefreshFromSystem();
        _loading = false;
    }

    /// <summary>Reads the real state, so the boxes always show what Windows actually has.</summary>
    private void RefreshFromSystem()
    {
        var state = AppState.Load();

        StartupCheck.IsChecked = RegistryHelper.IsStartupEnabled(_exePath);
        LockScreenCheck.IsChecked = _lockScreenSupported && state.LockScreenEnabled;
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
        var state = AppState.Load();
        state.LockScreenEnabled = enabled;
        state.Save();

        if (!enabled)
        {
            WallpaperManager.RestoreDefaultLockScreen();
        }
        else if (state.CurrentPath.Length > 0 && File.Exists(state.CurrentPath))
        {
            WallpaperManager.ApplyLockScreen(state.CurrentPath);
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, Strings.AboutText, Strings.AboutTitle,
            MessageBoxButton.OK, MessageBoxImage.Information);
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
}
