using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Middleware;
using Jellyfin.Plugin.JellyShim.Optimization;
using Jellyfin.Plugin.JellyShim.Transformation;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim;

/// <summary>
/// Registers JellyShim services into the Jellyfin DI container.
/// This runs during ConfigureServices, before Build(), so IStartupFilter is consumed.
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
        serviceCollection.AddSingleton<HtmlTransformer>();

        // Compression
        serviceCollection.AddSingleton<PreCompressor>();

        // Image processing (native — no external service)
        serviceCollection.AddSingleton<ImageProcessor>();

        // File Transformation bridge
        serviceCollection.AddSingleton<FileTransformationBridge>();

        // Orchestrator
        serviceCollection.AddSingleton<AssetProcessor>();

        // Inject middlewares into the pipeline via IStartupFilter
        serviceCollection.AddTransient<IStartupFilter, JellyShimStartupFilter>();
    }
}
