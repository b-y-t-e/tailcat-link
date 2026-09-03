// Copyright (c) Tailscale Inc & contributors
// Copyright (c) Andrzej Ból and contributors (.NET port)
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Tailcfg;

namespace Tailcat;

/// <summary>
/// Describes how to reach a server: its public key and which DERP relay
/// region to use. It is serialized into a <see cref="ConnBlob"/> for
/// exchange, via the wire types in Wire.cs.
/// </summary>
public sealed class ConnInfo : IEquatable<ConnInfo>
{
    /// <summary>The server's node public key.</summary>
    public NodePublic ServerPublic { get; set; }

    /// <summary>The server's public key for path discovery.</summary>
    /// <remarks>
    /// <para>
    /// It is deliberately independent of <see cref="ServerPublic"/>: disco
    /// packets carry this key in cleartext on direct UDP paths, while
    /// ServerPublic is the unguessable part of the server's address. It is
    /// derived from the node's private key by <see cref="DiscoPrivate.ForNode"/>.
    /// </para>
    /// <para>
    /// It is zero in blobs written before tailcat separated the two keys. Go
    /// refuses to connect to such a server, because its whole data plane is
    /// disco-based; this port meets peers over QUIC and never consults a
    /// peer's disco key, so it carries the field for wire compatibility and
    /// leaves old addresses working.
    /// </para>
    /// </remarks>
    public DiscoPublic ServerDiscoPublic { get; set; }

    /// <summary>
    /// If non-empty, lists the regions of a DERP map. Either
    /// <see cref="Region"/> or <see cref="RegionID"/> must be set. If Region
    /// is set the client can avoid doing a lookup to discover the DERP map,
    /// but the ConnBlob is longer.
    /// </summary>
    /// <remarks>
    /// As of 2023-09-22, a maximum of 1 region may be provided. In the
    /// future, a server might advertise its presence in multiple DERP
    /// regions and clients could try them all.
    /// </remarks>
    public List<DerpRegion> Region { get; set; } = [];

    /// <summary>
    /// The number of one of Tailscale's provided DERP servers. If set,
    /// <see cref="Region"/> may be omitted and the ConnBlob is shorter, at
    /// the cost of the client needing to fetch the DERP map from
    /// tailscale.com once at startup. If -1 (for use when saving a keypair
    /// to disk for reuse later), a region is selected automatically at
    /// startup based on latency.
    /// </summary>
    public int RegionID { get; set; }

    /// <summary>
    /// Serializes the ConnInfo into a compact <see cref="ConnBlob"/> string.
    /// </summary>
    /// <remarks>
    /// It is encoded via the wire types (see Wire.cs), which drop the DERP
    /// region fields tailcat doesn't use. Some other fields (RegionID,
    /// RegionCode, RegionName, node names that are redundant next to an
    /// explicit HostName) are zeroed before encoding to reduce size;
    /// <see cref="ConnBlob.Parse"/> restores them.
    /// </remarks>
    public ConnBlob ToConnBlob()
    {
        WireConnInfo w = new()
        {
            ServerPublic = ServerPublic,
            ServerDiscoPublic = ServerDiscoPublic,
            RegionID = RegionID,
        };
        foreach (DerpRegion r in Region)
        {
            WireRegion wr = WireRegion.Of(r);

            // Remove some fields before encoding to save space. The same
            // transforms are undone on the way back.
            wr.RegionID = 0;
            wr.RegionCode = "";
            wr.RegionName = "";
            foreach (WireNode n in wr.Nodes?.OfType<WireNode>() ?? [])
            {
                n.RegionID = 0;
                if (n.HostName.Length != 0)
                {
                    n.Name = "";
                }
            }
            (w.Region ??= []).Add(wr);
        }
        return ConnBlob.FromWire(w);
    }

    /// <summary>
    /// Populates <see cref="Region"/> from a DERP map if only
    /// <see cref="RegionID"/> was set. If Region is already populated, Expand
    /// is a no-op. When RegionID is -1, the best region is selected
    /// automatically via netcheck latency probes.
    /// </summary>
    /// <param name="options">
    /// How to obtain the DERP map. See <see cref="ExpandOptions"/>; a null
    /// value means the defaults.
    /// </param>
    /// <param name="cancellationToken">Cancels the DERP map fetch.</param>
    /// <exception cref="TailcatException">
    /// If the DERP map can't be fetched, or names no such region.
    /// </exception>
    public async Task ExpandAsync(ExpandOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ExpandOptions();

        foreach (DerpRegion r in Region)
        {
            if (r.RegionID == 0)
            {
                r.RegionID = 1;
            }
            foreach (DerpNode n in r.Nodes)
            {
                if (n.RegionID == 0)
                {
                    n.RegionID = r.RegionID;
                }
            }
        }

        if (Region.Count > 0 || RegionID == 0)
        {
            return;
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DerpMapFetcher.FetchTimeout);

        string dmSrc = "provided DERP map";
        DerpMap? dm = options.DerpMap;
        if (dm is null)
        {
            dmSrc = options.Url;
            try
            {
                dm = await DerpMapFetcher.FetchAsync(options, cts.Token).ConfigureAwait(false);
            }
            // cts is linked to the caller's token, so testing it would also
            // swallow the caller's own cancellation. Only our timeout should
            // become a TailcatException.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw new TailcatException($"fetching DERPMap for region {RegionID}: {ex.Message}", ex);
            }
        }

        if (RegionID == -1)
        {
            // Shuffle each DERP region's nodes.
            foreach (DerpRegion r in dm.Regions.Values)
            {
                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(r.Nodes));
            }

            int bestRegion = await options.RegionPicker.PickBestRegionAsync(dm, cts.Token).ConfigureAwait(false);
            if (bestRegion != 0)
            {
                // A picker is a public extension point; its contract does not
                // promise the ID is one of ours.
                if (!dm.Regions.TryGetValue(bestRegion, out DerpRegion? picked))
                {
                    throw new TailcatException(
                        $"the region picker chose region {bestRegion}, which is not in {dmSrc}");
                }
                RegionID = 0;
                Region = [picked];
                return;
            }

            // Netcheck failed? Just pick a random region from the map,
            // ignoring what's close to the user. Assume the server filtered
            // the map based on our IP when the Tailcat-Mode header was
            // "server".
            List<int> regIDs = [.. dm.Regions.Keys.Order()];
            if (regIDs.Count == 0)
            {
                throw new TailcatException("failed to auto-detect any regions");
            }
            RegionID = 0;
            Region.Add(dm.Regions[regIDs[Random.Shared.Next(regIDs.Count)]]);
            return;
        }

        if (!dm.Regions.TryGetValue(RegionID, out DerpRegion? region))
        {
            throw new TailcatException(
                $"connection string said only DERP RegionID {RegionID} but no such region in {dmSrc}");
        }
        Region.Add(region);
    }

    public bool Equals(ConnInfo? other) =>
        other is not null &&
        ServerPublic == other.ServerPublic &&
        ServerDiscoPublic == other.ServerDiscoPublic &&
        RegionID == other.RegionID &&
        Region.SequenceEqual(other.Region);

    public override bool Equals(object? obj) => Equals(obj as ConnInfo);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.Add(ServerPublic);
        h.Add(ServerDiscoPublic);
        h.Add(RegionID);
        foreach (DerpRegion r in Region)
        {
            h.Add(r);
        }
        return h.ToHashCode();
    }

    public override string ToString() =>
        $"ConnInfo{{ServerPublic={ServerPublic}, RegionID={RegionID}, Region=[{string.Join(", ", Region)}]}}";
}
