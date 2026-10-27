namespace QuickByte.Core.Helpers;

/// <summary>
/// The <c>User-Agent</c> every HTTP request the engine makes presents: the one
/// the user typed into Options, when they typed a usable one, else QuickByte's
/// own.
///
/// <para>It exists because the header is not cosmetic. Plenty of servers refuse
/// a client they do not recognise — a <c>403</c>, a redirect to a landing page,
/// or an HTML error body served with a <c>200</c> — and none of those is a
/// failure QuickByte can diagnose, so the only fix is to let the user say what
/// to claim to be.</para>
///
/// <para>A live static, for the reason <see cref="Services.Proxy.AppProxy"/> is
/// one: the engine's <see cref="HttpClient"/>s are static and built once, so a
/// User-Agent that needed a restart to apply is one the user would reasonably
/// assume is broken. Unlike the proxy, though, it is never written onto a
/// client's <c>DefaultRequestHeaders</c> — see below.</para>
/// </summary>
/// <remarks>
/// <para><b>Why per-request, and not on the clients.</b>
/// <c>HttpRequestHeaders</c> is not safe to mutate while a request is in flight,
/// and the download client is shared by up to
/// <see cref="Models.DownloadSettings.MaxConnections"/> connections per download
/// across every running download. Re-stamping its defaults when the setting
/// changes — or, worse, each time a connection is built — races every send on it.
/// So <see cref="Current"/> is an immutable string swapped atomically, and
/// <see cref="HttpRequestDecorator"/> writes it onto each request as it is built.
/// A request header also takes precedence over the client's default, which is
/// what makes the two mechanisms coexist.</para>
///
/// <para><b>Why the value is not validated into oblivion.</b> Only the rules that
/// have to hold are enforced (see <see cref="Normalize"/>): a header value cannot
/// carry a control character, and a megabyte-long one is a mistake rather than a
/// preference. Beyond that the string is sent exactly as typed, through
/// <c>TryAddWithoutValidation</c> — the point of the setting is to impersonate
/// whatever a server will accept, and <see cref="HttpClient"/>'s own parsing is
/// stricter than what real servers send and accept. The decorator passes captured
/// browser headers through for the same reason.</para>
/// </remarks>
public static class AppUserAgent
{
    /// <summary>
    /// What QuickByte calls itself when the user has not chosen otherwise. The
    /// engine's shared clients also carry this on their
    /// <c>DefaultRequestHeaders</c>, as a backstop for any request path that does
    /// not go through <see cref="HttpRequestDecorator"/>.
    /// </summary>
    public const string Default = "QuickByte/1.0 (+https://github.com/ismailian/quickbyte)";

    /// <summary>
    /// Longest custom value accepted. Generous next to the ~120 characters a real
    /// browser sends, and small enough that a pasted accident cannot push a
    /// request header into the size a server will refuse the whole request over.
    /// </summary>
    public const int MaxLength = 512;

    private static volatile string _current = Default;

    /// <summary>
    /// The User-Agent to present right now. Never null, never empty: a custom
    /// value that does not survive <see cref="Normalize"/> leaves
    /// <see cref="Default"/> in place rather than sending nothing.
    /// </summary>
    public static string Current => _current;

    /// <summary>True when <see cref="Current"/> is the user's value, not ours.</summary>
    public static bool IsCustom => !ReferenceEquals(_current, Default) && _current != Default;

    /// <summary>
    /// Points <see cref="Current"/> at <paramref name="custom"/>, or back at
    /// <see cref="Default"/> when it is empty or unusable. Called by the
    /// composition root at start-up and on every settings change — including the
    /// change that clears the field, which is why this has to handle null by
    /// restoring the default rather than by doing nothing.
    /// </summary>
    public static void Configure(string? custom) => _current = Normalize(custom) ?? Default;

    /// <summary>
    /// <paramref name="value"/> trimmed, or null when it cannot be sent as a
    /// header value: empty, longer than <see cref="MaxLength"/>, or carrying a
    /// control character. That last one is the load-bearing check — a stray CR or
    /// LF in a header value is request splitting, and this value is persisted in
    /// <c>settings.json</c>, which a user may well have edited by hand.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength) return null;

        foreach (char c in trimmed)
        {
            if (char.IsControl(c)) return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is one Options can accept. Blank counts:
    /// it is how the user asks for QuickByte's own.
    /// </summary>
    public static bool IsAcceptable(string? value) =>
        string.IsNullOrWhiteSpace(value) || Normalize(value) is not null;
}
