// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace Tailcat.WebDemo.Tests;

/// <summary>Port of webdemo_test.go.</summary>
public class WebDemoTests
{
    // FakeDist is the port of fakeDist()'s fstest.MapFS.
    private static InMemoryFileProvider FakeDist() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["index.html"] = "<html>demo</html>",
        ["app.js"] = "// app",
        ["wasm_exec.js"] = "// exec",
        ["main.wasm"] = "wasm-uncompressed",
        ["main.wasm.zst"] = "wasm-zst",
        ["main.wasm.gz"] = "wasm-gzip!",
    });

    [Fact]
    public async Task HandlerIncompleteDist()
    {
        InMemoryFileProvider dist = FakeDist();
        dist.Remove("main.wasm");

        await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(dist));
    }

    public static TheoryData<string, string, string, Dictionary<string, string>> Cases() => new()
    {
        { "/", "", "<html>demo</html>", [] },
        { "/app.js", "", "// app", [] },
        { "/wasm_exec.js", "", "// exec", [] },
        {
            "/main.wasm", "zstd, gzip", "wasm-zst",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Type"] = "application/wasm",
                ["Content-Encoding"] = "zstd",
                ["X-Uncompressed-Size"] = "17",
                ["X-Compressed-Size"] = "8",
            }
        },
        {
            "/main.wasm", "gzip", "wasm-gzip!",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Encoding"] = "gzip",
                ["X-Compressed-Size"] = "10",
            }
        },
        {
            "/main.wasm", "identity", "wasm-uncompressed",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Encoding"] = "",
                ["X-Compressed-Size"] = "17",
            }
        },
    };

    /// <param name="path">The request path.</param>
    /// <param name="acceptEncoding">The Accept-Encoding to send, if any.</param>
    /// <param name="wantBody">The exact body expected back.</param>
    /// <param name="wantHeaders">Response headers that must match exactly.</param>
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Handler(string path, string acceptEncoding, string wantBody, Dictionary<string, string> wantHeaders)
    {
        await using WebApplication app = await StartAsync(FakeDist());
        using HttpClient client = app.GetTestClient();

        HttpResponseMessage res = await GetAsync(client, path, acceptEncoding);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(wantBody, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        foreach ((string name, string want) in wantHeaders)
        {
            Assert.Equal(want, HeaderOf(res, name));
        }
    }

    [Theory]
    [InlineData("/nope")]
    // The precompressed forms are an implementation detail of /main.wasm and
    // must not be reachable on their own.
    [InlineData("/main.wasm.zst")]
    [InlineData("/main.wasm.gz")]
    public async Task UnknownPathsAre404(string path)
    {
        await using WebApplication app = await StartAsync(FakeDist());
        using HttpClient client = app.GetTestClient();

        HttpResponseMessage res = await GetAsync(client, path, "");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>
    /// index.html must reference only relative asset URLs so the handler can
    /// be mounted under a path prefix.
    /// </summary>
    [Fact]
    public async Task IndexUsesRelativeAssetUrls()
    {
        await using WebApplication app = await StartAsync(FakeDist());
        using HttpClient client = app.GetTestClient();

        HttpResponseMessage res = await GetAsync(client, "/", "");
        string body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("src=\"/", body, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string acceptEncoding)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, path);
        if (acceptEncoding.Length != 0)
        {
            req.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }
        // The test client does no transparent decompression, so a
        // Content-Encoding response arrives exactly as it was written.
        return await client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    private static string HeaderOf(HttpResponseMessage res, string name)
    {
        if (res.Headers.TryGetValues(name, out IEnumerable<string>? v) ||
            res.Content.Headers.TryGetValues(name, out v))
        {
            return string.Join(", ", v);
        }
        return "";
    }

    private static async Task<WebApplication> StartAsync(IFileProvider dist)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        WebApplication app = builder.Build();
        app.MapWebDemo(dist);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}

/// <summary>
/// A file provider backed by strings, the stand-in for Go's fstest.MapFS.
/// </summary>
internal sealed class InMemoryFileProvider(Dictionary<string, string> files) : IFileProvider
{
    public void Remove(string name) => files.Remove(name);

    public IFileInfo GetFileInfo(string subpath)
    {
        string name = subpath.TrimStart('/');
        return files.TryGetValue(name, out string? content)
            ? new StringFileInfo(name, content)
            : new NotFoundFileInfo(name);
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class StringFileInfo(string name, string content) : IFileInfo
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);

        public bool Exists => true;

        public long Length => _bytes.Length;

        public string? PhysicalPath => null;

        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream(_bytes, writable: false);
    }
}
