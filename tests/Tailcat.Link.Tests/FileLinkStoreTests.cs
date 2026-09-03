// Copyright (c) Tailscale Inc & contributors
// SPDX-License-Identifier: BSD-3-Clause

using Tailcat.Keys;
using Tailcat.Link.Storage;

namespace Tailcat.Link.Tests;

/// <summary>
/// Covers the file that everything else depends on: lose it, or write it
/// carelessly, and a machine nobody can reach is a machine nobody can reach.
/// </summary>
public class FileLinkStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tailcat-link-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private FileLinkStore Store(ISecretProtector? protector = null) => new(_root, protector);

    /// <summary>Everything a restart needs comes back exactly as it went in.</summary>
    [Fact]
    public async Task StateSurvivesARoundTrip()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();
        NodePrivate key = NodePrivate.NewKey();
        NodePublic peer = NodePrivate.NewKey().Public();
        InvitationCode code = InvitationCode.ForAddress(
            new ConnInfo { ServerPublic = peer, RegionID = 4 }.ToConnBlob(), "s3cret-token");
        PairingOffer offer = new("s3cret-token", DateTimeOffset.UtcNow.AddHours(1));

        await store.SaveAsync("demo", new LinkState
        {
            PrivateKey = key,
            HomeRegionId = 4,
            Pairing = offer,
            PeerCode = code,
            PeerKey = peer,
        }, ct);
        LinkState? loaded = await store.LoadAsync("demo", ct);

        Assert.NotNull(loaded);
        // The public key, not the private bytes: a key that derives the same
        // address is the only property that matters, and comparing what is
        // derived catches a clamping mistake that comparing bytes would not.
        Assert.Equal(key.Public(), loaded.PrivateKey.Public());
        Assert.Equal(4, loaded.HomeRegionId);
        Assert.Equal(code, loaded.PeerCode);
        Assert.Equal(offer, loaded.Pairing);
        Assert.Equal(peer, loaded.PeerKey);
        Assert.True(loaded.IsPaired);
    }

    /// <summary>A machine that has never been paired reads back as unpaired, not as broken.</summary>
    [Fact]
    public async Task AnIdentityWithoutAPairingIsFine()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();

        await store.SaveAsync("demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, ct);
        LinkState? loaded = await store.LoadAsync("demo", ct);

        Assert.NotNull(loaded);
        Assert.False(loaded.IsPaired);
        Assert.Null(loaded.PeerCode);
        Assert.Null(loaded.HomeRegionId);
    }

    /// <summary>Nothing stored is null, not an exception: it is how a first run looks.</summary>
    [Fact]
    public async Task NothingStoredReadsBackAsNull() =>
        Assert.Null(await Store().LoadAsync("never-used", TestContext.Current.CancellationToken));

    /// <summary>Saving twice leaves the newer pairing, and no debris beside it.</summary>
    [Fact]
    public async Task SavingAgainReplacesWhatWasThere()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();
        NodePrivate key = NodePrivate.NewKey();

        await store.SaveAsync("demo", new LinkState { PrivateKey = key }, ct);
        NodePublic peer = NodePrivate.NewKey().Public();
        await store.SaveAsync("demo", new LinkState { PrivateKey = key, PeerKey = peer }, ct);

        LinkState? loaded = await store.LoadAsync("demo", ct);
        Assert.Equal(peer, loaded?.PeerKey);
        // The temporary file the write goes through must not outlive it.
        Assert.Empty(Directory.GetFiles(_root, "*.new"));
    }

    /// <summary>Forgetting a pairing really removes it.</summary>
    [Fact]
    public async Task DeletingLeavesNothingBehind()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();

        await store.SaveAsync("demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, ct);
        await store.DeleteAsync("demo", ct);

        Assert.Null(await store.LoadAsync("demo", ct));
        Assert.False(File.Exists(store.PathFor("demo")));
    }

    /// <summary>Deleting what was never there is not an error.</summary>
    [Fact]
    public async Task DeletingNothingIsHarmless() =>
        await Store().DeleteAsync("never-used", TestContext.Current.CancellationToken);

    /// <summary>
    /// An application name becomes a filename, so anything that could escape
    /// the directory is refused outright.
    /// </summary>
    [Theory]
    [InlineData("../escape")]
    [InlineData("with/slash")]
    [InlineData("with\\backslash")]
    [InlineData(".hidden")]
    [InlineData("space here")]
    public void AnUnsafeApplicationNameIsRefused(string appName) =>
        Assert.Throws<ArgumentException>(() => Store().PathFor(appName));

    /// <summary>
    /// On Unix the identity is protected by the file mode alone, so the mode
    /// is the security property and is checked as one.
    /// </summary>
    [Fact]
    public async Task OnUnixTheFileIsReadableOnlyByItsOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("file modes are a Unix concept; Windows protects the key with DPAPI instead");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();
        await store.SaveAsync("demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, ct);

        UnixFileMode file = File.GetUnixFileMode(store.PathFor("demo"));
        UnixFileMode directory = File.GetUnixFileMode(_root);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, file);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, directory);
    }

    /// <summary>
    /// On Windows the key is encrypted to the user account, so the bytes on
    /// disk are not the key.
    /// </summary>
    [Fact]
    public async Task OnWindowsTheKeyIsNotStoredInTheClear()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI is a Windows facility");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();
        NodePrivate key = NodePrivate.NewKey();
        await store.SaveAsync("demo", new LinkState { PrivateKey = key }, ct);

        string contents = await File.ReadAllTextAsync(store.PathFor("demo"), ct);

        Assert.Contains("dpapi", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(key.Raw32()), contents, StringComparison.Ordinal);
        // And it is still the same key when it comes back.
        Assert.Equal(key.Public(), (await store.LoadAsync("demo", ct))?.PrivateKey.Public());
    }

    /// <summary>
    /// State written on one machine and copied to another is refused with an
    /// explanation, rather than decrypted into a key that is not a key.
    /// </summary>
    [Fact]
    public async Task StateFromAnotherPlatformIsRefusedWithAReason()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Store(new StubProtector("elsewhere")).SaveAsync(
            "demo", new LinkState { PrivateKey = NodePrivate.NewKey() }, ct);

        LinkException ex = await Assert.ThrowsAsync<LinkException>(
            () => Store(new StubProtector("here")).LoadAsync("demo", ct));

        Assert.Contains("elsewhere", ex.Message, StringComparison.Ordinal);
        Assert.Contains("here", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A file that is not link state says so, instead of failing later and elsewhere.</summary>
    [Fact]
    public async Task AFileThatIsNotLinkStateIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileLinkStore store = Store();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(store.PathFor("demo"), "not json at all", ct);

        await Assert.ThrowsAsync<LinkException>(() => store.LoadAsync("demo", ct));
    }

    // Names the protection without applying any, so the platform check can be
    // tested on the platform the test happens to run on.
    private sealed class StubProtector(string name) : ISecretProtector
    {
        public string Name => name;

        public byte[] Protect(byte[] secret) => secret;

        public byte[] Unprotect(byte[] protectedSecret) => protectedSecret;
    }
}
