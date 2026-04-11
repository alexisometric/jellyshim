using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Custom <see cref="IApplicationBuilderFactory"/> that injects JellyShim middleware
/// before the framework builds the HTTP pipeline.
/// Registered via <c>AddSingleton</c> before the framework's <c>TryAddSingleton</c>,
/// this factory takes precedence and guarantees middleware is in the pipeline.
/// </summary>
public class JellyShimApplicationBuilderFactory : IApplicationBuilderFactory
{
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

        _logger.LogInformation("[JellyShim] Middleware injection complete");

        return builder;
    }
}
