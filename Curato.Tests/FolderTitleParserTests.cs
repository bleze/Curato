using Curato.Core;

namespace Curato.Tests;

public class FolderTitleParserTests
{
    [Theory]
    [InlineData(".Pre")]
    [InlineData(".pre")]
    [InlineData(".Pre 1998")]
    [InlineData(".Pre-1998")]
    [InlineData(".PRE-digital")]
    public void Recognizes_the_standard_pre_digital_scans_convention(string folderName)
    {
        Assert.True(FolderTitleParser.IsPreDigitalScansFolder(folderName));
    }

    [Theory]
    [InlineData(".Preston")]
    [InlineData(".Prep")]
    [InlineData(".Present")]
    [InlineData("Pre 1998")] // no leading dot
    [InlineData("2003 Mille")]
    [InlineData("1998-2000")]
    public void Does_not_false_match_similar_names(string folderName)
    {
        Assert.False(FolderTitleParser.IsPreDigitalScansFolder(folderName));
    }
}
