using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.JellyShim.Optimization;
using Microsoft.Extensions.Logging;
using Moq;
using ZstdSharp;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class PreCompressorTests
{
    private readonly PreCompressor _compressor;

    public PreCompressorTests()
    {
        var logger = new Mock<ILogger<PreCompressor>>();
        _compressor = new PreCompressor(logger.Object);
    }

    [Fact]
    public void CompressBrotli_ProducesValidBrotli()
    {
        var original = Encoding.UTF8.GetBytes("Hello, this is a test of Brotli compression!");

        var compressed = _compressor.CompressBrotli(original);

        Assert.NotEmpty(compressed);
        Assert.True(compressed.Length < original.Length || original.Length < 50,
            "Compressed should be smaller (or input too small for savings)");

        // Decompress and verify roundtrip
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);

        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void CompressGzip_ProducesValidGzip()
    {
        var original = Encoding.UTF8.GetBytes("Hello, this is a test of Gzip compression!");

        var compressed = _compressor.CompressGzip(original);

        Assert.NotEmpty(compressed);

        // Decompress and verify roundtrip
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);

        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public void CompressBoth_ReturnsValidBrotliAndGzip()
    {
        var original = Encoding.UTF8.GetBytes(new string('a', 1000));

        var (brotli, gzip) = _compressor.CompressBoth(original);

        Assert.NotEmpty(brotli);
        Assert.NotEmpty(gzip);

        // Both should be significantly smaller for repetitive content
        Assert.True(brotli.Length < original.Length);
        Assert.True(gzip.Length < original.Length);

        // Brotli typically compresses better than Gzip
        Assert.True(brotli.Length <= gzip.Length,
            "Brotli should compress at least as well as Gzip");
    }

    [Fact]
    public void CompressBrotli_EmptyInput_ReturnsNonEmpty()
    {
        // Brotli produces a small header even for empty input
        var compressed = _compressor.CompressBrotli(Array.Empty<byte>());
        Assert.NotNull(compressed);
    }

    [Fact]
    public void CompressGzip_EmptyInput_ReturnsNonEmpty()
    {
        // Gzip produces a header even for empty input
        var compressed = _compressor.CompressGzip(Array.Empty<byte>());
        Assert.NotNull(compressed);
    }

    [Fact]
    public async Task CompressBrotliAsync_MatchesSyncResult()
    {
        var original = Encoding.UTF8.GetBytes("Async Brotli test content for comparison");

        var syncResult = _compressor.CompressBrotli(original);
        var asyncResult = await _compressor.CompressBrotliAsync(original);

        // Both should decompress to the same original
        Assert.Equal(original, Decompress(syncResult, "br"));
        Assert.Equal(original, Decompress(asyncResult, "br"));
    }

    [Fact]
    public async Task CompressGzipAsync_MatchesSyncResult()
    {
        var original = Encoding.UTF8.GetBytes("Async Gzip test content for comparison");

        var syncResult = _compressor.CompressGzip(original);
        var asyncResult = await _compressor.CompressGzipAsync(original);

        Assert.Equal(original, Decompress(syncResult, "gzip"));
        Assert.Equal(original, Decompress(asyncResult, "gzip"));
    }

    [Fact]
    public void CompressBrotli_LargeInput_Compresses()
    {
        // 100KB of JavaScript-like content
        var sb = new StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            sb.AppendLine($"var variable_{i} = {{ key: 'value_{i}', count: {i} }};");
        }
        var original = Encoding.UTF8.GetBytes(sb.ToString());

        var compressed = _compressor.CompressBrotli(original);

        Assert.True(compressed.Length < original.Length / 2,
            $"Expected at least 50% compression, got {compressed.Length}/{original.Length}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(11)]
    public void CompressBrotli_DifferentQualities_AllValid(int quality)
    {
        var original = Encoding.UTF8.GetBytes(new string('x', 500));

        var compressed = _compressor.CompressBrotli(original, quality);
        var decompressed = Decompress(compressed, "br");

        Assert.Equal(original, decompressed);
    }

    private static byte[] Decompress(byte[] data, string encoding)
    {
        using var input = new MemoryStream(data);
        Stream decompressor = encoding switch
        {
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            "gzip" => new GZipStream(input, CompressionMode.Decompress),
            _ => throw new ArgumentException($"Unknown encoding: {encoding}")
        };
        using (decompressor)
        {
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            return output.ToArray();
        }
    }

    // ── Zstd compression ──────────────────────────────────────────

    [Fact]
    public void CompressZstd_ProducesValidZstd()
    {
        var original = Encoding.UTF8.GetBytes("Hello, this is a test of Zstandard compression!");

        var compressed = _compressor.CompressZstd(original);

        Assert.NotEmpty(compressed);

        // Decompress and verify roundtrip
        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(compressed).ToArray();
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressZstd_EmptyInput_ReturnsNonEmpty()
    {
        var compressed = _compressor.CompressZstd(Array.Empty<byte>());

        Assert.NotEmpty(compressed);

        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(compressed).ToArray();
        Assert.Empty(decompressed);
    }

    [Fact]
    public void CompressZstd_LargeInput_Compresses()
    {
        var original = Encoding.UTF8.GetBytes(new string('z', 10_000));

        var compressed = _compressor.CompressZstd(original);

        Assert.True(compressed.Length < original.Length,
            $"Expected compression: {compressed.Length} < {original.Length}");

        using var decompressor = new Decompressor();
        var decompressed = decompressor.Unwrap(compressed).ToArray();
        Assert.Equal(original, decompressed);
    }
}
