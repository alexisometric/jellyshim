using System.Diagnostics;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Configuration;
using Jellyfin.Plugin.JellyShim.Transformation;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Orchestrates the full asset optimization pipeline:
/// scan WebPath → minify (JS/CSS) → transform (HTML) → pre-compress (Brotli/Gzip) → store in disk cache.
/// </summary>
public class AssetProcessor
{
    private static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".css", ".html", ".htm", ".json", ".svg", ".xml", ".txt", ".map", ".mjs"
    };

    private static readonly HashSet<string> JsExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs"
    };

    private static readonly HashSet<string> CssExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css"
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm"
    };

    private readonly DiskCacheManager _cache;
    private readonly PreCompressor _compressor;
    private readonly JsTransformer _jsTransformer;
    private readonly CssTransformer _cssTransformer;
    private readonly ILogger<AssetProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetProcessor"/> class.
    /// </summary>
    public AssetProcessor(
        DiskCacheManager cache,
        PreCompressor compressor,
        JsTransformer jsTransformer,
        CssTransformer cssTransformer,
        ILogger<AssetProcessor> logger)
    {
        _cache = cache;
        _compressor = compressor;
        _jsTransformer = jsTransformer;
        _cssTransformer = cssTransformer;
        _logger = logger;
    }

    /// <summary>
    /// Processes all compressible assets in the given web path.
    /// </summary>
    /// <returns>Processing statistics.</returns>
    public async Task<ProcessingStats> ProcessAllAsync(string webPath, PluginConfiguration config, CancellationToken cancellationToken = default)
    {
        var stats = new ProcessingStats();
        var sw = Stopwatch.StartNew();

        if (!Directory.Exists(webPath))
        {
            _logger.LogWarning("[JellyShim] WebPath not found: {WebPath}", webPath);
            return stats;
        }

        _logger.LogInformation("[JellyShim] Starting asset optimization from {WebPath}", webPath);

        var files = Directory.GetFiles(webPath, "*.*", SearchOption.AllDirectories);
        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ext = Path.GetExtension(filePath);
            if (!CompressibleExtensions.Contains(ext))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(webPath, filePath).Replace('\\', '/');
            var sourceLastModified = File.GetLastWriteTimeUtc(filePath);

            // Skip if cache is already valid
            if (_cache.IsCacheValid(relativePath, "br", sourceLastModified) &&
                _cache.IsCacheValid(relativePath, "gz", sourceLastModified))
            {
                stats.Skipped++;
                continue;
            }

            try
            {
                var raw = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
                var originalSize = raw.Length;
                var processed = raw;

                // Apply minification based on file type
                if (config.EnableMinification)
                {
                    processed = MinifyContent(processed, ext);
                }

                // Store the raw optimized version (for ETag computation and non-compressed serving)
                await _cache.StoreAsync(relativePath, "raw", processed, cancellationToken).ConfigureAwait(false);

                // Pre-compress
                if (config.EnableCompression)
                {
                    var (brotli, gzip) = _compressor.CompressBoth(processed, config.BrotliCompressionLevel);
                    await _cache.StoreAsync(relativePath, "br", brotli, cancellationToken).ConfigureAwait(false);
                    await _cache.StoreAsync(relativePath, "gz", gzip, cancellationToken).ConfigureAwait(false);

                    stats.TotalOriginalBytes += originalSize;
                    stats.TotalBrotliBytes += brotli.Length;
                    stats.TotalGzipBytes += gzip.Length;
                }

                stats.Processed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[JellyShim] Failed to process {File}", relativePath);
                stats.Errors++;
            }
        }

        sw.Stop();
        stats.ElapsedMs = sw.ElapsedMilliseconds;

        _logger.LogInformation(
            "[JellyShim] Optimization complete: {Processed} processed, {Skipped} skipped, {Errors} errors in {Elapsed}ms. " +
            "Original: {OriginalKB}KB → Brotli: {BrKB}KB ({BrRatio:P0}), Gzip: {GzKB}KB ({GzRatio:P0})",
            stats.Processed, stats.Skipped, stats.Errors, stats.ElapsedMs,
            stats.TotalOriginalBytes / 1024,
            stats.TotalBrotliBytes / 1024,
            stats.TotalOriginalBytes > 0 ? 1.0 - ((double)stats.TotalBrotliBytes / stats.TotalOriginalBytes) : 0,
            stats.TotalGzipBytes / 1024,
            stats.TotalOriginalBytes > 0 ? 1.0 - ((double)stats.TotalGzipBytes / stats.TotalOriginalBytes) : 0);

        return stats;
    }

    private byte[] MinifyContent(byte[] content, string extension)
    {
        if (JsExtensions.Contains(extension))
        {
            return _jsTransformer.MinifyBytes(content);
        }

        if (CssExtensions.Contains(extension))
        {
            return _cssTransformer.MinifyBytes(content);
        }

        return content;
    }

    /// <summary>
    /// Statistics from a processing run.
    /// </summary>
    public sealed class ProcessingStats
    {
        /// <summary>Gets or sets the number of files processed.</summary>
        public int Processed { get; set; }

        /// <summary>Gets or sets the number of files skipped (cache valid).</summary>
        public int Skipped { get; set; }

        /// <summary>Gets or sets the number of files that failed.</summary>
        public int Errors { get; set; }

        /// <summary>Gets or sets the total original bytes before optimization.</summary>
        public long TotalOriginalBytes { get; set; }

        /// <summary>Gets or sets the total Brotli compressed bytes.</summary>
        public long TotalBrotliBytes { get; set; }

        /// <summary>Gets or sets the total Gzip compressed bytes.</summary>
        public long TotalGzipBytes { get; set; }

        /// <summary>Gets or sets the elapsed time in milliseconds.</summary>
        public long ElapsedMs { get; set; }
    }
}
