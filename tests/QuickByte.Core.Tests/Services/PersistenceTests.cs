using QuickByte.Core.Enums;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// Both loaders promise the same thing: a corrupt or unreadable file falls back
/// to defaults rather than failing startup. It is the difference between an app
/// that forgets a download list and one that will not open.
/// </summary>
public sealed class DownloadRepositoryTests
{
    [Fact]
    public void An_empty_profile_loads_an_empty_list()
    {
        using var folder = new TempFolder();

        Assert.Empty(new DownloadRepository(folder.Path).LoadAll());
    }

    [Fact]
    public void A_saved_list_comes_back()
    {
        using var folder = new TempFolder();
        var repository = new DownloadRepository(folder.Path);
        var item = new DownloadItem
        {
            Url = "https://example.com/a.bin",
            FileName = "a.bin",
            SaveFolder = @"C:\Downloads",
            TotalBytes = 1234,
            DownloadedBytes = 567,
            Status = DownloadStatus.Paused
        };

        repository.SaveAll(new[] { item });
        var loaded = repository.LoadAll();

        var restored = Assert.Single(loaded);
        Assert.Equal(item.Id, restored.Id);
        Assert.Equal("a.bin", restored.FileName);
        Assert.Equal(1234, restored.TotalBytes);
        Assert.Equal(DownloadStatus.Paused, restored.Status);
    }

    [Fact]
    public void The_captured_headers_survive_a_restart()
    {
        // A session cookie that resolved the link at capture time is what lets a
        // resume three days later fetch the same bytes rather than a login page.
        using var folder = new TempFolder();
        var repository = new DownloadRepository(folder.Path);
        var item = new DownloadItem();
        item.Headers["Cookie"] = "session=abc";

        repository.SaveAll(new[] { item });

        Assert.Equal("session=abc", repository.LoadAll().Single().Headers["Cookie"]);
    }

    [Fact]
    public void A_password_is_never_written_to_the_file_in_the_clear()
    {
        using var folder = new TempFolder();
        var repository = new DownloadRepository(folder.Path);

        repository.SaveAll(new[]
        {
            new DownloadItem
            {
                Credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" }
            }
        });

        string onDisk = File.ReadAllText(Path.Combine(folder.Path, "downloads.json"));
        Assert.DoesNotContain("hunter2", onDisk);
        Assert.Equal("hunter2", repository.LoadAll().Single().Credentials!.Password);
    }

    [Fact]
    public void A_corrupt_file_loads_as_empty_rather_than_throwing()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "downloads.json"), "{ this is not json");

        Assert.Empty(new DownloadRepository(folder.Path).LoadAll());
    }

    [Fact]
    public void Saving_replaces_the_whole_list()
    {
        using var folder = new TempFolder();
        var repository = new DownloadRepository(folder.Path);

        repository.SaveAll(new[] { new DownloadItem(), new DownloadItem() });
        repository.SaveAll(new[] { new DownloadItem() });

        Assert.Single(repository.LoadAll());
    }
}

public sealed class QueueRepositoryTests
{
    [Fact]
    public void An_empty_profile_loads_an_empty_list()
    {
        using var folder = new TempFolder();

        Assert.Empty(new QueueRepository(folder.Path).LoadAll());
        Assert.True(new QueueRepository(folder.Path).TryLoadAll(out var queues));
        Assert.Empty(queues);
    }

    [Fact]
    public void A_saved_queue_comes_back_whole()
    {
        using var folder = new TempFolder();
        var repository = new QueueRepository(folder.Path);
        var queue = new DownloadQueue
        {
            Name = "Nightly",
            ConcurrentDownloads = 4,
            SpeedLimitBytesPerSecond = 500_000,
            Schedule = new QueueSchedule
            {
                Enabled = true,
                Days = ScheduleDays.Weekdays,
                StartTime = TimeSpan.FromHours(2),
                StopAtEnabled = true,
                StopTime = TimeSpan.FromHours(6)
            }
        };
        queue.ItemIds.Add(Guid.NewGuid());

        repository.SaveAll(new[] { queue });
        var restored = repository.LoadAll().Single();

        Assert.Equal("Nightly", restored.Name);
        Assert.Equal(4, restored.ConcurrentDownloads);
        Assert.Equal(500_000, restored.SpeedLimitBytesPerSecond);
        Assert.Equal(ScheduleDays.Weekdays, restored.Schedule.Days);
        Assert.Equal(TimeSpan.FromHours(2), restored.Schedule.StartTime);
        Assert.True(restored.Schedule.StopAtEnabled);
        Assert.Single(restored.ItemIds);
    }

