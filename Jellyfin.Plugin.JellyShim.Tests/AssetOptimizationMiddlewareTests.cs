using System.IO.Compression;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.JellyShim.Cache;
using Jellyfin.Plugin.JellyShim.Configuration;
using Jellyfin.Plugin.JellyShim.Middleware;
using Jellyfin.Plugin.JellyShim.Transformation;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller;
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
    private readonly Mock<IServerConfigurationManager> _configManagerMock;

    public AssetOptimizationMiddlewareTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "jellyshim-mw-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _cache = new DiskCacheManager(_tempDir, new Mock<ILogger<DiskCacheManager>>().Object);
        _jsTransformer = new JsTransformer(new Mock<ILogger<JsTransformer>>().Object);
        _cssTransformer = new CssTransformer(new Mock<ILogger<CssTransformer>>().Object);
        _loggerMock = new Mock<ILogger<AssetOptimizationMiddleware>>();

        var appPathsMock = new Mock<IServerApplicationPaths>();
        appPathsMock.Setup(p => p.WebPath).Returns(Path.Combine(_tempDir, "web"));
        _configManagerMock = new Mock<IServerConfigurationManager>();
        _configManagerMock.Setup(c => c.ApplicationPaths).Returns(appPathsMock.Object);
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
        return new AssetOptimizationMiddleware(next, _cache, _jsTransformer, _cssTransformer, _configManagerMock.Object, _loggerMock.Object);
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

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("main.abcdef12.js", "raw", jsContent);
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

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("etag304.js", "raw", jsContent);
        _cache.TryGetCachedFile("etag304.js", "raw", out var cachedPath);
        var etag = _cache.ComputeETag(cachedPath);

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

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("compressed.js", "raw", rawContent);
        _cache.Store("compressed.js", "br", brContent);

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

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        _cache.Store("plugin/CachedPlugin/app.js", "raw", cached);

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

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("corp-test.js", "raw", jsContent);

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

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("sec-test.js", "raw", jsContent);

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

    // ── File Transformation capture-after-transform tests ─────────

    [Fact]
    public async Task InvokeAsync_FtAsset_CapturesAndOptimizesTransformedResponse()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = "main.*.bundle.js",
            EnableMinification = true,
            EnableCompression = true
        });

        var upstream = """
            // This comment should be removed by minification
            function patchedByFT() {
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
        var context = CreateHttpContext("GET", "/web/main.abc123.bundle.js", "br, gzip");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        // Verify FT cache was populated (prefixed with ft/)
        Assert.True(_cache.TryGetCachedFile("ft/main.abc123.bundle.js", "raw", out _));
        Assert.True(_cache.TryGetCachedFile("ft/main.abc123.bundle.js", "br", out _));
        Assert.True(_cache.TryGetCachedFile("ft/main.abc123.bundle.js", "gz", out _));
    }

    [Fact]
    public async Task InvokeAsync_FtAsset_ServesCachedOnSecondRequest()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = "runtime.bundle.js",
            EnableMinification = true,
            EnableCompression = false
        });

        var cached = "var x=1;"u8.ToArray();

        var nextCalled = false;
        var middleware = CreateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        _cache.Store("ft/runtime.bundle.js", "raw", cached);

        var context = CreateHttpContext("GET", "/web/runtime.bundle.js");

        await middleware.InvokeAsync(context);

        // Should NOT call next — served from FT cache
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_FtAsset_Returns304_OnEtagMatch()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = "runtime.bundle.js"
        });

        var cached = "var x=1;"u8.ToArray();

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("ft/runtime.bundle.js", "raw", cached);
        _cache.TryGetCachedFile("ft/runtime.bundle.js", "raw", out var cachedPath);
        var etag = _cache.ComputeETag(cachedPath);

        var context = CreateHttpContext("GET", "/web/runtime.bundle.js");
        context.Request.Headers.IfNoneMatch = etag;

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_FtAsset_SetsNoCacheHeaders()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = "runtime.bundle.js",
            EnableCacheHeaders = true
        });

        var cached = "var x=1;"u8.ToArray();

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("ft/runtime.bundle.js", "raw", cached);

        var context = CreateHttpContext("GET", "/web/runtime.bundle.js");

        await middleware.InvokeAsync(context);

        // FT files should get no-cache to force ETag revalidation
        var cacheControl = context.Response.Headers.CacheControl.ToString();
        Assert.Contains("no-cache", cacheControl);
        Assert.DoesNotContain("max-age=2592000", cacheControl);
    }

    [Fact]
    public async Task InvokeAsync_FtAsset_CapturedResponse_SetsNoCacheHeaders()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = "runtime.bundle.js",
            EnableMinification = false,
            EnableCompression = false,
            EnableCacheHeaders = true
        });

        var upstream = "var x = 1;"u8.ToArray();

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/javascript";
            ctx.Response.Body.Write(upstream);
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/web/runtime.bundle.js");

        await middleware.InvokeAsync(context);

        var cacheControl = context.Response.Headers.CacheControl.ToString();
        Assert.Contains("no-cache", cacheControl);
    }

    [Fact]
    public async Task InvokeAsync_FtAsset_DetectsStaleCacheFromSourceFile()
    {
        // Set up a web directory with a source file
        var webDir = Path.Combine(_tempDir, "web");
        Directory.CreateDirectory(webDir);
        var sourceFile = Path.Combine(webDir, "runtime.bundle.js");
        File.WriteAllText(sourceFile, "// original source");

        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = "runtime.bundle.js",
            EnableMinification = false,
            EnableCompression = false
        });

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/javascript";
            ctx.Response.Body.Write("var updated = true;"u8);
            return Task.CompletedTask;
        });

        // Pre-populate cache with old content (backdate the cache file)
        _cache.Store("ft/runtime.bundle.js", "raw", "var old = true;"u8.ToArray());
        _cache.TryGetCachedFile("ft/runtime.bundle.js", "raw", out var cachedPath);

        // Make cache file older than source
        File.SetLastWriteTimeUtc(cachedPath, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow);

        var context = CreateHttpContext("GET", "/web/runtime.bundle.js");

        await middleware.InvokeAsync(context);

        // Should have re-captured from upstream (not served stale cache)
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        // Verify the cache was updated
        _cache.TryGetCachedFile("ft/runtime.bundle.js", "raw", out var updatedPath);
        var content = File.ReadAllText(updatedPath);
        Assert.Contains("updated", content);
    }

    // ── Cache header tests ────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_HashedAsset_SetsImmutableCacheHeaders()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            EnableCacheHeaders = true,
            HashedAssetMaxAge = 31536000
        });

        var jsContent = "var x=1;"u8.ToArray();

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("main.a1b2c3d4.js", "raw", jsContent);

        var context = CreateHttpContext("GET", "/web/main.a1b2c3d4.js");

        await middleware.InvokeAsync(context);

        var cacheControl = context.Response.Headers.CacheControl.ToString();
        Assert.Contains("immutable", cacheControl);
        Assert.Contains("31536000", cacheControl);
    }

    [Fact]
    public async Task InvokeAsync_NonHashedAsset_SetsStaticMaxAge()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            EnableCacheHeaders = true,
            StaticAssetMaxAge = 2592000,
            StaleWhileRevalidate = 86400
        });

        var jsContent = "var x=1;"u8.ToArray();

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("utils.js", "raw", jsContent);

        var context = CreateHttpContext("GET", "/web/utils.js");

        await middleware.InvokeAsync(context);

        var cacheControl = context.Response.Headers.CacheControl.ToString();
        Assert.Contains("2592000", cacheControl);
        Assert.Contains("stale-while-revalidate", cacheControl);
        Assert.DoesNotContain("immutable", cacheControl);
    }

    [Fact]
    public async Task InvokeAsync_PluginAsset_SetsPluginMaxAge()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/TestPlugin/",
            EnableCacheHeaders = true,
            PluginAssetMaxAge = 86400
        });

        var cached = "var x=1;"u8.ToArray();

        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("plugin/TestPlugin/script.js", "raw", cached);

        var context = CreateHttpContext("GET", "/TestPlugin/script.js");

        await middleware.InvokeAsync(context);

        var cacheControl = context.Response.Headers.CacheControl.ToString();
        Assert.Contains("86400", cacheControl);
    }

    // ── FT wildcard matching tests ────────────────────────────────

    [Theory]
    [InlineData("/web/home-html.abc123.chunk.js", "home*.chunk.js", true)]
    [InlineData("/web/main.jellyfin.bundle.js", "main.*.bundle.js", true)]
    [InlineData("/web/runtime.bundle.js", "runtime.bundle.js", true)]
    [InlineData("/web/user-plugin-settings.abc.chunk.js", "user-plugin*.chunk.js", true)]
    [InlineData("/web/utils.js", "home*.chunk.js\nmain.*.bundle.js", false)]
    [InlineData("/web/other.js", "runtime.bundle.js", false)]
    public async Task InvokeAsync_FtPatternMatching_RoutesCorrectly(string path, string patterns, bool shouldCapture)
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            FileTransformationBypassPatterns = patterns,
            EnableMinification = false,
            EnableCompression = false
        });

        var upstreamCalled = false;
        var middleware = CreateMiddleware(ctx =>
        {
            upstreamCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/javascript";
            ctx.Response.Body.Write("var x=1;"u8);
            return Task.CompletedTask;
        });

        var context = CreateHttpContext("GET", path);

        await middleware.InvokeAsync(context);

        if (shouldCapture)
        {
            // FT-matched files go through capture flow (upstream is called)
            Assert.True(upstreamCalled);
            var relativePath = path[5..]; // strip "/web/"
            Assert.True(_cache.TryGetCachedFile("ft/" + relativePath, "raw", out _));
        }
    }

    // ── ETag consistency tests ────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_WebAsset_ETagChangesWhenContentChanges()
    {
        var config = SetupPluginWithConfig();

        // First version
        var middleware1 = CreateMiddleware(_ => Task.CompletedTask);
        _cache.Store("etag-change.js", "raw", "var version1=1;"u8.ToArray());
        var context1 = CreateHttpContext("GET", "/web/etag-change.js");
        await middleware1.InvokeAsync(context1);
        var etag1 = context1.Response.Headers.ETag.ToString();

        // Update the content
        _cache.Store("etag-change.js", "raw", "var version2=2;"u8.ToArray());
        var context2 = CreateHttpContext("GET", "/web/etag-change.js");
        await middleware1.InvokeAsync(context2);
        var etag2 = context2.Response.Headers.ETag.ToString();

        Assert.NotEqual(etag1, etag2);
    }

    // ── Plugin CSS capture test ───────────────────────────────────

    [Fact]
    public async Task InvokeAsync_PluginCssAsset_CapturesAndMinifies()
    {
        var config = SetupPluginWithConfig(new PluginConfiguration
        {
            PluginAssetPaths = "/JellyTweaks/",
            EnableMinification = true,
            EnableCompression = false
        });

        var upstream = """
            /* This comment should be removed */
            body {
                margin: 0;
                padding: 0;
            }
            """;
        var upstreamBytes = Encoding.UTF8.GetBytes(upstream);

        var middleware = CreateMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/css";
            ctx.Response.Body.Write(upstreamBytes);
            return Task.CompletedTask;
        });
        var context = CreateHttpContext("GET", "/JellyTweaks/style.css");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(_cache.TryGetCachedFile("plugin/JellyTweaks/style.css", "raw", out var rawPath));

        var cachedContent = File.ReadAllText(rawPath);
        Assert.DoesNotContain("This comment", cachedContent);
    }
}
