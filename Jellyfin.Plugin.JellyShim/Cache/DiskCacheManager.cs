using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Cache;

/// <summary>
/// Manages a disk-based cache for pre-optimized and pre-compressed assets.
///
/// <para><b>Cache structure:</b></para>
/// <code>
/// {CachePath}/jellyshim/
///   raw/    ← minified but uncompressed (used for ETag computation + fallback serving)
///   br/     ← Brotli-compressed variant
///   gz/     ← Gzip-compressed variant
///   meta/   ← metadata (e.g. detected Content-Type for extensionless plugin URLs)
///   img/    ← processed images (resized/re-encoded by ImageProcessor)
/// </code>
///
/// <para>Each sub-directory mirrors the relative path of the original asset.
/// For example, <c>/web/main.abcdef12.bundle.js</c> is stored as
/// <c>{cache}/br/main.abcdef12.bundle.js</c> for the Brotli variant.</para>
///
/// <para><b>ETag caching:</b> SHA-256 hashes are computed from the raw file contents
/// and memoized in a <see cref="ConcurrentDictionary{TKey, TValue}"/>. The memo
/// is invalidated when the file's last-write timestamp changes, so updates to cached
/// files are reflected immediately without restarting the server.</para>
///
/// <para><b>Path traversal protection:</b> All relative paths are validated to ensure
/// they don't contain <c>..</c> segments and that the resolved path stays under the
/// cache root directory. This prevents a malicious request path from reading/writing
/// files elsewhere on the filesystem.</para>
///
/// <para><b>Cache never modifies original files</b> — it only reads from Jellyfin's
/// web root and stores copies under its own directory.</para>
/// </summary>
public class DiskCacheManager
{
    /// <summary>Absolute path to the cache root directory ({CachePath}/jellyshim).</summary>
    private readonly string _cacheRoot;
    private readonly ILogger<DiskCacheManager> _logger;

