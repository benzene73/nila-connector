namespace NilaConnector.Core.Models;

public sealed record TallyVoucherBatchResult(
    int Created,
    int Altered,
    int Combined,
    int Ignored,
    int Errors,
    int Exceptions,
    long LastVoucherId);
