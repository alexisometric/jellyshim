using System;
using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.JellyShim.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyShim.Transformation;

/// <summary>
/// Reflection-based bridge to IAmParadox27's File Transformation plugin.
/// Communicates via reflection across AssemblyLoadContexts — no hard DLL dependency.
/// </summary>
public class FileTransformationBridge
{
    private static readonly Guid TransformationId = new("b2c3d4e5-f6a7-8901-bcde-f12345678901");

    private readonly ILogger<FileTransformationBridge> _logger;
    private readonly HtmlTransformer _htmlTransformer;
    private bool _registered;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationBridge"/> class.
    /// </summary>
    public FileTransformationBridge(
        ILogger<FileTransformationBridge> logger,
        HtmlTransformer htmlTransformer)
    {
        _logger = logger;
        _htmlTransformer = htmlTransformer;
    }

    /// <summary>
    /// Gets a value indicating whether the File Transformation plugin was detected and registration succeeded.
    /// </summary>
    public bool IsRegistered => _registered;

    /// <summary>
    /// Attempts to detect the File Transformation plugin and register our transformations.
    /// </summary>
    public bool TryRegister(PluginConfiguration config)
    {
        if (!config.EnableFileTransformationIntegration)
        {
            _logger.LogInformation("[JellyShim] File Transformation integration disabled in config");
            return false;
        }

        try
        {
            var ftAssembly = FindFileTransformationAssembly();
            if (ftAssembly is null)
            {
                _logger.LogInformation(
                    "[JellyShim] File Transformation plugin not found — operating in standalone mode " +
                    "(pre-optimization + middleware only)");
                return false;
            }

            var pluginInterfaceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterfaceType is null)
            {
                _logger.LogWarning("[JellyShim] File Transformation assembly found but PluginInterface type missing");
                return false;
            }

            var registerMethod = pluginInterfaceType.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod is null)
            {
                _logger.LogWarning("[JellyShim] RegisterTransformation method not found on PluginInterface");
                return false;
            }

            // Register HTML transformation
            RegisterHtmlTransformation(registerMethod, config);

            _registered = true;
            _logger.LogInformation("[JellyShim] Successfully registered with File Transformation plugin");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JellyShim] Failed to register with File Transformation plugin — continuing in standalone mode");
            return false;
        }
    }

    /// <summary>
    /// Attempts to unregister our transformations from the File Transformation plugin.
    /// </summary>
    public void TryUnregister()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var ftAssembly = FindFileTransformationAssembly();
            if (ftAssembly is null)
            {
                return;
            }

            var pluginInterfaceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            // File Transformation v2.5+ supports RemoveTransformation via the write service
            // but the PluginInterface only exposes RegisterTransformation.
            // We'll log and let the server restart handle cleanup.
            _logger.LogInformation("[JellyShim] File Transformation unregistration will take effect on next restart");
            _registered = false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[JellyShim] Error during File Transformation unregistration");
        }
    }

    private void RegisterHtmlTransformation(MethodInfo registerMethod, PluginConfiguration config)
    {
        // Build JObject payload via reflection — avoids hard Newtonsoft.Json dependency
        var jObjectType = FindNewtonsoftType("Newtonsoft.Json.Linq.JObject");
        if (jObjectType is null)
        {
            _logger.LogWarning("[JellyShim] Newtonsoft.Json.Linq.JObject not found — cannot register with File Transformation");
            return;
        }

        var payload = Activator.CreateInstance(jObjectType)!;
        var indexer = jObjectType.GetProperty("Item", new[] { typeof(string) });
        if (indexer is null)
        {
            _logger.LogWarning("[JellyShim] JObject indexer not found");
            return;
        }

        // Create JValue instances via reflection
        var jValueType = FindNewtonsoftType("Newtonsoft.Json.Linq.JValue");
        if (jValueType is null)
        {
            _logger.LogWarning("[JellyShim] JValue type not found");
            return;
        }

        void SetProperty(string key, string value)
        {
            var jValue = Activator.CreateInstance(jValueType, [value]);
            indexer.SetValue(payload, jValue, [key]);
        }

        SetProperty("id", TransformationId.ToString());
        SetProperty("fileNamePattern", @"^index\.html$");
        SetProperty("callbackAssembly", GetType().Assembly.FullName!);
        SetProperty("callbackClass", typeof(FileTransformationCallback).FullName!);
        SetProperty("callbackMethod", nameof(FileTransformationCallback.TransformHtml));

        registerMethod.Invoke(null, [payload]);
        _logger.LogDebug("[JellyShim] Registered HTML transformation with pattern: ^index\\.html$");
    }

    private static Type? FindNewtonsoftType(string fullName)
    {
        return AssemblyLoadContext.All
            .SelectMany(ctx => ctx.Assemblies)
            .Where(a => a.FullName?.Contains("Newtonsoft.Json", StringComparison.Ordinal) ?? false)
            .Select(a => a.GetType(fullName))
            .FirstOrDefault(t => t is not null);
    }

    private static Assembly? FindFileTransformationAssembly()
    {
        return AssemblyLoadContext.All
            .SelectMany(ctx => ctx.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
    }
}

