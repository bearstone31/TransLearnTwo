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
}
