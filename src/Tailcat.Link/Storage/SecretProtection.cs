// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Tailcat.Link.Storage;

/// <summary>
/// Encrypts the identity key on its way to disk, using whatever the operating
/// system offers.
/// </summary>
/// <remarks>
/// Implementations are named, and the name is written next to the ciphertext,
/// so a file protected on one machine is refused with an explanation rather
/// than decrypted into nonsense on another.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>How this protection is recorded in the stored file.</summary>
    string Name { get; }

    /// <summary>Protects a secret on its way to disk.</summary>
    byte[] Protect(byte[] secret);

    /// <summary>Recovers a secret written by <see cref="Protect"/> on this machine.</summary>
    /// <exception cref="LinkException">If it cannot be recovered.</exception>
    byte[] Unprotect(byte[] protectedSecret);
}

/// <summary>Chooses the protection appropriate to the machine.</summary>
public static class SecretProtector
{
    /// <summary>
    /// DPAPI on Windows, file permissions elsewhere — the strongest thing
    /// available without asking the application for a password or taking a
    /// dependency on a desktop keyring daemon that a headless server will not
    /// be running.
    /// </summary>
    public static ISecretProtector ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsDpapiSecretProtector() : new FilePermissionSecretProtector();
}

/// <summary>
/// Windows protection: the key is encrypted to the user account, so the file
/// is useless to anyone else — including an administrator copying it to
/// another machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    // Entropy binds the ciphertext to this library's use of DPAPI, so a blob
    // from some other application of the same user cannot be substituted.
    private static readonly byte[] Entropy = "tailcat.link.identity.v1"u8.ToArray();

    /// <inheritdoc/>
    public string Name => "dpapi";

    /// <inheritdoc/>
    public byte[] Protect(byte[] secret) =>
        ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser);

    /// <inheritdoc/>
    public byte[] Unprotect(byte[] protectedSecret)
    {
        try
        {
            return ProtectedData.Unprotect(protectedSecret, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new LinkException(
                "the stored identity cannot be decrypted; it belongs to a different Windows user or machine", ex);
        }
    }
}

/// <summary>
/// Unix protection: the bytes are stored as they are, and the file they are
/// stored in is readable only by its owner.
/// </summary>
/// <remarks>
/// This is what OpenSSH does with a private key, and for the same reason:
/// encrypting a file with a key that has to sit next to it, unprotected, so
/// that an unattended service can start without a human typing a passphrase,
/// buys nothing. The protection is the file mode, which
/// <see cref="FileLinkStore"/> sets to 0600 inside a 0700 directory as the
/// file is created, never after.
/// </remarks>
public sealed class FilePermissionSecretProtector : ISecretProtector
{
    /// <inheritdoc/>
    public string Name => "file-permissions";

    /// <inheritdoc/>
    public byte[] Protect(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return (byte[])secret.Clone();
    }

    /// <inheritdoc/>
    public byte[] Unprotect(byte[] protectedSecret)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);
        return (byte[])protectedSecret.Clone();
    }
}
