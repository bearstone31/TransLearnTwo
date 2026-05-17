using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TransLearn.Views;

public partial class OverlayWindow : Window
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_SIZE = 0xF000;
    private const int WMSZ_LEFT = 1;
    private const int WMSZ_RIGHT = 2;
    private const int WMSZ_TOP = 3;
    private const int WMSZ_TOPLEFT = 4;
    private const int WMSZ_TOPRIGHT = 5;
    private const int WMSZ_BOTTOM = 6;
    private const int WMSZ_BOTTOMLEFT = 7;
    private const int WMSZ_BOTTOMRIGHT = 8;
    private const double EdgeSize = 8;

    private Color _bgColor = Color.FromArgb(0xBB, 0, 0, 0);
    private string _currentTranslation = "";
    private bool _showPrevTranslation = false; // 기본 OFF

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var screen = SystemParameters.WorkArea;
            Left = (screen.Width - Width) / 2;
            Top = screen.Height - Height - 60;
        };
    }

    // ── 번역 업데이트 ────────────────────────────────────────────────────
    public void UpdateTranslation(string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text == _currentTranslation) return;

            // 이전 번역 표시 (ON일 때만)
            if (_showPrevTranslation && !string.IsNullOrWhiteSpace(_currentTranslation))
            {
                PrevTranslationText.Text = _currentTranslation;
                PrevTranslationText.Visibility = Visibility.Visible;
            }
            else
            {
                PrevTranslationText.Visibility = Visibility.Collapsed;
            }

            _currentTranslation = text;
            TranslationText.Text = text;
        });
    }

    // ── 이전 번역 토글 ───────────────────────────────────────────────────
    private void PrevToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _showPrevTranslation = !_showPrevTranslation;
        PrevToggleBtn.Foreground = _showPrevTranslation
            ? new SolidColorBrush(Colors.Cyan)
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));
        PrevToggleBtn.ToolTip = _showPrevTranslation
            ? "이전 번역 표시 ON (클릭해서 끄기)"
            : "이전 번역 표시 OFF (클릭해서 켜기)";

        // OFF로 끄면 이전 번역 즉시 숨김
        if (!_showPrevTranslation)
            PrevTranslationText.Visibility = Visibility.Collapsed;
    }

    // ── 마우스 이벤트 ────────────────────────────────────────────────────
    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Cursor = GetResizeCursor(e.GetPosition(this));
        FadePanel(true);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        FadePanel(false);
        Cursor = Cursors.Arrow;
    }

    private void FadePanel(bool show)
    {
        ControlPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1.0 : 0.0,
                TimeSpan.FromMilliseconds(show ? 150 : 500)));
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        var pos = e.GetPosition(this);
        int dir = GetResizeDirection(pos);
        if (dir == 0) { DragMove(); return; }
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_SIZE + dir), IntPtr.Zero);
    }

    private Cursor GetResizeCursor(Point p)
    {
        bool l = p.X <= EdgeSize, r = p.X >= ActualWidth - EdgeSize;
        bool t = p.Y <= EdgeSize, b = p.Y >= ActualHeight - EdgeSize;
        if (t && l) return Cursors.SizeNWSE;
        if (t && r) return Cursors.SizeNESW;
        if (b && l) return Cursors.SizeNESW;
        if (b && r) return Cursors.SizeNWSE;
        if (t || b) return Cursors.SizeNS;
        if (l || r) return Cursors.SizeWE;
        return Cursors.SizeAll;
    }

    private int GetResizeDirection(Point p)
    {
        bool l = p.X <= EdgeSize, r = p.X >= ActualWidth - EdgeSize;
        bool t = p.Y <= EdgeSize, b = p.Y >= ActualHeight - EdgeSize;
        if (t && l) return WMSZ_TOPLEFT;
        if (t && r) return WMSZ_TOPRIGHT;
        if (b && l) return WMSZ_BOTTOMLEFT;
        if (b && r) return WMSZ_BOTTOMRIGHT;
        if (t) return WMSZ_TOP;
        if (b) return WMSZ_BOTTOM;
        if (l) return WMSZ_LEFT;
        if (r) return WMSZ_RIGHT;
        return 0;
    }

    // ── 폰트 크기 ────────────────────────────────────────────────────────
    private void FontSizeUp_Click(object sender, RoutedEventArgs e)
    {
        if (TranslationText.FontSize < 48)
        {
            TranslationText.FontSize += 2;
            PrevTranslationText.FontSize = TranslationText.FontSize - 6;
        }
    }

    private void FontSizeDown_Click(object sender, RoutedEventArgs e)
    {
        if (TranslationText.FontSize > 10)
        {
            TranslationText.FontSize -= 2;
            PrevTranslationText.FontSize = Math.Max(8, TranslationText.FontSize - 6);
        }
    }

    // ── 컬러 피커 ────────────────────────────────────────────────────────
    private void ColorPickerBtn_Click(object sender, RoutedEventArgs e)
        => ColorPopup.IsOpen = !ColorPopup.IsOpen;

    private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle rect && rect.Tag is string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                _bgColor = Color.FromArgb(_bgColor.A, c.R, c.G, c.B);
                UpdateBg();
                ColorPopup.IsOpen = false;
            }
            catch { }
        }
    }

    // ── 투명도 슬라이더 ──────────────────────────────────────────────────
    private void OpacitySlider_ValueChanged(object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (MainBorder == null) return;
        _bgColor = Color.FromArgb((byte)e.NewValue, _bgColor.R, _bgColor.G, _bgColor.B);
        UpdateBg();
    }

    private void UpdateBg()
        => MainBorder.Background = new SolidColorBrush(_bgColor);

    // ── 핀 토글 ──────────────────────────────────────────────────────────
    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinBtn.Foreground = Topmost
            ? new SolidColorBrush(Colors.Cyan)
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));
    }

    // ── 닫기 ─────────────────────────────────────────────────────────────
    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
        base.OnKeyDown(e);
    }
}
