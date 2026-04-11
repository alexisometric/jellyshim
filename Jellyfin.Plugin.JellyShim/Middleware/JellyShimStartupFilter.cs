using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Injects JellyShim middlewares into the Jellyfin HTTP pipeline
/// via the <see cref="IStartupFilter"/> mechanism. This runs before Jellyfin's own
/// static file middleware so we can intercept and serve cached/compressed assets.
/// </summary>
public class JellyShimStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            // Image optimization first (intercepts /Items/*/Images/* before anything else)
            builder.UseMiddleware<ImageOptimizationMiddleware>();

            // Asset optimization (serves /web/* from cache, adds headers to plugin paths)
            builder.UseMiddleware<AssetOptimizationMiddleware>();

            next(builder);
        };
    }
}
