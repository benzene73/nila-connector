namespace NilaConnector.Core.Models;

public sealed record CreateVoucherCommand(
    string JobKey,
    string Company,
    DateOnly Date,
    string VoucherType,
    string DebitLedger,
    string CreditLedger,
    decimal Amount,
    string Narration);
