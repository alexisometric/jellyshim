using System.Text;
using Microsoft.Extensions.Logging;
using NUglify;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Applies additional minification to JavaScript files using NUglify.
/// </summary>
public class JsTransformer
{
    private readonly ILogger<JsTransformer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsTransformer"/> class.
    /// </summary>
    public JsTransformer(ILogger<JsTransformer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Minifies JavaScript content. Returns original content if minification fails.
    /// </summary>
    public string Minify(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        // Skip if already heavily minified (heuristic: very low whitespace ratio)
        if (IsAlreadyMinified(input))
        {
            _logger.LogDebug("[JellyShim] JS already minified, skipping");
            return input;
        }

        var result = Uglify.Js(input);
        if (result.HasErrors)
        {
            foreach (var error in result.Errors)
            {
                _logger.LogWarning("[JellyShim] JS minification error: {Error}", error.Message);
            }

            return input;
        }

        var saved = input.Length - result.Code.Length;
        if (saved > 0)
        {
            _logger.LogDebug("[JellyShim] JS minified: {Original} → {Minified} (saved {Saved} bytes)",
                input.Length, result.Code.Length, saved);
        }

        return result.Code;
    }

    /// <summary>
    /// Minifies JavaScript content in a byte array.
    /// </summary>
    public byte[] MinifyBytes(byte[] input)
    {
        var text = Encoding.UTF8.GetString(input);
        var minified = Minify(text);
        return Encoding.UTF8.GetBytes(minified);
    }

    private static bool IsAlreadyMinified(string content)
    {
        if (content.Length < 500)
        {
            return false;
        }

        // Sample the first 2000 chars: if newlines are < 1% of content, it's already minified
        var sample = content.Length > 2000 ? content[..2000] : content;
        var newLines = 0;
        foreach (var c in sample)
        {
            if (c == '\n')
            {
                newLines++;
            }
        }

        return (double)newLines / sample.Length < 0.01;
    }
}
