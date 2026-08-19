using System.Xml;
using System.Xml.Linq;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

internal static class TallyRequestValidator
{
    private const int MaxRequestCharacters = 750_000;

    public static void ValidateVoucherImport(ImportVouchersCommand command)
    {
        if (command.Xml.Length == 0 || command.Xml.Length > MaxRequestCharacters)
        {
            throw new InvalidDataException("The voucher import XML has an invalid size.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRequestCharacters,
        };

        XDocument document;
        try
        {
            using var text = new StringReader(command.Xml);
            using var reader = XmlReader.Create(text, settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The voucher import contains malformed or unsafe XML.", exception);
        }

        RequireSingleValue(document, "TALLYREQUEST", "Import");
        RequireSingleValue(document, "TYPE", "Data");
        RequireSingleValue(document, "ID", "Vouchers");
        RequireSingleValue(document, "SVCURRENTCOMPANY", command.Company);

        var messages = Elements(document, "TALLYMESSAGE").ToArray();
        if (messages.Length != command.ExpectedCount ||
            messages.Any(message => Elements(message, "VOUCHER").Count() != 1))
        {
            throw new InvalidDataException("The voucher import XML does not match its expected voucher count.");
        }
    }

    private static void RequireSingleValue(XContainer document, string name, string expected)
    {
        var values = Elements(document, name).Select(element => element.Value.Trim()).ToArray();
        if (values.Length != 1 || !string.Equals(values[0], expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The voucher import XML has an invalid {name} value.");
        }
    }

    private static IEnumerable<XElement> Elements(XContainer container, string name) =>
        container.Descendants().Where(element =>
            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
}
