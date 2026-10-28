# Aether.Trading

交易运行时（Lean 引擎链）——QuantConnect LEAN 实盘/回测引擎进程内运行 + CTP 券商适配（Brokerage）+ Roslyn 策略编译。

## 组成（六项目）

| 项目 | 说明 |
|---|---|
| `src/Aether.Trading.Domain` | 交易领域模型 |
| `src/Aether.Trading.Runtime.Domain` | 实盘运行时域（LiveTradingRuntimeRequest / BarResolution / 引擎枚举等） |
| `src/Aether.Trading.Runtime.Lean` | LEAN NuGet（QuantConnect.Lean.Engine 2.5.18193）实盘/回测引擎：LeanLiveRunner（镜像 Launcher 编排）/ CtpBrokerage（data-queue-handler + live-mode-brokerage 双角色）/ Lean 脚本编译器。QuantConnect.* 类型只在本叶子内可见 |
| `src/Aether.Trading.Scripting` | Roslyn 策略编译（IStrategy 壳引擎） |
| `src/Aether.Trading.Application` | 应用服务（实盘运行时服务编排） |
| `src/Aether.Quotes.Domain` | 行情域 |

## 设计要点

- **Lean NuGet 化三坑**（均已修，详见 MIGRATION）：Engine 的 nuspec 不声明 Queues/Messaging 依赖，MEF 组合需显式补引（否则 `Unable to locate any exports: QuantConnect.Queues.JobQueue`）；LeanData 定位：向上找 `externals/Lean/Data` → fallback `<exe>/LeanData`（AetherAxis 形态走后者——⑧ console csproj 把本仓 `LeanData/` 随构建拷进输出）；live-mode 需在 `Initializer.Start()` 前显式 `Globals.Reset()`
- **CtpBrokerage 双角色**：同一类既是 data-queue-handler（tick 流自 Ctp.Net.ICtpSession 会话进 Lean）又是 live-mode-brokerage（下单/撤单/成交回报）
- **策略源运行时 Roslyn 编译**：`labs/leanalgorithms/*.cs` 动态加载——命名空间改名属运行时失败而非编译期失败
- **契约正典**：客户端会话接口（ICtpSession）在 ctp-net-contracts 仓（namespace `Ctp.Net`），本仓不持本地副本

## 构建与测试

```bash
dotnet test aether-trading.slnx    # tests/Aether.Trading.Tests：100 例（指标/条件编译/YAML 策略派发/Roslyn 编译缓存/Brokerage 映射与回报翻译——FakeCtpSession 零真实 CTP）
```

原 monorepo 的 Domain/Runtime 两个测试项目在此合并为单测试项目（拆分按依赖重量、独立仓无此必要）。
交易所 bot 策略源（market-maker/noise-taker）的编译锁定在 aether-ctp-hosts/tests（策略源存档在彼仓根 algorithms/）。

## 来历

2026-10-20 拆自 Aether monorepo（初名 `Acme.Trading.*`，同日命名统一 `Aether.Trading.*`；Lean 从 monorepo 源码引用改为 NuGet 包）。完整决策记录见工作区根 `MIGRATION.md`。
