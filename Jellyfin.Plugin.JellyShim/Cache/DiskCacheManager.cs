using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Cache;

/// <summary>
/// Manages a disk cache for pre-optimized and pre-compressed assets.
/// Cache lives under {CachePath}/jellyshim/ and never modifies original files.
/// </summary>
public class DiskCacheManager
{
    private readonly string _cacheRoot;
    private readonly ILogger<DiskCacheManager> _logger;

    // In-memory ETag cache: path → (lastWriteUtc, etag)
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
    /// Gets the full path to a cached file for a given relative path and encoding.
    /// Path traversal is prevented by rejecting segments containing "..".
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
    /// Checks whether a cached version exists and is still valid (source hasn't been modified since cache was built).
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
    /// Computes an ETag for a cached file based on its content hash.
    /// Results are cached in memory and invalidated when the file's last-write time changes.
    /// </summary>
    public string ComputeETag(string filePath)
    {
        var lastWrite = File.GetLastWriteTimeUtc(filePath);

        if (_etagCache.TryGetValue(filePath, out var cached) && cached.LastWrite == lastWrite)
        {
            return cached.ETag;
        }

        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        var etag = $"\"{Convert.ToHexStringLower(hash[..8])}\"";

        _etagCache[filePath] = (lastWrite, etag);
        return etag;
    }

    /// <summary>
    /// Invalidates the entire cache.
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
}
