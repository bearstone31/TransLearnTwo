using System.Windows.Controls;
using TransLearn.ViewModels;

namespace TransLearn.Views;

public partial class MemoView : Page
{
    public MemoView()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (DataContext is MemoViewModel vm)
                await vm.LoadMemosCommand.ExecuteAsync(null);
        };
    }
}