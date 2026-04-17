using Jellyfin.Plugin.JellyShim.Optimization;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class FileTransformationMatcherTests
{
    private readonly FileTransformationMatcher _matcher = new();

    // ── Null / empty patterns ────────────────────────────────────────

    [Fact]
    public void IsMatch_NullPatterns_ReturnsFalse()
    {
        Assert.False(_matcher.IsMatch("main.js", null));
    }

    [Fact]
    public void IsMatch_EmptyPatterns_ReturnsFalse()
    {
        Assert.False(_matcher.IsMatch("main.js", ""));
    }

    [Fact]
    public void IsMatch_WhitespacePatterns_ReturnsFalse()
    {
        Assert.False(_matcher.IsMatch("main.js", "   "));
    }

    // ── Chunk / bundle auto-matching ────────────────────────────────

    [Theory]
    [InlineData("home-html.abc123.chunk.js")]
    [InlineData("user-plugin.chunk.js")]
    [InlineData("SOME.CHUNK.JS")]
    public void IsMatch_ChunkJs_AlwaysTrue(string fileName)
    {
        // Chunk files match even with unrelated patterns
        Assert.True(_matcher.IsMatch(fileName, "unrelated-pattern.js"));
    }

    [Theory]
    [InlineData("main.jellyfin.bundle.js")]
    [InlineData("runtime.bundle.js")]
    [InlineData("APP.BUNDLE.JS")]
    public void IsMatch_BundleJs_AlwaysTrue(string fileName)
    {
        Assert.True(_matcher.IsMatch(fileName, "unrelated-pattern.js"));
    }

    // ── Pattern matching with wildcards ──────────────────────────────

    [Fact]
    public void IsMatch_ExactPattern_Matches()
    {
        Assert.True(_matcher.IsMatch("runtime.bundle.js", "runtime.bundle.js"));
    }

    [Fact]
    public void IsMatch_WildcardPattern_Matches()
    {
        Assert.True(_matcher.IsMatch("home-html.abc123.chunk.js", "home*.chunk.js"));
    }

    [Fact]
    public void IsMatch_WildcardPattern_MultiplePatterns()
    {
        var patterns = "main.*.bundle.js\nruntime.bundle.js";
        Assert.True(_matcher.IsMatch("main.12345.bundle.js", patterns));
        Assert.True(_matcher.IsMatch("runtime.bundle.js", patterns));
    }

    [Fact]
    public void IsMatch_NoMatchingPattern_ReturnsFalse()
    {
        // "app.js" doesn't end with .chunk.js or .bundle.js,
        // and doesn't match "main.*.bundle.js"
        Assert.False(_matcher.IsMatch("app.js", "main.*.bundle.js"));
    }

    [Fact]
    public void IsMatch_CaseInsensitive()
    {
        Assert.True(_matcher.IsMatch("MAIN.abc.BUNDLE.JS", "main.*.bundle.js"));
    }

    // ── Path with directory ──────────────────────────────────────────

    [Fact]
    public void IsMatch_MatchesOnFileNameOnly()
    {
        // Pattern matches the filename part, not the directory
        Assert.True(_matcher.IsMatch("sub/dir/main.abc.bundle.js", "main.*.bundle.js"));
    }

    // ── Config change invalidates cache ──────────────────────────────

    [Fact]
    public void IsMatch_ConfigChange_RecompilesRegexes()
    {
        // First config: only main files
        Assert.True(_matcher.IsMatch("main.abc.bundle.js", "main.*.bundle.js"));
        Assert.False(_matcher.IsMatch("custom.js", "main.*.bundle.js"));

        // Change config: now custom.js matches
        Assert.True(_matcher.IsMatch("custom.js", "custom.js"));
    }

    // ── Thread safety: concurrent access doesn't crash ──────────────

    [Fact]
    public void IsMatch_ConcurrentAccess_NoCrash()
    {
        var patterns = "main.*.bundle.js\nruntime.bundle.js\nhome*.chunk.js";

        Parallel.For(0, 100, i =>
        {
            // Mix of matching and non-matching paths
            var path = i % 2 == 0 ? "main.abc.bundle.js" : "app.js";
            // Should never throw, even with concurrent pattern recompilation
            _ = _matcher.IsMatch(path, patterns);
        });
    }

    [Fact]
    public void IsMatch_ConcurrentConfigChange_NoCrash()
    {
        var patterns1 = "main.*.bundle.js";
        var patterns2 = "runtime.bundle.js\nhome*.chunk.js";

        Parallel.For(0, 200, i =>
        {
            // Alternate config strings to trigger recompilation races
            var patterns = i % 2 == 0 ? patterns1 : patterns2;
            _ = _matcher.IsMatch("main.abc.bundle.js", patterns);
        });
    }
}
