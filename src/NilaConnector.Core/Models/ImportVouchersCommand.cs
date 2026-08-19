namespace NilaConnector.Core.Models;

public sealed record ImportVouchersCommand(
    string Company,
    string Xml,
    int ExpectedCount);
