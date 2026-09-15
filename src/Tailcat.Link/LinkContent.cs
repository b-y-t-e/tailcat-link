// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Runtime.InteropServices;
using System.Text;
using Tailcat.Link.Protocol;

namespace Tailcat.Link;

/// <summary>
/// What goes to the other machine — a request, a notification, or the answer a
/// handler gives — whatever its size.
/// </summary>
/// <remarks>
/// <para>
/// A kilobyte of JSON and twenty gigabytes of video are the same thing here.
/// Neither has a size limit; the content travels in blocks, is resumed from
/// where the other machine got to when a session dies, and is never held in
/// memory by the link unless it was handed over as memory in the first place.
/// </para>
/// <para>
/// Resuming needs content that can be read again from the middle: bytes, a
/// file, or a stream that can seek. A stream that only goes forwards still
/// works, up to the first time a session dies under it, and then ends the
/// exchange with an error that says so rather than delivering something with a
/// hole in it.
/// </para>
/// </remarks>
public sealed record LinkContent
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly bool _isBytes;
    private readonly Stream? _stream;
    private readonly bool _leaveOpen;
    private readonly string? _path;

    private LinkContent(ReadOnlyMemory<byte> bytes)
    {
        _bytes = bytes;
        _isBytes = true;
        Length = bytes.Length;
    }

    private LinkContent(Stream stream, bool leaveOpen)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        Length = stream.CanSeek ? stream.Length - stream.Position : null;
    }

    private LinkContent(string path)
    {
        _path = path;
        Name = Path.GetFileName(path);
        Length = new FileInfo(path).Length;
    }

    /// <summary>Nothing at all, which is what a handler with nothing to say returns.</summary>
    public static LinkContent Empty { get; } = new(ReadOnlyMemory<byte>.Empty);

    /// <summary>What the content is called. Shown to the other machine, never used by it as a path.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The media type, when the application wants to say one.</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>Whatever the application wants to travel in front of the content.</summary>
    public ReadOnlyMemory<byte> Metadata { get; init; }

    /// <summary>
    /// How many bytes there are, or null when nobody knows until the end.
    /// </summary>
    /// <remarks>
    /// Worked out for bytes, files and seekable streams. Set it for a stream
    /// that cannot seek but whose length is known: the other machine then
    /// refuses content that ends short rather than taking half of it as the
    /// whole.
    /// </remarks>
    public long? Length { get; init; }

    /// <summary>Told after each block that reaches the other machine.</summary>
    public IProgress<TransferProgress>? Progress { get; init; }

    /// <summary>Content already in memory. Not copied.</summary>
    public static LinkContent FromBytes(ReadOnlyMemory<byte> bytes) => new(bytes);

    /// <summary>Text, sent as UTF-8.</summary>
    public static LinkContent FromString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LinkContent(Encoding.UTF8.GetBytes(text)) { ContentType = "text/plain; charset=utf-8" };
    }

    /// <summary>Content read from a stream, starting where the stream now is.</summary>
    /// <param name="stream">Where to read from. Seekable to survive a session dying.</param>
    /// <param name="leaveOpen">
    /// Whether the stream is still the caller's once the content has gone. By
    /// default it is closed — which is what a handler returning a stream as
    /// its answer wants, since nobody else is left to close it.
    /// </param>
    public static LinkContent FromStream(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new LinkContent(stream, leaveOpen);
    }

    /// <summary>A file, read as it is sent and never held in memory.</summary>
    /// <remarks>
    /// Opened only when it is sent, and for shared reading, so that content
    /// taking an hour to cross does not lock the file against the rest of the
    /// machine. <see cref="Name"/> starts as the file's name.
    /// </remarks>
    public static LinkContent FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new LinkContent(path);
    }

    /// <summary>The bytes, when the content was handed over as bytes.</summary>
    internal bool TryGetBytes(out ReadOnlyMemory<byte> bytes)
    {
        bytes = _bytes;
        return _isBytes;
    }

    /// <summary>Opens the content for one exchange.</summary>
    /// <returns>The stream, and whether whoever sends it is the one to close it.</returns>
    internal (Stream Stream, bool Owned) Open()
    {
        if (_isBytes)
        {
            // Over the array itself where there is one, so that sending two
            // gigabytes does not first copy two gigabytes.
            return MemoryMarshal.TryGetArray(_bytes, out ArraySegment<byte> array) && array.Array is not null
                ? (new MemoryStream(array.Array, array.Offset, array.Count, writable: false), true)
                : (new MemoryStream(_bytes.ToArray(), writable: false), true);
        }
        if (_path is not null)
        {
            return (new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                TransferFrame.BlockBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan), true);
        }
        return (_stream!, !_leaveOpen);
    }

    /// <summary>
    /// The whole content as one array, for a machine that can only take it
    /// that way.
    /// </summary>
    /// <remarks>
    /// Throws what the runtime throws when it does not fit. That is the one
    /// limit on content, and it is the machine's rather than this library's.
    /// </remarks>
    internal async Task<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (_isBytes)
        {
            return _bytes;
        }
        (Stream stream, bool owned) = Open();
        try
        {
            using MemoryStream all = Length is long known ? new MemoryStream(checked((int)known)) : new MemoryStream();
            await stream.CopyToAsync(all, cancellationToken).ConfigureAwait(false);
            return all.GetBuffer().AsMemory(0, (int)all.Length);
        }
        finally
        {
            if (owned)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lets go of a stream nobody is going to send after all.</summary>
    internal async ValueTask ReleaseAsync()
    {
        if (_stream is not null && !_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
