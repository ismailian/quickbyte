namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// The server refused <c>REST</c>, so a transfer cannot be started at a byte
/// offset. Distinct from a generic failure because it has a specific remedy the
/// connection applies itself: throw away the partial chunk and fetch the file
/// from the beginning. Retrying the same offset would fail identically forever.
/// </summary>
internal sealed class FtpRestartNotSupportedException : Exception
{
    public FtpRestartNotSupportedException(string message) : base(message) { }
}
