using System.Windows.Controls;
using TransLearn.ViewModels;

namespace TransLearn.Views;

public partial class SettingsView : Page
{
    public SettingsView() => InitializeComponent();

    // PasswordBox doesn't support data binding directly for security reasons
    private void PbDeepL_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.DeepLKey = PbDeepL.Password;
    }

    private void PbAzure_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.AzureKey = PbAzure.Password;
    }

    /// <summary>
    /// [추가] 캡처 저장 폴더 선택. .NET 8 WPF에 내장된 Microsoft.Win32.OpenFolderDialog를 사용해
    /// System.Windows.Forms 참조를 추가하지 않고도(=WPF 컨트롤과 이름 충돌 위험 없이) 구현한다.
    /// </summary>
    private void BrowseCaptureDir_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "캡처 이미지를 저장할 폴더를 선택하세요"
        };

        if (!string.IsNullOrWhiteSpace(vm.CaptureStorageDir) &&
            System.IO.Directory.Exists(vm.CaptureStorageDir))
        {
            dlg.InitialDirectory = vm.CaptureStorageDir;
        }

        if (dlg.ShowDialog() == true)
            vm.CaptureStorageDir = dlg.FolderName;
    }
}
