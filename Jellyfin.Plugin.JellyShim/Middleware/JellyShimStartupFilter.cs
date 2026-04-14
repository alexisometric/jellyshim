using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Fallback middleware injection via <see cref="IStartupFilter"/>.
///
/// <para>This is a safety net in case <see cref="JellyShimApplicationBuilderFactory"/>
/// was not consumed by the host. Before adding middleware, it checks
/// <see cref="JellyShimApplicationBuilderFactory.MiddlewareInjected"/> to avoid
/// registering the same middleware twice, which would cause duplicate processing.</para>
///
/// <para>In practice, on Jellyfin 10.11, the ApplicationBuilderFactory path always
/// wins, so this filter logs "skipped" and does nothing. It's kept as insurance
/// for future Jellyfin versions that might change the hosting model.</para>
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
