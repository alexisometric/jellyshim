using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyShim.Api;

/// <summary>
/// Serves localization JSON files for the plugin config page.
/// To add a language, create Localization/{code}.json as an EmbeddedResource.
/// </summary>
[ApiController]
[Route("JellyShim/Localization")]
public class LocalizationController : ControllerBase
{
    /// <summary>
    /// Gets translation strings for the given language code.
    /// </summary>
    /// <param name="lang">Two-letter language code (e.g. en, fr, de).</param>
    /// <returns>JSON object with translation keys and values.</returns>
    [HttpGet("{lang}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetTranslation([FromRoute] string lang)
    {
        if (string.IsNullOrEmpty(lang) || lang.Length > 5 || !lang.All(char.IsLetter))
        {
            return NotFound();
        }

        var resourceName = $"Jellyfin.Plugin.JellyShim.Localization.{lang.ToLowerInvariant()}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), "application/json");
    }
}
