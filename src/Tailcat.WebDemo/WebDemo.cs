// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;

namespace Tailcat.WebDemo;

/// <summary>
/// Serves the tailcat browser app (the WebAssembly build of tailcat) from a
/// distribution directory of prebuilt static files.
/// </summary>
/// <remarks>
/// It is the port of Go's webdemo package, used by the browser integration
/// tests and by external servers that host the demo (such as tailcat.dev).
/// </remarks>
public static class WebDemo
{
    /// <summary>The files a dist file provider must contain.</summary>
    public static IReadOnlyList<string> DistFiles { get; } = ["index.html", "app.js", "wasm_exec.js", "main.wasm"];

    // The optional precompressed forms of main.wasm, best encoding first.
    private static readonly (string Encoding, string FileName)[] CompressedWasm =
    [
        ("zstd", "main.wasm.zst"),
        ("gzip", "main.wasm.gz"),
    ];

    /// <summary>
    /// Maps the web app's routes, serving from <paramref name="dist"/>: a file
    /// provider holding index.html at "/", app.js, wasm_exec.js, and
    /// main.wasm, the latter served precompressed (from main.wasm.zst or
    /// main.wasm.gz, if present in dist) when the client's Accept-Encoding
    /// allows.
    /// </summary>
    /// <remarks>
    /// The page's asset URLs are all relative, so the routes may be mounted
    /// under a path prefix.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// If <paramref name="dist"/> is missing one of <see cref="DistFiles"/>.
    /// </exception>
    public static IEndpointRouteBuilder MapWebDemo(this IEndpointRouteBuilder endpoints, IFileProvider dist)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(dist);

        Dictionary<string, long> sizes = [];
        foreach (string name in DistFiles)
        {
            IFileInfo fi = dist.GetFileInfo(name);
            if (!fi.Exists)
            {
                throw new InvalidOperationException($"webdemo: incomplete dist: {name} not found");
            }
            sizes[name] = fi.Length;
        }
        foreach ((_, string name) in CompressedWasm)
        {
            IFileInfo fi = dist.GetFileInfo(name);
            if (fi.Exists)
            {
                sizes[name] = fi.Length;
            }
        }

        endpoints.MapGet("/", ctx => ServeAsync(ctx, dist, "index.html", "text/html; charset=utf-8"));
        endpoints.MapGet("/app.js", ctx => ServeAsync(ctx, dist, "app.js", "text/javascript; charset=utf-8"));
        endpoints.MapGet("/wasm_exec.js", ctx => ServeAsync(ctx, dist, "wasm_exec.js", "text/javascript; charset=utf-8"));
        endpoints.MapGet("/main.wasm", ctx =>
        {
            // The wasm binary is tens of MB; serve it precompressed. The
            // Content-Type is set explicitly so it isn't inferred from the
            // compressed file's extension. The transfer size goes in
            // X-Compressed-Size because reverse proxies may drop
            // Content-Length, and the page can't compute it itself: its body
            // stream sees only decompressed bytes.
            ctx.Response.Headers.Vary = "Accept-Encoding";
            ctx.Response.Headers["X-Uncompressed-Size"] =
                sizes["main.wasm"].ToString(CultureInfo.InvariantCulture);

            string name = "main.wasm";

            // A substring match, not a parse: "gzip;q=0" is a refusal and this
            // treats it as an offer, and the q-values are ignored entirely.
            // That is what the Go original does, and this file exists to be
            // the same server; a browser never sends either. Changing it would
            // make the two implementations disagree over which bytes a given
            // request gets, which is exactly what the port is for testing.
            string accept = ctx.Request.Headers.AcceptEncoding.ToString();
            foreach ((string encoding, string fileName) in CompressedWasm)
            {
                if (accept.Contains(encoding, StringComparison.OrdinalIgnoreCase) &&
                    sizes.GetValueOrDefault(fileName) > 0)
                {
                    ctx.Response.Headers.ContentEncoding = encoding;
                    name = fileName;
                    break;
                }
            }
            ctx.Response.Headers["X-Compressed-Size"] = sizes[name].ToString(CultureInfo.InvariantCulture);
            return ServeAsync(ctx, dist, name, "application/wasm");
        });

        return endpoints;
    }

    private static async Task ServeAsync(HttpContext ctx, IFileProvider dist, string name, string contentType)
    {
        IFileInfo fi = dist.GetFileInfo(name);
        if (!fi.Exists)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = fi.Length;
        await using Stream body = fi.CreateReadStream();
        await body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }
}
