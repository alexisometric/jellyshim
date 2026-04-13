using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Middleware;
using Jellyfin.Plugin.JellyShim.Optimization;
using Jellyfin.Plugin.JellyShim.Transformation;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim;

/// <summary>
/// Registers JellyShim services into the Jellyfin DI container.
/// This runs during ConfigureServices, before Build(), so registrations are
/// available when the host starts.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Cache — resolve cache path from IApplicationPaths
        serviceCollection.AddSingleton<DiskCacheManager>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<DiskCacheManager>>();
            return new DiskCacheManager(paths.CachePath, logger);
        });

        // Transformers
        serviceCollection.AddSingleton<JsTransformer>();
        serviceCollection.AddSingleton<CssTransformer>();

        // Compression
        serviceCollection.AddSingleton<PreCompressor>();

        // Image processing (native — no external service)
        serviceCollection.AddSingleton<ImageProcessor>();

        // Orchestrator
        serviceCollection.AddSingleton<AssetProcessor>();

        // ── Middleware injection ──────────────────────────────────────
        // Primary: Replace IApplicationBuilderFactory with our own that
        // pre-registers JellyShim middleware before the pipeline is built.
        // This runs before the framework's TryAddSingleton in ConfigureWebDefaults,
        // so our factory takes precedence.
        serviceCollection.AddSingleton<IApplicationBuilderFactory, JellyShimApplicationBuilderFactory>();

        // Fallback: IStartupFilter (may not be consumed depending on host configuration)
        serviceCollection.AddTransient<IStartupFilter, JellyShimStartupFilter>();
    }
}
