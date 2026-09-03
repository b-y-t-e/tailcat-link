// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Security.Cryptography.X509Certificates;
using BcCertificateRequest = Org.BouncyCastle.Tls.CertificateRequest;
using System.Text;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Tailcat.Keys;

namespace Tailcat.Derp;

/// <summary>
/// Establishes the TLS session to a DERP relay.
/// </summary>
/// <remarks>
/// <para>
/// It uses BouncyCastle's TLS stack rather than <c>SslStream</c> because a
/// DERP relay appends a self-signed Ed25519 "meta certificate" to its chain,
/// which is how it publishes its DERP public key (subject
/// <c>CN=derpkey&lt;hex&gt;</c>). Windows' Schannel rejects that chain
/// outright — the handshake fails with SEC_E_INVALID_TOKEN before any
/// validation callback runs — so the platform stack cannot talk to a DERP
/// relay at all.
/// </para>
/// <para>
/// Certificate validation is therefore ours to do, and it is done in full:
/// the meta certificate is set aside, and the remaining chain is verified
/// against the operating system's trusted roots with the host name checked,
/// exactly as a browser would.
/// </para>
/// </remarks>
public static class DerpTlsConnector
{
    // The subject prefix of the self-signed cert carrying the relay's DERP key.
    private const string MetaCertPrefix = "derpkey";

    /// <summary>The result of a TLS handshake with a relay.</summary>
    /// <param name="Stream">The encrypted stream, ready for the HTTP upgrade.</param>
    /// <param name="ServerKeyFromMetaCert">
    /// The relay's DERP public key as advertised in its meta certificate, if
    /// it sent one. The handshake later confirms it independently, so this is
    /// only a hint.
    /// </param>
    public readonly record struct TlsSession(Stream Stream, NodePublic? ServerKeyFromMetaCert);

    /// <summary>
    /// Runs the TLS handshake over <paramref name="transport"/> for
    /// <paramref name="hostName"/>.
    /// </summary>
    /// <param name="transport">The connected TCP stream.</param>
    /// <param name="hostName">The name to send as SNI and to check the cert against.</param>
    /// <param name="insecureSkipVerify">
    /// Skips certificate validation. Only for tests against a local relay
    /// with a self-signed cert; never set it against a public relay.
    /// </param>
    /// <param name="cancellationToken">Cancels the handshake.</param>
    /// <exception cref="DerpProtocolException">If the handshake or validation fails.</exception>
    public static async Task<TlsSession> ConnectAsync(
        Stream transport,
        string hostName,
        bool insecureSkipVerify = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrEmpty(hostName);

        DerpTlsClient client = new(hostName, insecureSkipVerify);
        try
        {
            DerpTlsStream stream = await DerpTlsStream
                .HandshakeAsync(transport, client, cancellationToken)
                .ConfigureAwait(false);
            return new TlsSession(stream, client.ServerKeyFromMetaCert);
        }
        catch (TlsFatalAlert ex)
        {
            throw new DerpProtocolException($"TLS handshake with {hostName} failed: {ex.Message}", ex);
        }
    }

    private sealed class DerpTlsClient(string hostName, bool insecureSkipVerify)
        : DefaultTlsClient(new BcTlsCrypto(new Org.BouncyCastle.Security.SecureRandom()))
    {
        public NodePublic? ServerKeyFromMetaCert { get; private set; }

        public override TlsAuthentication GetAuthentication() => new Authentication(this);

        protected override IList<ServerName> GetSniServerNames() =>
            [new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(hostName))];

        private void OnServerCertificate(TlsServerCertificate serverCertificate)
        {
            List<X509Certificate2> certs = [];
            foreach (Org.BouncyCastle.Tls.Crypto.TlsCertificate c in serverCertificate.Certificate.GetCertificateList())
            {
                certs.Add(X509CertificateLoader.LoadCertificate(c.GetEncoded()));
            }
            if (certs.Count == 0)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate, new DerpProtocolException("relay sent no certificate"));
            }

            // The meta cert is not part of the PKI chain: it is a self-signed
            // carrier for the relay's DERP key. Take the key, then set it
            // aside so it can't confuse chain building.
            List<X509Certificate2> chainCerts = [];
            foreach (X509Certificate2 cert in certs)
            {
                string cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                if (cn.StartsWith(MetaCertPrefix, StringComparison.Ordinal) &&
                    TryParseMetaCertKey(cn, out NodePublic key))
                {
                    ServerKeyFromMetaCert = key;
                    continue;
                }
                chainCerts.Add(cert);
            }

            if (insecureSkipVerify)
            {
                return;
            }
            Verify(chainCerts, hostName);
        }

        private static void Verify(List<X509Certificate2> chainCerts, string hostName)
        {
            if (chainCerts.Count == 0)
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    new DerpProtocolException("relay sent only a meta certificate"));
            }

            X509Certificate2 leaf = chainCerts[0];
            using X509Chain chain = new();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
            for (int i = 1; i < chainCerts.Count; i++)
            {
                chain.ChainPolicy.ExtraStore.Add(chainCerts[i]);
            }

            if (!chain.Build(leaf))
            {
                string why = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    new DerpProtocolException($"relay certificate for {hostName} is not trusted: {why}"));
            }
            if (!leaf.MatchesHostname(hostName))
            {
                throw new TlsFatalAlert(AlertDescription.bad_certificate,
                    new DerpProtocolException($"relay certificate does not match host name {hostName}"));
            }
        }

        // A meta cert's subject is "derpkey" followed by the hex of the
        // relay's DERP public key.
        private static bool TryParseMetaCertKey(string commonName, out NodePublic key)
        {
            key = default;
            string hex = commonName[MetaCertPrefix.Length..];
            if (hex.Length != NodePublic.RawLen * 2)
            {
                return false;
            }
            try
            {
                key = NodePublic.FromRaw32(Convert.FromHexString(hex));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private sealed class Authentication(DerpTlsClient owner) : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) =>
                owner.OnServerCertificate(serverCertificate);

            public TlsCredentials? GetClientCredentials(BcCertificateRequest certificateRequest) => null;
        }
    }
}
