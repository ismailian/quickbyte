namespace QuickByte.Core.Exceptions;

/// <summary>
/// A streaming video that cannot be downloaded, and never will be by asking
/// again: a live stream, which has no end to download to, or one whose content is
/// protected by DRM, whose pieces are useless without a licence a player obtains.
///
/// Its own type so the Add window can show the message as the answer rather than
/// as a failure, and so <see cref="Helpers.RetryPolicy"/> does not spend its
/// attempts on it.
/// </summary>
public sealed class StreamNotSupportedException : Exception
{
    public StreamNotSupportedException(string message) : base(message) { }
}
