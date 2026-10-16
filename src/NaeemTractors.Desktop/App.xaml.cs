using System.IO;
using System.Windows;

namespace NaeemTractors.Desktop;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaeemTractors");
            var log = Path.Combine(directory, "crash.log");
            var detail = e.Exception.GetBaseException().Message;
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(log, $"{DateTimeOffset.Now:O}\n{e.Exception}\n\n");
                detail += "\n\nDiagnostic details: " + log;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            MessageBox.Show(detail, "Naeem Tractors could not continue", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
    }
}
