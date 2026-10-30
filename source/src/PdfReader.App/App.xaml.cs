using System.Windows;
using System.Windows.Threading;

namespace PdfReader.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // Every window (dialogs too) gets a dark title bar in the Dark theme.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => { if (sender is Window w) Theme.ApplyTitleBar(w); }));

        // "Open with" / drag a PDF onto the exe passes its path as the first argument.
        string? fileToOpen = e.Args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(fileToOpen);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "Simple PDF Narrator",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
