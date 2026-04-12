using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Configuration;
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
    private readonly ILogger<AssetOptimizationMiddleware> _logger;
    private static int _requestCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetOptimizationMiddleware"/> class.
    /// </summary>
    public AssetOptimizationMiddleware(RequestDelegate next, DiskCacheManager cache, ILogger<AssetOptimizationMiddleware> logger)
    {
        _next = next;
        _cache = cache;
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

        // Plugin assets aren't pre-processed into our cache (they live in plugin dirs).
        // For these, pass through with headers, compression handled by Kestrel/reverse-proxy.
        if (relativePath is null)
        {
            SetResponseHeaders(context, config, () =>
            {
                if (config.EnableCacheHeaders)
                {
                    SetPassthroughCacheHeaders(context.Response, path, isPluginAsset, config);
                }

                if (config.EnableJsModulepreloadHeaders &&
                    ext.Equals(".js", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.Append("Link", $"<{path}>; rel=modulepreload");
                }
            });
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
