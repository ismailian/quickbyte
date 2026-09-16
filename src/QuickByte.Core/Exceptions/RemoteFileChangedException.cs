namespace QuickByte.Core.Exceptions;

/// <summary>
/// A connection asked for the rest of a file with <c>If-Range</c> and the server
/// answered with a whole body instead — which is how HTTP says the file is no
/// longer the version the chunks on disk came from.
///
/// Not an ordinary failure, and deliberately not retried by
/// <see cref="Helpers.RetryPolicy"/>: asking again gets the same answer, and the
/// fix is not in this connection at all. Every chunk of the download belongs to
/// the old version, so <see cref="Services.ConnectionPoolManager"/> stops the
/// siblings and <see cref="Services.DownloadService"/> decides, with a fresh
/// probe, whether to start over.
/// </summary>
public sealed class RemoteFileChangedException : IOException
{
    public RemoteFileChangedException(string message) : base(message) { }
}
