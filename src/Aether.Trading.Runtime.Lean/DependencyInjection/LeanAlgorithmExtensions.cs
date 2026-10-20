using System.IO;
using Aether.Quotes.Domain;
using Aether.Trading.Runtime.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aether.Trading.Runtime.Lean.DependencyInjection;

/// <summary>
/// Lean 算法策略服务 DI 入口（与 <see cref="TradingRuntimeLeanExtensions.AddTradingRuntimeLean"/> 平行、互不耦合）。
/// 注册 Lean 算法回测运行时 + 脚本加载服务。新文件，不动既有 TradingRuntimeLeanExtensions。
/// </summary>
/// <remarks>
/// <b>Consumes</b>（不注册）：<c>IAnalysisManager</c>（Quotes，runner 预取 K 线）、<c>ILeanAlgorithmNoteSource</c>
/// （host 注册，桥接 Notes）。须在 <c>AddQuotesApplication</c> + <c>AddDesktopCommon</c> 之后调用。
/// </remarks>
public static class LeanAlgorithmExtensions
{
    /// <param name="tenantDir">租户数据目录，编译 dll 缓存落 <c>&lt;tenantDir&gt;/lean-algo-cache</c>。</param>
    /// <param name="defaultDataFolder">可选 LEAN data-folder 覆盖（默认 BacktestEnvironment 定位 externals/Lean/Data）。</param>
    public static IServiceCollection AddLeanAlgorithmServices(
        this IServiceCollection services,
        string tenantDir,
        string? defaultDataFolder = null)
    {
        var cacheDir = Path.Combine(tenantDir, "lean-algo-cache");

        services.AddSingleton<ILeanAlgorithmRuntime>(sp => new LeanAlgorithmBacktestRunner(
            analysisManager: sp.GetService<IAnalysisManager>(),
            logger: sp.GetService<ILogger<LeanAlgorithmBacktestRunner>>(),
            defaultDataFolder: defaultDataFolder));

        services.AddSingleton<ILeanAlgorithmScriptingService>(sp => new LeanAlgorithmScriptingService(
            sp.GetRequiredService<ILeanAlgorithmNoteSource>(),
            cacheDir,
            sp.GetService<ILogger<LeanAlgorithmScriptingService>>()!));

        return services;
    }
}
