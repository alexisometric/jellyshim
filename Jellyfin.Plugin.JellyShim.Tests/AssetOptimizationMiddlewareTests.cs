using System.IO.Compression;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Configuration;
using Jellyfin.Plugin.JellyShim.Middleware;
using Jellyfin.Plugin.JellyShim.Transformation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class AssetOptimizationMiddlewareTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiskCacheManager _cache;
    private readonly JsTransformer _jsTransformer;
    private readonly CssTransformer _cssTransformer;
    private readonly Mock<ILogger<AssetOptimizationMiddleware>> _loggerMock;

    public AssetOptimizationMiddlewareTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jellyshim-mw-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _cache = new DiskCacheManager(_tempDir, new Mock<ILogger<DiskCacheManager>>().Object);
        _jsTransformer = new JsTransformer(new Mock<ILogger<JsTransformer>>().Object);
        _cssTransformer = new CssTransformer(new Mock<ILogger<CssTransformer>>().Object);
        _loggerMock = new Mock<ILogger<AssetOptimizationMiddleware>>();
    }

    public void Dispose()
    {
        // Reset Plugin.Instance to avoid test pollution
        SetPluginInstance(null);

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private AssetOptimizationMiddleware CreateMiddleware(RequestDelegate next)
    {
        return new AssetOptimizationMiddleware(next, _cache, _jsTransformer, _cssTransformer, _loggerMock.Object);
    }

    private static DefaultHttpContext CreateHttpContext(string method, string path, string? acceptEncoding = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (acceptEncoding != null)
        {
            context.Request.Headers.AcceptEncoding = acceptEncoding;
        }
        return context;
    }

    /// <summary>
    /// Sets Plugin.Instance via reflection since the setter is private.
    /// </summary>
    private static void SetPluginInstance(Plugin? instance)
    {
        var prop = typeof(Plugin).GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
        prop!.SetValue(null, instance);
    }

    /// <summary>
    /// Creates a minimal Plugin instance for testing, using reflection to bypass the constructor.
    /// </summary>
    private static PluginConfiguration SetupPluginWithConfig(PluginConfiguration? config = null)
    {
        config ??= new PluginConfiguration();

        // Plugin constructor requires IApplicationPaths and IXmlSerializer.
        // Use RuntimeHelpers to create without calling constructor, then set Instance + Configuration.
#pragma warning disable SYSLIB0050
        var plugin = (Plugin)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Plugin));
