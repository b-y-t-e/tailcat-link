// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Tailcat.Tailcfg;

namespace Tailcat;

/// <summary>
/// Caches fetched DERP maps for <see cref="ConnInfo.ExpandAsync"/> and
/// <see cref="DerpMapFetcher.FetchAsync"/>.
/// </summary>
/// <remarks>
/// Without one, a process-wide in-memory cache is used; provide an
/// implementation (like the tailcat CLI's on-disk one) to persist across
/// processes. Implementations just store bytes; the freshness policy lives
/// in the fetcher: a stored map younger than an hour is used without any
/// network traffic, an older one is revalidated with If-None-Match (the
/// ETag is opaque to us), and a stored map of any age is used as a fallback
/// if the fetch fails or times out.
/// </remarks>
public interface IDerpMapCache
{
    /// <summary>
    /// Returns the previously stored DERP map response for
    /// <paramref name="url"/>: its raw JSON, the server's ETag (or ""), and
    /// when it was stored. Returns false if nothing usable is stored.
    /// </summary>
    bool TryGet(string url, [NotNullWhen(true)] out byte[]? data, out string etag, out DateTimeOffset storedAt);

    /// <summary>
    /// Stores the DERP map response for <paramref name="url"/>, replacing any
    /// prior entry and marking it stored as of now. An empty
    /// <paramref name="etag"/> means the server sent none.
    /// </summary>
    void Put(string url, byte[] data, string etag);
}

/// <summary>
/// A process-wide in-memory <see cref="IDerpMapCache"/>, the default when no
/// cache is provided.
/// </summary>
public sealed class MemDerpMapCache : IDerpMapCache
{
    /// <summary>The shared instance used when no cache is configured.</summary>
    public static MemDerpMapCache Default { get; } = new();

    private readonly Lock _mu = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly TimeProvider _time;

    /// <summary>Creates a cache that stamps entries with the system clock.</summary>
    public MemDerpMapCache() : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates a cache that stamps entries with <paramref name="timeProvider"/>,
    /// which must be the same clock the fetcher measures freshness against.
    /// </summary>
    public MemDerpMapCache(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _time = timeProvider;
    }

    /// <inheritdoc/>
    public bool TryGet(string url, [NotNullWhen(true)] out byte[]? data, out string etag, out DateTimeOffset storedAt)
    {
        lock (_mu)
        {
            if (_entries.TryGetValue(url, out Entry e))
            {
                (data, etag, storedAt) = (e.Data, e.ETag, e.StoredAt);
                return true;
            }
        }
        (data, etag, storedAt) = (null, "", default);
        return false;
    }

    /// <inheritdoc/>
    public void Put(string url, byte[] data, string etag)
    {
        lock (_mu)
        {
            _entries[url] = new Entry(data, etag, _time.GetUtcNow());
        }
    }

    private readonly record struct Entry(byte[] Data, string ETag, DateTimeOffset StoredAt);
}

/// <summary>
/// Options controlling how a DERP map is obtained, standing in for the
/// variadic options of Go's <c>ConnInfo.Expand</c> and <c>FetchDERPMap</c>.
/// </summary>
public sealed class ExpandOptions
{
    /// <summary>
    /// The URL of the JSON-encoded DERP map to fetch, defaulting to
    /// <see cref="DerpMapFetcher.DefaultUrl"/>.
    /// </summary>
    public string Url { get; init; } = DerpMapFetcher.DefaultUrl;

    /// <summary>
    /// A DERP map to expand from instead of fetching one over the network.
    /// Ignored by <see cref="DerpMapFetcher.FetchAsync"/>.
    /// </summary>
    public DerpMap? DerpMap { get; init; }

    /// <summary>
    /// Whether the fetch is on behalf of a tailcat server (which will listen
    /// on the chosen region) rather than a client. It is sent as a hint
    /// header to the DERP map server.
    /// </summary>
    public bool ForServer { get; init; }

    /// <summary>
    /// Where fetched DERP maps are cached. Defaults to
    /// <see cref="MemDerpMapCache.Default"/>.
    /// </summary>
    public IDerpMapCache Cache { get; init; } = MemDerpMapCache.Default;

    /// <summary>
    /// How the lowest-latency region is chosen when RegionID is -1. Defaults
    /// to <see cref="NoRegionPicker.Instance"/>, which picks none.
    /// </summary>
    public IRegionPicker RegionPicker { get; init; } = NoRegionPicker.Instance;

    /// <summary>
    /// The HTTP client used for fetches. Defaults to a shared client.
    /// </summary>
    public HttpClient HttpClient { get; init; } = DerpMapFetcher.SharedHttpClient;

    /// <summary>The clock used for cache freshness. Defaults to the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal string Mode => ForServer ? "server" : "client";
}

