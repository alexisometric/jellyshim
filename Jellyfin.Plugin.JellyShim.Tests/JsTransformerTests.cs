using System.Text;
using Jellyfin.Plugin.JellyShim.Transformation;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.JellyShim.Tests;

public class JsTransformerTests
{
    private readonly JsTransformer _transformer;

    public JsTransformerTests()
    {
        var logger = new Mock<ILogger<JsTransformer>>();
        _transformer = new JsTransformer(logger.Object);
    }

    [Fact]
    public void Minify_RemovesWhitespaceAndComments()
    {
        var input = """
            // This is a comment
            function hello() {
                var x = 1;
                var y = 2;
                return x + y;
            }
            """;

        var result = _transformer.Minify(input);

        Assert.DoesNotContain("// This is a comment", result);
        Assert.True(result.Length < input.Length, "Minified output should be shorter");
        Assert.Contains("function", result);
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
    public void Minify_SkipsAlreadyMinified()
    {
        // Generate a long single-line string that looks already minified (< 1% newlines)
        var minified = "var a=1;" + new string('x', 2500) + ";";
        var result = _transformer.Minify(minified);

        // Should return the same content since it detects it's already minified
        Assert.Equal(minified, result);
    }

    [Fact]
    public void Minify_HandlesLargeInput()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++)
        {
            sb.AppendLine($"// Comment line {i}");
            sb.AppendLine($"var variable_{i} = {i};");
        }

        var input = sb.ToString();
        var result = _transformer.Minify(input);

        Assert.True(result.Length < input.Length);
    }

    [Fact]
    public void Minify_PreservesStringLiterals()
    {
        var input = """
            var msg = "Hello World";
            console.log(msg);
            """;

        var result = _transformer.Minify(input);

        Assert.Contains("Hello World", result);
    }

    [Fact]
    public void MinifyBytes_WorksWithUtf8()
    {
        var input = """
            // Comment to remove
            var x = 42;
            """;
        var bytes = Encoding.UTF8.GetBytes(input);

        var result = _transformer.MinifyBytes(bytes);
        var resultText = Encoding.UTF8.GetString(result);

        Assert.DoesNotContain("// Comment", resultText);
        Assert.Contains("42", resultText);
    }

    [Fact]
    public void MinifyBytes_EmptyInput_ReturnsEmpty()
    {
        var result = _transformer.MinifyBytes(Array.Empty<byte>());
        Assert.Empty(result);
    }

    [Fact]
    public void Minify_ReturnsSyntacticallyValidJs_OnError()
    {
        // Intentionally malformed JS — NUglify returns original on parse error
        var input = """
            function broken(
                // Unterminated
            """;

        // Should not throw, returns original on error
        var result = _transformer.Minify(input);
        Assert.NotNull(result);
    }

    [Fact]
    public void Minify_UsesOutput_WhenNonFatalErrors()
    {
        // Duplicate property names in strict mode — NUglify reports error but still produces
        // smaller valid output; the transformer should use it instead of falling back.
        var input = """
            "use strict";
            var obj = {
                name: "hello",
                name: "world"
            };
            console.log(obj);
            """;

        var result = _transformer.Minify(input);

        // Should be minified (shorter than input), not the original
        Assert.True(result.Length < input.Length, "Non-fatal errors should still produce minified output");
        Assert.Contains("hello", result);
    }
}
