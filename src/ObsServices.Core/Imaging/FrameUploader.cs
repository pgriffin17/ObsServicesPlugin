using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Imaging;

public enum UploadStatus {
    /// <summary>The service verified the frame.</summary>
    Received,
    /// <summary><c>HEAD</c> gave 404: never announced, or no grant.</summary>
    Missing,
    /// <summary>A 4xx the service will keep answering; <see cref="UploadResult.Error"/> names it.</summary>
    Refused,
}

/// <summary>How <see cref="FrameUploader.UploadAsync"/> ended. <c>Http</c> is the last status, <c>Sent</c> the bytes this call pushed, <c>Offset</c> what the service holds.</summary>
public sealed record UploadResult(UploadStatus Status, int Http, string? Error = null, long Sent = 0, long Offset = 0) {
    public bool Ok => Status == UploadStatus.Received;
}

/// <summary>
/// One resumable upload against a grant, as <c>docs/IMAGES.md</c> has it: <c>HEAD {upload_url}{hash}</c> for
/// the committed offset, then <c>PATCH</c> chunks of <c>max_chunk</c> bytes with <c>Content-Range</c>
/// (202 → next chunk, 409 → resume from the offset in the body, 201 → received). Transport errors and
/// 5xx answers throw <see cref="HttpRequestException"/> for the caller to retry; every other outcome is an
/// <see cref="UploadResult"/>.
/// </summary>
public static class FrameUploader {
    /// <summary>Upload <paramref name="data"/> (seekable) as the frame <paramref name="hash"/>. <paramref name="onChunk"/> is told the bytes of each accepted chunk.</summary>
    public static async Task<UploadResult> UploadAsync(HttpClient http, ImageGrant grant, string hash, Stream data, Action<long>? onChunk = null, CancellationToken ct = default) {
        var url = (grant.UploadUrl.EndsWith('/') ? grant.UploadUrl : grant.UploadUrl + "/") + hash;
        var total = data.Length;
        var maxChunk = (int)Math.Clamp(grant.MaxChunk > 0 ? grant.MaxChunk : Images.MaxChunk, 1, Images.MaxChunk);

        using (var head = new HttpRequestMessage(HttpMethod.Head, url)) {
            head.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
            using var r = await http.SendAsync(head, ct).ConfigureAwait(false);

            if (r.StatusCode == HttpStatusCode.NotFound) {
                return new UploadResult(UploadStatus.Missing, 404);
            }

            if ((int)r.StatusCode >= 500) {
                throw new HttpRequestException($"HEAD {url}: {(int)r.StatusCode}");
            }

            if ((int)r.StatusCode >= 400) {
                return new UploadResult(UploadStatus.Refused, (int)r.StatusCode, await ErrorOf(r).ConfigureAwait(false));
            }

            var committed = r.Headers.TryGetValues("Upload-Offset", out var values) ? values.FirstOrDefault() : null;
            long.TryParse(committed, out var offset0);
            var offset = offset0;
            long sent = 0;
            var conflicts = 0;
            var buffer = new byte[Math.Min(maxChunk, (int)Math.Max(1, Math.Min(total, int.MaxValue)))];

            while (offset < total) {
                var n = (int)Math.Min(buffer.Length, total - offset);
                data.Seek(offset, SeekOrigin.Begin);
                await ReadExactlyAsync(data, buffer, n, ct).ConfigureAwait(false);
                var started = DateTime.UtcNow;

                using var patch = new HttpRequestMessage(HttpMethod.Patch, url);
                patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
                var content = new ByteArrayContent(buffer, 0, n);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + n - 1, total);
                patch.Content = content;
                using var response = await http.SendAsync(patch, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (status is 201 or 202) {
                    sent += n;
                    onChunk?.Invoke(n);

                    if (status == 201) {
                        return new UploadResult(UploadStatus.Received, 201, null, sent, total);
                    }

                    offset = (await OffsetOf(response).ConfigureAwait(false)) ?? offset + n;
                    conflicts = 0;

                    if (grant.RateBps is { } rate && rate > 0) {
                        // pace to the cap: a chunk of n bytes is owed n / rate seconds
                        var owed = TimeSpan.FromSeconds((double)n / rate) - (DateTime.UtcNow - started);

                        if (owed > TimeSpan.Zero) {
                            await Task.Delay(owed, ct).ConfigureAwait(false);
                        }
                    }
                } else if (status == 409) {
                    if (++conflicts > 5) {
                        return new UploadResult(UploadStatus.Refused, 409, "the service keeps disagreeing about the offset", sent, offset);
                    }

                    offset = (await OffsetOf(response).ConfigureAwait(false)) ?? 0;
                } else if (status >= 500) {
                    throw new HttpRequestException($"PATCH {url}: {status}");
                } else {
                    return new UploadResult(UploadStatus.Refused, status, await ErrorOf(response).ConfigureAwait(false), sent, offset);
                }
            }

            // nothing left to send: the service already committed every byte of this frame
            return new UploadResult(UploadStatus.Received, 200, null, sent, total);
        }
    }

    private static async Task ReadExactlyAsync(Stream s, byte[] buffer, int n, CancellationToken ct) {
        var read = 0;

        while (read < n) {
            var got = await s.ReadAsync(buffer.AsMemory(read, n - read), ct).ConfigureAwait(false);

            if (got == 0) {
                throw new IOException("the spooled file is shorter than announced");
            }

            read += got;
        }
    }

    private static async Task<long?> OffsetOf(HttpResponseMessage r) {
        try {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : null;
        } catch (Exception) {
            return null;
        }
    }

    private static async Task<string> ErrorOf(HttpResponseMessage r) {
        var text = await r.Content.ReadAsStringAsync().ConfigureAwait(false);

        try {
            using var doc = JsonDocument.Parse(text);

            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) {
                return e.GetString()!;
            }
        } catch (JsonException) {
        }

        return text.Length == 0 ? $"HTTP {(int)r.StatusCode}" : text[..Math.Min(200, text.Length)];
    }
}
