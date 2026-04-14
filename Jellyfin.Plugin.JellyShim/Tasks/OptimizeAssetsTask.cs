using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Optimization;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Tasks;

/// <summary>
/// Scheduled task that pre-optimizes all web assets (minify + compress + cache).
///
/// <para><b>Default triggers:</b></para>
/// <list type="bullet">
///   <item><b>Startup:</b> Runs when Jellyfin starts to warm the cache after the
///     AssetOptimizationMiddleware has cleared it.</item>
///   <item><b>Daily at 4 AM:</b> Re-processes to catch any Jellyfin web client updates.</item>
/// </list>
///
/// <para>Can also be triggered manually from the Jellyfin Dashboard → Scheduled Tasks.</para>
///
/// <para><b>Incremental:</b> Files with valid (non-stale) cache entries are skipped.
/// Only new or modified files are processed.</para>
/// </summary>
public class OptimizeAssetsTask : IScheduledTask
{
    private readonly AssetProcessor _processor;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<OptimizeAssetsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizeAssetsTask"/> class.
    /// </summary>
    public OptimizeAssetsTask(
        AssetProcessor processor,
        IServerConfigurationManager configManager,
        ILogger<OptimizeAssetsTask> logger)
    {
        _processor = processor;
        _configManager = configManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "JellyShim: Optimize Web Assets";

    /// <inheritdoc />
    public string Key => "JellyShimOptimizeAssets";

    /// <inheritdoc />
    public string Description => "Pre-optimizes Jellyfin web client assets (minify, compress, cache) for improved Lighthouse scores.";

    /// <inheritdoc />
    public string Category => "JellyShim";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            _logger.LogWarning("[JellyShim] Plugin instance not available");
            return;
        }

        var config = plugin.Configuration;
        var webPath = _configManager.ApplicationPaths.WebPath;

        if (string.IsNullOrEmpty(webPath))
        {
            _logger.LogWarning("[JellyShim] WebPath is not configured");
            return;
        }

        progress.Report(5);

        // Run the asset optimization pipeline
        var stats = await _processor.ProcessAllAsync(webPath, config, cancellationToken).ConfigureAwait(false);

        progress.Report(100);

        _logger.LogInformation(
            "[JellyShim] Task complete — {Processed} files optimized, {Skipped} cached, {Errors} errors, {Elapsed}ms",
            stats.Processed, stats.Skipped, stats.Errors, stats.ElapsedMs);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            // Run at startup
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            },
            // Run daily at 4 AM
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }
}
