using Jellyfin.Plugin.JellyShim.Optimization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class ImageProcessorTests : IDisposable
{
    private readonly ImageProcessor _processor;
    private readonly string _tempDir;

    public ImageProcessorTests()
    {
        var logger = new Mock<ILogger<ImageProcessor>>();
        var configManager = new Mock<IConfigurationManager>();
        _processor = new ImageProcessor(logger.Object, configManager.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"jellyshim_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Helper: create a solid color test image ─────────────────────

    private static byte[] CreateTestJpeg(int width, int height, int quality = 80)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(100, 150, 200, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private static byte[] CreateTestPng(int width, int height, bool transparent = false)
    {
        var color = transparent
            ? new Rgba32(100, 150, 200, 128) // semi-transparent
            : new Rgba32(100, 150, 200, 255);
        using var image = new Image<Rgba32>(width, height, color);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // ── Process: basic format conversion ────────────────────────────

    [Fact]
    public void Process_EmptyInput_ReturnsEmpty()
    {
        var (data, format) = _processor.Process([], 0, 80, "webp");
        Assert.Empty(data);
        Assert.Equal("webp", format);
    }

    [Fact]
    public void Process_ToWebp_ProducesValidWebp()
    {
        var input = CreateTestJpeg(200, 100);

        var (data, format) = _processor.Process(input, 0, 80, "webp");

        Assert.NotEmpty(data);
        Assert.Equal("webp", format);
        // Verify it's a valid WebP by loading it back
        using var img = Image.Load<Rgba32>(data);
        Assert.Equal(200, img.Width);
        Assert.Equal(100, img.Height);
    }

    [Fact]
    public void Process_ToJpeg_ProducesValidJpeg()
    {
        var input = CreateTestPng(300, 200);

        var (data, format) = _processor.Process(input, 0, 75, "jpeg");

        Assert.NotEmpty(data);
        Assert.Equal("jpeg", format);
        using var img = Image.Load<Rgba32>(data);
        Assert.Equal(300, img.Width);
        Assert.Equal(200, img.Height);
    }

    // ── Process: resize (downscale only) ────────────────────────────

    [Fact]
    public void Process_DownscalesWhenWiderThanMaxWidth()
    {
        var input = CreateTestJpeg(800, 600);

        var (data, _) = _processor.Process(input, 400, 80, "webp");

        using var img = Image.Load<Rgba32>(data);
        Assert.Equal(400, img.Width);
        // Aspect ratio preserved: 600 * 400/800 = 300
        Assert.Equal(300, img.Height);
    }

    [Fact]
    public void Process_DoesNotUpscale()
    {
        var input = CreateTestJpeg(200, 100);

        var (data, _) = _processor.Process(input, 400, 80, "webp");

        using var img = Image.Load<Rgba32>(data);
        Assert.Equal(200, img.Width);
        Assert.Equal(100, img.Height);
    }

    [Fact]
    public void Process_MaxWidthZero_NoResize()
    {
        var input = CreateTestJpeg(800, 600);

        var (data, _) = _processor.Process(input, 0, 80, "webp");

        using var img = Image.Load<Rgba32>(data);
        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);
    }

    // ── Process: AVIF transparency fallback ─────────────────────────

    [Fact]
    public void Process_AvifWithTransparentPixels_FallsBackToWebP()
    {
        // Create a PNG with transparent pixels
        var input = CreateTestPng(100, 100, transparent: true);

        var (data, format) = _processor.Process(input, 0, 80, "avif");

        // Should fall back to WebP for transparency
        Assert.Equal("webp", format);
        Assert.NotEmpty(data);
    }

    [Fact]
    public void Process_AvifWithOpaquePixels_AttemptsAvif()
    {
        var input = CreateTestJpeg(100, 100);

        // AVIF requires ffmpeg — this will either succeed (if ffmpeg is available)
        // or throw (if not). Either way, it should NOT fall back to WebP for opaque images.
        try
        {
            var (data, format) = _processor.Process(input, 0, 80, "avif");
            Assert.Equal("avif", format);
            Assert.NotEmpty(data);
        }
        catch (FileNotFoundException)
        {
            // Expected when ffmpeg isn't installed — the point is it tried AVIF, not WebP
        }
        catch (Exception ex) when (ex.Message.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            // Expected when ffmpeg isn't available
        }
    }

    // ── Temp file cleanup ───────────────────────────────────────────

    [Fact]
    public void CleanupOrphanedTempFiles_RemovesLeftoverFiles()
    {
        // Create fake orphaned temp files
        var tempPath = Path.GetTempPath();
        var orphan1 = Path.Combine(tempPath, "jellyshim_test1.png");
        var orphan2 = Path.Combine(tempPath, "jellyshim_test2.avif");
        File.WriteAllText(orphan1, "fake");
        File.WriteAllText(orphan2, "fake");

        try
        {
            // Creating a new ImageProcessor triggers cleanup
            var logger = new Mock<ILogger<ImageProcessor>>();
            var configManager = new Mock<IConfigurationManager>();
            _ = new ImageProcessor(logger.Object, configManager.Object);

            // Orphaned files should be cleaned up
            Assert.False(File.Exists(orphan1), "jellyshim_test1.png should have been cleaned up");
            Assert.False(File.Exists(orphan2), "jellyshim_test2.avif should have been cleaned up");
        }
        finally
        {
            // Safety cleanup in case test fails
            try { File.Delete(orphan1); } catch { }
            try { File.Delete(orphan2); } catch { }
        }
    }

    // ── Quality / size ──────────────────────────────────────────────

    [Fact]
    public void Process_LowerQuality_ProducesSmallerFile()
    {
        // Use a large image with varied pixel data so quality differences are measurable
        using var image = new Image<Rgba32>(800, 600);
        var rng = new Random(42);
        for (int y = 0; y < 600; y++)
        for (int x = 0; x < 800; x++)
            image[x, y] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        var input = ms.ToArray();

        var (highQ, _) = _processor.Process(input, 0, 95, "jpeg");
        var (lowQ, _) = _processor.Process(input, 0, 30, "jpeg");

        Assert.True(lowQ.Length < highQ.Length,
            $"Low quality ({lowQ.Length}B) should be smaller than high quality ({highQ.Length}B)");
    }
}
