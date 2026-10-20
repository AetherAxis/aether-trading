using System.IO;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// 回测运行环境辅助：定位 LEAN 数据目录。
/// dev 下从 BaseDirectory 逐级向上找 externals/Lean/Data（子模块随仓的全量数据，含 market-hours/symbol-properties）；
/// 打包部署（无 externals）退回 BaseDirectory/LeanData（随包打进的最小子集：symbol-properties + market-hours）。
/// </summary>
public static class BacktestEnvironment
{
    /// <summary>定位 LEAN 数据目录：优先 externals/Lean/Data，其次随包 LeanData。</summary>
    public static string LocateLeanDataFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "externals", "Lean", "Data");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "LeanData");
    }
}
