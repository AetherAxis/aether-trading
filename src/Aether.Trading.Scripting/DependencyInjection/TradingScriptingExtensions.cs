using Microsoft.Extensions.DependencyInjection;

namespace Aether.Trading.Scripting;

public static class TradingScriptingExtensions
{
    /// <summary>注册策略脚本加载服务（扫描 notes 策略笔记 → Roslyn 编译 → StrategyRegistry.Register）。
    /// 必须在 AddDesktopCommon 之后调用。</summary>
    public static IServiceCollection AddTradingScripting(this IServiceCollection services)
    {
        services.AddSingleton<IStrategyScriptingService, StrategyScriptingService>();
        return services;
    }
}
