using OODD.Timer;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OODD.Timer.Tests;

/// <summary>
/// 调度器测试：共享线程上的多定时器管理、堆的排序语义、停启不重复挂载。
/// </summary>
public class SchedulerTests
{
    [Fact]
    public void SharedScheduler_DrivesManyTimers()
    {
        const int timerCount = 50;
        var fired = new ConcurrentDictionary<int, int>();
        var timers = new List<HighResolutionTimer>(timerCount);

        try
        {
            for (int i = 0; i < timerCount; i++)
            {
                int index = i;
                var timer = new HighResolutionTimer(new HighResolutionTimerOptions
                {
                    Name = $"shared-{i}",
                    Period = TimeSpan.FromMilliseconds(5 + i % 7),
                    Mode = WaitMode.Spin,
                    Scheduler = HighResolutionScheduler.Shared,
                    AutoStart = false,
                    Callback = () => fired.AddOrUpdate(index, 1, (_, c) => c + 1),
                });
                timers.Add(timer);
                timer.Start();
            }

            Assert.True(HighResolutionScheduler.Shared.TimerCount >= timerCount);
            long before = HighResolutionScheduler.Shared.TotalFiredTicks;
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(250), WaitMode.Kernel);

            Assert.Equal(timerCount, fired.Count);
            Assert.All(fired.Values, count => Assert.True(count > 0, "存在从未触发的定时器"));
            Assert.True(HighResolutionScheduler.Shared.TotalFiredTicks > before + timerCount);

            TimerStatistics sharedStats = HighResolutionScheduler.Shared.GetStatistics();
            Assert.True(sharedStats.TickCount > 0);
            Assert.Contains("timers=", HighResolutionScheduler.Shared.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Dispose();
            }
        }

