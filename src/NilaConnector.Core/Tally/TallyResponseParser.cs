using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NilaConnector.Core.Models;

namespace NilaConnector.Core.Tally;

internal static class TallyResponseParser
{
    public static async Task<TallyVoucherResult> ParseVoucherImportAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ThrowLineError(document);
        ValidateOptionalStatus(document);

        var created = RequiredCounter(document, "CREATED");
        var errors = RequiredCounter(document, "ERRORS");
        var lastVoucherId = RequiredCounter(document, "LASTVCHID");

        if (created != 1 || errors != 0 || lastVoucherId <= 0)
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally did not create exactly one voucher (created={created}, errors={errors}, last_voucher_id={lastVoucherId}).");
        }

        return new TallyVoucherResult(created, errors, lastVoucherId);
    }

    public static async Task<IReadOnlyList<TallyCompany>> ParseOpenCompaniesAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ValidateApplicationStatus(document);

        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("COMPANY", StringComparison.OrdinalIgnoreCase))
            .Select(ParseCompany)
            .Where(company => company is not null)
            .Cast<TallyCompany>()
            .DistinctBy(company => (company.Name, company.TallyGuid), CompanyIdentityComparer.Instance)
            .OrderBy(company => company.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<string?> ParseCompanyGstinAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ValidateApplicationStatus(document);

        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("GSTREGNUMBER", StringComparison.OrdinalIgnoreCase))
            .Select(element => Normalize(element.Value))
            .FirstOrDefault(value => value is not null);
    }

    public static async Task<TallyVoucherBatchResult> ParseVoucherBatchImportAsync(
        Stream response,
        int maxCharacters,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var document = await LoadDocumentAsync(response, maxCharacters, cancellationToken);
        ThrowLineError(document);
        ValidateOptionalStatus(document);

        var result = new TallyVoucherBatchResult(
            RequiredCounter(document, "CREATED"),
            RequiredCounter(document, "ALTERED"),
            RequiredCounter(document, "COMBINED"),
            RequiredCounter(document, "IGNORED"),
            RequiredCounter(document, "ERRORS"),
            RequiredCounter(document, "EXCEPTIONS"),
            OptionalCounter(document, "LASTVCHID"));

        var accounted = result.Created + result.Altered + result.Combined + result.Ignored;
        if (result.Errors != 0 || result.Exceptions != 0 || accounted != expectedCount)
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally did not account for the complete voucher batch (expected={expectedCount}, accounted={accounted}, errors={result.Errors}, exceptions={result.Exceptions}).");
        }

        return result;
    }

    private static async Task<XDocument> LoadDocumentAsync(
        Stream response,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = maxCharacters,
        };

        try
        {
            using var streamReader = new StreamReader(
                response,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: true);
            var rawXml = await streamReader.ReadToEndAsync(cancellationToken);
            var sanitizedXml = RemoveInvalidXmlCharacters(rawXml);
            using var textReader = new StringReader(sanitizedXml);
            using var reader = XmlReader.Create(textReader, settings);
            return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
        }
        catch (XmlException exception)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                "Tally returned malformed or unsafe XML.",
                exception);
        }
    }

    private static string RemoveInvalidXmlCharacters(string value)
    {
        value = Regex.Replace(
            value,
            @"&#(?:(?<hex>[xX][0-9A-Fa-f]+)|(?<dec>[0-9]+));",
            match => InvalidNumericCharacterReference(match) ? string.Empty : match.Value,
            RegexOptions.CultureInvariant);

        if (value.All(XmlConvert.IsXmlChar))
        {
            return value;
        }

        return string.Concat(value.Where(XmlConvert.IsXmlChar));
    }

    private static bool InvalidNumericCharacterReference(Match match)
    {
        var hex = match.Groups["hex"].Value;
        int codePoint;
        var parsed = hex.Length > 0
            ? int.TryParse(hex[1..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint)
            : int.TryParse(match.Groups["dec"].Value, CultureInfo.InvariantCulture, out codePoint);

        return !parsed || !IsValidXmlCodePoint(codePoint);
    }

    private static bool IsValidXmlCodePoint(int value) =>
        value is 0x9 or 0xA or 0xD ||
        value is >= 0x20 and <= 0xD7FF ||
        value is >= 0xE000 and <= 0xFFFD ||
        value is >= 0x10000 and <= 0x10FFFF;

    private static int RequiredCounter(XDocument document, string name)
    {
        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (values.Length != 1 || !int.TryParse(values[0], out var value))
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned an invalid {name} import counter.");
        }

        return value;
    }

    private static int OptionalCounter(XDocument document, string name)
    {
        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (values.Length == 0)
        {
            return 0;
        }

        if (values.Length != 1 || !int.TryParse(values[0], out var value) || value < 0)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned an invalid {name} import counter.");
        }

        return value;
    }

    private static void ValidateOptionalStatus(XDocument document)
    {
        var statuses = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (statuses.Length > 1 || (statuses.Length == 1 && statuses[0] != "1"))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                "Tally returned a failed voucher import status.");
        }
    }

    private static void ThrowLineError(XDocument document)
    {
        var lineError = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(lineError))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally rejected the request: {Truncate(lineError.Trim(), 300)}");
        }
    }

    private static TallyCompany? ParseCompany(XElement element)
    {
        var name = AttributeValue(element, "NAME") ?? DescendantValue(element, "NAME");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var tallyGuid = AttributeValue(element, "GUID") ?? DescendantValue(element, "GUID");
        var gstin = AttributeValue(element, "GSTIN") ?? DescendantValue(element, "GSTIN");
        return new TallyCompany(name.Trim(), Normalize(tallyGuid), Normalize(gstin));
    }

    private static void ValidateApplicationStatus(XDocument document)
    {
        var lineError = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("LINEERROR", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        if (!string.IsNullOrWhiteSpace(lineError))
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally rejected the request: {Truncate(lineError.Trim(), 300)}");
        }

        var statuses = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .ToArray();

        if (statuses.Length != 1)
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                "Tally returned an invalid response envelope.");
        }

        if (statuses[0] is "0" or "-1")
        {
            throw new TallyClientException(
                TallyErrorKind.ApplicationError,
                $"Tally returned application status {statuses[0]}.");
        }

        if (statuses[0] != "1")
        {
            throw new TallyClientException(
                TallyErrorKind.InvalidResponse,
                $"Tally returned invalid status {Truncate(statuses[0], 20)}.");
        }
    }

    private static string? AttributeValue(XElement element, string name) => element
        .Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value;

    private static string? DescendantValue(XElement element, string name) => element
        .Descendants()
        .FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?.Value;

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed class CompanyIdentityComparer : IEqualityComparer<(string Name, string? TallyGuid)>
    {
        public static CompanyIdentityComparer Instance { get; } = new();

        public bool Equals((string Name, string? TallyGuid) x, (string Name, string? TallyGuid) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.TallyGuid, y.TallyGuid);

        public int GetHashCode((string Name, string? TallyGuid) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
            value.TallyGuid is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.TallyGuid));
    }

}
