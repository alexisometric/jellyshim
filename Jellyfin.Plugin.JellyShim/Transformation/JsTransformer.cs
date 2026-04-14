using System.Text;
using Microsoft.Extensions.Logging;
using NUglify;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Applies JavaScript minification using the NUglify library.
///
/// <para><b>Already-minified detection:</b> Files with a very low whitespace ratio
/// (heuristic for bundler-minified code) are skipped to avoid wasting CPU on
/// content that can't be compressed further.</para>
///
/// <para><b>Error handling:</b> NUglify often produces valid (smaller) output even
/// when it reports non-fatal errors (e.g. strict-mode duplicate properties,
/// unusual ES2020+ syntax). If the output is smaller than the input, it's used
/// despite warnings. Only when the output is empty or larger is the original returned.</para>
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
            // NUglify often still produces valid output for non-fatal errors
            // (e.g. strict-mode duplicate properties, unusual syntax). Use it if smaller.
            if (!string.IsNullOrEmpty(result.Code) && result.Code.Length < input.Length)
            {
                foreach (var error in result.Errors)
                {
                    _logger.LogDebug("[JellyShim] JS minification warning (non-fatal): {Error}", error.Message);
                }

                return result.Code;
            }

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
