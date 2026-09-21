using System.Xml;
using System.Xml.Linq;

namespace DataLoader.NGX;

/// <summary>
/// XML plumbing shared by both readers.
///
/// <para>
/// <b>Element lookup is by LOCAL NAME, ignoring the namespace.</b> Both documents
/// declare <c>xmlns="http://www.ngx.com/Clearing"</c>, so a namespace-qualified lookup
/// would work today — but it would also turn a vendor namespace bump into every column
/// silently going NULL, with a 200 response and a plausible-looking row count. Matching
/// on the local name degrades to "still works" instead.
/// </para>
/// <para>
/// <b>Records are streamed, not loaded whole.</b> Each reader walks its response with an
/// <see cref="XmlReader"/> and materialises ONE record element at a time via
/// <see cref="XNode.ReadFrom"/>. A calendar month of strip trades is 29 MB of XML and
/// ~36,000 records; an <c>XDocument</c> of that is several hundred megabytes of object
/// graph, multiplied by however many work units run concurrently. Streaming keeps the
/// footprint at one record regardless of window size.
/// </para>
/// <para>
/// ⚠ There is deliberately NO shared "stream me the records" helper here. Both
/// <see cref="XNode.ReadFrom"/> and <c>ReadElementContentAsString</c> leave the reader
/// on the NEXT node, so the enclosing loop must advance only when it has not already
/// consumed — a plain <c>while (reader.Read())</c> around either silently drops every
/// second sibling record. A helper that hid that loop behind an
/// <see cref="IEnumerable{T}"/> existed here and had exactly that bug; it is gone rather
/// than fixed, because the subtlety belongs next to the code that depends on it. See the
/// loops in <c>NgxIndexPriceReader.ParsePageCore</c> and
/// <c>NgxStripReader.ParseDocumentCore</c>.
/// </para>
/// </summary>
internal static class NgxXml
{
    /// <summary>The direct child with this local name, or null.</summary>
    internal static XElement? Child(this XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));

    /// <summary>
    /// The trimmed text of the direct child with this local name, or null when the
    /// element is absent OR present-but-blank. Blank collapses to null on purpose: the
    /// vendor emits self-closing and whitespace-only elements, and an empty string
    /// would land as '' in a VARCHAR column, which reads as "known to be blank" rather
    /// than "not supplied".
    /// </summary>
    internal static string? Text(this XElement parent, string localName)
    {
        var value = parent.Child(localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// The trimmed text of a grandchild, e.g. <c>price/amount</c>. Null if either level
    /// is missing.
    /// </summary>
    internal static string? Text(this XElement parent, string localName, string childLocalName) =>
        parent.Child(localName)?.Text(childLocalName);

    /// <summary>
    /// Reader settings used for every response: no DTD processing and no external
    /// entity resolution, so a hostile or merely malformed document cannot turn into an
    /// XXE fetch or a billion-laughs expansion.
    /// </summary>
    internal static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        Async = false
    };
}