    [Fact]
    public void A_malformed_file_is_not_a_reason_to_retry_or_to_fail()
    {
        // Malformed rather than busy: retrying will not help, and a corrupt file
        // must not stop the app from starting.
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "queues.json"), "[[[ nope");

        Assert.True(new QueueRepository(folder.Path).TryLoadAll(out var queues));
        Assert.Empty(queues);
    }

    [Fact]
    public void A_save_leaves_no_temporary_file_behind()
    {
        // Writes go via a temp file that is moved over the real one, so a reader
        // never sees the empty window a truncating write leaves behind — which
        // the agent would read as "this user has no scheduled queues".
        using var folder = new TempFolder();
        var repository = new QueueRepository(folder.Path);

        repository.SaveAll(new[] { new DownloadQueue() });

        Assert.True(File.Exists(Path.Combine(folder.Path, "queues.json")));
        Assert.False(File.Exists(Path.Combine(folder.Path, "queues.json.tmp")));
    }

    [Fact]
    public void A_reader_can_open_the_file_while_a_writer_holds_it()
    {
        using var folder = new TempFolder();
        var repository = new QueueRepository(folder.Path);
        repository.SaveAll(new[] { new DownloadQueue { Name = "Nightly" } });

        // The agent is a second process reading this file while the app writes it.
        using var heldOpen = new FileStream(
            Path.Combine(folder.Path, "queues.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.True(repository.TryLoadAll(out var queues));
        Assert.Equal("Nightly", queues.Single().Name);
    }

    [Fact]
    public void The_same_file_read_twice_gives_the_same_verdict()
    {
        // The app and the agent have to agree about whether a run is owed.
        using var folder = new TempFolder();
        var when = new DateTime(2026, 8, 26, 2, 30, 0, DateTimeKind.Local);
        var queue = new DownloadQueue
        {
            Schedule = new QueueSchedule
            {
                Enabled = true, Days = ScheduleDays.EveryDay, StartTime = TimeSpan.FromHours(2)
            }
        };

        new QueueRepository(folder.Path).SaveAll(new[] { queue });

        Assert.Equal(
            new QueueRepository(folder.Path).LoadAll().Single().IsDue(when),
            new QueueRepository(folder.Path).LoadAll().Single().IsDue(when));
        Assert.True(new QueueRepository(folder.Path).LoadAll().Single().IsDue(when));
    }
}

public sealed class SettingsServiceTests
{
    [Fact]
    public void An_empty_profile_loads_the_defaults()
    {
        using var folder = new TempFolder();
        var service = new SettingsService(folder.Path);

        service.Load();

        Assert.Equal(8, service.Current.DefaultConnectionsCount);
        Assert.Equal(3, service.Current.MaxConcurrentDownloads);
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var folder = new TempFolder();
        var service = new SettingsService(folder.Path);
        service.Load();

        service.Save(new DownloadSettings
        {
            DefaultConnectionsCount = 16,
            GlobalSpeedLimitBytesPerSecond = 250_000,
            StartMinimized = true,
            DefaultDownloadFolder = folder.Path
        });

        var reopened = new SettingsService(folder.Path);
        reopened.Load();

        Assert.Equal(16, reopened.Current.DefaultConnectionsCount);
        Assert.Equal(250_000, reopened.Current.GlobalSpeedLimitBytesPerSecond);
        Assert.True(reopened.Current.StartMinimized);
        Assert.Equal(folder.Path, reopened.Current.DefaultDownloadFolder);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_rather_than_crashing_startup()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "settings.json"), "not json");

        var service = new SettingsService(folder.Path);
        service.Load();

        Assert.Equal(8, service.Current.DefaultConnectionsCount);
    }

    [Fact]
    public void Saving_announces_the_new_settings()
    {
        // The two live subscribers are the global speed limit and the browser
        // bridge; a limit you have to restart the app to apply is not a limit.
        using var folder = new TempFolder();
        var service = new SettingsService(folder.Path);
        service.Load();

        DownloadSettings? announced = null;
        service.SettingsChanged += (_, settings) => announced = settings;

        var saved = new DownloadSettings
        {
            GlobalSpeedLimitBytesPerSecond = 999,
            DefaultDownloadFolder = folder.Path
        };
        service.Save(saved);

        Assert.Same(saved, announced);
        Assert.Same(saved, service.Current);
    }

    [Fact]
    public void Load_creates_the_folders_a_download_is_about_to_need()
    {
        using var folder = new TempFolder();
        string downloads = Path.Combine(folder.Path, "finished");

        var service = new SettingsService(folder.Path);
        service.Save(new DownloadSettings { DefaultDownloadFolder = downloads });
        service.Load();

        Assert.True(Directory.Exists(downloads));
        Assert.True(Directory.Exists(Path.Combine(folder.Path, "temp")));
    }

    [Fact]
    public void The_chunk_folder_follows_the_data_folder_and_the_download_folder_does_not()
    {
        // The chunk folder is derived, not configured: Options has no field for
        // it, and a settings.json written by an older build still names %TEMP%,
        // where every disk cleaner on the machine sweeps it. Stamping it here is
        // what carries an existing install over. The download folder in the same
        // file is a real setting and has to come back untouched.
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "settings.json"),
            """{"DefaultDownloadFolder":"D:\\keep-me","TempFolder":"C:\\Windows\\Temp\\QuickByte"}""");

        var service = new SettingsService(folder.Path);
        service.Load();

        Assert.Equal(Path.Combine(folder.Path, "temp"), service.Current.TempFolder);
        Assert.Equal(@"D:\keep-me", service.Current.DefaultDownloadFolder);
    }

    [Fact]
    public void Save_stamps_the_chunk_folder_onto_the_fresh_settings_the_options_dialog_builds()
    {
        // SettingsForm builds a *new* DownloadSettings on every save and no
        // longer carries TempFolder forward — so the service has to, or the
        // first Save after an Options visit points the chunks at %AppData%
        // proper rather than at wherever this install keeps its state.
        using var folder = new TempFolder();
        var service = new SettingsService(folder.Path);
        service.Load();

        service.Save(new DownloadSettings { DefaultConnectionsCount = 16 });

        Assert.Equal(Path.Combine(folder.Path, "temp"), service.Current.TempFolder);
    }
}
