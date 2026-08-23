namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// One reply from an FTP control connection: the three-digit code and the text
/// that came with it, already reassembled if the server sent it as a multi-line
/// block.
/// </summary>
/// <remarks>
/// The first digit is the whole protocol's error model — 1xx provisional,
/// 2xx done, 3xx more input wanted, 4xx try again later, 5xx refused — which is
/// why <see cref="IsPositive"/> and friends read the code by range rather than
/// matching the specific numbers each command happens to return.
/// </remarks>
internal readonly record struct FtpReply(int Code, string Text)
{
    public bool IsPositive => Code >= 200 && Code < 400;

    /// <summary>1xx: the server has started and will report again when it's done.</summary>
    public bool IsPreliminary => Code >= 100 && Code < 200;

    /// <summary>530 (not logged in) and 532 (account needed for storing files).</summary>
    public bool IsAuthenticationFailure => Code is 530 or 532;

    public override string ToString() => Text;
}
