using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Minifies SVG content by removing comments, metadata, editor-specific attributes,
/// unnecessary whitespace, and XML declarations. Uses regex-based cleanup rather than
/// a full XML parser for performance and resilience against malformed SVG.
/// </summary>
public partial class SvgTransformer
{
    private readonly ILogger<SvgTransformer> _logger;

    /// <summary>Initializes a new instance of the <see cref="SvgTransformer"/> class.</summary>
    public SvgTransformer(ILogger<SvgTransformer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Minifies SVG content from raw bytes. Returns the original bytes if minification fails.
    /// </summary>
    public byte[] MinifyBytes(byte[] input)
    {
        try
        {
            var svg = Encoding.UTF8.GetString(input);
            var minified = Minify(svg);
            var result = Encoding.UTF8.GetBytes(minified);

            if (result.Length < input.Length)
            {
                return result;
            }

            // Minification didn't reduce size — return original
            return input;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyShim] SVG minification failed, returning original");
            return input;
        }
    }

    /// <summary>
    /// Minifies an SVG string by removing unnecessary content.
    /// </summary>
    public static string Minify(string svg)
    {
        // Remove XML comments
        svg = XmlCommentRegex().Replace(svg, string.Empty);

        // Remove XML declaration (<?xml ... ?>)
        svg = XmlDeclarationRegex().Replace(svg, string.Empty);

        // Remove <!DOCTYPE ...>
        svg = DoctypeRegex().Replace(svg, string.Empty);

        // Remove metadata elements
        svg = MetadataRegex().Replace(svg, string.Empty);

        // Remove editor-specific elements (Inkscape, Illustrator, Sketch)
        svg = SodipodiRegex().Replace(svg, string.Empty);

        // Remove editor-specific attributes (inkscape:*, sodipodi:*, sketch:*, data-name)
        svg = EditorAttributeRegex().Replace(svg, string.Empty);

        // Remove empty id="" attributes only (non-empty ids may be referenced by CSS/use)
        svg = EmptyIdRegex().Replace(svg, string.Empty);

        // Collapse whitespace between tags
        svg = InterTagWhitespaceRegex().Replace(svg, "><");

        // Collapse multiple whitespace within tags to single space
        svg = MultiWhitespaceRegex().Replace(svg, " ");

        // Remove leading/trailing whitespace
        svg = svg.Trim();

        return svg;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlCommentRegex();

    [GeneratedRegex(@"<\?xml[^?]*\?>", RegexOptions.IgnoreCase)]
    private static partial Regex XmlDeclarationRegex();

    [GeneratedRegex(@"<!DOCTYPE[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DoctypeRegex();

    [GeneratedRegex(@"<metadata[\s>].*?</metadata>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MetadataRegex();

    [GeneratedRegex(@"<sodipodi:[^>]*(?:/>|>.*?</sodipodi:[^>]*>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SodipodiRegex();

    [GeneratedRegex(@"\s(?:inkscape|sodipodi|sketch|data-name):[a-zA-Z\-]+=(?:""[^""]*""|'[^']*')", RegexOptions.IgnoreCase)]
    private static partial Regex EditorAttributeRegex();

    [GeneratedRegex(@"\s+id=""""(?=[\s/>])", RegexOptions.None)]
    private static partial Regex EmptyIdRegex();

    [GeneratedRegex(@">\s+<")]
    private static partial Regex InterTagWhitespaceRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiWhitespaceRegex();
}
