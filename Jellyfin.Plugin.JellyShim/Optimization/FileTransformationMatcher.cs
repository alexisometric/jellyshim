using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyShim.Optimization;

/// <summary>
/// Determines whether a file path matches the File Transformation bypass patterns.
/// Shared by both <see cref="AssetProcessor"/> (scheduled pre-optimization) and
/// the AssetOptimizationMiddleware (live request handling) to keep matching logic
/// consistent and avoid duplication.
///
/// <para><b>Thread safety:</b> Each consumer creates its own instance, so the
/// cached pattern state is not shared across threads.</para>
/// </summary>
public class FileTransformationMatcher
{
    private string? _cachedPatternsRaw;
    private Regex[]? _cachedRegexes;

    /// <summary>
    /// Returns <c>true</c> if the given relative file path matches a configured
    /// File Transformation bypass pattern, or is a webpack chunk/bundle file.
    /// </summary>
    /// <param name="relativePath">Web-relative file path (e.g. "main.jellyfin.bundle.js").</param>
    /// <param name="patterns">Newline-separated bypass patterns from plugin configuration.</param>
    public bool IsMatch(string relativePath, string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            return false;
        }

        var fileName = Path.GetFileName(relativePath);

        // When FT plugins are in use, ALL webpack chunk/bundle files are potentially
        // patched at runtime — these must never be served from a pre-built cache.
        if (fileName.EndsWith(".chunk.js", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".bundle.js", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Recompile regexes only when the config string changes
        if (_cachedPatternsRaw != patterns)
        {
            var parsed = patterns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _cachedRegexes = parsed.Select(p =>
            {
                var regexPattern = "^" + Regex.Escape(p).Replace("\\*", ".*") + "$";
                return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }).ToArray();
            _cachedPatternsRaw = patterns;
        }

        foreach (var regex in _cachedRegexes!)
        {
            if (regex.IsMatch(fileName))
            {
                return true;
            }
        }

        return false;
    }
}
