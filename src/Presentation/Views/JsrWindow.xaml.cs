using System.Windows;
using Mskpy.Presentation.ViewModels;

namespace Mskpy.Presentation.Views;

public partial class JsrWindow : Window
{
    public JsrWindow(JsrViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, args) =>
        {
            if (viewModel.IsDirty && MessageBox.Show(this, "Close without saving your latest changes?", "Unsaved JSR draft", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                args.Cancel = true;
        };
        Closed += (_, _) => viewModel.Dispose();
    }
}
