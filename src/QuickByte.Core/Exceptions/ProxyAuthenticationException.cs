namespace QuickByte.Core.Exceptions;

/// <summary>
/// The proxy — not the server a download comes from — wants a user name and
/// password, or refused the ones in Options.
///
/// Kept apart from <see cref="AuthenticationRequiredException"/> because the two
/// are answered in different places: that one opens a sign-in prompt for the
/// download's own server, and a proxy login typed into it would be sent to the
/// wrong party. Not retried, for the reason a wrong password never is.
/// </summary>
public sealed class ProxyAuthenticationException : IOException
{
    public const string NeededMessage =
        "The proxy needs a user name and password. Enter them in Options > Proxy.";

    public const string RejectedMessage =
        "The proxy rejected its user name or password. Check them in Options > Proxy.";

    public ProxyAuthenticationException(string message) : base(message) { }
}
