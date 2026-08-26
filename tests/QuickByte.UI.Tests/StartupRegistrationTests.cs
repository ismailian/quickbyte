namespace QuickByte.UI.Tests;

/// <summary>
/// "Start QuickByte when I sign in", which is a value under the Run key and
/// nothing else.
///
/// Two things here are worth more than they look. The value is a <em>command
/// line</em>, so an unquoted path through <c>C:\Program Files\</c> asks Windows
/// to run <c>C:\Program.exe</c> — a startup entry that silently does nothing.
/// And the registry, not the setting, is the truth: Task Manager can remove the
/// entry behind the app's back, and an update moves the executable out from
/// under it, which is why every launch re-asserts it.
///
/// The tests run against a scratch key (see <see cref="ScratchRunKey"/>) — the
/// real one is the user's.
/// </summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly ScratchRunKey _key = new();

    public void Dispose() => _key.Dispose();

    private static string ExpectedCommand => $"\"{Environment.ProcessPath}\"";

    [Fact]
    public void Registering_names_this_executable()
    {
        Assert.True(StartupRegistration.TryApply(true, out string? error));

        Assert.Null(error);
        Assert.Equal(ExpectedCommand, _key.Value(StartupRegistration.ValueName));
        Assert.True(StartupRegistration.IsEnabled);
    }

    [Fact]
    public void The_path_is_quoted()
    {
        // Run values are command lines. Unquoted, "C:\Program Files\QuickByte\
        // QuickByte.exe" is read as C:\Program.exe with arguments -- and the
        // failure is silent, because nothing runs at all.
        StartupRegistration.TryApply(true, out _);

        string value = _key.Value(StartupRegistration.ValueName)!;

        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
        Assert.Equal(Environment.ProcessPath, value.Trim('"'));
    }

    [Fact]
    public void The_value_is_the_name_Task_Manager_shows()
    {
        // Users turn startup entries off in Task Manager, so the label matters:
        // it is the only thing identifying this row to them.
        Assert.Equal("QuickByte", StartupRegistration.ValueName);
    }

    [Fact]
    public void Deregistering_removes_the_entry()
    {
        StartupRegistration.TryApply(true, out _);

        Assert.True(StartupRegistration.TryApply(false, out string? error));

        Assert.Null(error);
        Assert.Null(_key.Value(StartupRegistration.ValueName));
        Assert.False(StartupRegistration.IsEnabled);
    }

    [Fact]
    public void Deregistering_something_that_was_never_registered_is_not_a_failure()
    {
        // The box is unticked and always was: reporting an error here would put
        // a warning on a successful Save.
        Assert.True(StartupRegistration.TryApply(false, out string? error));
        Assert.Null(error);
    }

    [Fact]
    public void An_empty_value_does_not_count_as_registered()
    {
        _key.SetValue(StartupRegistration.ValueName, string.Empty);

        Assert.False(StartupRegistration.IsEnabled);
    }

    [Fact]
    public void Nothing_is_written_to_the_registry_when_there_is_nothing_to_do()
    {
        // The common case, on every launch of an app the user never asked to
        // start with Windows: it must not create a key, let alone delete a value
        // it does not own.
        StartupRegistration.Sync(false);

        Assert.False(_key.Exists);
    }

    [Fact]
    public void Sync_registers_when_the_preference_says_so()
    {
        StartupRegistration.Sync(true);

        Assert.Equal(ExpectedCommand, _key.Value(StartupRegistration.ValueName));
    }

    [Fact]
    public void Sync_leaves_exactly_one_entry_however_often_it_runs()
    {
        StartupRegistration.Sync(true);
        StartupRegistration.Sync(true);
        StartupRegistration.Sync(true);

        Assert.Equal(new[] { StartupRegistration.ValueName }, _key.ValueNames());
        Assert.Equal(ExpectedCommand, _key.Value(StartupRegistration.ValueName));
    }

    [Fact]
    public void Sync_repoints_an_entry_left_behind_by_an_older_install()
    {
        // This is what carries "start with Windows" through an update or a move:
        // the old path is still in the key, and nothing but this rewrites it.
        _key.SetValue(StartupRegistration.ValueName, @"""C:\Program Files\QuickByte\QuickByte.exe""");

        StartupRegistration.Sync(true);

        Assert.Equal(ExpectedCommand, _key.Value(StartupRegistration.ValueName));
    }

    [Fact]
    public void Sync_removes_an_entry_the_user_has_turned_off()
    {
        StartupRegistration.Sync(true);

        StartupRegistration.Sync(false);

        Assert.Null(_key.Value(StartupRegistration.ValueName));
    }

    [Fact]
    public void The_registry_is_what_IsEnabled_reports_not_the_saved_preference()
    {
        // Task Manager can remove the entry behind the app's back, which is why
        // the Options checkbox loads from here rather than from settings.json.
        StartupRegistration.TryApply(true, out _);
        Assert.True(StartupRegistration.IsEnabled);

        _key.SetValue(StartupRegistration.ValueName, string.Empty);
        Assert.False(StartupRegistration.IsEnabled);
    }
}
