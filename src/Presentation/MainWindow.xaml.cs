using System.Windows;
using Mskpy.Presentation.Services;
namespace Mskpy.Windows;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }
    public ISecretInput SecretInput => new PasswordBoxInput(Password);
}
