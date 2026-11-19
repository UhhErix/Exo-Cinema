using ExoCinema.Mpv;

namespace ExoCinema.Tests;

/// <summary>The read-ahead and read-behind sized from the memory free now, at 8, 16, 32 and 64 GiB PCs.</summary>
public sealed class ReadAheadTests
{
    private const long Gib = ReadAhead.Gib;

    // Free memory with a normal desktop open (about 60% of the total) and under heavy use (about 10%).
    [Theory]
    [InlineData(8, 4.8, 2.4, 0.6)]
    [InlineData(16, 9.6, 4.8, 1.2)]
    [InlineData(32, 19.2, 9.6, 2.4)]
    [InlineData(64, 38.4, 19.2, 4.8)]
    public void Free_memory_is_shared_half_ahead_and_a_quarter_of_that_behind(int totalGb, double freeGb, double forwardGb, double backGb)
    {
        var plan = ReadAhead.Size(totalGb * Gib, (long)(freeGb * Gib));
        Assert.False(plan.Low);
        // Within a MiB: the shares are fractions of bytes.
        Assert.InRange(plan.ForwardBytes, (long)(forwardGb * Gib) - ReadAhead.Mib, (long)(forwardGb * Gib) + ReadAhead.Mib);
        Assert.InRange(plan.BackBytes, (long)(backGb * Gib) - ReadAhead.Mib, (long)(backGb * Gib) + ReadAhead.Mib);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Little_free_memory_drops_to_the_floors(int totalGb)
    {
        var total = totalGb * Gib;
        // Under 15% free: short.
        var plan = ReadAhead.Size(total, (long)(total * 0.10));
        Assert.True(plan.Low);
        Assert.Equal(ReadAhead.Floor, plan.ForwardBytes);
        Assert.Equal(ReadAhead.MinBack, plan.BackBytes);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Exactly_15_percent_free_is_not_short(int totalGb)
    {
        var total = totalGb * Gib;
        // Rounded up: a whole byte under the line is short, the line itself is not.
        var plan = ReadAhead.Size(total, (long)Math.Ceiling(total * ReadAhead.LowShare));
        Assert.False(plan.Low);
        Assert.True(plan.ForwardBytes >= ReadAhead.Floor);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void Read_ahead_never_falls_below_the_floor_and_read_behind_never_below_its_least(int totalGb)
    {
        var total = totalGb * Gib;
        foreach (var free in new[] { 0L, 1L, 100 * ReadAhead.Mib, total / 20, total / 4, total })
        {
            var plan = ReadAhead.Size(total, free);
            Assert.True(plan.ForwardBytes >= ReadAhead.Floor, $"{totalGb} GiB, {free} free");
            Assert.True(plan.BackBytes >= ReadAhead.MinBack, $"{totalGb} GiB, {free} free");
        }
    }

    [Fact]
    public void More_free_memory_never_gives_less_read_ahead()
    {
        long previous = 0;
        for (var free = 0L; free <= 64 * Gib; free += Gib / 2)
        {
            var plan = ReadAhead.Size(64 * Gib, free);
            Assert.True(plan.ForwardBytes >= previous);
            previous = plan.ForwardBytes;
        }
    }

    [Fact]
    public void Unknown_total_sizes_from_what_is_free()
    {
        var plan = ReadAhead.Size(0, 8 * Gib);
        Assert.False(plan.Low);
        Assert.Equal(4 * Gib, plan.ForwardBytes);
    }

    [Fact]
    public void Windows_reports_the_memory_it_has()
    {
        var (total, available) = SystemMemory.Read();
        Assert.True(total > 0);
        Assert.InRange(available, 0, total);
    }
}
