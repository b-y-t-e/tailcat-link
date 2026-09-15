// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

namespace Tailcat.Link.Protocol;

/// <summary>
/// One exchange this machine is sending, and how far it has got in both
/// directions.
/// </summary>
/// <remarks>
/// It belongs to the link rather than to a session for the same reason
/// <see cref="IncomingExchange"/> does: a session carries an attempt, and the
/// exchange is what survives one. The id the other machine knows it by, where
/// the content has got to, and how much of the answer has arrived are all here.
/// </remarks>
internal sealed class OutboundExchange
{
    private readonly Stream _stream;
    private readonly bool _owned;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource<IncomingTransfer> _answerStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _mu = new();

    private IncomingTransfer? _answer;
    private ReadOnlyMemory<byte>? _whole;
    private long _lastProgress;
    private bool _attempted;

    /// <param name="content">What to send. Opened here, once, for every attempt.</param>
    /// <param name="flags">What the other machine is asked for.</param>
    /// <param name="patience">
    /// How long nothing may move — no block either way, and no session to move
    /// one on — before the exchange is given up on. The only limit it has.
    /// </param>
    /// <param name="time">The clock patience is measured on.</param>
    /// <exception cref="LinkException">If the content is not the length it says it is.</exception>
    public OutboundExchange(LinkContent content, ExchangeFlags flags, TimeSpan patience, TimeProvider time)
    {
        Content = content;
        Flags = flags;
        Patience = patience;
        _time = time;
        _lastProgress = time.GetTimestamp();
        (_stream, _owned) = content.Open();

        if (content.Length is long announced && _stream.CanSeek && _stream.Length - _stream.Position != announced)
        {
            // Caught before a byte has crossed the network: the other machine
            // would refuse it at the end, after the whole of it had been sent.
            long actual = _stream.Length - _stream.Position;
            if (_owned)
            {
                _stream.Dispose();
            }
            throw new LinkException($"the content announces {announced} bytes and has {actual}");
        }

        Cursor = new OutboundTransfer(
            new TransferOffer
            {
                Name = content.Name,
                ContentType = content.ContentType,
                Length = content.Length,
                Metadata = content.Metadata,
            },
            _stream,
            content.Progress,
            time);
    }

    /// <summary>What the other machine knows the exchange by, on every attempt.</summary>
    public Guid Id => Cursor.Id;

    /// <summary>What is being sent.</summary>
    public LinkContent Content { get; }

    /// <summary>What the other machine is asked for.</summary>
    public ExchangeFlags Flags { get; }

    /// <summary>How long nothing may move.</summary>
    public TimeSpan Patience { get; }

    /// <summary>Where the content has got to.</summary>
    public OutboundTransfer Cursor { get; }

    /// <summary>
    /// Sent once and never again, whatever happens — a notification sent as a
    /// single frame, which the other machine cannot recognise having had.
    /// </summary>
    public bool OnlyOnce { get; set; }

    /// <summary>What happens when the caller lets go of the answer.</summary>
    public Func<ValueTask>? AnswerReleased { get; set; }

    /// <summary>The answer, once its header has arrived.</summary>
    public IncomingTransfer? Answer
    {
        get
        {
            lock (_mu)
            {
                return _answer;
            }
        }
    }

    /// <summary>Completes with the answer as soon as it starts to arrive.</summary>
    public Task<IncomingTransfer> AnswerStarted => _answerStarted.Task;

    /// <summary>
    /// Whether the content has gone further than it can be read again from,
    /// which a stream that only goes forwards cannot do.
    /// </summary>
    public bool CannotResume => !Cursor.CanRewind && Cursor.Sent > 0;

    /// <summary>How long nothing has moved, in either direction.</summary>
    public TimeSpan Stalled
    {
        get
        {
            long latest = Math.Max(Interlocked.Read(ref _lastProgress), Cursor.LastMoved);
            if (Answer is { } answer)
            {
                latest = Math.Max(latest, answer.LastArrived);
            }
            return _time.GetElapsedTime(latest);
        }
    }

    /// <summary>
    /// Whether this attempt may send the content without waiting to be told
    /// where to start — the first attempt, with content small enough to be one
    /// block, where the answer could only have been zero.
    /// </summary>
    public bool TakeFirstAttempt()
    {
        lock (_mu)
        {
            bool first = !_attempted;
            _attempted = true;
            return first && Cursor.Sent == 0 && Content.Length is long length && length <= TransferFrame.BlockBytes;
        }
    }

    /// <summary>The header for this attempt.</summary>
    public ExchangeHeader HeaderFor(bool pipelined) =>
        new(
            pipelined ? Flags | ExchangeFlags.Pipelined : Flags,
            Content.Length,
            Answer?.BytesReceived ?? 0,
            Content.Name,
            Content.ContentType,
            Content.Metadata);

    /// <summary>Takes the answer's header, the first time it arrives.</summary>
    public IncomingTransfer StartAnswer(AnswerHeader header)
    {
        IncomingTransfer answer;
        lock (_mu)
        {
            _answer ??= new IncomingTransfer(
                Id, string.Empty, header.ContentType, header.Length, header.Metadata, _time, AnswerReleased);
            answer = _answer;
        }
        Moved();
        _answerStarted.TrySetResult(answer);
        return answer;
    }

    /// <summary>Takes a whole answer, from a machine that sends answers in one frame.</summary>
    public void TakeWholeAnswer(ReadOnlyMemory<byte> whole)
    {
        IncomingTransfer answer;
        lock (_mu)
        {
            _answer ??= IncomingTransfer.FromBytes(Id, whole, string.Empty, _time);
            answer = _answer;
        }
        Moved();
        _answerStarted.TrySetResult(answer);
    }

    /// <summary>
    /// The whole content in memory, for a machine that takes it only that way.
    /// Read once, however many attempts it takes.
    /// </summary>
    /// <remarks>
    /// No limit but the runtime's: content larger than one array fails the
    /// way the runtime fails.
    /// </remarks>
    public async Task<ReadOnlyMemory<byte>> WholeContentAsync(CancellationToken cancellationToken)
    {
        if (_whole is { } whole)
        {
            return whole;
        }
        if (Content.TryGetBytes(out ReadOnlyMemory<byte> bytes))
        {
            _whole = bytes;
            return bytes;
        }
        Cursor.RewindTo(0);
        MemoryStream collected = Content.Length is long known ? new MemoryStream(checked((int)known)) : new MemoryStream();
        await _stream.CopyToAsync(collected, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> read = collected.GetBuffer().AsMemory(0, (int)collected.Length);
        _whole = read;
        return read;
    }

    /// <summary>Records progress that is not a block: an answer arriving whole, a header.</summary>
    public void Moved() => Interlocked.Exchange(ref _lastProgress, _time.GetTimestamp());

    /// <summary>Closes the content, when the exchange opened it.</summary>
    public async ValueTask ReleaseContentAsync()
    {
        if (_owned)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A heartbeat that went unanswered for its window on a session that was
/// moving other bytes all the while.
/// </summary>
/// <remarks>
/// Not a dead peer: its answer was queued behind the traffic that proves it
/// alive. The session is kept, and the next heartbeat asks again.
/// </remarks>
internal sealed class SessionBusyException(string message, Exception inner) : LinkException(message, inner);
