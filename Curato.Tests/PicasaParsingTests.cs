using Curato.Picasa;

namespace Curato.Tests;

public class PicasaParsingTests
{
    // left=0x4000 (~0.25), top=0x0000 (0), right=0xC000 (~0.75), bottom=0xFFFF (1.0)
    private const string SampleRectHex = "40000000c000ffff";

    [Fact]
    public void Rect64_decodes_normalized_fractions_into_pixel_coordinates()
    {
        var pixelRect = PicasaRect.ToPixelRect(SampleRectHex, imageWidth: 1000, imageHeight: 2000);

        Assert.NotNull(pixelRect);
        Assert.Equal(250, pixelRect!.Value.X);
        Assert.Equal(0, pixelRect.Value.Y);
        Assert.Equal(500, pixelRect.Value.Width);
        Assert.Equal(2000, pixelRect.Value.Height);
    }

    [Theory]
    [InlineData("tooshort")]
    [InlineData("not-hex-at-all!!")]
    [InlineData("ffff0000ffff000000")] // too long
    public void Rect64_returns_null_for_malformed_input_rather_than_guessing(string malformed)
    {
        Assert.Null(PicasaRect.ToPixelRect(malformed, 1000, 1000));
    }

    [Fact]
    public void Ini_parser_reads_sections_and_key_value_pairs_and_skips_comments()
    {
        var content = """
            ; a comment before anything
            stray=ignored because no section yet

            [.album:main]
            name=Ignored album marker

            [IMG_0001.JPG]
            star=yes
            rotate=rotate(1)
            # another comment
            crop=rect64(40000000c000ffff)
            """;

        var sections = PicasaIniParser.Parse(content);

        Assert.Equal(2, sections.Count);
        var photoSection = sections.Single(s => s.Name == "IMG_0001.JPG");
        Assert.Equal("yes", photoSection.Values["star"]);
        Assert.Equal("rotate(1)", photoSection.Values["rotate"]);
        Assert.Equal("rect64(40000000c000ffff)", photoSection.Values["crop"]);
    }

    [Fact]
    public void Photo_metadata_parses_star_rotate_crop_and_faces()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["star"] = "yes",
            ["rotate"] = "rotate(1)",
            ["crop"] = "rect64(40000000c000ffff)",
            ["faces"] = "rect64(40000000c000ffff),ffffffffe0929397a2417b56;rect64(40000000c000ffff),contact2",
            ["albums"] = "albumId1, albumId2",
        };

        var metadata = PicasaPhotoMetadataParser.Parse(values);

        Assert.True(metadata.Star);
        Assert.Equal(90, metadata.RotationDegrees);
        Assert.Equal("40000000c000ffff", metadata.CropRectHex);
        Assert.Equal(2, metadata.Faces.Count);
        Assert.Equal("ffffffffe0929397a2417b56", metadata.Faces[0].ContactId);
        Assert.Equal("contact2", metadata.Faces[1].ContactId);
        Assert.Equal(["albumId1", "albumId2"], metadata.AlbumIds);
    }

    [Fact]
    public void Photo_metadata_defaults_are_safe_when_keys_are_absent()
    {
        var metadata = PicasaPhotoMetadataParser.Parse(new Dictionary<string, string>());

        Assert.False(metadata.Star);
        Assert.Null(metadata.RotationDegrees);
        Assert.Null(metadata.CropRectHex);
        Assert.Empty(metadata.Faces);
        Assert.Empty(metadata.AlbumIds);
    }

    [Fact]
    public void Contacts_xml_maps_contact_id_to_name()
    {
        var xml = """
            <contacts>
              <contact id="ffffffffe0929397a2417b56" name="Jane Doe" email="" />
              <contact id="contact2" name="John Smith" />
              <contact id="noname" />
            </contacts>
            """;

        var contacts = PicasaContactsParser.Parse(xml);

        Assert.Equal(2, contacts.Count);
        Assert.Equal("Jane Doe", contacts["ffffffffe0929397a2417b56"]);
        Assert.Equal("John Smith", contacts["contact2"]);
    }

    [Fact]
    public void Contacts_xml_parsing_returns_empty_map_for_malformed_xml_rather_than_throwing()
    {
        var contacts = PicasaContactsParser.Parse("<not><valid");
        Assert.Empty(contacts);
    }

    [Fact]
    public void Albums_ini_maps_album_id_to_name()
    {
        var content = """
            [6bb598f172a0e6d4]
            name=Vacation 2019
            date=2019-06-01

            [9988776655443322]
            name=Family
            """;

        var albums = PicasaAlbumsParser.Parse(content);

        Assert.Equal(2, albums.Count);
        Assert.Equal("Vacation 2019", albums["6bb598f172a0e6d4"]);
        Assert.Equal("Family", albums["9988776655443322"]);
    }
}
