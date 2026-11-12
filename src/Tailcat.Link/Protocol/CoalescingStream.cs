// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// Holds back small writes and hands them to the stream underneath as one,
/// when <see cref="SendAsync"/> is called.
/// </summary>
/// <remarks>
/// <para>
/// A small exchange is several writes — a frame header, its payload, a block
/// header, a block, the end marker — and every writer here flushes after
/// itself. On QUIC each became a packet of its own, and the later ones were
/// paced behind the first: every few requests a small answer's body landed a
/// whole round trip after its header, 95 ms on a path that took 41
/// (tools/field-test/RESULTS.md). <see cref="Stream.FlushAsync(CancellationToken)"/>
/// is therefore ignored here; only <see cref="SendAsync"/> sends.
/// </para>
/// <para>
/// A write that would take what is held past <see cref="CoalesceBytes"/> sends
/// what is held and then itself, unchanged: a large block was one write before
/// and still is. Write-only, and never disposes the stream it writes to.
/// </para>
/// </remarks>
internal sealed class CoalescingStream(Stream inner) : Stream
{
    /// <summary>
    /// Up to how much is held back: one small exchange, header and all, with
    /// room to spare, and well under a block, so a block is never copied.
    /// </summary>
    public const int CoalesceBytes = 16 * 1024;

    private readonly MemoryStream _held = new();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Writes everything held, in one write, and flushes.</summary>
    public async Task SendAsync(CancellationToken cancellationToken)
    {
        await SendHeldAsync(cancellationToken).ConfigureAwait(false);
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_held.Length + buffer.Length <= CoalesceBytes)
        {
            _held.Write(buffer.Span);
            return;
        }
        await SendHeldAsync(cancellationToken).ConfigureAwait(false);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Ignored on purpose: see the remarks.
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("written asynchronously only");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _held.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task SendHeldAsync(CancellationToken cancellationToken)
    {
        if (_held.Length == 0)
        {
            return;
        }
        await inner.WriteAsync(_held.GetBuffer().AsMemory(0, (int)_held.Length), cancellationToken).ConfigureAwait(false);
        _held.SetLength(0);
    }
}
