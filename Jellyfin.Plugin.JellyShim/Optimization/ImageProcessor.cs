using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Native image processor using ImageSharp — resizes, re-encodes, and compresses images
/// without any external service dependency.
/// AVIF encoding uses Jellyfin's bundled ffmpeg (libaom-av1) — zero additional install required.
/// </summary>
public class ImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly Lazy<bool> _avifSupported;
    private string? _ffmpegPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessor"/> class.
    /// </summary>
    public ImageProcessor(ILogger<ImageProcessor> logger)
    {
        _logger = logger;
        _avifSupported = new Lazy<bool>(ProbeAvifSupport);
    }

    /// <summary>
    /// Gets a value indicating whether ffmpeg with libaom-av1 is available for AVIF encoding.
    /// Probed once on first access.
    /// </summary>
    public bool IsAvifSupported => _avifSupported.Value;

    /// <summary>
    /// Processes an image: resize to max width (preserving aspect ratio) and re-encode.
    /// Only downscales — images already smaller than maxWidth are not upscaled.
    /// </summary>
    /// <param name="input">Raw image bytes from Jellyfin.</param>
    /// <param name="maxWidth">Maximum output width in pixels. 0 = no resize.</param>
    /// <param name="quality">Output quality (1-100).</param>
    /// <param name="format">Output format: "avif", "webp", or "jpeg".</param>
    /// <returns>Processed image bytes.</returns>
    public byte[] Process(byte[] input, int maxWidth, int quality, string format)
    {
        if (input.Length == 0)
        {
            return input;
        }

        using var image = Image.Load(input);
        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // Only downscale, never upscale
        if (maxWidth > 0 && image.Width > maxWidth)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxWidth, 0)
            }));
        }

        using var output = new MemoryStream();

        switch (format.ToLowerInvariant())
        {
            case "avif":
                EncodeAvif(image, output, quality);
                break;
            case "webp":
                image.Save(output, new WebpEncoder
                {
                    Quality = quality,
                    Method = WebpEncodingMethod.BestQuality
                });
                break;
            default:
                image.Save(output, new JpegEncoder
                {
                    Quality = quality
                });
                break;
        }

        var result = output.ToArray();

        _logger.LogDebug(
            "[JellyShim] Image processed: {OrigSize}→{NewSize} bytes, {OrigW}×{OrigH}→{NewW}×{NewH}, {Format} q{Quality}",
            input.Length,
            result.Length,
            originalWidth,
            originalHeight,
            image.Width,
            image.Height,
            format,
            quality);

        return result;
    }

    /// <summary>
    /// Encodes an ImageSharp image to AVIF using Jellyfin's bundled ffmpeg (libaom-av1 encoder).
    /// Uses temp files for maximum compatibility across ffmpeg versions.
    /// Falls back to WebP if encoding fails.
    /// </summary>
    private void EncodeAvif(Image image, MemoryStream output, int quality)
    {
        if (_ffmpegPath is null)
        {
            _logger.LogWarning("[JellyShim] ffmpeg not available, falling back to WebP");
            image.Save(output, new WebpEncoder { Quality = quality, Method = WebpEncodingMethod.BestQuality });
            return;
        }

        var tempDir = Path.GetTempPath();
        var id = Guid.NewGuid().ToString("N");
        var inputPath = Path.Combine(tempDir, $"jellyshim_{id}.png");
        var outputPath = Path.Combine(tempDir, $"jellyshim_{id}.avif");

        try
        {
            // Save as PNG (lossless intermediate, fast compression)
            image.Save(inputPath, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });

            // Map quality (1-100) to CRF (63-0): lower CRF = higher quality
            var crf = (int)(63.0 * (100 - quality) / 100.0);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -y -i \"{inputPath}\" -c:v libaom-av1 -still-picture 1 -crf {crf} -cpu-used 6 -pix_fmt yuv420p \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch { }
                _logger.LogWarning("[JellyShim] ffmpeg AVIF encoding timed out, falling back to WebP");
                image.Save(output, new WebpEncoder { Quality = quality, Method = WebpEncodingMethod.BestQuality });
                return;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("[JellyShim] ffmpeg AVIF encoding failed (exit {Code}): {Error}, falling back to WebP", process.ExitCode, stderr);
                image.Save(output, new WebpEncoder { Quality = quality, Method = WebpEncodingMethod.BestQuality });
                return;
            }

            var avifBytes = File.ReadAllBytes(outputPath);
            output.Write(avifBytes, 0, avifBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyShim] AVIF encoding exception, falling back to WebP");
            output.SetLength(0);
            image.Save(output, new WebpEncoder { Quality = quality, Method = WebpEncodingMethod.BestQuality });
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
            try { File.Delete(outputPath); } catch { }
        }
    }

    /// <summary>
    /// Returns the MIME content type for the given format.
    /// </summary>
    public static string GetContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "avif" => "image/avif",
            "webp" => "image/webp",
            _ => "image/jpeg"
        };
    }

    /// <summary>
    /// Probes whether ffmpeg with libaom-av1 encoder is available for AVIF encoding.
    /// </summary>
    private bool ProbeAvifSupport()
    {
        _ffmpegPath = FindFfmpegPath();
        if (_ffmpegPath is null)
        {
            _logger.LogInformation("[JellyShim] ffmpeg not found — AVIF encoding disabled, using WebP/JPEG");
            return false;
        }

        _logger.LogInformation("[JellyShim] Found ffmpeg at: {Path}", _ffmpegPath);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);

            var supported = stdout.Contains("libaom-av1", StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation("[JellyShim] AVIF encoding support (ffmpeg libaom-av1): {Supported}", supported);
            return supported;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyShim] Failed to probe ffmpeg for AVIF support");
            return false;
        }
    }

    /// <summary>
    /// Finds the ffmpeg binary on the system (Jellyfin-bundled or system-wide).
    /// </summary>
    private static string? FindFfmpegPath()
    {
        string[] candidates =
        {
            "ffmpeg",
            "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            "/usr/lib/jellyfin-ffmpeg/ffmpeg7",
            "/usr/bin/ffmpeg"
        };

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                process.Start();
                process.WaitForExit(5_000);

                if (process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Not found at this path, try next
            }
        }

        return null;
    }
}
