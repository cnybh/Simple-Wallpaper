using System.Windows;
using System.Windows.Media.Imaging;

namespace SimpleWallpaper;

/// <summary>
/// The about box. It is a window of its own instead of a MessageBox so that it sits in the middle of
/// the window it was opened from (CenterOwner) rather than in the middle of the screen.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        Title = Strings.AboutTitle;
        NameText.Text = Strings.AboutText;
        VersionText.Text = Strings.VersionText;
        HomePageText.Text = Strings.AboutHomePage;
        OkButton.Content = Strings.OkButton;

        ShowLogo();
    }

    /// <summary>
    /// Draws the icon the executable carries. The .ico holds six frames and the decoder hands them
    /// over largest first, so the box is filled from the 256 pixel frame instead of being enlarged
    /// from a 16 pixel one. An icon that cannot be read is taken out of the layout entirely: an
    /// empty 64 pixel square above the name is the blank the box used to show.
    /// </summary>
    private void ShowLogo()
    {
        try
        {
            var stream = Application
                .GetResourceStream(new Uri("pack://application:,,,/SimpleWallpaper;component/logo2.ico"))?.Stream;
            if (stream == null)
            {
                AppState.Log("the about box logo is not packed into the assembly");
                LogoImage.Visibility = Visibility.Collapsed;
                return;
            }

            using (stream)
            {
                var decoder = new IconBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                LogoImage.Source = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            AppState.Log("loading the about box logo failed: " + ex.Message);
            LogoImage.Visibility = Visibility.Collapsed;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
