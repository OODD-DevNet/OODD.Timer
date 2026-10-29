# OODD.Timer

**English** | [简体中文](https://github.com/OODD-DevNet/OODD.Timer/blob/main/README.zh-CN.md)

A high-resolution timer library for .NET.

The reason for writing it was practical: I wanted a 1ms control loop, only to find that `System.Threading.Timer` on Windows arrived more than 14ms late on each tick and fell further behind the longer it ran—more than two minutes behind after 10000 ticks. This is not "jitter"; it is a consequence of the design: managed timers are tied to the system clock tick, which defaults to 64Hz on Windows.

OODD.Timer replaces both timekeeping and waiting. It reads a monotonic clock from the same source as `Stopwatch.GetTimestamp()` (measured at 10MHz on this machine, or 100ns per tick), waits using a combination of high-resolution kernel waits and a final spin, and schedules against an absolute time grid. This brings timing error down from 10–15ms to microseconds, reaching sub-microsecond accuracy in many scenarios without accumulating drift over long runs.

Source code, issues, and changelog: [github.com/OODD-DevNet/OODD.Timer](https://github.com/OODD-DevNet/OODD.Timer)

- Targets `net40` / `netstandard2.0` / `net8.0` / `net9.0` / `net10.0`, covering .NET Framework 4.0 through .NET 10; runs on Windows, Linux, macOS, iOS, Android, and FreeBSD
- No third-party package dependencies, only `System.*`; net8.0+ assets are annotated for trimming and Native AOT compatibility
- Public members have XML documentation in Chinese
- Apache-2.0 license, including an explicit patent grant

---

## Contents

- [Supported target frameworks](#supported-target-frameworks)
- [Why ordinary timers are inaccurate](#why-ordinary-timers-are-inaccurate)
- [Measurements](#measurements-on-this-machine-windows-11-and-net-10)
- [Quick start](#quick-start)
- [Choosing among the three wait modes](#choosing-among-the-three-wait-modes)
- [Pull mode: waiting for the next tick synchronously or asynchronously](#pull-mode-waiting-for-the-next-tick-synchronously-or-asynchronously)
- [API guide](#api-guide)
- [New APIs in 1.2](#new-apis-in-12)
- [Where the accuracy limits are](#where-the-accuracy-limits-are)
- [Reducing tail latency: process-level tuning](#reducing-tail-latency-process-level-tuning)
- [Usage pitfalls](#usage-pitfalls)
- [TimeProvider integration](#timeprovider-integration-one-clock-for-testing-and-production)
- [Building, testing, and benchmarking](#building-testing-and-benchmarking)
- [Frequently asked questions](#frequently-asked-questions)
- [License](#license)

---

## Supported target frameworks

| Target framework | Runtimes covered | Notes |
| --- | --- | --- |
| `net40` | .NET Framework 4.0 – 4.6.0 | Full functionality. The GC in 4.0 has no `SustainedLowLatency` mode; enabling that option throws `PlatformNotSupportedException`. No `TimeProvider` integration |
| `netstandard2.0` | .NET Framework 4.6.1+, .NET Core 2.x / 3.x, Mono, Xamarin, Unity 2021.2+ | Full functionality. `TimeProvider` integration is unavailable (the type does not exist in the 4.6.1 BCL) |
| `net8.0` / `net9.0` / `net10.0` | .NET 8 / 9 / 10 | Full functionality, plus `HighResolutionTimeProvider`, which also gives `TimeProvider.CreateTimer` sub-millisecond timing; annotated as trim-friendly and AOT-compatible |

Three potentially confusing points are worth clarifying up front.

First, .NET Framework 4.6.1–4.8 projects receive the `net40` asset rather than `netstandard2.0`. This is normal NuGet selection behavior—assets from the same framework family take precedence. Both assets behave identically, so there is no need to worry about which one is selected.

Second, the `net40` compiler does not support async methods (added in 4.5), so the library implements its asynchronous interfaces internally using `Task` continuations. The public signatures and semantics are exactly the same as on newer frameworks; you can use `await timer.WaitForNextTickAsync()` in your own project as usual.

Third, `TimeProvider` integration exists only on net8+. `HighResolutionTimeProvider` and `HighResolutionTimers.TimeProvider` are not compiled for older frameworks. On those frameworks, use `new HighResolutionTimer(...)` or `HighResolutionTimers.Periodic(...)` directly; there is no difference in accuracy.

All five TFMs share the same source code and smoke-test program (`tests/OODD.Timer.CompatSmoke*`). Each asset has been run on its corresponding actual runtime; see [Building, testing, and benchmarking](#building-testing-and-benchmarking) for details.

---

## Why ordinary timers are inaccurate

`System.Threading.Timer`, `PeriodicTimer`, `Task.Delay`, and `Thread.Sleep` all accept milliseconds, but they are all tied to the system clock tick. The default Windows tick is 15.625ms (64Hz), so `Thread.Sleep(1)` does not sleep for 1ms; it sleeps until the next tick boundary. Across 600 samples on this machine, p50 was 15.56ms, the minimum 9.87ms, and the maximum 17.27ms.

More troublesome is that these APIs are self-paced: after waking, they start timing again from the current instant, so error accumulates tick by tick. Running 10000 ticks at a nominal "period" of 1ms actually takes 145 seconds.

The library addresses this in three ways:

| Approach | Problem addressed |
|---|---|
| Use a high-resolution monotonic clock for timekeeping (measured at 10MHz on this machine, 1 tick = 100ns), rather than the 64Hz tick | Resolution changes from 15.6ms to 100ns on this machine |
| Wait with a hybrid strategy: sleep in the kernel until the spin window, then spin for the final stretch, avoiding kernel tick quantization | Wake-up latency drops from 15.5ms to sub-millisecond levels, with the final spin tightening it to the 100ns range |
| Schedule on an absolute grid: next due time = first due time + n × period, using integer tick arithmetic throughout, never `+= actual elapsed time` | Individual jitter does not accumulate, so long runs have no cumulative scheduling drift; under overload, missed ticks are coalesced instead of producing a catch-up storm |

As for `timeBeginPeriod(1)` (exposed here as `RuntimeTuning.RequestSystemTimerResolution(1)`), measurements show that it does work: `Thread.Sleep(1)` p50 drops from 15.56ms to 1.99ms, and per-tick lateness p50 for a 1ms `System.Threading.Timer` drops from 14.55ms to 0.76ms. The change is reversible on `Dispose`. But its limit is still in milliseconds, and it has process-/system-wide side effects—other threads in the same process are also forced to wake at 1ms intervals, consuming more power. Reaching microseconds requires `CreateWaitableTimerExW` with `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (Windows 10 1803+) plus a final spin. This is the library's default path; you do not need to change the system tick.

---

## Measurements on this machine (Windows 11 and .NET 10)

Commands to reproduce:

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000
# Append rt to include two additional Windows THREAD_PRIORITY_TIME_CRITICAL comparison rows
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000 rt
```

Each implementation is measured with the same yardstick: record how far each firing falls behind "anchor + n × period," then separate that into two metrics. Per-tick lateness is the interval between successive firings minus the period; it captures jitter without accumulating over time. Cumulative drift is the difference between the first and last offsets: 0 indicates advancement along an absolute grid, while a nonzero value indicates accumulating error.

Period: 1ms; 10000 ticks (about 10 seconds):

| Implementation | Per-tick lateness p50 | Per-tick lateness p95 | Per-tick lateness p99 | Per-tick lateness max | Cumulative drift | Drift/tick | CPU time during run |
|---|---:|---:|---:|---:|---:|---:|---:|
| Thread.Sleep(1ms) | 14561.0µs | 15199.1µs | 15515.0µs | 17855.9µs | 145848.91ms | 14586.35µs | 156ms |
| System.Threading.Timer (1.0ms) | 14563.2µs | 15232.0µs | 15715.6µs | 16726.0µs | 46784.97ms | 4678.97µs | 484ms |
| PeriodicTimer (1.0ms) | 14540.4µs | 15400.8µs | 16240.5µs | 20089.3µs | 144226.43ms | 14424.09µs | 1609ms |
| HighResolutionWait.Sleep(Kernel) | 568.2µs | 731.5µs | 881.2µs | 2055.6µs | 5687.24ms | 568.78µs | 94ms |
| HighResolutionWait.Sleep(Spin) | 0.1µs | 0.8µs | 42.4µs | 1094.5µs | 16.98ms | 1.70µs | 9781ms |
| OODD.Timer (Kernel) | 34.8µs | 326.2µs | 513.1µs | 3812.1µs | 38.50ms | 3.85µs | 141ms |
| OODD.Timer (Hybrid) | 0.0µs | 1.5µs | 26.8µs | 279.5µs | -0.20ms | -0.02µs | 9625ms |
| OODD.Timer (Spin) | 0.0µs | 1.3µs | 22.5µs | 1368.6µs | 0.81ms | 0.08µs | 9641ms |
| OODD.Timer (Hybrid, TIME_CRITICAL) | 0.0µs | 0.9µs | 2.7µs | 617.9µs | -0.62ms | -0.06µs | 9953ms |
| OODD.Timer (Spin, TIME_CRITICAL) | 0.0µs | 0.9µs | 3.1µs | 125.4µs | -0.13ms | -0.01µs | 9875ms |

The first three rows show per-tick lateness of 14.5ms and cumulative delay of 145 seconds. This is what the "10–15ms error" really means: structural accumulation, not random jitter.

For `OODD.Timer (Hybrid)`, median per-tick lateness is below the table's resolution, p95 is 1.5µs, and cumulative drift over the 10-second run stays near 0 (a negative value is simply a constant offset from the first tick being early). The library's internal statistics, measured relative to each tick's due time, report p50 = 256ns and p99 = 32.8µs.

The last two rows differ in just one setting: `UseRealTimePriority = true`, which raises the scheduler thread to Windows `THREAD_PRIORITY_TIME_CRITICAL` (15). p99 drops from 26.8µs to 2.7µs, at the cost of CPU usage rising from 96% to 99.5%—that core was already spinning; the difference is that it is now almost never interrupted. The `Spin + TIME_CRITICAL` row has a max of 125µs, the tightest set of measurements obtained on this machine.

`OODD.Timer (Kernel)` is the low-CPU option: per-tick lateness p50 = 34.8µs. Because it advances along an absolute grid, the fixed wake-up offset does not accumulate. CPU time is just 141ms / 10s, or about 1.4%. The max in this row reflects coalescing missed ticks under overload, as explained under [Where the accuracy limits are](#where-the-accuracy-limits-are).

The `HighResolutionWait.Sleep(Kernel)` row is a bare, self-paced wait primitive: its 568µs overshoot per wait accumulates to 5.7 seconds. It is included to show that "accuracy" and "no drift" are different things; this library primarily addresses the latter. Hybrid and Spin use nearly a full core—that is the cost of spinning.

These numbers describe only the machine that ran this command (Windows 11 10.0.26200, 16 cores, .NET 10, a console run with no other load). You must remeasure on a different machine or operating system, or with background load. The benchmark prints Markdown tables directly, ready to paste into a README or issue.

---

## Quick start

Install:

```powershell
dotnet add package OODD.Timer
```

Before the package is published to nuget.org, you can pack it locally and use a local feed: run `dotnet pack src/OODD.Timer -c Release`, then add `bin/Release/*.nupkg` to your local package feed.

Get started in ten lines:

```csharp
using OODD.Timer;

using var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(1));

timer.Elapsed += (_, e) =>
{
    // e.LatenessNanoseconds: how many nanoseconds this tick arrived after its due time
    Console.WriteLine($"tick {e.Sequence}, lateness {e.LatenessNanoseconds:F0}ns");
};

timer.Start();
Console.ReadKey();
```

This is only a demonstration. Do not use `Console.WriteLine` when measuring accuracy: console I/O on the scheduler thread delays the next tick. See [Usage pitfalls](#usage-pitfalls).

If you are unsure which API to use, consult this table:

| I want… | Use |
|---|---|
| A callback-based periodic timer | `new HighResolutionTimer(period)` + `Elapsed` |
| A one-line periodic callback | `HighResolutionTimers.Periodic(TimeSpan.FromMilliseconds(200), () => DoWork())` |
| An async loop (`while (await ...)`) | `timer.WaitForNextTickAsync(ct)`, or `HighResolutionTimers.RunPeriodicAsync(period, async ct => ...)` |
| A single precise delay | `HighResolutionWait.Sleep(duration)` / `SleepAsync(...)` |
| A blocking "wait for a tick, do some work" loop (Haukcode style) | `timer.SetPeriod(ms)` + `timer.WaitForTrigger()` |
| Blocking waits with microsecond-level delivery | `timer.WaitForTrigger(spinWindow)` |
| Asynchronous pulling, once per tick | `await timer.WaitForTriggerAsync(ct)` |
| Many timers running together, using fewer threads | Explicitly share a `HighResolutionScheduler` |
| To use existing `TimeProvider` / `ITimer` code | `HighResolutionTimeProvider.Instance` |
| To find out how accurate this machine can be | `HighResolutionWait.GetPlatformInfo()` and `MeasureKernelWakeLatency()` |

---

## Choosing among the three wait modes

`WaitMode` is the one architectural choice the library asks you to make: it represents a trade-off between accuracy and CPU usage.

`Spin` busy-waits throughout, reading the monotonic clock on every iteration. At a 1ms period on this machine, p50 was 0.0µs and p99 was 43µs, at the cost of occupying a full core. It is suited to periods below 1ms when a dedicated or isolated core is available.

`Hybrid` is the default. The kernel first sleeps until "due time minus a spin window," then the thread spins within that window until the deadline. The window is not fixed: it adapts to measured wake-up latency. Each kernel wake-up is measured against its intended wake-up time; if it is late, the window expands enough to cover it, with a 1.25× margin. If the window becomes clearly too large, it gradually shrinks by 1/64 each time. That shrinking step matters: without it, the window can only grow, eventually degenerating into continuous spinning. At a 1ms period on this machine, p50 was 0.0µs and p99 was 45µs. CPU usage is approximately the window divided by the period.

`Kernel` does not spin at all; it relies on the operating system's timer objects to wake up. Per-tick lateness p50 is 37.7µs (on an absolute grid, without accumulation), raw wake-up p50 is about 0.58ms, and CPU usage is close to zero. It is suited to periods measured in seconds, mobile devices, and power-sensitive applications.

If the window hits its ceiling (32ms by default), this machine's kernel waits are too coarse, and `Hybrid` has effectively become `Spin`. It is better to let it reach the ceiling than to shrink it manually—shrinking it directly causes late ticks.

```csharp
var timer = new HighResolutionTimer(new HighResolutionTimerOptions
{
    Period = TimeSpan.FromMilliseconds(200),
    Mode = WaitMode.Hybrid,
    AdaptiveSpinWindow = true,            // Enabled by default
    MaxSpinWindowNanoseconds = 8_000_000, // Window ceiling; defaults to 32ms
    CoarseChunkMilliseconds = 2,          // Coarse-wait chunks determine the earliest response to Stop / an earlier task
});
```

---

## Pull mode: waiting for the next tick synchronously or asynchronously

If you are used to the `Haukcode.HighResolutionTimer` pattern of "wait for a tick, do work, wait again," you can keep that structure and replace the names one by one:

| Haukcode.HighResolutionTimer | OODD.Timer | Notes |
|---|---|---|
| `new HighResolutionTimer()` | `new HighResolutionTimer()` | Defaults to a 1ms period and starts automatically |
| `SetPeriod(double periodMS)` | `SetPeriod(double periodMilliseconds)` | Accepts fractional values (such as `16.67`); takes effect while running and re-anchors the phase |
| `Start()` | `Start()` | Returns synchronously |
| `Stop()` | `Stop()` | Returns synchronously; does not cancel a tick already in flight |
| `WaitForTrigger()` | `WaitForTrigger()` | Blocks until the next tick, without spinning; recommended for everyday use |
| — | `WaitForTrigger(TimeSpan spinWindow)` | Includes a final spin for microsecond-level delivery |
| — | `await WaitForTriggerAsync(ct)` | Asynchronous version; completes once per tick and supports cancellation |
| `Dispose()` | `Dispose()` | Idempotent |

```csharp
using OODD.Timer;

using var timer = new HighResolutionTimer();
timer.SetPeriod(10.0);          // 10ms
timer.Start();

for (int i = 0; i < 100; i++)
{
    timer.WaitForTrigger();     // Block until the next tick (pull mode, single consumer)
    ProcessSample();
}
```

An asynchronous loop and microsecond-level delivery:

```csharp
// PeriodicTimer-style asynchronous pulling
while (!ct.IsCancellationRequested)
{
    await timer.WaitForTriggerAsync(ct);
    await HandleOneTickAsync();
}

// For microsecond-level delivery, supply a spin window. Measure this machine first, then use the recommendation
double recommended = HighResolutionWait.MeasureKernelWakeLatency().RecommendedSpinWindowNanoseconds;
while (running)
{
    timer.WaitForTrigger(TimeSpan.FromNanoseconds(recommended));
    DoTightWork();
}
```

Both `WaitForTrigger` and `WaitOne` can block until the next tick, but they wait for different things. `WaitOne` waits for a signaled event: a signal left over from the previous tick may make the next call return immediately (an empty iteration), and two ticks can merge into one wake-up under overload. `WaitForTrigger` uses the firing sequence number instead. Each call delivers one tick, always a newly occurring one; if the consumer falls behind, it coalesces to the latest tick, just like push mode. Use `WaitForTrigger` in new code; `WaitOne` remains only for compatibility.

### Measurements: how accurate is pull mode?

On this machine (Windows 11 10.0.26200 / 16 cores / .NET 10, with the scheduler thread using `WaitMode.Spin`), each method was run once for the same set of periods, recording "time of entry into user code minus due time" for every tick:

| Period | Method | p50 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|
| 1ms | Push `Elapsed` (scheduler thread) | 0.2µs | 3.7µs | 51.8µs | 151µs |
| 1ms | Pull `WaitForTrigger()` | 40.5µs | 114.3µs | 187.0µs | 682µs |
| 1ms | Pull `WaitForTrigger(200µs)` | 0.8µs | 8.6µs | 77.6µs | 465µs |
| 200µs | Push `Elapsed` | 0.1µs | 1.0µs | 36.1µs | 7.6ms |
| 200µs | Pull `WaitForTrigger()` | 6.0µs | 51.1µs | 102.4µs | 193µs |
| 200µs | Pull `WaitForTrigger(200µs)` | 0.6µs | 2.7µs | 82.1µs | 283µs |
| 100µs | Push `Elapsed` | 0.0µs | 0.3µs | 15.2µs | 243µs |
| 100µs | Pull `WaitForTrigger()` | 5.5µs | 46.7µs | 78.2µs | 139µs |
| 100µs | Pull `WaitForTrigger(200µs)` | 0.6µs | 3.7µs | 54.3µs | 173µs |

Loop CPU usage in the same scenarios (one core = 100%): 1ms event-only pulling, about 0%; 1ms pulling with a 200µs window, about 90%; 200µs pulling with a 200µs window, about 95%.

First, about the supposed "100µs floor": that is not the library's floor. It is the practical baseline for many pull-mode implementations, because each wake-up requires a kernel event and thread scheduling. This library's push-mode median lateness is 0–0.2µs (with 100ns clock granularity on this machine), and the median for `WaitForTrigger(spinWindow)` is also below 1µs.

Pull-mode p50 depends on what you are waiting for. Event-only waiting waits for a thread wake-up, which takes roughly a dozen to tens of microseconds on this machine. With a spin window, the last part busy-waits on your own thread, bringing the median down to around 1µs. The tens of microseconds at p99 are time during which the operating system preempts your thread; user-mode code cannot eliminate that.

Pull mode's CPU cost is real. The window has to cover the kernel's wake-up lateness (p50 about 577µs on this machine) to avoid being late, so a 1ms period with a 200µs window approaches continuous spinning in steady state—about 90% of a core in measurements. For accuracy with lower CPU use, use push mode: the callback runs directly on the scheduler thread when due, so there is only one set of spin overhead.

One easily overlooked point: the scheduler thread's mode also affects pull delivery. Pulling determines when the consumer receives a tick, but ticks are recorded on the scheduler thread. With the same 1ms period and 200µs window, pull p50 is 0.8–1.3µs when the scheduler uses `Spin` or `Hybrid`, but 332–347µs with `Kernel`—the scheduler thread itself is already hundreds of microseconds late. Keep the scheduler thread in `Spin` or `Hybrid` for pull mode.

A few semantic details: only one consumer may call `WaitForTrigger*` at a time; multiple consumers compete for ticks. Calls do not return before the due time, but if your thread is more than one period late, the library coalesces missed ticks and delivers the latest one. You can `Stop` at any time; a blocked call throws `InvalidOperationException` within about 50ms, even for long periods, because state is rechecked internally every 50ms. One-shot timers (`AutoReset = false`) also work: after `WaitForTrigger` delivers the single tick, the timer enters `Stopped`.

---

## API guide

### `HiResClock` — the clock foundation

High-resolution monotonic timestamps and unit conversions, using the same source as `Stopwatch`. All instants are represented by `long` timestamps; all durations are tick counts.

```csharp
long now = HiResClock.Now();                    // Measured on this machine: 10MHz, 1 tick = 100ns
long deadline = HiResClock.Deadline(TimeSpan.FromMicroseconds(500));
double us = HiResClock.ToMicroseconds(HiResClock.Now() - now);
Console.WriteLine(HiResClock.DescribePlatform()); // Frequency, high-resolution support, OS
```

`HiResClock.Add` uses saturating addition, and `IsDue` checks the sign of the difference rather than directly comparing values, preventing incorrect decisions caused by tick overflow during long runs.

### `HighResolutionTimer` — the timer itself

Timers run on a `HighResolutionScheduler` thread. By default, each timer owns a private scheduler; to share a thread, explicitly pass the same scheduler to the timers.

```csharp
// Construction: convenience overloads + options
new HighResolutionTimer();
new HighResolutionTimer(TimeSpan.FromMicroseconds(500));
new HighResolutionTimer(period, WaitMode.Spin);
new HighResolutionTimer(period, sharedScheduler);
new HighResolutionTimer(new HighResolutionTimerOptions { ... });

// Lifecycle
timer.Start();                                     // Calling again while running simply re-anchors the phase
bool stopped = timer.Stop();                       // Does not guarantee cancellation of a tick already in flight
bool ok = timer.Change(dueTime, period);           // Changing the period re-anchors the grid
timer.Reset();                                     // Clear statistics and restart timing
timer.Dispose();                                   // Idempotent; remove from scheduling and release wait resources

// Consumption methods (choose one)
timer.Elapsed += (_, e) => { /* e.DueTimestamp / FiredTimestamp / Sequence
                                 / MissedTicks / LatenessNanoseconds / IntervalNanoseconds / Overrun */ };
while (timer.WaitOne()) { /* Block until the next tick (event semantics, for compatibility) */ }
while (await timer.WaitForNextTickAsync(ct)) { /* Asynchronously wait for the next tick */ }

// Pull mode (Haukcode style)
timer.SetPeriod(10.0);                                // Milliseconds; fractional values allowed
timer.WaitForTrigger();                               // Block until the next tick
timer.WaitForTrigger(TimeSpan.FromMicroseconds(200)); // Final spin for microsecond-level delivery
await timer.WaitForTriggerAsync(ct);                  // Asynchronous pulling

// Diagnostics
TimerStatistics st = timer.GetStatistics();
timer.ResetStatistics();
```

Constructors start timers by default, so the extra `timer.Start()` in many examples is redundant. It is harmless now; the one time it did cause trouble (duplicate heap insertion) was fixed in 1.0.0.

`TimerElapsedEventArgs.MissedTicks` is an overload signal: a value greater than 0 is the number of missed ticks coalesced before this tick. The library does not replay them.

### `HighResolutionScheduler` — the scheduler thread

A dedicated thread plus an indexed binary heap, with O(log n) insertion and removal. Each timer creates its own scheduler by default; to share one, explicitly pass the same instance or `HighResolutionScheduler.Shared`.

```csharp
using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions
{
    Name = "My.ControlLoop",
    Mode = WaitMode.Hybrid,
    ThreadPriority = ThreadPriority.Highest,
    UseRealTimePriority = false,   // Raises priority to TIME_CRITICAL(15) on Windows; see "Reducing tail latency"
});

var a = new HighResolutionTimer(new HighResolutionTimerOptions { Period = TimeSpan.FromMilliseconds(1),  Scheduler = scheduler });
var b = new HighResolutionTimer(new HighResolutionTimerOptions { Period = TimeSpan.FromMilliseconds(10), Scheduler = scheduler });

Console.WriteLine($"{scheduler.TimerCount} timers, {scheduler.TotalFiredTicks} ticks fired in total, thread id={scheduler.ThreadId}");
```

When the heap is empty, the thread blocks on a wake-up event and uses no CPU. `HighResolutionScheduler.Shared` is a process-wide shared instance; do not `Dispose` it. Create your own with `new` if you need isolation. `ThreadPriority` only goes as high as `Highest`, which maps to a priority category on Windows; use `UseRealTimePriority = true` for an actual priority of 15.

### `HighResolutionWait` — waiting without a timer

```csharp
HighResolutionWait.Sleep(TimeSpan.FromMicroseconds(200), WaitMode.Hybrid);
await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(5), WaitMode.Hybrid, ct);
HighResolutionWait.SpinUntil(HiResClock.Deadline(TimeSpan.FromMicroseconds(50)));

// Measure this machine first, then choose a strategy and window
TimerPlatformInfo info = HighResolutionWait.GetPlatformInfo();
KernelWakeLatency wake = HighResolutionWait.MeasureKernelWakeLatency(400, TimeSpan.FromMilliseconds(1));
Console.WriteLine(info);
Console.WriteLine(wake);   // p50 / p99 / max and the recommended spin window
```

The `RecommendedSpinWindowNanoseconds` returned by `MeasureKernelWakeLatency` can be assigned directly to `HighResolutionTimerOptions.SpinWindowNanoseconds` with `AdaptiveSpinWindow = false`. This is useful for calibrating once at startup and then keeping the setting fixed.

### `TimerStatistics` — checking actual accuracy

```csharp
var st = timer.GetStatistics();
Console.WriteLine(st);                           // One-line summary: ticks / rate / missed / lateness percentiles
Console.WriteLine(st.ToLatenessHistogramText()); // log2-bucket histogram + cumulative percentiles
double p99 = st.GetLatenessPercentile(0.99);
bool healthy = st.JitterRatio < 0.05 && st.MissedTicks == 0;
```

The histogram has 64 power-of-two buckets. Bucket widths reflect the order of magnitude of the error, so reported percentiles are conservatively high: better to overestimate the error than underestimate it.

---

## New APIs in 1.2

Version 1.2 adds a handful of surface-level capabilities on top of the 1.1 core, none of which change the default precision path. Everything below is covered by the test suite (142 tests on net8.0 / net10.0).

- **`await using` (`IAsyncDisposable`)** on `HighResolutionTimer`, `HighResolutionScheduler` and `HighResolutionSchedulerPool` (net8.0+). When a timer owns its scheduler, `DisposeAsync` awaits that thread exiting cleanly instead of blocking a thread-pool thread; a shared scheduler is only detached from, matching the synchronous `Dispose` semantics.
- **`TimerStatistics.ToCsv()` / `ToJson()`** — machine-readable scalar metrics (invariant culture; CSV with header, valid JSON parseable by `JsonDocument`; fields suffixed `_ns` are nanoseconds).
- **`JitterNanoseconds`** — spreads the due point of each tick by a random `[0, JitterNanoseconds]` offset for thundering-herd avoidance. Default `0` keeps the strict `firstDue + n*period` grid; enabling it deliberately gives up that invariant (that is the point). The offset is non-negative, so `LatenessNanoseconds` still only measures lateness relative to the tick's own due point. Must be smaller than one period.
- **`OffloadCallback`** — runs the callback/event on a thread-pool thread so the scheduler thread returns to waiting immediately. Single-in-flight back pressure: while a delivery is running, the next due tick is dropped and counted in `HighResolutionTimer.OffloadedCallbackDrops` (never an unbounded queue). This trades away callback-delivery precision for non-blocking — opt in deliberately.
- **`HighResolutionTimers.At(DateTimeOffset, Action, WaitMode)`** — one-shot trigger at an absolute wall-clock instant (converted to a relative delay, then driven by the monotonic clock). Past / exactly-now times are clamped to fire as soon as possible.
- **`HighResolutionSchedulerPool`** — holds `N` schedulers and spreads timers across them via `Next()` (round-robin) or `LeastLoaded()`, with an indexer and `CreateTimer(...)`. For "callbacks are not tiny and there are many of them", it parallelizes firing across cores instead of serializing on one thread.
- **`VirtualTimeProvider`** (net8.0+) — a deterministic `TimeProvider` for tests: `Advance(TimeSpan)` / `AdvanceTo(long)` move a virtual clock and fire due timer callbacks synchronously in order. It does **not** drive real `HighResolutionTimer`s — for real low-latency firing use `HighResolutionTimeProvider`; for unit tests that must not actually wait, use `VirtualTimeProvider`.

```csharp
using OODD.Timer;

// await using + zero-allocation callback, then export metrics
await using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
{
    Period = TimeSpan.FromMilliseconds(1),
    Callback = Sample,                 // runs on the scheduler thread, allocation-free
    JitterNanoseconds = 50_000,        // spread due points by up to 50us (anti thundering-herd)
});

Console.WriteLine(timer.GetStatistics().ToCsv());
Console.WriteLine(timer.GetStatistics().ToJson());

// many non-trivial callbacks: spread them across 4 scheduler threads
await using var pool = new HighResolutionSchedulerPool(4);
for (int i = 0; i < 32; i++)
    pool.CreateTimer(TimeSpan.FromMilliseconds(5), OnTick, WaitMode.Hybrid);

// absolute-time one-shot
using var once = HighResolutionTimers.At(DateTimeOffset.UtcNow.AddSeconds(3), () => Console.WriteLine("three seconds from now"));

// deterministic virtual clock for tests
var clock = new VirtualTimeProvider();
using var parked = clock.CreateTimer(_ => Console.WriteLine("tick"), null,
    TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
clock.Advance(TimeSpan.FromSeconds(30));    // fires now, no real waiting
```

---

## Where the accuracy limits are

"Microsecond-level" means little without explaining what cannot be achieved.

First, the clock granularity on this machine is 100ns. The monotonic clock runs at 10MHz, with 100ns per tick, so finer differences cannot be distinguished. On other platforms, use the actual value of `HiResClock.Frequency`. This is not an upper bound on callback error.

Second, the library cannot impose a hard upper bound on tail latency. GC, JIT, kernel preemption, interrupts, and callback execution time can all cause spikes; without runtime/kernel traces, latency values alone cannot identify the cause. In one set of 1ms-period tests on this machine, default-priority p99 was in the thirty-something microsecond range. Enabling `UseRealTimePriority` brought p99 to 1.6–2.9µs and p99.9 to around 50µs, but max still ranged from hundreds of microseconds to several milliseconds. Real-time scheduling, isolated cores, and power policies can improve this further, but cannot by themselves eliminate CLR pauses or guarantee physical output timing.

Third, callbacks must be shorter than the period. When a callback exceeds the period, the library does not queue up catch-up firings; it coalesces missed ticks. `MissedTicks` increases and `Overrun` becomes true, but the grid does not drift. Overload appears as dropped ticks, not an ever-slower timer.

Fourth, server GC and large heaps worsen spikes. `GCSettings.IsServerGC` conflicts with this library's tail-latency goals.

Fifth, high-resolution waits on Windows require Windows 10 1803 or later. On older systems, or if handle creation fails, the library falls back to ordinary wait handles and `Thread.Sleep`, returning to system-tick-level accuracy. The adaptive window then grows automatically to compensate. Functionality is unaffected, but CPU usage rises. `GetPlatformInfo().KernelHighResolutionWaits` accurately reports which path is in use.

One related measurement: even with a high-resolution handle, any sleep request of 1ms or less on this machine is quantized to an approximately 500µs grid (a 50µs request actually sleeps 518–543µs; 100µs sleeps 516–598µs; 500µs sleeps 0.53–2.7ms; 1ms sleeps 1.06–1.56ms). Hybrid mode therefore effectively spins continuously at periods of 1ms and below, which is also why p50 can reach 100ns. Longer periods spend more time sleeping and save more CPU, at the cost of a kernel wake-up overshoot on each tick: at 100Hz, Hybrid p99 is around fifty microseconds, while pure spinning brings it back to 6µs at the cost of a full core. Approaching microsecond-level timing in both cases while saving CPU at longer periods requires `UseRealTimePriority` and a sufficiently large spin window.

Sixth, Linux and macOS use `nanosleep`. Sub-millisecond waits are usually achievable (tens to hundreds of microseconds), but depend on kernel HZ, `CONFIG_HRTICK`, CPU frequency scaling, and scheduler load. Verify on the target machine with `MeasureKernelWakeLatency()`; do not trust numbers from any documentation, including this README.

Seventh, pull mode adds a consumer-thread wake-up on top of push mode. Delivery latency for `WaitForTrigger()` is the thread wake-up time—roughly a dozen to tens of microseconds at p50 on this machine. `WaitForTrigger(spinWindow)` uses busy-waiting to bring this layer down to around 1µs, but p99 still reflects time during which your thread is preempted. Neither changes scheduling accuracy; they change when your code gets to run. Use callbacks if you want even tail latency to approach scheduling accuracy.

---

## Reducing tail latency: process-level tuning

Both settings in `RuntimeTuning` are process-wide and affect all code in the process. They are therefore disabled by default and require an explicit call.

```csharp
// SustainedLowLatency postpones some blocking Gen2 collections and may increase memory use, but does not eliminate all GC pauses.
using (RuntimeTuning.RequestGcLatencyMode(GCLatencyMode.SustainedLowLatency))
{
    // Reference-counted: the original mode is restored only when the last lease is released,
    // so A's Dispose cannot prematurely restore a mode that B still depends on
}

// Windows system tick: request 1ms instead of 15.6ms. On this machine, Thread.Sleep(1) p50 went from 15.56ms to 1.99ms,
// and System.Threading.Timer(1ms) per-tick lateness went from 14.55ms to 0.76ms.
// The library's high-resolution path does not use the system tick, so it is unaffected. This is useful when the platform
// falls back to managed sleep, or when other code in the same process still uses coarse ticks.
using (RuntimeTuning.RequestSystemTimerResolution(1)) { /* ... */ }
```

You can also let the timer manage this: `HighResolutionTimerOptions.PreferSustainedLowLatencyGc = true`.

### Windows: raise the scheduler thread to TIME_CRITICAL

This is the most direct setting for microsecond-level timing. .NET's `ThreadPriority` only goes up to `Highest` (base priority 10), while Windows also has priority 15:

```csharp
var timer = new HighResolutionTimer(new HighResolutionTimerOptions
{
    Period = TimeSpan.FromMilliseconds(1),
    Mode = WaitMode.Hybrid,
    UseRealTimePriority = true,   // Applies only on Windows; silently ignored on other platforms
});
```

Both `HighResolutionTimerOptions` and `HighResolutionSchedulerOptions` expose this switch (use the latter with a shared scheduler). Measurements on this machine at a 1ms period, over 20000 ticks, discarding the first 800:

| Configuration | p50 | p95 | p99 | p99.9 | max |
|---|---:|---:|---:|---:|---:|
| Default (Highest) | 0.1µs | 0.6–0.9µs | 36.5–45.3µs | 145–284µs | 0.68–1.69ms |
| `UseRealTimePriority` | 0.1µs | 0.5–0.6µs | 1.6–2.9µs | 50–56µs | 0.35–2.39ms |

Median values from three sets of samples, run alternately in the same process on the same machine. p99 drops from the thirty-something microsecond range to 1.6–2.9µs, and p99.9 falls to one fifth.

High-priority spinning can starve lower-priority threads on the same core; depending on locks held by those threads can also cause priority inversion. That is why this is disabled by default. It does not grant exclusive use of a core or prevent interrupts or GC.

The library's hot paths—waiting, spinning, the scheduling loop, and firing—are also annotated with `MethodImplOptions.AggressiveOptimization`, so they are fully optimized from the first call. This is not about instruction-level micro-optimization; it avoids background promotion by tiered compilation. Without the annotation, a 20-second run at a 1ms period consistently produced an OSR pause of about 6ms at the 5-second mark in all three A/B tests. With the annotation, that pause disappeared, and max dropped from 6.5ms to 0.14–0.98ms.

### Choosing the core for the scheduler thread

```csharp
using var timer = new HighResolutionTimer(new HighResolutionTimerOptions
{
    Period = TimeSpan.FromMilliseconds(1),
    Mode = WaitMode.Spin,
    ProcessorIndex = 2,
    ProcessorGroup = 0,
    UseRealTimePriority = true,
    Callback = ControlStep,
});
```

`ControlStep` is your own short callback. `ProcessorIndex = null` (the default) leaves affinity unset; the index identifies a **logical processor**, not a physical core. Windows uses an index within a group together with `ProcessorGroup`: for example, logical processor 2 in group 1 is `ProcessorGroup = 1, ProcessorIndex = 2`, not 66 placed into a 64-bit mask. A 32-bit process can represent only 0–31 in its group mask; use a 64-bit process for more processors. Linux uses system CPU IDs, and the group must be 0. Under container CPU quotas, `Environment.ProcessorCount` is not a list of usable CPU IDs.

Affinity is set inside the scheduler thread: it calls the native API, reads the mask back to verify it, and only then accepts timer tasks. Unsupported platforms throw during construction. If the system rejects the requested core, `Start()` throws `InvalidOperationException`, with the native error retained in `InnerException`; it does not silently continue on another core. With automatic startup, this exception is thrown by the constructor. Windows requires Windows 7+. macOS has no equivalent interface for strictly selecting a logical core, so the request is explicitly rejected rather than passing off an affinity tag as core binding. The net40 asset implements affinity only on Windows.

When multiple timers share a scheduler, set these two properties on `HighResolutionSchedulerOptions`. Supplying both `Scheduler` and `ProcessorIndex` on an individual timer is rejected, avoiding a configuration that appears to apply but has no effect. Configuration is copied at construction time; changing the options afterward does not migrate an already-running thread.

**Setting affinity does not reserve a core or make a thread immune to GC.** It reduces migration and cache invalidation, but other threads on that core, interrupts, and hyperthreads on the same physical core still compete for resources. Machines with P/E cores especially need separate measurements; choosing the wrong core can be slower. `UseRealTimePriority` does not reserve a core for your process either.

### How much can GC impact be reduced?

- **Continuous operation**: use `Callback` without also subscribing to `Elapsed`; preallocate arrays, queues, and workspaces, and keep logging, LINQ, boxing, and asynchronous waits out of the hot path. The empty-callback path can run without allocations, but it still pauses when other code in the process triggers GC.
- **Reducing collection impact**: `PreferSustainedLowLatencyGc` postpones some blocking Gen2 collections. It does not prevent Gen0/Gen1 collections, explicit collections, or collections under memory pressure. This option is a process-wide lease, released when the timer is disposed with `Dispose`; it does not affect only this thread.
- **Finite critical windows**: modern .NET provides `GC.TryStartNoGCRegion` to request a collection-free region based on an estimated process-wide allocation budget. Warm up first, then request it, and check the return value. Exhausting the budget, an explicit `GC.Collect`, or external code ending the region can invalidate the guarantee; `EndNoGCRegion` can also throw. Regions cannot be nested, must not wrap an infinite loop, and must not be independently switched on and off by each timer. The net40 BCL does not have this API.
- **True isolation from host GC**: put the actual control loop in a native execution thread/process that never enters the CLR, or in a motion controller, and exchange data through preallocated shared memory. Merely setting affinity on a C# timing thread, switching to NativeAOT (which still has GC), pinning object addresses, or having a native thread call back into C# every tick does not eliminate managed pauses. A separate managed process isolates it from the UI process's GC, but still has its own GC.

The library does not expose a "disable GC" boolean, because it cannot honor such a promise for a single managed thread. To reproduce comparisons of idle operation, affinity, finite allocation pressure, low-latency GC, and a short NoGCRegion:

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- --gc-probe 2 0
```

The output separately reports lateness at the scheduler's firing timestamp and at **callback entry**, GC counts, missed ticks, and allocations on the callback thread. A clock reading on the scheduler thread must not be presented as the time the control code actually began executing. Rejected or prematurely ended NoGCRegion requests are also reported.

In two comparison rounds on this machine at 1ms/Spin/Highest, each scenario showed zero allocations across 2000 callback intervals, and actual Gen0/Gen1 collections were observed in the pressure scenarios. Even when NoGCRegion successfully prevented collections, the worst callback-entry lateness with affinity was still 1.46–2.00ms. Neither CPU 2 nor CPU 15 was more accurate than running without affinity. This is why affinity is not enabled by default and the library does not promise to eliminate tail latency.

### A hardware clock is not a hard real-time executor

`HiResClock` currently uses `Stopwatch.GetTimestamp()` to access the system-selected high-resolution monotonic counter. On Windows this is QPC, usually already backed by a hardware counter. Reading the TSC directly changes only the cost of reading the clock; it does not prevent preemption or GC, and leaves you responsible for ordering, virtualization, and frequency conversion. Nor can an ordinary managed library exclusively own HPET/APIC timer interrupts. If physical pulses must be emitted on time, execution should happen in hardware—a timer peripheral, motion-control card, or FPGA—with the PC submitting timestamped tasks ahead of time.

Tuning options that can be combined, each requiring verification on the target machine:

1. `UseRealTimePriority = true` on Windows, or place the thread on an isolated core (using `isolcpus` / `cset` on Linux).
2. Workstation GC (`ServerGarbageCollection=false`) and reduced allocation; avoid closures and boxing in hot callbacks. Use `Callback` rather than `Elapsed` at high frequencies to avoid constructing event arguments on every tick.
3. Disable CPU frequency scaling and deep sleep states; plug laptops into power and disable power saving.
4. Use `WaitMode.Spin`, or set `SpinWindowNanoseconds` to the value recommended by `MeasureKernelWakeLatency()` so spikes do not inflate the window during operation.
5. For extremely demanding accuracy requirements, create a separate thread with `HighResolutionScheduler` and put only those one or two timers on it.

The trade-offs are visible in the benchmark table near the top of this document: raising scheduler priority and pinning a core tighten the tail, but push that core's CPU occupancy toward saturation.

---

## Usage pitfalls

Remember to `Dispose`. Timers do not disappear on their own; `using` is the simplest approach. `Dispose` is idempotent, and calling `Start()` afterward throws `ObjectDisposedException`.

Do not block inside callbacks. Callbacks run on the scheduler thread, so a slow callback holds up all timers on that scheduler. Put heavy work into a queue or use a separate scheduler.

Do not write to the console inside callbacks either. `Console.WriteLine` is synchronous, goes through conhost locks, and can take several milliseconds per call. Because it runs on the scheduler thread, it directly delays the next tick. This explains paired sequence numbers in logs where "a printed tick is immediately followed by another late tick." Record into a ring buffer and print afterward. If you only need the distribution, use `GetStatistics()`; do not assess accuracy by printing whenever a threshold is exceeded.

`Stop()` does not guarantee cancellation of a tick already in flight, matching the semantics of `System.Threading.Timer.Change`: a callback may still be executing when the call returns. To ensure there is no further tick, check `Sequence` or use a state flag.

Callback exceptions are swallowed by default and reported through `Error`. `SuppressCallbackExceptions` defaults to `true` to keep the scheduler thread alive; exceptions are reported through the `Error` event and increment `TimerStatistics.ErrorCount`. During development, subscribe to `Error`, or set the option to `false` to let exceptions propagate directly.

Do not `Dispose` the shared scheduler. `HighResolutionScheduler.Shared` is process-wide; create your own if you need isolation.

Sub-millisecond periods on Windows require version 1803 or later. On older systems or if handle creation fails, `Kernel` mode falls back to the 15.6ms tick, in which case use `Spin`. You can also use `RequestSystemTimerResolution(1)` to reduce the fallback sleep granularity to 2ms (measured), allowing the adaptive window to shrink accordingly. If unsure, let `GetPlatformInfo().RecommendedMode` choose for you.

Avoid `Kernel` waits on short-lived threads. High-resolution wait handles are thread-local. Long-lived threads such as the library's scheduler are fine, but frequent calls on thread-pool threads can repeatedly create handles.

---

## TimeProvider integration: one clock for testing and production

`HighResolutionTimeProvider` can be registered directly in DI to replace `TimeProvider.System`:

```csharp
builder.Services.AddSingleton<TimeProvider>(HighResolutionTimeProvider.Instance);

TimeProvider clock = HighResolutionTimeProvider.Instance;
long ts = clock.GetTimestamp();                       // From the monotonic clock, measured at 10MHz on this machine
ITimer t = clock.CreateTimer(_ => Poll(), null,        // High-resolution implementation, not System.Threading.Timer
    TimeSpan.FromMicroseconds(500), TimeSpan.FromMicroseconds(500));
```

The boundary matters. The high-resolution path applies to `GetTimestamp`, `TimestampFrequency` (10MHz on this machine), and `CreateTimer` (sub-millisecond). `GetUtcNow()` and `LocalTimeZone` still come from the system wall clock. The library does not invent dates or time zones; it improves timing resolution. Use `new HighResolutionTimeProvider(myScheduler)` when you need scheduler-thread isolation.

---

## Building, testing, and benchmarking

```powershell
dotnet build OODD.Timer.slnx -c Release          # 0 warnings, 0 errors (5 TFMs)
dotnet test  tests/OODD.Timer.Tests -c Release   # 123 behavioral tests (net8.0 / net10.0)
dotnet pack  src/OODD.Timer -c Release           # Produces nupkg + snupkg, including README / LICENSE / NOTICE
```

Cross-framework smoke tests run each asset on its actual runtime:

```powershell
dotnet build tests/OODD.Timer.CompatSmoke -c Release             # net40 / net48 / net8 / net9 / net10
dotnet build tests/OODD.Timer.CompatSmoke.NetStandard -c Release # Test the netstandard2.0 asset separately
```

`CompatSmoke` first prints the asset actually loaded by reading `TargetFrameworkAttribute`, then tests the clock, periodic callbacks, all three wait modes, synchronous and asynchronous pulling, cancellation, one-shot timers, statistics histograms, kernel wake-up calibration, and `timeBeginPeriod` reference counting. It returns 0 only if all assertions pass. Run the net40 and net48 executables directly in cmd; use `dotnet xxx.dll` for net8+. `CompatSmoke.NetStandard` references the netstandard2.0 output directly, because NuGet prefers the `net40` asset on .NET Framework; without this targeted test, that asset would not be exercised.

The most important test checks an invariant rather than "run once and look at the numbers." It iterates over every tick's due timestamp and asserts:

```csharp
HiResClock.Add(firstDue, slot * periodTicks) == tick.Due   // slot accumulates (Missed + 1)
```

In other words, the grid must be exactly an integer multiple of the period. The test also requires at least 90% of ticks to be on time, with the final tick still less than one system tick late. This pins "no drift" to a test rather than the luck of a single sample.

Timing assertions depend on real elapsed wall-clock time, so `xunit.runner.json` disables collection parallelism; parallel runs would contaminate each other's measurements.

Benchmark (`<periodMs>` is the period in milliseconds; `<ticks>` is the tick count):

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- <periodMs> <ticks>
# Examples: -- 1 10000 (10000 ticks at a 1ms period), -- 0.2 5000 (sub-millisecond)
```

It first prints platform information and measured kernel wake-up latency for this machine, then a Markdown comparison table ready to paste into a README, the library's internal statistics, and a lateness histogram.

---

## Frequently asked questions

**Q: Why not just use `Thread.SpinWait`, or enable a high-resolution mode for `Task.Delay`?**

`Task.Delay` uses the same 64Hz tick. `Thread.SpinWait` only busy-waits: it has neither the hybrid "kernel sleep until the window, then spin" strategy nor absolute-grid scheduling. The value of this library is combining those three pieces while making accuracy measurable and testable with assertions.

**Q: Isn't calling `timeBeginPeriod(1)` to change the system tick to 1ms enough?**

No, though it is worth trying. It worked in measurements on this machine: `Thread.Sleep(1)` p50 dropped from 15.56ms to 1.99ms, and per-tick lateness for a 1ms `System.Threading.Timer` dropped from 14.55ms to 0.76ms. There are two problems. First, the limit remains in milliseconds; microseconds still require spinning or high-resolution wait handles. Second, this is a process- and system-wide setting: all waits in the same process are forced to wake at 1ms intervals, increasing power use and context switching. `RuntimeTuning.RequestSystemTimerResolution` exposes this switch; see [process-level tuning](#reducing-tail-latency-process-level-tuning) above for its uses.

**Q: I currently use `Haukcode.HighResolutionTimer`. Can I switch directly?**

Yes. See the mapping table under [Pull mode](#pull-mode-waiting-for-the-next-tick-synchronously-or-asynchronously). `SetPeriod(double)`, `Start`, `Stop`, `WaitForTrigger()`, and `Dispose()` have matching names and semantics, with `WaitForTrigger(spinWindow)` and `WaitForTriggerAsync(ct)` added. This library provides "drift-free absolute-grid scheduling" and "measurable lateness" by default, which that library does not. After switching, `GetStatistics()` directly reports the accuracy achieved.

**Q: Pull-mode median latency will not reach 0.1µs. Can that 100µs floor be lowered?**

The clock floor on this machine is 100ns, not 100µs. The 100µs figure is a practical baseline when every pull requires a kernel wake-up and thread scheduling; it is a cost of this usage pattern, not a library limit. To bring the median down to 1µs, give `WaitForTrigger(spinWindow)` a busy-wait window, at the cost of CPU usage. For better p99 as well, use push mode: the callback runs on the scheduler thread when due, with p50 of 0.2µs on this machine. The tens of microseconds at p99 are time during which the operating system preempts your thread; a user-mode library cannot eliminate that.

**Q: How many timers can run at once?**

The scheduler uses an indexed binary heap with O(log n) insertion and removal. How many a single thread can handle depends on callback execution time per tick and wake-up accuracy, not the count alone. Measure `GetStatistics().MissedTicks` and `JitterRatio` first, then decide whether to split the work across multiple schedulers.

**Q: Are Unity, Godot, older projects, AOT, and Blazor WASM supported?**

Unity 2021.2+ and Godot 4 targeting .NET 6/7 receive the `netstandard2.0` asset and can use it directly. Godot 4 with .NET 8, and any net8+ project, receive the framework-specific asset with trimming and `IsAotCompatible` annotations. Older .NET Framework 4.0–4.8 projects receive the `net40` asset without needing a framework upgrade. Browsers (WASM) lack the high-resolution monotonic clock and kernel timers used here; `GetPlatformInfo()` reports the fallback path, and accuracy is outside this library's control.

**Q: I am doing motion control. Can these large fluctuations be eliminated?**

First, distinguish the distribution from its tail. Measurements on this machine (1ms period, 20000 ticks, discarding the first 800, default `Hybrid`, Release build) gave p50 of 0.1µs, p95 of 0.6–0.9µs, p99 of 36–45µs, p99.9 of 145–284µs, and max of 0.7–1.7ms. With `UseRealTimePriority` enabled under the same conditions, p99 was 1.6–2.9µs and p99.9 was 50–56µs. These tails are not caused by the wait algorithm; they arise when execution is taken away from the thread—time slices, DPCs, other processes, and GC are outside a user-mode library's control. Switching to `WaitMode.Spin` does not remove them either: the bottleneck is preemption, not the waiting strategy. Raising thread priority is what can push competing work aside.

Here is what you can do:

1. On Windows, enable `HighResolutionTimerOptions.UseRealTimePriority = true`. In these measurements, this is the only switch that brings p99 from tens of microseconds into single-digit microseconds, at the cost of occupying a full logical core.
2. Keep callbacks allocation-free and I/O-free. Use `HighResolutionTimerOptions.Callback`; write logs to a ring buffer and print afterward.
3. Warm up and discard the first 1–2 seconds after startup. The first tick includes scheduler-thread startup and JIT; measured lateness on this machine was 1–3ms.
4. Use a Release build. Enable `PreferSustainedLowLatencyGc` under allocation pressure.
5. Interpolate trajectories by timestamp. Use `TimerElapsedEventArgs.FiredTimestamp` (or read `HiResClock.Now()` yourself in an allocation-free callback) to calculate actual elapsed time: position = f(t). A callback that is 3ms late means the command update is 3ms late, rather than turning into a position-calculation error. Conversely, if the control law assumes every tick is exactly 1ms, any jitter accumulates into error.
6. For hard real-time requirements such as servo loops or interpolation cycles, put the control loop in a real-time kernel, motion-control card, or FPGA, leaving trajectory planning and submission to the PC. This is an operating-system boundary that raising `UseRealTimePriority` cannot cross.

**Q: Why is `Hybrid` the default rather than `Kernel`?**

Most people use this library because they need timing more accurate than the system tick. On platforms with sub-millisecond kernel waits, `Hybrid` trades some CPU usage for microsecond-level accuracy. If the platform does not support it, the window grows automatically: correctness is unaffected, but CPU usage rises. In that case, `GetPlatformInfo().RecommendedMode` recommends `Spin`.

**Q: How do I find out what accuracy I can actually get on my machine?**

Run `dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000` and compare the printed table with your requirements. Do not rely on numbers from anywhere else, including this README.

---

## License

[Apache License 2.0](https://github.com/OODD-DevNet/OODD.Timer/blob/main/LICENSE) © 2026 OODD

Commercial use, modification, redistribution, and closed-source use are permitted. When redistributing, retain [LICENSE](https://github.com/OODD-DevNet/OODD.Timer/blob/main/LICENSE) and [NOTICE](https://github.com/OODD-DevNet/OODD.Timer/blob/main/NOTICE), and state what you changed. The explicit patent grant in Apache-2.0 (Section 3) is its main distinction from MIT.
