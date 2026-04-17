using Jellyfin.Plugin.JellyShim.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyShim;

/// <summary>
/// JellyShim — Performance optimization plugin for Jellyfin.
///
/// <para><b>Entry point:</b> This is the main plugin class discovered by Jellyfin's
/// plugin loader. It extends <see cref="BasePlugin{T}"/> to provide configuration
/// management, and implements <see cref="IHasWebPages"/> to register the admin config page
/// and <see cref="IDisposable"/> to clean up the disk cache when the plugin is uninstalled.</para>
///
/// <para><b>Lifecycle:</b> Constructed once when Jellyfin starts. The static
/// <see cref="Instance"/> property is set in the constructor so that middleware
/// (which can't receive the plugin via DI) can access the configuration.
/// When disposed (uninstall/shutdown), the entire cache directory is deleted
/// to avoid leaving orphaned files on disk.</para>
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
{
    /// <summary>Absolute path to the cache root ({CachePath}/jellyshim), used for cleanup on Dispose.</summary>
    private readonly string _cacheRoot;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _cacheRoot = Path.Combine(applicationPaths.CachePath, "jellyshim");
    }

    /// <inheritdoc />
    public override string Name => "JellyShim";

    /// <inheritdoc />
    public override Guid Id => new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <inheritdoc />
    public override string Description =>
        "Optimizes Jellyfin web client performance — JS/CSS/SVG minification, " +
        "Brotli/Zstd/Gzip pre-compression, native image processing, optimal cache headers.";

    /// <summary>
    /// Gets the current plugin instance.
    /// Set once in the constructor and used by middleware to access configuration
    /// without DI (middleware instances can't inject the plugin directly because
    /// the plugin is constructed after the DI container is built).
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "bolt"
            }
        ];
    }

    /// <summary>
    /// Cleans up the plugin cache directory.
    /// Called by Jellyfin when the plugin is uninstalled or the server shuts down.
    /// The cache is fully disposable — it will be rebuilt on next startup by
    /// <see cref="Tasks.OptimizeAssetsTask"/> and lazy capture in the middleware.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && Directory.Exists(_cacheRoot))
        {
            try
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — directory may be locked
            }
        }

        _disposed = true;
    }
}
