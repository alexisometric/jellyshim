using System.Threading;

namespace Jellyfin.Plugin.JellyShim;

/// <summary>
/// Thread-safe performance statistics tracker using atomic counters.
/// Tracks cache hit/miss rates, bytes served/saved, and per-category request counts.
/// Registered as a singleton — data persists for the lifetime of the Jellyfin process.
/// </summary>
public class PerformanceStatsTracker
{
    private long _cacheHits;
    private long _cacheMisses;
    private long _bytesServedFromCache;
    private long _bytesSavedByCompression;
    private long _webAssetRequests;
    private long _pluginAssetRequests;
    private long _ftAssetRequests;
    private long _imageRequests;
    private long _notModifiedResponses;

    /// <summary>Total cache hits.</summary>
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    /// <summary>Total cache misses.</summary>
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    /// <summary>Total bytes served from cache.</summary>
    public long BytesServedFromCache => Interlocked.Read(ref _bytesServedFromCache);
    /// <summary>Total bytes saved by compression.</summary>
    public long BytesSavedByCompression => Interlocked.Read(ref _bytesSavedByCompression);
    /// <summary>Total web asset requests.</summary>
    public long WebAssetRequests => Interlocked.Read(ref _webAssetRequests);
    /// <summary>Total plugin asset requests.</summary>
    public long PluginAssetRequests => Interlocked.Read(ref _pluginAssetRequests);
    /// <summary>Total File Transformation asset requests.</summary>
    public long FtAssetRequests => Interlocked.Read(ref _ftAssetRequests);
    /// <summary>Total image requests.</summary>
    public long ImageRequests => Interlocked.Read(ref _imageRequests);
    /// <summary>Total 304 Not Modified responses.</summary>
    public long NotModifiedResponses => Interlocked.Read(ref _notModifiedResponses);

    /// <summary>Cache hit rate as a percentage (0–100).</summary>
    public double HitRate
    {
        get
        {
            var total = CacheHits + CacheMisses;
            return total == 0 ? 0 : (double)CacheHits / total * 100;
        }
    }

    /// <summary>Records a cache hit, optionally tracking bytes served.</summary>
    public void RecordCacheHit(long bytesServed = 0)
    {
        Interlocked.Increment(ref _cacheHits);
        if (bytesServed > 0) Interlocked.Add(ref _bytesServedFromCache, bytesServed);
    }

    /// <summary>Records a cache miss.</summary>
    public void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);

    /// <summary>Records bytes saved by compression.</summary>
    public void RecordCompressionSaving(long originalBytes, long compressedBytes)
    {
        if (originalBytes > compressedBytes)
        {
            Interlocked.Add(ref _bytesSavedByCompression, originalBytes - compressedBytes);
        }
    }

    /// <summary>Records a web asset request.</summary>
    public void RecordWebAssetRequest() => Interlocked.Increment(ref _webAssetRequests);
    /// <summary>Records a plugin asset request.</summary>
    public void RecordPluginAssetRequest() => Interlocked.Increment(ref _pluginAssetRequests);
    /// <summary>Records a File Transformation asset request.</summary>
    public void RecordFtAssetRequest() => Interlocked.Increment(ref _ftAssetRequests);
    /// <summary>Records an image request.</summary>
    public void RecordImageRequest() => Interlocked.Increment(ref _imageRequests);
    /// <summary>Records a 304 Not Modified response.</summary>
    public void RecordNotModified() => Interlocked.Increment(ref _notModifiedResponses);

    /// <summary>
    /// Resets all counters to zero.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _bytesServedFromCache, 0);
        Interlocked.Exchange(ref _bytesSavedByCompression, 0);
        Interlocked.Exchange(ref _webAssetRequests, 0);
        Interlocked.Exchange(ref _pluginAssetRequests, 0);
        Interlocked.Exchange(ref _ftAssetRequests, 0);
        Interlocked.Exchange(ref _imageRequests, 0);
        Interlocked.Exchange(ref _notModifiedResponses, 0);
    }

    /// <summary>
    /// Returns a snapshot of all counters.
    /// </summary>
    public PerformanceSnapshot GetSnapshot() => new()
    {
        CacheHits = CacheHits,
        CacheMisses = CacheMisses,
        HitRatePercent = Math.Round(HitRate, 1),
        BytesServedFromCache = BytesServedFromCache,
        BytesSavedByCompression = BytesSavedByCompression,
        WebAssetRequests = WebAssetRequests,
        PluginAssetRequests = PluginAssetRequests,
        FtAssetRequests = FtAssetRequests,
        ImageRequests = ImageRequests,
        NotModifiedResponses = NotModifiedResponses
    };
}

/// <summary>
/// Immutable snapshot of performance counters.
/// </summary>
public class PerformanceSnapshot
{
    /// <summary>Total cache hits.</summary>
    public long CacheHits { get; init; }
    /// <summary>Total cache misses.</summary>
    public long CacheMisses { get; init; }
    /// <summary>Cache hit rate percentage.</summary>
    public double HitRatePercent { get; init; }
    /// <summary>Total bytes served from cache.</summary>
    public long BytesServedFromCache { get; init; }
    /// <summary>Total bytes saved by compression.</summary>
    public long BytesSavedByCompression { get; init; }
    /// <summary>Total web asset requests.</summary>
    public long WebAssetRequests { get; init; }
    /// <summary>Total plugin asset requests.</summary>
    public long PluginAssetRequests { get; init; }
    /// <summary>Total File Transformation asset requests.</summary>
    public long FtAssetRequests { get; init; }
    /// <summary>Total image requests.</summary>
    public long ImageRequests { get; init; }
    /// <summary>Total 304 Not Modified responses.</summary>
    public long NotModifiedResponses { get; init; }
}
