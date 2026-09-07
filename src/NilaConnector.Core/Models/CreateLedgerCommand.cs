namespace NilaConnector.Core.Models;

public sealed record CreateLedgerCommand(
    string Company,
    string Name,
    string Parent,
    string Gstin,
    string RegistrationType,
    string State,
    string Country,
    DateOnly ApplicableFrom,
    GstSalesLedgerSettings? SalesGst = null);
