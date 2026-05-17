using System.Windows;
using System.Windows.Controls;
using TransLearn.Views;

namespace TransLearn;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pageCache = new();

    public MainWindow()
    {
        InitializeComponent();
        Navigate("OCR");
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            Navigate(tag);
    }

    private void Navigate(string tag)
    {
        // Update sidebar active state
        foreach (var child in FindVisualChildren<Button>(this))
        {
            if (child.Tag is string t && new[] { "OCR", "Sound", "Learn", "History", "Memo", "Settings" }.Contains(t))
            {
                child.Style = (t == tag)
                    ? (Style)FindResource("SidebarButtonActiveStyle")
                    : (Style)FindResource("SidebarButtonStyle");
            }
        }

        // Cache pages
        if (!_pageCache.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "OCR" => new OcrView(),
                "Sound" => new SoundView(),
                "Learn" => new LearningView(),
                "History" => new HistoryView(),
                "Memo" => new MemoView(),
                "Settings" => new SettingsView(),
                _ => new OcrView()
            };
            _pageCache[tag] = page;
        }

        MainFrame.Navigate(page);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }
}
