using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Orders.Console.Integrations.GitHub;

/// <summary>
/// Conditional GETs for the GitHub API: remembers each response's ETag and body, sends If-None-Match,
/// and turns a 304 back into the cached 200. 304s do not count against GitHub's rate limit.
/// </summary>
public sealed class ETagCacheHandler(ETagCache store) : DelegatingHandler
{
    private const int MaxEntries = 500;
    private readonly ConcurrentDictionary<string, ETagCache.Entry> _cache = store.Entries;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var key = request.RequestUri.AbsoluteUri;
        if (_cache.TryGetValue(key, out var cached))
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cached.ETag));
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = cached.ToContent() };
        }

        if (response.IsSuccessStatusCode && response.Headers.ETag is { } etag)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (_cache.Count >= MaxEntries)
            {
                _cache.Clear();
            }

            var entry = new ETagCache.Entry(etag.ToString(), body, response.Content.Headers.ContentType?.ToString());
            _cache[key] = entry;
            response.Content = entry.ToContent();
        }

        return response;
    }
}

/// <summary>Singleton store so cached bodies survive HttpClientFactory handler rotation.</summary>
public sealed class ETagCache
{
    public ConcurrentDictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

    public sealed record Entry(string ETag, byte[] Body, string? MediaType)
    {
        public HttpContent ToContent()
        {
            var content = new ByteArrayContent(Body);
            if (MediaType is not null)
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaType);
            }

            return content;
        }
    }
}
