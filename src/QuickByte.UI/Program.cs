using System.Threading;
using System.Windows.Forms;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Services;
using QuickByte.UI.Forms;

namespace QuickByte.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Before anything is constructed: if QuickByte is already running, hand
        // this launch's URL to that window and get out. Two processes sharing
        // one downloads.json would overwrite each other's state.
        var singleInstance = SingleInstance.Acquire();
        if (singleInstance is null)
        {
            SingleInstance.SendToRunningInstance(SingleInstance.FindUrl(args) ?? string.Empty);
            return;
        }

        using (singleInstance)
        {
            ApplicationConfiguration.Initialize();

            // --- Composition root -------------------------------------------------
            // Plain constructor injection (no DI container needed for an app this
            // size) wires the Core services together and hands the assembled
            // IDownloadManager facade to the UI. This is the only place concrete
            // Core service types are referenced directly.
            var settingsService = new SettingsService();
            settingsService.Load();

            var repository = new DownloadRepository();

            // The protocol pair, and they must stay a pair: whichever provider
            // resolves a URL's size, the matching factory has to be the one that
            // then fetches it. See ProtocolFileInfoProvider.
            IRemoteFileInfoProvider fileInfoProvider = new ProtocolFileInfoProvider();
            var connectionFactory = new ProtocolConnectionFactory();

            IUpdateService updateService = new UpdateService();
            var fileMerger = new FileMerger();

            // Explicitly install a WinForms sync context so background threads
            // (HTTP downloads, timers) can safely marshal events to the UI thread
            // even before any control handle has been created.
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            var dispatcher = new AppDispatcher(SynchronizationContext.Current!);

            IDownloadManager downloadManager = new DownloadManager(
                repository, settingsService, fileInfoProvider, connectionFactory, fileMerger, dispatcher);

            downloadManager.LoadPersistedDownloads();

            // Only meaningful once the list is loaded — until then every folder
            // looks unclaimed. Deliberately not awaited: it walks the temp
            // volume, and nothing on screen depends on the result.
            _ = downloadManager.CleanupOrphanedTempFoldersAsync();

            using IBrowserIntegrationService browserIntegration = new BrowserIntegrationServer(settingsService);

            var mainForm = new MainForm(downloadManager, settingsService, updateService, fileInfoProvider, browserIntegration);

            // The pipe listener raises this on a thread-pool thread, so it goes
            // through the same dispatcher Core's events do rather than touching
            // a control directly.
            singleInstance.SecondInstanceStarted += (_, payload) =>
                dispatcher.Post(() => mainForm.HandleSecondInstance(payload));

            // Same story for the browser bridge: its accept loop is on the thread
            // pool, and the handler opens a dialog.
            browserIntegration.DownloadCaptured += (_, captured) =>
                dispatcher.Post(() => mainForm.HandleCapturedDownload(captured));

            // After the subscription, so a capture that lands during startup is
            // not raised into nothing. A failure to bind is recorded on the
            // service and shown in Options; it must not stop the app.
            browserIntegration.Start();

            // A URL passed to the *first* launch still deserves the Add dialog.
            string? startupUrl = SingleInstance.FindUrl(args);
            if (startupUrl is not null)
                dispatcher.Post(() => mainForm.HandleSecondInstance(startupUrl));

            Application.Run(mainForm);
        }
    }
}
