using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TransLearn.ViewModels;

namespace TransLearn.Views;

public partial class LearningView : Page
{
    public LearningView() => InitializeComponent();

    // ── 탭 전환 ──────────────────────────────────────────────────────────
    private void Tab_WordList(object sender, RoutedEventArgs e)
    {
        WordListPanel.Visibility = Visibility.Visible;
        QuizPanel.Visibility = Visibility.Collapsed;
    }

    private void Tab_Quiz(object sender, RoutedEventArgs e)
    {
        // 퀴즈가 없으면 탭 전환하지 않음 (NullReferenceException 방지)
        if (DataContext is LearningViewModel vm && vm.Quiz.Count == 0)
        {
            WordListPanel.Visibility = Visibility.Visible;
            QuizPanel.Visibility = Visibility.Collapsed;
            return;
        }
        WordListPanel.Visibility = Visibility.Collapsed;
        QuizPanel.Visibility = Visibility.Visible;
    }

    // ── 난이도 버튼 클릭 ─────────────────────────────────────────────────
    private void DiffBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clicked) return;
        if (!int.TryParse(clicked.Tag?.ToString(), out var level)) return;

        // ViewModel에 난이도 전달
        if (DataContext is LearningViewModel vm)
            vm.SelectedDifficulty = level;

        // 버튼 색상 업데이트
        var selectedBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x4A));
        var selectedFg = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        var selectedBorder = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        var normalBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        var normalFg = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        var normalBorder = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        foreach (var btn in new[] { DiffBtn1, DiffBtn2, DiffBtn3, DiffBtn4 })
        {
            bool isSelected = btn.Tag?.ToString() == level.ToString();
            btn.Background = isSelected ? selectedBg : normalBg;
            btn.Foreground = isSelected ? selectedFg : normalFg;
            btn.BorderBrush = isSelected ? selectedBorder : normalBorder;
        }
    }
}
