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
///
/// <para><b>Execution timing:</b> This runs during <c>ConfigureServices</c>, BEFORE
/// <c>Build()</c> is called. All registrations are available when the host starts.</para>
///
/// <para><b>Middleware injection strategy:</b> Jellyfin does NOT reliably consume
/// <see cref="IStartupFilter"/> for plugin middleware. The working approach is to
/// replace <see cref="IApplicationBuilderFactory"/> with our own implementation
/// (<see cref="JellyShimApplicationBuilderFactory"/>) that injects middleware when
/// the pipeline is being built. We register via <c>AddSingleton</c> (not Try*),
/// which runs before the framework's <c>TryAddSingleton</c> in ConfigureWebDefaults,
/// so our factory takes precedence.</para>
///
/// <para><b>Fallback:</b> <see cref="JellyShimStartupFilter"/> is also registered
/// as a safety net, but it checks <see cref="JellyShimApplicationBuilderFactory.MiddlewareInjected"/>
/// to avoid duplicating middleware in the pipeline.</para>
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // ── Core services ─────────────────────────────────────────────

        // Disk cache manager — resolves cache directory from Jellyfin's IApplicationPaths
        // so it's stored alongside other Jellyfin cache data (e.g. /var/cache/jellyfin/jellyshim/)
        serviceCollection.AddSingleton<DiskCacheManager>(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var logger = sp.GetRequiredService<ILogger<DiskCacheManager>>();
            return new DiskCacheManager(paths.CachePath, logger);
        });

        // ── Transformation ──────────────────────────────────────────
        // JS and CSS minifiers using NUglify. Stateless — safe as singletons.
        serviceCollection.AddSingleton<JsTransformer>();
        serviceCollection.AddSingleton<CssTransformer>();

        // ── Compression ─────────────────────────────────────────────
        // Brotli + Gzip pre-compressor. Stateless — safe as singleton.
        serviceCollection.AddSingleton<PreCompressor>();

        // ── Image processing ────────────────────────────────────────
        // Native image processor (ImageSharp + ffmpeg for AVIF).
        // Reads Jellyfin's configured ffmpeg path via IConfigurationManager.
        serviceCollection.AddSingleton<ImageProcessor>();

        // ── Orchestrator ────────────────────────────────────────────
        // Coordinates the scan → minify → compress → cache pipeline for scheduled tasks.
        serviceCollection.AddSingleton<AssetProcessor>();

        // ── Middleware injection ──────────────────────────────────────
        // Primary: Replace IApplicationBuilderFactory with our own that
        // pre-registers JellyShim middleware before the pipeline is built.
        // This runs before the framework's TryAddSingleton in ConfigureWebDefaults,
        // so our factory takes precedence and guarantees middleware is in the pipeline.
        serviceCollection.AddSingleton<IApplicationBuilderFactory, JellyShimApplicationBuilderFactory>();

        // Fallback: IStartupFilter — skips if ApplicationBuilderFactory already injected
        serviceCollection.AddTransient<IStartupFilter, JellyShimStartupFilter>();
    }
}
