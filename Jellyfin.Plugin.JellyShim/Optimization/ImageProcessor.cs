using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaSharp;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Native image processor using ImageSharp — resizes, re-encodes, and compresses images
/// without any external service dependency.
/// </summary>
public class ImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly Lazy<bool> _avifSupported;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessor"/> class.
    /// </summary>
    public ImageProcessor(ILogger<ImageProcessor> logger)
    {
        _logger = logger;
        _avifSupported = new Lazy<bool>(ProbeAvifSupport);
    }

    /// <summary>
    /// Gets a value indicating whether the runtime SkiaSharp build supports AVIF encoding.
    /// Probed once on first access via a 1×1 test encode.
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
    /// Encodes an ImageSharp image to AVIF using SkiaSharp (provided at runtime by Jellyfin).
    /// Falls back to WebP if the runtime Skia build lacks AVIF codec support.
    /// </summary>
    private void EncodeAvif(Image image, MemoryStream output, int quality)
    {
        var width = image.Width;
        var height = image.Height;
        var pixelData = new byte[width * height * 4];
        using (var rgba = image.CloneAs<Rgba32>())
        {
            rgba.CopyPixelDataTo(pixelData);
        }

        var skInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var skBitmap = new SKBitmap(skInfo);

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixelData, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            skBitmap.SetPixels(handle.AddrOfPinnedObject());

            using var skImage = SKImage.FromBitmap(skBitmap);
            using var avifData = skImage.Encode(SKEncodedImageFormat.Avif, quality);

            if (avifData is not null && avifData.Size > 0)
            {
                avifData.SaveTo(output);
                return;
            }
        }
        finally
        {
            handle.Free();
        }

        // Fallback to WebP if AVIF encoding is not supported by the runtime Skia build
        _logger.LogWarning("[JellyShim] AVIF encoding not supported by runtime, falling back to WebP");
        image.Save(output, new WebpEncoder
        {
            Quality = quality,
            Method = WebpEncodingMethod.BestQuality
        });
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
    /// Probes whether the runtime SkiaSharp build can encode AVIF by attempting a 1×1 test encode.
    /// </summary>
    private bool ProbeAvifSupport()
    {
        try
        {
            using var bitmap = new SKBitmap(new SKImageInfo(1, 1));
            using var img = SKImage.FromBitmap(bitmap);
            using var data = img.Encode(SKEncodedImageFormat.Avif, 80);
            var supported = data is not null && data.Size > 0;
            _logger.LogInformation("[JellyShim] AVIF encoding support: {Supported}", supported);
            return supported;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[JellyShim] AVIF encoding not available: {Message}", ex.Message);
            return false;
        }
    }
}
