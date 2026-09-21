using System.Windows;

namespace SimpleWallpaper;

/// <summary>
/// The untitled box that reports a cycle change. It centres on the settings window (CenterOwner)
/// rather than the screen, and it is not modal: the user may pick another cycle while it is up.
/// </summary>
public partial class SwitchNoticeWindow : Window
{
    public SwitchNoticeWindow(string text)
    {
        InitializeComponent();
        NoticeText.Text = text;
    }

    /// <summary>Swaps the text once the change is confirmed, without moving the box.</summary>
    public void SetText(string text) => NoticeText.Text = text;
}
