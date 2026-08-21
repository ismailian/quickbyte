using System.Threading;
using System.Net.Http;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Resolves remote file metadata. Tries a HEAD request first (cheap); if the
/// server doesn't answer HEAD usefully, falls back to a ranged GET probe
/// (bytes=0-0) which also lets us positively confirm Range support via a
/// 206 Partial Content response.
/// </summary>
public sealed class RemoteFileInfoProvider : IRemoteFileInfoProvider
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default)
    {
        var info = new RemoteFileInfo { FinalUrl = url };

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await Client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (headResponse.IsSuccessStatusCode)
            {
                PopulateFromHeaders(info, headResponse);
                if (info.HasKnownSize) return info;
            }
        }
        catch
        {
            // Some servers reject HEAD entirely — fall through to a ranged GET probe.
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
        getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var getResponse = await Client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        getResponse.EnsureSuccessStatusCode();

        info.SupportsRangeRequests = getResponse.StatusCode == System.Net.HttpStatusCode.PartialContent;
        PopulateFromHeaders(info, getResponse, preferContentRangeTotal: true);

        return info;
    }

    private static void PopulateFromHeaders(RemoteFileInfo info, HttpResponseMessage response, bool preferContentRangeTotal = false)
    {
        if (response.Headers.AcceptRanges.Contains("bytes"))
            info.SupportsRangeRequests = true;

        if (preferContentRangeTotal && response.Content.Headers.ContentRange?.Length is long total)
            info.ContentLength = total;
        else if (response.Content.Headers.ContentLength is long len && len > 0)
            info.ContentLength = len;

        info.ContentType = response.Content.Headers.ContentType?.MediaType ?? info.ContentType;
        info.ETag = response.Headers.ETag?.Tag;
        info.LastModified = response.Content.Headers.LastModified;

        string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                            ?? response.Content.Headers.ContentDisposition?.FileName;
        fileName = fileName?.Trim('"');

        info.FileName = !string.IsNullOrWhiteSpace(fileName)
            ? FileNameHelper.SanitizeFileName(fileName)
            : FileNameHelper.FileNameFromUrl(info.FinalUrl);

        if (response.RequestMessage?.RequestUri is not null)
            info.FinalUrl = response.RequestMessage.RequestUri.ToString();
    }
}
