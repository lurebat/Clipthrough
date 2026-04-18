namespace Clipthrough.Models;

public sealed record OcrCoverage(long EligibleTotal, long Succeeded, long Pending, long Running, long Failed)
{
    public double FractionReady => EligibleTotal <= 0 ? 1.0 : (double)Succeeded / EligibleTotal;
}
