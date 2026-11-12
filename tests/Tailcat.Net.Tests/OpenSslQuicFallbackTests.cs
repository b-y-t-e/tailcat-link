// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Text.RegularExpressions;

namespace Tailcat.Net.Tests;

public sealed partial class OpenSslQuicFallbackTests
{
    [Fact]
    public void AWindowsWithoutTls13InSchannelIsGivenTheOpenSslBuild()
    {
        bool set = false;

        bool applied = OpenSslQuicFallback.Apply(oldWindows: true, besideApplication: true, alreadyDecided: false, () => set = true);

        Assert.True(applied);
        Assert.True(set);
    }

    [Theory]
    // Windows 11 keeps the copy Windows Update maintains.
    [InlineData(false, true, false)]
    // Nothing beside the application to load: switching would find no msquic.dll.
    [InlineData(true, false, false)]
    // The application decided for itself, either way.
    [InlineData(true, true, true)]
    public void EverythingElseIsLeftAsItIs(bool oldWindows, bool besideApplication, bool alreadyDecided)
    {
        bool set = false;

        bool applied = OpenSslQuicFallback.Apply(oldWindows, besideApplication, alreadyDecided, () => set = true);

        Assert.False(applied);
        Assert.False(set);
    }

    [Fact]
    public void TheTargetsLookForTheVersionTheBuildRestores()
    {
        // Tailcat.Link.targets finds the DLL by the version it names; restored
        // at any other, the fallback silently finds nothing.
        string root = RepositoryRoot();
        string targets = File.ReadAllText(Path.Combine(root, "src", "Tailcat.Link", "buildTransitive", "Tailcat.Link.targets"));
        string packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));

        string named = TargetsVersion().Match(targets).Groups[1].Value;
        string restored = PackagesVersion().Match(packages).Groups[1].Value;

        Assert.NotEmpty(named);
        Assert.Equal(restored, named);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, "Directory.Packages.props")))
            {
                return at.FullName;
            }
        }
        throw new InvalidOperationException("not inside the repository");
    }

    [GeneratedRegex(@"<_TailcatLinkMsQuicOpenSslVersion>([^<]+)</_TailcatLinkMsQuicOpenSslVersion>")]
    private static partial Regex TargetsVersion();

    [GeneratedRegex(@"Include=""Microsoft\.Native\.Quic\.MsQuic\.OpenSSL"" Version=""([^""]+)""")]
    private static partial Regex PackagesVersion();
}
