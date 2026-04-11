using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Pre-compresses content using Brotli and Gzip at build time.
/// </summary>
public class PreCompressor
{
    private readonly ILogger<PreCompressor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreCompressor"/> class.
    /// </summary>
    public PreCompressor(ILogger<PreCompressor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compresses data with Brotli at the specified quality level.
    /// </summary>
    /// <param name="input">The raw bytes to compress.</param>
    /// <param name="quality">Brotli quality level (0-11). Default 11 for max compression.</param>
    /// <returns>The compressed bytes.</returns>
    public byte[] CompressBrotli(byte[] input, int quality = 11)
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
    /// Compresses data with Brotli asynchronously.
    /// </summary>
    public async Task<byte[]> CompressBrotliAsync(byte[] input, int quality = 11, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        // BrotliStream with CompressionLevel.SmallestSize maps to quality 11
        var level = quality >= 10 ? CompressionLevel.SmallestSize : CompressionLevel.Optimal;
        using (var brotli = new BrotliStream(output, level, leaveOpen: true))
        {
            await brotli.WriteAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compresses data with Gzip at optimal level.
    /// </summary>
    /// <param name="input">The raw bytes to compress.</param>
    /// <returns>The compressed bytes.</returns>
    public byte[] CompressGzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compresses data with Gzip asynchronously.
    /// </summary>
    public async Task<byte[]> CompressGzipAsync(byte[] input, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Compresses input and returns both Brotli and Gzip variants.
    /// </summary>
    public (byte[] Brotli, byte[] Gzip) CompressBoth(byte[] input, int brotliQuality = 11)
    {
        var br = CompressBrotli(input, brotliQuality);
        var gz = CompressGzip(input);
        if (input.Length > 0)
        {
            _logger.LogDebug(
                "[JellyShim] Compressed {InputSize} → BR {BrSize} ({BrRatio:P0}), GZ {GzSize} ({GzRatio:P0})",
                input.Length,
                br.Length,
                1.0 - ((double)br.Length / input.Length),
                gz.Length,
                1.0 - ((double)gz.Length / input.Length));
        }
        return (br, gz);
    }
}
