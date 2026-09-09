// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tailcat.Keys;

namespace Tailcat.Link.Storage;

/// <summary>
/// Keeps each application's link state in one file under the user's own
/// directory, with the identity key protected as well as the platform allows.
/// </summary>
/// <remarks>
/// <para>
/// Windows: <c>%LOCALAPPDATA%\Tailcat\&lt;app&gt;.link.json</c>, with the key
/// encrypted to the user account by DPAPI. Unix:
/// <c>$XDG_DATA_HOME/tailcat/&lt;app&gt;.link.json</c> (or
/// <c>~/.local/share/tailcat</c>), created 0600 inside a 0700 directory.
/// </para>
/// <para>
/// The file is written to a temporary name and moved into place, so a machine
/// that loses power mid-write comes back with the previous identity rather
/// than half of a new one — which would be the one failure this whole design
/// cannot recover from, since it would lose the pairing.
/// </para>
/// </remarks>
public sealed class FileLinkStore : ILinkStore
{
    /// <summary>
    /// The shape written today: version 1 held one peer and one offer,
    /// version 2 holds a list of each.
    /// </summary>
    /// <remarks>
    /// A version 1 file is read and folded into the new shape, and written
    /// back as version 2. A build older than this one refuses a version 2
    /// file rather than silently losing every peer but the first, which is
    /// the case to think about before rolling a deployment back.
    /// </remarks>
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly ISecretProtector _protector;

    /// <summary>Creates a store under the platform's default directory.</summary>
    /// <param name="root">Where to keep the files. Defaults to <see cref="DefaultRoot"/>.</param>
    /// <param name="protector">
    /// How to protect the identity key. Defaults to
    /// <see cref="SecretProtector.ForCurrentPlatform"/>.
    /// </param>
    public FileLinkStore(string? root = null, ISecretProtector? protector = null)
    {
        _root = root ?? DefaultRoot();
        _protector = protector ?? SecretProtector.ForCurrentPlatform();
    }

