using System.Xml;
using System.Xml.Linq;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

internal static class TallyRequestValidator
{
    private const int MaxRequestCharacters = 750_000;

    public static void ValidateVoucherImport(ImportVouchersCommand command)
    {
        _ = ReadSingleVoucherFingerprint(command, requireSingleVoucher: false);
    }

    public static VoucherImportFingerprint ReadSingleVoucherFingerprint(
        ImportVouchersCommand command,
        VoucherNumberMode numberMode = VoucherNumberMode.Source) =>
        ReadSingleVoucherFingerprint(command, requireSingleVoucher: true, numberMode)
        ?? throw new InvalidDataException("The voucher import must contain exactly one voucher for recovery.");

    private static VoucherImportFingerprint? ReadSingleVoucherFingerprint(
        ImportVouchersCommand command,
        bool requireSingleVoucher,
        VoucherNumberMode numberMode = VoucherNumberMode.Source)
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

        if (!requireSingleVoucher)
        {
            return null;
        }

        if (command.ExpectedCount != 1 || messages.Length != 1)
        {
            throw new InvalidDataException("The voucher import must contain exactly one voucher for recovery.");
        }

        var voucher = Elements(messages[0], "VOUCHER").Single();
        var correlationId = numberMode switch
        {
            VoucherNumberMode.Source => RequireVoucherValue(voucher, "GUID", 200),
            VoucherNumberMode.Tally => RequireDirectVoucherValue(voucher, "REMOTEALTGUID", 100),
            _ => throw new InvalidDataException("The voucher number mode is invalid."),
        };
        var semantic = VoucherSemanticFingerprintBuilder.FromVoucher(
            voucher,
            requireVoucherNumber: numberMode == VoucherNumberMode.Source);
        return new VoucherImportFingerprint(numberMode, correlationId, semantic);
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

    private static string RequireVoucherValue(XContainer voucher, string name, int maximumLength)
    {
        var values = Elements(voucher, name).Select(element => element.Value.Trim()).ToArray();
        if (values.Length != 1 || values[0].Length == 0 || values[0].Length > maximumLength)
        {
            throw new InvalidDataException($"The voucher import XML has an invalid {name} value.");
        }

        return values[0];
    }

    private static string RequireDirectVoucherValue(
        XElement voucher,
        string name,
        int maximumLength)
    {
        var values = voucher.Elements()
            .Where(value => value.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Value.Trim())
            .ToArray();

        if (values.Length != 1 || values[0].Length == 0 || values[0].Length > maximumLength)
        {
            throw new InvalidDataException($"The voucher import XML has an invalid {name} value.");
        }

        return values[0];
    }
}

internal enum VoucherNumberMode
{
    Source,
    Tally,
}

internal sealed record VoucherImportFingerprint(
    VoucherNumberMode NumberMode,
    string CorrelationId,
    VoucherSemanticFingerprint Semantic)
{
    public DateOnly Date => Semantic.Date;
    public string VoucherType => Semantic.VoucherType;
    public string? VoucherNumber => Semantic.VoucherNumber;
}
