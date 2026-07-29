using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GamerGod.Ui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // An unhandled exception in a tool that changes system state must not vanish into a
        // silent process exit. The user needs to know something went wrong and, more
        // importantly, that rebooting undoes anything that was applied.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        base.OnStartup(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true;
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Report(e.ExceptionObject as Exception);

    private static void Report(Exception? exception)
    {
        var log = TryWriteLog(exception);

        MessageBox.Show(
            "GamerGod hit an unexpected error.\n\n"
            + (exception?.Message ?? "No detail available.")
            + "\n\nNothing on your machine is stuck. Any change GamerGod applied is undone by "
            + "'gamergod off', and rebooting undoes it regardless — that does not depend on "
            + "GamerGod working."
            + (log is null ? string.Empty : $"\n\nDetails written to:\n{log}"),
            "GamerGod",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string? TryWriteLog(Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GamerGod", "logs");

            Directory.CreateDirectory(directory);

            // Deterministic name so a user can find it, and so repeated failures append
            // rather than filling the folder.
            var path = Path.Combine(directory, "ui-errors.log");

            File.AppendAllText(
                path,
                $"---- {DateTimeOffset.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}");

            return path;
        }
        catch (Exception)
        {
            // If even logging fails there is nothing useful left to do about it.
            return null;
        }
    }
}
