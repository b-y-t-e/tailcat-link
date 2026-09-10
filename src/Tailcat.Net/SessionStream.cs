// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Net.Quic;

namespace Tailcat.Net;

/// <summary>
/// A stream of a QUIC session, saying why the session ended when the node is
/// what ended it.
/// </summary>
/// <remarks>
/// Almost nothing learns of an end from a fresh call: a transfer sits in a
/// read and a request sits in a write, and QUIC can only tell them that
/// something was disposed or aborted — which names the type that noticed and
/// nothing that happened. The relayed transport already carries the reason
/// into its parked reads (<see cref="Relay1.Relay1Stream"/>), and a pair that
/// has QUIC uses QUIC, so a reason only relay1 knew would be one almost
/// nobody in the field ever read.
/// </remarks>
/// <remarks>
/// The reason arrives as an <see cref="IOException"/>, which is what the
/// <see cref="Stream"/> contract has callers catch around a read and what
/// <see cref="Relay1.Relay1Stream"/> already throws. A type of this library's
/// own would make a consumer's reading loop survive a relayed session and fall
/// over a QUIC one, for the same event.
/// </remarks>
internal sealed class SessionStream(Stream inner, Func<string?> endedBecause) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        TranslatedAsync(new ValueTask(inner.FlushAsync(cancellationToken))).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        TranslatedAsync(inner.ReadAsync(buffer, cancellationToken));

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        TranslatedAsync(inner.WriteAsync(buffer, cancellationToken));

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private async ValueTask<int> TranslatedAsync(ValueTask<int> work)
    {
        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (Exception ended) when (ReasonFor(ended) is { } why)
        {
            throw new IOException(why, ended);
        }
    }

    private async ValueTask TranslatedAsync(ValueTask work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ended) when (ReasonFor(ended) is { } why)
        {
            throw new IOException(why, ended);
        }
    }

    // Only the ends the node caused are renamed: a peer that reset one stream,
    // or a caller that disposed its own, still gets QUIC's own account of it.
    private string? ReasonFor(Exception ex) =>
        ex is ObjectDisposedException or QuicException ? endedBecause() : null;
}
