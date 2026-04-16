using Jellyfin.Plugin.JellyShim.Cache;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyShim.Api;

/// <summary>
/// REST API controller for cache statistics, per-plugin cache management,
/// and performance dashboard data.
/// </summary>
[ApiController]
[Route("JellyShim")]
public class CacheStatsController : ControllerBase
{
    private readonly DiskCacheManager _cache;
    private readonly PerformanceStatsTracker _stats;

    /// <summary>Initializes a new instance of the <see cref="CacheStatsController"/> class.</summary>
    public CacheStatsController(DiskCacheManager cache, PerformanceStatsTracker stats)
    {
        _cache = cache;
        _stats = stats;
    }

    /// <summary>
    /// Gets cache statistics including file counts, sizes, and per-prefix breakdown.
    /// </summary>
    [HttpGet("CacheStats")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetCacheStats()
    {
        var overall = _cache.GetCacheStats();
        var byPrefix = _cache.GetCacheStatsByPrefix();

        return Ok(new
        {
            TotalFiles = overall.FileCount,
            TotalBytes = overall.TotalBytes,
            Categories = byPrefix
        });
    }

    /// <summary>
    /// Gets performance statistics (hit/miss rates, bytes served, etc.).
    /// </summary>
    [HttpGet("PerformanceStats")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPerformanceStats()
    {
        return Ok(_stats.GetSnapshot());
    }

    /// <summary>
    /// Clears cache entries for a specific prefix (e.g., "plugin", "ft", "img").
    /// </summary>
    [HttpDelete("Cache/{prefix}")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ClearCacheByPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return BadRequest("Prefix is required");
        }

        // Only allow prefixes that correspond to actual cache key paths (plugin/, ft/)
        var allowedPrefixes = new[] { "plugin", "ft" };
        if (!Array.Exists(allowedPrefixes, p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest("Invalid prefix. Allowed: " + string.Join(", ", allowedPrefixes));
        }

        _cache.InvalidatePrefix(prefix);
        return Ok(new { Message = $"Cache cleared for prefix: {prefix}" });
    }

    /// <summary>
    /// Resets performance statistics counters.
    /// </summary>
    [HttpPost("PerformanceStats/Reset")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetPerformanceStats()
    {
        _stats.Reset();
        return Ok(new { Message = "Performance statistics reset" });
    }
}
