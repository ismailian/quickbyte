using System.Globalization;

namespace QuickByte.Core.Commands;

/// <summary>What a command line asked for: a request to send, help, the version, or a usage mistake.</summary>
public sealed record ParsedCommandLine(CommandRequest? Request, bool ShowHelp, bool ShowVersion, bool Json, string? Error)
{
    public static ParsedCommandLine Help() => new(null, true, false, false, null);
    public static ParsedCommandLine Mistake(string error) => new(null, false, false, false, error);
}

/// <summary>
/// Reads <c>qb</c>'s arguments. Pure — the working directory is a parameter — so the
/// grammar is tested rather than discovered: which switch takes a value, what a
/// relative folder means, which combinations make no sense.
/// </summary>
public static class CommandLine
{
    public const string Usage =
        """
        qb - control QuickByte from a prompt or a script.

        Usage:
          qb add <url> [<url>...] [options]   Add downloads (QuickByte is started if needed)
              -o, --folder <folder>    Save into this folder (default: as Options says)
              -n, --name <file>        File name, for a single link
              -c, --connections <n>    Connections per download (1-32)
              -q, --queue <name>       Put them in this queue (created if missing) instead of starting them
                  --paused             Add them without starting
                  --checksum <hash>    SHA-256 (or SHA-1/MD5) the file must match
                  --quality <q>        Streaming video: best (default), worst, or part of a label ("720p")
          qb list [--status active|paused|queued|completed|failed|all] [--json]
          qb pause <id|all>                   Pause a download (the first characters of its id are enough)
          qb resume <id|all>
          qb queues [--json]                  List the queues
          qb queue start <name>
          qb queue stop <name>
          qb status [--json]
          qb help | qb --version

        Exit codes: 0 done, 1 QuickByte refused or failed, 2 bad command line, 3 QuickByte not running.
        """;

    private static readonly string[] Filters = { "active", "paused", "queued", "completed", "failed", "all" };

    public static ParsedCommandLine Parse(IReadOnlyList<string> args, string workingDirectory)
    {
        if (args.Count == 0) return ParsedCommandLine.Help();

        string verb = args[0].ToLowerInvariant();
        if (verb is "help" or "-h" or "--help" or "/?") return ParsedCommandLine.Help();
        if (verb is "version" or "--version" or "-v") return new ParsedCommandLine(null, false, true, false, null);

        var rest = args.Skip(1).ToList();
        bool json = rest.RemoveAll(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase)) > 0;

        var parsed = verb switch
        {
            "add" => ParseAdd(rest, workingDirectory),
            "list" or "ls" => ParseList(rest),
            "pause" => ParseTarget(CommandVerb.Pause, rest),
            "resume" => ParseTarget(CommandVerb.Resume, rest),
            "queues" => NoArguments(CommandVerb.Queues, rest),
            "queue" => ParseQueue(rest),
            "status" => NoArguments(CommandVerb.Status, rest),
            _ => ParsedCommandLine.Mistake($"'{args[0]}' is not a qb command. Run 'qb help' to see them.")
        };

        return parsed with { Json = json };
    }

    private static ParsedCommandLine ParseAdd(List<string> args, string workingDirectory)
    {
        var request = new CommandRequest { Verb = CommandVerb.Add };

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith('-'))
            {
                request.Urls.Add(arg.Trim().Trim('"'));
                continue;
            }

            string name = arg.ToLowerInvariant();
            if (name == "--paused")
            {
                request.Paused = true;
                continue;
            }

            if (name is not ("-o" or "--folder" or "-n" or "--name" or "-c" or "--connections" or "-q" or "--queue" or "--checksum" or "--quality"))
                return ParsedCommandLine.Mistake($"'{arg}' is not an option of 'qb add'.");

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                return ParsedCommandLine.Mistake($"'{arg}' needs a value.");
            string value = args[++i].Trim();

            switch (name)
            {
                case "-o" or "--folder":
                    try
                    {
                        // Relative to where qb was run, which only this side of the pipe knows.
                        request.Folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim('"')), workingDirectory);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        return ParsedCommandLine.Mistake($"'{value}' is not a folder path.");
                    }
                    break;
                case "-n" or "--name":
                    request.FileName = value.Trim('"');
                    break;
                case "-c" or "--connections":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int connections) || connections is < 1 or > 32)
                        return ParsedCommandLine.Mistake("--connections takes a number from 1 to 32.");
                    request.Connections = connections;
                    break;
                case "-q" or "--queue":
                    request.Queue = value.Trim('"');
                    break;
                case "--checksum":
                    request.Checksum = value;
                    break;
                default:
                    request.Quality = value.Trim('"');
                    break;
            }
        }

        if (request.Urls.Count == 0) return ParsedCommandLine.Mistake("'qb add' needs at least one link.");
        if (request.FileName is not null && request.Urls.Count > 1)
            return ParsedCommandLine.Mistake("--name names one file; give one link with it.");
        if (request.Checksum is not null && request.Urls.Count > 1)
            return ParsedCommandLine.Mistake("--checksum belongs to one file; give one link with it.");
        if (request.Queue is not null && request.Paused)
            return ParsedCommandLine.Mistake("A download added to a queue waits for the queue already; --paused is not needed.");

        return new ParsedCommandLine(request, false, false, false, null);
    }

    private static ParsedCommandLine ParseList(List<string> args)
    {
        var request = new CommandRequest { Verb = CommandVerb.List, Filter = "all" };
        for (int i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals("--status", StringComparison.OrdinalIgnoreCase))
                return ParsedCommandLine.Mistake($"'{args[i]}' is not an option of 'qb list'.");
            if (i + 1 >= args.Count || !Filters.Contains(args[i + 1].ToLowerInvariant()))
                return ParsedCommandLine.Mistake($"--status takes one of: {string.Join(", ", Filters)}.");
            request.Filter = args[++i].ToLowerInvariant();
        }
        return new ParsedCommandLine(request, false, false, false, null);
    }

    private static ParsedCommandLine ParseTarget(CommandVerb verb, List<string> args)
    {
        string name = verb.ToString().ToLowerInvariant();
        if (args.Count != 1) return ParsedCommandLine.Mistake($"'qb {name}' takes one download id, or 'all'.");
        return new ParsedCommandLine(new CommandRequest { Verb = verb, Target = args[0].Trim() }, false, false, false, null);
    }

    private static ParsedCommandLine ParseQueue(List<string> args)
    {
        if (args.Count < 2 || args[0].ToLowerInvariant() is not ("start" or "stop"))
            return ParsedCommandLine.Mistake("Use 'qb queue start <name>' or 'qb queue stop <name>'.");

        var verb = args[0].Equals("start", StringComparison.OrdinalIgnoreCase) ? CommandVerb.QueueStart : CommandVerb.QueueStop;
        // A name with spaces may arrive as several arguments when it was not quoted.
        string queue = string.Join(' ', args.Skip(1)).Trim().Trim('"');
        return new ParsedCommandLine(new CommandRequest { Verb = verb, Queue = queue }, false, false, false, null);
    }

    private static ParsedCommandLine NoArguments(CommandVerb verb, List<string> args) =>
        args.Count == 0
            ? new ParsedCommandLine(new CommandRequest { Verb = verb }, false, false, false, null)
            : ParsedCommandLine.Mistake($"'qb {verb.ToString().ToLowerInvariant()}' takes no arguments.");

    /// <summary>Whether a request needs QuickByte started when it is not running. Reading state does not.</summary>
    public static bool StartsQuickByte(CommandRequest request) =>
        request.Verb is CommandVerb.Add or CommandVerb.QueueStart;
}
