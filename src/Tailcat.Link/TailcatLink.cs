// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Globalization;
using System.Text;
using Tailcat.Keys;
using Microsoft.Extensions.Logging;
using Tailcat.Link.Diagnostics;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;

namespace Tailcat.Link;

/// <summary>
/// Pairs machines that cannot see each other, and keeps them linked.
/// </summary>
/// <remarks>
/// <para>
/// One machine hosts and shows a code; the others join with it, once. From
/// then on both ends remember each other, and either can move network, lose
/// its connection, or reboot without anybody re-entering anything.
/// </para>
/// <para>
/// On the machine to be reached:
/// </para>
/// <code>
/// await using ILink link = await TailcatLink.HostAsync("my-app");
/// Console.WriteLine(link.InvitationCode);          // show this once
/// link.OnRequest(command => Handle(command));      // answer the operator
/// </code>
/// <para>
/// On the machine doing the reaching, the first time only with the code:
/// </para>
/// <code>
/// await using ILink link = await TailcatLink.JoinAsync("my-app", code);
/// string answer = await link.RequestAsync("status");
/// </code>
/// <para>
/// A machine that has to hold several clients at once uses
/// <see cref="HostManyAsync"/> instead, which is the same thing without the
/// bound of one.
/// </para>
/// </remarks>
public static class TailcatLink
{
    /// <summary>
    /// Brings up the end that publishes a code and waits to be joined by one
    /// machine.
    /// </summary>
    /// <remarks>
    /// <see cref="HostManyAsync"/> with <see cref="LinkOptions.MaxPeers"/> of
    /// one, behind the narrower <see cref="ILink"/>. It is deliberately the
    /// same machinery: two implementations of the pairing rules would drift,
    /// and the drift would be in who is let in.
    /// </remarks>
    /// <param name="appName">
    /// Names this link's stored state, so one machine can hold several
    /// independent pairings. Letters, digits, '-', '_' and '.' only.
    /// </param>
    /// <param name="options">Everything else, all of which has a sane default.</param>
    /// <param name="cancellationToken">Cancels bringing the node up.</param>
    /// <returns>
    /// A link that is already listening. It is up as soon as the peer arrives;
    /// nothing needs to be awaited for that to happen.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// If <see cref="LinkOptions.MaxPeers"/> asks for more than one machine.
    /// </exception>
    public static async Task<ILink> HostAsync(
        string appName,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new LinkOptions();

        // Refused rather than narrowed silently: hosting narrows the store to
        // the bound as well as the process, so reinterpreting a caller's
        // MaxPeers of four as one would unpair three machines and report it in
        // a log line. An application that means one peer says so; one that
        // means several wants HostManyAsync.
        if (options.MaxPeers > 1)
        {
            throw new ArgumentException(
                $"HostAsync holds one machine, so MaxPeers of {options.MaxPeers} cannot be honoured here; "
                + "use HostManyAsync, which is the same host without the bound of one",
                nameof(options));
        }

        LinkHost host = await OpenHostAsync(appName, options with { MaxPeers = 1 }, cancellationToken)
            .ConfigureAwait(false);
        DurableLink link = new(host, options);
        link.Start();
        return link;
    }

    /// <summary>
    /// Brings up the end that publishes invitations and holds every machine
    /// that accepts one.
    /// </summary>
    /// <remarks>
    /// One identity, one stored file, one region measurement and one node,
    /// however many peers there are — which is what an application with
    /// several clients would otherwise have to fake with one link per client.
    /// <see cref="LinkOptions.MaxPeers"/> bounds how many may pair, and is a
    /// security bound: every admitted peer reaches the application's handler.
    /// </remarks>
    /// <param name="appName">As for <see cref="HostAsync"/>.</param>
    /// <param name="options">Everything else, all of which has a sane default.</param>
    /// <param name="cancellationToken">Cancels bringing the node up.</param>
    public static async Task<ILinkHost> HostManyAsync(
        string appName,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LinkHost host = await OpenHostAsync(appName, options ?? new LinkOptions(), cancellationToken)
            .ConfigureAwait(false);
        host.Start();
        return host;
    }

    /// <summary>
    /// Brings up the end that connects to a host.
    /// </summary>
    /// <param name="appName">The name the pairing is stored under, as for <see cref="HostAsync"/>.</param>
    /// <param name="invitationCode">
    /// The host's code. Needed the first time only: afterwards it is stored,
    /// and passing null joins the machine this one is already paired with.
    /// Passing a different code re-pairs to that host instead.
    /// </param>
    /// <param name="options">Everything else, all of which has a sane default.</param>
    /// <param name="cancellationToken">Cancels bringing the node up.</param>
    /// <returns>
    /// A link that is already dialling. Await
    /// <see cref="ILink.WaitUntilConnectedAsync"/> to know when the host
    /// answered, or simply send a request — it waits by itself.
    /// </returns>
    /// <exception cref="LinkException">
    /// If no code is given and this machine has never been paired.
    /// </exception>
    public static Task<ILink> JoinAsync(
        string appName,
        string? invitationCode = null,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default) =>
        JoinAsync(appName, invitationCode, request: null, options, cancellationToken);

