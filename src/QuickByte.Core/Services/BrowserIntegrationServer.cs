using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// A very small HTTP server on 127.0.0.1 that the browser extension posts
/// captured downloads to. Two routes: <c>GET /ping</c> (so the extension can
/// show whether QuickByte is running and correctly paired) and
/// <c>POST /download</c> (a link to take over).
///
/// Written on <see cref="TcpListener"/> rather than <see cref="HttpListener"/>.
/// HttpListener goes through http.sys, whose URL reservations are an
/// administrator-level concept — the prefixes that work without one are a
/// Windows implementation detail, and a download manager that needs an elevated
/// prompt to talk to a browser extension is not shippable. A socket bound to
/// loopback needs no permission from anyone.
///
/// Three things guard it, because anything running on the machine — including
/// every web page in the browser — can reach a loopback port:
/// <list type="bullet">
/// <item><b>The bind address.</b> <see cref="IPAddress.Loopback"/>, so nothing
/// off-machine can even connect.</item>
/// <item><b>The token.</b> Every request must carry
/// <c>X-QuickByte-Token</c>, compared in fixed time. A page can send a request
/// it cannot read the answer to, so the secret has to be in the request.</item>
/// <item><b>The origin.</b> CORS headers are only issued to
/// <c>chrome-extension://</c> and <c>moz-extension://</c> origins, so an
/// ordinary page cannot complete the preflight that a JSON POST requires.</item>
/// </list>
/// </summary>
public sealed class BrowserIntegrationServer : IBrowserIntegrationService
{
    /// <summary>Caps on what one request may send, so a stray client can't exhaust memory.</summary>
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>A pairing request that stalls holding a socket open is not worth waiting on.</summary>
    private const int RequestTimeoutMilliseconds = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ISettingsService _settingsService;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _activePort;

    public event EventHandler<CapturedDownload>? DownloadCaptured;
    public event EventHandler? StatusChanged;

    public bool IsRunning => _listener is not null;
    public int Port => IsRunning ? _activePort : _settingsService.Current.BrowserIntegrationPort;
    public string? LastError { get; private set; }

