using CodeXProxy.App;
using CodeXProxy.UI;
using CodeXProxy.Windows;

namespace CodeXProxy;

internal static class Program
{
    private const string MutexName = @"Local\CodeXProxy.SingleInstance";
    private const string ShowEventName = @"Local\CodeXProxy.ShowWindow";

    [STAThread]
    private static void Main()
    {
        AppPaths.EnsureDirectories();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();

        var systemProxy = new SystemProxyManager();
        var recovery = systemProxy.RecoverIfNeeded();
        var form = new MainForm(systemProxy, recovery);

        var showRegistration = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) =>
            {
                if (form.IsDisposed || !form.IsHandleCreated)
                {
                    return;
                }

                try
                {
                    form.BeginInvoke(form.ShowFromTray);
                }
                catch (InvalidOperationException)
                {
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        try
        {
            Application.Run(form);
        }
        finally
        {
            showRegistration.Unregister(null);
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }
}
