namespace NilaConnector.Core.Models;

public sealed record GstSalesLedgerSettings(
    string HsnSac, string Description, bool Services, decimal Rate,
    GstSalesTreatment Treatment = GstSalesTreatment.Domestic);
