// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Link.Protocol;
using Tailcat.Link.Storage;
using Tailcat.Link.Transport;

namespace Tailcat.Link;

/// <summary>
/// Pairs two machines that cannot see each other, and keeps them linked.
/// </summary>
/// <remarks>
/// <para>
/// One machine hosts and shows a code; the other joins with it, once. From
/// then on both remember each other, and either can move network, lose its
/// connection, or reboot without anybody re-entering anything.
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
/// </remarks>
public static class TailcatLink
{
    /// <summary>
    /// Brings up the end that publishes a code and waits to be joined.
    /// </summary>
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
    public static async Task<ILink> HostAsync(
        string appName,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        options ??= new LinkOptions();

        PairingRecord pairing = await OpenAsync(appName, options, cancellationToken).ConfigureAwait(false);
        INodeGateway gateway = await options.Gateway
            .CreateAsync(pairing.State.PrivateKey, pairing.State.HomeRegionId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            // Whichever region was measured the first time is the region this
            // machine listens in from now on. Its address contains the region,
            // so re-measuring after a move would silently retire the code that
            // is already out in the world — the one failure nobody could
            // recover from remotely.
            await pairing.RememberHomeRegionAsync(gateway.HomeRegionId, cancellationToken).ConfigureAwait(false);

            // The address is public whether this machine likes it or not — the
            // relay it connects to sees it — so what makes the code worth
            // holding is the token minted here, and how briefly it is good for.
            PairingOffer offer = await pairing
                .OfferPairingAsync(options.PairingWindow, cancellationToken).ConfigureAwait(false);

            AcceptingSessionSource source = new(
                pairing,
                options.RequestTimeout,
                options.ListenSilenceTimeout,
                options.TimeProvider,
                options.Log);
            DurableLink link = new(
                pairing,
                new HostInvitation(pairing, gateway.Address, offer, options.PairingWindow),
                source,
                gateway,
                options);
            link.Start();
            return link;
        }
        catch
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
    public static async Task<ILink> JoinAsync(
        string appName,
        string? invitationCode = null,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        options ??= new LinkOptions();

        PairingRecord pairing = await OpenAsync(appName, options, cancellationToken).ConfigureAwait(false);
        InvitationCode code = invitationCode is not null
            ? InvitationCode.Parse(invitationCode)
            : pairing.State.PeerCode ?? throw new LinkException(
                $"this machine has not been paired for \"{appName}\" yet; pass the host's invitation code once");

        // The host's key is inside the code, so this end knows who it is
        // talking to before a single packet goes out.
        NodePublic host = code.Address.Parse().ServerPublic;
        await pairing.JoinPeerAsync(code, host, cancellationToken).ConfigureAwait(false);

        // No pinned region: this end publishes no address, so it is free to
        // use whichever relay is closest to wherever it is switched on today.
        INodeGateway gateway = await options.Gateway
            .CreateAsync(pairing.State.PrivateKey, homeRegionId: null, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            DialingSessionSource source = new(
                code.Address, code.PairingToken, options.RequestTimeout, options.TimeProvider);
            DurableLink link = new(pairing, new JoinedInvitation(code), source, gateway, options);
            link.Start();
            return link;
        }
        catch
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Forgets this machine's identity and pairing for <paramref name="appName"/>.
    /// </summary>
    /// <remarks>
    /// The next <see cref="HostAsync"/> publishes a new code and the next
    /// <see cref="JoinAsync"/> demands one, so this is how a machine that
    /// changed hands is taken out of service.
    /// </remarks>
    public static Task ForgetAsync(
        string appName,
        LinkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        return (options ?? new LinkOptions()).Store.DeleteAsync(appName, cancellationToken);
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
        return new PairingRecord(appName, state, options.Store, options.TimeProvider);
    }
}
