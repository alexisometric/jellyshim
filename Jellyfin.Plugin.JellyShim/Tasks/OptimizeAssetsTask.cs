using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Optimization;
using Jellyfin.Plugin.JellyShim.Transformation;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Tasks;

/// <summary>
/// Scheduled task that pre-optimizes all web assets at startup and on a daily schedule.
/// </summary>
public class OptimizeAssetsTask : IScheduledTask
{
    private readonly AssetProcessor _processor;
    private readonly FileTransformationBridge _bridge;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger<OptimizeAssetsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizeAssetsTask"/> class.
    /// </summary>
    public OptimizeAssetsTask(
        AssetProcessor processor,
        FileTransformationBridge bridge,
        IServerConfigurationManager configManager,
        ILogger<OptimizeAssetsTask> logger)
    {
        _processor = processor;
        _bridge = bridge;
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

        // Try to register with File Transformation plugin (if not already done)
        if (!_bridge.IsRegistered)
        {
            _bridge.TryRegister(config);
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
