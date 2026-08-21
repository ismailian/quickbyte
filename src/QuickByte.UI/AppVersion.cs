using System.Reflection;

namespace QuickByte.UI;

/// <summary>
/// Reads the running build's version back off its own assembly, so no window
/// ever hardcodes a version string. The numbers themselves are set in one place
/// — <c>Directory.Build.props</c> at the repo root — and flow here through the
/// assembly attributes the compiler stamps in.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// The product version as a person would say it ("1.1.0", or "1.2.0-beta.1"
    /// for a pre-release). Taken from InformationalVersion, minus any build
    /// metadata the SDK appends after a '+'.
    /// </summary>
    public static string Display { get; } = ReadDisplayVersion();

    /// <summary>
    /// The four-part file version, including the build number a CI run stamped
    /// in. This is the string that matches the .exe's Properties page, which is
    /// what makes it the useful one on a bug report.
    /// </summary>
    public static string File { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? Display;

    /// <summary>Copyright line as stamped into the assembly.</summary>
    public static string Copyright { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
        ?? string.Empty;

    private static string ReadDisplayVersion()
    {
        string? informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        // SourceLink appends "+<commit sha>" — useful in a log, noise in a title bar.
        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
