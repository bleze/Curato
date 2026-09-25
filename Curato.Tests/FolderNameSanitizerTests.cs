using Curato.Core;

namespace Curato.Tests;

public class FolderNameSanitizerTests
{
    [Theory]
    [InlineData("Beach/Day", "Beach-Day")]
    [InlineData("Beach\\Day", "Beach-Day")]
    [InlineData("Who: What?", "Who- What-")]
    [InlineData("<Weird>Title", "-Weird-Title")]
    public void Invalid_characters_are_replaced_with_a_hyphen(string input, string expected)
    {
        Assert.Equal(expected, FolderNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("Trailing dot.", "Trailing dot")]
    [InlineData("Trailing space ", "Trailing space")]
    public void Trailing_dot_or_space_is_trimmed(string input, string expected)
    {
        Assert.Equal(expected, FolderNameSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void A_reserved_device_name_gets_suffixed(string reservedName)
    {
        var sanitized = FolderNameSanitizer.Sanitize(reservedName);
        Assert.NotEqual(reservedName, sanitized, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(reservedName, sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_title_that_sanitizes_to_nothing_falls_back_to_a_placeholder()
    {
        Assert.Equal("Untitled", FolderNameSanitizer.Sanitize("..."));
    }

    [Fact]
    public void An_ordinary_title_is_unchanged()
    {
        Assert.Equal("Beach Day 2026", FolderNameSanitizer.Sanitize("Beach Day 2026"));
    }
}