    /// <summary>
    /// Brings up the end that connects to a host, telling it what to call
    /// this machine.
    /// </summary>
    /// <param name="appName">The name the pairing is stored under.</param>
    /// <param name="invitationCode">The host's code, as for the other overload.</param>
    /// <param name="request">
    /// What this machine says about itself. The name is a hint: the host
    /// cannot check it, and nothing should be keyed off it.
    /// </param>
    /// <param name="options">Everything else, all of which has a sane default.</param>
    /// <param name="cancellationToken">Cancels bringing the node up.</param>
    /// <exception cref="ArgumentException">
    /// If the display name is longer than 256 bytes of UTF-8.
    /// </exception>
    public static async Task<ILink> JoinAsync(
        string appName,
        string? invitationCode,
        JoinRequest? request,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        // Checked here rather than where the hello is written: over the limit
        // it throws inside the supervision loop, which cannot tell a name that
        // will never fit from a relay having a bad minute, and so retries a
        // link that can never come up.
        int nameBytes = request?.DisplayName is { } displayName ? Encoding.UTF8.GetByteCount(displayName) : 0;
        if (nameBytes > LinkHello.MaxDisplayNameBytes)
        {
            throw new ArgumentException(
                $"a display name may be at most {LinkHello.MaxDisplayNameBytes} bytes, this one is {nameBytes}",
                nameof(request));
        }

        options ??= new LinkOptions();

        PairingRecord pairing = await OpenAsync(appName, options, cancellationToken).ConfigureAwait(false);
        InvitationCode code = invitationCode is not null
            ? InvitationCode.Parse(invitationCode, CultureInfo.InvariantCulture)
            : pairing.State.PeerCode ?? throw new LinkException(
                $"this machine has not been paired for \"{appName}\" yet; pass the host's invitation code once");

        // The host's key is inside the code, so this end knows who it is
        // talking to before a single packet goes out.
        NodePublic hostKey = code.Address.Parse().ServerPublic;
        await pairing.JoinPeerAsync(code, hostKey, cancellationToken).ConfigureAwait(false);

        // No pinned region: this end publishes no address, so it is free to
        // use whichever relay is closest to wherever it is switched on today.
        NodeHolder node = new(options.Gateway, () => pairing.State);
        node.Adopt(await options.Gateway
            .CreateAsync(pairing.State.PrivateKey, homeRegionId: null, cancellationToken)
            .ConfigureAwait(false));

        try
        {
            LinkHost host = new(pairing, new JoinedInvitation(code), node, options, listens: false);
            DialingSessionSource source = new(
                code.Address,
                new LinkHello(code.PairingToken, request?.DisplayName),
                options.RequestTimeout,
                options.TimeProvider);
            host.AddDialingPeer(
                pairing.State.PeerWith(hostKey) ?? throw new LinkException("the pairing was not written down"),
                source);

            DurableLink link = new(host, options);
            link.Start();
            return link;
        }
        catch
        {
            await node.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Forgets this machine's identity and every pairing for <paramref name="appName"/>.
    /// </summary>
    /// <remarks>
    /// The next <see cref="HostAsync"/> publishes a new code and the next
    /// <see cref="JoinAsync(string, string?, LinkOptions?, CancellationToken)"/> demands one, so this is how a machine that
    /// changed hands is taken out of service.
    /// <see cref="ILinkHost.ForgetPeerAsync"/> is the narrower thing: it
    /// unpairs one device and leaves this machine's identity alone.
    /// </remarks>
    public static Task ForgetAsync(
        string appName,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        return (options ?? new LinkOptions()).Store.DeleteAsync(appName, cancellationToken);
    }

    private static async Task<LinkHost> OpenHostAsync(
        string appName,
        LinkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        PairingRecord pairing = await OpenAsync(appName, options, cancellationToken).ConfigureAwait(false);
        NodeHolder node = new(options.Gateway, () => pairing.State);
        INodeGateway gateway = await options.Gateway
            .CreateAsync(pairing.State.PrivateKey, pairing.State.HomeRegionId, cancellationToken)
            .ConfigureAwait(false);
        node.Adopt(gateway);

        try
        {
            // Whichever region was measured the first time is the region this
            // machine listens in from now on. Its address contains the region,
            // so re-measuring after a move would silently retire the code that
            // is already out in the world — the one failure nobody could
            // recover from remotely.
            await pairing.RememberHomeRegionAsync(gateway.HomeRegionId, cancellationToken).ConfigureAwait(false);

            // MaxPeers is a security bound, so lowering it has to take effect
            // against the peers already written down: left in the store, each
            // of them comes back up reaching the application's handler under a
            // bound the application believes it has narrowed.
            LinkLog log = new(options.LoggerFactory?.CreateLogger<ILinkHost>(), options.Log);
            foreach (PairedPeer dropped in
                await pairing.EnforcePeerLimitAsync(cancellationToken).ConfigureAwait(false))
            {
                log.Warn($"unpaired {dropped.Key}: the store held more peers than MaxPeers of {options.MaxPeers}");
            }

            // The address is public whether this machine likes it or not — the
            // relay it connects to sees it — so what makes the code worth
            // holding is the token minted here, and how briefly it is good for.
            await pairing.OfferPairingAsync(options.PairingWindow, cancellationToken).ConfigureAwait(false);

            return new LinkHost(
                pairing,
                new InvitationBook(pairing, gateway.Address, options.PairingWindow),
                node,
                options,
                listens: true);
        }
        catch
        {
            await node.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // The identity is written before it is ever used, so a machine that is
    // switched off between its first start and its first pairing comes back as
    // the same machine rather than a new one.
    private static async Task<PairingRecord> OpenAsync(
        string appName,
        LinkOptions options,
        CancellationToken cancellationToken)
    {
        LinkState? state = await options.Store.LoadAsync(appName, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new LinkState { PrivateKey = NodePrivate.NewKey() };
            await options.Store.SaveAsync(appName, state, cancellationToken).ConfigureAwait(false);
        }
        return new PairingRecord(appName, state, options.Store, options.TimeProvider, options.MaxPeers);
    }
}
