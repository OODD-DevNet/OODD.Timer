using System.Globalization;
using System.Runtime;
using OODD.Timer;

namespace OODD.Timer.Benchmark;

internal static class GcProbe
{
    private const int WarmupCallbacks = 800;
    private const int MeasuredCallbacks = 2_000;
    private const long NoGcBudget = 64L * 1024 * 1024;

    private enum GcPolicy { Default, SustainedLowLatency, NoGCRegion }

    public static int Run(string[] args)
    {
        int processor = 0;
        ushort group = 0;
        if (args.Length > 3
            || (args.Length > 1 && (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out processor) || processor < 0))
            || (args.Length > 2 && !ushort.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out group)))
        {
            Console.Error.WriteLine("Usage: --gc-probe [processorIndex (>=0)] [processorGroup (0..65535)]");
            return 2;
        }

        try
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
            {
                Console.Error.WriteLine("GC probe cannot run inside an existing process-wide NoGCRegion; that scope was not changed.");
                return 1;
            }

            int? processorIndex = args.Length > 1 ? processor : null;
            Console.WriteLine($"# GC probe: {HiResClock.DescribePlatform()}");
            Console.WriteLine($"# period=1ms, mode=Spin, priority=Highest, warmup={WarmupCallbacks}, samples={MeasuredCallbacks}");
            Console.WriteLine($"# serverGC={GCSettings.IsServerGC}, default latency mode={GCSettings.LatencyMode}");
            Console.WriteLine("# Empty workload uses Callback only; instrumentation remains. No Elapsed subscription.");
            Console.WriteLine("# After warmup and GC setup, one callback captures the allocation baseline; the next 2000 callbacks are measured.");
            Console.WriteLine("# Worker: up to 16 pairs of 8KiB arrays per Sleep(1), <=48MiB allocated including warmup; allocation gated to the sample window after worker warmup.");
            Console.WriteLine("# Worker window bytes can include one in-flight pair at window close; the hard allocation cap still applies.");
            Console.WriteLine("# NoGCRegion: process-wide, 64MiB, disallowFullBlockingGC=true, entered only after warmup and ended in cleanup.");
            Console.WriteLine("# GC counts are process-wide sample-window deltas; zero callback-thread allocation is NOT GC immunity.");
            Console.WriteLine("# Measurements have no performance pass thresholds or hard-realtime guarantees; zero collections provide no evidence of GC pause effects.");

            if (!RunScenario(null, 0, false, GcPolicy.Default))
            {
                return 1;
            }

            if (processorIndex.HasValue && !RunScenario(processorIndex, group, false, GcPolicy.Default))
            {
                return 1;
            }

            foreach (GcPolicy policy in new[] { GcPolicy.Default, GcPolicy.SustainedLowLatency, GcPolicy.NoGCRegion })
            {
                if (!RunScenario(null, 0, true, policy)
                    || (processorIndex.HasValue && !RunScenario(processorIndex, group, true, policy)))
                {
                    return 1;
                }
            }

            if (!processorIndex.HasValue)
            {
                Console.WriteLine("# Bound scenarios skipped: supply a processor index (and optional group) to compare affinity.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"GC probe error: {exception}");
            return 1;
        }
    }

    private static bool RunScenario(int? processorIndex, ushort processorGroup, bool pressure, GcPolicy policy)
    {
        string affinity = processorIndex.HasValue ? $"bound group={processorGroup}/cpu={processorIndex}" : "unbound";
        Console.WriteLine($"\n## {affinity}; {(pressure ? "bounded allocation worker" : "empty workload")}; requested GC={policy}");
        var errors = new List<Exception>();
        var worker = pressure ? new AllocationWorker() : null;
        var samples = new Samples(worker);
        HighResolutionTimer? timer = null;
        GCLatencyMode previousMode = GCSettings.LatencyMode;
        GCLatencyMode effectiveMode = previousMode;
        bool ownsNoGcRegion = false;
        bool changedLatencyMode = false;
        string gcStatus = policy == GcPolicy.NoGCRegion ? "not attempted" : "unchanged";
        long callbackErrors = 0;

        try
        {
            if (previousMode == GCLatencyMode.NoGCRegion)
            {
                throw new InvalidOperationException("An existing NoGCRegion belongs to another scope; refusing to nest or change it.");
            }

            worker?.StartAndWarm();
            timer = new HighResolutionTimer(new HighResolutionTimerOptions
            {
                Period = TimeSpan.FromMilliseconds(1),
                Mode = WaitMode.Spin,
                AutoStart = false,
                ProcessorIndex = processorIndex,
                ProcessorGroup = processorGroup,
                Callback = samples.OnCallback,
            });
            samples.Timer = timer;
            timer.Start();
            if (!WaitForSignal(ref samples.Warmed, 10_000))
            {
                throw new TimeoutException("Timer warmup did not finish within 10 seconds.");
            }

            if (policy == GcPolicy.SustainedLowLatency)
            {
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                changedLatencyMode = true;
                gcStatus = "SustainedLowLatency requested";
            }
            else if (policy == GcPolicy.NoGCRegion)
            {
                ownsNoGcRegion = GC.TryStartNoGCRegion(totalSize: NoGcBudget, disallowFullBlockingGC: true);
                gcStatus = ownsNoGcRegion ? "entered" : "not entered (request denied; measuring with unchanged GC mode)";
            }

            effectiveMode = GCSettings.LatencyMode;
            if (policy == GcPolicy.SustainedLowLatency && effectiveMode != GCLatencyMode.SustainedLowLatency)
            {
                throw new InvalidOperationException("The requested SustainedLowLatency mode was not applied.");
            }

            Volatile.Write(ref samples.Armed, 1);
            if (!WaitForSignal(ref samples.Completed, 10_000))
            {
                throw new TimeoutException("The 2000-callback sample window did not finish within 10 seconds.");
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            samples.Close();
            worker?.RequestStop();
            if (ownsNoGcRegion)
            {
                bool stillActive = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
                if (!stillActive)
                {
                    gcStatus = "entered, exited early";
                    errors.Add(new InvalidOperationException("NoGCRegion ended before probe cleanup."));
                }

                try
                {
                    GC.EndNoGCRegion();
                    gcStatus = stillActive ? "entered and ended" : gcStatus + "; end accepted";
                }
                catch (Exception exception)
                {
                    gcStatus += "; EndNoGCRegion failed";
                    errors.Add(exception);
                }
            }

            if (changedLatencyMode)
            {
                try
                {
                    if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                    {
                        errors.Add(new InvalidOperationException("Cannot restore latency mode inside a NoGCRegion not owned by this scenario."));
                    }
                    else
                    {
                        GCSettings.LatencyMode = previousMode;
                        gcStatus += "; restored previous mode";
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            try
            {
                if (timer is not null)
                {
                    try
                    {
                        timer.Stop();
                        callbackErrors = timer.GetStatistics().ErrorCount;
                    }
                    finally
                    {
                        timer.Dispose();
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }

            try
            {
                worker?.StopAndJoin();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        if (callbackErrors != 0)
        {
            errors.Add(new InvalidOperationException($"Timer reported {callbackErrors} callback errors (including warmup)."));
        }

        if (!samples.SameThread)
        {
            errors.Add(new InvalidOperationException("Callback thread changed; per-thread allocation deltas are invalid."));
        }

        Console.WriteLine($"GC status: {gcStatus}; effective mode at window start={effectiveMode}");
        samples.Print();
        if (worker is not null)
        {
            Console.WriteLine($"Worker allocated: warmup={worker.WarmupBytes}B, window={worker.WindowBytes}B, total={worker.TotalBytes}B, capReached={worker.CapReached}");
        }

        foreach (Exception error in errors)
        {
            Console.Error.WriteLine($"Probe error: {error.GetType().Name}: {error.Message}");
        }

        return errors.Count == 0;
    }

    private static bool WaitForSignal(ref int signal, int timeoutMilliseconds)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Volatile.Read(ref signal) == 0)
        {
            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }

    private sealed class Samples(AllocationWorker? worker)
    {
        private readonly double[] _schedulerNanoseconds = new double[MeasuredCallbacks];
        private readonly double[] _entryNanoseconds = new double[MeasuredCallbacks];
        private readonly int[] _gcBefore = new int[3];
        private readonly int[] _gcAfter = new int[3];
        private int _warmups;
        private int _count;
        private int _closed;
        private int _threadId;
        private bool _hasBaseline;
        private long _allocationBaseline;
        private long _allocatedBytes;

        public HighResolutionTimer Timer { get; set; } = null!;
        public bool SameThread { get; private set; } = true;
        public int Warmed;
        public int Armed;
        public int Completed;

        public void OnCallback()
        {
            long enteredAt = HiResClock.Now();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            if (Volatile.Read(ref _closed) != 0 || _count == MeasuredCallbacks)
            {
                return;
            }

            double schedulerLateness = Timer.LastLatenessNanoseconds;
            double entryLateness = HiResClock.ToNanoseconds(enteredAt - Timer.LastDueTimestamp);
            int threadId = Environment.CurrentManagedThreadId;
            if (_warmups < WarmupCallbacks)
            {
                _schedulerNanoseconds[0] = schedulerLateness;
                _entryNanoseconds[0] = entryLateness;
                ReadCollections(_gcBefore);
                if (++_warmups == WarmupCallbacks)
                {
                    Volatile.Write(ref Warmed, 1);
                }

                return;
            }

            if (Volatile.Read(ref Armed) == 0)
            {
                return;
            }

            if (!_hasBaseline)
            {
                ReadCollections(_gcBefore);
                _allocationBaseline = allocated;
                _threadId = threadId;
                _hasBaseline = true;
                worker?.OpenWindow();
                return;
            }

            SameThread &= threadId == _threadId;
            _schedulerNanoseconds[_count] = schedulerLateness;
            _entryNanoseconds[_count] = entryLateness;
            _allocatedBytes = allocated - _allocationBaseline;
            _count++;
            if (_count == MeasuredCallbacks)
            {
                worker?.CloseWindow();
                ReadCollections(_gcAfter);
                Volatile.Write(ref Completed, 1);
            }
        }

        public void Close()
        {
            Volatile.Write(ref _closed, 1);
            worker?.CloseWindow();
        }

        public void Print()
        {
            Console.WriteLine($"Samples: actual={_count}/{MeasuredCallbacks}, warmup={_warmups}/{WarmupCallbacks}, complete={Completed != 0}");
            if (_hasBaseline)
            {
                Console.WriteLine($"Callback-thread allocation: {_allocatedBytes}B across {_count} inter-callback intervals; sameThread={SameThread}");
            }
            else
            {
                Console.WriteLine("Callback-thread allocation: n/a (no baseline)");
            }

            if (Completed != 0)
            {
                Console.WriteLine($"GC.CollectionCount deltas: gen0={_gcAfter[0] - _gcBefore[0]}, gen1={_gcAfter[1] - _gcBefore[1]}, gen2={_gcAfter[2] - _gcBefore[2]}");
            }
            else
            {
                Console.WriteLine("GC.CollectionCount deltas: n/a (incomplete sample window)");
            }

            long missed = 0;
            long periodTicks = HiResClock.ToTicks(TimeSpan.FromMilliseconds(1));
            for (int i = 0; i < _count; i++)
            {
                // Recover integer scheduler ticks before division to avoid rounding exact-period misses down.
                long lateTicks = (long)Math.Round(_schedulerNanoseconds[i] / HiResClock.NanosecondsPerTick);
                missed += Math.Min(int.MaxValue, Math.Max(0, lateTicks / periodTicks));
            }

            Console.WriteLine($"Missed ticks (derived from sampled scheduler lateness): {missed}");
            PrintPercentiles("Scheduler lateness", _schedulerNanoseconds, _count);
            PrintPercentiles("Callback-entry minus LastDueTimestamp", _entryNanoseconds, _count);
        }

        private static void ReadCollections(int[] counts)
        {
            for (int generation = 0; generation < counts.Length; generation++)
            {
                counts[generation] = GC.CollectionCount(generation);
            }
        }
    }

    private static void PrintPercentiles(string label, double[] values, int count)
    {
        if (count == 0)
        {
            Console.WriteLine($"{label}: n/a");
            return;
        }

        Array.Sort(values, 0, count);
        double p50 = values[(int)Math.Ceiling(count * 0.50) - 1] / 1_000;
        double p99 = values[(int)Math.Ceiling(count * 0.99) - 1] / 1_000;
        double p999 = values[(int)Math.Ceiling(count * 0.999) - 1] / 1_000;
        Console.WriteLine(FormattableString.Invariant($"{label} (us): p50={p50:F3}, p99={p99:F3}, p99.9={p999:F3}, max={values[count - 1] / 1_000:F3}"));
    }

    private sealed class AllocationWorker
    {
        private const int BufferBytes = 8 * 1024;
        private const int PairsPerBatch = 16;
        private const long BudgetBytes = 48L * 1024 * 1024;
        private const int PairReservationBytes = 2 * (BufferBytes + 64);
        private readonly Thread _thread;
        private byte[]? _sink;
        private int _ready;
        private int _window;
        private int _stop;
        private bool _started;
        private Exception? _error;

        public AllocationWorker()
        {
            _thread = new Thread(Work) { IsBackground = true, Name = "GC probe bounded allocator" };
        }

        public long WarmupBytes { get; private set; }
        public long WindowBytes { get; private set; }
        public long TotalBytes { get; private set; }
        public bool CapReached { get; private set; }

        public void StartAndWarm()
        {
            _thread.Start();
            _started = true;
            if (!WaitForSignal(ref _ready, 5_000))
            {
                throw new TimeoutException("Allocation worker warmup did not finish within 5 seconds.");
            }

            if (_error is not null)
            {
                throw new InvalidOperationException("Allocation worker warmup failed.", _error);
            }
        }

        public void OpenWindow() => Volatile.Write(ref _window, 1);
        public void CloseWindow() => Volatile.Write(ref _window, 0);
        public void RequestStop() => Volatile.Write(ref _stop, 1);

        public void StopAndJoin()
        {
            RequestStop();
            if (_started && !_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Allocation worker did not stop within 5 seconds.");
            }

            if (_error is not null)
            {
                throw new InvalidOperationException("Allocation worker failed.", _error);
            }
        }

        private void Work()
        {
            long baseline = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                for (int i = 0; i < 16 && Volatile.Read(ref _stop) == 0; i++)
                {
                    AllocatePair();
                    Thread.Sleep(1);
                }

                WarmupBytes = GC.GetAllocatedBytesForCurrentThread() - baseline;
                TotalBytes = WarmupBytes;
                Volatile.Write(ref _ready, 1);
                while (Volatile.Read(ref _stop) == 0)
                {
                    if (BudgetBytes - TotalBytes < PairReservationBytes)
                    {
                        CapReached = true;
                        break;
                    }

                    for (int pair = 0; pair < PairsPerBatch
                        && Volatile.Read(ref _window) != 0 && Volatile.Read(ref _stop) == 0; pair++)
                    {
                        if (BudgetBytes - TotalBytes < PairReservationBytes)
                        {
                            CapReached = true;
                            break;
                        }

                        AllocatePair();
                        TotalBytes = GC.GetAllocatedBytesForCurrentThread() - baseline;
                        WindowBytes = TotalBytes - WarmupBytes;
                    }

                    Thread.Sleep(1);
                }
            }
            catch (Exception exception)
            {
                _error = exception;
            }
            finally
            {
                Volatile.Write(ref _sink, null);
                Volatile.Write(ref _ready, 1);
            }
        }

        private void AllocatePair()
        {
            byte[] first = new byte[BufferBytes];
            first[0] = 1;
            Volatile.Write(ref _sink, first);
            byte[] second = new byte[BufferBytes];
            second[0] = 2;
            Volatile.Write(ref _sink, second);
            GC.KeepAlive(first);
            GC.KeepAlive(second);
        }
    }
}
