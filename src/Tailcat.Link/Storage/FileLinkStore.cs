// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

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
    private const int CurrentVersion = 1;

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
            return new LinkState
            {
                PrivateKey = NodePrivate.FromRaw32(key),
                HomeRegionId = stored.HomeRegionId,
                Pairing = stored.PairingToken is null || stored.PairingExpiresAt is null
                    ? null
                    : new PairingOffer(stored.PairingToken, stored.PairingExpiresAt.Value),
                PeerCode = stored.PeerCode is null ? null : InvitationCode.Parse(stored.PeerCode),
                PeerKey = stored.PeerKey is null ? default : NodePublic.FromRaw32(Convert.FromHexString(stored.PeerKey)),
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
                PairingToken = state.Pairing?.Token,
                PairingExpiresAt = state.Pairing?.ExpiresAt,
                PeerCode = state.PeerCode?.Value,
                PeerKey = state.IsPaired ? Convert.ToHexStringLower(state.PeerKey.Raw32()) : null,
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
        if (stored.Version != CurrentVersion)
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

        public string? PairingToken { get; init; }

        public DateTimeOffset? PairingExpiresAt { get; init; }

        public string? PeerCode { get; init; }

        public string? PeerKey { get; init; }
    }
}