    public BrowserIntegrationServer(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        // The second subscriber to SettingsChanged, and for the same reason as
        // the first (the global speed limit): a bridge you have to restart the
        // app to enable is a bridge the user will assume is broken.
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    public string Token
    {
        get
        {
            var settings = _settingsService.Current;
            if (!string.IsNullOrEmpty(settings.BrowserIntegrationToken)) return settings.BrowserIntegrationToken;

            // Generated lazily and mutated in place before saving, the way
            // DownloadCompleteForm's checkbox does: Save() persists whatever
            // object it is handed, so building a fresh DownloadSettings here
            // would quietly reset every field this class doesn't know about.
            settings.BrowserIntegrationToken = NewToken();
            _settingsService.Save(settings);
            return settings.BrowserIntegrationToken;
        }
    }

    public string RegenerateToken()
    {
        var settings = _settingsService.Current;
        settings.BrowserIntegrationToken = NewToken();
        _settingsService.Save(settings);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return settings.BrowserIntegrationToken;
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
               .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // ------------------------------------------------------------ lifecycle --

    public void Start()
    {
        if (!_settingsService.Current.BrowserIntegrationEnabled) return;

        // Minted here, on the caller's thread, rather than lazily inside a
        // request handler: reading Token can persist a new one, and writing
        // settings.json from an accept-loop task would race the UI thread doing
        // the same. Outside the lock because that save re-enters this class.
        _ = Token;

        lock (_sync)
        {
            if (_listener is not null) return;

            int port = _settingsService.Current.BrowserIntegrationPort;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();

                _listener = listener;
                _activePort = ((IPEndPoint)listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                LastError = null;

                _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            }
            catch (SocketException ex)
            {
                // Almost always "port already in use" — another copy of a
                // download manager, or something else squatting the port. Recorded
                // rather than thrown: the app is perfectly usable without the
                // bridge, and Options shows the reason.
                LastError = ex.Message;
                _listener = null;
            }
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_listener is null) return;

            _cts?.Cancel();
            try { _listener.Stop(); } catch { /* best-effort */ }
            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSettingsChanged(object? sender, DownloadSettings settings)
    {
        bool shouldRun = settings.BrowserIntegrationEnabled;
        bool portMoved = IsRunning && _activePort != settings.BrowserIntegrationPort;

        if (IsRunning && (!shouldRun || portMoved)) Stop();
        if (shouldRun && !IsRunning) Start();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return; // Stop() pulled the listener out from under us.
            }
            catch
            {
                continue; // One bad accept must not take the bridge down.
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    // -------------------------------------------------------------- request --

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeoutMilliseconds);

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request is null) return;

                string response = BuildResponse(request);
                byte[] bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
                await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // A half-spoken request, a client that hung up, a timeout. None
                // of it is worth surfacing — the extension retries by simply
                // capturing the next download.
            }
        }
    }

    private sealed record ParsedRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

    private static async Task<ParsedRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        int filled = 0;
        int headerEnd = -1;

        // Read until the blank line that ends the header block. The body usually
        // arrives in the same packet, so whatever came with it is kept and
        // counted rather than re-read.
        while (headerEnd < 0)
        {
            if (filled == buffer.Length) return null; // headers over the cap

            int read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return null;
            filled += read;

            headerEnd = IndexOfHeaderTerminator(buffer, filled);
        }

        string headerText = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int bodyStart = headerEnd + 4;
        int contentLength = headers.TryGetValue("Content-Length", out string? raw) && int.TryParse(raw, out int length)
            ? Math.Clamp(length, 0, MaxBodyBytes)
            : 0;

        var body = new MemoryStream(contentLength);
        int carried = Math.Min(contentLength, filled - bodyStart);
        if (carried > 0) body.Write(buffer, bodyStart, carried);

        while (body.Length < contentLength)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, contentLength - body.Length)), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0) break;
            body.Write(buffer, 0, read);
        }

        return new ParsedRequest(
            requestLine[0].ToUpperInvariant(),
            requestLine[1].Split('?')[0],
            headers,
            Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
    }

    private static int IndexOfHeaderTerminator(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n' &&
                buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                return i;
        }
        return -1;
    }

    // ------------------------------------------------------------- routing --

    private string BuildResponse(ParsedRequest request)
    {
        string origin = request.Headers.GetValueOrDefault("Origin", string.Empty);

        // Answered before the token check: a preflight is not allowed to carry
        // custom headers, so it could never present one.
        if (request.Method == "OPTIONS") return Respond(204, string.Empty, origin);

        if (!IsTokenValid(request.Headers.GetValueOrDefault("X-QuickByte-Token")))
            return Respond(401, """{"ok":false,"error":"pairing token missing or wrong"}""", origin);

        return request.Path switch
        {
            "/ping" when request.Method == "GET" => Respond(200, $$"""{"ok":true,"app":"QuickByte","version":"{{AssemblyVersion}}"}""", origin),
            "/download" when request.Method == "POST" => Capture(request.Body, origin),
            _ => Respond(404, """{"ok":false,"error":"no such route"}""", origin)
        };
    }

    private bool IsTokenValid(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        // Fixed-time: the token is a bearer secret on a port every process on the
        // machine can reach, so it must not be discoverable one byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(Token));
    }

    private string Capture(string body, string origin)
    {
        CaptureRequest? payload;
        try { payload = JsonSerializer.Deserialize<CaptureRequest>(body, JsonOptions); }
        catch { payload = null; }

        if (payload?.Url is null || !Uri.TryCreate(payload.Url, UriKind.Absolute, out var uri))
            return Respond(400, """{"ok":false,"error":"a valid absolute url is required"}""", origin);

        // Whitelisted rather than blacklisted: the URL ends up being fetched, and
        // file:// or a custom scheme has no business arriving over this bridge.
        if (uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
            return Respond(400, """{"ok":false,"error":"unsupported url scheme"}""", origin);

        DownloadCaptured?.Invoke(this, new CapturedDownload
        {
            Url = payload.Url,
            FileName = payload.FileName,
            TotalBytes = Math.Max(0, payload.FileSize),
            MimeType = payload.MimeType,
            Referrer = payload.Referrer,
            UserAgent = payload.UserAgent,
            Cookie = payload.Cookie
        });

        return Respond(200, """{"ok":true}""", origin);
    }

    private static string Respond(int status, string json, string origin)
    {
        string reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            _ => "Not Found"
        };

        var response = new StringBuilder();
        response.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        response.Append("Content-Type: application/json; charset=utf-8\r\n");
        response.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(json)).Append("\r\n");
        response.Append("Cache-Control: no-store\r\n");

        // CORS only for browser extensions. Without a matching allow-origin the
        // preflight fails, and a JSON POST from an ordinary page never gets sent
        // at all — which is the whole defence against a web page driving this.
        if (IsExtensionOrigin(origin))
        {
            response.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
            response.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            response.Append("Access-Control-Allow-Headers: Content-Type, X-QuickByte-Token\r\n");
            response.Append("Access-Control-Max-Age: 600\r\n");
        }

        // Every response closes its connection: there is no keep-alive bookkeeping
        // here, and a capture is one request every few minutes at most.
        response.Append("Connection: close\r\n\r\n");
        response.Append(json);
        return response.ToString();
    }

    private static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);

    private static string AssemblyVersion =>
        typeof(BrowserIntegrationServer).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>Wire shape of a <c>POST /download</c> body.</summary>
    private sealed record CaptureRequest
    {
        public string? Url { get; init; }
        public string? FileName { get; init; }
        public long FileSize { get; init; }
        public string? MimeType { get; init; }
        public string? Referrer { get; init; }
        public string? UserAgent { get; init; }
        public string? Cookie { get; init; }
    }

    public void Dispose()
    {
        _settingsService.SettingsChanged -= OnSettingsChanged;
        Stop();
    }
}
