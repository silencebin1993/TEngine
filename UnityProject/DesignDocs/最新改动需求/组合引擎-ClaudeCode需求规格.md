# 化学引擎代码库需求规格（给 Claude Code）

> 用途：把本文整份交给 Claude Code，生成一个**可复用、尽量万能适配**的「组合化学引擎」库。  
> 实现路线：**做法 C**（基元只写本地规则 + 固定引擎做叠加/规约/结算）。  
> **禁止**方案 A（基元互相 `isinstance` / 两两配对接口膨胀）。

---

## 1. 目标一句话

做一个与具体游戏解耦的 **C# 独立库**：作者定义任意数量「基元」（模块 / 契约 / 状态修饰 / 弹体行为），玩家任意组合后，引擎用**统一数据包 + 有序管道 + 正规形软帽**产生可复现的化学反应结果。

交付形态：`dotnet build` 得到 **`ChemEngine.dll`**，或把**独立源码文件夹**拷进 Unity（核心仍不引用 Unity）。

**后续无限扩展**靠架构保证（新基元只挂字段/管道，不改旧基元），**不靠堆例子数量**。例子只需证明两类能力：① tag/状态催化（火+水→汽）；② 多修饰正交叠乘（双倍×变大×散射×旋转×碰撞爆炸）。

成功标准：

1. 新增基元 = 只加新类/新数据，**不改旧基元代码**  
2. 任意拼接/叠加都有确定语义（Accept / Coerce / Reject-to-Safe / **NoOp 正交叠**），不崩溃、不 NaN  
3. 同一输入 + 同一 seed → 同一输出（可测）  
4. 用少量例子证明：催化反应 + 多修饰叠乘 + 软帽/冲突规约至少各 1 个  

---

## 2. 非目标

- 不要用 **Python**（或其它脚本当主库）  
- 不要依赖 LLM 运行时「现编反应」  
- **核心程序集禁止引用** `UnityEngine` / `UnityEditor` / Godot / Unreal 等任何游戏引擎 API  
- 不要为每个两两组合写死技能表当主路径  
- 不要从零训练 AI 模型  

---

## 3. 技术形态（独立 DLL / 可拷贝进 Unity 的无依赖文件夹）

### 3.1 语言与目标框架

- 语言：**C#**  
- TFM 优先：`netstandard2.1`（或同时打 `netstandard2.0` + `net8.0`）  
  - 可 `dotnet build -c Release` 产出 **DLL**  
  - Unity 可将整个源码文件夹或 DLL 放进 `Assets/Plugins/ChemEngine/`（或类似路径）**直接使用**  
  - 因零 Unity 引用，在纯 .NET 控制台 / 服务器 / 工具链同样可跑  
- 包名 / 程序集名：`ChemEngine`（命名空间 `ChemEngine.*`）  
- 可选第二宿主：同一套逻辑不写第二语言；若需 C++/其它，仅允许薄 FFI，**本需求只交付 C# 核**

### 3.2 仓库/文件夹布局

```text
ChemEngine/                          # 独立文件夹（可整体拷进 Unity Assets）
  ChemEngine.sln
  src/
    ChemEngine/
      ChemEngine.csproj              # netstandard2.1，无引擎包引用
      Core/                          # Packet, HitEvent, RuleVector, SimContext
      Modules/                       # IModule + 内置模块
      Contracts/                     # IContract + 内置契约
      Statuses/                      # 状态 tag + 反应注册表
      Engine/                        # Normalize, Pipeline, Fire, Typecheck, Search
      Catalog/                       # 注册表
      Serialization/                 # JSON（System.Text.Json 或手写轻量）
  examples/
    ChemEngine.Examples/
      ChemEngine.Examples.csproj     # net8.0 控制台，引用 ChemEngine
      Example_01_....cs
      ...
      RunAll.cs
  tests/
    ChemEngine.Tests/
      ChemEngine.Tests.csproj        # xUnit 或 NUnit，引用 ChemEngine
  README.md
```

### 3.3 构建与交付物

必须支持：

```bash
dotnet build src/ChemEngine/ChemEngine.csproj -c Release
# 产出：bin/Release/netstandard2.1/ChemEngine.dll

dotnet run --project examples/ChemEngine.Examples -c Release
dotnet test tests/ChemEngine.Tests -c Release
```

交付物：

1. **`ChemEngine.dll`**（及依赖，理想为零第三方或仅 BCL）  
2. **完整源码文件夹** `ChemEngine/`，可复制到 Unity 工程且仍不包含 `#if UNITY` 核心逻辑  
3. 控制台 Examples 可独立运行验证化学，不启动 Unity  

