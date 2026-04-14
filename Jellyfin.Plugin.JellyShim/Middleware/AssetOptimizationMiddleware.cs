using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Configuration;
using Jellyfin.Plugin.JellyShim.Transformation;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using ZstdSharp;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// Core middleware that intercepts all GET requests to Jellyfin's web client (/web/*),
/// plugin static resource paths, and API endpoints.
///
/// <para><b>Pipeline position:</b> Registered BEFORE Jellyfin's own middleware via
/// <see cref="JellyShimApplicationBuilderFactory"/>, so every request passes through
/// here first. <see cref="ImageOptimizationMiddleware"/> runs even earlier (image paths).</para>
///
/// <para><b>Request classification flow:</b></para>
/// <list type="number">
///   <item>Non-GET or null path → pass through immediately</item>
///   <item>Image paths (/Items/*/Images/*) → skip (handled by ImageOptimizationMiddleware)</item>
///   <item>API paths → no-store + CORP + security headers, pass through</item>
///   <item>Font files → immutable cache + Link preload header, pass through</item>
///   <item>HTML files → never cached (File Transformation plugins need raw access)</item>
///   <item>Plugin assets → on-the-fly capture, minify, compress, cache</item>
///   <item>FT-bypassed web assets → capture after transformation, then optimize</item>
///   <item>Normal web assets → serve from pre-built disk cache</item>
/// </list>
///
/// <para><b>Compression strategy:</b> Brotli preferred, Gzip fallback, raw if client
/// doesn't accept either. Three cache variants stored per asset (br, gz, raw).</para>
///
/// <para><b>File Transformation compatibility:</b> Files matching
/// <see cref="Configuration.PluginConfiguration.FileTransformationBypassPatterns"/> are
/// NOT served from the pre-built cache. Instead, the transformed response is captured
/// from upstream, then minified/compressed/cached with a separate "ft/" cache prefix
/// and <c>no-cache</c> browser cache policy (forces ETag revalidation).</para>
/// </summary>
public class AssetOptimizationMiddleware
{
    /// <summary>
    /// File extensions eligible for minification and pre-compression.
    /// Fonts, images, and binary files are excluded — they have their own handling.
    /// </summary>
    private static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".css", ".html", ".htm", ".json", ".svg", ".xml", ".txt", ".map", ".mjs"
    };

    /// <summary>
    /// File extensions eligible for SVG-specific minification.
    /// </summary>
    private static readonly HashSet<string> SvgExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg"
    };

    /// <summary>
    /// Font file extensions — served with immutable cache and Link preload headers
    /// but NOT minified/compressed (already optimized binary formats).
    /// </summary>
    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".woff2", ".woff", ".ttf", ".otf", ".eot"
    };

    /// <summary>
    /// API-like path prefixes that should NEVER be cached.
    /// Jellyfin doesn't route everything under /api/ — most endpoints are at root level
    /// (e.g. /System/Info, /Users/{id}, /Items/{id}). This list covers all known prefixes.
    /// </summary>
    private static readonly string[] ApiPrefixes =
    [
        "/api/", "/emby/", "/mediabrowser/",
        "/System/", "/Sessions/", "/Library/", "/Dlna/",
        "/Playlists/", "/Notifications/", "/ScheduledTasks/",
        "/Startup/", "/Environment/", "/Devices/",
        "/DisplayPreferences/", "/Plugins/", "/Packages/",
        "/Localization/", "/QuickConnect/", "/Branding/",
        "/ClientLog/", "/Audio/", "/Videos/",
        "/LiveTv/", "/MusicGenres/", "/Genres/", "/Artists/",
        "/Studios/", "/Years/", "/Persons/", "/Channels/",
        "/Search/", "/Collections/", "/SyncPlay/",
        "/socket", "/health"
    ];

    // ── Cached config parsing ──────────────────────────────────────────
    // Config strings (newline-separated lists) are parsed once and re-parsed only
    // when the raw string changes. This avoids String.Split on every request.

    /// <summary>Parsed plugin asset path prefixes from <see cref="Configuration.PluginConfiguration.PluginAssetPaths"/>.</summary>
    private string[]? _cachedPluginPaths;
    /// <summary>Raw config string used to detect when re-parsing is needed.</summary>
    private string? _cachedPluginPathsRaw;

    /// <summary>Parsed File Transformation bypass patterns from config.</summary>
    private string[]? _cachedBypassPatterns;
    /// <summary>Raw config string used to detect when re-parsing is needed.</summary>
    private string? _cachedBypassPatternsRaw;

    // ── Dependencies ─────────────────────────────────────────────────

    private readonly RequestDelegate _next;
    private readonly DiskCacheManager _cache;
    private readonly JsTransformer _jsTransformer;
    private readonly CssTransformer _cssTransformer;
    private readonly SvgTransformer _svgTransformer;
    private readonly PerformanceStatsTracker _stats;
    private readonly ILogger<AssetOptimizationMiddleware> _logger;

    /// <summary>
    /// Jellyfin's web client root directory (e.g. /usr/share/jellyfin-web).
    /// Used to check source file timestamps for FT cache staleness detection.
    /// </summary>
    private readonly string? _webPath;

    /// <summary>Counts the first few requests for diagnostic logging at startup.</summary>
    private static int _requestCount;

    /// <summary>
    /// Per-key locks that prevent duplicate processing when multiple concurrent
    /// requests hit the same uncached asset simultaneously.
    /// Without this, N concurrent requests for the same plugin script would each
    /// capture → minify → compress → store independently, wasting CPU.
    /// The first request does the work; others wait and then serve from cache.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _inflightLocks = new();

    /// <summary>
    /// Maximum response body size to capture for on-the-fly optimization (2 MB).
    /// Plugin assets larger than this are forwarded unmodified to avoid
    /// excessive memory allocation and processing time.
    /// </summary>
    private const int MaxPluginCaptureBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetOptimizationMiddleware"/> class.
    /// Called once by the DI container when the HTTP pipeline is built.
    /// Clears the entire disk cache on startup because Jellyfin or its plugins may have
    /// been updated since the last run — stale cached assets could cause UI breakage.
    /// The <see cref="Tasks.OptimizeAssetsTask"/> startup trigger then repopulates
    /// the pre-built web asset cache; FT and plugin assets are lazily captured on first request.
    /// </summary>
    public AssetOptimizationMiddleware(
        RequestDelegate next,
        DiskCacheManager cache,
        JsTransformer jsTransformer,
        CssTransformer cssTransformer,
        SvgTransformer svgTransformer,
        PerformanceStatsTracker stats,
        IServerConfigurationManager configManager,
        ILogger<AssetOptimizationMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _jsTransformer = jsTransformer;
        _cssTransformer = cssTransformer;
        _svgTransformer = svgTransformer;
        _stats = stats;
        _logger = logger;
        _webPath = configManager.ApplicationPaths.WebPath;
        _logger.LogInformation("[JellyShim] AssetOptimizationMiddleware instantiated");

        // Clear entire cache on startup — Jellyfin or plugins may have been updated.
        // Risk of serving stale minified code outweighs the cost of a cold start.
        // The OptimizeAssetsTask startup trigger will repopulate pre-built assets;
        // FT and plugin assets are re-captured lazily on first request.
        _cache.InvalidateAll();
    }

    /// <summary>
    /// Processes an incoming HTTP request. This is the main entry point called by
    /// the ASP.NET Core pipeline for every request.
    /// See class-level documentation for the full request classification flow.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (Interlocked.Increment(ref _requestCount) <= 3)
        {
            _logger.LogInformation("[JellyShim] AssetOptimizationMiddleware handling request #{Count}: {Method} {Path}",
                _requestCount, context.Request.Method, context.Request.Path);
        }
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
        var ext = Path.GetExtension(path);

        // Classify the request
        var isWebAsset = path.StartsWith("/web/", StringComparison.OrdinalIgnoreCase);
        var isPluginAsset = !isWebAsset && IsPluginAssetPath(path, config);

        // Image paths are handled by ImageOptimizationMiddleware — skip here
        var isImagePath = path.Contains("/Images/", StringComparison.OrdinalIgnoreCase) &&
                          (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase));

        // API/dynamic paths: NEVER cache. Jellyfin uses many root-level API prefixes.
        var isApiPath = !isWebAsset && !isPluginAsset && !isImagePath && IsApiPath(path);

        // API paths: no-store, CORP, then pass through
        if (isApiPath)
        {
            SetResponseHeaders(context, config, () =>
            {
                if (config.EnableCacheHeaders)
                {
                    context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                }
            });
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Not our concern
        if (!isWebAsset && !isPluginAsset)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Font files: immutable cache + preload Link header, then pass through
        if (FontExtensions.Contains(ext))
        {
            SetResponseHeaders(context, config, () =>
            {
                if (config.EnableCacheHeaders)
                {
                    context.Response.Headers[HeaderNames.CacheControl] =
                        $"public, max-age={config.HashedAssetMaxAge}, immutable";
                }

                if (config.EnableFontPreloadHeaders)
                {
                    var fontType = ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase) ? "font/woff2" : "font/" + ext.TrimStart('.');
                    context.Response.Headers.Append("Link",
                        $"<{path}>; rel=preload; as=font; type={fontType}; crossorigin");
                }
            });
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Non-compressible, non-font pass-through with cache + CORP
        if (!CompressibleExtensions.Contains(ext))
        {
            SetResponseHeaders(context, config, () =>
            {
                if (config.EnableCacheHeaders)
                {
                    SetPassthroughCacheHeaders(context.Response, path, isPluginAsset, config);
                }
            });
            await _next(context).ConfigureAwait(false);
            return;
        }

        // ── Serve compressible assets from cache ──────────────────────

        // NEVER serve HTML from cache — File Transformation plugins (Custom Tabs,
        // Jellyfin Enhanced, JellyTweaks, etc.) need to process index.html at the
        // static-file layer.  Short-circuiting would bypass their injections.
        if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            SetResponseHeaders(context, config, () =>
            {
                if (config.EnableCacheHeaders)
                {
                    // Short max-age for HTML — it's the SPA entry point
                    context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
                }
            });
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? relativePath = null;
        if (isWebAsset)
        {
            relativePath = path[5..]; // strip "/web/"
        }

        // Plugin assets: on-the-fly minification + compression + caching.
        // Captures the response body from the upstream middleware, minifies JS/CSS,
        // compresses with Brotli/Gzip, caches to disk, and serves the optimized version.
        if (relativePath is null && isPluginAsset)
        {
            var isCompressible = ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                                 ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
                                 ext.Equals(".css", StringComparison.OrdinalIgnoreCase);

            // Extensionless URLs (e.g. /JellyfinEnhanced/script) may still serve JS/CSS.
            // Allow them through to capture+detect Content-Type from the upstream response.
            var isExtensionless = string.IsNullOrEmpty(ext);

            if (!isCompressible && !isExtensionless)
            {
                SetResponseHeaders(context, config, () =>
                {
                    if (config.EnableCacheHeaders)
                    {
                        SetPassthroughCacheHeaders(context.Response, path, true, config);
                    }
                });
                await _next(context).ConfigureAwait(false);
                return;
            }

            // Build a cache key from the full path + query string (plugins use ?v= for versioning)
            var pluginCacheKey = "plugin" + path + context.Request.QueryString.Value;

            // Determine best encoding
            var pluginAcceptEncoding = request.Headers.AcceptEncoding.ToString();
            var (pluginEncoding, pluginContentEncoding) = NegotiateEncoding(pluginAcceptEncoding, config);

            // Try to serve from cache (compressed version first, then raw)
            string? pluginCachedPath = null;
            if (pluginEncoding is not null &&
                _cache.TryGetCachedFile(pluginCacheKey, pluginEncoding, out var pluginCompressedPath))
            {
                pluginCachedPath = pluginCompressedPath;
            }

            if (pluginCachedPath is null && _cache.TryGetCachedFile(pluginCacheKey, "raw", out var pluginRawPath))
            {
                pluginCachedPath = pluginRawPath;
                pluginContentEncoding = null;
            }

            if (pluginCachedPath is not null)
            {
                // For extensionless URLs, retrieve the detected content type from cache metadata
                var serveExt = ext;
                if (string.IsNullOrEmpty(serveExt) && _cache.TryGetCachedFile(pluginCacheKey, "meta", out var metaPath))
                {
                    serveExt = File.ReadAllText(metaPath).Trim();
                }

                await ServePluginCachedFile(context, config, pluginCachedPath, serveExt, pluginContentEncoding, path).ConfigureAwait(false);
                return;
            }

            // Cache miss — use per-key lock to prevent duplicate processing of concurrent requests
            var keyLock = _inflightLocks.GetOrAdd(pluginCacheKey, _ => new SemaphoreSlim(1, 1));
            await keyLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                // Re-check cache after acquiring lock (another request may have populated it)
                pluginCachedPath = null;
                if (pluginEncoding is not null &&
                    _cache.TryGetCachedFile(pluginCacheKey, pluginEncoding, out var compPath2))
                {
                    pluginCachedPath = compPath2;
                }

                if (pluginCachedPath is null && _cache.TryGetCachedFile(pluginCacheKey, "raw", out var rawPath2))
                {
                    pluginCachedPath = rawPath2;
                    pluginContentEncoding = null;
                }

                if (pluginCachedPath is not null)
                {
                    var serveExt2 = ext;
                    if (string.IsNullOrEmpty(serveExt2) && _cache.TryGetCachedFile(pluginCacheKey, "meta", out var metaPath2))
                    {
                        serveExt2 = File.ReadAllText(metaPath2).Trim();
                    }

                    await ServePluginCachedFile(context, config, pluginCachedPath, serveExt2, pluginContentEncoding, path).ConfigureAwait(false);
                    return;
                }

                // Still a cache miss — we're the first. Capture, optimize, cache, serve.
                await CaptureAndOptimizePluginAsset(context, config, ext, path, pluginCacheKey, pluginEncoding, pluginContentEncoding).ConfigureAwait(false);
            }
            finally
            {
                keyLock.Release();
            }

            return;
        }

        // Non-plugin, non-web pass-through
        if (relativePath is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Determine best encoding from Accept-Encoding
        var acceptEncoding = request.Headers.AcceptEncoding.ToString();
        var (encoding, contentEncoding) = NegotiateEncoding(acceptEncoding, config);

        // File Transformation compatibility: certain JS files are patched at runtime
        // by downstream plugins (HSS, Custom Tabs, JellyfinEnhanced, etc.) via
        // File Transformation's PhysicalTransformedFileProvider.
        // Instead of bypassing, we let upstream apply the patches, then capture the
        // transformed response, minify, compress, cache, and serve it optimized.
        if (IsFileTransformationBypassed(relativePath, config))
        {
            var ftCacheKey = "ft/" + relativePath;

            // Determine best encoding
            var (ftEncoding, ftContentEncoding) = NegotiateEncoding(acceptEncoding, config);

            // Try to serve from FT cache (compressed first, then raw),
            // but only if the source file hasn't changed since we cached
            string? ftCachedPath = null;
            bool ftCacheStale = false;

            if (ftEncoding is not null &&
                _cache.TryGetCachedFile(ftCacheKey, ftEncoding, out var ftCompressedPath))
            {
                ftCachedPath = ftCompressedPath;
            }

            if (ftCachedPath is null && _cache.TryGetCachedFile(ftCacheKey, "raw", out var ftRawPath))
            {
                ftCachedPath = ftRawPath;
                ftContentEncoding = null;
            }

            // Verify source file hasn't been modified since cache was written
            if (ftCachedPath is not null && _webPath is not null)
            {
                var sourceFile = Path.Combine(_webPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(sourceFile))
                {
                    var sourceModified = File.GetLastWriteTimeUtc(sourceFile);
                    var cacheModified = File.GetLastWriteTimeUtc(ftCachedPath);
                    if (sourceModified > cacheModified)
                    {
                        _logger.LogInformation("[JellyShim] FT cache stale for {Path} (source modified), re-capturing", path);
                        ftCachedPath = null;
                        ftCacheStale = true;
                    }
                }
            }

            if (ftCachedPath is not null)
            {
                await ServeCachedWebAsset(context, config, ftCachedPath, ext, ftContentEncoding, path, isFileTransformation: true).ConfigureAwait(false);
                return;
            }

            // Cache miss — use per-key lock to prevent duplicate capture
            var ftKeyLock = _inflightLocks.GetOrAdd(ftCacheKey, _ => new SemaphoreSlim(1, 1));
            await ftKeyLock.WaitAsync(context.RequestAborted).ConfigureAwait(false);
            try
            {
                // Re-check cache after acquiring lock (skip if we already know it's stale)
                if (!ftCacheStale)
                {
                    ftCachedPath = null;
                    if (ftEncoding is not null &&
                        _cache.TryGetCachedFile(ftCacheKey, ftEncoding, out var ftCompPath2))
                    {
                        ftCachedPath = ftCompPath2;
                    }

                    if (ftCachedPath is null && _cache.TryGetCachedFile(ftCacheKey, "raw", out var ftRawPath2))
                    {
                        ftCachedPath = ftRawPath2;
                        ftContentEncoding = null;
                    }

                    if (ftCachedPath is not null)
                    {
                        await ServeCachedWebAsset(context, config, ftCachedPath, ext, ftContentEncoding, path, isFileTransformation: true).ConfigureAwait(false);
                        return;
                    }
                }

                // Cache miss or stale — capture the transformed response from upstream
                await CaptureAndOptimizeTransformedAsset(context, config, ext, path, ftCacheKey, ftEncoding, ftContentEncoding).ConfigureAwait(false);
            }
            finally
            {
                ftKeyLock.Release();
            }

            return;
        }

        // Try to serve from cache
        string? cachedPath = null;
        if (encoding is not null &&
            _cache.TryGetCachedFile(relativePath, encoding, out var compressedPath))
        {
            cachedPath = compressedPath;
        }

        // Fall back to raw optimized version
        if (cachedPath is null)
        {
            if (_cache.TryGetCachedFile(relativePath, "raw", out var rawPath))
            {
                cachedPath = rawPath;
            }

            contentEncoding = null;
        }

        if (cachedPath is null)
        {
            // No cached version — let Jellyfin serve the original with headers
            SetResponseHeaders(context, config, () =>
            {
                if (config.EnableCacheHeaders)
                {
                    SetPassthroughCacheHeaders(context.Response, path, false, config);
                }
            });
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Serve the cached version
        var response = context.Response;
        var etag = _cache.ComputeETag(cachedPath);

        // ETag-based conditional request
        if (request.Headers.IfNoneMatch == etag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            _stats.RecordNotModified();
            return;
        }

        var fileInfo = new FileInfo(cachedPath);
        _stats.RecordCacheHit(fileInfo.Length);
        _stats.RecordWebAssetRequest();

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = GetContentType(ext);
        response.ContentLength = fileInfo.Length;

        if (contentEncoding is not null)
        {
            response.Headers.ContentEncoding = contentEncoding;
        }

        response.Headers.Vary = "Accept-Encoding";
        response.Headers.ETag = etag;

        if (config.EnableCacheHeaders)
        {
            SetCacheHeaders(response, path, config);
        }

        // Link headers for JS modulepreload
        if (config.EnableJsModulepreloadHeaders &&
            (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
             ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.Append("Link", $"<{path}>; rel=modulepreload");
        }

        // CORP
        if (config.EnableCrossOriginResourcePolicy)
        {
            response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
        }

        // Security headers
        AddSecurityHeaders(response, config);
        AddPreconnectHeaders(response, config);

        // Stream file directly to response — avoid loading entire file into memory
        await using var fs = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await fs.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures a plugin asset response from upstream, minifies JS/CSS,
    /// compresses to Brotli/Gzip, caches all variants, and serves the best one.
    /// </summary>
    private async Task CaptureAndOptimizePluginAsset(
        HttpContext context,
        PluginConfiguration config,
        string ext,
        string path,
        string cacheKey,
        string? encoding,
        string? contentEncoding)
    {
        var originalBody = context.Response.Body;
        using var captureStream = new MemoryStream();
        context.Response.Body = captureStream;

        // Strip Accept-Encoding so upstream middleware doesn't compress the response.
        // We need raw bytes for minification; we'll compress ourselves afterwards.
        var savedAcceptEncoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.AcceptEncoding = Microsoft.Extensions.Primitives.StringValues.Empty;

        try
        {
            await _next(context).ConfigureAwait(false);

            // Restore Accept-Encoding (needed for our own encoding negotiation above)
            context.Request.Headers.AcceptEncoding = savedAcceptEncoding;

            if (context.Response.StatusCode != StatusCodes.Status200OK ||
                captureStream.Length == 0 ||
                captureStream.Length > MaxPluginCaptureBytes)
            {
                // Too big or not 200 — just forward the original response
                captureStream.Position = 0;
                context.Response.Body = originalBody;
                await captureStream.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var rawBytes = captureStream.ToArray();

            // Safety net: if upstream still compressed despite removing Accept-Encoding,
            // decompress before minification to avoid corrupting the content.
            var upstreamContentEncoding = context.Response.Headers.ContentEncoding.ToString();
            if (!string.IsNullOrEmpty(upstreamContentEncoding))
            {
                rawBytes = DecompressUpstreamResponse(rawBytes, upstreamContentEncoding);
                context.Response.Headers.ContentEncoding = Microsoft.Extensions.Primitives.StringValues.Empty;
            }

            // For extensionless URLs, detect the actual content type from the response
            // to decide whether minification applies (e.g. /JellyfinEnhanced/script serves JS).
            var effectiveExt = ext;
            if (string.IsNullOrEmpty(effectiveExt))
            {
                var responseContentType = context.Response.ContentType ?? string.Empty;
                if (responseContentType.Contains("javascript", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveExt = ".js";
                }
                else if (responseContentType.Contains("text/css", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveExt = ".css";
                }
                else if (responseContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    effectiveExt = ".json";
                }
                else
                {
                    // Not JS/CSS/JSON — forward the response unmodified
                    captureStream.Position = 0;
                    context.Response.Body = originalBody;
                    await captureStream.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                _logger.LogInformation("[JellyShim] Extensionless plugin asset detected as {Ext}: {Path}", effectiveExt, path);
            }

            // Minify JS or CSS (JSON is compressed but not minified — already compact from serializers)
            byte[] optimized = rawBytes;
            if (config.EnableMinification)
            {
                if (effectiveExt.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                    effectiveExt.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    optimized = _jsTransformer.MinifyBytes(rawBytes);
                }
                else if (effectiveExt.Equals(".css", StringComparison.OrdinalIgnoreCase))
                {
                    optimized = _cssTransformer.MinifyBytes(rawBytes);
                }
            }

            // SVG minification
            if (config.EnableSvgMinification && effectiveExt.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                optimized = _svgTransformer.MinifyBytes(optimized);
            }

            // Cache the raw minified version
            _cache.Store(cacheKey, "raw", optimized);

            // For extensionless URLs, store the detected content type so cache hits serve correctly
            if (string.IsNullOrEmpty(ext))
            {
                _cache.Store(cacheKey, "meta", Encoding.UTF8.GetBytes(effectiveExt));
            }

            // Compress and cache all variants
            if (config.EnableCompression)
            {
                var brotli = CompressBrotli(optimized, config.BrotliCompressionLevel);
                var gzip = CompressGzip(optimized);
                _cache.Store(cacheKey, "br", brotli);
                _cache.Store(cacheKey, "gz", gzip);

                if (config.EnableZstdCompression)
                {
                    var zstd = CompressZstd(optimized);
                    _cache.Store(cacheKey, "zstd", zstd);
                }

                _logger.LogInformation(
                    "[JellyShim] Plugin asset optimized: {Path} — {Original}B → minified {Minified}B → br {Br}B / gz {Gz}B",
                    path, rawBytes.Length, optimized.Length, brotli.Length, gzip.Length);
            }

            // Serve the best version
            byte[] toServe;
            if (encoding is not null)
            {
                _cache.TryGetCachedFile(cacheKey, encoding, out var compressedCachePath);
                toServe = compressedCachePath is not null ? await File.ReadAllBytesAsync(compressedCachePath, context.RequestAborted).ConfigureAwait(false) : optimized;
                if (compressedCachePath is null)
                {
                    contentEncoding = null;
                }
            }
            else
            {
                toServe = optimized;
                contentEncoding = null;
            }

            context.Response.Body = originalBody;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = GetContentType(effectiveExt);
            context.Response.ContentLength = toServe.Length;

            if (contentEncoding is not null)
            {
                context.Response.Headers.ContentEncoding = contentEncoding;
            }

            context.Response.Headers.Vary = "Accept-Encoding";

            var etag = _cache.ComputeETag(_cache.GetCachedFilePath(cacheKey, "raw"));
            context.Response.Headers.ETag = etag;

            if (config.EnableCacheHeaders)
            {
                context.Response.Headers[HeaderNames.CacheControl] =
                    $"public, max-age={config.PluginAssetMaxAge}, stale-while-revalidate=3600";
            }

            if (config.EnableCrossOriginResourcePolicy)
            {
                context.Response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
            }

            AddSecurityHeaders(context.Response, config);
            AddPreconnectHeaders(context.Response, config);

            _stats.RecordCacheMiss();
            _stats.RecordPluginAssetRequest();

            await context.Response.Body.WriteAsync(toServe, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[JellyShim] Failed to optimize plugin asset: {Path}", path);

            // Fall back to forwarding the original captured response
            captureStream.Position = 0;
            context.Response.Body = originalBody;
            await captureStream.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Captures a web asset response that has been transformed by File Transformation
    /// (PhysicalTransformedFileProvider), minifies JS/CSS, compresses, caches, and serves.
    /// </summary>
    private async Task CaptureAndOptimizeTransformedAsset(
        HttpContext context,
        PluginConfiguration config,
        string ext,
        string path,
        string cacheKey,
        string? encoding,
        string? contentEncoding)
    {
        var originalBody = context.Response.Body;
        using var captureStream = new MemoryStream();
        context.Response.Body = captureStream;

        // Strip Accept-Encoding so upstream doesn't compress — we need raw bytes for minification
        var savedAcceptEncoding = context.Request.Headers.AcceptEncoding;
        context.Request.Headers.AcceptEncoding = Microsoft.Extensions.Primitives.StringValues.Empty;

        try
        {
            await _next(context).ConfigureAwait(false);

            context.Request.Headers.AcceptEncoding = savedAcceptEncoding;

            if (context.Response.StatusCode != StatusCodes.Status200OK ||
                captureStream.Length == 0 ||
                captureStream.Length > MaxPluginCaptureBytes)
            {
                captureStream.Position = 0;
                context.Response.Body = originalBody;
                await captureStream.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var rawBytes = captureStream.ToArray();

            // Decompress if upstream still compressed despite removing Accept-Encoding
            var upstreamContentEncoding = context.Response.Headers.ContentEncoding.ToString();
            if (!string.IsNullOrEmpty(upstreamContentEncoding))
            {
                rawBytes = DecompressUpstreamResponse(rawBytes, upstreamContentEncoding);
                context.Response.Headers.ContentEncoding = Microsoft.Extensions.Primitives.StringValues.Empty;
            }

            // Skip minification for FT-bypassed files — they are already webpack-minified
            // production bundles, and NUglify can break runtime-patched code injected by
            // File Transformation plugins (HSS loadSections generator, Custom Tabs, etc.).
            // Compression alone provides the majority of transfer size savings.
            byte[] optimized = rawBytes;

            // Cache raw version (no minification for FT files)
            _cache.Store(cacheKey, "raw", optimized);

            // Compress and cache all variants
            if (config.EnableCompression)
            {
                var brotli = CompressBrotli(optimized, config.BrotliCompressionLevel);
                var gzip = CompressGzip(optimized);
                _cache.Store(cacheKey, "br", brotli);
                _cache.Store(cacheKey, "gz", gzip);

                if (config.EnableZstdCompression)
                {
                    var zstd = CompressZstd(optimized);
                    _cache.Store(cacheKey, "zstd", zstd);
                }

                _logger.LogInformation(
                    "[JellyShim] File Transformation asset optimized: {Path} — {Original}B → minified {Minified}B → br {Br}B / gz {Gz}B",
                    path, rawBytes.Length, optimized.Length, brotli.Length, gzip.Length);
            }

            // Serve the best version
            byte[] toServe;
            if (encoding is not null)
            {
                _cache.TryGetCachedFile(cacheKey, encoding, out var compressedCachePath);
                toServe = compressedCachePath is not null ? await File.ReadAllBytesAsync(compressedCachePath, context.RequestAborted).ConfigureAwait(false) : optimized;
                if (compressedCachePath is null)
                {
                    contentEncoding = null;
                }
            }
            else
            {
                toServe = optimized;
                contentEncoding = null;
            }

            context.Response.Body = originalBody;
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = GetContentType(ext);
            context.Response.ContentLength = toServe.Length;

            if (contentEncoding is not null)
            {
                context.Response.Headers.ContentEncoding = contentEncoding;
            }

            context.Response.Headers.Vary = "Accept-Encoding";

            var etag = _cache.ComputeETag(_cache.GetCachedFilePath(cacheKey, "raw"));
            context.Response.Headers.ETag = etag;

            if (config.EnableCacheHeaders)
            {
                SetCacheHeaders(context.Response, path, config, isFileTransformation: true);
            }

            if (config.EnableJsModulepreloadHeaders &&
                (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.Headers.Append("Link", $"<{path}>; rel=modulepreload");
            }

            if (config.EnableCrossOriginResourcePolicy)
            {
                context.Response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
            }

            AddSecurityHeaders(context.Response, config);
            AddPreconnectHeaders(context.Response, config);

            _stats.RecordCacheMiss();
            _stats.RecordFtAssetRequest();

            await context.Response.Body.WriteAsync(toServe, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[JellyShim] Failed to optimize File Transformation asset: {Path}", path);

            captureStream.Position = 0;
            context.Response.Body = originalBody;
            await captureStream.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves a previously cached web asset (including File Transformation captures)
    /// with ETag/304 support, cache headers, and security headers.
    /// </summary>
    private async Task ServeCachedWebAsset(
        HttpContext context,
        PluginConfiguration config,
        string cachedPath,
        string ext,
        string? contentEncoding,
        string path,
        bool isFileTransformation = false)
    {
        var response = context.Response;
        var etag = _cache.ComputeETag(cachedPath);

        if (context.Request.Headers.IfNoneMatch == etag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            _stats.RecordNotModified();
            return;
        }

        var fileInfo = new FileInfo(cachedPath);
        _stats.RecordCacheHit(fileInfo.Length);
        if (isFileTransformation) _stats.RecordFtAssetRequest(); else _stats.RecordWebAssetRequest();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = GetContentType(ext);
        response.ContentLength = fileInfo.Length;

        if (contentEncoding is not null)
        {
            response.Headers.ContentEncoding = contentEncoding;
        }

        response.Headers.Vary = "Accept-Encoding";
        response.Headers.ETag = etag;

        if (config.EnableCacheHeaders)
        {
            SetCacheHeaders(response, path, config, isFileTransformation);
        }

        if (config.EnableJsModulepreloadHeaders &&
            (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
             ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.Append("Link", $"<{path}>; rel=modulepreload");
        }

        if (config.EnableCrossOriginResourcePolicy)
        {
            response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
        }

        AddSecurityHeaders(response, config);
        AddPreconnectHeaders(response, config);

        await using var fs = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await fs.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves a previously cached plugin asset with ETag/304 support.
    /// </summary>
    private async Task ServePluginCachedFile(
        HttpContext context,
        PluginConfiguration config,
        string cachedPath,
        string ext,
        string? contentEncoding,
        string path)
    {
        var response = context.Response;
        var etag = _cache.ComputeETag(cachedPath);

        if (context.Request.Headers.IfNoneMatch == etag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            _stats.RecordNotModified();
            return;
        }

        var fileInfo = new FileInfo(cachedPath);
        _stats.RecordCacheHit(fileInfo.Length);
        _stats.RecordPluginAssetRequest();
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = GetContentType(ext);
        response.ContentLength = fileInfo.Length;

        if (contentEncoding is not null)
        {
            response.Headers.ContentEncoding = contentEncoding;
        }

        response.Headers.Vary = "Accept-Encoding";
        response.Headers.ETag = etag;

        if (config.EnableCacheHeaders)
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.PluginAssetMaxAge}, stale-while-revalidate=3600";
        }

        if (config.EnableJsModulepreloadHeaders &&
            (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
             ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)))
        {
            response.Headers.Append("Link", $"<{path}>; rel=modulepreload");
        }

        if (config.EnableCrossOriginResourcePolicy)
        {
            response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
        }

        AddSecurityHeaders(response, config);
        AddPreconnectHeaders(response, config);

        await using var fs = new FileStream(cachedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        await fs.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Decompresses a response body that was compressed by upstream middleware.
    /// Returns the original bytes if the encoding is unknown or decompression fails.
    /// </summary>
    private byte[] DecompressUpstreamResponse(byte[] data, string contentEncoding)
    {
        try
        {
            if (contentEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            {
                using var input = new MemoryStream(data);
                using var brotli = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                brotli.CopyTo(output);
                _logger.LogDebug("[JellyShim] Decompressed upstream Brotli response: {Compressed}B → {Raw}B", data.Length, output.Length);
                return output.ToArray();
            }

            if (contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                using var input = new MemoryStream(data);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                _logger.LogDebug("[JellyShim] Decompressed upstream Gzip response: {Compressed}B → {Raw}B", data.Length, output.Length);
                return output.ToArray();
            }

            _logger.LogWarning("[JellyShim] Unknown upstream Content-Encoding: {Encoding}", contentEncoding);
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyShim] Failed to decompress upstream response (Content-Encoding: {Encoding})", contentEncoding);
            return data;
        }
    }

    /// <summary>
    /// Negotiates the best compression encoding based on Accept-Encoding and config.
    /// Priority: zstd > br > gzip.
    /// Returns (cacheVariant, contentEncoding) or (null, null) if no compression.
    /// </summary>
    private static (string? CacheVariant, string? ContentEncoding) NegotiateEncoding(string acceptEncoding, PluginConfiguration config)
    {
        if (!config.EnableCompression)
        {
            return (null, null);
        }

        if (config.EnableZstdCompression && acceptEncoding.Contains("zstd", StringComparison.OrdinalIgnoreCase))
        {
            return ("zstd", "zstd");
        }

        if (acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            return ("br", "br");
        }

        if (acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return ("gz", "gzip");
        }

        return (null, null);
    }

    /// <summary>
    /// Compresses data with Brotli.
    /// </summary>
    private static byte[] CompressBrotli(byte[] input, int quality)
    {
        using var output = new MemoryStream();
        var level = quality >= 10 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        using (var brotli = new BrotliStream(output, level, leaveOpen: true))
        {
            brotli.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compresses data with Gzip.
    /// </summary>
    private static byte[] CompressGzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compresses data with Zstandard. Uses level 19 (high compression).
    /// </summary>
    private static byte[] CompressZstd(byte[] input)
    {
        using var compressor = new Compressor(19);
        return compressor.Wrap(input).ToArray();
    }

    /// <summary>
    /// Checks if a path matches one of the configured plugin static asset prefixes.
    /// Parsed paths are cached to avoid re-splitting on every request.
    /// </summary>
    private bool IsPluginAssetPath(string path, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.PluginAssetPaths))
        {
            return false;
        }

        // Re-parse only when config string changes
        if (_cachedPluginPathsRaw != config.PluginAssetPaths)
        {
            _cachedPluginPaths = config.PluginAssetPaths.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _cachedPluginPathsRaw = config.PluginAssetPaths;
        }

        foreach (var prefix in _cachedPluginPaths!)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a web asset filename matches any File Transformation bypass pattern.
    /// These files must pass through to downstream middleware so that plugins like Custom Tabs
    /// can apply their runtime patches before the response reaches the client.
    /// Patterns support * as a wildcard.
    /// </summary>
    private bool IsFileTransformationBypassed(string relativePath, PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.FileTransformationBypassPatterns))
        {
            return false;
        }

        if (_cachedBypassPatternsRaw != config.FileTransformationBypassPatterns)
        {
            _cachedBypassPatterns = config.FileTransformationBypassPatterns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _cachedBypassPatternsRaw = config.FileTransformationBypassPatterns;
        }

        var fileName = Path.GetFileName(relativePath);

        // When FT plugins are in use (any patterns configured), ALL webpack chunk/bundle
        // files are potentially patched at runtime. Pre-caching these from disk would
        // serve the original unpatched content, bypassing FT patches entirely.
        // This catches HSS, Custom Tabs, JellyfinEnhanced, etc. regardless of the
        // specific chunk filename (which changes between Jellyfin versions).
        if (fileName.EndsWith(".chunk.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var pattern in _cachedBypassPatterns!)
        {
            if (WildcardMatch(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Simple wildcard matching: * matches any sequence of characters.
    /// </summary>
    private static bool WildcardMatch(string input, string pattern)
    {
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Checks if a path is an API/dynamic endpoint that should NEVER be cached.
    /// </summary>
    private static bool IsApiPath(string path)
    {
        foreach (var prefix in ApiPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Catch /Items/{id}/* paths that aren't images (API data endpoints)
        if (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sets CORP, security headers, preconnect headers, and a custom action via OnStarting callback
    /// for responses that pass through to the next middleware.
    /// </summary>
    private void SetResponseHeaders(HttpContext context, PluginConfiguration config, Action headerAction)
    {
        context.Response.OnStarting(() =>
        {
            headerAction();

            if (config.EnableCrossOriginResourcePolicy)
            {
                context.Response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
            }

            AddSecurityHeaders(context.Response, config);
            AddPreconnectHeaders(context.Response, config);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Determines appropriate cache policy based on the file path.
    /// </summary>
    /// <param name="response">The HTTP response.</param>
    /// <param name="path">The request path.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="isFileTransformation">True if this is a File Transformation-captured asset
    /// that may change when FT plugins are updated. Uses no-cache to force ETag revalidation.</param>
    private static void SetCacheHeaders(HttpResponse response, string path, PluginConfiguration config, bool isFileTransformation = false)
    {
        if (isFileTransformation)
        {
            // FT files can change when FT plugins are updated. Force browser to revalidate
            // every request via ETag; the 304 path keeps it fast when content hasn't changed.
            response.Headers[HeaderNames.CacheControl] = "public, no-cache";
            return;
        }

        if (IsHashedAsset(path))
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.HashedAssetMaxAge}, immutable";
        }
        else if (path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers[HeaderNames.CacheControl] = "public, no-cache, must-revalidate";
        }
        else
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.StaticAssetMaxAge}, stale-while-revalidate={config.StaleWhileRevalidate}";
        }
    }

    /// <summary>
    /// Sets cache headers for pass-through (non-cached) responses.
    /// </summary>
    private static void SetPassthroughCacheHeaders(HttpResponse response, string path, bool isPluginAsset, PluginConfiguration config)
    {
        if (IsHashedAsset(path))
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.HashedAssetMaxAge}, immutable";
        }
        else if (isPluginAsset)
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.PluginAssetMaxAge}, stale-while-revalidate=3600";
        }
        else
        {
            response.Headers[HeaderNames.CacheControl] =
                $"public, max-age={config.StaticAssetMaxAge}, stale-while-revalidate={config.StaleWhileRevalidate}";
        }
    }

    /// <summary>
    /// Adds security headers if enabled in config.
    /// </summary>
    private static void AddSecurityHeaders(HttpResponse response, PluginConfiguration config)
    {
        if (!config.EnableSecurityHeaders)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(config.XContentTypeOptions))
        {
            response.Headers["X-Content-Type-Options"] = config.XContentTypeOptions;
        }

        if (!string.IsNullOrWhiteSpace(config.ReferrerPolicy))
        {
            response.Headers["Referrer-Policy"] = config.ReferrerPolicy;
        }

        if (!string.IsNullOrWhiteSpace(config.PermissionsPolicy))
        {
            response.Headers["Permissions-Policy"] = config.PermissionsPolicy;
        }

        if (config.EnableHsts)
        {
            var hstsValue = $"max-age={config.HstsMaxAge}";
            if (config.HstsIncludeSubDomains)
            {
                hstsValue += "; includeSubDomains";
            }

            response.Headers["Strict-Transport-Security"] = hstsValue;
        }

        if (config.EnableContentSecurityPolicy && !string.IsNullOrWhiteSpace(config.ContentSecurityPolicy))
        {
            response.Headers["Content-Security-Policy"] = config.ContentSecurityPolicy;
        }

        if (config.EnableXFrameOptions && !string.IsNullOrWhiteSpace(config.XFrameOptionsValue))
        {
            response.Headers["X-Frame-Options"] = config.XFrameOptionsValue;
        }
    }

    /// <summary>Parsed preconnect origins from config.</summary>
    private string[]? _cachedPreconnectOrigins;
    /// <summary>Raw config string used to detect when re-parsing is needed.</summary>
    private string? _cachedPreconnectOriginsRaw;

    /// <summary>
    /// Adds Link: rel=preconnect headers for configured origins.
    /// </summary>
    private void AddPreconnectHeaders(HttpResponse response, PluginConfiguration config)
    {
        if (!config.EnablePreconnectHeaders || string.IsNullOrWhiteSpace(config.PreconnectOrigins))
        {
            return;
        }

        if (_cachedPreconnectOriginsRaw != config.PreconnectOrigins)
        {
            _cachedPreconnectOrigins = config.PreconnectOrigins.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _cachedPreconnectOriginsRaw = config.PreconnectOrigins;
        }

        foreach (var origin in _cachedPreconnectOrigins!)
        {
            response.Headers.Append("Link", $"<{origin}>; rel=preconnect");
        }
    }

    /// <summary>
    /// Heuristic: files with a content hash segment (8+ hex chars) in their name.
    /// </summary>
    private static bool IsHashedAsset(string path)
    {
        var fileName = Path.GetFileName(path.AsSpan());
        var dotIndex = fileName.IndexOf('.');
        while (dotIndex >= 0 && dotIndex < fileName.Length - 1)
        {
            var rest = fileName[(dotIndex + 1)..];
            var nextDot = rest.IndexOf('.');
            if (nextDot < 0)
            {
                break;
            }

            var segment = rest[..nextDot];
            if (segment.Length >= 8 && IsHexString(segment))
            {
                return true;
            }

            dotIndex = dotIndex + 1 + nextDot;
        }

        return false;
    }

    private static bool IsHexString(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml; charset=utf-8",
        ".xml" => "application/xml; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".map" => "application/json; charset=utf-8",
        _ => "application/octet-stream"
    };
}
