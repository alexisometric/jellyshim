using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using Jellyfin.Plugin.JellyShim.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Optimizes HTML files using AngleSharp DOM manipulation:
/// modulepreload injection, script defer, SRI stripping, and optional critical CSS.
/// </summary>
public class HtmlTransformer
{
    private readonly ILogger<HtmlTransformer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HtmlTransformer"/> class.
    /// </summary>
    public HtmlTransformer(ILogger<HtmlTransformer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies all HTML optimizations.
    /// </summary>
    public string Transform(string html, PluginConfiguration config)
    {
        var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        var parser = context.GetService<IHtmlParser>();
        if (parser is null)
        {
            _logger.LogWarning("[JellyShim] Could not create HTML parser");
            return html;
        }

        var document = parser.ParseDocument(html);
        var modified = false;

        if (config.EnablePreloadInjection)
        {
            modified |= InjectModulePreloads(document);
        }

        if (config.EnableScriptDefer)
        {
            modified |= AddScriptDefer(document);
        }

        if (config.EnablePreconnectHints)
        {
            modified |= InjectPreconnectHints(document, config);
        }

        if (config.StripSriOnModification && modified)
        {
            StripSriAttributes(document);
        }

        if (!modified)
        {
            return html;
        }

        using var writer = new StringWriter();
        document.ToHtml(writer, new MinifyMarkupFormatter());
        var result = writer.ToString();

        _logger.LogDebug("[JellyShim] HTML optimized: {Original} → {Optimized} chars", html.Length, result.Length);
        return result;
    }

    /// <summary>
    /// Transforms HTML content from a byte array.
    /// </summary>
    public byte[] TransformBytes(byte[] input, PluginConfiguration config)
    {
        var html = Encoding.UTF8.GetString(input);
        var transformed = Transform(html, config);
        return Encoding.UTF8.GetBytes(transformed);
    }

    /// <summary>
    /// Callback-compatible transform for File Transformation integration.
    /// Reads from stream, transforms, writes back.
    /// </summary>
    public void TransformStream(string path, Stream stream, PluginConfiguration config)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true);
        var html = reader.ReadToEnd();

        var transformed = Transform(html, config);

