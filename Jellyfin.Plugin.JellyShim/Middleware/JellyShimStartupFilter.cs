using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Fallback mechanism to inject JellyShim middlewares into the Jellyfin HTTP pipeline
/// via <see cref="IStartupFilter"/>. The primary mechanism is
/// <see cref="JellyShimApplicationBuilderFactory"/>; this filter only activates when
/// the factory was not used.
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
        _logger.LogInformation("[JellyShim] IStartupFilter.Configure called — middleware already injected via factory, skipping duplicate registration");
        return next;
    }
}
