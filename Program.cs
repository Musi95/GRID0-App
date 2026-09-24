using Avalonia;
using System;
using System.IO;

namespace GRID0;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            CrashLog("Main entered");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            CrashLog("Main exited normally");
        }
        catch (Exception ex)
        {
            CrashLog("FATAL: " + ex);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    // WinExe has no console, so a crash before the window appears is
    // invisible. This leaves a trail next to the exe instead.
    internal static void CrashLog(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "GRID0-crash.log"),
                "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }
        catch { }
    }
}
