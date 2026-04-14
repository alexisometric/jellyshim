using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Custom <see cref="IApplicationBuilderFactory"/> that injects JellyShim middleware
/// before the framework builds the HTTP pipeline.
///
/// <para><b>Why not IStartupFilter?</b> Jellyfin 10.11 does not reliably consume
/// IStartupFilter for plugins. By replacing the factory itself,
/// we guarantee our middleware is in the pipeline regardless of host configuration.</para>
///
/// <para><b>Registration precedence:</b> Registered via <c>AddSingleton</c> in
/// <see cref="PluginServiceRegistrator"/> before the framework's <c>TryAddSingleton</c>
/// in ConfigureWebDefaults, so this factory takes precedence.</para>
///
/// <para><b>Middleware order:</b></para>
/// <list type="number">
///   <item><see cref="ImageOptimizationMiddleware"/> — intercepts /Items/*/Images/* first</item>
///   <item><see cref="AssetOptimizationMiddleware"/> — handles /web/* and plugin assets</item>
///   <item>Jellyfin's own middleware chain follows</item>
/// </list>
/// </summary>
public class JellyShimApplicationBuilderFactory : IApplicationBuilderFactory
{
    /// <summary>
    /// Set to <c>true</c> once this factory has injected middleware,
    /// so <see cref="JellyShimStartupFilter"/> can skip its own registration
    /// and avoid duplicating the pipeline.
    /// </summary>
    internal static bool MiddlewareInjected { get; private set; }

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JellyShimApplicationBuilderFactory> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyShimApplicationBuilderFactory"/> class.
    /// </summary>
    public JellyShimApplicationBuilderFactory(IServiceProvider serviceProvider, ILogger<JellyShimApplicationBuilderFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IApplicationBuilder CreateBuilder(IFeatureCollection serverFeatures)
    {
        var builder = new ApplicationBuilder(_serviceProvider, serverFeatures);

        _logger.LogInformation("[JellyShim] Injecting middleware via ApplicationBuilderFactory");

        // Image optimization first (intercepts /Items/*/Images/* before anything else)
        builder.UseMiddleware<ImageOptimizationMiddleware>();

        // Asset optimization (serves /web/* from cache, adds headers to plugin paths)
        builder.UseMiddleware<AssetOptimizationMiddleware>();

        MiddlewareInjected = true;
        _logger.LogInformation("[JellyShim] Middleware injection complete");

        return builder;
    }
}
