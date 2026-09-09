// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text;
using Tailcat.Link.Storage;
using Tailcat.Net;

namespace Tailcat.Link.Protocol;

/// <summary>
/// The first exchange on every session: the end that dialled says which
/// invitation it holds and what to call it, and the end that was dialled says
/// whether it is listening to that machine at all.
/// </summary>
/// <remarks>
/// <para>
/// It runs on every session rather than only on the first, because neither
/// end can tell a first session from a later one: a host that rebooted, a
/// peer that re-paired, and a stranger that guessed an address all arrive the
/// same way. Making it unconditional is what keeps both ends' state machines
/// down to one path.
/// </para>
/// <para>
/// Its cost is one round trip through the relay per reconnection, against a
/// host that would otherwise be claimed for good by whoever connected to it
/// first — including the operator of the relay it is connected to, who sees
/// every unpaired host's address as a matter of course.
/// </para>
/// </remarks>
internal static class PairingHandshake
{
    /// <summary>
    /// Presents this machine's <paramref name="hello"/> and waits to be let in.
    /// </summary>
    /// <exception cref="PairingRefusedException">If the other machine will not have this one.</exception>
    public static async Task OfferAsync(
        ITailcatConnection connection,
        LinkHello hello,
        IdleTimeout idle,
        CancellationToken cancellationToken)
    {
        Stream stream = await connection.OpenStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            idle.Restart();
            await LinkFrame.WriteAsync(
                    stream,
                    (byte)LinkFrameKind.Hello,
                    Guid.NewGuid(),
                    hello.Encode(),
                    idle,
                    cancellationToken)
                .ConfigureAwait(false);

            (byte status, _, byte[] answer) =
                await LinkFrame.ReadAsync(stream, idle, cancellationToken).ConfigureAwait(false);
            if (status != (byte)LinkFrameStatus.Ok)
            {
                throw new PairingRefusedException(
                    $"the other machine refused this one: {Encoding.UTF8.GetString(answer)}");
            }
        }
    }

    /// <summary>
    /// Reads what the machine that dialled presents, and answers it.
    /// </summary>
    /// <returns>The peer it may stay as, or null if it may not.</returns>
    /// <exception cref="InvitationExpiredException">
    /// If the machine that dialled holds an invitation this one has stopped
    /// offering. It is refused first and identically, so the throw tells this
    /// end's operator what the wire deliberately does not.
    /// </exception>
    public static async Task<PairedPeer?> AcceptAsync(
        ITailcatConnection connection,
        IPairingPolicy policy,
        IdleTimeout idle,
        CancellationToken cancellationToken)
    {
        Stream stream = await connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            (byte tag, Guid exchange, byte[] payload) =
                await LinkFrame.ReadAsync(stream, idle, cancellationToken).ConfigureAwait(false);
            if (tag != (byte)LinkFrameKind.Hello)
            {
                await RefuseAsync(stream, exchange, "say hello first", cancellationToken).ConfigureAwait(false);
                return null;
            }

            PairedPeer? admitted;
            try
            {
                admitted = await policy
                    .AdmitAsync(connection.Peer, LinkHello.Decode(payload), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvitationExpiredException)
            {
                // The same answer everyone else gets, and then on to this
                // machine's own end of the link: what it knows about why is
                // for its operator, never for the machine outside.
                await RefuseAsync(stream, exchange, "this machine is not open to you", cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }

            if (admitted is null)
            {
                // Deliberately says nothing about which part was wrong: an
                // expired invitation, a wrong token and a host that is already
                // full are the same answer, so that nothing here helps
                // anybody search for the right one.
                await RefuseAsync(stream, exchange, "this machine is not open to you", cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            await LinkFrame.WriteAsync(
                    stream, (byte)LinkFrameStatus.Ok, exchange, default, idle, cancellationToken)
                .ConfigureAwait(false);
            return admitted;
        }
    }

    private static Task RefuseAsync(Stream stream, Guid exchange, string reason, CancellationToken ct) =>
        LinkFrame.WriteAsync(
            stream, (byte)LinkFrameStatus.Failed, exchange, Encoding.UTF8.GetBytes(reason), idle: null, ct);
}
