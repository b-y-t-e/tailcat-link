// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tailcat.Keys;

namespace Tailcat.Net.Tests;

/// <summary>
/// Covers the identity a node presents: the long-lived key peers address it
/// by, and the throwaway TLS certificate its sessions are pinned to.
/// </summary>
public class NodeIdentityTests
{
    [Fact]
    public void IdentityKeepsTheKeyItIsGiven()
    {
        NodePrivate key = NodePrivate.NewKey();

        using NodeIdentity identity = NodeIdentity.Create(key);

        Assert.Equal(key.Public(), identity.PublicKey);
    }

    [Fact]
    public void IdentityGeneratesAKeyWhenGivenNone()
    {
        using NodeIdentity first = NodeIdentity.Create();
        using NodeIdentity second = NodeIdentity.Create();

        Assert.False(first.PublicKey.IsZero);
        Assert.NotEqual(first.PublicKey, second.PublicKey);
    }

    /// <summary>
    /// The fingerprint is what a peer pins, so it must be the hash of the
    /// certificate actually presented.
    /// </summary>
    [Fact]
    public void FingerprintIsTheHashOfTheCertificate()
    {
        using NodeIdentity identity = NodeIdentity.Create();

        Assert.Equal(SHA256.HashData(identity.Certificate.RawData), identity.Fingerprint);
        Assert.Equal(32, identity.Fingerprint.Length);
        Assert.True(NodeIdentity.MatchesFingerprint(identity.Certificate, identity.Fingerprint));
    }

    /// <summary>
    /// Any other certificate must fail the check: that comparison is the whole
    /// of the authentication, since no CA vouches for these.
    /// </summary>
    [Fact]
    public void AnotherCertificateDoesNotMatch()
    {
        using NodeIdentity identity = NodeIdentity.Create();
        using NodeIdentity impostor = NodeIdentity.Create();

        Assert.False(NodeIdentity.MatchesFingerprint(impostor.Certificate, identity.Fingerprint));
        Assert.False(NodeIdentity.MatchesFingerprint(null, identity.Fingerprint));
        Assert.False(NodeIdentity.MatchesFingerprint(identity.Certificate, new byte[32]));
    }

    /// <summary>Two identities never share a certificate, even on the same key.</summary>
    [Fact]
    public void CertificatesAreGeneratedFreshPerIdentity()
    {
        NodePrivate key = NodePrivate.NewKey();

        using NodeIdentity first = NodeIdentity.Create(key);
        using NodeIdentity second = NodeIdentity.Create(key);

        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    /// <summary>
    /// The certificate has to carry a usable private key and serve both roles:
    /// sessions authenticate in both directions.
    /// </summary>
    [Fact]
    public void CertificateCanActAsBothClientAndServer()
    {
        using NodeIdentity identity = NodeIdentity.Create();

        Assert.True(identity.Certificate.HasPrivateKey);

        X509EnhancedKeyUsageExtension usage = identity.Certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Single();
        List<string> oids = [.. usage.EnhancedKeyUsages.OfType<Oid>().Select(o => o.Value!)];

        Assert.Contains("1.3.6.1.5.5.7.3.1", oids); // server auth
        Assert.Contains("1.3.6.1.5.5.7.3.2", oids); // client auth
    }

    /// <summary>The certificate must be valid now, not from some future moment.</summary>
    [Fact]
    public void CertificateIsValidRightAway()
    {
        using NodeIdentity identity = NodeIdentity.Create();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(identity.Certificate.NotBefore.ToUniversalTime() <= now);
        Assert.True(identity.Certificate.NotAfter.ToUniversalTime() > now);
    }
}
