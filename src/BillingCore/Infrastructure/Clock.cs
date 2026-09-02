namespace BillingCore.Infrastructure;

public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>
/// Virtual clock: real time plus a demo-controlled offset. Workers make all
/// scheduling decisions against this clock so the demo can advance days in seconds
/// while the machinery (trigger, ladders, sweeps) genuinely ticks.
/// </summary>
public sealed class VirtualClock : IClock
{
    private long _offsetTicks;

    public DateTime UtcNow => DateTime.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public void AdvanceTo(DateTime targetUtc)
    {
        var delta = targetUtc - UtcNow;
        if (delta > TimeSpan.Zero) Advance(delta);
    }

    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
}
