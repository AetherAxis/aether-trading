# Aether.Trading 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

进程内跑 LEAN 回测/实盘，并把 CTP 会话桥成 Lean 的 brokerage + 行情源。不在本仓：CTP SDK/契约（`ctp-net-*`）、行情采集、宿主（`aether-ctp-hosts`/`aether-desktop`）。

## 2. 项目组成

| 项目 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `Aether.Trading.Domain` | 纯域：策略/指标/结果 | `IStrategy`、`MarketBar`、`StrategyProgram`、`YamlStrategy` | 无（叶子） |
| `Aether.Quotes.Domain` | 行情只读契约 | `IAnalysisManager`、`Quote` | 无（叶子） |
| `Aether.Trading.Runtime.Domain` | Lean 中性端口 | `ILiveTradingRuntime`、`IBacktestRuntime`、`BarResolution` | Trading.Domain |
| `Aether.Trading.Scripting` | 笔记源码 → `IStrategy` | `StrategyScriptingService`、`StrategyScriptCompiler` | Domain、Runtime.Domain；Roslyn/YamlDotNet |
| `Aether.Trading.Application` | 薄编排 | `BacktestApplicationService`、`LiveTradingApplicationService` | Domain、Runtime.Domain；M.E.DI |
| `Aether.Trading.Runtime.Lean` | 唯一 Lean 叶子 | `LeanLiveRunner`、`CtpBrokerage`、`LeanAlgorithmScriptCompiler` | 三域 + `ctp-net-contracts`；QuantConnect 三包、Roslyn |
| `tests/Aether.Trading.Tests` | 单测 99 例 | 指标/YAML/Roslyn | 四 src + contracts + xunit |

## 3. 关键设计决策

### 3.1 QuantConnect 只活在叶子
- **决策**：上层只用 `Runtime.Domain` 端口；Lean Config 键与 handler 链收在 `LeanLiveRunner.ConfigureLeanLive`。
- **理由**：编译期即边界；DI 装配测试不必起引擎。
- **代价**：加能力需扩端口并拼 Config。

### 3.2 CtpBrokerage 单类双角色
- **决策**：`partial class CtpBrokerage : Brokerage, IDataQueueHandler` 一实例两职；工厂建后 `Composer.Instance.AddPart` 使其解析回同一实例。
- **理由**：交易与行情共用一个 `ICtpSession`，事件只订阅一次。
- **代价**：约 770 行；依赖 Composer 内部解析路径。

### 3.3 双引擎 + 运行时 Roslyn
- **决策**：`StrategyRegistry`（可卸载 ALC + 源码 hash）与 `LeanAlgorithmRegistry`（落盘 dll + hash 文件名）平行。
- **理由**：普通策略不碰 QuantConnect；bot 可直写 `QCAlgorithm`。
- **代价**：改名是运行时失败；两份编译器重复。

### 3.4 LeanData 最小子集 + 两级定位
- **决策**：仓内只放 `market-hours/`、`symbol-properties/`（3 文件）。`LocateLeanDataFolder()` 先向上找 `externals/Lean/Data`，回落 `<BaseDirectory>/LeanData`；⑧ 宿主 `Aether.CtpConsole.csproj` 用 `..\..\aether-trading\LeanData\**\*` 的 Content glob 拷进输出。
- **理由**：装配只需证券/时段库，部署不依赖 `externals`。
- **代价**：要求兄弟仓并排 checkout；新增数据类要改宿主。

### 3.5 静态网关 + 进程级锁
- **决策**：`CtpLeanGateway`（session/行情前置/交易选项）与 `TradingGateway`（日志/停止/`ForceStop`）静态注入并 `Reset`；三入口共用 `LeanGlobalLock`。
- **理由**：Lean 全局静态并发互污；实盘须先 `Globals.Reset()`。
- **代价**：同进程只能一个 Lean 会话，漏 `Reset` 会污染下次启动。

## 4. 运行与验证

- 构建：`dotnet build aether-trading.slnx -c Release`，需同级 checkout `ctp-net-contracts`。
- 测试：`dotnet test --no-build`，99 例（源码可数 99 个 `[Fact]/[Theory]`）；`FakeCtpSession` 不触真实 CTP。
- 本次只读调查未执行 build/test；99/99 为 README/CI/MIGRATION ⑥ 的既有记录。

