// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tailcat.Keys;

namespace Tailcat.Net;

/// <summary>
/// A node's identity: the long-lived key peers address it by, plus the
/// short-lived TLS certificate its QUIC sessions present.
/// </summary>
/// <remarks>
/// <para>
/// The two are tied together without a certificate authority. The node key is
/// the address: a peer that wants to reach us seals its messages to that key,
/// so only we can read them, and we can prove authorship the same way. The
/// TLS certificate is generated fresh and is meaningless on its own — what
/// makes it trustworthy is that its fingerprint is announced inside a sealed
/// message. Pinning that exact fingerprint is therefore as strong as the node
/// keys themselves, and no CA is involved.
/// </para>
/// <para>
/// A certificate is per-process and never written to disk; only the node key
/// is worth persisting.
/// </para>
/// </remarks>
public sealed class NodeIdentity : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private NodeIdentity(NodePrivate privateKey, X509Certificate2 certificate)
    {
        PrivateKey = privateKey;
        PublicKey = privateKey.Public();
        DiscoPublicKey = DiscoPrivate.ForNode(privateKey).Public();
        Certificate = certificate;
        Fingerprint = SHA256.HashData(certificate.RawData);
    }

    /// <summary>The node's private key. Its public half is the node's address.</summary>
    public NodePrivate PrivateKey { get; }

    /// <summary>The node's public key: what peers send to.</summary>
    // Derived once: Public() is an X25519 scalar multiplication.
    public NodePublic PublicKey { get; }

    /// <summary>
    /// The node's disco public key: the half of its identity that is safe to
    /// show on a direct path, and what its address advertises alongside
    /// <see cref="PublicKey"/>.
    /// </summary>
    // Derived once, as the derivation ends in a scalar multiplication.
    public DiscoPublic DiscoPublicKey { get; }

    /// <summary>The TLS certificate this node's QUIC sessions present.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The SHA-256 hash of <see cref="Certificate"/>, announced to peers.</summary>
    public byte[] Fingerprint { get; }

    /// <summary>Creates an identity, generating a node key if none is given.</summary>
    /// <param name="privateKey">An existing node key to reuse, or null for a fresh one.</param>
    public static NodeIdentity Create(NodePrivate? privateKey = null)
    {
        NodePrivate key = privateKey ?? NodePrivate.NewKey();
        return new NodeIdentity(key, CreateCertificate(key.Public()));
    }

    /// <summary>
    /// Reports whether <paramref name="certificate"/> is the one whose
    /// fingerprint a peer announced.
    /// </summary>
    /// <remarks>
    /// The comparison is fixed-time, so it can't be probed byte by byte.
    /// </remarks>
    public static bool MatchesFingerprint(X509Certificate2? certificate, ReadOnlySpan<byte> expected) =>
        certificate is not null &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), expected);

    private static X509Certificate2 CreateCertificate(NodePublic publicKey)
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // The subject is only a label: peers authenticate the fingerprint,
        // never the name. Naming the node key makes traces readable.
        CertificateRequest req = new(
            $"CN=tailcat-{Convert.ToHexStringLower(publicKey.Raw32())[..16]}",
            ecdsa,
            HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        // Spelled as OIDs, not friendly names: the name lookup is localized
        // and fails outright on a non-English system.
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid(ServerAuthOid), new Oid(ClientAuthOid)],
                false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 cert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));

        // The certificate has to round-trip through a PKCS#12 blob so its
        // private key is one the platform TLS stack will accept as a
        // credential. On Windows that rules out an ephemeral key set:
        // Schannel rejects it with SEC_E_UNKNOWN_CREDENTIALS. Without
        // PersistKeySet the key still goes away with the handle.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>Disposes the certificate.</summary>
    public void Dispose() => Certificate.Dispose();
}