/// <summary>
/// Static callback class invoked by File Transformation plugin via reflection.
/// </summary>
public static class FileTransformationCallback
{
    /// <summary>
    /// Callback invoked by File Transformation when index.html is requested.
    /// Receives a JObject with "contents" property — accessed via reflection.
    /// </summary>
    public static void TransformHtml(object payload)
    {
        try
        {
            // Access JObject properties via reflection (payload is Newtonsoft.Json.Linq.JObject)
            var payloadType = payload.GetType();
            var indexer = payloadType.GetProperty("Item", new[] { typeof(string) });
            if (indexer is null)
            {
                return;
            }

            var contentsToken = indexer.GetValue(payload, ["contents"]);
            var contents = contentsToken?.ToString();
            if (string.IsNullOrEmpty(contents))
            {
                return;
            }

            var plugin = Plugin.Instance;
            if (plugin is null)
            {
                return;
            }

            var config = plugin.Configuration;
            var context = AngleSharp.BrowsingContext.New(AngleSharp.Configuration.Default);
            var parser = context.GetService<AngleSharp.Html.Parser.IHtmlParser>();
            if (parser is null)
            {
                return;
            }

            var document = parser.ParseDocument(contents);
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
                StripSri(document);
            }

            if (modified)
            {
                using var writer = new StringWriter();
                document.ToHtml(writer, new AngleSharp.Html.HtmlMarkupFormatter());

                // Write back via reflection: payload["contents"] = transformedHtml
                var jValueType = contentsToken?.GetType()
                    ?? payloadType.Assembly.GetType("Newtonsoft.Json.Linq.JValue");
                if (jValueType is not null)
                {
                    var jValue = Activator.CreateInstance(jValueType, [writer.ToString()]);
                    indexer.SetValue(payload, jValue, ["contents"]);
                }
            }
        }
        catch
        {
            // Swallow errors in callback — don't break File Transformation pipeline
        }
    }

    private static bool InjectModulePreloads(AngleSharp.Dom.IDocument document)
    {
        var head = document.Head;
        if (head is null)
        {
            return false;
        }

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

            if (!src.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                !src.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var preloadLink = document.CreateElement("link");
            preloadLink.SetAttribute("rel", "modulepreload");
            preloadLink.SetAttribute("href", src);

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
        }

        return injected;
    }

    private static bool AddScriptDefer(AngleSharp.Dom.IDocument document)
    {
        var scripts = document.QuerySelectorAll("script[src]");
        var modified = false;

        foreach (var script in scripts)
        {
            if (script.HasAttribute("defer") ||
                script.HasAttribute("async") ||
                string.Equals(script.GetAttribute("type"), "module", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            script.SetAttribute("defer", string.Empty);
            modified = true;
        }

        return modified;
    }

    private static void StripSri(AngleSharp.Dom.IDocument document)
    {
        var elements = document.QuerySelectorAll("[integrity]");
        foreach (var element in elements)
        {
            element.RemoveAttribute("integrity");
            if (element.HasAttribute("crossorigin"))
            {
                element.RemoveAttribute("crossorigin");
            }
        }
    }

    private static bool InjectPreconnectHints(AngleSharp.Dom.IDocument document, Configuration.PluginConfiguration config)
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

            var preconnect = document.CreateElement("link");
            preconnect.SetAttribute("rel", "preconnect");
            preconnect.SetAttribute("href", origin);
            preconnect.SetAttribute("crossorigin", string.Empty);

            if (firstChild is not null)
            {
                head.InsertBefore(preconnect, firstChild);
            }
            else
            {
                head.AppendChild(preconnect);
            }

            existingOrigins.Add(origin);
            injected = true;
        }

        return injected;
    }
}
