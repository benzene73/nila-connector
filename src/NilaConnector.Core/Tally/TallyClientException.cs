namespace NilaConnector.Core.Tally;

public sealed class TallyClientException : Exception
{
    public TallyClientException(TallyErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public TallyErrorKind Kind { get; }
}