### 3.4 Unity 使用方式（文档写清即可，代码仍零依赖）

- 方式 A：拷贝 `src/ChemEngine` 源码到 `Assets/ThirdParty/ChemEngine`  
- 方式 B：把 Release DLL 放到 `Assets/Plugins/ChemEngine/`  
- 游戏侧另写 **可选** `ChemEngine.Unity/`（本需求可不实现）做 `MonoBehaviour` 桥接；**不得**把桥接代码编进核心 DLL  

### 3.5 API 风格

- 公共 API 稳定；XML 文档注释齐全  
- 避免 Python 式动态；用接口 + 可序列化 DTO  
- `payload` 用 `Dictionary<string, object>` 或更类型安全的 `Dictionary<string, string>` + 数值旁路字段；优先可序列化结构

---

## 4. 核心概念（必须实现）

### 4.1 Packet（装配链流通物）

至少字段：

- `energy: float`  
- `heat: float`  
- `shape: str`（如 Bolt/Beam/Arc/Field/Wave）  
- `tags: HashSet<string>` 或只读包装  
- `payload`：可序列化扩展字典（给自定义基元用，避免改核心）  

### 4.2 HitEvent / WorldEvent（世界出口）

至少字段：

- `damage`, `heal`, `delay`, `burn`, `wet`, `shock`, `poise`  
- `shape`, `tags`, `payload`  
- 可扩展：击退、传送、召唤计数等放 `payload`

### 4.3 RuleVector（契约正规形）

契约无限叠先累加进向量，再软帽。至少包含：

- 伤害/治疗修饰、燃烧、潮湿、感电、延迟、反射模式、禁远程、近战倍率  
- 共享状态、寡兵曲线、吸血、承伤倍率、绝境标记  
- `stack_count`  
- `payload` 扩展  

软帽公式（可配置）：

```text
soft = 1 / (1 + beta * max(0, stack_count - 1))
对可缩放字段乘 soft；delay 用 max 合并；互斥用冲突代数规约
```

### 4.4 Module（装配基元）

```csharp
public interface IModule
{
    string Id { get; }
    string Name { get; }
    Packet Step(Packet packet, SimContext ctx);
}
```

规则：

- **只改 packet / 自己的 state / ctx**  
- **禁止**用具体模块类型做组合技（禁止 `is Capacitor` / `as FocusModule` 写化学反应）  
- 通过 `packet.Tags` / `packet.Payload` / `ctx` 协作  

内置模块建议 ≥12 个（例子会用到）：  
能源芯、电容、分流器、汇流器、聚焦镜、散射器、散热器、反馈环(Hit/Hurt)、保险闸、延迟线、执行器(攻击/护盾/位移)、过滤器(按 tag 放行)

### 4.5 Contract（契约基元）

```csharp
public interface IContract
{
    string Id { get; }
    void Apply(RuleVector rules);
}
```

只改 RuleVector；不点名其它契约。  
内置契约建议 ≥12 个：血债、燃律、潮律、雷律、镜界、哑火、延偿、共相、寡兵、绝境、节拍加速、过载许可以等。

### 4.6 Status / Reaction（可选但建议有）

单位或地形带 status tags（Burning, Wet, Oiled, Frozen…）。  
反应引擎根据 **tag 集合 + 事件** 产生结果（如 Wet+Shock→额外伤并清 Wet）。  
反应规则注册在引擎表里（数据/注册函数），**不要**写进每个 Status 类的两两接口。

### 4.7 Engine API（对外主入口）

必须提供（C# 公共 API）：

```csharp
TypecheckResult Typecheck(IReadOnlyList<IModule> modules);
RuleVector NormalizeContracts(IReadOnlyList<IContract> contracts);
AssemblyNF NormalizeAssembly(IReadOnlyList<IModule> modules);
IReadOnlyList<HitEvent> RunAssembly(IReadOnlyList<IModule> modules, int ticks = 1, int seed = 0);
HitEvent ApplyPipeline(HitEvent evt, RuleVector rules, WorldState world);
FireResult Fire(IReadOnlyList<IModule> modules, IReadOnlyList<IContract> contracts, WorldState world, int seed = 0);
void RegisterReaction(ReactionRule rule);
string Explain(FireResult result);
SearchResult Search(SearchRequest request); // 最小：随机+爬山
string ToJson(BuildDto build);
BuildDto FromJson(string json);
```

