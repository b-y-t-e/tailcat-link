// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Diagnostics.CodeAnalysis;
using System.Buffers.Text;
using System.Formats.Cbor;
using Tailcat.Cbor;
using Tailcat.Tailcfg;

namespace Tailcat;

/// <summary>
/// A compact, URL-safe string that a server gives to clients so they can
/// connect. It is the "tc"-prefixed base64url encoding of CBOR-encoded
/// <see cref="ConnInfo"/>. A typical ConnBlob looks like "tcomFwWC…".
/// </summary>
/// <param name="Value">The blob's text form, including the "tc" prefix.</param>
public readonly record struct ConnBlob(string Value)
{
    /// <summary>The prefix every ConnBlob starts with.</summary>
    public const string Prefix = "tc";

    /// <summary>Whether the blob holds no value at all.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Encodes a wire-form ConnInfo into a blob.</summary>
    internal static ConnBlob FromWire(WireConnInfo w)
    {
        byte[] x = CborMapper.Serialize(w);
        return new ConnBlob(Prefix + Base64Url.EncodeToString(x));
    }

    /// <summary>
    /// Decodes the blob into its wire form, without restoring the fields
    /// that <see cref="ConnInfo.ToConnBlob"/> elides. The returned value is
    /// only meant for JSON display, as by the CLI's "parse" subcommand: its
    /// JSON form shows just the fields the encoded blob actually carries.
    /// </summary>
    /// <exception cref="TailcatException">If the blob isn't well-formed.</exception>
    public WireConnInfo ParseRaw() => ParseWire(this);

    /// <summary>
    /// Decodes the blob back into a <see cref="ConnInfo"/>, restoring fields
    /// that were stripped during encoding (RegionID, RegionCode, node names).
    /// </summary>
    /// <exception cref="TailcatException">If the blob isn't well-formed.</exception>
    public ConnInfo Parse()
    {
        WireConnInfo w = ParseWire(this);
        ConnInfo ci = new()
        {
            ServerPublic = w.ServerPublic,
            ServerDiscoPublic = w.ServerDiscoPublic,
            RegionID = w.RegionID,
        };
        List<WireRegion?> regions = w.Region ?? [];
        for (int i = 0; i < regions.Count; i++)
        {
            // A CBOR null decodes to a null element, and blobs come from
            // untrusted places (a pasted address, a "tailcat=" TXT record), so
            // reject one rather than dereferencing it below.
            WireRegion wr = regions[i]
                ?? throw new TailcatException($"invalid connection blob: region {i} is null");
            List<WireNode?> nodes = wr.Nodes ?? [];
            for (int j = 0; j < nodes.Count; j++)
            {
                if (nodes[j] is null)
                {
                    throw new TailcatException($"invalid connection blob: region {i} node {j} is null");
                }
            }
            ci.Region.Add(wr.ToDerpRegion());
        }
        for (int ri = 0; ri < ci.Region.Count; ri++)
        {
            DerpRegion r = ci.Region[ri];
            if (r.RegionID == 0)
            {
                r.RegionID = ri + 1;
            }
            if (r.RegionCode.Length == 0)
            {
                r.RegionCode = r.RegionID.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            foreach (DerpNode n in r.Nodes)
            {
                if (n.Name.Length == 0)
                {
                    // Netcheck identifies nodes by Name, so give each a
                    // unique one.
                    n.Name = n.HostName;
                }
                if (n.RegionID == 0)
                {
                    n.RegionID = r.RegionID;
                }
            }
        }
        return ci;
    }

    /// <summary>
    /// Decodes the blob back into a <see cref="ConnInfo"/>, returning false
    /// instead of throwing if it isn't well-formed.
    /// </summary>
    public bool TryParse([NotNullWhen(true)] out ConnInfo? connInfo)
    {
        try
        {
            connInfo = Parse();
            return true;
        }
        catch (TailcatException)
        {
            connInfo = null;
            return false;
        }
    }

    /// <summary>
    /// Returns a self-contained equivalent of this blob with the DERP relay's
    /// details embedded, so that later use of the blob requires no network
    /// access to fetch the DERP map.
    /// </summary>
    /// <remarks>
    /// It is to a ConnBlob roughly what a DNS lookup is to a hostname: the
    /// resolved form is longer, works offline, and pins the relay details as
    /// they were at resolution time. If this blob already embeds its relay
    /// details, it is returned unchanged. The options are as documented on
    /// <see cref="ConnInfo.ExpandAsync"/>.
    /// </remarks>
    public async Task<ConnBlob> ResolveAsync(ExpandOptions? options = null, CancellationToken cancellationToken = default)
    {
        ConnInfo ci = Parse();
        if (ci.Region.Count > 0)
        {
            return this;
        }
        await ci.ExpandAsync(options, cancellationToken).ConfigureAwait(false);

        // Keep the blob short: two relay nodes suffice.
        foreach (DerpRegion r in ci.Region)
        {
            if (r.Nodes.Count > 2)
            {
                r.Nodes = [.. r.Nodes.Take(2)];
            }
        }
        ci.RegionID = 0;
        return ci.ToConnBlob();
    }

    // ParseWire decodes cb into its wire form, without restoring the fields
    // that ConnInfo.ToConnBlob elides.
    private static WireConnInfo ParseWire(ConnBlob cb)
    {
        string s = cb.Value ?? "";
        if (!s.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new TailcatException("server address doesn't start with \"tc\"");
        }
        byte[] x;
        try
        {
            x = Base64Url.DecodeFromChars(s.AsSpan(Prefix.Length));
        }
        catch (FormatException ex)
        {
            throw new TailcatException($"base64 decode: {ex.Message}", ex);
        }
        try
        {
            return CborMapper.Deserialize<WireConnInfo>(x);
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new TailcatException($"CBOR unmarshal: {ex.Message}", ex);
        }
    }

    /// <summary>Returns the blob's text form.</summary>
    public override string ToString() => Value ?? "";
}
