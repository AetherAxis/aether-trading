using System.Threading;
using System.Threading.Tasks;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 进程级串行化锁：LEAN 的 <c>Config</c>/<c>Globals</c>/<c>Composer</c>/<c>WorkerThread</c>/
/// <c>Log.LogHandler</c> 与 <c>Console.SetOut</c> 都是进程全局静态，回测/实盘/LeanAlgo 并发跑会互污
/// → 崩/数据错乱（见 <see cref="LeanBacktestRunner"/>/<see cref="LeanLiveRunner"/>/
/// <see cref="LeanAlgorithmBacktestRunner"/> 三入口）。
/// <para>三入口各自在 Run 体持此锁，保证同一时刻只有一个 Lean 会话触碰这些全局量。
/// UI 侧 <c>TradingViewModel.CanRun</c>/<c>CanStartLive</c> 互查是早拒绝（免阻塞），本锁是硬兜底
/// —— AI BacktestTool 等绕过 UI 的调用者也受保护。</para>
/// </summary>
internal static class LeanGlobalLock
{
    private static readonly SemaphoreSlim _sync = new(1, 1);

    /// <summary>异步 runner（回测/LeanAlgo）用——协作式等锁，可取消。</summary>
    public static Task WaitAsync(CancellationToken ct) => _sync.WaitAsync(ct);

    /// <summary>同步 runner（实盘 <see cref="LeanLiveRunner.Run"/>，调用方已 Task.Run 卸载到后台线程）用——阻塞等。</summary>
    public static void Wait() => _sync.Wait();

    /// <summary>释放锁。必须与 <see cref="WaitAsync"/>/<see cref="Wait"/> 配对（try/finally）。</summary>
    public static void Release() => _sync.Release();
}
