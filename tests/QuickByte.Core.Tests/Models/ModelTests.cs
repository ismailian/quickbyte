using System.Text.Json;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Models;

public sealed class DownloadQueueTests
{
    private static readonly DateTime Wednesday = new(2026, 8, 26, 0, 0, 0, DateTimeKind.Local);

    private static DownloadQueue Scheduled(TimeSpan start) => new()
    {
        Schedule = new QueueSchedule { Enabled = true, Days = ScheduleDays.EveryDay, StartTime = start }
    };

    [Fact]
    public void A_queue_in_its_window_that_has_never_run_is_due() =>
        Assert.True(Scheduled(TimeSpan.FromHours(2)).IsDue(Wednesday.AddHours(2).AddMinutes(10)));

    [Fact]
    public void A_queue_already_started_in_this_window_is_not_due_again()
    {
        // The single question both the in-app scheduler and the agent ask, so two
        // watchers of one file cannot start the same window twice.
        var queue = Scheduled(TimeSpan.FromHours(2));
        queue.LastRunAt = new DateTimeOffset(Wednesday.AddHours(2).AddMinutes(1));

        Assert.False(queue.IsDue(Wednesday.AddHours(2).AddMinutes(10)));
    }

    [Fact]
    public void A_run_from_a_previous_window_does_not_satisfy_this_one()
    {
        var queue = Scheduled(TimeSpan.FromHours(2));
        queue.LastRunAt = new DateTimeOffset(Wednesday.AddDays(-1).AddHours(2));

        Assert.True(queue.IsDue(Wednesday.AddHours(2).AddMinutes(10)));
    }

    [Fact]
    public void NextRunAt_is_now_when_a_run_is_owed()
    {
        var queue = Scheduled(TimeSpan.FromHours(2));

        Assert.Equal(Wednesday.AddHours(2), queue.NextRunAt(Wednesday.AddHours(2).AddMinutes(10)));
    }

    [Fact]
    public void NextRunAt_is_the_next_occurrence_when_nothing_is_owed()
    {
        var queue = Scheduled(TimeSpan.FromHours(2));

        Assert.Equal(Wednesday.AddDays(1).AddHours(2), queue.NextRunAt(Wednesday.AddHours(10)));
    }

    [Fact]
    public void NextRunAt_is_null_for_a_queue_that_is_not_scheduled() =>
        Assert.Null(new DownloadQueue().NextRunAt(Wednesday));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(20, 20)]
    [InlineData(999, 20)]
    public void ClampConcurrency_keeps_the_queue_inside_its_own_bounds(int requested, int expected)
    {
        var queue = new DownloadQueue { ConcurrentDownloads = requested };

        Assert.Equal(expected, queue.ClampConcurrency());
    }

    [Fact]
    public void An_explicit_null_in_the_json_does_not_beat_the_initializer()
    {
        // A property initializer only survives a member that is absent from the
        // JSON. Every read below assumes these objects exist.
        var queue = JsonSerializer.Deserialize<DownloadQueue>("""{"Schedule":null,"ItemIds":null}""");

        Assert.NotNull(queue);
        Assert.NotNull(queue!.Schedule);
        Assert.NotNull(queue.ItemIds);
        Assert.Empty(queue.ItemIds);
    }

    [Fact]
    public void Clone_gives_an_editor_a_copy_that_is_not_already_live()
    {
        var original = new DownloadQueue { Name = "Nightly", ConcurrentDownloads = 3 };
        original.ItemIds.Add(Guid.NewGuid());

        var copy = original.Clone();
        copy.Name = "Edited";
        copy.ItemIds.Add(Guid.NewGuid());
        copy.Schedule.Enabled = true;

        Assert.Equal("Nightly", original.Name);
        Assert.Single(original.ItemIds);
        Assert.False(original.Schedule.Enabled);
        Assert.Equal(original.Id, copy.Id);
    }
}

