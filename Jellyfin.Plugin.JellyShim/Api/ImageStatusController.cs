using Jellyfin.Plugin.JellyShim.Optimization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyShim.Api;

/// <summary>
/// REST API controller that exposes image optimization status for the admin config page.
/// Returns whether AVIF encoding is supported, the ffmpeg path in use, and the source
/// that provided the path (Jellyfin config, env var, or system path).
/// Requires administrator privileges (<c>RequiresElevation</c> policy).
/// </summary>
[ApiController]
[Route("JellyShim")]
public class ImageStatusController : ControllerBase
{
    private readonly ImageProcessor _imageProcessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageStatusController"/> class.
    /// </summary>
    public ImageStatusController(ImageProcessor imageProcessor)
    {
        _imageProcessor = imageProcessor;
    }

    /// <summary>
    /// Gets the AVIF encoding status.
    /// </summary>
    [HttpGet("ImageStatus")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetImageStatus()
    {
        return Ok(new
        {
            AvifSupported = _imageProcessor.IsAvifSupported,
            FfmpegPath = _imageProcessor.FfmpegPath,
            FfmpegSource = _imageProcessor.FfmpegSource,
            Av1Encoder = _imageProcessor.Av1Encoder
        });
    }
}
