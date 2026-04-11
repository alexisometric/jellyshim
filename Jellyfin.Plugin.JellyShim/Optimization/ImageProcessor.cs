using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Native image processor using ImageSharp — resizes, re-encodes, and compresses images
/// without any external service dependency.
/// </summary>
public class ImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProcessor"/> class.
    /// </summary>
    public ImageProcessor(ILogger<ImageProcessor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Processes an image: resize to max width (preserving aspect ratio) and re-encode.
    /// Only downscales — images already smaller than maxWidth are not upscaled.
    /// </summary>
    /// <param name="input">Raw image bytes from Jellyfin.</param>
    /// <param name="maxWidth">Maximum output width in pixels. 0 = no resize.</param>
    /// <param name="quality">Output quality (1-100).</param>
    /// <param name="format">Output format: "webp" or "jpeg".</param>
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
    /// Returns the MIME content type for the given format.
    /// </summary>
    public static string GetContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "webp" => "image/webp",
            _ => "image/jpeg"
        };
    }
}
