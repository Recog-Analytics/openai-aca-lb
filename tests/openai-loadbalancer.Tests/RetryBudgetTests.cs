using openai_loadbalancer.Health;

namespace openai_loadbalancer.Tests;

public class RetryBudgetTests
{
    [Fact]
    public void AllowsOneHundredRetriesInWindowWithLowTraffic()
    {
        var budget = new RetryBudget(new TestClock());
        budget.RecordRequest();

        for (var i = 0; i < 100; i++)
            Assert.True(budget.TryAcquireRetry());

        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void AllowsTwentyPercentOfRequestsWhenAboveFloor()
    {
        var budget = new RetryBudget(new TestClock());
        for (var i = 0; i < 1004; i++)
            budget.RecordRequest();

        for (var i = 0; i < 200; i++)
            Assert.True(budget.TryAcquireRetry());

        Assert.False(budget.TryAcquireRetry());
        budget.RecordRequest();
        Assert.True(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void RetriesDoNotIncreaseRequestCount()
    {
        var budget = new RetryBudget(new TestClock());
        for (var i = 0; i < 500; i++)
            budget.RecordRequest();
        for (var i = 0; i < 100; i++)
            Assert.True(budget.TryAcquireRetry());

        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void RetryCreditsExpireAtTenSeconds()
    {
        var clock = new TestClock();
        var budget = new RetryBudget(clock);
        for (var i = 0; i < 100; i++)
            Assert.True(budget.TryAcquireRetry());

        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(budget.TryAcquireRetry());
    }

    [Fact]
    public void SlidingWindowExpiresOnlyOldRetries()
    {
        var clock = new TestClock();
        var budget = new RetryBudget(clock);
        for (var i = 0; i < 50; i++)
            Assert.True(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50; i++)
            Assert.True(budget.TryAcquireRetry());

        clock.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50; i++)
            Assert.True(budget.TryAcquireRetry());
        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void ExpiredRequestsLowerLimitEvenWhileRetriesRemain()
    {
        var clock = new TestClock();
        var budget = new RetryBudget(clock);
        for (var i = 0; i < 1000; i++)
            budget.RecordRequest();
        clock.Advance(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 150; i++)
            Assert.True(budget.TryAcquireRetry());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(budget.TryAcquireRetry());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(budget.TryAcquireRetry());
    }

    [Fact]
    public void ConcurrentCallsCannotExceedBudgetOrLoseRequests()
    {
        var budget = new RetryBudget(new TestClock());
        Parallel.For(0, 2000, _ => budget.RecordRequest());
        var acquired = 0;

        Parallel.For(0, 1000, _ =>
        {
            if (budget.TryAcquireRetry())
                Interlocked.Increment(ref acquired);
        });

        Assert.Equal(400, acquired);
        Assert.False(budget.TryAcquireRetry());
    }

    [Fact]
    public void InstancesKeepIndependentBudgets()
    {
        var clock = new TestClock();
        var first = new RetryBudget(clock);
        var second = new RetryBudget(clock);
        for (var i = 0; i < 100; i++)
            Assert.True(first.TryAcquireRetry());

        Assert.False(first.TryAcquireRetry());
        Assert.True(second.TryAcquireRetry());
    }
}
