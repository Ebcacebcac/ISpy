using System.Globalization;
using System.Xml.Linq;

namespace ISpy.Core.Protocol;

/// <summary>
/// Namespace-agnostic XML helpers.
/// </summary>
/// <remarks>
/// Every OEM badge of this hardware picks its own XML namespace - stock Hikvision uses
/// <c>http://www.hikvision.com/ver20/XMLSchema</c>, others use a PSIA urn, and some firmware emits
/// no namespace at all. Matching on local names means one parser handles all of them instead of
/// silently returning nothing for a device we have not seen before.
/// </remarks>
public static class Xml
{
    // The Local suffix is load-bearing: XElement already has Elements(XName)/Descendants(XName),
    // and string converts implicitly to XName, so an extension named Elements or Descendants would
    // silently never be called - it would bind to the namespace-sensitive instance method instead.

    public static IEnumerable<XElement> ElementsLocal(this XElement element, string localName) =>
        element.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<XElement> DescendantsLocal(this XElement element, string localName) =>
        element.Descendants().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    public static XElement? ChildLocal(this XElement element, string localName) =>
        element.ElementsLocal(localName).FirstOrDefault();

    /// <summary>First descendant with this local name, at any depth.</summary>
    public static XElement? FindLocal(this XElement element, string localName) =>
        element.DescendantsLocal(localName).FirstOrDefault();

    public static string? Value(this XElement element, string localName)
    {
        var value = element.FindLocal(localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static int? Int(this XElement element, string localName) =>
        int.TryParse(element.Value(localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    public static bool? Bool(this XElement element, string localName) => element.Value(localName) switch
    {
        null => null,
        var v when v.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
        var v when v.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
        var v when v == "1" => true,
        var v when v == "0" => false,
        _ => null,
    };

    /// <summary>Parses a document, returning null instead of throwing on malformed input.</summary>
    public static XElement? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return XDocument.Parse(text, LoadOptions.None).Root;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
