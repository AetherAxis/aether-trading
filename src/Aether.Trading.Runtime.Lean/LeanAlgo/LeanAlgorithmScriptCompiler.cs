using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Aether.Trading.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Indicators;

namespace Aether.Trading.Runtime.Lean;

/// <summary>
/// Roslyn 动态编译用户 Lean 算法源码（<see cref="QCAlgorithm"/> 子类）→ 落盘 dll + 返回类型。
/// 与 <c>StrategyScriptCompiler</c>（编译 IStrategy，in-memory）平行、互不耦合。差异：
/// (1) references 显式含 Lean 程序集（force-touch 关键类型触发 lazy-load 再收集 AppDomain）；
/// (2) 找 QCAlgorithm 派生非抽象类（非 IStrategy）；
/// (3) Emit 到磁盘文件（Lean BacktestingSetupHandler 按文件路径反射加载，不接受 in-memory），
///     每次重编译用源码 hash 命名避免 Assembly.LoadFrom 路径缓存。
/// </summary>
public static class LeanAlgorithmScriptCompiler
{
    /// <summary>编译源码。成功 out type + dllPath；失败 out error。</summary>
    public static bool TryCompile(string source, string name, string cacheDir, out Type? type, out string? dllPath, out string? error)
    {
        type = null;
        dllPath = null;
        error = null;

        var references = BuildReferences();
        var compilation = CSharpCompilation.Create(
            "LeanAlgo_" + name,
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // 落盘 dll：按源码 hash 命名，重编译内容变 → 新文件名 → 新程序集（绕过 LoadFrom 路径缓存）。
        Directory.CreateDirectory(cacheDir);
        var hashSuffix = HashSuffix(source);
        dllPath = Path.Combine(cacheDir, $"LeanAlgo_{name}_{hashSuffix}.dll");

        // 同 hash 同内容 → 复用已编译 dll，免每次 NotesChanged 全量重 emit（Roslyn 编译开销）。
        var preExisting = File.Exists(dllPath);
        if (!preExisting)
        {
            var result = compilation.Emit(dllPath);
            if (!result.Success)
            {
                error = EmitError(result.Diagnostics);
                return false;
            }
        }

        if (LoadAlgorithmType(dllPath, out type, out error)) return true;

        // 复用的旧 dll 加载失败（损坏/被截断）——删后重 emit 再加载一次。
        // 首次 emit 即失败的不再重试（同源码同样字节，重 emit 不会变好）。
        if (!preExisting) return false;
        try { File.Delete(dllPath); } catch { /* 占用中忽略，下面重 emit 覆盖 */ }
        var retry = compilation.Emit(dllPath);
        if (!retry.Success)
        {
            error = EmitError(retry.Diagnostics);
            return false;
        }
        return LoadAlgorithmType(dllPath, out type, out error);
    }

    /// <summary>删除 cacheDir 中本轮源码集合之外的旧 dll（重加载后清已删除/改名/改坏的旧版本落盘文件）。</summary>
    public static void CleanupStaleDlls(string cacheDir, IEnumerable<(string Name, string Source)> current)
    {
        if (!Directory.Exists(cacheDir)) return;
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, source) in current)
            expected.Add($"LeanAlgo_{name}_{HashSuffix(source)}.dll");
        foreach (var f in Directory.EnumerateFiles(cacheDir, "LeanAlgo_*.dll"))
        {
            if (expected.Contains(Path.GetFileName(f))) continue;
            try { File.Delete(f); } catch { /* 被 Lean 加载占用/竞态忽略——best-effort 清理 */ }
        }
    }

    private static string HashSuffix(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];

    private static string EmitError(System.Collections.Generic.IEnumerable<Diagnostic> diagnostics)
        => string.Join("\n", diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString()));

    // 从落盘 dll 加载（与 Lean BacktestingSetupHandler 同路径加载方式一致），找 QCAlgorithm 派生类。
    // GetTypes() 可能因依赖解析失败抛 ReflectionTypeLoadException——捕获并以可读错误返回，免致整批加载静默失败。
    private static bool LoadAlgorithmType(string dllPath, out Type? type, out string? error)
    {
        type = null;
        error = null;
        try
        {
            var asm = Assembly.LoadFrom(dllPath);
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                var loaderErrs = rtle.LoaderExceptions?.Where(e => e is not null).Select(e => e!.Message);
                error = "加载算法类型失败（依赖解析）：" + string.Join("; ", loaderErrs ?? Array.Empty<string>());
                if (error.Length > 500) error = error[..500] + "…";
                return false;
            }
            type = types.FirstOrDefault(t =>
                typeof(QCAlgorithm).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        }
        catch (Exception ex)
        {
            error = "加载编译产物失败：" + ex.Message;
            return false;
        }
        if (type is null)
        {
            error = "源码中未找到继承 QCAlgorithm（或 LeanAlgorithmBase）的非抽象类";
            return false;
        }
        return true;
    }

    /// <summary>反射类型的 public static 成员 ParameterDefinitions（IReadOnlyList&lt;StrategyParameterDef&gt;）。
    /// 静态约定——免实例化 QCAlgorithm（引擎外 new 有风险）。无则返回空。</summary>
    public static IReadOnlyList<StrategyParameterDef> ReadParameterDefinitions(Type type)
    {
        var prop = type.GetProperty("ParameterDefinitions",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (prop?.GetValue(null) is IReadOnlyList<StrategyParameterDef> list) return list;
        var field = type.GetField("ParameterDefinitions",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        if (field?.GetValue(null) is IReadOnlyList<StrategyParameterDef> list2) return list2;
        return Array.Empty<StrategyParameterDef>();
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        // references 收集策略：Lean 程序集多且 lazy-load，仅收集 AppDomain 已加载的程序集会漏（如
        // QuantConnect.Algorithm.Framework——SMA/SetHoldings/Liquidate 等辅助方法所在），导致用户源码编译报
        // "X 未找到"。故：(1) 收集 AppDomain 已加载程序集（System + 已 lazy-load 的 Lean/Aether）；
        // (2) 扫描 Lean 输出目录全量 QuantConnect.*.dll + Aether.*.dll（按磁盘路径建引用，不依赖 lazy-load）。
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                paths.Add(a.Location);

        // Lean 运行时所有 dll 与 QuantConnect.Algorithm.dll 同目录（Stockapp bin 输出已拷贝全量依赖）。
        // 扫描全量 *.dll（不限 QuantConnect.*/Aether.*）——Lean 类型层级引用的第三方依赖也须进引用，
        // 如 Python.Runtime.dll（QCAlgorithm 的 PyObject 成员致 CS0012）、NodaTime/Newtonsoft 等。
        var leanDir = Path.GetDirectoryName(typeof(QCAlgorithm).Assembly.Location);
        if (leanDir is not null && Directory.Exists(leanDir))
        {
            foreach (var dll in Directory.EnumerateFiles(leanDir, "*.dll"))
                paths.Add(dll);
        }

        // force-touch 关键类型确保其程序集已加载（保险——上面已按路径收集，这里仅触发 lazy-load 供 AppDomain 路径补全）。
        ForceLoad(new object[]
        {
            typeof(QCAlgorithm),                    // QuantConnect.Algorithm
            typeof(LeanAlgorithmBase),              // 本项目（用户继承它 / 用 CtpBar）
            typeof(CtpBar),
            typeof(Symbol),                         // QuantConnect（含 Resolution）
            typeof(Slice),                          // QuantConnect.Data
            typeof(SimpleMovingAverage),            // QuantConnect.Indicators
            typeof(StrategyParameterDef),           // Aether.Trading.Domain
            typeof(Console),                        // System
        });
        // force-touch 后再补一次 AppDomain（新 lazy-load 进来的程序集路径）
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (!a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                paths.Add(a.Location);

        var refs = new List<MetadataReference>();
        foreach (var p in paths)
        {
            if (!IsManagedAssembly(p)) continue;   // 跳过原生 dll（CTP thost*_se/ctp_native 等），否则 CS0009
            try { refs.Add(MetadataReference.CreateFromFile(p)); }
            catch { /* 个别 dll 非有效元数据忽略 */ }
        }
        return refs;
    }

    /// <summary>判断 dll 是否含托管（CLI）元数据——排除原生 PE（CTP thost*_se.dll / ctp_native.dll 等），
    /// 否则 MetadataReference.CreateFromFile 不抛异常但编译时报 CS0009「PE 映像不包含任何托管元数据」。</summary>
    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var pe = new PEReader(File.OpenRead(path));
            return pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
        }
        catch { return false; }
    }

    private static void ForceLoad(object[] touch)
    {
        foreach (var t in touch)
        {
            try { _ = t?.ToString(); } catch { /* 触发加载即可，忽略 */ }
        }
    }
}
