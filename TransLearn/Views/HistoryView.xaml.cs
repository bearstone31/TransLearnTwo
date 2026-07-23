using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TransLearn.Models;
using TransLearn.ViewModels;

namespace TransLearn.Views;

public partial class HistoryView : Page
{
    public HistoryView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is HistoryViewModel vm)
                await vm.LoadCommand.ExecuteAsync(null);
        };
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

    }

    /// <summary>[추가] 기록 목록의 캡처 썸네일 클릭 시 원본 이미지를 기본 뷰어로 연다.</summary>
    private async void Thumbnail_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TranslationRecord rec) return;

        if (string.IsNullOrWhiteSpace(rec.ImagePath) || !File.Exists(rec.ImagePath))
        {
            // 파일이 삭제/이동된 경우 조용히 무시하지 않고 알려준 뒤,
            // 목록을 새로고침해 DB에 정리된 상태(썸네일 사라짐)를 화면에 반영한다.
            MessageBox.Show(
                "이미지 파일을 찾을 수 없습니다. 삭제되었거나 다른 위치로 이동된 것 같습니다.",
                "TransLearn", MessageBoxButton.OK, MessageBoxImage.Information);

            if (DataContext is HistoryViewModel vm)
                await vm.LoadCommand.ExecuteAsync(null);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(rec.ImagePath) { UseShellExecute = true });
        }
        catch
        {
            // 뷰어 실행 실패는 조용히 무시 (연결된 프로그램이 없을 수 있음)
        }
    }
}
