using Curato.Core;

namespace Curato.Tests;

public class FolderTitleParserTests
{
    [Theory]
    [InlineData(".Before")]
    [InlineData(".before")]
    [InlineData(".Before 1998")]
    [InlineData(".Before-1998")]
    [InlineData(".BEFORE-digital")]
    public void Recognizes_the_standard_pre_digital_scans_convention(string folderName)
    {
        Assert.True(FolderTitleParser.IsBeforeDigitalScansFolder(folderName));
    }

    [Theory]
    [InlineData(".Beforehand")]
    [InlineData("Before 1998")] // no leading dot
    [InlineData("2003 Mille")]
    [InlineData("1998-2000")]
    public void Does_not_false_match_similar_names(string folderName)
    {
        Assert.False(FolderTitleParser.IsBeforeDigitalScansFolder(folderName));
    }
}