        // 借用的调度器不会因为定时器释放而被连带释放。
        Assert.False(HighResolutionScheduler.Shared.IsDisposed);
        HighResolutionScheduler.Shared.ResetStatistics();
    }

    [Fact]
    public void EarlierDeadline_FiresFirst()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Order" });
        var order = new ConcurrentQueue<string>();

        using var slow = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Name = "slow",
            Period = TimeSpan.FromMilliseconds(10),
            DueTime = TimeSpan.FromMilliseconds(60),
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => order.Enqueue("slow"),
        });
        using var fast = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Name = "fast",
            Period = TimeSpan.FromMilliseconds(10),
            DueTime = TimeSpan.FromMilliseconds(10),
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => order.Enqueue("fast"),
        });

        slow.Start();
        fast.Start();

        // 等到第一拍出现（fast 的到期点是 10ms，slow 是 60ms），再判顺序。
        long deadline = HiResClock.Add(HiResClock.Now(), HiResClock.ToTicks(TimeSpan.FromSeconds(5)));
        while (order.IsEmpty && !HiResClock.IsDue(deadline))
        {
            HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(1), WaitMode.Kernel);
        }

        Assert.False(order.IsEmpty, "两个定时器都没有触发");
        Assert.True(order.TryPeek(out string? first));
        Assert.Equal("fast", first);
        Assert.Equal(2, scheduler.TimerCount);
    }

    [Fact]
    public void SameDeadline_AllTimersFireExactlyOncePerPeriod()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.SameDue" });
        int a = 0;
        int b = 0;
        var due = TimeSpan.FromMilliseconds(4);

        using var ta = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = due,
            DueTime = due,
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => Interlocked.Increment(ref a),
        });
        using var tb = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = due,
            DueTime = due,
            Mode = WaitMode.Spin,
            Scheduler = scheduler,
            AutoStart = false,
            Callback = () => Interlocked.Increment(ref b),
        });

        ta.Start();
        tb.Start();
        Assert.Equal(2, scheduler.TimerCount);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(120), WaitMode.Kernel);
        ta.Stop();
        tb.Stop();
        watch.Stop();

        // 上限按"实际经过了多久"算，而不是按名义的 120ms：机器偶尔被外部事件拖住 60ms，
        // 用固定上限会把这种环境抖动误判成"重复触发"。
        int expected = (int)(watch.Elapsed.TotalMilliseconds / due.TotalMilliseconds);
        Assert.True(a > 0 && a <= expected + 3, $"a={a}，理论上限 {expected + 3}（实际 {watch.ElapsedMilliseconds}ms）");
        // 两者计数差只用来抓"重复注册"（那会表现为接近 2 倍），不应预置一个固定拍数：
        // 同一时刻挂载的两拍在调度线程上串行执行，卡顿只会整体推后，差值随运行时长按比例放宽。
        int tolerance = Math.Max(3, expected / 8);
        Assert.True(Math.Abs(a - b) <= tolerance,
            $"同时刻挂载的两个定时器计数差过大：a={a} b={b}（容差 {tolerance}）");
        Assert.Equal(0, scheduler.TimerCount);
    }

    [Fact]
    public void StopStart_LoopDoesNotDoubleRegister()
    {
        // 回归测试：Stop→Start 若在堆里留下重复记录，这一拍会被触发两次，
        // 表现为计数约为预期的两倍。
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(5),
            Mode = WaitMode.Spin,
            AutoStart = false,
        });

        for (int i = 0; i < 50; i++)
        {
            timer.Start();
            _ = timer.Stop();
        }

        int fired = 0;
        timer.Elapsed += (_, _) => Interlocked.Increment(ref fired);
        timer.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(100), WaitMode.Kernel);
        timer.Stop();
        watch.Stop();

        // 上界按"实际经过的墙钟时间"算：外部卡顿会让 100ms 的睡眠变成 300ms，
        // 用固定 25 拍会把环境抖动误判成重复挂载。重复挂载的特征是约两倍，上界足以抓住它。
        int expected = (int)(watch.Elapsed.TotalMilliseconds / 5);
        Assert.True(fired > 0, "回归循环之后定时器没有触发过");
        Assert.True(fired <= expected + 2, $"重复挂载？实际 {watch.ElapsedMilliseconds}ms 内触发 {fired} 拍（约一半即 {expected}）");
    }

    [Fact]
    public void DisposedScheduler_RejectsArming()
    {
        var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Dead" });
        scheduler.Dispose();
        scheduler.Dispose(); // 幂等
        Assert.True(scheduler.IsDisposed);

        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(1),
            Scheduler = scheduler,
            AutoStart = false,
        });

        Assert.Throws<ObjectDisposedException>(() => timer.Start());
        Assert.Equal(TimerState.Stopped, timer.State);
    }

    [Fact]
    public void TimerCount_ReflectsArmedTimers()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = "Tests.Count" });
        Assert.Equal(0, scheduler.TimerCount);
        Assert.Equal(WaitMode.Hybrid, scheduler.DefaultMode);

        var timers = Enumerable.Range(0, 8).Select(i => new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(20 + i),
            Mode = WaitMode.Kernel,
            Scheduler = scheduler,
            AutoStart = false,
        })).ToList();

        try
        {
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Start();
            }

            Assert.Equal(8, scheduler.TimerCount);
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Stop();
            }

            Assert.Equal(0, scheduler.TimerCount);
        }
        finally
        {
            foreach (HighResolutionTimer timer in timers)
            {
                timer.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessorAffinity_BindsNativeThreadAndSurvivesRestartWithClonedOptions(bool useSharedScheduler)
    {
        bool windows = OperatingSystem.IsWindows();
        if (!windows && !OperatingSystem.IsLinux())
        {
            return;
        }

        NativeGroupAffinity callerWindowsAffinity = windows ? ReadWindowsAffinity() : default;
        byte[] callerLinuxAffinity = windows ? Array.Empty<byte>() : ReadLinuxAffinity();
        int processorIndex = windows
            ? FindWindowsProcessor(callerWindowsAffinity.Mask)
            : FindLinuxProcessor(callerLinuxAffinity);
        ushort processorGroup = windows ? callerWindowsAffinity.Group : (ushort)0;
        byte[] callbackLinuxAffinity = new byte[callerLinuxAffinity.Length];
        using var completed = new ManualResetEventSlim(false);
        var callbackGate = new object();
        int samples = 0;
        bool observing = false;
        bool failed = false;
        var schedulerOptions = new HighResolutionSchedulerOptions
        {
            ProcessorIndex = processorIndex,
            ProcessorGroup = processorGroup,
            ThreadPriority = ThreadPriority.Normal,
        };
        using var suppliedScheduler = useSharedScheduler ? new HighResolutionScheduler(schedulerOptions) : null;
        var timerOptions = new HighResolutionTimerOptions
        {
            Scheduler = suppliedScheduler,
            ProcessorIndex = useSharedScheduler ? null : processorIndex,
            ProcessorGroup = useSharedScheduler ? (ushort)0 : processorGroup,
            Period = TimeSpan.FromMilliseconds(1),
            DueTime = TimeSpan.Zero,
            Mode = WaitMode.Kernel,
            ThreadPriority = ThreadPriority.Normal,
            AutoStart = false,
            Callback = () =>
            {
                lock (callbackGate)
                {
                    if (!observing)
                    {
                        return;
                    }

                    try
                    {
                        failed |= !MatchesNativeProcessor(windows, processorIndex, processorGroup, callbackLinuxAffinity);
                    }
                    catch (Exception)
                    {
                        failed = true;
                    }

                    if (++samples == 100)
                    {
                        observing = false;
                        completed.Set();
                    }
                }
            },
        };
        using var timer = new HighResolutionTimer(timerOptions);
        HighResolutionScheduler scheduler = timer.Scheduler;
        schedulerOptions.ProcessorIndex = -1;
        schedulerOptions.ProcessorGroup = ushort.MaxValue;
        timerOptions.ProcessorIndex = -1;
        timerOptions.ProcessorGroup = ushort.MaxValue;
        timerOptions.Callback = null;

        try
        {
            Assert.Equal(TimerState.Stopped, timer.State);
            Assert.Equal(0, scheduler.TimerCount);
            AssertSchedulerThreadNotStarted(scheduler);

            for (int run = 0; run < 2; run++)
            {
                lock (callbackGate)
                {
                    samples = 0;
                    completed.Reset();
                    observing = true;
                }

                timer.Start();
                AssertCallerAffinityUnchanged(windows, callerWindowsAffinity, callerLinuxAffinity);
                bool signaled = completed.Wait(TimeSpan.FromSeconds(10));
                timer.Stop();

                lock (callbackGate)
                {
                    observing = false;
                    Assert.True(signaled, $"Affinity callback sampling timed out on run {run + 1}.");
                    Assert.Equal(100, samples);
                    Assert.False(failed, "A callback's native CPU or affinity mask did not match the requested processor.");
                }

                Assert.Equal(TimerState.Stopped, timer.State);
                Assert.Equal(0, scheduler.TimerCount);
                Assert.False(scheduler.IsDisposed);
                AssertCallerAffinityUnchanged(windows, callerWindowsAffinity, callerLinuxAffinity);
            }
        }
        finally
        {
            // Stop can leave a callback in flight; close sampling before disposing its signal.
            lock (callbackGate)
            {
                observing = false;
            }

            timer.Dispose();
            suppliedScheduler?.Dispose();
        }

        AssertCallerAffinityUnchanged(windows, callerWindowsAffinity, callerLinuxAffinity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessorAffinity_DefaultOptionsRemainUnboundAndCanStart(bool useSharedScheduler)
    {
        var schedulerOptions = new HighResolutionSchedulerOptions();
        var timerOptions = new HighResolutionTimerOptions { AutoStart = false };
        Assert.Null(schedulerOptions.ProcessorIndex);
        Assert.Equal((ushort)0, schedulerOptions.ProcessorGroup);
        Assert.Null(timerOptions.ProcessorIndex);
        Assert.Equal((ushort)0, timerOptions.ProcessorGroup);

        using var completed = new ManualResetEventSlim(false);
        using var scheduler = useSharedScheduler ? new HighResolutionScheduler(schedulerOptions) : null;
        int callbacks = 0;
        timerOptions.Scheduler = scheduler;
        timerOptions.Callback = () =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                completed.Set();
            }
        };
        using var timer = new HighResolutionTimer(timerOptions);
        AssertSchedulerThreadNotStarted(timer.Scheduler);
        timer.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
        timer.Stop();
        Assert.Equal(TimerState.Stopped, timer.State);
        Assert.False(timer.Scheduler.IsDisposed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ProcessorAffinity_NegativeIndexIsRejectedByBothConstructors(int processorIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { ProcessorIndex = processorIndex });
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions { ProcessorIndex = processorIndex, AutoStart = false });
        });
    }

    [Fact]
    public void ProcessorAffinity_WindowsIndexMustFitNativePointerMask()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (int processorIndex in new[] { IntPtr.Size * 8, IntPtr.Size * 8 + 1, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { ProcessorIndex = processorIndex });
            });
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                using var timer = new HighResolutionTimer(new HighResolutionTimerOptions { ProcessorIndex = processorIndex, AutoStart = false });
            });
        }
    }

    [Fact]
    public void ProcessorAffinity_GroupWithoutIndexIsRejectedByBothConstructors()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { ProcessorGroup = 1 });
        });
        Assert.Throws<ArgumentException>(() =>
        {
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions { ProcessorGroup = 1, AutoStart = false });
        });
    }

    [Fact]
    public void ProcessorAffinity_TimerCannotOverrideSuppliedScheduler()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var scheduler = new HighResolutionScheduler();
        Assert.Throws<ArgumentException>(() =>
        {
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
            {
                Scheduler = scheduler,
                ProcessorIndex = 0,
                AutoStart = false,
            });
        });
        Assert.False(scheduler.IsDisposed);
        Assert.Equal(0, scheduler.TimerCount);
        AssertSchedulerThreadNotStarted(scheduler);
    }

    [Fact]
    public void ProcessorAffinity_LinuxRejectsWindowsProcessorGroups()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { ProcessorIndex = 0, ProcessorGroup = 1 });
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions { ProcessorIndex = 0, ProcessorGroup = 1, AutoStart = false });
        });
    }

    [Fact]
    public void ProcessorAffinity_LinuxCanRebindOutsideInheritedThreadMask()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        byte[] original = ReadLinuxAffinity();
        int first = FindLinuxProcessor(original);
        byte[] narrowed = new byte[original.Length];
        narrowed[CpuByteIndex(first)] = (byte)(1 << (first % 8));
        byte[] remaining = (byte[])original.Clone();
        remaining[CpuByteIndex(first)] &= (byte)~(1 << (first % 8));
        if (!remaining.Any(value => value != 0))
        {
            return;
        }

        int target = FindLinuxProcessor(remaining);
        byte[] observed = new byte[original.Length];
        using var completed = new ManualResetEventSlim(false);
        bool matched = false;
        try
        {
            Assert.Equal(0, sched_setaffinity(0, new UIntPtr((uint)narrowed.Length), narrowed));
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
            {
                ProcessorIndex = target,
                Mode = WaitMode.Kernel,
                AutoReset = false,
                Callback = () =>
                {
                    matched = MatchesNativeProcessor(false, target, 0, observed);
                    completed.Set();
                },
            });
            Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(matched);
            Assert.Equal(narrowed, ReadLinuxAffinity());
        }
        finally
        {
            Assert.Equal(0, sched_setaffinity(0, new UIntPtr((uint)original.Length), original));
        }

        Assert.Equal(original, ReadLinuxAffinity());
    }

    [Fact]
    public void ProcessorAffinity_UnsupportedPlatformsRejectExplicitBinding()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(() =>
        {
            using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { ProcessorIndex = 0 });
        });
        Assert.Throws<PlatformNotSupportedException>(() =>
        {
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions { ProcessorIndex = 0, AutoStart = false });
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessorAffinity_InvalidNativeTargetFailsStartWithoutArming(bool useSharedScheduler)
    {
        bool windows = OperatingSystem.IsWindows();
        if (!windows && !OperatingSystem.IsLinux())
        {
            return;
        }

        int processorIndex = windows ? 0 : int.MaxValue;
        ushort processorGroup = windows ? ushort.MaxValue : (ushort)0;
        using var suppliedScheduler = useSharedScheduler
            ? new HighResolutionScheduler(new HighResolutionSchedulerOptions
            {
                ProcessorIndex = processorIndex,
                ProcessorGroup = processorGroup,
            })
            : null;
        int callbacks = 0;
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Scheduler = suppliedScheduler,
            ProcessorIndex = useSharedScheduler ? null : processorIndex,
            ProcessorGroup = useSharedScheduler ? (ushort)0 : processorGroup,
            DueTime = TimeSpan.Zero,
            AutoStart = false,
            Callback = () => Interlocked.Increment(ref callbacks),
        });
        HighResolutionScheduler scheduler = timer.Scheduler;
        Assert.False(scheduler.IsDisposed);
        Assert.Equal(0, scheduler.TimerCount);
        AssertSchedulerThreadNotStarted(scheduler);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => timer.Start());
        if (windows)
        {
            Assert.IsType<Win32Exception>(error.InnerException);
        }
        else
        {
            Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
        }

        Assert.Equal(0, Volatile.Read(ref callbacks));
        Assert.Equal(TimerState.Stopped, timer.State);
        Assert.Equal(0, scheduler.TimerCount);
        Assert.Equal(0L, scheduler.TotalFiredTicks);
        Assert.True(scheduler.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => timer.Start());
        Assert.Equal(0, Volatile.Read(ref callbacks));
        Assert.Equal(TimerState.Stopped, timer.State);
        Assert.Equal(0, scheduler.TimerCount);
        Assert.True(scheduler.IsDisposed);
    }

    [Fact]
    public void ProcessorAffinity_AutoStartFailureReleasesGcLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int requests = RuntimeTuning.GcLatencyRequestCount;
        System.Runtime.GCLatencyMode mode = System.Runtime.GCSettings.LatencyMode;
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
            {
                ProcessorIndex = 0,
                ProcessorGroup = ushort.MaxValue,
                PreferSustainedLowLatencyGc = true,
            });
        });
        Assert.Equal(requests, RuntimeTuning.GcLatencyRequestCount);
        Assert.Equal(mode, System.Runtime.GCSettings.LatencyMode);
    }

    private static int CpuByteIndex(int index)
    {
        int offset = index / (IntPtr.Size * 8) * IntPtr.Size;
        int byteInWord = index / 8 % IntPtr.Size;
        return offset + (BitConverter.IsLittleEndian ? byteInWord : IntPtr.Size - 1 - byteInWord);
    }

    private static void AssertSchedulerThreadNotStarted(HighResolutionScheduler scheduler)
    {
        // ThreadId is reserved at construction, so inspect the thread's actual startup state.
        var field = typeof(HighResolutionScheduler).GetField("_thread",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var thread = Assert.IsType<Thread>(field?.GetValue(scheduler));
        Assert.True((thread.ThreadState & System.Threading.ThreadState.Unstarted) != 0);
    }

    private static int FindWindowsProcessor(UIntPtr mask)
    {
        for (int index = IntPtr.Size * 8 - 1; index >= 0; index--)
        {
            if ((mask.ToUInt64() & (1UL << index)) != 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The caller has no allowed Windows processor.");
    }

    private static int FindLinuxProcessor(byte[] mask)
    {
        for (int index = 0; index < mask.Length * 8; index++)
        {
            if ((mask[CpuByteIndex(index)] & (1 << (index % 8))) != 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The caller has no allowed Linux processor.");
    }

    private static NativeGroupAffinity ReadWindowsAffinity()
    {
        if (!GetThreadGroupAffinity(GetCurrentThread(), out NativeGroupAffinity affinity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return affinity;
    }

    private static byte[] ReadLinuxAffinity()
    {
        for (int size = 128; size <= 1024 * 1024; size *= 2)
        {
            var mask = new byte[size];
            if (sched_getaffinity(0, new UIntPtr((uint)size), mask) == 0)
            {
                return mask;
            }

            int error = Marshal.GetLastWin32Error();
            if (error != 22)
            {
                throw new Win32Exception(error);
            }
        }

        throw new InvalidOperationException("The native Linux affinity mask exceeded the test buffer limit.");
    }

    private static bool MatchesNativeProcessor(bool windows, int processorIndex, ushort processorGroup, byte[] linuxMask)
    {
        if (windows)
        {
            NativeGroupAffinity affinity = ReadWindowsAffinity();
            GetCurrentProcessorNumberEx(out NativeProcessorNumber processor);
            return processor.Group == processorGroup && processor.Number == processorIndex
                && affinity.Group == processorGroup && affinity.Mask.ToUInt64() == (1UL << processorIndex);
        }

        if (sched_getcpu() != processorIndex
            || sched_getaffinity(0, new UIntPtr((uint)linuxMask.Length), linuxMask) != 0)
        {
            return false;
        }

        for (int index = 0; index < linuxMask.Length; index++)
        {
            byte expected = index == CpuByteIndex(processorIndex) ? (byte)(1 << (processorIndex % 8)) : (byte)0;
            if (linuxMask[index] != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertCallerAffinityUnchanged(bool windows, NativeGroupAffinity windowsBefore, byte[] linuxBefore)
    {
        if (windows)
        {
            NativeGroupAffinity after = ReadWindowsAffinity();
            Assert.Equal(windowsBefore.Group, after.Group);
            Assert.Equal(windowsBefore.Mask, after.Mask);
        }
        else
        {
            Assert.Equal(linuxBefore, ReadLinuxAffinity());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGroupAffinity
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadGroupAffinity(IntPtr thread, out NativeGroupAffinity affinity);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentProcessorNumberEx(out NativeProcessorNumber processor);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, UIntPtr size, [Out] byte[] mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, UIntPtr size, [In] byte[] mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getcpu();

    [Fact]
    public void SpinModeBurnsACore_WhileKernelModeIdlesForFree()
    {
        // 这是 WaitMode 的核心权衡，必须实测验证，不能只写在文档里。
        // 判据用"进程累计 CPU 时间"的增量：自旋一个核约消耗 1ms CPU / 1ms 墙上时间，
        // 内核等待的空闲调度线程则接近 0。
        // 进程级 CPU 会被同进程其它工作的尾巴污染（后台 JIT、GC、上一个测试遗留的线程收尾），
        // 而污染是瞬态的：空闲侧取 3 次窗口的最小值，才能把"线程真的挂住了"和"恰好有别的东西在跑"分开。
        const int WindowMilliseconds = 300;
        const int IdleWindowSamples = 3;

        double kernelCpu = Enumerable.Range(0, IdleWindowSamples)
            .Select(_ => MeasureCpuMilliseconds(WindowMilliseconds, WaitMode.Kernel))
            .Min();
        double spinCpu = MeasureCpuMilliseconds(WindowMilliseconds, WaitMode.Spin);

        Assert.True(spinCpu > WindowMilliseconds * 0.5,
            $"Spin 模式在 {WindowMilliseconds}ms 内只消耗了 {spinCpu:F1}ms CPU，忙等可能未生效");
        Assert.True(kernelCpu < WindowMilliseconds * 0.5,
            $"Kernel 模式在 {WindowMilliseconds}ms 的 {IdleWindowSamples} 次采样里最干净的一次仍消耗了 {kernelCpu:F1}ms CPU，线程没有被内核挂起");
    }

    private static double MeasureCpuMilliseconds(int windowMilliseconds, WaitMode mode)
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions { Name = $"Tests.Cpu.{mode}" });
        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            // 周期取大值：绝大多数时间线程都在"等待下一拍"，测到的正是等待策略本身的开销。
            Period = TimeSpan.FromSeconds(30),
            Mode = mode,
            Scheduler = scheduler,
            AutoStart = false,
        });

        timer.Start();
        // 让调度线程先起跑（首次 Arm 才创建线程），再开始计时。
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(20), WaitMode.Kernel);

        long before = CurrentProcessCpuNanoseconds();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(windowMilliseconds), WaitMode.Kernel);
        double cpu = (CurrentProcessCpuNanoseconds() - before) / 1_000_000.0;

        timer.Stop();
        return cpu;
    }

    private static long CurrentProcessCpuNanoseconds()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return (long)(process.TotalProcessorTime.TotalMilliseconds * 1_000_000.0);
    }

    [Fact]
    public void SchedulerOptions_AreAppliedToThreadNameAndPriority()
    {
        using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions
        {
            Name = "Tests.Named",
            Mode = WaitMode.Kernel,
            ThreadPriority = ThreadPriority.AboveNormal,
        });

        using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
        {
            Period = TimeSpan.FromMilliseconds(10),
            Scheduler = scheduler,
            AutoStart = false,
        });

        timer.Start();
        HighResolutionWait.Sleep(TimeSpan.FromMilliseconds(30), WaitMode.Kernel);
        Assert.True(scheduler.ThreadId > 0);
        Assert.Equal(WaitMode.Kernel, scheduler.DefaultMode);
        timer.Stop();
    }
}
