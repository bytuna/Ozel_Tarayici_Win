using System.Windows;

namespace OzelTarayici;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(
                $"Beklenmeyen bir hata oluştu:\n{args.Exception.Message}",
                "Özel Tarayıcı",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };
        base.OnStartup(e);
    }
}

