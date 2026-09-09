// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Tailcat.Link.Json;

/// <summary>
/// Requests and answers as objects rather than bytes.
/// </summary>
/// <remarks>
/// <para>
/// A separate package, and extensions rather than members, so that
/// <see cref="ILink"/> stays one thing: bytes in, bytes out. What it is
/// <em>not</em> is a matter of taste — every application that speaks JSON
/// over a link writes this, and each writes it slightly differently, which is
/// exactly the sort of thing that belongs in a library.
/// </para>
/// <para>
/// Everything here takes a <see cref="JsonTypeInfo{T}"/> rather than reaching
/// for reflection, so an application that trims or compiles ahead of time
/// gets the same behaviour as one that does not. A
/// <c>JsonSerializerContext</c> is where those come from.
/// </para>
/// </remarks>
public static class LinkJsonExtensions
{
    /// <summary>Sends <paramref name="request"/> as JSON and reads the answer back.</summary>
    /// <exception cref="LinkException">As <see cref="ILink.RequestAsync"/>, or if the answer is not that shape.</exception>
    public static Task<TResponse> RequestAsync<TRequest, TResponse>(
        this ILink link,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        return ExchangeAsync(
            (payload, ct) => link.RequestAsync(payload, ct), request, requestType, responseType, cancellationToken);
    }

    /// <inheritdoc cref="RequestAsync{TRequest, TResponse}(ILink, TRequest, JsonTypeInfo{TRequest}, JsonTypeInfo{TResponse}, CancellationToken)"/>
    public static Task<TResponse> RequestAsync<TRequest, TResponse>(
        this ILinkPeer peer,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return ExchangeAsync(
            (payload, ct) => peer.RequestAsync(payload, ct), request, requestType, responseType, cancellationToken);
    }

    /// <summary>Sends <paramref name="message"/> as JSON, expecting no answer.</summary>
    public static Task NotifyAsync<TMessage>(
        this ILink link,
        TMessage message,
        JsonTypeInfo<TMessage> messageType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.NotifyAsync(Encode(message, messageType), cancellationToken);
    }

    /// <inheritdoc cref="NotifyAsync{TMessage}(ILink, TMessage, JsonTypeInfo{TMessage}, CancellationToken)"/>
    public static Task NotifyAsync<TMessage>(
        this ILinkPeer peer,
        TMessage message,
        JsonTypeInfo<TMessage> messageType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return peer.NotifyAsync(Encode(message, messageType), cancellationToken);
    }

    /// <summary>Answers the peer's JSON requests with JSON.</summary>
    public static void SetRequestHandler<TRequest, TResponse>(
        this ILink link,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        Func<TRequest, CancellationToken, Task<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(handler);
        link.OnRequest(async (payload, ct) =>
            Encode(await handler(Decode(payload, requestType), ct).ConfigureAwait(false), responseType));
    }

    /// <summary>Answers every peer's JSON requests with JSON.</summary>
    public static void SetRequestHandler<TRequest, TResponse>(
        this ILinkHost host,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        Func<ILinkPeer, TRequest, CancellationToken, Task<TResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(handler);
        host.SetRequestHandler(async (peer, payload, ct) =>
            Encode(await handler(peer, Decode(payload, requestType), ct).ConfigureAwait(false), responseType));
    }

    private static async Task<TResponse> ExchangeAsync<TRequest, TResponse>(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> send,
        TRequest request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        byte[] answer = await send(Encode(request, requestType), cancellationToken).ConfigureAwait(false);
        return Decode(answer, responseType);
    }

    private static byte[] Encode<T>(T value, JsonTypeInfo<T> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return JsonSerializer.SerializeToUtf8Bytes(value, type);
    }

    // A malformed answer is the other machine's fault and reads as one, rather
    // than as a JsonException nobody expected from a link.
    private static T Decode<T>(ReadOnlyMemory<byte> payload, JsonTypeInfo<T> type)
    {
        ArgumentNullException.ThrowIfNull(type);
        try
        {
            return JsonSerializer.Deserialize(payload.Span, type)
                ?? throw new LinkException($"the other machine sent \"null\" where a {typeof(T).Name} was expected");
        }
        catch (JsonException ex)
        {
            throw new LinkException($"the other machine sent something that is not a {typeof(T).Name}", ex);
        }
    }
}
