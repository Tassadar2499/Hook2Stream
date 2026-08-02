using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using BclIPNetwork = System.Net.IPNetwork;

namespace Hook2Stream.Api;

internal sealed class TrustedReverseProxyOptions
{
    internal const string SectionName = "ReverseProxy";

    public bool Enabled { get; set; }
    public int ForwardLimit { get; set; } = 1;
    public bool TrustAllProxies { get; set; }
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
}

internal static class TrustedReverseProxy
{
    internal static TrustedReverseProxyOptions AddTrustedReverseProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration
            .GetSection(TrustedReverseProxyOptions.SectionName)
            .Get<TrustedReverseProxyOptions>() ?? new TrustedReverseProxyOptions();
        Validate(settings);

        if (!settings.Enabled)
        {
            return settings;
        }

        var knownProxies = settings.KnownProxies.Select(IPAddress.Parse).ToArray();
        var knownNetworks = settings.KnownNetworks.Select(BclIPNetwork.Parse).ToArray();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor |
                ForwardedHeaders.XForwardedHost |
                ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = settings.ForwardLimit;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            if (settings.TrustAllProxies)
            {
                return;
            }

            foreach (var proxy in knownProxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in knownNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });

        return settings;
    }

    private static void Validate(TrustedReverseProxyOptions settings)
    {
        if (settings.ForwardLimit is < 1 or > 10)
        {
            throw new InvalidOperationException(
                "ReverseProxy:ForwardLimit must be between 1 and 10.");
        }

        var invalidProxy = settings.KnownProxies.FirstOrDefault(value =>
            !IPAddress.TryParse(value, out _));
        if (invalidProxy is not null)
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies contains invalid IP address '{invalidProxy}'.");
        }

        var invalidNetwork = settings.KnownNetworks.FirstOrDefault(value =>
            !BclIPNetwork.TryParse(value, out _));
        if (invalidNetwork is not null)
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownNetworks contains invalid CIDR network '{invalidNetwork}'.");
        }

        if (settings.Enabled &&
            !settings.TrustAllProxies &&
            settings.KnownProxies.Length == 0 &&
            settings.KnownNetworks.Length == 0)
        {
            throw new InvalidOperationException(
                "ReverseProxy requires at least one KnownProxies/KnownNetworks entry, or explicit TrustAllProxies=true.");
        }
    }
}
