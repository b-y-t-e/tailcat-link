// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using System.Runtime.CompilerServices;

namespace Tailcat.Net;

/// <summary>
/// Gives a Windows without TLS 1.3 in Schannel — Windows 10, Server 2019 —
/// the OpenSSL build of msquic, and leaves every other Windows on its own.
/// </summary>
/// <remarks>
/// <para>
/// The msquic.dll .NET ships takes TLS 1.3 from Schannel, and
/// <c>System.Net.Quic</c> refuses it below build 20145; such a node offers
/// relay1 only and never leaves the relay. The OpenSSL build brings its own
/// TLS 1.3 and passes, because the runtime checks the Windows version only for
/// Schannel. <c>Tailcat.Link.targets</c> puts that build beside a
/// framework-dependent application as <c>msquic.dll</c>, where the runtime
/// does not look unless told to — and this tells it to, on such a Windows only.
/// Windows 11 keeps the copy Windows Update maintains.
/// </para>
/// <para>
/// A switch the application set itself is left alone, either way. And it has
/// to be decided before anything asks whether QUIC is supported: the runtime
/// reads the switch once. That is why it runs when this assembly loads — an
/// application that used QUIC before touching this library has decided
/// already, and gets what it decided.
/// </para>
/// </remarks>
internal static class OpenSslQuicFallback
{
    /// <summary>The runtime's switch: load msquic.dll from beside the application.</summary>
    internal const string AppLocalMsQuic = "System.Net.Quic.AppLocalMsQuic";

    // System.Net.Quic's own threshold (MsQuicApi.s_minWindowsVersion): the
    // first build whose Schannel has TLS 1.3.
    private const int FirstBuildWithTls13 = 20145;

    /// <summary>What was decided when the library loaded, for the warning that explains it.</summary>
    internal static bool Applied { get; private set; }

#pragma warning disable CA2255 // A library's module initializer, on purpose: it must run before anything in the process reads the runtime's switch, and the first use of this library is the latest that can be.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ApplyOnLoad()
    {
        try
        {
            Applied = Apply(
                oldWindows: OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstBuildWithTls13),
                besideApplication: File.Exists(Path.Combine(AppContext.BaseDirectory, "msquic.dll")),
                alreadyDecided: AppContext.TryGetSwitch(AppLocalMsQuic, out _),
                set: () => AppContext.SetSwitch(AppLocalMsQuic, true));
        }
#pragma warning disable CA1031 // Nothing here may stop the library loading: at worst the node offers relay1, as it would have anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The decision itself, apart from the process it is made in.</summary>
    internal static bool Apply(bool oldWindows, bool besideApplication, bool alreadyDecided, Action set)
    {
        if (!oldWindows || !besideApplication || alreadyDecided)
        {
            return false;
        }
        set();
        return true;
    }
}
