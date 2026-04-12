using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Optimization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Middleware that intercepts Jellyfin image requests and processes them natively
/// using ImageSharp for on-the-fly resizing, quality reduction, and format conversion.
/// No external service required — all processing happens in-process.
/// </summary>
public partial class ImageOptimizationMiddleware
{
    // Matches all Jellyfin image endpoints:
    //   /Items/{id}/Images/{type}[/{index}]
    //   /Users/{id}/Images/{type}
    //   /Artists|Genres|MusicGenres|Persons|Studios|Years/{name}/Images/{type}
    // Also handles /emby/ and /mediabrowser/ compatibility prefixes
    [GeneratedRegex(@"^(?:/emby|/mediabrowser)?/(?:Items|Users|Artists|Genres|MusicGenres|Persons|Studios|Years)/[^/]+/Images/([^/?]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ImagePathRegex();

    private readonly RequestDelegate _next;
    private readonly DiskCacheManager _cache;
    private readonly ImageProcessor _imageProcessor;
    private readonly ILogger<ImageOptimizationMiddleware> _logger;

    /// <summary>Maximum original image size we'll attempt to process (50 MB).</summary>
    private const long MaxCaptureBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageOptimizationMiddleware"/> class.
    /// </summary>
    public ImageOptimizationMiddleware(
        RequestDelegate next,
        DiskCacheManager cache,
        ImageProcessor imageProcessor,
        ILogger<ImageOptimizationMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _imageProcessor = imageProcessor;
        _logger = logger;
        _logger.LogInformation("[JellyShim] ImageOptimizationMiddleware instantiated");
    }

    /// <summary>
    /// Processes the request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value;

        if (request.Method != HttpMethods.Get || path is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var config = plugin.Configuration;

        if (!config.EnableImageOptimization)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var match = ImagePathRegex().Match(path);
        if (!match.Success)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var imageType = match.Groups[1].Value;
        var (maxWidth, quality) = GetImageSettings(imageType, config);
        quality = Math.Clamp(quality, 1, 100);
        maxWidth = Math.Max(0, maxWidth);
        var format = NegotiateFormat(request, config);

        // Build cache key from path + query + processing parameters
        var cacheKey = BuildCacheKey(path, request.QueryString.Value, maxWidth, quality, format);

        // Try cache first
        if (config.EnableImageCache && _cache.TryGetCachedFile(cacheKey, "img", out var cachedPath))
        {
            await ServeCachedImage(context, cachedPath, format, config).ConfigureAwait(false);
            return;
        }

        // Capture Jellyfin's response by wrapping the body stream
        var originalBody = context.Response.Body;
        using var captureStream = new MemoryStream();
        context.Response.Body = captureStream;

        try
        {
            await _next(context).ConfigureAwait(false);

            // Restore original body before writing anything
            context.Response.Body = originalBody;

            // Only process successful responses within size limits
            if (context.Response.StatusCode != StatusCodes.Status200OK ||
                captureStream.Length == 0 ||
                captureStream.Length > MaxCaptureBytes)
            {
                captureStream.Seek(0, SeekOrigin.Begin);
                context.Response.ContentLength = captureStream.Length;
                await captureStream.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var originalBytes = captureStream.ToArray();

            byte[] processed;
            try
            {
                processed = _imageProcessor.Process(originalBytes, maxWidth, quality, format);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[JellyShim] Image processing failed for {Path}, serving original", path);
                context.Response.ContentLength = originalBytes.Length;
                await context.Response.Body.WriteAsync(originalBytes, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Cache the processed image
            if (config.EnableImageCache)
            {
                try
                {
                    _cache.Store(cacheKey, "img", processed);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[JellyShim] Failed to cache processed image for {Path}", path);
                }
            }

            // Serve the processed image
            context.Response.ContentType = ImageProcessor.GetContentType(format);
            context.Response.ContentLength = processed.Length;
            SetImageHeaders(context.Response, config);

            await context.Response.Body.WriteAsync(processed, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Response.Body = originalBody;
            if (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[JellyShim] Image optimization failed for {Path}, falling through", path);
            }

            throw;
        }
    }

    /// <summary>
    /// Serves a cached processed image file with ETag/304 support.
    /// </summary>
    private async Task ServeCachedImage(HttpContext context, string cachedPath, string format, Configuration.PluginConfiguration config)
    {
        var fileInfo = new FileInfo(cachedPath);
        if (!fileInfo.Exists)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var etag = _cache.ComputeETag(cachedPath);

        if (context.Request.Headers.IfNoneMatch == etag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ImageProcessor.GetContentType(format);
        context.Response.ContentLength = fileInfo.Length;
        context.Response.Headers.ETag = etag;
        SetImageHeaders(context.Response, config);

        await using var fs = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await fs.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets cache and CORP headers for image responses.
    /// </summary>
    private static void SetImageHeaders(HttpResponse response, Configuration.PluginConfiguration config)
    {
        if (config.EnableCacheHeaders)
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.ImageCacheMaxAge}, stale-while-revalidate={config.ImageStaleWhileRevalidate}";
        }

        if (config.EnableCrossOriginResourcePolicy)
        {
            response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
        }

        response.Headers.Vary = "Accept";
    }

    /// <summary>
    /// Retrieves per-type MaxWidth and Quality from configuration.
    /// Each Jellyfin image type has its own independent settings.
    /// </summary>
    private static (int MaxWidth, int Quality) GetImageSettings(string imageType, Configuration.PluginConfiguration config)
    {
        return imageType.ToUpperInvariant() switch
        {
            "PRIMARY" => (config.PrimaryMaxWidth, config.PrimaryQuality),
            "BACKDROP" => (config.BackdropMaxWidth, config.BackdropQuality),
            "ART" => (config.ArtMaxWidth, config.ArtQuality),
            "BANNER" => (config.BannerMaxWidth, config.BannerQuality),
            "LOGO" => (config.LogoMaxWidth, config.LogoQuality),
            "THUMB" => (config.ThumbMaxWidth, config.ThumbQuality),
            "SCREENSHOT" => (config.ScreenshotMaxWidth, config.ScreenshotQuality),
            "CHAPTER" => (config.ChapterMaxWidth, config.ChapterQuality),
            "PROFILE" => (config.ProfileMaxWidth, config.ProfileQuality),
            "DISC" => (config.DiscMaxWidth, config.DiscQuality),
            "BOX" => (config.BoxMaxWidth, config.BoxQuality),
            "BOXREAR" => (config.BoxRearMaxWidth, config.BoxRearQuality),
            _ => (config.DefaultImageMaxWidth, config.DefaultImageQuality)
        };
    }

    /// <summary>
    /// Selects output format based on Accept header and config preference.
    /// Supports AVIF, WebP, and JPEG output with automatic negotiation.
    /// AVIF requires ffmpeg with libaom-av1 encoder (bundled with Jellyfin).
    /// </summary>
    private string NegotiateFormat(HttpRequest request, Configuration.PluginConfiguration config)
    {
        var accept = request.Headers.Accept.ToString();
        var preferred = config.ImageOutputFormat.ToLowerInvariant();

        if (preferred == "avif" &&
            accept.Contains("image/avif", StringComparison.OrdinalIgnoreCase) &&
            _imageProcessor.IsAvifSupported)
        {
            return "avif";
        }

        if (preferred == "webp" && accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase))
        {
            return "webp";
        }

        // Auto: prefer AVIF > WebP > JPEG based on browser support
        if (preferred == "auto")
        {
            if (accept.Contains("image/avif", StringComparison.OrdinalIgnoreCase) &&
                _imageProcessor.IsAvifSupported)
            {
                return "avif";
            }

            if (accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase))
            {
                return "webp";
            }
        }

        return "jpeg";
    }

    /// <summary>
    /// Builds a deterministic cache key from request path and processing parameters.
    /// </summary>
    private static string BuildCacheKey(string path, string? query, int maxWidth, int quality, string format)
    {
        var raw = $"{path}?{query}|{maxWidth}x{quality}.{format}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(hash[..16]);
    }
}
