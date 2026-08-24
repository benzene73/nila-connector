namespace NilaConnector.Core.Tally.Diagnostics;

public sealed record DateRange
{
    public DateRange(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException("The end date cannot precede the start date.", nameof(to));
        }

        From = from;
        To = to;
    }

    public DateOnly From { get; }

    public DateOnly To { get; }
}
