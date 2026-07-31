namespace Reliant.Tests.TestHelpers;

public class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset Current { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Current;

    public void Advance(TimeSpan span) => Current += span;

    public void SetUtcNow(DateTimeOffset value) => Current = value;
}
