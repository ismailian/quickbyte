namespace QuickByte.Core.Tests;

/// <summary>
/// A scratch directory that deletes itself at the end of a test.
///
/// The engine is built around what is on disk — resume is driven by chunk file
/// length, the repositories are files, and the temp-folder sweep is the whole
/// point of several code paths — so a good many of these tests need a real
/// folder rather than a mock file system. Each gets its own, named with a Guid
/// so tests can run in parallel without colliding.
/// </summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "QuickByte.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Full path of <paramref name="name"/> inside this folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="name"/> and returns its full path.</summary>
    public string WriteFile(string name, byte[] bytes)
    {
        string path = File(name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch { /* best-effort, matching the engine's own cleanup idiom */ }
    }
}
