## ADDED Requirements

### Requirement: 独立库零 Unity 依赖
`ChemEngine` SHALL 是独立 C# 库（`ChemEngine/src/ChemEngine/`，目标框架 `netstandard2.1`），不引用任何 Unity 引擎包，可脱离 Unity 单独构建与测试。

#### Scenario: 脱离 Unity 独立构建
- **WHEN** 在 `ChemEngine/` 目录执行 `dotnet build src/ChemEngine/ChemEngine.csproj -c Release`
- **THEN** 产出 `ChemEngine.dll`，构建过程不依赖 Unity 编辑器或引擎包

### Requirement: 通用数据容器
所有战斗效果 SHALL 只读写三个通用容器 `Packet`（装配链流通物）、`HitEvent`（出口事件）、`RuleVector`（基因/全局正规形），基元不得携带私有字段替代这三者的语义。

#### Scenario: 基元通过通用容器交互
- **WHEN** 一个 `Module` 处理流经它的 `Packet`
- **THEN** 它只修改 `Packet` 冻结字段（Energy/Heat/Shape/Scale/Count/Spin/Orbit/Tags/Mult/Payload），不新增基元专属字段

### Requirement: 装配管道与求解器
`Engine` SHALL 提供装配基元（`Module`）+ 契约基元（`Contract`）的注册与求解，`ReactionCatalog.RegisterDefaults` 等目录类负责登记默认反应/组件，求解入口 MUST 按冻结的契约管道顺序（BanRanged → DelayWrap → ElementAttach → Reflect → Share → SwarmScale → Commit → Lifesteal → RageCheck → HeatSettle → WorldTagReactions）结算并产出 `HitEvent` 列表。

#### Scenario: 注册目录后求解产出 HitEvent
- **WHEN** 调用 `ReactionCatalog.RegisterDefaults(engine)` 注册默认反应，随后对一个装配好的 `SlotGrid` 调用求解入口（如 `MetabolicSliceRunner.Tick`）
- **THEN** 返回 `IReadOnlyList<HitEvent>`，每个事件字段（Damage/Heal/Tags/...）按契约管道顺序结算完成
