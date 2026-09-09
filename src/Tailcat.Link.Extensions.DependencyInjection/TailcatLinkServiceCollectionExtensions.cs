// Copyright (c) Andrzej Ból and contributors
// SPDX-License-Identifier: BSD-3-Clause

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tailcat.Link.Extensions.DependencyInjection;

/// <summary>Registers a link host with the application's own lifetime.</summary>
public static class TailcatLinkServiceCollectionExtensions
{
    /// <summary>
    /// Adds an <see cref="ILinkHost"/> that comes up when the application does
    /// and is disposed when it stops.
    /// </summary>
    /// <remarks>
    /// The host's <see cref="LinkOptions.LoggerFactory"/> is filled in from
    /// the application's own if the caller left it unset, so what the link is
    /// doing lands wherever everything else does without being wired up twice.
    /// </remarks>
    /// <param name="services">Where to register it.</param>
    /// <param name="appName">
    /// Names the stored state, as for <see cref="TailcatLink.HostManyAsync"/>.
    /// </param>
    /// <param name="options">
    /// Builds the options from the application's services. A function rather
    /// than an <c>Action</c> because <see cref="LinkOptions"/> is a record and
    /// is meant to be built rather than mutated.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// If a host is already registered. One container holds one, because
    /// <see cref="ILinkHost"/> is resolved by its own type and a second
    /// registration could only be the one nobody gets.
    /// </exception>
    public static IServiceCollection AddTailcatLinkHost(
        this IServiceCollection services,
        string appName,
        Func<IServiceProvider, LinkOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);

        // Refused rather than deduplicated: TryAddSingleton would keep the
        // first registration and drop this one silently, and an application
        // asking for two independent pairings would host only one of them.
        if (services.Any(registered => registered.ServiceType == typeof(LinkHostSource)))
        {
            throw new InvalidOperationException(
                $"a link host is already registered in this container, so \"{appName}\" cannot be added; "
                + "one container holds one host, and a second pairing needs a container of its own");
        }

        services.AddSingleton(provider => new LinkHostSource(appName, Build, provider));

        // A proxy rather than the host itself: every IHostedService is
        // constructed before any of them runs, so a worker taking ILinkHost in
        // its constructor is built while the link is still being made. The
        // proxy is resolvable then and answers from the running host after.
        services.AddSingleton<ILinkHost>(provider =>
            new LinkHostProxy(provider.GetRequiredService<LinkHostSource>()));
        services.AddHostedService<LinkHostService>();
        return services;

        LinkOptions Build(IServiceProvider provider)
        {
            LinkOptions built = options?.Invoke(provider) ?? new LinkOptions();
            return built.LoggerFactory is null
                ? built with { LoggerFactory = provider.GetService<ILoggerFactory>() }
                : built;
        }
    }
}
