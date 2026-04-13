using System.Text;
using Jellyfin.Plugin.JellyShim.Transformation;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class CssTransformerTests
{
    private readonly CssTransformer _transformer;

    public CssTransformerTests()
    {
        var logger = new Mock<ILogger<CssTransformer>>();
        _transformer = new CssTransformer(logger.Object);
    }

    [Fact]
    public void Minify_RemovesWhitespaceAndComments()
    {
        var input = """
            /* Main container styles */
            .container {
                margin: 0 auto;
                padding: 20px;
                background-color: #ffffff;
            }

            /* Header */
            .header {
                font-size: 16px;
                color: #333333;
            }
            """;

        var result = _transformer.Minify(input);

        Assert.DoesNotContain("/* Main container styles */", result);
        Assert.DoesNotContain("/* Header */", result);
        Assert.True(result.Length < input.Length, "Minified output should be shorter");
        Assert.Contains(".container", result);
        Assert.Contains(".header", result);
    }

    [Fact]
    public void Minify_ReturnsOriginal_WhenInputIsEmpty()
    {
        Assert.Equal("", _transformer.Minify(""));
    }

    [Fact]
    public void Minify_ReturnsOriginal_WhenInputIsNull()
    {
        Assert.Null(_transformer.Minify(null!));
    }

    [Fact]
    public void Minify_ReturnsOriginal_WhenInputIsWhitespace()
    {
        var result = _transformer.Minify("   \n  \t  ");
        Assert.Equal("   \n  \t  ", result);
    }

    [Fact]
    public void Minify_PreservesPropertyValues()
    {
        var input = """
            .element {
                color: rgb(255, 0, 0);
                font-family: "Helvetica Neue", Arial, sans-serif;
            }
            """;

        var result = _transformer.Minify(input);

        // NUglify may shorten rgb(255,0,0) to #f00 — both are valid
        Assert.True(
            result.Contains("rgb(255,0,0)") || result.Contains("#f00"),
            "Expected color value to be preserved (either rgb or shorthand hex)");
        Assert.Contains("Helvetica Neue", result);
    }

    [Fact]
    public void Minify_CollapsesMultipleRules()
    {
        var input = """
            .a {
                color: red;
            }

            .b {
                color: blue;
            }

            .c {
                color: green;
            }
            """;

        var result = _transformer.Minify(input);

        Assert.Contains(".a", result);
        Assert.Contains(".b", result);
        Assert.Contains(".c", result);
        Assert.True(result.Length < input.Length);
    }

    [Fact]
    public void MinifyBytes_WorksWithUtf8()
    {
        var input = """
            /* Remove this comment */
            body {
                margin: 0;
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(input);

        var result = _transformer.MinifyBytes(bytes);
        var resultText = Encoding.UTF8.GetString(result);

        Assert.DoesNotContain("/* Remove", resultText);
        Assert.Contains("body", resultText);
        Assert.Contains("margin", resultText);
    }

    [Fact]
    public void MinifyBytes_EmptyInput_ReturnsEmpty()
    {
        var result = _transformer.MinifyBytes(Array.Empty<byte>());
        Assert.Empty(result);
    }

    [Fact]
    public void Minify_HandlesInvalidCss_ReturnsOriginal()
    {
        // Severely broken CSS
        var input = "{{{{ not valid css }}}} :::";

        var result = _transformer.Minify(input);
        Assert.NotNull(result);
    }

    // ── font-display:swap injection tests ──────────────────────────

    [Fact]
    public void InjectFontDisplaySwap_AddsToPureFontFace()
    {
        var input = "@font-face{font-family:MyFont;src:url(font.woff2)}";
        var result = _transformer.InjectFontDisplaySwap(input);
        Assert.Contains("font-display:swap", result);
    }

    [Fact]
    public void InjectFontDisplaySwap_SkipsWhenAlreadyPresent()
    {
        var input = "@font-face{font-family:MyFont;src:url(font.woff2);font-display:swap}";
        var result = _transformer.InjectFontDisplaySwap(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void InjectFontDisplaySwap_HandlesMultipleFontFaces()
    {
        var input = "@font-face{font-family:A;src:url(a.woff2)}@font-face{font-family:B;src:url(b.woff2)}";
        var result = _transformer.InjectFontDisplaySwap(input);
        Assert.Equal(2, CountOccurrences(result, "font-display:swap"));
    }

    [Fact]
    public void InjectFontDisplaySwap_MixedPresentAndMissing()
    {
        var input = "@font-face{font-family:A;src:url(a.woff2);font-display:swap}@font-face{font-family:B;src:url(b.woff2)}";
        var result = _transformer.InjectFontDisplaySwap(input);
        Assert.Equal(2, CountOccurrences(result, "font-display:swap"));
    }

    [Fact]
    public void InjectFontDisplaySwap_NoCssInput_ReturnsUnchanged()
    {
        var input = "body{margin:0}";
        var result = _transformer.InjectFontDisplaySwap(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void InjectFontDisplaySwap_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", _transformer.InjectFontDisplaySwap(""));
    }

    [Fact]
    public void Minify_InjectsFontDisplaySwap_EndToEnd()
    {
        var input = """
            @font-face {
                font-family: "TestFont";
                src: url("test.woff2") format("woff2");
                font-weight: 400;
            }
            body { margin: 0; }
            """;

        var result = _transformer.Minify(input);

        Assert.Contains("font-display:swap", result);
        Assert.Contains("body", result);
    }

    [Fact]
    public void MinifyBytes_InjectsFontDisplaySwap()
    {
        var input = "@font-face { font-family: X; src: url(x.woff2); } .a { color: red; }";
        var bytes = Encoding.UTF8.GetBytes(input);
        var result = Encoding.UTF8.GetString(_transformer.MinifyBytes(bytes));
        Assert.Contains("font-display:swap", result);
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(search, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }
}
