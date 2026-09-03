// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using Tailcat.Tailcfg;

namespace Tailcat.Tests;

/// <summary>Port of TestFetchDERPMapMemoryCache from tailcat_test.go, plus
/// the freshness and fallback rules documented on <see cref="IDerpMapCache"/>.</summary>
public class DerpMapFetcherTests
{
    // A stand-in for httptest.NewServer: it counts requests and answers them
    // from a caller-supplied function, without touching the network.
    private sealed class FakeServer : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        private int _fetches;

        public FakeServer(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        // A unique URL per server, so the process-wide cache never mixes up
        // two tests (each httptest server gets its own port in Go).
        public string Url { get; } = $"https://derpmap.test/{Guid.NewGuid():N}";

        public int Fetches => Volatile.Read(ref _fetches);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetches);
            lock (Requests)
            {
                Requests.Add(request);
            }
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null)
    {
        HttpResponseMessage res = new(status) { Content = new StringContent(body) };
        if (etag is not null)
        {
            res.Headers.TryAddWithoutValidation("ETag", etag);
        }
        return res;
    }

    private static ExpandOptions OptionsFor(FakeServer srv, IDerpMapCache? cache = null, TimeProvider? time = null) => new()
    {
        Url = srv.Url,
        HttpClient = new HttpClient(srv),
        Cache = cache ?? MemDerpMapCache.Default,
        TimeProvider = time ?? TimeProvider.System,
    };

    /// <summary>
    /// Verifies the default in-memory DERP map cache: a second fetch of the
    /// same URL within the freshness window makes no network request.
    /// </summary>
    [Fact]
    public async Task FetchDerpMapMemoryCache()
    {
        using FakeServer srv = new(_ => Json("""{"Regions":{"1":{"RegionID":1}}}"""));
        ExpandOptions opts = OptionsFor(srv);

        for (int i = 0; i < 2; i++)
        {
            DerpMap dm = await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
            Assert.True(dm.Regions.Count == 1, $"got {dm.Regions.Count} regions; want 1");
        }
        Assert.True(srv.Fetches == 1, $"fetches = {srv.Fetches}; want 1");
    }

    /// <summary>
    /// A cached map older than the max age is revalidated with If-None-Match,
    /// and a 304 restarts the freshness window without re-downloading.
    /// </summary>
    [Fact]
    public async Task StaleCacheIsRevalidatedWithETag()
    {
        using FakeServer srv = new(req =>
            req.Headers.TryGetValues("If-None-Match", out IEnumerable<string>? v) && v.First() == "\"v1\""
                ? Json("", HttpStatusCode.NotModified)
                : Json("""{"Regions":{"1":{"RegionID":1}}}""", etag: "\"v1\""));

        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        MemDerpMapCache cache = new(time);
        ExpandOptions opts = OptionsFor(srv, cache, time);

        await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        Assert.Equal(1, srv.Fetches);

        // Still fresh: no request.
        time.Advance(DerpMapFetcher.CacheMaxAge - TimeSpan.FromMinutes(1));
        await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        Assert.Equal(1, srv.Fetches);

        // Stale: revalidated, and the 304 answer is served from cache.
        time.Advance(TimeSpan.FromMinutes(2));
        DerpMap dm = await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        Assert.Equal(2, srv.Fetches);
        Assert.Single(dm.Regions);
        Assert.Equal("\"v1\"", srv.Requests[1].Headers.GetValues("If-None-Match").Single());

        // The 304 restarted the freshness window, so the next fetch is quiet.
        time.Advance(TimeSpan.FromMinutes(1));
        await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        Assert.Equal(2, srv.Fetches);
    }

    /// <summary>A stale cached map is served when the fetch fails.</summary>
    [Fact]
    public async Task FailedFetchFallsBackToStaleCache()
    {
        int calls = 0;
        using FakeServer srv = new(_ => ++calls == 1
            ? Json("""{"Regions":{"7":{"RegionID":7}}}""")
            : Json("nope", HttpStatusCode.InternalServerError));

        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
        MemDerpMapCache cache = new(time);
        ExpandOptions opts = OptionsFor(srv, cache, time);

        await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        time.Advance(DerpMapFetcher.CacheMaxAge + TimeSpan.FromMinutes(1));

        DerpMap dm = await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        Assert.Equal(7, Assert.Single(dm.Regions).Key);
    }

    /// <summary>With nothing cached, a failed fetch surfaces the error.</summary>
    [Fact]
    public async Task FailedFetchWithoutCacheThrows()
    {
        using FakeServer srv = new(_ => Json("nope", HttpStatusCode.InternalServerError));
        ExpandOptions opts = OptionsFor(srv, new MemDerpMapCache());

        TailcatException ex = await Assert.ThrowsAsync<TailcatException>(
            () => DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken));
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The fetch tells the map server whether it's for a server or a client.</summary>
    [Theory]
    [InlineData(false, "client")]
    [InlineData(true, "server")]
    public async Task FetchSendsModeHeader(bool forServer, string wantMode)
    {
        using FakeServer srv = new(_ => Json("""{"Regions":{"1":{"RegionID":1}}}"""));
        ExpandOptions opts = new()
        {
            Url = srv.Url,
            HttpClient = new HttpClient(srv),
            Cache = new MemDerpMapCache(),
            ForServer = forServer,
        };

        await DerpMapFetcher.FetchAsync(opts, TestContext.Current.CancellationToken);
        Assert.Equal(wantMode, srv.Requests[0].Headers.GetValues("Tailcat-Mode").Single());
    }

    /// <summary>Invalid JSON decodes to null rather than throwing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public void DecodeRejectsBadInput(string body) =>
        Assert.Null(DerpMapFetcher.Decode(System.Text.Encoding.UTF8.GetBytes(body)));
}

/// <summary>A manually advanced clock, so cache freshness tests don't sleep.</summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan d) => _now += d;
}