public sealed class DownloadItemTests
{
    [Theory]
    [InlineData(0, 100, 0)]
    [InlineData(50, 100, 50)]
    [InlineData(100, 100, 100)]
    [InlineData(150, 100, 100)]
    [InlineData(50, 0, 0)]
    public void ProgressPercentage_never_exceeds_a_full_bar(long done, long total, double expected)
    {
        var item = new DownloadItem { DownloadedBytes = done, TotalBytes = total };

        Assert.Equal(expected, item.ProgressPercentage);
    }

    [Theory]
    [InlineData(DownloadStatus.Queued, DownloadCategory.Queued)]
    [InlineData(DownloadStatus.Connecting, DownloadCategory.InProgress)]
    [InlineData(DownloadStatus.Downloading, DownloadCategory.InProgress)]
    [InlineData(DownloadStatus.Merging, DownloadCategory.InProgress)]
    [InlineData(DownloadStatus.Paused, DownloadCategory.Paused)]
    [InlineData(DownloadStatus.Completed, DownloadCategory.Completed)]
    [InlineData(DownloadStatus.Failed, DownloadCategory.Failed)]
    [InlineData(DownloadStatus.Cancelled, DownloadCategory.Failed)]
    public void Category_drives_which_sidebar_entry_a_row_belongs_to(DownloadStatus status, DownloadCategory expected) =>
        Assert.Equal(expected, new DownloadItem { Status = status }.Category);

    [Fact]
    public void FullPath_is_the_folder_and_the_name()
    {
        var item = new DownloadItem { SaveFolder = @"C:\Downloads", FileName = "a.bin" };

        Assert.Equal(Path.Combine(@"C:\Downloads", "a.bin"), item.FullPath);
    }

    [Fact]
    public void Headers_survives_an_explicit_null_in_a_persisted_download()
    {
        var item = JsonSerializer.Deserialize<DownloadItem>("""{"Headers":null}""");

        Assert.NotNull(item!.Headers);
        Assert.Empty(item.Headers);
    }

    [Fact]
    public void ToRequestOptions_leaves_headers_null_rather_than_empty()
    {
        var bare = new DownloadItem().ToRequestOptions();

        Assert.Null(bare.Headers);
        Assert.False(bare.HasHeaders);
        Assert.False(bare.HasCredentials);
    }

    [Fact]
    public void ToRequestOptions_carries_the_login_and_the_captured_headers()
    {
        var item = new DownloadItem
        {
            Credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" }
        };
        item.Headers["Cookie"] = "session=abc";

        var options = item.ToRequestOptions();

        Assert.True(options.HasCredentials);
        Assert.True(options.HasHeaders);
        Assert.Equal("session=abc", options.Headers!["Cookie"]);
    }

    [Fact]
    public void RequestOptions_None_is_anonymous_and_header_free()
    {
        Assert.False(RequestOptions.None.HasCredentials);
        Assert.False(RequestOptions.None.HasHeaders);
    }
}

public sealed class DownloadCredentialsTests
{
    [Fact]
    public void The_live_password_never_reaches_the_json()
    {
        var credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" };

        string json = JsonSerializer.Serialize(credentials);

        Assert.DoesNotContain("hunter2", json);
        Assert.Contains("alice", json);
    }

    [Fact]
    public void A_password_round_trips_through_the_protected_property()
    {
        var original = new DownloadCredentials { UserName = "alice", Password = "hunter2" };

        var restored = JsonSerializer.Deserialize<DownloadCredentials>(JsonSerializer.Serialize(original));

        // Same user, same machine: DPAPI gives it back.
        Assert.Equal("alice", restored!.UserName);
        Assert.Equal("hunter2", restored.Password);
    }

    [Fact]
    public void A_profile_copied_from_another_machine_loads_with_an_empty_password()
    {
        // Far better than a startup crash over one unreadable field: the download
        // simply asks again.
        var restored = JsonSerializer.Deserialize<DownloadCredentials>(
            """{"UserName":"alice","ProtectedPassword":"bm90IGEgcmVhbCBkcGFwaSBibG9i"}""");

        Assert.Equal("alice", restored!.UserName);
        Assert.Equal(string.Empty, restored.Password);
    }