    /// <summary>
    /// In-memory ETag cache: maps file path → (lastWriteUtc, etagString).
    /// Avoids re-hashing unchanged files on every conditional request (If-None-Match).
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime LastWrite, string ETag)> _etagCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiskCacheManager"/> class.
    /// </summary>
    public DiskCacheManager(string cachePath, ILogger<DiskCacheManager> logger)
    {
        _cacheRoot = Path.Combine(cachePath, "jellyshim");
        _logger = logger;
        Directory.CreateDirectory(_cacheRoot);
    }

    /// <summary>
    /// Gets the root cache directory.
    /// </summary>
    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// Gets the full filesystem path to a cached file for a given relative path and encoding variant.
    /// The encoding parameter selects the sub-directory: "raw", "br", "gz", "meta", or "img".
    ///
    /// <para><b>Security:</b> Rejects paths containing ".." segments and validates that the
    /// resolved absolute path stays under <see cref="CacheRoot"/> to prevent path traversal
    /// attacks from crafted request URLs.</para>
    /// </summary>
    public string GetCachedFilePath(string relativePath, string encoding)
    {
        var safePath = relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        // Prevent path traversal
        if (safePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal detected", nameof(relativePath));
        }

        var fullPath = Path.Combine(_cacheRoot, encoding, safePath);

        // Ensure resolved path is still under cache root
        var resolved = Path.GetFullPath(fullPath);
        if (!resolved.StartsWith(_cacheRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal detected", nameof(relativePath));
        }

        return fullPath;
    }

    /// <summary>
    /// Checks whether a cached version exists and is still valid.
    /// Compares the cache file's last-write timestamp against the source file's timestamp.
    /// Returns <c>false</c> if the source has been modified since the cache was built,
    /// which triggers re-optimization on the next request.
    /// Used by <see cref="Optimization.AssetProcessor"/> during scheduled pre-optimization
    /// to skip files that haven't changed since last build.
    /// </summary>
    public bool IsCacheValid(string relativePath, string encoding, DateTime sourceLastModified)
    {
        var cachedPath = GetCachedFilePath(relativePath, encoding);
        if (!File.Exists(cachedPath))
        {
            return false;
        }

        var cacheWriteTime = File.GetLastWriteTimeUtc(cachedPath);
        return cacheWriteTime >= sourceLastModified;
    }

    /// <summary>
    /// Tries to get a cached file's full path. Returns false if not cached or stale.
    /// </summary>
    public bool TryGetCachedFile(string relativePath, string encoding, out string cachedPath)
    {
        cachedPath = GetCachedFilePath(relativePath, encoding);
        return File.Exists(cachedPath);
    }

    /// <summary>
    /// Stores content in the cache.
    /// </summary>
    public void Store(string relativePath, string encoding, byte[] content)
    {
        var cachedPath = GetCachedFilePath(relativePath, encoding);
        var dir = Path.GetDirectoryName(cachedPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(cachedPath, content);
    }

    /// <summary>
    /// Stores content in the cache asynchronously.
    /// </summary>
    public async Task StoreAsync(string relativePath, string encoding, byte[] content, CancellationToken cancellationToken = default)
    {
        var cachedPath = GetCachedFilePath(relativePath, encoding);
        var dir = Path.GetDirectoryName(cachedPath);
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(cachedPath, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes an ETag for a cached file based on its SHA-256 content hash.
    /// Results are memoized in memory and automatically invalidated when the
    /// file's last-write timestamp changes. The ETag format is a quoted hex string
    /// of the first 8 bytes of the hash: <c>"a1b2c3d4e5f67890"</c>.
    ///
    /// <para>This is used for HTTP conditional requests (If-None-Match / 304 Not Modified)
    /// which avoids transferring the full response body when the browser already has
    /// the current version cached.</para>
    /// </summary>
    public string ComputeETag(string filePath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(filePath);

        if (_etagCache.TryGetValue(filePath, out var cached) && cached.LastWrite == lastWrite)
        {
            return cached.ETag;
        }

        using var stream = File.OpenRead(filePath);
        var hash = new byte[32];
        SHA256.HashData(stream, hash);
        var etag = $"\"{Convert.ToHexStringLower(hash.AsSpan(0, 8))}\"";

        _etagCache[filePath] = (lastWrite, etag);
        return etag;
    }

    /// <summary>
    /// Invalidates the entire cache by deleting and recreating the cache root directory.
    /// Also clears the in-memory ETag cache.
    /// Called on middleware startup (server restart) and by the ClearCacheTask.
    /// </summary>
    public void InvalidateAll()
    {
        _etagCache.Clear();

        if (Directory.Exists(_cacheRoot))
        {
            Directory.Delete(_cacheRoot, recursive: true);
            Directory.CreateDirectory(_cacheRoot);
            _logger.LogInformation("[JellyShim] Cache invalidated");
        }
    }

    /// <summary>
    /// Invalidates all cache entries whose relative path starts with the given prefix.
    /// Deletes files from all encoding sub-directories (raw, br, gz, meta, img) and
    /// cleans up the corresponding in-memory ETag entries.
    /// Used to selectively purge FT-captured assets when their source files change.
    /// </summary>
    public void InvalidatePrefix(string prefix)
    {
        var safePath = prefix.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

        // Prevent path traversal
        if (safePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal detected", nameof(prefix));
        }

        int deletedFiles = 0;

        // Clear entries from all encoding subdirectories (raw, br, gz, meta)
        foreach (var encodingDir in Directory.GetDirectories(_cacheRoot))
        {
            var prefixDir = Path.Combine(encodingDir, safePath);
            var resolvedDir = Path.GetFullPath(prefixDir);

            // Safety: ensure resolved path stays under cache root
            if (!resolvedDir.StartsWith(Path.GetFullPath(_cacheRoot), StringComparison.Ordinal))
            {
                continue;
            }

            if (Directory.Exists(prefixDir))
            {
                var files = Directory.GetFiles(prefixDir, "*", SearchOption.AllDirectories);
                deletedFiles += files.Length;
                Directory.Delete(prefixDir, recursive: true);
            }
        }

        // Clear matching ETag cache entries (match on path segment boundaries)
        var matchPattern = Path.DirectorySeparatorChar + safePath + Path.DirectorySeparatorChar;
        foreach (var key in _etagCache.Keys)
        {
            if (key.Contains(matchPattern, StringComparison.OrdinalIgnoreCase))
            {
                _etagCache.TryRemove(key, out _);
            }
        }

        if (deletedFiles > 0)
        {
            _logger.LogInformation("[JellyShim] Invalidated {Count} cached files with prefix '{Prefix}'", deletedFiles, prefix);
        }
    }

    /// <summary>
    /// Gets total cached files count and size.
    /// </summary>
    public (int FileCount, long TotalBytes) GetCacheStats()
    {
        if (!Directory.Exists(_cacheRoot))
        {
            return (0, 0);
        }

        var files = Directory.GetFiles(_cacheRoot, "*", SearchOption.AllDirectories);
        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes += new FileInfo(file).Length;
        }

        return (files.Length, totalBytes);
    }

    /// <summary>
    /// Gets cache statistics broken down by encoding sub-directory (raw, br, gz, zstd, meta, img).
    /// Returns a dictionary mapping sub-directory name to (FileCount, TotalBytes).
    /// </summary>
    public Dictionary<string, object> GetCacheStatsByPrefix()
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_cacheRoot))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(_cacheRoot))
        {
            var name = Path.GetFileName(dir);
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            long totalBytes = 0;
            foreach (var file in files)
            {
                totalBytes += new FileInfo(file).Length;
            }

            result[name] = new { FileCount = files.Length, TotalBytes = totalBytes };
        }

        return result;
    }
}
