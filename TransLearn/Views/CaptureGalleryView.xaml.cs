// ============================================================
// CaptureGalleryView.xaml.cs [추가]
// 역할 : 갤러리 카드 클릭 시 원본 이미지를 기본 뷰어로 연다.
//        파일이 이미 삭제/이동된 경우 조용히 무시하지 않고
//        사용자에게 안내한 뒤 목록을 새로고침해 화면과 실제 상태를 맞춘다.
// ============================================================
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TransLearn.Models;
using TransLearn.ViewModels;

namespace TransLearn.Views;

public partial class CaptureGalleryView : UserControl
{
    public CaptureGalleryView()
    {
        InitializeComponent();

        // [추가] 이 컨트롤은 HistoryView 안에서 Visibility로만 탭 전환되고
        // 한 번 생성되면 재사용되기 때문에, 생성 시점에 한 번만 로드하면
        // 그 이후에 저장된 캡처가 탭을 다시 열어도 보이지 않는다.
        // 탭이 다시 "보이게" 될 때마다 최신 목록을 다시 불러온다.
        IsVisibleChanged += async (_, e) =>
        {
            if (e.NewValue is true && DataContext is CaptureGalleryViewModel vm)
                await vm.LoadCommand.ExecuteAsync(null);
        };
    }

    private async void GalleryCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TranslationRecord rec) return;

        if (string.IsNullOrWhiteSpace(rec.ImagePath) || !File.Exists(rec.ImagePath))
        {
            // 파일이 삭제되었거나 다른 곳으로 이동된 경우: 조용히 무시하지 않고 알려준 뒤
            // 목록을 다시 불러와 DB의 누락 표시를 화면에 반영한다 (DatabaseService가 자동 정리).
            MessageBox.Show(
                "이미지 파일을 찾을 수 없습니다. 삭제되었거나 다른 위치로 이동된 것 같습니다.",
                "TransLearn", MessageBoxButton.OK, MessageBoxImage.Information);

            if (DataContext is CaptureGalleryViewModel vm)
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