/// <summary>
/// Chooses the DERP region with the lowest latency, the port of Go's
/// <c>PickBestRegion</c>. Implementing it requires netcheck-style STUN
/// probing, which lives outside this library.
/// </summary>
public interface IRegionPicker
{
    /// <summary>
    /// Returns the region ID with the lowest latency, or 0 if no region
    /// latency could be measured.
    /// </summary>
    Task<int> PickBestRegionAsync(DerpMap derpMap, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="IRegionPicker"/> that measures nothing and always reports
/// "no usable latencies", so callers fall back to picking a region at
/// random. It is the default because latency probing needs a STUN client.
/// </summary>
public sealed class NoRegionPicker : IRegionPicker
{
    /// <summary>The shared instance.</summary>
    public static NoRegionPicker Instance { get; } = new();

    /// <inheritdoc/>
    public Task<int> PickBestRegionAsync(DerpMap derpMap, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

/// <summary>Fetches and decodes the JSON DERP map, with caching.</summary>
public static class DerpMapFetcher
{
    /// <summary>
    /// The URL of the JSON-encoded DERP map that <see cref="ConnInfo.ExpandAsync"/>
    /// fetches when no alternate DERP map source is specified.
    /// </summary>
    public const string DefaultUrl = "https://tailcat.dev/derpmap.json";

    /// <summary>
    /// How old a cached DERP map may be and still be used without
    /// revalidating with the server.
    /// </summary>
    public static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a fetch may take before a stale cached map (if any) is served
    /// instead. It matters most when we hold a stale copy: better to serve
    /// that than to hang on a slow or unreachable map server.
    /// </summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    // The maximum DERP map body we'll read, mirroring Go's 8 MB limit.
    private const int MaxBodyBytes = 8 << 20;

    /// <summary>The default HTTP client, used when no other is configured.</summary>
    public static HttpClient SharedHttpClient { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Fetches and decodes the JSON DERP map, honoring the cache freshness
    /// policy documented on <see cref="IDerpMapCache"/>.
    /// </summary>
    /// <exception cref="TailcatException">
    /// If the map can't be fetched or decoded and no cached copy exists.
    /// </exception>
    public static async Task<DerpMap> FetchAsync(ExpandOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExpandOptions();
        IDerpMapCache cache = options.Cache;
        string fetchUrl = options.Url;

        byte[]? cachedData = null;
        string cachedETag = "";
        if (cache.TryGet(fetchUrl, out byte[]? data, out string etag, out DateTimeOffset storedAt))
        {
            DerpMap? cachedMap = Decode(data);
            if (cachedMap is not null)
            {
                if (options.TimeProvider.GetUtcNow() - storedAt < CacheMaxAge)
                {
                    return cachedMap;
                }
                (cachedData, cachedETag) = (data, etag);
            }
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(FetchTimeout);

        DerpMap StaleOr(Exception cause)
        {
            DerpMap? stale = Decode(cachedData);
            if (stale is not null)
            {
                return stale;
            }
            throw cause as TailcatException ?? new TailcatException(cause.Message, cause);
        }

        HttpResponseMessage res;
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, fetchUrl);
            req.Headers.TryAddWithoutValidation("Tailcat-Mode", options.Mode);
            if (cachedETag.Length != 0)
            {
                req.Headers.TryAddWithoutValidation("If-None-Match", cachedETag);
            }
            res = await options.HttpClient
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return StaleOr(new TailcatException($"fetching {fetchUrl}: {ex.Message}", ex));
        }

        using (res)
        {
            if (res.StatusCode == HttpStatusCode.NotModified && cachedData is not null)
            {
                // Still valid; re-store it to restart the freshness window.
                cache.Put(fetchUrl, cachedData, cachedETag);
                return Decode(cachedData)!;
            }
            if (res.StatusCode != HttpStatusCode.OK)
            {
                return StaleOr(new TailcatException($"fetching {fetchUrl}: {(int)res.StatusCode} {res.ReasonPhrase}"));
            }

            byte[] body;
            try
            {
                body = await ReadLimitedAsync(res, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return StaleOr(new TailcatException($"reading {fetchUrl}: {ex.Message}", ex));
            }

            DerpMap? dm = Decode(body);
            if (dm is null)
            {
                return StaleOr(new TailcatException($"invalid DERP map JSON from {fetchUrl}"));
            }
            cache.Put(fetchUrl, body, ETagOf(res.Headers));
            return dm;
        }
    }

    /// <summary>
    /// Decodes a JSON DERP map, returning null if <paramref name="data"/> is
    /// empty or invalid.
    /// </summary>
    public static DerpMap? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<DerpMap>(data, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage res, CancellationToken cancellationToken)
    {
        await using Stream body = await res.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buf = new();
        byte[] chunk = new byte[64 * 1024];
        int total = 0;
        while (total < MaxBodyBytes)
        {
            int n = await body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxBodyBytes - total)), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            buf.Write(chunk, 0, n);
            total += n;
        }
        return buf.ToArray();
    }

    private static string ETagOf(HttpResponseHeaders headers) =>
        headers.TryGetValues("ETag", out IEnumerable<string>? values) ? values.FirstOrDefault() ?? "" : "";
}
