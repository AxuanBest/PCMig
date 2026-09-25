using System;
using System.IO;
using System.Text;
using Microsoft.UI.Xaml;

namespace PCMig.WinUI;

public partial class App : Application
{
    public static Window? MainWindowInstance { get; private set; }

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "poc-startup.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log("UnhandledException", e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched", null, "start");
            MainWindowInstance = new MainWindow();
            MainWindowInstance.Activate();
            Log("OnLaunched", null, "activated");
        }
        catch (Exception ex)
        {
            Log("OnLaunched", ex);
            throw;
        }
    }

    private static void Log(string where, Exception? ex, string? note = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(where).Append(']');
            if (note != null) sb.Append(' ').Append(note);
            if (ex != null) sb.AppendLine().Append(ex.ToString());
            File.AppendAllText(LogPath, sb.AppendLine().ToString(), new UTF8Encoding(false));
        }
        catch { }
    }
}