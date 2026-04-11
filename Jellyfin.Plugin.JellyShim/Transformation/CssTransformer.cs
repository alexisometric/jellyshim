using System.Text;
using Microsoft.Extensions.Logging;
using NUglify;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Applies additional minification to CSS files using NUglify.
/// </summary>
public class CssTransformer
{
    private readonly ILogger<CssTransformer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CssTransformer"/> class.
    /// </summary>
    public CssTransformer(ILogger<CssTransformer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Minifies CSS content. Returns original content if minification fails.
    /// </summary>
    public string Minify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var result = Uglify.Css(input);
        if (result.HasErrors)
        {
            foreach (var error in result.Errors)
            {
                _logger.LogWarning("[JellyShim] CSS minification error: {Error}", error.Message);
            }

            return input;
        }

        var saved = input.Length - result.Code.Length;
        if (saved > 0)
        {
            _logger.LogDebug("[JellyShim] CSS minified: {Original} → {Minified} (saved {Saved} bytes)",
                input.Length, result.Code.Length, saved);
        }

        return result.Code;
    }

    /// <summary>
    /// Minifies CSS content in a byte array.
    /// </summary>
    public byte[] MinifyBytes(byte[] input)
    {
        var text = Encoding.UTF8.GetString(input);
        var minified = Minify(text);
        return Encoding.UTF8.GetBytes(minified);
    }
}
