namespace Jellyfin.Plugin.JellyShim.Tests;

public class PerformanceStatsTrackerTests
{
    // ── Basic counter operations ──────────────────────────────────

    [Fact]
    public void RecordCacheHit_IncrementsCounter()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit();
        tracker.RecordCacheHit();
        tracker.RecordCacheHit();

        Assert.Equal(3, tracker.CacheHits);
    }

    [Fact]
    public void RecordCacheHit_AccumulatesBytesServed()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit(100);
        tracker.RecordCacheHit(250);

        Assert.Equal(2, tracker.CacheHits);
        Assert.Equal(350, tracker.BytesServedFromCache);
    }

    [Fact]
    public void RecordCacheHit_ZeroBytes_DoesNotAddToServed()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit(0);

        Assert.Equal(1, tracker.CacheHits);
        Assert.Equal(0, tracker.BytesServedFromCache);
    }

    [Fact]
    public void RecordCacheMiss_IncrementsCounter()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheMiss();
        tracker.RecordCacheMiss();

        Assert.Equal(2, tracker.CacheMisses);
    }

    [Fact]
    public void RecordCompressionSaving_AddsPositiveDelta()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCompressionSaving(1000, 300);

        Assert.Equal(700, tracker.BytesSavedByCompression);
    }

    [Fact]
    public void RecordCompressionSaving_IgnoresWhenCompressedIsLarger()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCompressionSaving(100, 200);

        Assert.Equal(0, tracker.BytesSavedByCompression);
    }

    [Fact]
    public void RecordCompressionSaving_IgnoresWhenEqual()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCompressionSaving(500, 500);

        Assert.Equal(0, tracker.BytesSavedByCompression);
    }

    // ── Per-category request counters ─────────────────────────────

    [Fact]
    public void RecordRequestCategories_IncrementIndependently()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordWebAssetRequest();
        tracker.RecordWebAssetRequest();
        tracker.RecordPluginAssetRequest();
        tracker.RecordFtAssetRequest();
        tracker.RecordImageRequest();
        tracker.RecordImageRequest();
        tracker.RecordImageRequest();
        tracker.RecordNotModified();

        Assert.Equal(2, tracker.WebAssetRequests);
        Assert.Equal(1, tracker.PluginAssetRequests);
        Assert.Equal(1, tracker.FtAssetRequests);
        Assert.Equal(3, tracker.ImageRequests);
        Assert.Equal(1, tracker.NotModifiedResponses);
    }

    // ── Hit rate calculation ──────────────────────────────────────

    [Fact]
    public void HitRate_ReturnsZero_WhenNoActivity()
    {
        var tracker = new PerformanceStatsTracker();

        Assert.Equal(0, tracker.HitRate);
    }

    [Fact]
    public void HitRate_ReturnsCorrectPercentage()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit();
        tracker.RecordCacheHit();
        tracker.RecordCacheHit();
        tracker.RecordCacheMiss();

        Assert.Equal(75.0, tracker.HitRate);
    }

    [Fact]
    public void HitRate_Returns100_WhenAllHits()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit();
        tracker.RecordCacheHit();

        Assert.Equal(100.0, tracker.HitRate);
    }

    // ── Reset ─────────────────────────────────────────────────────

    [Fact]
    public void Reset_ZerosAllCounters()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit(500);
        tracker.RecordCacheMiss();
        tracker.RecordCompressionSaving(1000, 200);
        tracker.RecordWebAssetRequest();
        tracker.RecordPluginAssetRequest();
        tracker.RecordFtAssetRequest();
        tracker.RecordImageRequest();
        tracker.RecordNotModified();

        tracker.Reset();

        Assert.Equal(0, tracker.CacheHits);
        Assert.Equal(0, tracker.CacheMisses);
        Assert.Equal(0, tracker.BytesServedFromCache);
        Assert.Equal(0, tracker.BytesSavedByCompression);
        Assert.Equal(0, tracker.WebAssetRequests);
        Assert.Equal(0, tracker.PluginAssetRequests);
        Assert.Equal(0, tracker.FtAssetRequests);
        Assert.Equal(0, tracker.ImageRequests);
        Assert.Equal(0, tracker.NotModifiedResponses);
        Assert.Equal(0, tracker.HitRate);
    }

    // ── GetSnapshot ───────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ReturnsCorrectValues()
    {
        var tracker = new PerformanceStatsTracker();

        tracker.RecordCacheHit(1000);
        tracker.RecordCacheHit(500);
        tracker.RecordCacheMiss();
        tracker.RecordCompressionSaving(2000, 400);
        tracker.RecordWebAssetRequest();
        tracker.RecordPluginAssetRequest();
        tracker.RecordFtAssetRequest();
        tracker.RecordImageRequest();
        tracker.RecordNotModified();

        var snapshot = tracker.GetSnapshot();

        Assert.Equal(2, snapshot.CacheHits);
        Assert.Equal(1, snapshot.CacheMisses);
        Assert.Equal(66.7, snapshot.HitRatePercent);
        Assert.Equal(1500, snapshot.BytesServedFromCache);
        Assert.Equal(1600, snapshot.BytesSavedByCompression);
        Assert.Equal(1, snapshot.WebAssetRequests);
        Assert.Equal(1, snapshot.PluginAssetRequests);
        Assert.Equal(1, snapshot.FtAssetRequests);
        Assert.Equal(1, snapshot.ImageRequests);
        Assert.Equal(1, snapshot.NotModifiedResponses);
    }

    // ── Thread safety ─────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentIncrements_AreThreadSafe()
    {
        var tracker = new PerformanceStatsTracker();
        const int iterations = 10_000;
        const int threads = 4;

        var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                tracker.RecordCacheHit(1);
                tracker.RecordCacheMiss();
                tracker.RecordWebAssetRequest();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(threads * iterations, tracker.CacheHits);
        Assert.Equal(threads * iterations, tracker.CacheMisses);
        Assert.Equal(threads * iterations, tracker.WebAssetRequests);
        Assert.Equal((long)threads * iterations, tracker.BytesServedFromCache);
    }
}
