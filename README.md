# OODD.Timer

跨平台的 **.NET 亚微秒级高精度定时器库**。用单调时钟 + 自旋/混合等待替代系统时钟节拍，把定时器误差从常见的 **10~15ms** 压到 **微秒甚至亚微秒** 量级。

源码、问题反馈与更新日志：[github.com/OODD-DevNet/OODD.Timer](https://github.com/OODD-DevNet/OODD.Timer)

- 目标框架：`net8.0` / `net9.0` / `net10.0`（Windows、Linux、macOS、iOS、Android、FreeBSD 都能跑）
- 零第三方依赖，纯 `System.*` 实现；支持 trimming 与 Native AOT
- 全部公开成员带中文 XML 文档，IDE 里悬停即得
- Apache-2.0 许可（含明确的专利授权）

---

## 目录

- [为什么普通定时器不准](#为什么普通定时器不准)
- [实测数字](#实测数字本机-windows-11--net-10)
- [快速开始](#快速开始)
- [三种等待模式怎么选](#三种等待模式怎么选)
- [拉模式：同步/异步等下一拍](#拉模式同步异步等下一拍)
- [API 导览](#api-导览)
- [精度的天花板在哪里](#精度的天花板在哪里)
- [把尾延迟压下去：进程级调优](#把尾延迟压下去进程级调优)
- [用法陷阱](#用法陷阱)
- [TimeProvider 集成](#timeprovider-集成把测试与生产统一到一套时钟上)
- [构建、测试与基准](#构建测试与基准)
- [常见问题](#常见问题)
- [许可](#许可)

---

## 为什么普通定时器不准

`System.Threading.Timer`、`PeriodicTimer`、`Task.Delay`、`Thread.Sleep` 这些看起来是"毫秒"接口，实际上它们都挂在**系统时钟节拍**上：

- Windows 默认的定时器分辨率是 **15.625ms**（64Hz）。`Thread.Sleep(1)` 不是睡 1ms，是睡到下一个节拍边沿——实测 p50 = **15.56ms**（600 次采样，min 9.87 / max 17.27）。
- 而且这些 API 是**自我节律（self-paced）**的：每次醒来后重新计时，于是每次的超时会**累加**。1ms 的"周期"跑 10000 拍，实际已经过去 15 秒。

本库做三件事来摆脱这个天花板：

| 做法 | 解决的问题 |
|---|---|
| 计时改用 `Stopwatch.GetTimestamp()` 同源的高分辨率单调时钟（实测 10MHz，1 tick = 100ns），而不是 64Hz 的节拍 | 分辨率从 15.6ms 降到 100ns |
| 等待改用"内核高分辨率等待 + 尾部自旋"的混合策略，而不是让内核按节拍量化 | 唤醒延迟从 15.5ms 降到亚毫秒；最后一段用自旋收紧到 100ns 级 |
| 调度改成**绝对格点**：下一拍的到期点 = `第一拍到期点 + n × 周期`（整数 tick 运算），永不 `+= 实际耗时` | 单次抖动不会累加，长跑零漂移；过载时合并空拍而不是补发风暴 |

> 关于 `timeBeginPeriod(1)`（本库的 `RuntimeTuning.RequestSystemTimerResolution(1)`）：本机实测它**确实有效**——`Thread.Sleep(1)` 的 p50 从 15.56ms 降到 **1.99ms**，1ms 周期的 `System.Threading.Timer` 每拍迟到 p50 从 14.55ms 降到 **0.76ms**，`Dispose` 后可逆。但它的天花板仍然是**毫秒**，而且是**进程/整机级**副作用（同进程其它线程也被迫按 1ms 唤醒，更耗电）。到 µs 级靠的是 `CreateWaitableTimerExW` 的 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` 标志（Windows 10 1803+）加上尾部自旋，本库默认就在用它，不需要你动系统节拍。

---

## 实测数字（本机 Windows 11 / .NET 10）

复现命令：

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000
```

测量口径（每个实现都用同一把尺子）：每次触发相对"锚点 + n × 周期"记录累计落后量，再拆成两个指标——

- **每拍迟到**：相邻两次落点的间隔减去周期。只看抖动，不随运行时长累加。
- **累积漂移**：首尾之差。为 0 表示按绝对格点推进；非 0 表示误差在累加。

周期 1ms × 10000 拍（约 10 秒）：

| 实现 | 每拍迟到 p50 | 每拍迟到 p95 | 每拍迟到 p99 | 每拍迟到 max | 累积漂移 | 漂移/拍 | 期间 CPU |
|---|---:|---:|---:|---:|---:|---:|---:|
| Thread.Sleep(1ms) | 14531.1µs | 15292.0µs | 15992.5µs | 20986.8µs | 145313.19ms | 14532.77µs | 219ms |
| System.Threading.Timer (1.0ms) | 14526.0µs | 15271.9µs | 16024.6µs | 20744.2µs | 46755.62ms | 4676.03µs | 469ms |
| PeriodicTimer (1.0ms) | 14526.3µs | 15258.0µs | 15945.6µs | 18368.0µs | 144815.43ms | 14482.99µs | 984ms |
| HighResolutionWait.Sleep(Kernel) | 579.4µs | 795.2µs | 1203.9µs | 7089.0µs | 5919.13ms | 591.97µs | 125ms |
| HighResolutionWait.Sleep(Spin) | 0.1µs | 1.0µs | 73.6µs | 867.1µs | 30.05ms | 3.00µs | 9734ms |
| **OODD.Timer (Kernel)** | **37.7µs** | 340.4µs | 573.1µs | 13313.3µs | 78.86ms | 7.89µs | **203ms** |
| **OODD.Timer (Hybrid)** | **0.0µs** | **1.8µs** | **45.1µs** | 1181.3µs | **0.62ms** | **0.06µs** | 9859ms |
| **OODD.Timer (Spin)** | **0.0µs** | **1.9µs** | **43.4µs** | 3181.5µs | 4.83ms | 0.48µs | 9688ms |

怎么读这张表：

- 普通定时器的每拍迟到就是 **14.5ms**，而且 10000 拍之后累计落后 **145 秒**——这是"10~15ms 误差"的真相，它不是抖动，是**结构性累加**。
- `OODD.Timer (Hybrid)` 的每拍中位迟到低于表格分辨率（0.0µs），p95 = **1.8µs**，10 秒长跑累计漂移 **0.62ms**（漂移/拍 0.06µs）。库内自统计（相对本拍应到时刻）给出 p50 = **128ns**、p99 = **65µs**。
- `OODD.Timer (Kernel)` 是"几乎不占 CPU"的档位：每拍迟到 p50 = **37.7µs**（因为按绝对格点推进，唤醒的固定偏置不会累加），CPU 只有 203ms/10s ≈ **2%**。
- `HighResolutionWait.Sleep(Kernel)` 那一行是**裸等待原语**，它是自我节律的，所以 579µs 的每次超时会累加成 5.9 秒——这正说明"精度"和"无漂移"是两件事，库的价值在于后者。
- `Hybrid`/`Spin` 的 CPU 接近 100% 一个核，是自旋的代价；`Kernel` 行 `每拍迟到 max = 13.3ms` 是过载时合并空拍的表现（见[精度的天花板](#精度的天花板在哪里)）。

> **这些数字只描述跑这条命令的那台机器**（Windows 11 10.0.26200、16 核、.NET 10、控制台无其他负载）。换机器、换系统、加了后台负载必须重测；基准程序会把 Markdown 表格直接打印出来，贴回你的 README 或 issue 即可。

亚毫秒节拍的实测见 [docs/benchmarks.md](https://github.com/OODD-DevNet/OODD.Timer/blob/main/docs/benchmarks.md)。

---

## 快速开始

### 安装

```powershell
dotnet add package OODD.Timer
```

（尚未发布到 nuget.org 时：`dotnet pack src/OODD.Timer -c Release`，然后把 `bin/Release/*.nupkg` 加进本地源。）

### 10 行上手

```csharp
using OODD.Timer;

using var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(1));

timer.Elapsed += (_, e) =>
{
    // e.LatenessNanoseconds：这一拍比"应到时刻"晚了多少纳秒
    Console.WriteLine($"tick {e.Sequence}, lateness {e.LatenessNanoseconds:F0}ns");
};

timer.Start();
Console.ReadKey();
```

### 我最该选哪种写法？

| 我想要… | 用什么 |
|---|---|
| 回调式周期定时器 | `new HighResolutionTimer(period)` + `Elapsed` |
| 一行起一个周期回调 | `HighResolutionTimers.Periodic(TimeSpan.FromMilliseconds(200), () => DoWork())` |
| async 循环（`while (await ...)`） | `timer.WaitForNextTickAsync(ct)`，或 `HighResolutionTimers.RunPeriodicAsync(period, async ct => ...)` |
| 只要一次精确延时 | `HighResolutionWait.Sleep(duration)` / `SleepAsync(...)` |
| 阻塞式"等一拍、干一段活"（Haukcode 风格） | `timer.SetPeriod(ms)` + `timer.WaitForTrigger()` |
| 阻塞式但要求 µs 级投递 | `timer.WaitForTrigger(自旋窗口)` |
| 异步拉取、每拍一次 | `await timer.WaitForTriggerAsync(ct)` |
| 很多定时器一起跑，省线程 | 显式共享一个 `HighResolutionScheduler` |
| 现有代码用 `TimeProvider`/`ITimer` | `HighResolutionTimeProvider.Instance` |
| 想知道本机到底能多准 | `HighResolutionWait.GetPlatformInfo()` 与 `MeasureKernelWakeLatency()` |

---

## 三种等待模式怎么选

`WaitMode` 是本库唯一需要你做的架构级决策，它是一条"精度 ↔ CPU"的取舍曲线：

| 模式 | 原理 | 精度（本机 1ms 节拍） | CPU | 适合 |
|---|---|---|---|---|
| `Spin` | 全程忙等，每次迭代读单调时钟 | p50 0.0µs，p99 43µs | 独占 1 核 100% | 节拍 < 1ms、有专用/隔离核 |
| `Hybrid`（默认） | 内核睡到"到期前一个自旋窗口"，窗口内自旋到点；窗口按实测唤醒延迟自适应标定 | p50 0.0µs，p99 45µs | ≈ 窗口 / 节拍 | 绝大多数亚毫秒场景 |
| `Kernel` | 完全不自旋，靠操作系统定时对象唤醒 | p50 37.7µs（按格点），裸唤醒 p50 ≈ 0.58ms | ≈ 0（本机 2%） | 秒级节拍、移动端、省电 |

`Hybrid` 的关键是**自适应自旋窗口**：每次内核唤醒都测量"比预定唤醒点晚了多少"，晚到就把窗口放大到足以覆盖（留 1.25 倍余量），窗口明显偏大时再以每次 1/64 的速度缓慢收缩。窗口不收缩会退化成长期自旋，所以收缩同样重要。

窗口触顶（默认上限 32ms）意味着这台机器的内核等待太粗，此时 `Hybrid` 已等价于 `Spin`——**宁可让它触顶，也不要手工调小**，调小会直接表现为定时器迟到。

```csharp
var timer = new HighResolutionTimer(new HighResolutionTimerOptions
{
    Period = TimeSpan.FromMilliseconds(200),
    Mode = WaitMode.Hybrid,
    AdaptiveSpinWindow = true,          // 默认开启
    MaxSpinWindowNanoseconds = 8_000_000, // 窗口上限，默认 32ms
    CoarseChunkMilliseconds = 2,        // 粗等待分段，决定 Stop/插队的最早响应延迟
});
```

---

## 拉模式：同步/异步等下一拍

如果你习惯 `Haukcode.HighResolutionTimer` 那种"**等一拍 → 干活 → 再等一拍**"的写法，本库不用改结构，逐个换名字即可：

| Haukcode.HighResolutionTimer | OODD.Timer | 说明 |
|---|---|---|
| `new HighResolutionTimer()` | `new HighResolutionTimer()` | 默认 1ms 周期、自动启动 |
| `SetPeriod(double periodMS)` | `SetPeriod(double periodMilliseconds)` | 可小数（如 `16.67`）；运行中生效，会重新锚定相位 |
| `Start()` | `Start()` | 同步返回 |
| `Stop()` | `Stop()` | 同步返回；不取消"已在飞"的那一拍 |
| `WaitForTrigger()` | `WaitForTrigger()` | 阻塞到下一拍，零自旋。**推荐日常用** |
| — | `WaitForTrigger(TimeSpan spinWindow)` | 带尾部自旋，µs 级投递 |
| — | `await WaitForTriggerAsync(ct)` | 异步版，每拍完成一次，支持取消 |
| `Dispose()` | `Dispose()` | 幂等 |

```csharp
using OODD.Timer;

using var timer = new HighResolutionTimer();
timer.SetPeriod(10.0);          // 10ms
timer.Start();

for (int i = 0; i < 100; i++)
{
    timer.WaitForTrigger();     // 阻塞到下一拍（拉模式，单消费者）
    ProcessSample();
}
```

异步循环、µs 级投递：

```csharp
// PeriodicTimer 风格的 async 拉取
while (!ct.IsCancellationRequested)
{
    await timer.WaitForTriggerAsync(ct);
    await HandleOneTickAsync();
}

// 想要 µs 级：给一段自旋窗口。先量这台机器，再取推荐值
double recommended = HighResolutionWait.MeasureKernelWakeLatency().RecommendedSpinWindowNanoseconds;
while (running)
{
    timer.WaitForTrigger(TimeSpan.FromNanoseconds(recommended));
    DoTightWork();
}
```

**它和 `WaitOne()` 的区别：** 两者都阻塞到下一拍，但 `WaitOne` 等的是一个"置位事件"：上一拍留下的置位可能让下一次调用立即返回（空转），过载时两拍也会合并成一次唤醒。`WaitForTrigger` 以**触发序列号**为判据——每次调用交付一拍，且交付的都是"新发生"的那一拍；跟不上时合并到最新一拍（与推送模式一致）。新代码用 `WaitForTrigger`，`WaitOne` 只为兼容保留。

### 实测：拉模式到底能到多准

本机（Windows 11 10.0.26200 / 16 核 / .NET 10 / 调度线程 `WaitMode.Spin`）测同一批周期，每拍记录"进入用户代码的时刻 − 应到时刻"：

| 周期 | 方式 | p50 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|
| 1ms | 推送 `Elapsed`（调度线程） | 0.2µs | 3.7µs | 51.8µs | 151µs |
| 1ms | 拉取 `WaitForTrigger()` | 40.5µs | 114.3µs | 187.0µs | 682µs |
| 1ms | 拉取 `WaitForTrigger(200µs)` | 0.8µs | 8.6µs | 77.6µs | 465µs |
| 200µs | 推送 `Elapsed` | 0.1µs | 1.0µs | 36.1µs | 7.6ms |
| 200µs | 拉取 `WaitForTrigger()` | 6.0µs | 51.1µs | 102.4µs | 193µs |
| 200µs | 拉取 `WaitForTrigger(200µs)` | 0.6µs | 2.7µs | 82.1µs | 283µs |
| 100µs | 推送 `Elapsed` | 0.0µs | 0.3µs | 15.2µs | 243µs |
| 100µs | 拉取 `WaitForTrigger()` | 5.5µs | 46.7µs | 78.2µs | 139µs |
| 100µs | 拉取 `WaitForTrigger(200µs)` | 0.6µs | 3.7µs | 54.3µs | 173µs |

同样场景下的循环 CPU（单核 = 100%）：1ms 拉取纯事件 ≈ **0%**；1ms 拉取 200µs 窗口 ≈ **90%**；200µs 拉取 200µs 窗口 ≈ **95%**。

怎么读：

- **"100µs 地板"不是本库的地板。** 那是很多拉模式实现的现实水位——它们每次唤醒都要等一次内核事件/线程调度。本库推送模式的中位迟到就是 **0~0.2µs**（时钟粒度 100ns），`WaitForTrigger(窗口)` 的中位数也进了 **1µs** 以内。
- **拉模式的 p50 由"你在等什么"决定：** 纯事件等的是**一次线程唤醒**（本机十几~几十 µs）；给了自旋窗口，最后一段在**你的线程**上忙等，中位数就掉到 ~1µs。p99 的几十 µs 是操作系统抢占你线程的时间，任何用户态库都消不掉。
- **拉模式的 CPU 是诚实的：** 窗口要盖住内核"睡醒迟到"（本机 p50 ≈ 577µs）才不迟到，所以 1ms 周期配 200µs 窗口，稳态会逼近全程自旋（实测约 90% 一个核）。**想又准又省 CPU，用推送模式**：回调在调度线程上、到期即执行，自旋开销只有一份。
- **调度线程的模式同样影响拉取投递。** 拉取只决定"消费者什么时候拿到拍"，而拍的记账发生在调度线程上：同一组参数（1ms 周期 + 200µs 窗口），调度线程用 `Spin`/`Hybrid` 时拉取 p50 = 0.8~1.3µs，用 `Kernel` 时 p50 = **332~347µs**（调度线程自己就晚到几百 µs）。拉模式请让调度线程保持 `Spin`/`Hybrid`，别设成 `Kernel`。

语义细节：

- **同一时刻只允许一个消费者**调用 `WaitForTrigger*`；多个消费者会互相抢拍。
- **不会早于应到时刻返回。** 反过来，如果你的线程自己迟到超过一个周期，库会合并空拍（交付最新一拍），和推送模式过载时的行为一致。
- **可以随时 `Stop`。** 阻塞中的调用在约 50ms 内抛出 `InvalidOperationException`（长周期也一样，内部有 50ms 的状态复核节拍）。
- 单次定时器（`AutoReset = false`）也能用：`WaitForTrigger` 会把这唯一一拍交出去，然后定时器进入 `Stopped`。

---

## API 导览

### `HiResClock` — 时钟底座

高分辨率单调时间戳与单位换算，和 `Stopwatch` 同源。所有"时刻"都用 `long` 时间戳表达，所有"时长"用 tick 计数。

```csharp
long now = HiResClock.Now();                    // 10MHz，1 tick = 100ns
long deadline = HiResClock.Deadline(TimeSpan.FromMicroseconds(500));
double us = HiResClock.ToMicroseconds(HiResClock.Now() - now);
Console.WriteLine(HiResClock.DescribePlatform()); // 频率、是否高分辨率、OS
```

`HiResClock.Add` 做饱和加法并支持回绕安全的比较（`IsDue`），所以长时间运行不会因 tick 溢出而误判。

### `HighResolutionTimer` — 定时器本体

一个定时器**不占一个线程**：它挂在 `HighResolutionScheduler` 的调度线程上。

```csharp
// 构造：便捷重载 + options
new HighResolutionTimer();
new HighResolutionTimer(TimeSpan.FromMicroseconds(500));
new HighResolutionTimer(period, WaitMode.Spin);
new HighResolutionTimer(period, sharedScheduler);
new HighResolutionTimer(new HighResolutionTimerOptions { ... });

// 生命周期
timer.Start();
bool stopped = timer.Stop();                       // 不保证取消"已在飞"的那一拍
bool ok = timer.Change(dueTime, period);           // 改周期会重新锚定格点
timer.Reset();                                     // 清零统计并重新起表
timer.Dispose();                                   // 幂等；会摘除调度并释放等待资源

// 消费方式（任选其一）
timer.Elapsed += (_, e) => { /* e.DueTimestamp / FiredTimestamp / Sequence
                                 / MissedTicks / LatenessNanoseconds / IntervalNanoseconds / Overrun */ };
while (timer.WaitOne()) { /* 阻塞等下一拍（事件语义，兼容用） */ }
while (await timer.WaitForNextTickAsync(ct)) { /* 异步等下一拍 */ }

// 拉模式（Haukcode 风格）
timer.SetPeriod(10.0);                            // 毫秒，可小数
timer.WaitForTrigger();                           // 阻塞到下一拍
timer.WaitForTrigger(TimeSpan.FromMicroseconds(200)); // 带尾部自旋，µs 级投递
await timer.WaitForTriggerAsync(ct);              // 异步拉取

// 诊断
TimerStatistics st = timer.GetStatistics();
timer.ResetStatistics();
```

`TimerElapsedEventArgs.MissedTicks` 是过载信号：> 0 表示这一拍之前有几个空拍被合并（库**不会**补发风暴）。

### `HighResolutionScheduler` — 调度线程

单个专用线程 + 索引化二叉堆（O(log n) 插入/删除）。全进程默认共享一个；多定时器时复用它能省线程和上下文切换。

```csharp
using var scheduler = new HighResolutionScheduler(new HighResolutionSchedulerOptions
{
    Name = "My.ControlLoop",
    Mode = WaitMode.Hybrid,
    ThreadPriority = ThreadPriority.Highest,
});

var a = new HighResolutionTimer(new HighResolutionTimerOptions { Period = TimeSpan.FromMilliseconds(1),  Scheduler = scheduler });
var b = new HighResolutionTimer(new HighResolutionTimerOptions { Period = TimeSpan.FromMilliseconds(10), Scheduler = scheduler });

Console.WriteLine($"{scheduler.TimerCount} 个定时器，累计 {scheduler.TotalFiredTicks} 拍，线程 id={scheduler.ThreadId}");
```

要点：

- 空闲（堆空）时线程阻塞在唤醒事件上，**零 CPU**；有定时器时才工作。
- `HighResolutionScheduler.Shared` 是全进程共享实例，别 `Dispose` 它；需要隔离就 `new` 一个。
- 线程优先级只到 `ThreadPriority.Highest` 这一档——Windows 的 `ThreadPriority` 映射的是优先级类别，不是真正的实时优先级。

### `HighResolutionWait` — 只要等待，不要定时器

```csharp
HighResolutionWait.Sleep(TimeSpan.FromMicroseconds(200), WaitMode.Hybrid);
await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(5), WaitMode.Hybrid, ct);
HighResolutionWait.SpinUntil(HiResClock.Deadline(TimeSpan.FromMicroseconds(50)));

// 先量一下这台机器，再决定策略与窗口
TimerPlatformInfo info = HighResolutionWait.GetPlatformInfo();
KernelWakeLatency wake = HighResolutionWait.MeasureKernelWakeLatency(400, TimeSpan.FromMilliseconds(1));
Console.WriteLine(info);
Console.WriteLine(wake);   // 给出 p50/p99/max 与"推荐自旋窗口"
```

`MeasureKernelWakeLatency` 的返回值里 `RecommendedSpinWindowNanoseconds` 就是可以直接写进 `HighResolutionTimerOptions.SpinWindowNanoseconds` 的保守值（配 `AdaptiveSpinWindow = false` 使用，适合启动时标定一次然后固化）。

### `TimerStatistics` — 精度是不是真的达到了

```csharp
var st = timer.GetStatistics();
Console.WriteLine(st);                                  // 一行摘要：ticks/rate/missed/lateness 分位数
Console.WriteLine(st.ToLatenessHistogramText());         // log2 分桶直方图 + 累计百分位
double p99 = st.GetLatenessPercentile(0.99);
bool healthy = st.JitterRatio < 0.05 && st.MissedTicks == 0;
```

直方图用 2 的幂分桶（64 桶，桶宽即误差量级），所以报出的分位数是**保守偏大**的——宁可高估误差也不会低估。

---

## 精度的天花板在哪里

必须说清楚本库**做不到**什么，否则任何"微秒级"的说法都是假的。

1. **100ns 是地板。** 单调时钟频率 10MHz，一个 tick = 100ns。低于这个量级的差异，本库无法区分，只能说"已到点"。
2. **尾延迟不归定时器管，归操作系统管。** p50 已经在时钟粒度以内，p99/max 的尖峰来自 **GC 停顿**与**内核抢占**。本机实测纯自旋 1ms 等待的最大迟到尖峰 1.31ms，来源就是这两项。想要 µs 级 *硬* 保证，需要操作系统的实时调度（`SCHED_FIFO`、隔离核、关掉 CPU 频率调节），那不是任何库能凭空给的。
3. **回调必须比节拍短。** 回调超过周期时，库不会堆叠补发，而是合并空拍：`MissedTicks` 会增加、`Overrun` 为 true，但**格点不漂**。也就是说"过载"表现成丢拍，而不是越跑越慢。
4. **服务器 GC / 大堆会让尖峰变差。** `GCSettings.IsServerGC` 与本库的尾延迟是相互冲突的目标。
5. **Windows 的高分辨率等待需要 Windows 10 1803+。** 更早的系统（或句柄创建失败）会退回普通等待句柄 / `Thread.Sleep`，精度回到节拍量级——这时自适应窗口会自动放大来自救，功能不受影响，只是 CPU 变高。`GetPlatformInfo().KernelHighResolutionWaits` 会诚实地告诉你当前是哪条路径。
6. **Linux/macOS 走 `nanosleep`。** 亚毫秒通常可以（数十~数百µs），但取决于内核 HZ、`CONFIG_HRTICK`、CPU 频率调节与调度器负载。**必须在目标机上用 `MeasureKernelWakeLatency()` 复核**，别信任何文档里的数字，包括本文件。
7. **拉模式比推送模式多一层"消费者线程唤醒"。** `WaitForTrigger()` 的投递延迟 = 线程唤醒（本机 p50 十几~几十 µs）；`WaitForTrigger(窗口)` 用忙等把这一层压到 ~1µs，但 p99 仍然是"你的线程被操作系统抢占"的时间。两者都**不改变调度精度**，只改变"什么时候轮到你干活"——想连尾延迟都接近调度精度，用回调。

---

## 把尾延迟压下去：进程级调优

`RuntimeTuning` 里两项都是**进程级**设置，会影响同进程所有代码，所以默认全关，由你显式调用。

```csharp
// GC：进入 SustainedLowLatency（推迟阻塞式 GC，代价是托管内存占用上升）
// 只压 p99/max——本库定时器的 p50 本来就落在时钟粒度以内，尖峰才是 GC 造成的
using (RuntimeTuning.RequestGcLatencyMode(GCLatencyMode.SustainedLowLatency))
{
    // 引用计数式：最后一个释放时才恢复原模式，不会出现"A 的 Dispose 把 B 依赖的模式改回去"
}

// Windows 系统节拍：把 15.6ms 节拍请求到 1ms。本机实测 Thread.Sleep(1) p50 15.56ms → 1.99ms，
// System.Threading.Timer(1ms) 每拍迟到 14.55ms → 0.76ms。本库的高分辨率路径不经过系统节拍，
// 所以对它没影响；值得开的场景是"平台降级到托管 sleep 兜底"或"进程里有别的粗节拍代码"。
using (RuntimeTuning.RequestSystemTimerResolution(1)) { /* ... */ }
```

也可以让定时器自己管：`HighResolutionTimerOptions.PreferSustainedLowLatencyGc = true`。

真正有效的组合（按收益排序，全部需要你在目标机上验证）：

1. 客户端 GC（`ServerGarbageCollection=false`）+ 减少分配；热点回调里避免闭包与装箱。
2. 把调度线程放到**隔离核**（Linux `isolcpus`/`cset`、Windows 进程亲和性），并设 `ThreadPriority.Highest`。
3. 关掉 CPU 频率调节 / 深度睡眠状态；笔记本插电并关闭节能。
4. 用 `WaitMode.Spin` 或把 `SpinWindowNanoseconds` 设到 `MeasureKernelWakeLatency()` 的推荐值，别让窗口在运行中被尖峰撑大。
5. 精度要求极高时，用 `HighResolutionScheduler` 单独起一个线程，只放这一两个定时器。

详细的做法、每一步的实测影响与代价见 [docs/进阶：把误差压到极限.md](https://github.com/OODD-DevNet/OODD.Timer/blob/main/docs/%E8%BF%9B%E9%98%B6%EF%BC%9A%E6%8A%8A%E8%AF%AF%E5%B7%AE%E5%8E%8B%E5%88%B0%E6%9E%81%E9%99%90.md)。

---

## 用法陷阱

- **别忘了 `Dispose`。** 定时器不会自己消失；`using` 最好。`Dispose` 是幂等的，之后再 `Start()` 抛 `ObjectDisposedException`。
- **回调里不要阻塞。** 回调跑在调度线程上，一个慢回调会拖住同一调度器上的**所有**定时器。要做重活就把工作丢进队列，或用独立调度器。
- **`Stop()` 不保证取消"已在飞"的那一拍。** 这和 `System.Threading.Timer.Change` 语义一致：返回时可能仍有一个回调正在执行。需要"绝对不再有下一拍"，看 `Sequence` 或用状态标志。
- **回调抛异常默认被吞掉并计入 `Error`。** `SuppressCallbackExceptions` 默认 `true`（保护调度线程不挂），异常会通过 `Error` 事件上报并累加 `TimerStatistics.ErrorCount`。开发期建议订阅 `Error`，或把它设为 `false` 让异常炸出来。
- **别 `Dispose` 共享调度器。** `HighResolutionScheduler.Shared` 是全进程的；要隔离就 `new` 一个自己的。
- **亚毫秒周期在 Windows 上要求 1803+。** 更早的系统 / 句柄失败时 `Kernel` 模式会退回 15.6ms 节拍，此时该用 `Spin`；也可以 `RequestSystemTimerResolution(1)` 把兜底 sleep 的粒度压到 2ms（实测），让自适应自旋窗口跟着缩小。用 `GetPlatformInfo().RecommendedMode` 让库替你判断。
- **短命线程上别用 `Kernel` 等待。** 高分辨率等待句柄是线程本地的，长期线程（本库的调度线程）无所谓；线程池线程高频调用会反复建句柄。

---

## TimeProvider 集成（把测试与生产统一到一套时钟上）

`HighResolutionTimeProvider` 可以直接注册进 DI 替换 `TimeProvider.System`：

```csharp
builder.Services.AddSingleton<TimeProvider>(HighResolutionTimeProvider.Instance);

TimeProvider clock = HighResolutionTimeProvider.Instance;
long ts = clock.GetTimestamp();                        // 来自 10MHz 单调时钟
ITimer t = clock.CreateTimer(_ => Poll(), null,          // 高分辨率实现，不是 System.Threading.Timer
    TimeSpan.FromMicroseconds(500), TimeSpan.FromMicroseconds(500));
```

清楚边界：

- 走高分辨率路径的是 `GetTimestamp`/`TimestampFrequency`（10MHz）与 `CreateTimer`（亚毫秒）。
- **日期与时区不伪造**：`GetUtcNow()`、`LocalTimeZone` 仍来自系统 wall-clock。本库不做假时钟，只把精度提上来。
- 需要隔离调度线程时：`new HighResolutionTimeProvider(myScheduler)`。

---

## 构建、测试与基准

```powershell
dotnet build OODD.Timer.slnx -c Release          # 0 警告 0 错误
dotnet test  tests/OODD.Timer.Tests  -c Release  # 103 个行为测试（net8.0 / net10.0）
dotnet pack  src/OODD.Timer -c Release           # 产出 nupkg + snupkg，README / LICENSE / NOTICE 一并打包
```

测试里最关键的一条是**不变量测试**而不是"跑一次看数字"：它遍历每一拍的到期时间戳，断言

```csharp
HiResClock.Add(firstDue, slot * periodTicks) == tick.Due   // slot 累加 (Missed + 1)
```

即**格点精确等于周期整数倍**，同时要求 ≥90% 的拍准点、且最后一拍的迟到仍小于一个系统节拍。这样"无漂移"这件事被钉死在测试里，而不是靠一次采样的运气。

精度断言依赖真实挂钟，因此 `xunit.runner.json` 关掉了集合并行——并行跑会互相污染测量。

基准：

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- <周期ms> <拍数>
# 例：-- 1 10000（1ms 节拍跑 1 万拍）、-- 0.2 5000（亚毫秒）
```

它会先打印本机平台信息与内核唤醒延迟实测，再打印可直接粘进 README 的 Markdown 对比表、库内自统计与迟到直方图。

---

## 常见问题

**Q：为什么不直接 `Thread.SpinWait` 或者 `Task.Delay` + 高精度模式？**
A：`Task.Delay` 落在同一个 64Hz 节拍上；`Thread.SpinWait` 只有忙等一种形态，没有"内核睡到窗口 + 尾部自旋"的混合策略，也没有绝对格点调度。本库的价值是把这三件事组合起来，并且让精度可测量、可断言。

**Q：调一下 `timeBeginPeriod(1)` 把系统节拍改成 1ms 不就够了？**
A：不够，但值得一试——本机实测它确实生效：`Thread.Sleep(1)` 的 p50 从 15.56ms 降到 1.99ms，1ms 的 `System.Threading.Timer` 每拍迟到从 14.55ms 降到 0.76ms。问题有两层：① 天花板还是毫秒，你要微秒就绕不开自旋或高分辨率等待句柄；② 它是进程/整机级设置，同进程所有等待都被迫按 1ms 唤醒，功耗与上下文切换都涨。`RuntimeTuning.RequestSystemTimerResolution` 保留这个开关，用途见上文「进程级调优」。

**Q：我原来用 `Haukcode.HighResolutionTimer`，能直接换过来吗？**
A：能，见[拉模式](#拉模式同步异步等下一拍)的映射表：`SetPeriod(double)`、`Start`、`Stop`、`WaitForTrigger()`、`Dispose()` 名字和语义都对齐；另外多了 `WaitForTrigger(自旋窗口)` 和 `WaitForTriggerAsync(ct)`。它没做的"无漂移绝对格点"和"迟到可测量"本库默认就有，换过去后 `GetStatistics()` 能直接告诉你实际精度。

**Q：为什么拉模式的中位数进不了 0.1µs？100µs 的地板能再低吗？**
A：先纠正一件事：本库的时钟地板是 **100ns**，不是 100µs。100µs 是"每次拉取都要等一次内核唤醒/线程调度"的现实水位，属于拉模式这种用法的成本，不是库的成本。要把中位数压到 ~1µs，给 `WaitForTrigger(自旋窗口)` 一段忙等（代价是 CPU）；要连 p99 都好看，用推送模式——回调就在调度线程上、到期即执行，本机 p50 = 0.2µs。p99 那几十 µs 是你的线程被操作系统抢占的时间，用户态库消不掉。

**Q：能同时跑多少个定时器？**
A：调度器是索引化二叉堆，插入/删除 O(log n)；单线程能撑住多少个取决于"每拍回调耗时 + 唤醒精度"，而不是数量本身。先用 `GetStatistics().MissedTicks` 和 `JitterRatio` 量，再决定要不要拆多个调度器。

**Q：支持 Unity / Godot / AOT / Blazor WASM 吗？**
A：库只依赖 `System.*`，标记为 trim 友好与 `IsAotCompatible`；浏览器（WASM）里没有高分辨率单调时钟与内核定时器，`GetPlatformInfo()` 会诚实报告降级路径。WebAssembly 上的精度不受本库控制。

**Q：为什么 `Hybrid` 默认而不是 `Kernel`？**
A：因为绝大多数用本库的人要的是"比系统节拍准"。`Hybrid` 在支持亚毫秒内核等待的平台上用少量 CPU 换微秒精度；平台不支持时窗口会自动放大，仍保持正确性——只是 CPU 变高，此时 `GetPlatformInfo().RecommendedMode` 会建议 `Spin`。

**Q：我怎么知道在我的机器上到底能到多少？**
A：`dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000`。把打印出来的表格和你的需求对比，不要参考任何别处的数字。

---

## 许可

[Apache License 2.0](https://github.com/OODD-DevNet/OODD.Timer/blob/main/LICENSE) © 2026 OODD

可以商用、修改、再分发，也可以闭源使用；再分发时需保留 [LICENSE](https://github.com/OODD-DevNet/OODD.Timer/blob/main/LICENSE) 与 [NOTICE](https://github.com/OODD-DevNet/OODD.Timer/blob/main/NOTICE)，并说明你改了什么。Apache-2.0 还附带显式的专利授权（第 3 条），这是它相对 MIT 最主要的区别。