## 5. 升级记录（2026-10-24）

改动仅 `Directory.Packages.props`（22 增 8 删）。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| M.E.DI.Abstractions / DI / Logging.Abstractions | 10.0.0 | 10.0.13 | 同线补丁 |
| Microsoft.CodeAnalysis.CSharp | 4.13.0 | 5.9.0 | 两项目同步 |
| YamlDotNet | 16.2.0 | 18.1.0 | 仅 Scripting 引 |
| Test.Sdk / runner / coverlet | 17.14.1 / 3.1.4 / 6.0.4 | 18.11.0 / 4.0.1 / 10.1.0 | xunit 不动 |
| `CentralPackageTransitivePinningEnabled` | 未开 | `true` | 覆盖传递解析 |
| ServiceModel.Primitives / Private.ServiceModel / Drawing.Common | 4.4.0 / 传递 / 4.7.0 | 4.10.3 / 4.10.3 / 4.7.3 | 4 条 GHSA |
| System.Net.Http.WinHttpHandler | 传递 4.7.2 | 显式 4.7.2 | net10.0 未解析，预防钉 |
| System.Security.Cryptography.Pkcs | 6.0.1 | 6.0.5 | GHSA-555c-2p6r-68mm |

依赖链见 `project.assets.json`：`NetMQ → ServiceModel.Primitives`、`Cryptography.Xml → Pkcs`；钉后四包在 net10.0 解析为 4.10.3/4.10.3/4.7.3/6.0.5。

**为何同主版本末位而非 8.x/10.x**：五包本仓不用，只为消 GHSA；跨主版本会换契约与目标框架——`System.Drawing.Common` 自 5.0 起仅支持 Windows（CI 为 Linux），跳线即 `PlatformNotSupportedException`；WCF 8.x 拆包且类型前移。同线只换实现。**代价**：仍在停更线，属止血。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | 跨仓依赖 | 两项目直引 `..\..\..\ctp-net-contracts`；CI 多 checkout | 发布包并切 PackageReference | 两仓发版协同 |
| 2 | P1 | 构建复现 | 无 `global.json`；CI `10.0.x` 浮动 | 固定 SDK + `latestPatch` | 低 |
| 3 | P1 | 安全审计 | CI 仅 build+test；无 `NuGetAudit`；5 条 GHSA 人工钉 | 加 `--vulnerable` 审计步 | 低；或有存量告警 |
| 4 | P2 | 还原确定性 | 无 `packages.lock.json`；本次钉 5 个传递版本 | 锁文件 + `--locked-mode` | 升级需提交锁 |
| 5 | P2 | 并发模型 | `LeanGlobalLock` 单锁；两网关皆静态 | 独立进程或租户排队 | 高 |
| 6 | P2 | 账户币种 | `AccountBaseCurrency => "USD"`，注释承认 CNY | 接 `ICurrencyConverter` | 中 |
| 7 | P3 | 覆盖率 | 引 `coverlet.collector`，CI 无 `--collect` | 采集设门槛或删包 | 低 |
| 8 | P3 | 陈旧授权 | `Scripting.csproj` 指向已并入的 `Runtime.Tests` | 改名或删除 | 极低 |

## 7. 本轮升级落地（2026-10-24）

- **SDK 版本钉死**：新增 `global.json`（`10.0.301`，`rollForward: latestFeature`）——本机 `~/.dotnet` 是 9.0.100，不钉住会 NETSDK1045。
- **测试程序集名对齐**：`Aether.Trading.Scripting` 的 `InternalsVisibleTo` 由 `Aether.Trading.Runtime.Tests` 改为实际存在的 `Aether.Trading.Tests`。
- **NuGet 审计门**：CI 新增审计步骤（allowlist 里只有 DotNetZip 的 GHSA-xhg6-9j5j-w4vf，`dotnet build` 期审计不报它、`package list --vulnerable` 报——已写明理由）。
- **告警清零**：`Assert.Single/Empty(…Where…)` 改谓词重载；`FakeCtpSession.ConnectionStateChanged` 不再闲置——补 `RaiseConnectionState` 并新增 `Disconnected_session_logs_not_ready_but_still_submits_the_order`（钉住「断线窗口只记日志、不发 Brokerage Error、本单照常提交」这条防停引擎契约）。测试 99→100。
