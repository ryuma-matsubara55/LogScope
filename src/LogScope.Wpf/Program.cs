using System.Windows;
using Velopack;

namespace LogScope.Wpf;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer hooks must complete before WPF creates a window or opens SQLite.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        // A second process could hold unsaved investigations when the updater restarts us.
        using var instance = new Mutex(true, "Local\\LogScope.Desktop", out bool ownsInstance);
        if (!ownsInstance)
        {
            MessageBox.Show("LogScopeは既に起動しています。起動済みのウィンドウを使用してください。", "LogScope");
            return;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
