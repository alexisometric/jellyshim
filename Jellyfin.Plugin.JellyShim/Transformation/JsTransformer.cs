using System.Text;
using Microsoft.Extensions.Logging;
using NUglify;
using NUglify.JavaScript;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Applies JavaScript minification using the NUglify library.
///
/// <para><b>Already-minified detection:</b> Files with a very low whitespace ratio
/// (heuristic for bundler-minified code) are skipped to avoid wasting CPU on
/// content that can't be compressed further.</para>
///
/// <para><b>Safe minification:</b> Uses conservative NUglify settings that only remove
/// whitespace and comments, without renaming variables or evaluating expressions.
/// This prevents breaking third-party plugin code and webpack-bundled code that
/// relies on variable names, dynamic requires, or runtime-evaluated expressions.</para>
///
/// <para><b>Error handling:</b> NUglify often produces valid (smaller) output even
/// when it reports non-fatal errors (e.g. strict-mode duplicate properties,
/// unusual ES2020+ syntax). If the output is smaller than the input, it's used
/// despite warnings. Only when the output is empty or larger is the original returned.</para>
/// </summary>
public class JsTransformer
{
    private readonly ILogger<JsTransformer> _logger;

    private static readonly CodeSettings SafeMinifySettings = new()
    {
        // ── Variable safety ────────────────────────────────────────
        // Don't rename local variables — third-party and bundled code relies on
        // variable names being preserved (eval, Function(), cross-scope refs,
        // dynamic requires, stack traces). This was causing "X is not defined"
        // errors in plugin scripts like Announcements banner.js.
        LocalRenaming = LocalRenaming.KeepAll,

        // Keep original function names (needed for stack traces, .name property,
        // Function.prototype.toString() checks, and runtime reflection).
        PreserveFunctionNames = true,

        // Don't try to optimize around eval() — plugins may use it legitimately.
        EvalTreatment = EvalTreatment.Ignore,

        // ── Structural safety ──────────────────────────────────────
        // Don't remove code NUglify considers "dead" — it can't know if code is
        // dynamically referenced at runtime (e.g. via string-based lookups).
        RemoveUnneededCode = false,

        // Don't rename properties — breaks bracket notation, JSON, and APIs.
        RenamePairs = null,

        // Preserve important comments (/*! ... */) — license headers.
        PreserveImportantComments = true,

        // ── Safe optimizations (whitespace + literals only) ────────
        // Collapse new Object()/Array() to literals {},[]. Safe, no semantic change.
        CollapseToLiteral = true,

        // Safari quirks mode for maximum browser compatibility.
        MacSafariQuirks = true,

        // NUglify still applies these safe transforms with default MinifyCode=true:
        // - Remove comments & whitespace
        // - Shorten true→!0, false→!1, undefined→void 0
        // - Combine adjacent var statements
        // - Remove redundant semicolons
        // The real size savings come from Brotli/Zstd compression (60-80%),
        // not from variable mangling (~5-10%), so this is the right trade-off.
    };

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

        var result = Uglify.Js(input, SafeMinifySettings);
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
