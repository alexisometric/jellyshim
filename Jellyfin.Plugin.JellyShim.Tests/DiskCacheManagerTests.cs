using System.Text;
using Jellyfin.Plugin.JellyShim.Cache;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class DiskCacheManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskCacheManager _cache;

    public DiskCacheManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jellyshim-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var logger = new Mock<ILogger<DiskCacheManager>>();
        _cache = new DiskCacheManager(_tempDir, logger.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Store_And_TryGetCachedFile_RoundTrip()
    {
        var content = "var x = 1;"u8.ToArray();
        _cache.Store("test/app.js", "raw", content);

        var found = _cache.TryGetCachedFile("test/app.js", "raw", out var cachedPath);

        Assert.True(found);
        Assert.True(File.Exists(cachedPath));
        Assert.Equal(content, File.ReadAllBytes(cachedPath));
    }

    [Fact]
    public void TryGetCachedFile_ReturnsFalse_WhenNotCached()
    {
        var found = _cache.TryGetCachedFile("nonexistent/file.js", "raw", out _);
        Assert.False(found);
    }

    [Fact]
    public void Store_MultileEncodings_IndependentPaths()
    {
        var raw = "body{margin:0}"u8.ToArray();
        var br = new byte[] { 1, 2, 3 }; // Simulated compressed
        var gz = new byte[] { 4, 5, 6 };

        _cache.Store("style.css", "raw", raw);
        _cache.Store("style.css", "br", br);
        _cache.Store("style.css", "gz", gz);

        _cache.TryGetCachedFile("style.css", "raw", out var rawPath);
        _cache.TryGetCachedFile("style.css", "br", out var brPath);
        _cache.TryGetCachedFile("style.css", "gz", out var gzPath);

        Assert.Equal(raw, File.ReadAllBytes(rawPath));
        Assert.Equal(br, File.ReadAllBytes(brPath));
        Assert.Equal(gz, File.ReadAllBytes(gzPath));

        // Paths should be different
        Assert.NotEqual(rawPath, brPath);
        Assert.NotEqual(brPath, gzPath);
    }

    [Fact]
    public void GetCachedFilePath_ThrowsOnPathTraversal_DoubleDot()
    {
        Assert.Throws<ArgumentException>(() =>
            _cache.GetCachedFilePath("../../etc/passwd", "raw"));
    }

    [Fact]
    public void GetCachedFilePath_ThrowsOnPathTraversal_EncodedDoubleDot()
    {
        Assert.Throws<ArgumentException>(() =>
            _cache.GetCachedFilePath("foo/../../../etc/passwd", "raw"));
    }

    [Fact]
    public void GetCachedFilePath_AllowsNormalNestedPaths()
    {
        // Should not throw
        var path = _cache.GetCachedFilePath("plugins/myPlugin/script.js", "raw");
        Assert.Contains("plugins", path);
        Assert.Contains("script.js", path);
    }

    [Fact]
    public void ComputeETag_ReturnsConsistentValue()
    {
        var content = "hello world"u8.ToArray();
        _cache.Store("etag-test.js", "raw", content);
        _cache.TryGetCachedFile("etag-test.js", "raw", out var cachedPath);

        var etag1 = _cache.ComputeETag(cachedPath);
        var etag2 = _cache.ComputeETag(cachedPath);

        Assert.Equal(etag1, etag2);
        Assert.StartsWith("\"", etag1);
        Assert.EndsWith("\"", etag1);
    }

    [Fact]
    public void ComputeETag_DiffersForDifferentContent()
    {
        _cache.Store("etag-a.js", "raw", "content-a"u8.ToArray());
        _cache.Store("etag-b.js", "raw", "content-b"u8.ToArray());

        _cache.TryGetCachedFile("etag-a.js", "raw", out var pathA);
        _cache.TryGetCachedFile("etag-b.js", "raw", out var pathB);

        var etagA = _cache.ComputeETag(pathA);
        var etagB = _cache.ComputeETag(pathB);

        Assert.NotEqual(etagA, etagB);
    }

    [Fact]
    public void InvalidateAll_RemovesAllCachedFiles()
    {
        _cache.Store("file1.js", "raw", "content1"u8.ToArray());
        _cache.Store("file2.css", "br", "content2"u8.ToArray());

        var (countBefore, _) = _cache.GetCacheStats();
        Assert.True(countBefore >= 2);

        _cache.InvalidateAll();

        var (countAfter, _) = _cache.GetCacheStats();
        Assert.Equal(0, countAfter);
    }

    [Fact]
    public void GetCacheStats_ReturnsCorrectCountAndSize()
    {
        var content = new byte[1024];
        _cache.Store("stats1.js", "raw", content);
        _cache.Store("stats2.js", "raw", content);

        var (count, totalBytes) = _cache.GetCacheStats();

        Assert.Equal(2, count);
        Assert.Equal(2048, totalBytes);
    }

    [Fact]
    public void IsCacheValid_ReturnsFalse_WhenNotCached()
    {
        var valid = _cache.IsCacheValid("nonexistent.js", "raw", DateTime.UtcNow);
        Assert.False(valid);
    }

    [Fact]
    public void IsCacheValid_ReturnsFalse_WhenSourceIsNewer()
    {
        _cache.Store("stale.js", "raw", "old"u8.ToArray());

        // Source modified after cache was built
        var futureTime = DateTime.UtcNow.AddHours(1);
        var valid = _cache.IsCacheValid("stale.js", "raw", futureTime);

        Assert.False(valid);
    }

    [Fact]
    public void IsCacheValid_ReturnsTrue_WhenCacheIsNewer()
    {
        var pastTime = DateTime.UtcNow.AddHours(-1);
        _cache.Store("fresh.js", "raw", "current"u8.ToArray());

        var valid = _cache.IsCacheValid("fresh.js", "raw", pastTime);

        Assert.True(valid);
    }

    [Fact]
    public async Task StoreAsync_WorksCorrectly()
    {
        var content = "async content"u8.ToArray();
        await _cache.StoreAsync("async-test.js", "raw", content);

        var found = _cache.TryGetCachedFile("async-test.js", "raw", out var cachedPath);

        Assert.True(found);
        Assert.Equal(content, await File.ReadAllBytesAsync(cachedPath));
    }

    [Fact]
    public void Store_OverwritesExistingFile()
    {
        _cache.Store("overwrite.js", "raw", "original"u8.ToArray());
        _cache.Store("overwrite.js", "raw", "updated"u8.ToArray());

        _cache.TryGetCachedFile("overwrite.js", "raw", out var cachedPath);
        var content = File.ReadAllText(cachedPath);

        Assert.Equal("updated", content);
    }

    [Fact]
    public void CacheRoot_IsUnderProvidedPath()
    {
        Assert.StartsWith(_tempDir, _cache.CacheRoot);
        Assert.Contains("jellyshim", _cache.CacheRoot);
    }

    // ── InvalidatePrefix tests ────────────────────────────────────

    [Fact]
    public void InvalidatePrefix_RemovesMatchingFiles()
    {
        _cache.Store("ft/runtime.bundle.js", "raw", "content1"u8.ToArray());
        _cache.Store("ft/runtime.bundle.js", "br", "content2"u8.ToArray());
        _cache.Store("ft/runtime.bundle.js", "gz", "content3"u8.ToArray());
        _cache.Store("other/file.js", "raw", "keep"u8.ToArray());

        _cache.InvalidatePrefix("ft/");

        Assert.False(_cache.TryGetCachedFile("ft/runtime.bundle.js", "raw", out _));
        Assert.False(_cache.TryGetCachedFile("ft/runtime.bundle.js", "br", out _));
        Assert.False(_cache.TryGetCachedFile("ft/runtime.bundle.js", "gz", out _));
        Assert.True(_cache.TryGetCachedFile("other/file.js", "raw", out _));
    }

    [Fact]
    public void InvalidatePrefix_ClearsETagCacheForAffectedFiles()
    {
        _cache.Store("ft/test.js", "raw", "content"u8.ToArray());
        _cache.TryGetCachedFile("ft/test.js", "raw", out var cachedPath);

        // Generate an ETag (populates in-memory cache)
        var etag1 = _cache.ComputeETag(cachedPath);
        Assert.NotNull(etag1);

        _cache.InvalidatePrefix("ft/");

        // File is gone, so TryGetCachedFile should return false
        Assert.False(_cache.TryGetCachedFile("ft/test.js", "raw", out _));
    }

    [Fact]
    public void InvalidatePrefix_NoOpForNonexistentPrefix()
    {
        _cache.Store("keep.js", "raw", "content"u8.ToArray());

        // Should not throw
        _cache.InvalidatePrefix("nonexistent/prefix/");

        Assert.True(_cache.TryGetCachedFile("keep.js", "raw", out _));
    }

    [Fact]
    public void InvalidatePrefix_RejectsPathTraversal()
    {
        Assert.Throws<ArgumentException>(() =>
            _cache.InvalidatePrefix("../../etc/"));
    }

    [Fact]
    public void InvalidateAll_CreatesEmptyCacheRoot()
    {
        _cache.Store("file.js", "raw", "content"u8.ToArray());
        _cache.InvalidateAll();

        Assert.True(Directory.Exists(_cache.CacheRoot));
        var (count, _) = _cache.GetCacheStats();
        Assert.Equal(0, count);
    }
}