#pragma warning restore SYSLIB0050

        // Set Instance
        SetPluginInstance(plugin);

        // Set Configuration via the base class property
        var configProp = typeof(Plugin).GetProperty("Configuration");
        configProp!.SetValue(plugin, config);

        return config;
    }

    // ── Pass-through tests ────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_NonGetRequest_PassesThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateHttpContext("POST", "/web/main.js");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task InvokeAsync_NonGetMethods_PassesThrough(string method)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateHttpContext(method, "/web/main.js");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_NullPluginInstance_PassesThrough()
    {
        SetPluginInstance(null);
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
        var context = CreateHttpContext("GET", "/web/main.js");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    // ── API path tests ────────────────────────────────────────────

    [Theory]
    [InlineData("/api/something")]
    [InlineData("/System/Info")]
    [InlineData("/Sessions/active")]
    [InlineData("/Library/VirtualFolders")]
    [InlineData("/Plugins/list")]
    [InlineData("/Items/abc123/playbackinfo")]
    [InlineData("/Users/abc123/items")]
    public async Task InvokeAsync_ApiPaths_SetsNoStoreAndPassesThrough(string path)
    {
        var config = SetupPluginWithConfig();
        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", path);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    // ── Web asset cache serving ───────────────────────────────────

    [Fact]
    public async Task InvokeAsync_WebAssetCacheHit_ServesFromCache()
    {
        var config = SetupPluginWithConfig();
        var jsContent = "var x=1;"u8.ToArray();
        _cache.Store("main.abcdef12.js", "raw", jsContent);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext("GET", "/web/main.abcdef12.js", "br, gzip");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/javascript; charset=utf-8", context.Response.ContentType);

        // Verify ETag is set
        Assert.True(context.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task InvokeAsync_WebAssetCacheHit_Returns304_OnEtagMatch()
    {
        var config = SetupPluginWithConfig();
        var jsContent = "var x=1;"u8.ToArray();
        _cache.Store("etag304.js", "raw", jsContent);
        _cache.TryGetCachedFile("etag304.js", "raw", out var cachedPath);
        var etag = _cache.ComputeETag(cachedPath);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext("GET", "/web/etag304.js");
        context.Request.Headers.IfNoneMatch = etag;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_WebAssetCacheHit_ServesBrotli_WhenAccepted()
    {
        var config = SetupPluginWithConfig();
        var rawContent = "var longVariable = 'test';"u8.ToArray();
        var brContent = CompressBrotli(rawContent);
        _cache.Store("compressed.js", "raw", rawContent);
        _cache.Store("compressed.js", "br", brContent);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext("GET", "/web/compressed.js", "br, gzip");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("br", context.Response.Headers.ContentEncoding.ToString());
    }

    // ── HTML handling ─────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_HtmlPath_NeverServesFromCache()
    {
        var config = SetupPluginWithConfig();
        // Even if HTML is cached, it should NOT be served from cache
        _cache.Store("index.html", "raw", "<html></html>"u8.ToArray());

        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/web/index.html");

        await middleware.InvokeAsync(context);

        // Must pass through to next middleware, not serve from cache
        Assert.True(nextCalled);
    }

    // ── Font handling ─────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_FontPath_SetsImmutableCache()
    {
        var config = SetupPluginWithConfig();
        var nextCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/web/fonts/roboto.woff2");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    // ── Plugin asset capture & optimization ───────────────────────

    [Fact]
    public async Task InvokeAsync_PluginAsset_CapturesAndMinifies()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/TestPlugin/",
            EnableMinification = true,
            EnableCompression = true
        });

        var upstream = """
            // This comment should be removed
            function testFunction() {
                var longVariableName = 42;
                return longVariableName;
            }
            """;
        var upstreamBytes = Encoding.UTF8.GetBytes(upstream);

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/javascript";
            ctx.Response.Body.Write(upstreamBytes);
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/TestPlugin/script.js", "br, gzip");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/javascript; charset=utf-8", context.Response.ContentType);

        // Verify it was cached
        Assert.True(_cache.TryGetCachedFile("plugin/TestPlugin/script.js", "raw", out _));
        Assert.True(_cache.TryGetCachedFile("plugin/TestPlugin/script.js", "br", out _));
        Assert.True(_cache.TryGetCachedFile("plugin/TestPlugin/script.js", "gz", out _));
    }

    [Fact]
    public async Task InvokeAsync_PluginAsset_ServesCachedOnSecondRequest()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/CachedPlugin/",
            EnableMinification = true,
            EnableCompression = true
        });

        // Pre-populate cache
        var cached = "var x=1;"u8.ToArray();
        _cache.Store("plugin/CachedPlugin/app.js", "raw", cached);

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/CachedPlugin/app.js");

        await middleware.InvokeAsync(context);

        // Should NOT call next — served from cache
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PluginAsset_DecompressesUpstreamBrotli()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/BrPlugin/",
            EnableMinification = true,
            EnableCompression = false
        });

        // Simulate upstream returning brotli-compressed JS despite Accept-Encoding strip
        var rawJs = """
            // Comment to remove
            function test() {
                var result = 100;
                return result;
            }
            """;
        var compressedJs = CompressBrotli(Encoding.UTF8.GetBytes(rawJs));

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/javascript";
            ctx.Response.Headers.ContentEncoding = "br";
            ctx.Response.Body.Write(compressedJs);
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/BrPlugin/script.js");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        // Should have decompressed and cached the raw version
        Assert.True(_cache.TryGetCachedFile("plugin/BrPlugin/script.js", "raw", out var rawPath));
        var cachedContent = Encoding.UTF8.GetString(File.ReadAllBytes(rawPath));
        Assert.DoesNotContain("// Comment", cachedContent);
    }

    [Fact]
    public async Task InvokeAsync_ExtensionlessPluginUrl_DetectsJsFromContentType()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/JellyfinEnhanced/",
            EnableMinification = true,
            EnableCompression = false
        });

        var upstream = """
            // Should be minified
            function enhanced() {
                var x = 1;
                return x;
            }
            """;
        var upstreamBytes = Encoding.UTF8.GetBytes(upstream);

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/javascript; charset=utf-8";
            ctx.Response.Body.Write(upstreamBytes);
            return Task.CompletedTask;
        });

        // Extensionless URL like /JellyfinEnhanced/script
        var context = CreateHttpContext("GET", "/JellyfinEnhanced/script");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("javascript", context.Response.ContentType!);
    }

    [Fact]
    public async Task InvokeAsync_ExtensionlessPluginUrl_PassesThroughNonJsCss()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/TestPlugin/",
            EnableMinification = true,
            EnableCompression = false
        });

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/html";
            ctx.Response.Body.Write("<html></html>"u8);
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/TestPlugin/page");

        await middleware.InvokeAsync(context);

        // Should pass through unmodified since it's not JS/CSS
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_PluginAsset_HandlesNon200Gracefully()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/ErrorPlugin/"
        });

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/ErrorPlugin/missing.js");

        await middleware.InvokeAsync(context);

        // Should not cache 404 responses
        Assert.False(_cache.TryGetCachedFile("plugin/ErrorPlugin/missing.js", "raw", out _));
    }

    // ── CORP / Security headers ───────────────────────────────────

    [Fact]
    public async Task InvokeAsync_SetsCorpHeader_WhenEnabled()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            EnableCrossOriginResourcePolicy = true,
            CrossOriginResourcePolicyValue = "same-origin"
        });

        var jsContent = "var a=1;"u8.ToArray();
        _cache.Store("corp-test.js", "raw", jsContent);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext("GET", "/web/corp-test.js");

        await middleware.InvokeAsync(context);

        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Resource-Policy"].ToString());
    }

    [Fact]
    public async Task InvokeAsync_SetsSecurityHeaders_WhenEnabled()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            EnableSecurityHeaders = true,
            XContentTypeOptions = "nosniff",
            ReferrerPolicy = "no-referrer"
        });

        var jsContent = "var a=1;"u8.ToArray();
        _cache.Store("sec-test.js", "raw", jsContent);

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateHttpContext("GET", "/web/sec-test.js");

        await middleware.InvokeAsync(context);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
    }

    // ── Unrelated paths ───────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_UnknownPath_PassesThrough()
    {
        var config = SetupPluginWithConfig();
        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/some/unknown/path.xyz");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static byte[] CompressBrotli(byte[] input)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(input);
        }
        return output.ToArray();
    }
}
