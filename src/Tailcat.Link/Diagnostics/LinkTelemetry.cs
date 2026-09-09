// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Tailcat.Link.Diagnostics;

/// <summary>
/// What the link has been doing, in the shapes a collector already knows:
/// one <see cref="System.Diagnostics.ActivitySource"/> and one
/// <see cref="System.Diagnostics.Metrics.Meter"/>, both named
/// <see cref="Name"/>.
/// </summary>
/// <remarks>
/// <para>
/// A link is meant to run for months, and the part whose behaviour is worth a
/// graph is precisely the one nothing logs usefully: how often the supervisor
/// had to reconnect, and how often that was not enough and it rebuilt the
/// node. A log line per reconnection answers "did it happen"; only a counter
/// answers "is it getting worse".
/// </para>
/// <para>
/// Nothing here allocates a collector: an unlistened <c>ActivitySource</c>
/// returns no activity and an uncollected instrument records nothing, so an
/// application that never asks pays for none of it.
/// </para>
/// </remarks>
internal static class LinkTelemetry
{
    /// <summary>The name to subscribe to, for both the source and the meter.</summary>
    public const string Name = "Tailcat.Link";

    private static readonly string? Version =
        typeof(LinkTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

    private static readonly ActivitySource Source = new(Name, Version);

    private static readonly Meter Meter = new(Name, Version);

    private static readonly UpDownCounter<long> ConnectedSessions = Meter.CreateUpDownCounter<long>(
        "tailcat.link.sessions.connected",
        unit: "{session}",
        description: "Sessions that are up right now.");

    private static readonly Counter<long> Sessions = Meter.CreateCounter<long>(
        "tailcat.link.sessions",
        unit: "{session}",
        description: "Sessions that have come up since this process started.");

    private static readonly Counter<long> Reconnects = Meter.CreateCounter<long>(
        "tailcat.link.reconnects",
        unit: "{session}",
        description: "Sessions that have ended and are being replaced, by reason.");

    private static readonly Counter<long> NodeRebuilds = Meter.CreateCounter<long>(
        "tailcat.link.node.rebuilds",
        unit: "{rebuild}",
        description: "Times reconnecting failed often enough that the node underneath was rebuilt.");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "tailcat.link.requests",
        unit: "{request}",
        description: "Requests sent from this end, by outcome.");

    private static readonly Counter<long> BytesSent = Meter.CreateCounter<long>(
        "tailcat.link.sent",
        unit: "By",
        description: "Payload bytes handed to a session, by what carried them.");

    private static readonly Counter<long> BytesReceived = Meter.CreateCounter<long>(
        "tailcat.link.received",
        unit: "By",
        description: "Payload bytes taken from a session, by what carried them.");

    /// <summary>Follows one request, across every attempt it takes.</summary>
    /// <remarks>
    /// The span covers the retries too, because the id belongs to the request
    /// rather than to the attempt: a span per attempt would show a reconnection
    /// as several unrelated requests.
    /// </remarks>
    public static Activity? StartRequest() => Source.StartActivity("Tailcat.Link.Request", ActivityKind.Client);

    /// <summary>Records a session that came up.</summary>
    public static void SessionUp()
    {
        Sessions.Add(1);
        ConnectedSessions.Add(1);
    }

    /// <summary>Records a session that is no longer up.</summary>
    /// <remarks>
    /// Apart from <see cref="Reconnecting"/> because the two do not always
    /// happen together: a link being disposed ends its session without any
    /// reconnection to count, and counting nothing there would leave the
    /// gauge claiming a session that is gone.
    /// </remarks>
    public static void SessionEnded() => ConnectedSessions.Add(-1);

    /// <summary>Records a session that ended and is being replaced, and why.</summary>
    public static void Reconnecting(LinkDisconnectReason reason) =>
        Reconnects.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    /// <summary>Records the repair of last resort for a socket nothing else fixes.</summary>
    public static void NodeRebuilt() => NodeRebuilds.Add(1);

    /// <summary>Records how a request ended: answered, refused by the peer, or unanswered.</summary>
    public static void RequestEnded(string outcome) =>
        Requests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>Records payload bytes leaving, tagged with what carried them.</summary>
    public static void Sent(string kind, long bytes) =>
        BytesSent.Add(bytes, new KeyValuePair<string, object?>("kind", kind));

    /// <summary>Records payload bytes arriving, tagged with what carried them.</summary>
    public static void Received(string kind, long bytes) =>
        BytesReceived.Add(bytes, new KeyValuePair<string, object?>("kind", kind));
}