伤害/事件管道顺序（写死可配置，但默认固定）：

```text
1 BanRanged
2 DelayWrap
3 ElementAttach (burn/wet/shock…)
4 Reflect
5 Share/Redirect
6 SwarmScale
7 Commit
8 Lifesteal
9 RageCheck
10 HeatSettle / StatusReactions
```

### 4.8 不变量（测试必须覆盖）

- 能量不无界放大超过 `AmpCap`  
- 聚焦类增伤必带 heat 增加  
- 反馈回能有预算 κ  
- 敌我同律开关（默认 True）  
- 同 seed 可复现  
- 软帽后 RuleVector 范数有界  
- 乱接不抛未处理异常（Reject-to-Safe）  

### 4.9 求解器（可先做最小版）

最小实现：随机采样 + 爬山。目标示例：`damage / (1+overheat_risk)`。  
接口预留在 `ChemEngine.Engine.Search`。

---

## 5. 「万能适配」要求

1. **核心 csproj 不得引用任何游戏引擎包**  
2. 所有数值常量进 `EngineConfig`  
3. Packet/Event/RuleVector 均有扩展槽  
4. 管道步骤可插拔：`pipeline.InsertAfter("BurnAttach", name, fn)`  
5. 提供适配协议（核心内只定义接口）：

```csharp
public interface IGameAdapter
{
    void OnVfx(HitEvent evt);
    void ApplyDamage(string targetId, HitEvent evt);
}
```

库只产出 `FireResult`；宿主（Unity 或其它）自行 apply。  

6. JSON 序列化保存 build（模块链 + 契约列表 + 配置）  
7. 线程假设：默认单线程调用；文档标明非线程安全或提供不可变快照 API  

---

## 6. 实现禁令（Code Review 用）

在 `Modules/`、`Contracts/` 业务基元中：

- ❌ `is ConcreteModule` / `as ConcreteModule` 做组合技  
- ❌ `if (team.Any(m => m is Capacitor) && …)`  
- ❌ 每加基元就改旧基元文件  

允许：

- ✅ 引擎层对 `IModule` / `IContract` 调度  
- ✅ 少量催化反应注册在 `Reactions` 数据表（tag 集合 → 结果）  
- ✅ 冲突代数在 `NormalizeContracts` 统一处理  

---

## 7. 文档要求

README（中文可）必须包含：

- 5 分钟：`dotnet build` 出 DLL + `dotnet run` 跑例子  
- 如何拷进 Unity（DLL 或源码文件夹）且强调核心零依赖  
- 做法 C 原理（Packet 流水线）  
- 如何新增 Module / Contract / Reaction（C# 模板）  
- 例子索引表  
- 与「反应大表」关系：测试预言，非实现主轴  

---

## 8. 例子需求（精简：证明架构即可）

**不需要 30 个例子。** 无限扩展靠 §4–§6 的 Packet/管道/注册机制；例子只做验收演示。

硬性最少 **3 个**可运行例子 + `RunAll`：

### 例 A — 催化反应（火 + 水 → 汽）

- 基元：`FireHit`（攻击附带 Fire tag/烧）、`WaterCoat`（上 Wet）、目标初始或过程带 Wet、反应表 `Fire+Wet → Steam`（清 Wet、出蒸汽场/伤害改写）  
- 验收：打出的结果是蒸汽语义，而不是「火和水分开各算各的」且无第三产物  
- 证明：少量 **tag 催化**可注册，不必两两写死在基元类里  

### 例 B — 多修饰正交叠乘（用户指定场景）

基元（≥5）：

1. `DoubleEffect`（双倍）— 全局倍率 `mult *= 2`（作用于后续可缩放字段）  
2. `Grow`（变大）— 弹体 `scale` 随时间增大；基础变大速率或目标倍率记为 1→「变大」  
3. `Scatter`（散射）— 基础分裂数 `count = 2`  
4. `OrbitSpin`（自身旋转 / 绕圈）— 弹体沿圆周运动参数  
5. `ExplodeOnHit`（碰撞爆炸）— 命中触发爆炸事件  

期望正规结果（数值按双倍规则）：

- 散射：`2 * 2 = 4` 发  
- 变大：变大效果双倍（例如尺度增速或最终尺度相关量 ×2）  
- 运动：发射后缓慢变大，并绕圆旋转  
- 命中：爆炸  
- **不要**为「双倍+散射」「双倍+变大」写专用配对函数；双倍只改 `RuleVector`/`Modifier` 倍率，散射/变大读取倍率  