    [Fact]
    public void IsEmpty_is_what_decides_whether_anything_is_presented()
    {
        Assert.True(new DownloadCredentials().IsEmpty);
        Assert.False(new DownloadCredentials { UserName = "alice" }.IsEmpty);
        Assert.False(new DownloadCredentials { Password = "hunter2" }.IsEmpty);
    }

    [Fact]
    public void Clone_copies_the_live_password_not_the_protected_one()
    {
        var clone = new DownloadCredentials { UserName = "alice", Password = "hunter2" }.Clone();

        Assert.Equal("alice", clone.UserName);
        Assert.Equal("hunter2", clone.Password);
    }
}

public sealed class SecretProtectorTests
{
    [Fact]
    public void A_secret_round_trips_for_the_user_that_wrote_it()
    {
        string? protectedValue = SecretProtector.Protect("hunter2");

        Assert.NotNull(protectedValue);
        Assert.NotEqual("hunter2", protectedValue);
        Assert.Equal("hunter2", SecretProtector.Unprotect(protectedValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_to_protect_protects_to_nothing(string? plaintext) =>
        Assert.Null(SecretProtector.Protect(plaintext));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all !!")]
    [InlineData("dHJ1bmNhdGVk")]
    public void Anything_unreadable_comes_back_empty_rather_than_throwing(string? stored) =>
        Assert.Equal(string.Empty, SecretProtector.Unprotect(stored));

    [Fact]
    public void The_ciphertext_is_bound_to_this_application()
    {
        // App-specific entropy, so a blob lifted out of QuickByte's file cannot be
        // handed to another CurrentUser-scope DPAPI consumer to decrypt.
        string? mine = SecretProtector.Protect("hunter2");
        byte[] raw = System.Security.Cryptography.ProtectedData.Unprotect(
            Convert.FromBase64String(mine!),
            System.Text.Encoding.UTF8.GetBytes("QuickByte.Credential.v1"),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);

        Assert.Equal("hunter2", System.Text.Encoding.UTF8.GetString(raw));
    }
}

public sealed class DownloadSettingsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(32, 32)]
    [InlineData(500, 32)]
    public void ClampConnections_keeps_a_request_inside_the_supported_range(int requested, int expected) =>
        Assert.Equal(expected, new DownloadSettings().ClampConnections(requested));

    [Theory]
    [InlineData(81920, 81920)]
    [InlineData(0, DownloadSettings.MinBufferSizeBytes)]
    [InlineData(-1, DownloadSettings.MinBufferSizeBytes)]
    [InlineData(64, DownloadSettings.MinBufferSizeBytes)]
    [InlineData(int.MaxValue, DownloadSettings.MaxBufferSizeBytes)]
    public void ClampBufferSize_defuses_a_hand_edited_settings_file(int configured, int expected)
    {
        // StreamBufferSizeBytes has no field in Options, so the only way it holds
        // a number is by being edited into settings.json. A 0 there would reach a
        // new byte[] and a FileStream constructor.
        var settings = new DownloadSettings { StreamBufferSizeBytes = configured };

        Assert.Equal(expected, settings.ClampBufferSize());
    }

    [Fact]
    public void The_defaults_are_the_ones_the_app_is_documented_with()
    {
        var settings = new DownloadSettings();

        Assert.Equal(8, settings.DefaultConnectionsCount);
        Assert.Equal(100, settings.ProgressUpdateIntervalMilliseconds);
        Assert.Equal(3, settings.MaxConcurrentDownloads);
        Assert.Equal(0, settings.GlobalSpeedLimitBytesPerSecond);
        Assert.Equal(9614, settings.BrowserIntegrationPort);
        Assert.Equal(string.Empty, settings.BrowserIntegrationToken);
    }

    [Fact]
    public void The_bridge_token_has_no_default_worth_the_name()
    {
        // A fixed default would be no secret at all — every install would accept
        // every install's extension.
        Assert.Empty(new DownloadSettings().BrowserIntegrationToken);
    }
}

public sealed class CapturedDownloadTests
{
    [Fact]
    public void ToHeaders_carries_what_makes_a_signed_in_link_resolve()
    {
        var captured = new CapturedDownload
        {
            Url = "https://example.com/f.bin",
            Cookie = "session=abc",
            Referrer = "https://example.com/page",
            UserAgent = "Mozilla/5.0"
        };

        var headers = captured.ToHeaders();

        Assert.Equal("session=abc", headers["Cookie"]);
        Assert.Equal("https://example.com/page", headers["Referer"]);
        Assert.Equal("Mozilla/5.0", headers["User-Agent"]);
    }

