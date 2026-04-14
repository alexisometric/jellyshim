using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyShim.Configuration;

/// <summary>
/// Plugin configuration for JellyShim.
/// All settings are editable via the admin config page (Dashboard → Plugins → JellyShim).
/// Defaults are chosen to be safe and performant out of the box.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Asset Optimization ──────────────────────────────────────────
    // Controls whether JS/CSS content is minified (NUglify) and pre-compressed
    // (Brotli/Gzip) before being cached to disk. Both are enabled by default
    // for maximum performance improvement.

    /// <summary>Gets or sets a value indicating whether JS/CSS minification is enabled.</summary>
    public bool EnableMinification { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Brotli/Gzip pre-compression is enabled.</summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>Gets or sets the Brotli compression level (0-11). 11 is max compression.</summary>
    public int BrotliCompressionLevel { get; set; } = 11;

    // ── Cache Headers ───────────────────────────────────────────────
    // Controls HTTP Cache-Control headers sent to browsers. Hashed assets
    // (content-addressed filenames like main.a1b2c3d4.bundle.js) get long
    // immutable caching. Unhashed assets get shorter max-age with
    // stale-while-revalidate for background refresh.

    /// <summary>Gets or sets a value indicating whether optimal cache headers are added.</summary>
    public bool EnableCacheHeaders { get; set; } = true;

    /// <summary>Gets or sets max-age for hashed assets (seconds). Default: 1 year.</summary>
    public int HashedAssetMaxAge { get; set; } = 31536000;

    /// <summary>Gets or sets max-age for general static assets (seconds). Default: 30 days.</summary>
    public int StaticAssetMaxAge { get; set; } = 2592000;

    /// <summary>Gets or sets max-age for plugin assets (seconds). Default: 1 day.</summary>
    public int PluginAssetMaxAge { get; set; } = 86400;

    /// <summary>Gets or sets stale-while-revalidate for static assets (seconds). Default: 1 day.</summary>
    public int StaleWhileRevalidate { get; set; } = 86400;

    /// <summary>Gets or sets max-age for image responses (seconds). Default: 30 days.</summary>
    public int ImageCacheMaxAge { get; set; } = 2592000;

    /// <summary>Gets or sets stale-while-revalidate for images (seconds). Default: 7 days.</summary>
    public int ImageStaleWhileRevalidate { get; set; } = 604800;

    // ── Link Headers (103 Early Hints / Link) ──────────────────────

    /// <summary>Gets or sets a value indicating whether Link headers for fonts are added (preload).</summary>
    public bool EnableFontPreloadHeaders { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Link headers for JS are added (modulepreload).</summary>
    public bool EnableJsModulepreloadHeaders { get; set; } = true;

    // ── CORS / Resource Policy ──────────────────────────────────────

    /// <summary>Gets or sets a value indicating whether Cross-Origin-Resource-Policy header is added.</summary>
    public bool EnableCrossOriginResourcePolicy { get; set; } = true;

    /// <summary>Gets or sets the CORP value. Default: cross-origin (for iframe Jellyseerr compat).</summary>
    public string CrossOriginResourcePolicyValue { get; set; } = "cross-origin";

    // ── Security Headers ────────────────────────────────────────────

    /// <summary>Gets or sets a value indicating whether security headers are enabled.</summary>
    public bool EnableSecurityHeaders { get; set; } = false;

    /// <summary>Gets or sets X-Content-Type-Options value.</summary>
    public string XContentTypeOptions { get; set; } = "nosniff";

    /// <summary>Gets or sets Referrer-Policy value.</summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>Gets or sets Permissions-Policy value.</summary>
    public string PermissionsPolicy { get; set; } = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), usb=()";

    // ── Image Optimization (native) ────────────────────────────────
    // When enabled, intercepts Jellyfin image API responses and processes
    // them in-process: resize to max width, re-encode to a modern format
    // (AVIF/WebP/JPEG), and cache to disk. Disabled by default to avoid
    // unexpected changes to image quality — enable after reviewing per-type settings.

    /// <summary>Gets or sets a value indicating whether native image optimization is enabled.</summary>
    public bool EnableImageOptimization { get; set; } = false;

    /// <summary>Gets or sets the preferred output format (avif, webp, jpeg, or auto).</summary>
    public string ImageOutputFormat { get; set; } = "auto";

    /// <summary>Gets or sets a value indicating whether processed images are cached to disk.</summary>
    public bool EnableImageCache { get; set; } = true;

    // ── Per-type image settings ─────────────────────────────────────

    /// <summary>Gets or sets the max width for Primary images (poster, cover art).</summary>
    public int PrimaryMaxWidth { get; set; } = 600;

    /// <summary>Gets or sets the quality for Primary images (1-100).</summary>
    public int PrimaryQuality { get; set; } = 80;

    /// <summary>Gets or sets the max width for Backdrop images (background art).</summary>
    public int BackdropMaxWidth { get; set; } = 1920;

    /// <summary>Gets or sets the quality for Backdrop images (1-100).</summary>
    public int BackdropQuality { get; set; } = 75;

    /// <summary>Gets or sets the max width for Art images (fan art, clear art).</summary>
    public int ArtMaxWidth { get; set; } = 1280;

    /// <summary>Gets or sets the quality for Art images (1-100).</summary>
    public int ArtQuality { get; set; } = 75;

    /// <summary>Gets or sets the max width for Banner images.</summary>
    public int BannerMaxWidth { get; set; } = 1000;

    /// <summary>Gets or sets the quality for Banner images (1-100).</summary>
    public int BannerQuality { get; set; } = 80;

    /// <summary>Gets or sets the max width for Logo images (transparent).</summary>
    public int LogoMaxWidth { get; set; } = 400;

    /// <summary>Gets or sets the quality for Logo images (1-100).</summary>
    public int LogoQuality { get; set; } = 90;

    /// <summary>Gets or sets the max width for Thumb images (thumbnails).</summary>
    public int ThumbMaxWidth { get; set; } = 480;

    /// <summary>Gets or sets the quality for Thumb images (1-100).</summary>
    public int ThumbQuality { get; set; } = 75;

    /// <summary>Gets or sets the max width for Screenshot images.</summary>
    public int ScreenshotMaxWidth { get; set; } = 1280;

    /// <summary>Gets or sets the quality for Screenshot images (1-100).</summary>
    public int ScreenshotQuality { get; set; } = 75;

    /// <summary>Gets or sets the max width for Chapter images.</summary>
    public int ChapterMaxWidth { get; set; } = 400;

    /// <summary>Gets or sets the quality for Chapter images (1-100).</summary>
    public int ChapterQuality { get; set; } = 70;

    /// <summary>Gets or sets the max width for Profile images (user pictures).</summary>
    public int ProfileMaxWidth { get; set; } = 200;

    /// <summary>Gets or sets the quality for Profile images (1-100).</summary>
    public int ProfileQuality { get; set; } = 85;

    /// <summary>Gets or sets the max width for Disc images (CD/DVD art).</summary>
    public int DiscMaxWidth { get; set; } = 300;

    /// <summary>Gets or sets the quality for Disc images (1-100).</summary>
    public int DiscQuality { get; set; } = 80;

    /// <summary>Gets or sets the max width for Box images (box art).</summary>
    public int BoxMaxWidth { get; set; } = 300;

    /// <summary>Gets or sets the quality for Box images (1-100).</summary>
    public int BoxQuality { get; set; } = 80;

    /// <summary>Gets or sets the max width for BoxRear images (rear box art).</summary>
    public int BoxRearMaxWidth { get; set; } = 300;

    /// <summary>Gets or sets the quality for BoxRear images (1-100).</summary>
    public int BoxRearQuality { get; set; } = 80;

    /// <summary>Gets or sets the max width for unrecognized image types (fallback).</summary>
    public int DefaultImageMaxWidth { get; set; } = 300;

    /// <summary>Gets or sets the quality for unrecognized image types (fallback, 1-100).</summary>
    public int DefaultImageQuality { get; set; } = 80;

    // ── Plugin Static Asset Paths ───────────────────────────────────
    // Path prefixes that identify plugin static resources (JS/CSS/JSON).
    // Requests matching these prefixes are captured, minified, compressed,
    // and cached on first access. Paths excluded here (like /HomeScreen/)
    // pass through unmodified — important for plugins that serve dynamic content.

    /// <summary>
    /// Gets or sets known plugin static asset path prefixes (one per line).
    /// These paths get plugin-level caching + compression.
    /// </summary>
    public string PluginAssetPaths { get; set; } =
        "/HomeScreen/\n/JellyTweaks/\n/MediaBarEnhanced/\n/Plugins/Announcements/\n/JellyfinEnhanced/\n/JavaScriptInjector/";

    // ── File Transformation Compatibility ───────────────────────────
    // Filename patterns for web assets patched at runtime by File Transformation
    // plugins (HSS, Custom Tabs, JellyfinEnhanced, etc.).
    // These files are NOT pre-optimized by the scheduled task — instead,
    // the middleware captures the TRANSFORMED response from upstream,
    // then minifies, compresses, and caches it with a separate "ft/" prefix.
    // Browser cache uses no-cache (ETag revalidation) because the content
    // can change when FT plugins are updated without a Jellyfin restart.

    /// <summary>
    /// Gets or sets filename patterns (one per line, supports * wildcard) for web assets
    /// that are patched at runtime by File Transformation plugins (HSS, Custom Tabs,
    /// JellyfinEnhanced, etc.).  These files are captured AFTER transformation, then
    /// minified, compressed, and cached separately from pre-built assets.
    /// </summary>
    public string FileTransformationBypassPatterns { get; set; } =
        "home*.chunk.js\nmain.*.bundle.js\nruntime.bundle.js\nuser-plugin*.chunk.js";
}