        stream.Seek(0, SeekOrigin.Begin);
        stream.SetLength(0);
        using var writer = new StreamWriter(stream, Encoding.UTF8, bufferSize: -1, leaveOpen: true);
        writer.Write(transformed);
        writer.Flush();
    }

    private bool InjectModulePreloads(IDocument document)
    {
        var head = document.Head;
        if (head is null)
        {
            return false;
        }

        // Find all module scripts that could benefit from preloading
        var scripts = document.QuerySelectorAll("script[type='module'][src], script[src$='.js']");
        var existingPreloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in document.QuerySelectorAll("link[rel='modulepreload']"))
        {
            var href = link.GetAttribute("href");
            if (href is not null)
            {
                existingPreloads.Add(href);
            }
        }

        var injected = false;
        foreach (var script in scripts)
        {
            var src = script.GetAttribute("src");
            if (src is null || existingPreloads.Contains(src))
            {
                continue;
            }

            // Only preload JS bundles (skip inline or tiny scripts)
            if (!src.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                !src.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var preloadLink = document.CreateElement("link");
            preloadLink.SetAttribute("rel", "modulepreload");
            preloadLink.SetAttribute("href", src);

            // Insert preload before the first script in head, or append to head
            var firstScript = head.QuerySelector("script");
            if (firstScript is not null)
            {
                head.InsertBefore(preloadLink, firstScript);
            }
            else
            {
                head.AppendChild(preloadLink);
            }

            existingPreloads.Add(src);
            injected = true;
            _logger.LogDebug("[JellyShim] Injected modulepreload for {Src}", src);
        }

        return injected;
    }

    private bool AddScriptDefer(IDocument document)
    {
        var scripts = document.QuerySelectorAll("script[src]");
        var modified = false;

        foreach (var script in scripts)
        {
            // Skip if already has defer, async, or is type=module (modules are deferred by default)
            if (script.HasAttribute("defer") ||
                script.HasAttribute("async") ||
                string.Equals(script.GetAttribute("type"), "module", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            script.SetAttribute("defer", string.Empty);
            modified = true;
            _logger.LogDebug("[JellyShim] Added defer to script: {Src}", script.GetAttribute("src"));
        }

        return modified;
    }

    private void StripSriAttributes(IDocument document)
    {
        // Remove integrity attributes from scripts and links whose content may have been modified
        var elements = document.QuerySelectorAll("[integrity]");
        foreach (var element in elements)
        {
            element.RemoveAttribute("integrity");
            // Also remove crossorigin if it was only there for SRI
            if (element.HasAttribute("crossorigin"))
            {
                element.RemoveAttribute("crossorigin");
            }

            _logger.LogDebug("[JellyShim] Stripped SRI from {Tag} {Src}",
                element.TagName,
                element.GetAttribute("src") ?? element.GetAttribute("href") ?? "?");
        }
    }

    private bool InjectPreconnectHints(IDocument document, PluginConfiguration config)
    {
        var head = document.Head;
        if (head is null || string.IsNullOrWhiteSpace(config.PreconnectOrigins))
        {
            return false;
        }

        var origins = config.PreconnectOrigins.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (origins.Length == 0)
        {
            return false;
        }

        // Collect existing preconnect origins
        var existingOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in document.QuerySelectorAll("link[rel='preconnect']"))
        {
            var href = link.GetAttribute("href");
            if (href is not null)
            {
                existingOrigins.Add(href);
            }
        }

        var injected = false;
        var firstChild = head.FirstChild;

        foreach (var origin in origins)
        {
            if (existingOrigins.Contains(origin))
            {
                continue;
            }

            // dns-prefetch as fallback for browsers that lack preconnect support
            var dnsPrefetch = document.CreateElement("link");
            dnsPrefetch.SetAttribute("rel", "dns-prefetch");
            dnsPrefetch.SetAttribute("href", origin);

            var preconnect = document.CreateElement("link");
            preconnect.SetAttribute("rel", "preconnect");
            preconnect.SetAttribute("href", origin);
            preconnect.SetAttribute("crossorigin", string.Empty);

            // Insert preconnect + dns-prefetch as early as possible in <head>
            if (firstChild is not null)
            {
                head.InsertBefore(dnsPrefetch, firstChild);
                head.InsertBefore(preconnect, firstChild);
            }
            else
            {
                head.AppendChild(dnsPrefetch);
                head.AppendChild(preconnect);
            }

            existingOrigins.Add(origin);
            injected = true;
            _logger.LogDebug("[JellyShim] Injected preconnect + dns-prefetch for {Origin}", origin);
        }

        return injected;
    }

    /// <summary>
    /// Minimal HTML formatter that strips unnecessary whitespace.
    /// </summary>
    private sealed class MinifyMarkupFormatter : HtmlMarkupFormatter
    {
        /// <inheritdoc />
        public override string Text(ICharacterData text)
        {
            var content = text.Data;
            // Preserve content in <script>, <style>, <pre>, <textarea>
            var parent = text.ParentElement?.TagName;
            if (parent is not null &&
                (parent.Equals("SCRIPT", StringComparison.OrdinalIgnoreCase) ||
                 parent.Equals("STYLE", StringComparison.OrdinalIgnoreCase) ||
                 parent.Equals("PRE", StringComparison.OrdinalIgnoreCase) ||
                 parent.Equals("TEXTAREA", StringComparison.OrdinalIgnoreCase)))
            {
                return content;
            }

            // Collapse whitespace
            return CollapseWhitespace(content);
        }

        private static string CollapseWhitespace(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            var sb = new StringBuilder(input.Length);
            var lastWasWhitespace = false;
            foreach (var c in input)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasWhitespace)
                    {
                        sb.Append(' ');
                        lastWasWhitespace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    lastWasWhitespace = false;
                }
            }

            return sb.ToString();
        }
    }
}
