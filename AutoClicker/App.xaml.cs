using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace AutoClicker
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Never let a stray exception silently kill the whole program. Log it, write a crash
            // file the user can send us, and (for UI-thread hiccups) keep the app running.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrash("ui-thread", e.Exception);
            e.Handled = true;   // a UI hiccup must not close the app
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrash("background", e.ExceptionObject as Exception);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            WriteCrash("task", e.Exception);
            e.SetObserved();
        }

        private static void WriteCrash(string where, Exception ex)
        {
            try
            {
                Log.Error(ex, "Unhandled {Where} exception", where);
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AutoClicker_CRASH.txt");
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}\r\n{ex}\r\n\r\n");
            }
            catch
            {
                // the crash handler must never throw
            }
        }
    }
}
