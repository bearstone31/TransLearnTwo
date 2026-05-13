using System.Drawing;
using System.Windows;
using System.Windows.Input;

namespace TransLearn.Views;

public partial class OcrRegionWindow : Window
{
    public event Action<Rectangle>? RegionSelected;

    public OcrRegionWindow() => InitializeComponent();

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        // Convert WPF window position to screen pixels (DPI-aware)
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var rect = new Rectangle(
            (int)(Left * dpiX),
            (int)(Top  * dpiY),
            (int)(ActualWidth  * dpiX),
            (int)(ActualHeight * dpiY));

        RegionSelected?.Invoke(rect);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
