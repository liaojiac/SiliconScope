using System.Windows.Controls;
using SiliconScope.UI.ViewModels;

namespace SiliconScope.UI.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void ListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel mvm && mvm.History.OpenCmd.CanExecute(null))
            mvm.History.OpenCmd.Execute(null);
    }
}
