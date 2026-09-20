using System.Xml.Linq;

namespace Curato.Picasa;

/// <summary>
/// Parses a Picasa `contacts.xml` (historically at
/// `%LocalAppData%\Google\Picasa2\contacts\contacts.xml` on the machine
/// Picasa ran on) into a contactId -&gt; display name map. Without a copy of
/// this file somewhere the import can reach, faces still import with their
/// box intact and PersonId left null (§14's existing "unassigned" state) -
/// this file only ever adds names on top of boxes that already work without it.
/// </summary>
public static class PicasaContactsParser
{
    public static IReadOnlyDictionary<string, string> Parse(string xmlContent)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xmlContent);
        }
        catch (System.Xml.XmlException)
        {
            return result;
        }

        foreach (var contact in doc.Descendants("contact"))
        {
            var id = (string?)contact.Attribute("id");
            var name = (string?)contact.Attribute("name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                result[id] = name;
            }
        }

        return result;
    }
}