    /// <summary>The directory link state is kept in when none is given.</summary>
    public static string DefaultRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tailcat");
        }

        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "tailcat");
    }

    /// <summary>The file <paramref name="appName"/>'s state is kept in.</summary>
    public string PathFor(string appName) => Path.Combine(_root, ValidName(appName) + ".link.json");

    /// <inheritdoc/>
    public async Task<LinkState?> LoadAsync(string appName, CancellationToken cancellationToken = default)
    {
        string path = PathFor(appName);
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        StoredLink stored = Deserialize(json, path);

        if (!string.Equals(stored.Protector, _protector.Name, StringComparison.Ordinal))
        {
            throw new LinkException(
                $"{path} was protected with \"{stored.Protector}\" but this machine uses \"{_protector.Name}\"; " +
                "link state does not travel between machines — delete the file to pair again");
        }

        byte[] key = _protector.Unprotect(Convert.FromBase64String(stored.PrivateKey));
        try
        {
            (IReadOnlyList<PairingOffer> pairings, IReadOnlyList<PairedPeer> peers) = ReadPairings(stored);
            return new LinkState
            {
                PrivateKey = NodePrivate.FromRaw32(key),
                HomeRegionId = stored.HomeRegionId,
                Pairings = pairings,
                Peers = peers,
                PeerCode = stored.PeerCode is null ? null : InvitationCode.Parse(stored.PeerCode, CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new LinkException($"{path} is not readable as link state", ex);
        }
        finally
        {
            // The raw key existed for as long as it took to parse it, and no
            // longer: a heap that still holds it is a heap that can be dumped.
            Array.Clear(key);
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string appName, LinkState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        string path = PathFor(appName);
        CreateRoot();

        byte[] raw = state.PrivateKey.Raw32();
        StoredLink stored;
        try
        {
            stored = new StoredLink
            {
                Version = CurrentVersion,
                Protector = _protector.Name,
                PrivateKey = Convert.ToBase64String(_protector.Protect(raw)),
                HomeRegionId = state.HomeRegionId,
                PeerCode = state.PeerCode?.Value,
                Pairings = [.. state.Pairings.Select(StoredOffer.From)],
                Peers = [.. state.Peers.Select(StoredPeer.From)],
            };
        }
        finally
        {
            Array.Clear(raw);
        }

        // Same directory as the target, so the move is a rename within one
        // filesystem and therefore atomic. The name is unique per write: a
        // fixed one means two saves at once fight over the same exclusively
        // opened file, and the loser writes nothing at all.
        string temporary = $"{path}.{Guid.NewGuid():n}.new";
        try
        {
            await using (FileStream file = Create(temporary))
            {
                await JsonSerializer.SerializeAsync(file, stored, Json, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A write that failed part way must not leave its half behind for
            // good; the state itself is unharmed, since it is still whatever
            // the last completed move put there.
            File.Delete(temporary);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string appName, CancellationToken cancellationToken = default)
    {
        string path = PathFor(appName);
        // Forgetting a pairing that was never made is what an application does
        // on uninstall, so it must not fail because nothing was written.
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private void CreateRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(_root);
            return;
        }
        Directory.CreateDirectory(
            _root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // The mode is part of creating the file, not a step after it: a file that
    // is briefly world-readable while it holds a private key is a file that
    // was briefly readable by the whole machine.
    private static FileStream Create(string path) =>
        OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });

    private static StoredLink Deserialize(string json, string path)
    {
        StoredLink? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredLink>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new LinkException($"{path} is not readable as link state", ex);
        }
        if (stored is null || stored.PrivateKey.Length == 0)
        {
            throw new LinkException($"{path} holds no identity");
        }
        if (stored.Version > CurrentVersion)
        {
            throw new LinkException(
                $"{path} was written by a newer version of this library (format {stored.Version})");
        }
        return stored;
    }

    private static string ValidName(string appName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        // The name becomes a filename, so it is checked rather than escaped:
        // an application name is chosen by a developer once, and a rejected
        // one is a five-second fix, while an escaped one is a path traversal
        // waiting to be found.
        foreach (char c in appName)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                throw new ArgumentException(
                    $"the application name may only hold letters, digits, '-', '_' and '.', not '{c}'",
                    nameof(appName));
            }
        }
        if (appName.Length > 64 || appName.StartsWith('.'))
        {
            throw new ArgumentException(
                "the application name must be at most 64 characters and may not start with '.'", nameof(appName));
        }
        return appName;
    }

    // The on-disk shape, deliberately separate from LinkState: the file is a
    // compatibility surface, and the domain type should be free to change
    // without rewriting everyone's stored pairing.
    private sealed class StoredLink
    {
        public int Version { get; init; }

        public string Protector { get; init; } = "";

        public string PrivateKey { get; init; } = "";

        public int? HomeRegionId { get; init; }

        public string? PeerCode { get; init; }

        public IReadOnlyList<StoredOffer>? Pairings { get; init; }

        public IReadOnlyList<StoredPeer>? Peers { get; init; }

        /// <summary>The single offer a version 1 file held. Read, never written.</summary>
        public string? PairingToken { get; init; }

        /// <inheritdoc cref="PairingToken"/>
        public DateTimeOffset? PairingExpiresAt { get; init; }

        /// <summary>The single peer a version 1 file held. Read, never written.</summary>
        public string? PeerKey { get; init; }
    }

    private sealed class StoredOffer
    {
        public Guid Id { get; init; }

        public string Token { get; init; } = "";

        public DateTimeOffset ExpiresAt { get; init; }

        public string? Label { get; init; }

        public bool SingleUse { get; init; }

        public static StoredOffer From(PairingOffer offer) => new()
        {
            Id = offer.Id,
            Token = offer.Token,
            ExpiresAt = offer.ExpiresAt,
            Label = offer.Label,
            SingleUse = offer.SingleUse,
        };

        public PairingOffer ToOffer() =>
            new(Token, ExpiresAt) { Id = Id, Label = Label, SingleUse = SingleUse };
    }

    private sealed class StoredPeer
    {
        public string Key { get; init; } = "";

        public string? Name { get; init; }

        public DateTimeOffset PairedAt { get; init; }

        public DateTimeOffset LastSeen { get; init; }

        /// <summary>Absent for a peer written before invitations were named.</summary>
        public Guid? InvitationId { get; init; }

        public static StoredPeer From(PairedPeer peer) => new()
        {
            Key = Convert.ToHexStringLower(peer.Key.Raw32()),
            Name = peer.Name,
            PairedAt = peer.PairedAt,
            LastSeen = peer.LastSeen,
            InvitationId = peer.InvitationId,
        };

        public PairedPeer ToPeer() =>
            new(NodePublic.FromRaw32(Convert.FromHexString(Key)), Name, PairedAt, LastSeen)
            {
                InvitationId = InvitationId,
            };
    }

    // Version 1 held exactly one of each. Folding it into a one-element list
    // here is the whole of the migration: nothing above this class ever learns
    // that the file had another shape. The two are read together because the
    // fold has to tie them to one another.
    private static (IReadOnlyList<PairingOffer> Offers, IReadOnlyList<PairedPeer> Peers) ReadPairings(StoredLink stored)
    {
        if (stored.Pairings is null && stored.Peers is null)
        {
            return FoldVersion1(stored);
        }
        return (
            [.. (stored.Pairings ?? []).Select(offer => offer.ToOffer())],
            [.. (stored.Peers ?? []).Select(peer => peer.ToPeer())]);
    }

    /// <summary>Reads the single offer and single peer a version 1 file held.</summary>
    /// <remarks>
    /// The two are given one invitation id, though the file never recorded
    /// that they belonged together — and in version 1 they always did, since
    /// there was only ever one of each and the offer outlived the pairing.
    /// Without the id <see cref="PairingRecord.ForgetPeerAsync"/> would drop
    /// the machine and leave the token it came in on: an unpaired device would
    /// walk back in on the same still-valid code the moment the host had room,
    /// which is the way back that unpairing is meant to close. The id is
    /// minted rather than read because there is nothing to read; the next save
    /// writes the file as version 2 and it is a stored fact from then on.
    /// </remarks>
    private static (IReadOnlyList<PairingOffer>, IReadOnlyList<PairedPeer>) FoldVersion1(StoredLink stored)
    {
        Guid invitationId = Guid.NewGuid();
        IReadOnlyList<PairingOffer> offers = stored.PairingToken is null || stored.PairingExpiresAt is null
            ? []
            : [new PairingOffer(stored.PairingToken, stored.PairingExpiresAt.Value) { Id = invitationId }];
        if (stored.PeerKey is null)
        {
            return (offers, []);
        }

        // A version 1 file recorded neither when the pairing was made nor when
        // the peer was last seen. The file's own timestamps are no better a
        // guess than this one, and nothing depends on it but a display.
        DateTimeOffset unknown = DateTimeOffset.UnixEpoch;
        PairedPeer peer = new(NodePublic.FromRaw32(Convert.FromHexString(stored.PeerKey)), null, unknown, unknown)
        {
            InvitationId = offers.Count == 0 ? null : invitationId,
        };
        return (offers, [peer]);
    }
}
