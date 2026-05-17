using System.Windows.Controls;
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
}
