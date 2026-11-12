using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tailcat.Link;
using Tailcat.Link.Transport;
using Tailcat.Link.Storage;
using Tailcat.Net;

namespace Tailcat.Demo;

/// <summary>
/// The .NET half of tools/field-test: a host that answers pings and collects
/// reports, and a client that pings it and reports what it saw. Both speak
/// exactly what the JavaScript ones do (tools/field-test/web/field.js and
/// host.mjs), so any pairing of the two can be run against any other and the
/// reports read side by side. Each run has an identity of its own, in a
/// temporary folder, so it never meets the pairings of the run before.
/// </summary>
internal static class FieldTest
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> HostAsync(bool relayOnly, string? reportsPath, CancellationToken ct)
    {
        string store = Path.Combine(Path.GetTempPath(), "tailcat-field-host-" + Guid.NewGuid().ToString("N")[..8]);
        await using ILinkHost host = await TailcatLink.HostManyAsync(
            "tailcat-field",
            new LinkOptions
            {
                Store = new FileLinkStore(store),
                MaxPeers = 16,
                // A run goes on for longer than the default window, and a
                // client that arrives after it is refused as a stranger.
                PairingWindow = TimeSpan.FromHours(12),
                Gateway = Gateway(relayOnly),
                Log = line => Log($"  [link] {line}"),
            },
            ct);

        host.PeerJoined += (_, e) =>
        {
            Log($"[peer-joined] {e.Peer.Name} ({e.Peer.Route})");
            e.Peer.RouteChanged += (_, r) => Log($"[route] {e.Peer.Name}: {Lower(r.Route)}");
        };
        host.PeerLeft += (_, e) => Log($"[peer-left] {e.Peer.Name}");

        host.SetRequestHandler(async (peer, request, _) =>
        {
            string text = Encoding.UTF8.GetString(request.Span);
            if (!text.StartsWith("report ", StringComparison.Ordinal))
            {
                return Encoding.UTF8.GetBytes("pong");
            }

            // The route the host saw, beside the one the client reported: the
            // two ends decide it independently and should agree.
            string line = text[7..].TrimEnd('}') + $",\"hostSaw\":\"{Lower(peer.Route)}\",\"host\":\"dotnet\"}}";
            Log($"REPORT {line}");
            if (reportsPath is not null)
            {
                await File.AppendAllTextAsync(reportsPath, line + Environment.NewLine, ct);
            }
            return Encoding.UTF8.GetBytes("ok");
        });

        Log($"host up, relay-only {relayOnly}");
        Console.WriteLine($"CODE {host.InvitationCode.Value}");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    public static async Task<int> JoinAsync(string code, string name, int seconds, bool relayOnly, CancellationToken ct)
    {
        string store = Path.Combine(Path.GetTempPath(), "tailcat-field-join-" + Guid.NewGuid().ToString("N")[..8]);
        Log($"joining as {name}, relay-only {relayOnly}");
        Stopwatch clock = Stopwatch.StartNew();
        List<object> events = [];

        await using ILink link = await TailcatLink.JoinAsync(
            "tailcat-field",
            code,
            new JoinRequest { DisplayName = name },
            new LinkOptions { Store = new FileLinkStore(store), Gateway = Gateway(relayOnly), Log = line => Log($"  [link] {line}") },
            ct);
        link.RouteChanged += (_, e) =>
        {
            lock (events)
            {
                events.Add(new { at = clock.ElapsedMilliseconds, @event = "route-changed", detail = Lower(e.Route) });
            }
            Log($"[route-changed] {Lower(e.Route)}");
        };

        await link.WaitUntilConnectedAsync(ct);
        long connectMs = clock.ElapsedMilliseconds;
        Log($"connected after {connectMs} ms, route {Lower(link.Route)}");

        List<(string Route, double Ms)> samples = [];
        int failures = 0;
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            string route = Lower(link.Route);
            Stopwatch rtt = Stopwatch.StartNew();
            try
            {
                using CancellationTokenSource one = CancellationTokenSource.CreateLinkedTokenSource(ct);
                one.CancelAfter(TimeSpan.FromSeconds(10));
                await link.RequestAsync("ping", one.Token);
                samples.Add((route, rtt.Elapsed.TotalMilliseconds));
                Log($"ping {rtt.Elapsed.TotalMilliseconds:F1} ms ({route})");
            }
            catch (Exception ex) when (ex is LinkException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                failures++;
                Log($"ping failed: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        string report;
        lock (events)
        {
            report = JsonSerializer.Serialize(
                new
                {
                    name,
                    ua = $"dotnet {Environment.Version} on {Environment.OSVersion}{(relayOnly ? ", relay-only" : "")}",
                    finalRoute = Lower(link.Route),
                    connectMs,
                    relayedMedianMs = Median(samples, "relayed"),
                    directMedianMs = Median(samples, "direct"),
                    relayedSamples = samples.Count(s => s.Route == "relayed"),
                    directSamples = samples.Count(s => s.Route == "direct"),
                    failures,
                    events,
                },
                Json);
        }
        await link.RequestAsync("report " + report, ct);
        Log($"reported: {report}");
        return failures == 0 ? 0 : 1;
    }

    // relay1 alone is what a browser and Windows 10 get; forcing it on a
    // machine with QUIC is how that pairing is tested from here.
    private static TailcatNodeGatewayFactory Gateway(bool relayOnly) =>
        new()
        {
            Transports = relayOnly ? [PeerTransport.Relay1] : null,
            Observer = new TextTailcatObserver(line => Log($"  [node] {line}")),
        };

    private static double? Median(List<(string Route, double Ms)> samples, string route)
    {
        double[] of = [.. samples.Where(s => s.Route == route).Select(s => s.Ms).Order()];
        return of.Length == 0 ? null : Math.Round(of[of.Length / 2], 1);
    }

    private static string Lower(LinkRoute route) => route.ToString().ToLowerInvariant();

    private static void Log(string line) =>
        Console.WriteLine($"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}");
}