    [Fact]
    public void ToHeaders_drops_an_empty_value_rather_than_sending_it_blank()
    {
        // An empty Referer is a different request from no Referer at all.
        var headers = new CapturedDownload { Cookie = "  ", Referrer = "", UserAgent = null }.ToHeaders();

        Assert.Empty(headers);
    }

    [Fact]
    public void ToHeaders_is_case_insensitive_like_the_wire_it_came_off()
    {
        var headers = new CapturedDownload { Cookie = "session=abc" }.ToHeaders();

        Assert.True(headers.ContainsKey("cookie"));
    }
}

public sealed class UpdateModelTests
{
    [Fact]
    public void A_manifest_needs_a_version_and_a_link_to_be_worth_acting_on()
    {
        Assert.True(new UpdateManifest { Version = "1.4.0", DownloadUrl = "https://x/y.exe" }.IsUsable);
        Assert.False(new UpdateManifest { Version = "1.4.0" }.IsUsable);
        Assert.False(new UpdateManifest { DownloadUrl = "https://x/y.exe" }.IsUsable);
        Assert.False(new UpdateManifest().IsUsable);
    }

    [Fact]
    public void A_manifest_binds_whichever_way_it_was_hand_written()
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(
            """{"Version":"1.4.0","downloadUrl":"https://x/y.exe"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("1.4.0", manifest!.Version);
        Assert.Equal("https://x/y.exe", manifest.DownloadUrl);
    }

    [Fact]
    public void The_optional_parts_of_a_manifest_really_are_optional()
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(
            """{"version":"1.4.0","downloadUrl":"https://x/y.exe"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.True(manifest!.IsUsable);
        Assert.Null(manifest.ReleaseNotes);
        Assert.Null(manifest.Sha256);
        Assert.Null(manifest.ReleaseDate);
        Assert.Equal(0, manifest.FileSizeBytes);
    }

    [Fact]
    public void An_up_to_date_result_still_has_a_number_to_show()
    {
        var result = UpdateCheckResult.UpToDate("1.4.0");

        Assert.False(result.UpdateAvailable);
        Assert.Equal("1.4.0", result.CurrentVersion);
        Assert.Equal("1.4.0", result.LatestVersion);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void An_available_result_carries_both_versions()
    {
        var manifest = new UpdateManifest { Version = "1.5.0", DownloadUrl = "https://x/y.exe" };

        var result = UpdateCheckResult.Available("1.4.0", manifest);

        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.4.0", result.CurrentVersion);
        Assert.Equal("1.5.0", result.LatestVersion);
        Assert.Same(manifest, result.Manifest);
    }
}

public sealed class FileCategoryHelperTests
{
    [Theory]
    [InlineData("archive.zip", FileCategory.Compressed)]
    [InlineData("archive.7z", FileCategory.Compressed)]
    [InlineData("paper.PDF", FileCategory.Documents)]
    [InlineData("song.mp3", FileCategory.Music)]
    [InlineData("setup.exe", FileCategory.Programs)]
    [InlineData("app.apk", FileCategory.Programs)]
    [InlineData("movie.mkv", FileCategory.Video)]
    [InlineData("photo.jpeg", FileCategory.Pictures)]
    public void GetCategory_reads_the_extension(string fileName, FileCategory expected) =>
        Assert.Equal(expected, FileCategoryHelper.GetCategory(fileName));

    [Theory]
    [InlineData("mystery.qqq")]
    [InlineData("noextension")]
    [InlineData("")]
    public void Anything_unrecognised_is_Other(string fileName) =>
        Assert.Equal(FileCategory.Other, FileCategoryHelper.GetCategory(fileName));
}
