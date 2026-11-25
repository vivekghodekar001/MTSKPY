using System.Windows;
using System.Windows.Controls;
using Mskpy.Presentation.Services;
namespace Mskpy.Windows;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateCompact();
        DataContextChanged += (_, _) => UpdateCompact();
        // M5: the statewide list is paged; near the end of the scroll ask the
        // view model for the next page instead of loading 45k rows up front.
        ApplicationList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnListScrolled));
    }

    async void OnListScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is Mskpy.Presentation.ViewModels.ShellViewModel vm
            && e.ExtentHeight - e.VerticalOffset - e.ViewportHeight < 600)
            await vm.LoadNextAllPageAsync();
    }

    void UpdateCompact()
    {
        if (DataContext is Mskpy.Presentation.ViewModels.ShellViewModel vm)
            vm.IsCompactSidebar = ActualWidth < 900;
    }
    public ISecretInput SecretInput => new PasswordBoxInput(Password);
}
