using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace MeshVenes;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        System.IO.FileStream? installationLock;
        try { installationLock = Services.InstallationUseLock.Acquire(AppContext.BaseDirectory); }
        catch (System.IO.IOException) { return; } // An updater has exclusive access; do not load partially replaced assemblies.
        using var lifetimeLock = installationLock;
        ComWrappersSupport.InitializeComWrappers();

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App();
        });
    }
}
