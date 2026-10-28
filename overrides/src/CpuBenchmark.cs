using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace BatteryDiagnostic;

// Optional, user-initiated single-thread CPU timing test. This is not a CPU health test.
internal static class CpuBenchmark
{
    private const int Iterations = 100_000_000;

    public static Task<double> RunAsync(CancellationToken token = default)
        => RunWithIterationsAsync(Iterations, token);

    // Small iteration counts are used only in deterministic/fast self-tests.
    internal static Task<double> RunWithIterationsAsync(int iterations, CancellationToken token = default)
    {
        if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            _ = Calculate(Math.Min(iterations, 100_000), token); // warm up JIT
            var measurements = new double[3];
            long expected = 3L * (iterations / 2) + (iterations % 2);

            for (int i = 0; i < measurements.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                long start = Stopwatch.GetTimestamp();
                long sum = Calculate(iterations, token);
                measurements[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (sum != expected)
                    throw new InvalidOperationException("CPU benchmark calculation verification failed.");
            }

            Array.Sort(measurements);
            return measurements[1]; // median; lower is faster on the same workload
        }, token);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Calculate(int count, CancellationToken token)
    {
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            // Respond to Stop while a pass is running, not just between passes.
            if ((i & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
            sum += (i & 1) + 1;
        }
        return sum;
    }

    internal static void SelfTest()
    {
        if (Calculate(0, CancellationToken.None) != 0 ||
            Calculate(5, CancellationToken.None) != 7 ||
            Calculate(10, CancellationToken.None) != 15)
            throw new Exception("CPU benchmark arithmetic self-test failed.");
        double median = RunWithIterationsAsync(1_000, CancellationToken.None).GetAwaiter().GetResult();
        if (!double.IsFinite(median) || median < 0)
            throw new Exception("CPU benchmark median self-test failed.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            _ = RunWithIterationsAsync(1_000, canceled.Token).GetAwaiter().GetResult();
            throw new Exception("CPU benchmark cancellation self-test failed.");
        }
        catch (OperationCanceledException) { }
    }
}
