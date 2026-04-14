using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Cache;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Tasks;

/// <summary>
/// Scheduled task that clears the JellyShim disk cache (all compressed, minified,
/// and image-optimized assets). After clearing, assets are re-optimized on next access
/// or on the next <see cref="OptimizeAssetsTask"/> run.
///
/// <para><b>No automatic triggers</b> — manual only. Can be triggered from
/// Jellyfin Dashboard → Scheduled Tasks or programmatically.</para>
/// </summary>
public class ClearCacheTask : IScheduledTask
{
    private readonly DiskCacheManager _cache;
    private readonly ILogger<ClearCacheTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClearCacheTask"/> class.
    /// </summary>
    public ClearCacheTask(DiskCacheManager cache, ILogger<ClearCacheTask> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyShim: Clear Cache";

    /// <inheritdoc />
    public string Key => "JellyShimClearCache";

    /// <inheritdoc />
    public string Description => "Clears all cached optimized assets (compressed JS/CSS, optimized images). Assets will be re-optimized on next access.";

    /// <inheritdoc />
    public string Category => "JellyShim";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var (fileCount, totalBytes) = _cache.GetCacheStats();
        _logger.LogInformation("[JellyShim] Clearing cache: {FileCount} files, {Size:F1} MB", fileCount, totalBytes / (1024.0 * 1024.0));

        progress.Report(10);
        _cache.InvalidateAll();
        progress.Report(100);

        _logger.LogInformation("[JellyShim] Cache cleared successfully");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No automatic triggers — manual only (from dashboard or config button)
        return [];
    }
}
