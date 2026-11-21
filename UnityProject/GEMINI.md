# GEMINI.md — TEngine / UnityProject

请使用中文回答和写方案。本文件为 Gemini / Antigravity 处理 `TEngine/UnityProject/` 游戏代码时的工程入口（基于 HybridCLR + YooAsset + UniTask + Luban，Unity **6000.3.17f1** Built-in RP）。

---

## ⚡ 强制工作流（按任务等级查 `tengine-dev`）

### 第零步：判断任务等级（宁可高估，不低估）
| 等级 | 判断标准 | `tengine-dev` 查询策略 |
|---|---|---|
| **L1 简单** | typo、注释、纯日志、单行改名（不涉框架 API/UI 节点/事件/资源路径） | 跳过查询，直接改 |
| **L2 调用** | 调用已知 API、单一模块局部改动 | 用 `read_file` **只读** `.claude/skills/tengine-dev/references/` 对应单篇 |
| **L3 功能** | 新功能、跨文件改动、新增 UI/资源/事件逻辑 | 先看 `.claude/skills/tengine-dev/SKILL.md` 路由表，按需读相关几篇 |
| **L4 架构** | 模块设计、系统重构、多模块协作 | 并行读取所需多篇 references（禁整夹通读） |

### 第一步：References 唯一权威路由（`.claude/skills/tengine-dev/references/`）
| 场景 | 必读文档 | 备注 |
|---|---|---|
| 新面板 UI（战斗/抽卡/构筑/商店/图鉴/结算） | `ui-toolkit.md` | **一律 UI Toolkit**；先读 `Docs/UI/UI_PIPELINE.md` 与 `UI_WORKFLOW_GUIDE.md` |
| 旧链 UI（登录/启动/主菜单 uGUI 生命周期） | `ui-lifecycle.md` + `ui-patterns.md` | 仅旧 Canvas 链；禁混用 UI Toolkit |
| 旧链 UI 布局/Canvas 缩放 | `ui-ugui-layout.md` | 仅 `LoginUI` / `MainMenuUI` / `BattleMainUI` |
| 资源加载/释放 | `resource-api.md` + `resource-patterns.md` | 路径为 `Assets/GameRes/Raw/...` |
| 事件系统 | `event-system.md` + `event-antipatterns.md` | 跨模块 `GameEvent`，UI 内 `AddUIEvent` |
| 模块 API | `modules.md` | 统一 `GameModule.XXX` |
| 热更边界 | `hotfix-workflow.md` | `Main/` AOT vs `HotFix/` 热更 |
| 配置表 | `luban-config.md` | `python ../../tools/cell_tables/gen_all.py` 再导 Luban |
| 代码规范 / 群体避障 | `naming-rules.md` / `combat-avoidance.md` | 按需查阅 |
| UnityMCP 操作 / 排障 | `mcp-tools.md` / `mcp-visual.md` / `troubleshooting.md` | 按需查阅 |

若 references 与实际代码冲突：用 `codegraph` / `read_file` 核对真实签名，**以源码为准**，并记录冲突到 `.claude/memory/`。

---

## 核心编码红线
1. **异步优先**：IO 用 `UniTask`，禁止同步加载与 `Coroutine`。
2. **模块访问**：统一 `GameModule.XXX`，禁止 `ModuleSystem.GetModule<T>()`。
3. **资源成对释放**：`LoadAssetAsync` 对应 `UnloadAsset`，预制体用 `LoadGameObjectAsync`。唯一内容根：`Assets/GameRes/`（YooAsset 只打 `Raw/`，`Art/` 为源；禁止用已删除的 `AssetRaw`/`AssetArt`）。
4. **热更与性能边界**：`Assets/GameScripts/Main/`（含 `Main/Sim/` `BinGames.Sim`）为 AOT，Burst/Jobs/O(N) 放此处；`HotFix/GameLogic/` 全热更、每帧 O(1)，禁直接用 `NativeArray`/`NativeList`（仅 `Battle/SimBridge.cs` 与压测允许）；`Campaign/Combat/` 外不直接碰 `site.Kernel`。
5. **事件解耦**：跨模块 `GameEvent`（新增接口后跑 `TEngine/Tools/GameEventSourceGenerator`），UI 内用 `AddUIEvent`。

---

## ProjectA 0.2 正式版（FG）规格与验收
1. 从 `DesignDocs/README.md` 定位：`ProjectA_GDD.md`（0.2）、`ProjectA_FullGame_Milestones.md`、`DesignDocs/fullgame/` `FGxx`、`FG00_Completeness_Addendum.md`（B01～B25）与 `00_Implementation_Completeness_Contract.md`。Demo 0.1（`earth-reclamation/`）已冻结仅用于回归。
2. 查 FG Story 卡用 `python ../../tools/fg_card.py <StoryID>`（禁整本读 `FG-STORY-CARDS.md`）；开工前过 `../../production/agent-rules/` 对应类别清单。
3. 编译校验用 Git Bash 跑 `& "C:/Program Files/Git/bin/bash.exe" ../../tools/check-build.sh`（禁直接 `dotnet build` 过期 csproj）；静态扫雷跑 `python ../../.agents/plugins/bingames-gemini/scripts/static_audit.py`；UI Toolkit 改完跑 `BinGames.EditorTools.UiToolkitLayoutProbe.Probe("<uxml路径>", "<根节点名>")`。
4. 验收证据仅写仓库根 `../../production/qa/evidence/`（已 gitignore）；**禁止**写入或提交 `Assets/Screenshots/`。
