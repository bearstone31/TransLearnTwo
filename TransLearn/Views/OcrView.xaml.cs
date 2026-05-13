using System.Windows.Controls;
using TransLearn.ViewModels;

namespace TransLearn.Views;

public partial class OcrView : Page
{
    public OcrView()
    {
        InitializeComponent();
        // 페이지가 화면에 표시될 때마다 창 목록 자동 갱신
        Loaded += (_, _) =>
        {
            if (DataContext is OcrViewModel vm)
                vm.RefreshWindows();
        };
    }
}
