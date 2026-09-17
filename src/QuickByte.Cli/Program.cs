using System.Diagnostics;
using QuickByte.Core.Commands;

namespace QuickByte.Cli;

/// <summary>
/// <c>qb</c>: QuickByte from a prompt or a script. It parses the command line,
/// hands the request to the running app over <see cref="CommandPipe"/>, and prints
/// what came back — nothing is downloaded or decided in this process, so a download
/// added here follows exactly the rules of one added in the window.
///
/// A command that adds work starts QuickByte (in the notification area) when it is
/// not running; one that only reads state reports that it is not, with its own exit
/// code, rather than starting an app to say there is nothing to show.
/// </summary>
internal static class Program
{
    private const int Done = 0;
    private const int Refused = 1;
    private const int Usage = 2;
    private const int NotRunning = 3;

    private const string AppExecutable = "QuickByte.exe";

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(3);

    private static async Task<int> Main(string[] args)
    {
        // File names are whatever servers call them; a script reading the output
        // gets them intact rather than through the console's legacy code page.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var parsed = CommandLine.Parse(args, Environment.CurrentDirectory);

        if (parsed.ShowHelp)
        {
            Console.WriteLine(CommandLine.Usage);
            return Done;
        }

        if (parsed.ShowVersion)
        {
            Console.WriteLine($"qb {FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? typeof(Program).Assembly.Location).ProductVersion}");
            return Done;
        }

        if (parsed.Error is not null || parsed.Request is null)
        {
            Console.Error.WriteLine(parsed.Error ?? "Nothing to do.");
            Console.Error.WriteLine("Run 'qb help' for the commands.");
            return Usage;
        }

        var request = parsed.Request;
        var response = await CommandPipe.SendAsync(request, TimeSpan.FromMilliseconds(400), ReplyTimeout).ConfigureAwait(false);

        if (response is null && CommandLine.StartsQuickByte(request))
        {
            if (!TryStartQuickByte(out string? problem))
            {
                Console.Error.WriteLine(problem);
                return NotRunning;
            }

            // The app has to load its downloads before it listens; give it time.
            response = await CommandPipe.SendAsync(request, TimeSpan.FromSeconds(30), ReplyTimeout).ConfigureAwait(false);
        }

        if (response is null)
        {
            Console.Error.WriteLine("QuickByte is not running.");
            return NotRunning;
        }

        string output = CommandFormatter.Format(request, response, parsed.Json);
        if (output.Length > 0) Console.WriteLine(output);
        if (!response.Ok && !parsed.Json && response.Error is { } error) Console.Error.WriteLine(error);

        return response.Ok ? Done : Refused;
    }

    /// <summary>QuickByte from beside this executable — never from PATH, which could name anything.</summary>
    private static bool TryStartQuickByte(out string? problem)
    {
        problem = null;
        string app = Path.Combine(AppContext.BaseDirectory, AppExecutable);
        if (!File.Exists(app))
        {
            problem = $"QuickByte is not running, and {app} is not there to start.";
            return false;
        }

        try
        {
            var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add("--minimized");
            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            problem = $"QuickByte is not running and could not be started: {ex.Message}";
            return false;
        }
    }
}
