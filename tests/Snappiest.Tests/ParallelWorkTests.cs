using Snappiest.Internal;

namespace Snappiest.Tests;

/// <summary>
/// The work loop behind the parallel paths: every index runs once, and a failing body stops the others.
/// </summary>
public class ParallelWorkTests
{
    [Test]
    [Arguments(1, 1)]
    [Arguments(2, 7)]
    [Arguments(5, 100)]
    [Arguments(16, 3)]
    public async Task For_EveryIndexOnce(int threads, int count)
    {
        int[] runs = new int[count];
        ParallelWork.For(count, threads, i => Interlocked.Increment(ref runs[i]));

        await Assert.That(runs.All(r => r == 1)).IsTrue();
    }

    [Test]
    public async Task StartAndJoin_CallerWorksToo()
    {
        int[] runs = new int[1000];
        var threads = new HashSet<int>();
        ParallelWork work = ParallelWork.Start(runs.Length, 4, i =>
        {
            Interlocked.Increment(ref runs[i]);
            lock (threads)
            {
                threads.Add(Environment.CurrentManagedThreadId);
            }

            Thread.SpinWait(1000);
        });

        work.Join();

        await Assert.That(runs.All(r => r == 1)).IsTrue();
        await Assert.That(threads).Contains(Environment.CurrentManagedThreadId);
    }

    [Test]
    public async Task For_BodyThrows_AggregateExceptionAndTheRestStops()
    {
        int started = 0;
        var exception = await Assert.That(() => ParallelWork.For(100_000, 4, i =>
        {
            Interlocked.Increment(ref started);
            if (i == 5)
            {
                throw new InvalidOperationException("boom");
            }

            Thread.SpinWait(100);
        })).Throws<AggregateException>();

        await Assert.That(exception!.InnerExceptions.Any(e => e is InvalidOperationException { Message: "boom" })).IsTrue();
        await Assert.That(started).IsLessThan(100_000);
    }

    [Test]
    public async Task Abandon_WaitsForRunningWorkersAndSwallowsTheirErrors()
    {
        int running = 0;
        int maxRunning = 0;
        ParallelWork work = ParallelWork.Start(10_000, 4, i =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref maxRunning)) && Interlocked.CompareExchange(ref maxRunning, now, seen) != seen)
            {
            }

            Thread.Sleep(1);
            Interlocked.Decrement(ref running);
            if (i == 2)
            {
                throw new InvalidOperationException();
            }
        });

        // Wait until a worker is running (a fixed sleep failed when other tests kept the pool busy)
        SpinWait.SpinUntil(() => Volatile.Read(ref maxRunning) >= 1, TimeSpan.FromSeconds(10));
        work.Abandon();

        await Assert.That(running).IsEqualTo(0);
        await Assert.That(maxRunning).IsGreaterThanOrEqualTo(1);
    }
}
