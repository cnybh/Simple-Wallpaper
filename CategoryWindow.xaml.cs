using System.Windows;
using System.Windows.Controls;

namespace SimpleWallpaper;

/// <summary>
/// Picks the wallpaper subjects. It is modal; "确定" remembers the selection (multi-select is
/// allowed) and asks the background program for a wallpaper of the new selection right away.
/// </summary>
public partial class CategoryWindow : Window
{
    private readonly Dictionary<string, CheckBox> _boxes = new();

    public CategoryWindow()
    {
        InitializeComponent();

        Title = Strings.CategoryWindowTitle;
        HintText.Text = Strings.CategoryHint;
        OkButton.Content = Strings.OkButton;
        CancelButton.Content = Strings.CancelButton;

        // What the user picked before - 风景 on a fresh installation.
        var selected = Categories.Normalize(AppState.Load().Categories);

        foreach (var key in Categories.All)
        {
            var box = new CheckBox
            {
                Content = Strings.CategoryName(key),
                Tag = key,
                Margin = new Thickness(0, 0, 0, 8),
                IsChecked = selected.Contains(key),
            };

            _boxes[key] = box;
            CategoryList.Children.Add(box);
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var picked = Categories.All.Where(key => _boxes[key].IsChecked == true).ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, Strings.CategoryRequired, Strings.CategoryWindowTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AppState.Mutate(state =>
        {
            state.Categories = picked;
            return true;
        });
        AppState.Log("wallpaper categories set to " + string.Join(",", picked));

        // The background program owns the download; the settings window waits for the new picture.
        Program.SendCommand(Program.CommandCategory);
        DialogResult = true;
    }
}