### 例 C — 边界/规约（任选其简）

三选一即可：软帽叠很多次同类修饰、冲突规约（两契互斥收成 NF）、Reject-to-Safe（缺执行器不崩溃）。

每个例子输出：标题、输入、最终事件关键字段、`Explain` 一两行。

---

## 8.1 架构如何保证「后续无限玩法」而不靠堆例子

| 新玩法类型 | 怎么加 | 要不要改旧基元 |
|------------|--------|----------------|
| 新弹体行为（跟踪、弹射…） | 新 Module/Modifier，写自己的字段 | 否 |
| 新元素反应（油+火…） | `RegisterReaction(tags → result)` | 否 |
| 新全局倍率/诅咒 | 新 Contract → RuleVector 字段 | 否 |
| 新管道步骤 | `InsertAfter(...)` | 否 |

「万能」= **字段 + 管道 + 反应注册表** 开闭原则；不是例子覆盖所有未来组合。

---

## 9. 测试需求

- `dotnet test`：xUnit 或 NUnit  
- 不变量、软帽/冲突、可复现、**例 A/B/C**  
- 至少一个：随机合法 build 100 次不崩溃  
- 断言：火+湿→汽；双倍后散射 2→4、变大相关量×2  

---

## 10. 交付清单（Claude Code 完成定义）

- [ ] `ChemEngine.csproj` → `netstandard2.1`，**零游戏引擎引用**  
- [ ] Release 产出 **`ChemEngine.dll`**；源码可拷进 Unity  
- [ ] 核心引擎 + 够用的内置基元（不必堆数量）  
- [ ] `Fire` / `Normalize*` / `Explain` / JSON；Search 可选  
- [ ] **例 A、B、C**，`RunAll` 通过  
- [ ] README：DLL、Unity 拷贝、加新基元模板  
- [ ] `dotnet test` 通过；禁止方案 A  

---

## 11. 给 Claude Code 的启动提示（可复制）

```text
请根据《化学引擎-ClaudeCode需求规格》实现独立 C# 库 ChemEngine。

硬性要求：
1. 不要用 Python。用 C#，ChemEngine.csproj 目标 netstandard2.1（例子/测试可用 net8）。
2. 核心禁止引用 UnityEngine/Godot/Unreal；可产出 ChemEngine.dll；源码可拷进 Unity。
3. 做法 C：只改 Packet/RuleVector/注册反应表，禁止具体类型配对写组合技。
4. 统一引擎：流水线 + normalize（软帽+冲突）+ 可插拔 pipeline + RegisterReaction。
5. 例子不需要很多：至少 3 个——
   A) 火+水→水蒸气（tag 催化）
   B) 双倍+变大+散射+自身旋转+碰撞爆炸 → 4 散射、变大×2、绕圈、命中爆炸（双倍只改倍率，散射/变大读倍率）
   C) 一个边界（软帽或冲突或 Reject-to-Safe）
6. 架构必须支持后续只加新基元/新反应注册而不改旧基元；README 写清扩展模板。
7. dotnet test；先跑通 A/B 再补 C。

不要堆 30 个例子。不要 LLM 运行时反应。
```

---

## 12. 设计决策摘要

| 决策 | 选择 |
|------|------|
| 交付形态 | 独立 C# DLL / 可拷贝文件夹；不依赖 Unity |
| 实现路线 | 做法 C |
| 无限扩展靠什么 | 字段+管道+反应注册，**不靠例子穷举** |
| 例子 | 少而硬：催化 1 + 多修饰叠乘 1 + 边界 1 |
| 无特殊反应的组合 | **必然大量存在**（正交叠加或 NoOp），不是缺陷 |

---

## 13. 关于「是否必然存在不能相互反应的组合」

**是，必然存在——而且应该存在。**

| 类型 | 含义 | 是否正常 |
|------|------|----------|
| **正交叠加** | 两者都生效，但不产生第三种新名字（双倍×散射→4 发） | 正常，无限组合主力 |
| **催化空窗** | 未注册 tag 对，就各算各的 | 正常；要新化学就 RegisterReaction |
| **互斥/抵消** | 正规形里削掉或覆盖 | 正常 |

只有「未定义就崩溃」才是缺陷。  
若强求任意两基元都有独特命名反应 → 回到方案 A，无法无限开发。

---

*本文是代码生成需求；玩法叙事见《自洽宇宙最小规格》《做法C详解》。*
