using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Injects JellyShim middlewares into the Jellyfin HTTP pipeline via <see cref="IStartupFilter"/>.
/// This wraps the entire pipeline so our middleware runs first on every request.
/// </summary>
public class JellyShimStartupFilter : IStartupFilter
{
    private readonly ILogger<JellyShimStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyShimStartupFilter"/> class.
    /// </summary>
    public JellyShimStartupFilter(ILogger<JellyShimStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        _logger.LogInformation("[JellyShim] IStartupFilter.Configure invoked — injecting middleware into pipeline");

        return app =>
        {
            if (JellyShimApplicationBuilderFactory.MiddlewareInjected)
            {
                _logger.LogInformation("[JellyShim] IStartupFilter skipped — middleware already injected by ApplicationBuilderFactory");
            }
            else
            {
                // Image optimization first (intercepts /Items/*/Images/*)
                app.UseMiddleware<ImageOptimizationMiddleware>();

                // Asset optimization (serves /web/* from cache, adds headers to plugin paths)
                app.UseMiddleware<AssetOptimizationMiddleware>();

                _logger.LogInformation("[JellyShim] Middleware registered via IStartupFilter (before Jellyfin pipeline)");
            }

            // Continue with the rest of the pipeline (Jellyfin's own middleware)
            next(app);
        };
    }
}
