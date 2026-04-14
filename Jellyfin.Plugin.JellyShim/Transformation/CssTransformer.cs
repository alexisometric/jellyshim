using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NUglify;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Applies CSS minification using the NUglify library, and injects
/// <c>font-display: swap</c> into <c>@font-face</c> rules that lack it.
///
/// <para><b>Font-display injection:</b> Adding <c>font-display: swap</c> prevents
/// FOIT (Flash of Invisible Text) during font loading — the browser renders text
/// immediately with a fallback font, then swaps in the custom font once loaded.
/// This is a Lighthouse best practice and improves perceived performance.
/// Only <c>@font-face</c> blocks that don't already contain <c>font-display</c>
/// are modified.</para>
///
/// <para><b>Error handling:</b> Same as <see cref="JsTransformer"/> — NUglify's output
/// is used if it's smaller, even with non-fatal errors.</para>
/// </summary>
public class CssTransformer
{
    // Matches @font-face blocks that do NOT already contain font-display
    private static readonly Regex FontFaceWithoutDisplayRegex = new(
        @"(@font-face\s*\{)((?:(?!font-display)[^}])*)\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

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
            // NUglify often still produces valid output for non-fatal errors
            // (e.g. invalid unicode-range tokens, unusual syntax). Use it if smaller.
            if (!string.IsNullOrEmpty(result.Code) && result.Code.Length < input.Length)
            {
                foreach (var error in result.Errors)
                {
                    _logger.LogDebug("[JellyShim] CSS minification warning (non-fatal): {Error}", error.Message);
                }

                return InjectFontDisplaySwap(result.Code);
            }

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

        // Inject font-display:swap into @font-face rules that lack it
        var output = InjectFontDisplaySwap(result.Code);

        return output;
    }

    /// <summary>
    /// Injects <c>font-display:swap</c> into every <c>@font-face</c> block that doesn't already have it.
    /// This prevents FOIT (Flash of Invisible Text) — text renders immediately with a fallback font,
    /// then swaps in the custom font once loaded.
    /// </summary>
    public string InjectFontDisplaySwap(string css)
    {
        if (string.IsNullOrEmpty(css) || !css.Contains("@font-face", StringComparison.OrdinalIgnoreCase))
        {
            return css;
        }

        var injected = FontFaceWithoutDisplayRegex.Replace(css, match =>
        {
            var prefix = match.Groups[1].Value;  // "@font-face{"
            var body = match.Groups[2].Value;     // properties inside
            return $"{prefix}{body}font-display:swap}}";
        });

        if (injected != css)
        {
            var count = FontFaceWithoutDisplayRegex.Matches(css).Count;
            _logger.LogDebug("[JellyShim] Injected font-display:swap into {Count} @font-face rule(s)", count);
        }

        return injected;
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
