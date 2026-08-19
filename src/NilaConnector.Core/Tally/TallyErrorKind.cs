namespace NilaConnector.Core.Tally;

public enum TallyErrorKind
{
    Unreachable,
    Timeout,
    HttpError,
    ApplicationError,
    ResponseTooLarge,
    InvalidResponse,
}
