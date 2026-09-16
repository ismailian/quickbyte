using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QuickByte.Core.Tests.Browser;

/// <summary>
/// The browser extension beside <c>BrowserIntegrationServer</c>: one set of
/// scripts, packaged for Chrome, Edge and Firefox from two manifests.
///
/// Nothing builds the extension, so nothing else would notice the two manifests
/// drifting apart — a permission added for Chrome only, a version bumped in one —
/// or a new script the packaging script does not ship. And the extension only runs
/// in Firefox if every call goes through the shared <c>qbApi</c> name without a
/// callback, which is a rule a grep can hold and a reviewer will not.
///
/// The logic inside the scripts has its own tests, in <c>browser/tests</c>, run
/// with <c>node --test "browser/tests/*.test.mjs"</c>.
/// </summary>
public sealed class ExtensionPackageTests
{
    private static readonly string Extension = Path.Combine(RepositoryRoot(), "browser", "chrome-extension");

    private static JsonObject Manifest(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Extension, name)))!.AsObject();

    [Fact]
    public void The_Firefox_manifest_differs_from_Chrome_s_only_where_Firefox_needs_it_to()
    {
        var chrome = Manifest("manifest.json");
        var firefox = Manifest("manifest.firefox.json");

        foreach (var (key, value) in chrome)
        {
            if (key == "background") continue;
            Assert.True(JsonNode.DeepEquals(value, firefox[key]), $"'{key}' differs between the two manifests");
        }

        var extra = firefox.Select(pair => pair.Key).Except(chrome.Select(pair => pair.Key)).ToList();
        Assert.Equal(new[] { "browser_specific_settings" }, extra);
    }

    [Fact]
    public void Chrome_and_Edge_run_a_service_worker_and_Firefox_background_scripts()
    {
        Assert.Equal("background.js", (string?)Manifest("manifest.json")["background"]!["service_worker"]);
        Assert.Null(Manifest("manifest.json")["background"]!["scripts"]);

        // Firefox has no importScripts in a background script, so the shared file
        // has to be listed, and listed first.
        var scripts = Manifest("manifest.firefox.json")["background"]!["scripts"]!.AsArray().Select(n => (string?)n);
        Assert.Equal(new[] { "shared.js", "background.js" }, scripts);
    }

    [Fact]
    public void Firefox_is_given_the_add_on_id_it_requires()
    {
        var gecko = Manifest("manifest.firefox.json")["browser_specific_settings"]!["gecko"]!;

        Assert.Matches(@"^[a-z0-9-]+@[a-z0-9.-]+$", (string?)gecko["id"]);
        Assert.NotNull(gecko["strict_min_version"]);
    }

    [Fact]
    public void Every_file_the_extension_loads_exists_and_is_packaged()
    {
        var referenced = ReferencedFiles().ToList();
        var packaged = PackagedFiles();

        Assert.All(referenced, file => Assert.True(File.Exists(Path.Combine(Extension, file)), $"{file} is referenced but missing"));
        Assert.All(referenced, file => Assert.Contains(file, packaged));
    }

    [Fact]
    public void No_script_calls_the_Chrome_only_namespace_or_passes_a_callback()
    {
        foreach (string script in Directory.GetFiles(Extension, "*.js"))
        {
            string code = StripComments(File.ReadAllText(script));
            string name = Path.GetFileName(script);

            // The one place the name is allowed: choosing between the two.
            code = code.Replace("globalThis.browser ?? globalThis.chrome", string.Empty);

            Assert.DoesNotMatch(@"\bchrome\.", code);
            Assert.DoesNotMatch(@"\bbrowser\.", code);
            Assert.DoesNotMatch(@"lastError", code);
            Assert.False(Regex.IsMatch(code, @"sendMessage\([^;]*?,\s*\(?\s*\w*\s*\)?\s*=>"), $"{name} passes sendMessage a callback");
        }
    }

    // ------------------------------------------------------------- plumbing --

    private static IEnumerable<string> ReferencedFiles()
    {
        foreach (string manifestName in new[] { "manifest.json", "manifest.firefox.json" })
        {
            string json = File.ReadAllText(Path.Combine(Extension, manifestName));
            foreach (Match match in Regex.Matches(json, @"""([\w/.-]+\.(?:js|html|png|css))"""))
                yield return match.Groups[1].Value;
        }

        foreach (string page in Directory.GetFiles(Extension, "*.html"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(page), @"(?:src|href)=""([\w/.-]+)"""))
                yield return match.Groups[1].Value;
        }
    }

    private static IReadOnlySet<string> PackagedFiles()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "browser", "package-extension.ps1"));
        var block = Regex.Match(script, @"\$include = @\((?<list>.*?)\)", RegexOptions.Singleline).Groups["list"].Value;
        return Regex.Matches(block, @"'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet();
    }

    private static string StripComments(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuickByte.sln"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("QuickByte.sln is not above the test output folder.");
    }
}
