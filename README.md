# OODD.Timer

一个 .NET 的高精度定时器库。

写它的起因挺实际：想做一个 1ms 的控制循环，结果发现 `System.Threading.Timer` 在 Windows 上每拍迟到 14ms 多，而且越跑越偏——10000 拍下来累计落后两分多钟。这不是"抖动"，是设计使然：所有托管定时器都挂在系统时钟节拍上，Windows 默认是 64Hz。

OODD.Timer 的做法是把计时和等待都换掉。计时读 `Stopwatch.GetTimestamp()` 同源的单调时钟（10MHz，一格 100ns），等待用"内核高分辨率等待 + 尾部自旋"，调度按绝对格点推进。这样定时误差从 10~15ms 降到微秒级，多数场景能到亚微秒，长跑也不漂。

源码、issue 和更新日志：[github.com/OODD-DevNet/OODD.Timer](https://github.com/OODD-DevNet/OODD.Timer)

- 目标框架 `net40` / `netstandard2.0` / `net8.0` / `net9.0` / `net10.0`，覆盖 .NET Framework 4.0 到 .NET 10；Windows、Linux、macOS、iOS、Android、FreeBSD 均可运行
- 不依赖任何第三方包，只用 `System.*`；net8.0+ 标注了 trimming 与 Native AOT 兼容
- 公开成员都带中文 XML 文档
- Apache-2.0 许可，含显式专利授权

---

## 目录

- [支持的目标框架](#支持的目标框架)
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

## 支持的目标框架

| 目标框架 | 覆盖的运行时 | 说明 |
| --- | --- | --- |
| `net40` | .NET Framework 4.0 – 4.6.0 | 全功能。4.0 的 GC 没有 `SustainedLowLatency` 模式，打开对应选项会抛 `PlatformNotSupportedException`；没有 `TimeProvider` 集成 |
| `netstandard2.0` | .NET Framework 4.6.1+、.NET Core 2.x / 3.x、Mono、Xamarin、Unity 2021.2+ | 全功能。`TimeProvider` 集成不可用（4.6.1 的 BCL 里没这个类型） |
| `net8.0` / `net9.0` / `net10.0` | .NET 8 / 9 / 10 | 全功能，另含 `HighResolutionTimeProvider`，能把 `TimeProvider.CreateTimer` 也变成亚毫秒；标注 trim 友好与 AOT 兼容 |

有三件事容易让人困惑，提前说清楚。

第一，.NET Framework 4.6.1–4.8 的项目会拿到 `net40` 资产而不是 `netstandard2.0`。这是 NuGet 的正常选择——同框架族的资产优先——两边行为完全一致，不用管拿到了哪个。

第二，`net40` 的编译器不支持 async 方法（4.5 才加），所以库内部用 `Task` 续体实现异步接口，公开签名和语义跟新框架一模一样，你在自己项目里照常 `await timer.WaitForNextTickAsync()` 就行。

第三，`TimeProvider` 集成只在 net8+ 存在，`HighResolutionTimeProvider` 和 `HighResolutionTimers.TimeProvider` 在老框架上不参与编译。老框架直接 `new HighResolutionTimer(...)` 或用 `HighResolutionTimers.Periodic(...)`，精度没有任何差别。

五个 TFM 共用同一套源码和同一份冒烟程序（`tests/OODD.Timer.CompatSmoke*`），每个资产都在对应的真实运行时上跑过，细节见[构建、测试与基准](#构建测试与基准)。

---

## 为什么普通定时器不准

`System.Threading.Timer`、`PeriodicTimer`、`Task.Delay`、`Thread.Sleep` 名字里都写着毫秒，但它们全部挂在系统时钟节拍上。Windows 默认的节拍是 15.625ms（64Hz），所以 `Thread.Sleep(1)` 不是睡 1ms，而是睡到下一个节拍边沿——本机 600 次采样，p50 是 15.56ms，最小 9.87ms，最大 17.27ms。

更麻烦的是这些 API 是自我节律的：每次醒来后从当前时刻重新计时，误差一拍一拍累加。1ms 的"周期"跑 10000 拍，实际耗掉的是 145 秒。

本库从三个地方下手：

| 做法 | 解决的问题 |
|---|---|
| 计时用高分辨率单调时钟（实测 10MHz，1 tick = 100ns），不用 64Hz 节拍 | 分辨率从 15.6ms 变成 100ns |
| 等待用"内核睡到窗口 + 尾部自旋"的混合策略，不让内核按节拍量化 | 唤醒延迟从 15.5ms 压到亚毫秒，最后一段自旋收紧到 100ns 级 |
| 调度改成绝对格点：下一拍到期点 = 第一拍到期点 + n × 周期，全部整数 tick 运算，绝不 `+= 实际耗时` | 单次抖动不累加，长跑零漂移；过载时合并空拍，不补发风暴 |

关于 `timeBeginPeriod(1)`（也就是本库的 `RuntimeTuning.RequestSystemTimerResolution(1)`），实测确实有效：`Thread.Sleep(1)` 的 p50 从 15.56ms 降到 1.99ms，1ms 周期的 `System.Threading.Timer` 每拍迟到 p50 从 14.55ms 降到 0.76ms，`Dispose` 之后可逆。但它的天花板还是毫秒，而且是进程/整机级副作用——同进程别的线程也被迫按 1ms 唤醒，更费电。要做到微秒得靠 `CreateWaitableTimerExW` 的 `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`（Windows 10 1803+）加尾部自旋，本库默认就走这条路，不需要你动系统节拍。

---

## 实测数字（本机 Windows 11 / .NET 10）

复现命令：

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000
```

每个实现用同一把尺子量：每次触发相对"锚点 + n × 周期"记录落后量，再拆成两个指标。一个是每拍迟到，即相邻落点间隔减去周期，只看抖动、不随时长累加；另一个是累积漂移，即首尾之差，为 0 说明按绝对格点推进，非 0 说明误差在累加。

周期 1ms、10000 拍（约 10 秒）：

| 实现 | 每拍迟到 p50 | 每拍迟到 p95 | 每拍迟到 p99 | 每拍迟到 max | 累积漂移 | 漂移/拍 | 期间 CPU |
|---|---:|---:|---:|---:|---:|---:|---:|
| Thread.Sleep(1ms) | 14531.1µs | 15292.0µs | 15992.5µs | 20986.8µs | 145313.19ms | 14532.77µs | 219ms |
| System.Threading.Timer (1.0ms) | 14526.0µs | 15271.9µs | 16024.6µs | 20744.2µs | 46755.62ms | 4676.03µs | 469ms |
| PeriodicTimer (1.0ms) | 14526.3µs | 15258.0µs | 15945.6µs | 18368.0µs | 144815.43ms | 14482.99µs | 984ms |
| HighResolutionWait.Sleep(Kernel) | 579.4µs | 795.2µs | 1203.9µs | 7089.0µs | 5919.13ms | 591.97µs | 125ms |
| HighResolutionWait.Sleep(Spin) | 0.1µs | 1.0µs | 73.6µs | 867.1µs | 30.05ms | 3.00µs | 9734ms |
| OODD.Timer (Kernel) | 37.7µs | 340.4µs | 573.1µs | 13313.3µs | 78.86ms | 7.89µs | 203ms |
| OODD.Timer (Hybrid) | 0.0µs | 1.8µs | 45.1µs | 1181.3µs | 0.62ms | 0.06µs | 9859ms |
| OODD.Timer (Spin) | 0.0µs | 1.9µs | 43.4µs | 3181.5µs | 4.83ms | 0.48µs | 9688ms |

前三行的每拍迟到就是 14.5ms，累计落后 145 秒，这就是"10~15ms 误差"的真面目——它是结构性累加，不是随机抖动。

`OODD.Timer (Hybrid)` 的每拍中位迟到低于表格分辨率，p95 是 1.8µs，10 秒长跑累计漂移 0.62ms。库内自统计（相对本拍应到时刻）给出 p50 = 128ns、p99 = 65µs。

`OODD.Timer (Kernel)` 是几乎不占 CPU 的档位：每拍迟到 p50 = 37.7µs，因为按绝对格点推进，唤醒的固定偏置不会累加，CPU 只有 203ms / 10s，约 2%。这一行 max 的 13.3ms 是过载时合并空拍的表现，后面[精度的天花板](#精度的天花板在哪里)会解释。

`HighResolutionWait.Sleep(Kernel)` 那行是裸等待原语，自我节律，579µs 的每次超时累加成了 5.9 秒。放在表里就是为了说明"精度"和"无漂移"是两件不同的事，本库主要解决后者。Hybrid 和 Spin 的 CPU 接近满一个核，那是自旋的代价。

这些数字只描述跑这条命令的那台机器（Windows 11 10.0.26200、16 核、.NET 10、控制台无其他负载）。换机器、换系统、加后台负载都必须重测，基准程序会把 Markdown 表格直接打印出来，贴到 README 或 issue 里就行。

亚毫秒节拍的实测在 [docs/benchmarks.md](https://github.com/OODD-DevNet/OODD.Timer/blob/main/docs/benchmarks.md)。

---

## 快速开始

安装：

```powershell
dotnet add package OODD.Timer
```

还没发布到 nuget.org 的时候，可以本地打包再用本地源：`dotnet pack src/OODD.Timer -c Release`，然后把 `bin/Release/*.nupkg` 加进你的本地包源。

十行上手：

```csharp
using OODD.Timer;

using var timer = new HighResolutionTimer(TimeSpan.FromMilliseconds(1));

timer.Elapsed += (_, e) =>
{
    // e.LatenessNanoseconds：这一拍比应到时刻晚了多少纳秒
    Console.WriteLine($"tick {e.Sequence}, lateness {e.LatenessNanoseconds:F0}ns");
};

timer.Start();
Console.ReadKey();
```

注意上面这个写法只是演示。真要看精度不要 `Console.WriteLine`，控制台 I/O 在调度线程上会拖慢下一拍，理由见[用法陷阱](#用法陷阱)。

不确定该用哪种写法的时候，查这张表：

| 我想要… | 用什么 |
|---|---|
| 回调式周期定时器 | `new HighResolutionTimer(period)` + `Elapsed` |
| 一行起一个周期回调 | `HighResolutionTimers.Periodic(TimeSpan.FromMilliseconds(200), () => DoWork())` |
| async 循环（`while (await ...)`） | `timer.WaitForNextTickAsync(ct)`，或 `HighResolutionTimers.RunPeriodicAsync(period, async ct => ...)` |
| 只要一次精确延时 | `HighResolutionWait.Sleep(duration)` / `SleepAsync(...)` |
| 阻塞式"等一拍、干一段活"（Haukcode 风格） | `timer.SetPeriod(ms)` + `timer.WaitForTrigger()` |
| 阻塞式，但要求微秒级投递 | `timer.WaitForTrigger(自旋窗口)` |
| 异步拉取，每拍一次 | `await timer.WaitForTriggerAsync(ct)` |
| 很多定时器一起跑，省线程 | 显式共享一个 `HighResolutionScheduler` |
| 现有代码用 `TimeProvider` / `ITimer` | `HighResolutionTimeProvider.Instance` |
| 想知道本机到底能多准 | `HighResolutionWait.GetPlatformInfo()` 与 `MeasureKernelWakeLatency()` |

---

## 三种等待模式怎么选

`WaitMode` 是本库唯一需要你做架构决策的地方，它是一条"精度换 CPU"的曲线。

`Spin` 全程忙等，每次迭代读一次单调时钟。本机 1ms 节拍下 p50 0.0µs、p99 43µs，代价是独占一个核。适合节拍小于 1ms、且有专用或隔离核的场景。

`Hybrid` 是默认值。内核先睡到"到期点减去一个自旋窗口"，然后在窗口里自旋到点。窗口不写死，而是按实测唤醒延迟自适应：每次内核唤醒都量一下比预定唤醒点晚了多少，晚了就把窗口放大到足以覆盖（留 1.25 倍余量），窗口明显偏大时再按每次 1/64 的速度慢慢收。收缩这一步不能省，否则窗口只会长不会缩，最后退化成长期自旋。本机 1ms 节拍下 p50 0.0µs、p99 45µs，CPU 占用大约等于窗口除以节拍。

`Kernel` 完全不自旋，靠操作系统定时对象唤醒。每拍迟到 p50 37.7µs（绝对格点，不累加），裸唤醒 p50 约 0.58ms，CPU 基本为零。适合秒级节拍、移动端、需要省电的场合。

窗口触到上限（默认 32ms）说明这台机器的内核等待太粗，此时 `Hybrid` 已经等价于 `Spin`。宁可让它触顶，也不要手工调小——调小直接表现为定时器迟到。

```csharp
var timer = new HighResolutionTimer(new HighResolutionTimerOptions
{
    Period = TimeSpan.FromMilliseconds(200),
    Mode = WaitMode.Hybrid,
    AdaptiveSpinWindow = true,            // 默认开启
    MaxSpinWindowNanoseconds = 8_000_000, // 窗口上限，默认 32ms
    CoarseChunkMilliseconds = 2,          // 粗等待分段，决定 Stop / 插队的最早响应延迟
});
```

---

## 拉模式：同步/异步等下一拍

如果你习惯 `Haukcode.HighResolutionTimer` 那种"等一拍、干活、再等一拍"的写法，不用改结构，逐个换名字就行：

| Haukcode.HighResolutionTimer | OODD.Timer | 说明 |
|---|---|---|
| `new HighResolutionTimer()` | `new HighResolutionTimer()` | 默认 1ms 周期，自动启动 |
| `SetPeriod(double periodMS)` | `SetPeriod(double periodMilliseconds)` | 可为小数（如 `16.67`）；运行中生效，会重新锚定相位 |
| `Start()` | `Start()` | 同步返回 |
| `Stop()` | `Stop()` | 同步返回；不取消已在飞的那一拍 |
| `WaitForTrigger()` | `WaitForTrigger()` | 阻塞到下一拍，零自旋，日常推荐 |
| — | `WaitForTrigger(TimeSpan spinWindow)` | 带尾部自旋，微秒级投递 |
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

异步循环和微秒级投递：

```csharp
// PeriodicTimer 风格的异步拉取
while (!ct.IsCancellationRequested)
{
    await timer.WaitForTriggerAsync(ct);
    await HandleOneTickAsync();
}

// 想要微秒级：给一段自旋窗口。先量这台机器，再取推荐值
double recommended = HighResolutionWait.MeasureKernelWakeLatency().RecommendedSpinWindowNanoseconds;
while (running)
{
    timer.WaitForTrigger(TimeSpan.FromNanoseconds(recommended));
    DoTightWork();
}
```

`WaitForTrigger` 和 `WaitOne` 都能阻塞到下一拍，区别在于等的东西不同。`WaitOne` 等的是一个置位事件，上一拍留下的置位可能让下一次调用立刻返回（空转），过载时两拍也会并成一次唤醒。`WaitForTrigger` 以触发序列号为判据，每次调用交付一拍，而且一定是新发生的那一拍，跟不上时合并到最新一拍，和推送模式一致。新代码用 `WaitForTrigger`，`WaitOne` 只为兼容旧代码保留。

### 实测：拉模式到底能到多准

本机（Windows 11 10.0.26200 / 16 核 / .NET 10，调度线程用 `WaitMode.Spin`）对同一批周期各跑一轮，每拍记录"进入用户代码的时刻减去应到时刻"：

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

同一场景下的循环 CPU（单核算 100%）：1ms 拉取纯事件约 0%；1ms 拉取 200µs 窗口约 90%；200µs 拉取 200µs 窗口约 95%。

先说"100µs 地板"这个说法。那不是本库的地板，而是很多拉模式实现的现实水位——它们每次唤醒都要等一次内核事件和线程调度。本库推送模式的中位迟到是 0~0.2µs（时钟粒度 100ns），`WaitForTrigger(窗口)` 的中位数也进了 1µs 以内。

拉模式的 p50 取决于你在等什么。纯事件等的是线程唤醒，本机十几到几十微秒；给了自旋窗口，最后一段在你自己的线程上忙等，中位数就掉到 1µs 附近。p99 那几十微秒是操作系统抢占你线程的时间，用户态代码消不掉。

拉模式的 CPU 消耗是诚实的。窗口要盖住内核睡醒的迟到（本机 p50 约 577µs）才不迟到，所以 1ms 周期配 200µs 窗口，稳态会逼近全程自旋，实测约 90% 一个核。想又准又省 CPU，用推送模式：回调就在调度线程上，到期即执行，自旋开销只有一份。

还有一点容易被忽略：调度线程的模式同样影响拉取投递。拉取只决定消费者什么时候拿到拍，而拍的记账发生在调度线程上。同样 1ms 周期加 200µs 窗口，调度线程用 `Spin` 或 `Hybrid` 时拉取 p50 是 0.8~1.3µs，用 `Kernel` 时 p50 是 332~347µs——调度线程自己就晚了几百微秒。所以拉模式要把调度线程保持在 `Spin` 或 `Hybrid`。

几个语义细节：同一时刻只允许一个消费者调用 `WaitForTrigger*`，多个消费者会互相抢拍；调用不会早于应到时刻返回，但如果你的线程自己迟到超过一个周期，库会合并空拍，交付最新一拍；随时可以 `Stop`，阻塞中的调用会在约 50ms 内抛 `InvalidOperationException`（长周期也一样，内部有 50ms 的状态复核节拍）；单次定时器（`AutoReset = false`）也能用，`WaitForTrigger` 把这唯一一拍交出去之后定时器进入 `Stopped`。

---

## API 导览

### `HiResClock` — 时钟底座

高分辨率单调时间戳和单位换算，和 `Stopwatch` 同源。所有"时刻"用 `long` 时间戳表示，所有"时长"用 tick 计数。

```csharp
long now = HiResClock.Now();                    // 10MHz，1 tick = 100ns
long deadline = HiResClock.Deadline(TimeSpan.FromMicroseconds(500));
double us = HiResClock.ToMicroseconds(HiResClock.Now() - now);
Console.WriteLine(HiResClock.DescribePlatform()); // 频率、是否高分辨率、OS
```

`HiResClock.Add` 做饱和加法，`IsDue` 用差值符号而不是大小比较，所以长时间运行不会因为 tick 溢出而误判。

### `HighResolutionTimer` — 定时器本体

一个定时器不占一个线程，它挂在 `HighResolutionScheduler` 的调度线程上。

```csharp
// 构造：便捷重载 + options
new HighResolutionTimer();
new HighResolutionTimer(TimeSpan.FromMicroseconds(500));
new HighResolutionTimer(period, WaitMode.Spin);
new HighResolutionTimer(period, sharedScheduler);
new HighResolutionTimer(new HighResolutionTimerOptions { ... });

// 生命周期
timer.Start();                                     // 已在运行时再调一次只是重新锚定相位
bool stopped = timer.Stop();                       // 不保证取消已在飞的那一拍
bool ok = timer.Change(dueTime, period);           // 改周期会重新锚定格点
timer.Reset();                                     // 清零统计并重新起表
timer.Dispose();                                   // 幂等；摘除调度并释放等待资源

// 消费方式（任选其一）
timer.Elapsed += (_, e) => { /* e.DueTimestamp / FiredTimestamp / Sequence
                                 / MissedTicks / LatenessNanoseconds / IntervalNanoseconds / Overrun */ };
while (timer.WaitOne()) { /* 阻塞等下一拍（事件语义，兼容用） */ }
while (await timer.WaitForNextTickAsync(ct)) { /* 异步等下一拍 */ }

// 拉模式（Haukcode 风格）
timer.SetPeriod(10.0);                                // 毫秒，可为小数
timer.WaitForTrigger();                               // 阻塞到下一拍
timer.WaitForTrigger(TimeSpan.FromMicroseconds(200)); // 带尾部自旋，微秒级投递
await timer.WaitForTriggerAsync(ct);                  // 异步拉取

// 诊断
TimerStatistics st = timer.GetStatistics();
timer.ResetStatistics();
```

构造函数默认就会启动定时器，所以很多示例里再写一句 `timer.Start()` 是多余的——不过写着也不会出事，之前会（见[更新日志](https://github.com/OODD-DevNet/OODD.Timer/blob/main/docs/CHANGELOG.md)，1.0.0 修掉了重复入堆）。

`TimerElapsedEventArgs.MissedTicks` 是过载信号，大于 0 表示这一拍之前有几个空拍被合并了。库不会补发。

### `HighResolutionScheduler` — 调度线程

一个专用线程加一个索引化二叉堆，插入和删除都是 O(log n)。全进程默认共享一个，定时器多的时候复用它省线程和上下文切换。

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

堆空的时候线程阻塞在唤醒事件上，CPU 为零。`HighResolutionScheduler.Shared` 是全进程共享实例，别 `Dispose` 它，需要隔离就自己 `new` 一个。另外线程优先级最高只能到 `ThreadPriority.Highest`，Windows 上这映射的是优先级类别，不是真正的实时优先级。

### `HighResolutionWait` — 只要等待，不要定时器

```csharp
HighResolutionWait.Sleep(TimeSpan.FromMicroseconds(200), WaitMode.Hybrid);
await HighResolutionWait.SleepAsync(TimeSpan.FromMilliseconds(5), WaitMode.Hybrid, ct);
HighResolutionWait.SpinUntil(HiResClock.Deadline(TimeSpan.FromMicroseconds(50)));

// 先量一下这台机器，再决定策略与窗口
TimerPlatformInfo info = HighResolutionWait.GetPlatformInfo();
KernelWakeLatency wake = HighResolutionWait.MeasureKernelWakeLatency(400, TimeSpan.FromMilliseconds(1));
Console.WriteLine(info);
Console.WriteLine(wake);   // p50 / p99 / max 与推荐自旋窗口
```

`MeasureKernelWakeLatency` 返回值里的 `RecommendedSpinWindowNanoseconds` 可以直接写进 `HighResolutionTimerOptions.SpinWindowNanoseconds`，配合 `AdaptiveSpinWindow = false` 使用，适合启动时标定一次然后固化下来。

### `TimerStatistics` — 精度是不是真的达到了

```csharp
var st = timer.GetStatistics();
Console.WriteLine(st);                           // 一行摘要：ticks / rate / missed / lateness 分位数
Console.WriteLine(st.ToLatenessHistogramText()); // log2 分桶直方图 + 累计百分位
double p99 = st.GetLatenessPercentile(0.99);
bool healthy = st.JitterRatio < 0.05 && st.MissedTicks == 0;
```

直方图用 2 的幂分桶，64 个桶，桶宽就是误差量级，所以报出来的分位数是保守偏大的——宁可高估误差，也不低估。

---

## 精度的天花板在哪里

不说清楚做不到什么，"微秒级"就是空话。

第一，100ns 是地板。单调时钟频率 10MHz，一格 100ns，比这更细的差异本库无法区分，只能说"已到点"。

第二，尾延迟不归定时器管，归操作系统管。p50 已经在时钟粒度以内，p99 和 max 的尖峰来自 GC 停顿和内核抢占。本机实测纯自旋 1ms 等待的最大迟到尖峰 1.31ms，来源就是这两项。真要微秒级硬保证，需要操作系统的实时调度——`SCHED_FIFO`、隔离核、关掉 CPU 频率调节，这不是任何库能凭空给的。

第三，回调必须比节拍短。回调超过周期时，库不堆叠补发，而是合并空拍：`MissedTicks` 增加、`Overrun` 为 true，但格点不漂。过载表现成丢拍，而不是越跑越慢。

第四，服务器 GC 和大堆会让尖峰变差。`GCSettings.IsServerGC` 和这个库的尾延迟目标是冲突的。

第五，Windows 的高分辨率等待要求 Windows 10 1803 以上。更早的系统或者句柄创建失败时会退回普通等待句柄和 `Thread.Sleep`，精度回到节拍量级，这时自适应窗口会自动放大来自救，功能不受影响，只是 CPU 变高。`GetPlatformInfo().KernelHighResolutionWaits` 会如实告诉你当前走的是哪条路。

第六，Linux 和 macOS 走 `nanosleep`。亚毫秒通常没问题（几十到几百微秒），但具体取决于内核 HZ、`CONFIG_HRTICK`、CPU 频率调节和调度器负载。必须在目标机上用 `MeasureKernelWakeLatency()` 复核，别信任何文档里的数字，包括这份。

第七，拉模式比推送模式多一层"消费者线程唤醒"。`WaitForTrigger()` 的投递延迟就是线程唤醒的时间，本机 p50 十几到几十微秒；`WaitForTrigger(窗口)` 用忙等把这一层压到 1µs 左右，但 p99 仍然是你线程被抢占的时间。两者都不改变调度精度，只改变什么时候轮到你干活。想连尾延迟都接近调度精度，用回调。

---

## 把尾延迟压下去：进程级调优

`RuntimeTuning` 里的两项都是进程级设置，会影响同进程所有代码，所以默认全关，要你自己显式调用。

```csharp
// GC 进入 SustainedLowLatency，推迟阻塞式 GC，代价是托管内存占用上升。
// 它只压 p99 和 max——本库定时器的 p50 本来就在时钟粒度以内，尖峰才是 GC 造成的。
using (RuntimeTuning.RequestGcLatencyMode(GCLatencyMode.SustainedLowLatency))
{
    // 引用计数式：最后一个释放时才恢复原模式，
    // 不会出现"A 的 Dispose 把 B 依赖的模式提前改回去"
}

// Windows 系统节拍：把 15.6ms 请求成 1ms。本机实测 Thread.Sleep(1) 的 p50 从 15.56ms 到 1.99ms，
// System.Threading.Timer(1ms) 每拍迟到从 14.55ms 到 0.76ms。
// 本库的高分辨率路径不经过系统节拍，所以对它没影响；值得开的场景是平台降级到托管 sleep 兜底，
// 或者同进程里还有别的粗节拍代码。
using (RuntimeTuning.RequestSystemTimerResolution(1)) { /* ... */ }
```

也可以让定时器自己管：`HighResolutionTimerOptions.PreferSustainedLowLatencyGc = true`。

真正有效的组合，按收益排序，每一条都需要你在目标机上验证：

1. 客户端 GC（`ServerGarbageCollection=false`）加减少分配，热点回调里避免闭包和装箱。
2. 把调度线程放到隔离核上（Linux 用 `isolcpus` / `cset`，Windows 用进程亲和性），并设 `ThreadPriority.Highest`。
3. 关掉 CPU 频率调节和深度睡眠状态；笔记本插电并关掉节能。
4. 用 `WaitMode.Spin`，或者把 `SpinWindowNanoseconds` 设成 `MeasureKernelWakeLatency()` 的推荐值，别让窗口在运行中被尖峰撑大。
5. 精度要求极高时，用 `HighResolutionScheduler` 单独起一个线程，只放这一两个定时器。

详细做法、每一步的实测影响和代价在 [docs/进阶：把误差压到极限.md](https://github.com/OODD-DevNet/OODD.Timer/blob/main/docs/%E8%BF%9B%E9%98%B6%EF%BC%9A%E6%8A%8A%E8%AF%AF%E5%B7%AE%E5%8E%8B%E5%88%B0%E6%9E%81%E9%99%90.md)。

---

## 用法陷阱

记得 `Dispose`。定时器不会自己消失，`using` 最省事。`Dispose` 是幂等的，之后再 `Start()` 会抛 `ObjectDisposedException`。

回调里不要阻塞。回调跑在调度线程上，一个慢回调会拖住同一调度器上的所有定时器。重活丢进队列，或者用独立调度器。

回调里也不要写控制台。`Console.WriteLine` 是同步写，要过 conhost 的锁，一次就可能几毫秒，而且它发生在调度线程上，会直接把下一拍推后。日志里"打印过的那一拍紧跟着又迟到一拍"这种成对出现的序号就是这么来的。要记录就写环形缓冲、事后再打；只看分布用 `GetStatistics()`，别靠"超过阈值就打印"来观察精度。

`Stop()` 不保证取消已在飞的那一拍，这和 `System.Threading.Timer.Change` 的语义一致：返回时可能还有一个回调正在执行。要保证绝对不再有下一拍，看 `Sequence` 或用状态标志。

回调抛异常默认被吞掉并计入 `Error`。`SuppressCallbackExceptions` 默认是 `true`，为的是保护调度线程不挂，异常通过 `Error` 事件上报，同时累加 `TimerStatistics.ErrorCount`。开发期建议订阅 `Error`，或者把它设成 `false` 让异常直接炸出来。

别 `Dispose` 共享调度器，`HighResolutionScheduler.Shared` 是全进程的，要隔离就自己新建一个。

亚毫秒周期在 Windows 上要求 1803 以上。更早的系统或句柄失败时，`Kernel` 模式会退回 15.6ms 节拍，这时候该用 `Spin`；也可以 `RequestSystemTimerResolution(1)` 把兜底 sleep 的粒度压到 2ms（实测），让自适应窗口跟着缩小。拿不准就用 `GetPlatformInfo().RecommendedMode` 让库替你判断。

短命线程上别用 `Kernel` 等待。高分辨率等待句柄是线程本地的，本库调度线程这种长期线程无所谓，线程池线程高频调用会反复创建句柄。

---

## TimeProvider 集成（把测试与生产统一到一套时钟上）

`HighResolutionTimeProvider` 可以直接注册进 DI 顶替 `TimeProvider.System`：

```csharp
builder.Services.AddSingleton<TimeProvider>(HighResolutionTimeProvider.Instance);

TimeProvider clock = HighResolutionTimeProvider.Instance;
long ts = clock.GetTimestamp();                       // 来自 10MHz 单调时钟
ITimer t = clock.CreateTimer(_ => Poll(), null,        // 高分辨率实现，不是 System.Threading.Timer
    TimeSpan.FromMicroseconds(500), TimeSpan.FromMicroseconds(500));
```

边界要说清楚。走高分辨率路径的是 `GetTimestamp`、`TimestampFrequency`（10MHz）和 `CreateTimer`（亚毫秒）；`GetUtcNow()` 和 `LocalTimeZone` 仍然来自系统 wall-clock，本库不伪造日期和时区，只把精度提上来。需要隔离调度线程时用 `new HighResolutionTimeProvider(myScheduler)`。

---

## 构建、测试与基准

```powershell
dotnet build OODD.Timer.slnx -c Release          # 0 警告 0 错误（5 个 TFM）
dotnet test  tests/OODD.Timer.Tests -c Release   # 104 个行为测试（net8.0 / net10.0）
dotnet pack  src/OODD.Timer -c Release           # 产出 nupkg + snupkg，README / LICENSE / NOTICE 一并打包
```

跨框架冒烟程序，每个资产在自己的真实运行时上跑一遍：

```powershell
dotnet build tests/OODD.Timer.CompatSmoke -c Release             # net40 / net48 / net8 / net9 / net10
dotnet build tests/OODD.Timer.CompatSmoke.NetStandard -c Release # netstandard2.0 资产单独打靶
```

`CompatSmoke` 先打印实际加载的资产（读 `TargetFrameworkAttribute`），再跑时钟、周期回调、三种等待模式、同步和异步拉取、取消、一次性定时器、统计直方图、内核唤醒标定、`timeBeginPeriod` 引用计数，全部断言通过才返回 0。net40 和 net48 的 exe 直接在 cmd 里跑，net8+ 用 `dotnet xxx.dll`。`CompatSmoke.NetStandard` 直接引用 netstandard2.0 产物，因为 NuGet 在 .NET Framework 上会优先挑 `net40` 资产，不这样打靶就测不到它。

测试里最关键的一条是不变量测试，而不是"跑一次看数字"。它遍历每一拍的到期时间戳，断言

```csharp
HiResClock.Add(firstDue, slot * periodTicks) == tick.Due   // slot 累加 (Missed + 1)
```

也就是格点精确等于周期的整数倍，同时要求至少 90% 的拍准点、最后一拍的迟到仍小于一个系统节拍。这样"无漂移"被钉在测试里，不靠一次采样的运气。

精度断言依赖真实挂钟，所以 `xunit.runner.json` 关掉了集合并行，并行跑会互相污染测量。

基准：

```powershell
dotnet run -c Release --project bench/OODD.Timer.Benchmark -- <周期ms> <拍数>
# 例：-- 1 10000（1ms 节拍跑一万拍）、-- 0.2 5000（亚毫秒）
```

它会先打印本机平台信息和内核唤醒延迟实测，再打印可以直接粘进 README 的 Markdown 对比表、库内自统计和迟到直方图。

---

## 常见问题

**Q：为什么不直接用 `Thread.SpinWait`，或者 `Task.Delay` 开高精度模式？**

`Task.Delay` 落在同一个 64Hz 节拍上；`Thread.SpinWait` 只有忙等一种形态，没有"内核睡到窗口 + 尾部自旋"的混合策略，也没有绝对格点调度。这个库的价值是把这三件事组合起来，并且让精度可测量、可断言。

**Q：调一下 `timeBeginPeriod(1)` 把系统节拍改成 1ms 不就够了？**

不够，但确实值得一试。本机实测它生效：`Thread.Sleep(1)` 的 p50 从 15.56ms 降到 1.99ms，1ms 的 `System.Threading.Timer` 每拍迟到从 14.55ms 降到 0.76ms。问题有两层。一是天花板还在毫秒，想要微秒就绕不开自旋或高分辨率等待句柄；二是它是进程和整机级设置，同进程所有等待都被迫按 1ms 唤醒，功耗和上下文切换都涨。`RuntimeTuning.RequestSystemTimerResolution` 保留了这个开关，用途见上面「进程级调优」。

**Q：我原来用 `Haukcode.HighResolutionTimer`，能直接换过来吗？**

能。见[拉模式](#拉模式同步异步等下一拍)的映射表，`SetPeriod(double)`、`Start`、`Stop`、`WaitForTrigger()`、`Dispose()` 的名字和语义都对得上，另外多了 `WaitForTrigger(自旋窗口)` 和 `WaitForTriggerAsync(ct)`。它没做的"无漂移绝对格点"和"迟到可测量"这个库默认就有，换过去之后 `GetStatistics()` 能直接告诉你实际精度。

**Q：拉模式的中位数进不了 0.1µs，那个 100µs 的地板能再低吗？**

时钟地板是 100ns，不是 100µs。100µs 是每次拉取都要等一次内核唤醒和线程调度的现实水位，属于拉模式这种用法的成本，不是库的成本。想把中位数压到 1µs，给 `WaitForTrigger(自旋窗口)` 一段忙等，代价是 CPU；想连 p99 都好看，用推送模式，回调就在调度线程上、到期即执行，本机 p50 是 0.2µs。p99 那几十微秒是你的线程被操作系统抢占的时间，用户态库消不掉。

**Q：能同时跑多少个定时器？**

调度器是索引化二叉堆，插入删除都是 O(log n)。单线程能撑住多少取决于每拍的 callback 耗时和唤醒精度，不是数量本身。先用 `GetStatistics().MissedTicks` 和 `JitterRatio` 量一下，再决定要不要拆多个调度器。

**Q：支持 Unity、Godot、老项目、AOT、Blazor WASM 吗？**

Unity 2021.2+ 和 Godot 4（.NET 6/7 目标）拿 `netstandard2.0` 资产，可以直接用；Godot 4 配 .NET 8、以及任何 net8+ 工程拿原生资产，带 trimming 和 `IsAotCompatible` 标注；.NET Framework 4.0–4.8 的老项目拿 `net40` 资产，不用升框架。浏览器（WASM）里没有高分辨率单调时钟和内核定时器，`GetPlatformInfo()` 会如实报告降级路径，精度不受这个库控制。

**Q：我做运动控制，这么大的波动能消掉吗？**

先分清分布和尾巴。本机实测（1ms 节拍、1.5 万拍、默认 `Hybrid`、Release 构建）p50 是 0.1µs、p99 是 131µs、p99.9 是 262µs，超过 200µs 的只有 28 拍，占 0.19%；尾部那几拍通常是 1~5ms，极端时 15~40ms。这些尾巴不是等待算法造成的，是操作系统把线程抢走了——时间片、其他进程、GC、首次 JIT，都在用户态库的控制之外。换成 `WaitMode.Spin` 也压不掉：同样 1.5 万拍，Spin 超过 200µs 的拍数反而更多（41 对 15），因为瓶颈在抢占，不在等待方式。

能做的是这几件事：

1. 回调里零分配、零 I/O。用 `HighResolutionTimerOptions.Callback`，日志写环形缓冲事后打印。
2. 预热，丢掉启动后的头 1~2 秒。第一拍包含调度线程启动和 JIT，本机实测迟到 1~3ms。
3. Release 构建。有分配压力时开 `PreferSustainedLowLatencyGc`，`ThreadPriority` 默认已经是 `Highest`。
4. 轨迹按时间戳插补。用 `TimerElapsedEventArgs.FiredTimestamp`（零分配回调里自己读 `HiResClock.Now()`）算真实经过时间，位置 = f(t)。回调晚 3ms 只意味着"命令更新晚 3ms"，不会变成位置误差；反过来，如果控制律假设每拍正好 1ms，任何抖动都会累积成误差。
5. 需要微秒级硬实时（伺服环、插补周期）时，把控制环放进实时内核、运动控制卡或 FPGA，PC 只做轨迹规划和下发。这是操作系统的边界。

**Q：为什么默认是 `Hybrid` 而不是 `Kernel`？**

因为绝大多数人用这个库，要的是"比系统节拍准"。`Hybrid` 在支持亚毫秒内核等待的平台上用少量 CPU 换微秒精度；平台不支持时窗口自动放大，正确性不受影响，只是 CPU 变高，这时候 `GetPlatformInfo().RecommendedMode` 会建议你用 `Spin`。

**Q：我怎么知道在我的机器上到底能到多少？**

跑 `dotnet run -c Release --project bench/OODD.Timer.Benchmark -- 1 10000`，把打印出来的表格和你的需求对比。不要参考任何别处的数字，包括这份 README。

---

## 许可

[Apache License 2.0](https://github.com/OODD-DevNet/OODD.Timer/blob/main/LICENSE) © 2026 OODD

可以商用、修改、再分发，也可以闭源使用。再分发时保留 [LICENSE](https://github.com/OODD-DevNet/OODD.Timer/blob/main/LICENSE) 和 [NOTICE](https://github.com/OODD-DevNet/OODD.Timer/blob/main/NOTICE)，并说明你改了什么。Apache-2.0 附带的显式专利授权（第 3 条）是它和 MIT 最主要的区别。
