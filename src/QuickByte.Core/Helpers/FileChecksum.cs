using System.Security.Cryptography;
using System.Threading;

namespace QuickByte.Core.Helpers;

public enum ChecksumAlgorithm
{
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512
}

public enum ChecksumResult
{
    /// <summary>No checksum was given, or checking was interrupted.</summary>
    NotChecked = 0,
    Match = 1,
    Mismatch = 2
}

/// <summary>A checksum a publisher gave for a file: which hash, and its value in lower-case hex.</summary>
public sealed record ExpectedChecksum(ChecksumAlgorithm Algorithm, string Hex)
{
    /// <summary>The form it is persisted in, <c>sha256:3f…</c> — the algorithm is never guessed twice.</summary>
    public override string ToString() => $"{FileChecksum.Name(Algorithm).Replace("-", "").ToLowerInvariant()}:{Hex}";
}

/// <summary>
/// Checks a finished download against the checksum its publisher lists — the
/// only way to know that what arrived is the file that was published, byte for
/// byte, and not a truncated, corrupted or substituted one.
///
/// It reads the forms people actually paste: a bare hex string (whose length
/// says which hash it is), a <c>sha256:</c>-style prefix, a line of
/// <c>sha256sum</c> output with the file name after it, or the BSD
/// <c>SHA256 (file) = …</c> form.
/// </summary>
/// <remarks>
/// MD5 and SHA-1 are accepted because they are still what many download pages
/// list. They detect a damaged download perfectly well; they are no defence
/// against a file someone deliberately forged to match, and the window that
/// takes the value says so.
/// </remarks>
public static class FileChecksum
{
    private const int BufferSize = 1024 * 1024;
    private const long ProgressIntervalBytes = 16L * 1024 * 1024;

    public static string Name(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Md5 => "MD5",
        ChecksumAlgorithm.Sha1 => "SHA-1",
        ChecksumAlgorithm.Sha256 => "SHA-256",
        ChecksumAlgorithm.Sha384 => "SHA-384",
        _ => "SHA-512"
    };

    /// <summary>Whether the hash is too weak to stand against a deliberately forged file.</summary>
    public static bool IsWeak(ChecksumAlgorithm algorithm) => algorithm is ChecksumAlgorithm.Md5 or ChecksumAlgorithm.Sha1;

    /// <summary>The checksum in <paramref name="text"/>, or null when there is none to find.</summary>
    public static ExpectedChecksum? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string value = text.Trim();

        // A prefix names the algorithm outright: "sha256:…", "SHA-256=…", "md5 …".
        ChecksumAlgorithm? named = null;
        foreach (var (prefix, algorithm) in Prefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = value[prefix.Length..];
            if (rest.Length == 0 || !(rest[0] is ':' or '=' or ' ' or '(')) continue;

            named = algorithm;
            value = rest;
            break;
        }

        // What is left may still carry a file name (sha256sum's "hex  name", BSD's
        // "(name) = hex"): the checksum is whichever token is all hex.
        string? hex = value
            .Split(new[] { ' ', '\t', '=', ':', '*', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(IsHexHash);
        if (hex is null) return null;

        var byLength = AlgorithmForLength(hex.Length);
        if (byLength is null) return null;
        if (named is not null && named != byLength) return null;

        return new ExpectedChecksum(byLength.Value, hex.ToLowerInvariant());
    }

    /// <summary>The persisted form, <c>sha256:…</c>; null for anything else.</summary>
    public static ExpectedChecksum? FromStored(string? stored) => Parse(stored);

    /// <summary>Hashes a file, reporting bytes read.</summary>
    public static async Task<string> ComputeAsync(
        string path, ChecksumAlgorithm algorithm, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashName(algorithm));
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        long read = 0, lastReported = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, count);
            read += count;
            if (read - lastReported >= ProgressIntervalBytes)
            {
                lastReported = read;
                progress?.Report(read);
            }
        }

        progress?.Report(read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Checks <paramref name="path"/> against <paramref name="expected"/>.</summary>
    public static async Task<ChecksumResult> VerifyAsync(
        string path, ExpectedChecksum expected, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        string actual = await ComputeAsync(path, expected.Algorithm, progress, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expected.Hex, StringComparison.OrdinalIgnoreCase)
            ? ChecksumResult.Match
            : ChecksumResult.Mismatch;
    }

    private static readonly (string Prefix, ChecksumAlgorithm Algorithm)[] Prefixes =
    {
        ("sha-256", ChecksumAlgorithm.Sha256), ("sha256", ChecksumAlgorithm.Sha256),
        ("sha-384", ChecksumAlgorithm.Sha384), ("sha384", ChecksumAlgorithm.Sha384),
        ("sha-512", ChecksumAlgorithm.Sha512), ("sha512", ChecksumAlgorithm.Sha512),
        ("sha-1", ChecksumAlgorithm.Sha1), ("sha1", ChecksumAlgorithm.Sha1),
        ("md5", ChecksumAlgorithm.Md5)
    };

    private static ChecksumAlgorithm? AlgorithmForLength(int hexLength) => hexLength switch
    {
        32 => ChecksumAlgorithm.Md5,
        40 => ChecksumAlgorithm.Sha1,
        64 => ChecksumAlgorithm.Sha256,
        96 => ChecksumAlgorithm.Sha384,
        128 => ChecksumAlgorithm.Sha512,
        _ => null
    };

    private static bool IsHexHash(string token) =>
        AlgorithmForLength(token.Length) is not null && token.All(char.IsAsciiHexDigit);

    private static HashAlgorithmName HashName(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
        ChecksumAlgorithm.Sha1 => HashAlgorithmName.SHA1,
        ChecksumAlgorithm.Sha256 => HashAlgorithmName.SHA256,
        ChecksumAlgorithm.Sha384 => HashAlgorithmName.SHA384,
        _ => HashAlgorithmName.SHA512
    };
}
