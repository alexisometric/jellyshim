using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Native image processor using ImageSharp for resize/re-encode operations.
///
/// <para><b>Supported output formats:</b></para>
/// <list type="bullet">
///   <item><b>JPEG</b> — universal fallback, best compatibility</item>
///   <item><b>WebP</b> — smaller than JPEG at equivalent quality, supported by all modern browsers</item>
///   <item><b>AVIF</b> — smallest file size, requires ffmpeg with an AV1 encoder
///     (libsvtav1 preferred for speed, libaom-av1 as fallback)</item>
/// </list>
///
/// <para><b>AVIF encoding pipeline:</b> ImageSharp → temp PNG → ffmpeg (SVT-AV1 or libaom-av1) → AVIF.
/// This uses Jellyfin's own ffmpeg binary, so no additional installation is needed.
/// The ffmpeg path is resolved via a priority cascade: Jellyfin config → JELLYFIN_FFMPEG
/// env → common system paths. See <see cref="FindFfmpegPath"/> for details.</para>
///
/// <para><b>Resize behavior:</b> Only downscales — images already smaller than the
/// configured maxWidth are never upscaled. Aspect ratio is always preserved.</para>
/// </summary>
public class ImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly IConfigurationManager? _configManager;
    private readonly Lazy<bool> _avifSupported;
    private string? _ffmpegPath;
    private string? _ffmpegSource;

    /// <summary>
    /// The AV1 encoder to use for AVIF encoding: "libsvtav1" (faster, preferred)
    /// or "libaom-av1" (slower, wider availability). Detected during probe.
    /// </summary>
    private string? _av1Encoder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessor"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="configManager">Jellyfin configuration manager — used to read the
    /// ffmpeg path configured in Dashboard → Playback → Transcoding.</param>
    public ImageProcessor(ILogger<ImageProcessor> logger, IConfigurationManager configManager)
    {
        _logger = logger;
        _configManager = configManager;
        _avifSupported = new Lazy<bool>(ProbeAvifSupport);
    }

    /// <summary>
    /// Gets a value indicating whether ffmpeg with an AV1 encoder (libsvtav1 or libaom-av1)
    /// is available for AVIF encoding. Probed once on first access.
    /// </summary>
    public bool IsAvifSupported => _avifSupported.Value;

    /// <summary>
    /// Gets the name of the AV1 encoder detected in ffmpeg (e.g. "libsvtav1" or "libaom-av1"),
    /// or null if no AV1 encoder is available.
    /// </summary>
    public string? Av1Encoder => _av1Encoder;

    /// <summary>
    /// Gets the path to the ffmpeg binary used for AVIF encoding, or null if not found.
    /// </summary>
    public string? FfmpegPath => _ffmpegPath;

    /// <summary>
    /// Gets a human-readable label indicating where the ffmpeg path was discovered
    /// (e.g. "Jellyfin configuration", "JELLYFIN_FFMPEG env", "/usr/bin/ffmpeg").
    /// Null when ffmpeg hasn't been probed yet or wasn't found.
    /// </summary>
    public string? FfmpegSource => _ffmpegSource;

    /// <summary>
    /// Processes an image: resize to max width (preserving aspect ratio) and re-encode.
    /// Only downscales — images already smaller than maxWidth are not upscaled.
    /// </summary>
    /// <param name="input">Raw image bytes from Jellyfin.</param>
    /// <param name="maxWidth">Maximum output width in pixels. 0 = no resize.</param>
    /// <param name="quality">Output quality (1-100).</param>
    /// <param name="format">Output format: "avif", "webp", or "jpeg".</param>
    /// <returns>Processed image bytes and the actual output format used
    /// (may differ from requested format if alpha fallback occurred).</returns>
    public (byte[] Data, string Format) Process(byte[] input, int maxWidth, int quality, string format)
    {
        if (input.Length == 0)
        {
            return (input, format);
        }

        // Safety net: AVIF (yuv420p) discards alpha transparency.
        // If the image has an alpha channel (32bpp), fall back to WebP.
        // The middleware already forces WebP for known-transparent types (Logo, Art),
        // but this catches edge cases (e.g. transparent Primary/Thumb images).
        if (format.Equals("avif", StringComparison.OrdinalIgnoreCase))
        {
            var info = Image.Identify(input);
            if (info?.PixelType.BitsPerPixel > 24)
            {
                _logger.LogDebug(
                    "[JellyShim] Alpha channel detected ({Bpp}bpp), falling back to WebP instead of AVIF",
                    info.PixelType.BitsPerPixel);
                format = "webp";
            }
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

        return (result, format);
    }

    /// <summary>
    /// Encodes an ImageSharp image to AVIF using ffmpeg's AV1 encoder.
    /// Supports both SVT-AV1 (faster, preferred) and libaom-av1 (fallback).
    ///
    /// <para><b>Pipeline:</b> ImageSharp image → save as lossless PNG temp file → ffmpeg
    /// converts to AVIF with the specified CRF quality → result is read back into the
    /// output stream. Temp files are always cleaned up (even on failure).</para>
    ///
    /// <para><b>CRF mapping:</b> User-facing quality (1–100) is mapped to ffmpeg CRF (63–0).
    /// Lower CRF = higher quality. Formula: <c>CRF = 63 * (100 - quality) / 100</c>.</para>
    ///
    /// <para><b>Encoder-specific arguments:</b></para>
    /// <list type="bullet">
    ///   <item>libsvtav1: <c>-preset 6 -svtav1-params tune=0</c> (fast, good quality for stills)</item>
    ///   <item>libaom-av1: <c>-still-picture 1 -cpu-used 6</c> (still-image optimized mode)</item>
    /// </list>
    ///
    /// <para><b>Fallback:</b> If ffmpeg is unavailable, times out (30s), or returns a
    /// non-zero exit code, the image is encoded as WebP instead.</para>
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
            // Clamp defensively — callers should already validate, but Process() is public
            var crf = (int)(63.0 * (100 - Math.Clamp(quality, 1, 100)) / 100.0);

            // Build encoder-specific arguments:
            //   SVT-AV1:  fast software encoder, preset 6 balances speed/quality for stills
            //   libaom:   reference encoder with dedicated still-picture mode
            var encoderArgs = _av1Encoder == "libsvtav1"
                ? $"-c:v libsvtav1 -crf {crf} -preset 6 -pix_fmt yuv420p -svtav1-params tune=0"
                : $"-c:v libaom-av1 -still-picture 1 -crf {crf} -cpu-used 6 -pix_fmt yuv420p";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -y -i \"{inputPath}\" {encoderArgs} \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(true); } catch { }
                _logger.LogWarning("[JellyShim] ffmpeg AVIF encoding timed out, falling back to WebP");
                image.Save(output, new WebpEncoder { Quality = quality, Method = WebpEncodingMethod.BestQuality });
                return;
            }

            var stderr = stderrTask.GetAwaiter().GetResult();

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
    /// Probes whether the found ffmpeg binary includes an AV1 encoder for AVIF encoding.
    /// Checks for both libsvtav1 (faster, preferred) and libaom-av1 (fallback).
    /// Runs <c>ffmpeg -encoders</c> and searches the output.
    /// Result is cached via <see cref="Lazy{T}"/> so the probe runs exactly once,
    /// on the first access to <see cref="IsAvifSupported"/>.
    /// </summary>
    private bool ProbeAvifSupport()
    {
        _ffmpegPath = FindFfmpegPath();
        if (_ffmpegPath is null)
        {
            _logger.LogWarning("[JellyShim] ffmpeg not found — AVIF encoding disabled, using WebP/JPEG. "
                + "Checked: Jellyfin config, JELLYFIN_FFMPEG env, common system paths");
            return false;
        }

        _logger.LogInformation("[JellyShim] Found ffmpeg at: {Path} (source: {Source})", _ffmpegPath, _ffmpegSource);

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
            if (!process.WaitForExit(5_000))
            {
                try { process.Kill(true); } catch { }
            }

            // Prefer SVT-AV1 (faster encoding) over libaom-av1 (reference but slower).
            // Both produce valid AVIF output — SVT-AV1 is 5-10x faster for similar quality.
            if (stdout.Contains("libsvtav1", StringComparison.OrdinalIgnoreCase))
            {
                _av1Encoder = "libsvtav1";
                _logger.LogInformation("[JellyShim] AVIF encoding enabled via SVT-AV1 (fast encoder)");
                return true;
            }

            if (stdout.Contains("libaom-av1", StringComparison.OrdinalIgnoreCase))
            {
                _av1Encoder = "libaom-av1";
                _logger.LogInformation("[JellyShim] AVIF encoding enabled via libaom-av1 (reference encoder)");
                return true;
            }

            _logger.LogWarning("[JellyShim] ffmpeg found but no AV1 encoder available (need libsvtav1 or libaom-av1) — AVIF disabled");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyShim] Failed to probe ffmpeg for AVIF support");
            return false;
        }
    }

    /// <summary>
    /// Finds the ffmpeg binary on the system using a priority cascade:
    ///   1. Jellyfin's own configured path (Dashboard → Playback → Transcoding → FFmpeg path)
    ///   2. JELLYFIN_FFMPEG environment variable (always set in Docker images)
    ///   3. Common system paths: jellyfin-ffmpeg package paths, then system-wide ffmpeg
    /// Each candidate is verified by running <c>ffmpeg -version</c> before accepting it.
    /// </summary>
    private string? FindFfmpegPath()
    {
        var candidates = new List<(string Path, string Source)>();

        // 1. Jellyfin's configured ffmpeg path (Dashboard → Playback → Transcoding)
        //    This is the most reliable source on Debian/Ubuntu installs where the user
        //    has explicitly set the path in Jellyfin's admin panel.
        if (_configManager is not null)
        {
            try
            {
                var encodingConfig = _configManager.GetConfiguration("encoding") as EncodingOptions;
                var configuredPath = encodingConfig?.EncoderAppPath;
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    candidates.Add((configuredPath, "Jellyfin configuration"));
                    _logger.LogDebug("[JellyShim] Found ffmpeg path in Jellyfin config: {Path}", configuredPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[JellyShim] Could not read Jellyfin encoding configuration");
            }
        }

        // 2. JELLYFIN_FFMPEG environment variable (always set in Docker images)
        var envFfmpeg = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG");
        if (!string.IsNullOrWhiteSpace(envFfmpeg))
        {
            candidates.Add((envFfmpeg, "JELLYFIN_FFMPEG env"));
        }

        // 3. Common system paths (Jellyfin-bundled and system-wide)
        //    The jellyfin-ffmpeg Debian/Ubuntu package installs to /usr/lib/jellyfin-ffmpeg/
        //    with version-suffixed binaries (ffmpeg6, ffmpeg7) alongside an unversioned symlink.
        candidates.AddRange(new[]
        {
            ("/usr/lib/jellyfin-ffmpeg/ffmpeg", "jellyfin-ffmpeg package"),
            ("/usr/lib/jellyfin-ffmpeg/ffmpeg7", "jellyfin-ffmpeg7 package"),
            ("/usr/lib/jellyfin-ffmpeg/ffmpeg6", "jellyfin-ffmpeg6 package"),
            ("/usr/bin/ffmpeg", "system ffmpeg"),
            ("ffmpeg", "PATH lookup")
        });

        foreach (var (candidate, source) in candidates)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                process.Start();
                if (!process.WaitForExit(5_000))
                {
                    try { process.Kill(true); } catch { }
                    continue;
                }

                if (process.ExitCode == 0)
                {
                    _ffmpegSource = source;
                    return candidate;
                }
            }
            catch
            {
                // Not found at this path, try next candidate
            }
        }

        return null;
    }
}
