using System.Windows;
using Mskpy.Presentation.Services;
namespace Mskpy.Windows;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); SizeChanged += (_, _) => UpdateCompact(); DataContextChanged += (_, _) => UpdateCompact(); }
    void UpdateCompact()
    {
        if (DataContext is Mskpy.Presentation.ViewModels.ShellViewModel vm)
            vm.IsCompactSidebar = ActualWidth < 900;
    }
    public ISecretInput SecretInput => new PasswordBoxInput(Password);
}