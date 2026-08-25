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
            // Whatever this launch was for — a link, or a queue the scheduler
            // agent says is due — goes to the copy that is already running.
            SingleInstance.SendToRunningInstance(SingleInstance.BuildHandoffPayload(args));
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

            // Re-asserts the Run key entry against this executable's current
            // path, so "start with Windows" survives an update or a move. Silent
            // and best-effort: nobody asked for it on this launch.
            StartupRegistration.Sync(settingsService.Current.StartWithWindows);

            var repository = new DownloadRepository();
            var queueRepository = new QueueRepository();

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

            // After the downloads: a queue's membership is a list of download
            // ids, and until they exist a queue looks empty. Load() also starts
            // the in-app schedule timer, which is what runs a due queue while the
            // window is open — the agent below covers the hours it is not.
            IQueueManager queueManager = new QueueManager(queueRepository, downloadManager, dispatcher);
            queueManager.Load();

            // Installs (or removes) the background scheduler that starts queues
            // when QuickByte is closed. Re-asserted on every launch for the same
            // reason StartupRegistration is: an update moves the executable, and
            // a Run entry pointing at the old path is a scheduler that silently
            // stopped working.
            QueueAgentRegistration.Sync(queueManager.HasScheduledQueues);

            // Only meaningful once the list is loaded — until then every folder
            // looks unclaimed. Deliberately not awaited: it walks the temp
            // volume, and nothing on screen depends on the result.
            _ = downloadManager.CleanupOrphanedTempFoldersAsync();

            using IBrowserIntegrationService browserIntegration = new BrowserIntegrationServer(settingsService);

            // A launch that goes straight to the notification area, either
            // because the user asked for it in Options or because whatever
            // started us said so on the command line. The switch is what lets an
            // auto-start entry be quiet while a hand-launch still opens a window,
            // if the user ever wants to split the two.
            bool startMinimized = settingsService.Current.StartMinimized || HasMinimizedSwitch(args);

            var mainForm = new MainForm(
                downloadManager, queueManager, settingsService, updateService, fileInfoProvider,
                browserIntegration, startMinimized);

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

            // The agent starts QuickByte with --run-queue when a queue comes due
            // and nothing was running to hear about it. Posted like everything
            // else here: the manager raises its events through the dispatcher,
            // and the message loop has not started yet.
            Guid? startupQueueId = SingleInstance.FindQueueId(args);
            if (startupQueueId is { } queueId)
                dispatcher.Post(() => mainForm.StartQueueFromScheduler(queueId));

            // A URL passed to the *first* launch still deserves the Add window.
            string? startupUrl = SingleInstance.FindUrl(args);
            if (startupUrl is not null)
                dispatcher.Post(() => mainForm.HandleSecondInstance(startupUrl));
            else if (startMinimized)
                // Posted rather than called: the balloon belongs to the message
                // loop, which has not started yet. A launch that puts nothing on
                // screen is indistinguishable from one that failed, so it says
                // where it went.
                dispatcher.Post(mainForm.NotifyStartedMinimized);

            Application.Run(mainForm);

            // Stops the schedule timer and cancels any run still in flight. The
            // downloads themselves are already paused by MainForm's close path.
            queueManager.Dispose();
        }
    }

    /// <summary>
    /// <c>--minimized</c> / <c>-m</c> on the command line, for anything that
    /// wants a quiet launch without the setting being on — a shortcut, a
    /// scheduled task, or a test run.
    /// </summary>
    private static bool HasMinimizedSwitch(IEnumerable<string> args) =>
        args.Any(argument => argument.Trim().Trim('"')
            is "--minimized" or "-minimized" or "/minimized" or "-m");
}
