namespace QuickByte.Core.Exceptions;

/// <summary>
/// Thrown when a server refuses a request for want of credentials — an HTTP
/// <c>401</c>, or an FTP <c>530</c> — rather than for any of the ordinary
/// reasons a request fails.
///
/// It is a distinct type because the two cases call for opposite handling. A
/// timeout or a 500 is worth retrying; a missing password will fail identically
/// forever, so <see cref="Helpers.RetryPolicy"/> must not burn its attempts on
/// it, and the Add Download dialog has to tell the user what to do about it
/// instead of reporting a generic failure.
/// </summary>
public sealed class AuthenticationRequiredException : Exception
{
    public AuthenticationRequiredException(string message) : base(message) { }

    public AuthenticationRequiredException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>True once credentials were supplied and still rejected.</summary>
    public bool CredentialsWereSupplied { get; init; }
}
