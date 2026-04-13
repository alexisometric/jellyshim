using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Configuration;
using Jellyfin.Plugin.JellyShim.Transformation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyShim.Middleware;

/// <summary>
/// ASP.NET Core middleware that intercepts requests to /web/* and plugin static paths,
/// serving pre-compressed cached assets with Cache-Control, ETag, Vary, CORP,
/// Link preload, and security headers.
/// </summary>
public class AssetOptimizationMiddleware
{
    private static readonly HashSet<string> CompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".css", ".html", ".htm", ".json", ".svg", ".xml", ".txt", ".map", ".mjs"
    };

    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".woff2", ".woff", ".ttf", ".otf", ".eot"
    };

    /// <summary>
    /// API-like path prefixes that should NEVER be cached.
    /// Jellyfin doesn't route everything under /api/ — most endpoints are at root level.
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

    // Parsed plugin paths (cached to avoid re-splitting on every request)
    private string[]? _cachedPluginPaths;
    private string? _cachedPluginPathsRaw;

    private readonly RequestDelegate _next;
    private readonly DiskCacheManager _cache;
    private readonly JsTransformer _jsTransformer;
    private readonly CssTransformer _cssTransformer;
    private readonly ILogger<AssetOptimizationMiddleware> _logger;
    private static int _requestCount;

    /// <summary>
    /// Maximum response body size to capture for on-the-fly optimization (2 MB).
    /// </summary>
    private const int MaxPluginCaptureBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetOptimizationMiddleware"/> class.
    /// </summary>
    public AssetOptimizationMiddleware(
        RequestDelegate next,
        DiskCacheManager cache,
        JsTransformer jsTransformer,
        CssTransformer cssTransformer,
        ILogger<AssetOptimizationMiddleware> logger)
    {
        _next = next;
        _cache = cache;
        _jsTransformer = jsTransformer;
        _cssTransformer = cssTransformer;
        _logger = logger;
        _logger.LogInformation("[JellyShim] AssetOptimizationMiddleware instantiated");
    }

    /// <summary>
    /// Processes the request.
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
            string? pluginEncoding = null;
            string? pluginContentEncoding = null;

            if (config.EnableCompression)
            {
                if (pluginAcceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
                {
                    pluginEncoding = "br";
                    pluginContentEncoding = "br";
                }
                else if (pluginAcceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                {
                    pluginEncoding = "gz";
                    pluginContentEncoding = "gzip";
                }
            }

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

            // Cache miss — capture the upstream response, minify, compress, cache, serve
            await CaptureAndOptimizePluginAsset(context, config, ext, path, pluginCacheKey, pluginEncoding, pluginContentEncoding).ConfigureAwait(false);
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
        string? encoding = null;
        string? contentEncoding = null;

        if (config.EnableCompression)
        {
            if (acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase))
            {
                encoding = "br";
                contentEncoding = "br";
            }
            else if (acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                encoding = "gz";
                contentEncoding = "gzip";
            }
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
            return;
        }

        var fileInfo = new FileInfo(cachedPath);

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

            // Cache the raw minified version
            _cache.Store(cacheKey, "raw", optimized);

            // For extensionless URLs, store the detected content type so cache hits serve correctly
            if (string.IsNullOrEmpty(ext))
            {
                _cache.Store(cacheKey, "meta", Encoding.UTF8.GetBytes(effectiveExt));
            }

            // Compress and cache both variants
            if (config.EnableCompression)
            {
                var brotli = CompressBrotli(optimized, config.BrotliCompressionLevel);
                var gzip = CompressGzip(optimized);
                _cache.Store(cacheKey, "br", brotli);
                _cache.Store(cacheKey, "gz", gzip);

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
            return;
        }

        var fileInfo = new FileInfo(cachedPath);
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
    /// Sets CORP, security headers, and a custom action via OnStarting callback
    /// for responses that pass through to the next middleware.
    /// </summary>
    private static void SetResponseHeaders(HttpContext context, PluginConfiguration config, Action headerAction)
    {
        context.Response.OnStarting(() =>
        {
            headerAction();

            if (config.EnableCrossOriginResourcePolicy)
            {
                context.Response.Headers["Cross-Origin-Resource-Policy"] = config.CrossOriginResourcePolicyValue;
            }

            AddSecurityHeaders(context.Response, config);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Determines appropriate cache policy based on the file path.
    /// </summary>
    private static void SetCacheHeaders(HttpResponse response, string path, PluginConfiguration config)
    {
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
