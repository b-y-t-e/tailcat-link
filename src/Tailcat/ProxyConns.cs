// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Sockets;

namespace Tailcat;

/// <summary>
/// A duplex byte stream that can shut down just its writing side, the way a
/// TCP connection can. It stands in for Go's <c>net.Conn</c> plus the
/// optional <c>CloseWrite</c> that <see cref="Proxy.ConnsAsync"/> looks for.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a duplex stream that can half-close; naming it anything else would obscure that.")]
public interface IHalfCloseableStream
{
    /// <summary>The stream to read from and write to.</summary>
    Stream Stream { get; }

    /// <summary>
    /// Shuts down the writing side, sending a FIN, while leaving the reading
    /// side open. Returns false if the stream can't half-close, in which case
    /// the proxy closes it outright.
    /// </summary>
    bool TryCloseWrite();
}

/// <summary>
/// Wraps a <see cref="Socket"/> as an <see cref="IHalfCloseableStream"/>,
/// which is what a real TCP connection is.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It presents a socket as a stream, which is what the name says.")]
public sealed class SocketStream : IHalfCloseableStream, IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;

    /// <summary>Wraps <paramref name="socket"/>, taking ownership of it.</summary>
    public SocketStream(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    /// <inheritdoc/>
    public Stream Stream => _stream;

    /// <inheritdoc/>
    public bool TryCloseWrite()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Closes the underlying socket.</summary>
    public void Dispose() => _stream.Dispose();
}

/// <summary>Copies data between two connections, as Go's <c>ProxyConns</c> does.</summary>
public static class Proxy
{
    /// <summary>
    /// Copies data between <paramref name="a"/> and <paramref name="b"/> in
    /// both directions until both sides have finished, then closes both
    /// connections.
    /// </summary>
    /// <remarks>
    /// When one direction's copy finishes (its source reached EOF), the
    /// destination gets a write shutdown via
    /// <see cref="IHalfCloseableStream.TryCloseWrite"/> if supported,
    /// propagating the TCP half-close instead of tearing down the whole
    /// connection. This lets protocols where one side signals end-of-request
    /// with a FIN and then reads the response (netcat style) work through the
    /// proxy.
    /// </remarks>
    public static async Task ConnsAsync(
        IHalfCloseableStream a,
        IHalfCloseableStream b,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        await Task.WhenAll(CopyAsync(a, b, cancellationToken), CopyAsync(b, a, cancellationToken))
            .ConfigureAwait(false);

        Close(a);
        Close(b);
    }

    private static async Task CopyAsync(
        IHalfCloseableStream src,
        IHalfCloseableStream dst,
        CancellationToken cancellationToken)
    {
        try
        {
            await src.Stream.CopyToAsync(dst.Stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // As in Go, a copy error just ends that direction; the shutdown
            // below still runs so the peer isn't left waiting.
        }

        if (!dst.TryCloseWrite())
        {
            Close(dst);
        }
    }

    private static void Close(IHalfCloseableStream s)
    {
        if (s is IDisposable d)
        {
            d.Dispose();
        }
        else
        {
            s.Stream.Dispose();
        }
    }
}
