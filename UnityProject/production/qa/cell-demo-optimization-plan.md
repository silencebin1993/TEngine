# 细胞 Demo 优化设计规格书（供 Claude Code CLI 执行）

> **项目**：`F:\Project\BinGames\TEngine\UnityProject`（TEngine + HybridCLR + Unity MCP）
> **分工**：本文档由设计方编写，**所有代码实现与验收均由 Claude Code CLI 执行**。
> **核心原则**：**逐项 AI 验收**，每项改动必须"看得到、验得出、不破坏"，截图留证到 `production/qa/evidence/`。

---

## 〇、已拍板决策（不必再问）

| 决策项 | 结果 |
|--------|------|
| 器官归类 | **方案②：重新归类**——把非"载体生成"功能的器官从"器官"类目移出，单独成"内质/代谢模块"类目 |
| 验收工具 | **CC CLI（`claude`）驱动 Unity MCP**（Play 模式 + 截图） |
| 3D 造型资源 | 程序化 Mesh / 组合体，零美术依赖 |

---

## 一、现状诊断（根因）

### 1. 3D 表现：基元/器官/基因"看不出区别"
- 渲染路径：`Assets/GameScripts/Main/Sim/SimRenderer.cs`，GPU Instancing，按 `VisualId → SimVisual(Mesh + Material + ScaleMul + BaseColor)` 渲染。
- **根因**：绝大多数单元共用同一种球体/团块 Mesh + 同一套材质，只有 `BaseColor` 不同。器官/基因在 3D 上没有任何形状/结构/朝向差异。玩家 Carrier 也是"一个彩色圆团"，装备的器官/挂载的基因在模型上不体现。

### 2. 器官选择：只有分泌喷射器/纤毛刺可选
- 根因：`Assets/GameScripts/HotFix/GameLogic/MetabolicSlice/ContentCatalog/OrganelleCatalog.cs` 中，**仅 `org_emitter`、`org_cilia` 标了 `isCarrier: true`**，只有它们能作为 Carrier 的"出口器官"被选择。
- 其余器官（液泡/高尔基/汇流/晶状/纺锤/膨胀/鞭毛/溶酶等）`isCarrier: false`，是**模块基因**，走基因插槽，不在 Carrier 器官栏展示 → 玩家觉得"别的器官不能选"。
- `OrganelleDef` 公开属性：`IsCarrier`（第 31 行）、`IsRetired`（第 35 行）、`Id`、`DisplayName`、`Description`、`Role`、`AllowedSlotTypes`、`ArtId`。

### 3. UI：布局不人性化、个别框畸形
- 沙盒（LookDev）/ 木桩（Dummy）功能仍在 `Assets/GameScripts/HotFix/GameLogic/UI/Battle/CellDebugHud.cs` 的 **IMGUI `OnGUI`** 中：固定宽度、不随内容自适应 → 文字溢出、框畸形。
- 缺基元/器官/基因的**悬停简介（Tooltip）**。
- 已有 UI Toolkit 框架可复用：`BattleCarrierUIToolkit`、`BattleOverlayUIToolkit`（含 `ShowTooltip`/`HideTooltip` 浮层，可复用）。

### 4. 玩法深度不足
- 技能/卡牌已覆盖吞噬/电化/孢子/菌毯/污染路线，缺**器官直接驱动的高频玩法**；用户点名**召唤（Summon）**缺失。

---

## 二、统一验收口径

每项改动用 3 个维度验收，缺一不可：

| 维度 | 判定方式 | 工具 |
|------|---------|------|
| **看得到** | Play Mode 下截图/肉眼对比，同屏两种东西必须有明确差异（形状/颜色/朝向/大小） | Unity MCP `manage_scene screenshot` |
| **验得出** | 行为/数值/选择在运行时按预期工作 | Unity MCP `manage_editor play` + `read_console` |
| **不破坏** | 编译零错误、原功能不回退 | `read_console`（过滤 Error）+ 回归 |

**留证规则**：所有验收截图写入 `production/qa/evidence/<任务号>/`，**严禁**写入 `Assets/Screenshots/`（已 gitignore）。

---

## 三、任务一：器官重新分类（方案②）

> **目标**：玩家眼中"器官"= 载体器官（emitter/cilia）；其余 22 个归"代谢模块"，避免"不能选"的误导。

### 涉及文件与改动点

**1. `Assets/GameScripts/HotFix/GameLogic/Progression/CodexRegistry.cs`**
- 现状：`AllOrganelleEntries()`（约 169 行）返回全部 24 个组织，未区分载体/模块。
- 改动：
  - 新增 `AllCarrierOrganelleEntries()`：遍历 `OrganelleCatalog.All.Values`，仅返回 `def.IsCarrier == true` 的条目（载体器官）。
  - 新增 `AllMetabolicModuleEntries()`：返回 `def.IsCarrier == false` 的条目（代谢模块）。
  - 保留 `AllOrganelleEntries()` 不动（向后兼容，其他调用方不受影响）。

**2. `Assets/GameScripts/HotFix/GameLogic/UI/BattleOverlayUIToolkit/BattleOverlayUIToolkit.cs`**
- `CodexCategory` 枚举（约 42 行）：在 `Organelle` 后插入 `MetabolicModule`。
- `CodexTabNodeNames` 数组（约 81 行）：在 `"TabOrganelle"` 后插入 `"TabModule"`。
- `RefreshCodex` 方法（约 602 行）：
  - `case CodexCategory.Organelle:` 改用 `codex.AllCarrierOrganelleEntries()`，来源标签改 `"载体器官"`。
  - 新增 `case CodexCategory.MetabolicModule:` 遍历 `codex.AllMetabolicModuleEntries()`，来源标签 `"代谢模块"`。

**3. UXML（两份都改，保持一致）**
- `Assets/GameRes/Art/Cell/BattleUI/uxml/BattleOverlays.uxml`
- `Assets/GameRes/Raw/UI/BattleUI/BattleOverlays.uxml`
- 改动：`CodexTabBar` 内（约 34 行），在 `<ui:Button name="TabOrganelle" text="器官" .../>` 后插入：
  `<ui:Button name="TabModule" text="代谢模块" class="codex-tab" />`

**4. `Assets/GameScripts/HotFix/GameLogic/UI/BattleCarrierUIToolkit/BattleCarrierUIToolkit.cs`**
- `RefreshCarrierListAllCatalog()`（约 348 行）：全量目录当前用 `AllOrganelleEntries()` 平铺 24 项。
- 改动：拆为两组渲染——先输出分组标题 `"载体器官"` + `AllCarrierOrganelleEntries()` 条目，再输出分组标题 `"代谢模块"` + `AllMetabolicModuleEntries()` 条目（分组标题复用 `RefreshGeneList` 里"契约基因/模块基因"的分组标题样式）。

### 验收（CC CLI 执行）
1. `manage_editor play` 进入 Play，`read_console` 确认零编译错误。
2. 打开图鉴（V 键）：截图确认出现"器官 / 代谢模块 / 基因 / 插槽 / 地形 / 状态 / 敌人"7 个 Tab；器官 Tab 仅 2 项，代谢模块 Tab 约 22 项。
3. 打开 Carrier 装配面板：截图确认全量目录分组显示"载体器官"/"代谢模块"。
4. 截图存 `production/qa/evidence/task1-organelle-reclassify/`。

---

## 四、任务二：3D 表现差异化（最高优先级）

> **目标**：每个基元/器官/基因在 3D 上"形状可辨"，从"同球不同色"升级为"形状/结构/朝向可区分"。

### 涉及文件与改动点
- `Assets/GameScripts/Main/Sim/SimRenderer.cs`：扩展 `VisualId → SimVisual` 注册。
- `Assets/GameScripts/HotFix/GameLogic/Core/CellContentSeed.cs`：单元 `VisualId` 分配（当前敌人 VisualId 仅为整数占位）。
- 程序化 Mesh 方案：为不同器官/基因生成差异化几何——球体/胶囊/锥体/环面/多刺球/尾鳍挂件等组合体，按 `ArtId`（如 `org/emitter`、`org/cilia`、`org/vacuole`）映射。
- 玩家 Carrier：装备不同出口器官（emitter=远程喷口、cilia=近战鞭毛）+ 挂载基因时，3D 造型随装配变化（附加尾部/突刺/腔体/鞭毛等子物体）。

### 验收
1. 在沙盒一键生成"全部基元/器官/基因 3D 对比台"（同屏摆开所有原型）。
2. `manage_scene screenshot` 逐个/分组截图，肉眼确认两两可区分。
3. 截图存 `production/qa/evidence/task2-3d-differentiation/`。

---

## 五、任务三：召唤（Summon）机制

> **目标**：器官/技能驱动生成附属体，作为新的可验收玩法。

### 涉及文件与改动点
- 新增召唤器官/技能定义（ContentCatalog），`isCarrier` 归类按任务一结论。
- 生成逻辑：附属体（孢子/噬菌体/菌丝仆从）作为独立 Sim 单元，可控或自动索敌。
- 3D：附属体有独立造型（复用任务二的差异化体系）。

### 验收
1. Play 中触发召唤 → `read_console` 确认附属体生成。
2. 截图确认附属体在场景中可见且与主体可区分。
3. 截图存 `production/qa/evidence/task3-summon/`。

---

## 六、任务四：UI 重设计（沙盒/木桩迁 UI Toolkit + 修畸形框）

> **目标**：把 IMGUI 沙盒面板迁移到 UI Toolkit 正式窗口，木桩开火功能随之迁移；消灭固定宽度造成的文字溢出/框畸形，布局自适应。

### 现状（精确）
- 沙盒面板 = `CellDebugHud.DrawLookDevSandbox`（`CellDebugHud.cs:447`），IMGUI 窗口，固定 `_lookDevRect = new Rect(12,12,900,680)`（:452）。
- 内容 = `DrawLookDevSandboxContent`（:457）：左列基因/器官多选列表（固定宽 320，:467/:481）、中列 7 维度覆盖滑杆（固定宽 300，:501）、右列 HitEvent 预览 + 开火按钮（固定宽 240，:549）。
- **木桩（dummy）开火已内嵌在沙盒里**：`开火（打木桩）`按钮（:559）→ `FireSandbox`（:645）→ `MetabolicSliceBridge.ApplyEvent`；另有"自动连发"开关（:564）+ 间隔滑杆，逻辑在 `Update`（:93-111）。
- 畸形根因：IMGUI `GUILayout.Width(...)` 硬编码 + 固定 Rect，不随内容/分辨率自适应。
- 沙盒开关态：`_lookDevActive`（:49），`OnGUI` 里 `_lookDevActive` 时 `DrawLookDevSandbox` 并 return（:133-137）。入口在主菜单"LookDev 沙盒"按钮（:222，仅 `#if UNITY_EDITOR || DEVELOPMENT_BUILD`）。

### 迁移目标（参照现有 UI Toolkit 控制器模式）
新建 `BattleSandboxUIToolkit.cs`（参照 `BattleCarrierUIToolkit` 常驻单例模式）：
- `Awake` 设 `Instance` + `DontDestroyOnLoad`；`Start` 里 `LoadAssetAsync<VisualTreeAsset>` + `PanelSettings("BattleHudPanelSettings")`，`AddComponent<UIDocument>`，10 帧 rootVisualElement 轮询保护（照抄 `BattleCarrierUIToolkit.cs:83-117`）。
- sortingOrder 取空档（现况 HUD=0 / Carrier=4 / Draft=6 / Overlay=10 / Result=12，沙盒可用 5）。
- 新建配套 UXML（参照 `BattleOverlays.uxml` / `BattleCarrierUI.uxml` 布局写法），三列用 `flex-direction: row` + `flex-grow`，**不写死宽度**，文字用 USS `white-space: normal` 允许换行。

### 改动点
1. 新增 `BattleSandboxUIToolkit.cs` + `BattleSandboxUI.uxml`：三列布局——基元多选列表（基因/器官，ScrollView）、7 维度覆盖控件（Toggle+Slider/TextField，Label 换行）、HitEvent 预览 + `开火（打木桩）`按钮 + 自动连发开关/间隔滑杆 + 退出沙盒按钮。
2. 数据/逻辑**全部复用**现有静态/实例方法，不重写：`SandboxAssembler.Compose`、`SandboxAssembler.OverridesFromEvent`、`LookDevFixtures.All`、`FireSandbox` 的 `MetabolicBridge.ApplyEvent` 调用、`ResetSandboxAssembler` 默认值、`Update` 自动连发计时（迁到新控制器）。
3. `CellDebugHud.cs`：沙盒入口（:222 按钮 + :133 `_lookDevActive` 分支）改为唤起 `BattleSandboxUIToolkit` 面板；旧 `DrawLookDevSandbox`/`DrawLookDevSandboxContent` 及相关 `DrawSandbox*` 辅助方法（:447-665）保留为对照（默认关闭），或按 CLAUDE.md 决定删除——**改动前先在会话里确认保留还是删除**。
4. 木桩/清场/刷怪等若需独立调试按钮，统一放进新沙盒面板，不散落 IMGUI。

### 验收
1. `manage_editor play` → 主菜单点"LookDev 沙盒"，`read_console` 零 Error。
2. 截图确认：沙盒为 UI Toolkit 面板、无 IMGUI 痕迹、三列自适应、文字无溢出。
3. 选基因/器官 → 预览实时更新；点"开火（打木桩）"→ `read_console` 确认 ApplyEvent 触发；自动连发按间隔工作。
4. 截图存 `production/qa/evidence/task4-ui-redesign/`。

---

## 七、任务五：基元/器官/基因 Tooltip

> **目标**：图鉴（Codex）里的条目悬停显示简介。**注意：基因/器官条目的 tooltip 已实现，本任务只补图鉴缺口。**

### 现状（精确）
- 浮层机制已就绪：`BattleOverlayUIToolkit.CreateTooltip`（:309）建 `_tooltip` Label（class `codex-tooltip`），`ShowTooltip(text, worldPos)`（:324，截断≤3行，定位+16/+16）、`HideTooltip()`（:337）。跨控制器经 `BattleOverlayUIToolkit.Instance` 复用。
- 已接入 tooltip 的：`BattleCarrierUIToolkit` 基因/器官/全量目录/插槽条目（:316/:340/:363/:490/:522/:569 已注册 PointerEnter/Leave）、`BattleOverlayUIToolkit` Deck 卡牌（:534）。
- **缺口**：图鉴条目 `AddCodexRow`（:752）生成的行**没有注册任何 hover 事件**——V 键图鉴里悬停基元/器官/基因/插槽/地形/状态/敌人条目都不出简介。

### 改动点（单文件：`BattleOverlayUIToolkit.cs`）
- 在 `AddCodexRow(name, description, extra, source, revealed)`（:752）内，给 `row` 注册：
  - `row.RegisterCallback<PointerEnterEvent>(evt => ShowTooltip(BuildCodexTooltip(name, description, extra, source), evt.position));`
  - `row.RegisterCallback<PointerLeaveEvent>(evt => HideTooltip());`
- 新增私有静态方法 `BuildCodexTooltip(...)`：拼接 `名称 + 换行 + 描述 + （extra 非空时）换行 + extra + （来源）`，交给 `ShowTooltip`（它内部已 `TrimToLines(...,3)`，超长自动截断）。
- 未揭示（`revealed==false`，如敌人"？？？"）条目也允许出"尚未遭遇…"提示文案，与现有遮罩逻辑一致。

### 验收
1. `manage_editor play`，V 键开图鉴，切到"器官/代谢模块/基因"Tab。
2. 鼠标悬停任一条目 → 截图确认浮层显示 名称/描述/数值/来源（≤3行，跟随鼠标偏移）。
3. 移出条目浮层消失。
4. 截图存 `production/qa/evidence/task5-tooltip/`。

---

## 八、任务六：整体验收（CC CLI 驱动 Unity MCP）

> **目标**：所有任务完成后，用 CC CLI 统一回归验收。

### 执行
1. `manage_editor play` 进 Play，`read_console` 确认零 Error。
2. 逐项复查任务一~五的截图与行为。
3. 回归：确认原功能（移动/吞噬/技能/卡牌/商店/图鉴/暂停）无回退。
4. 汇总证据到 `production/qa/evidence/task6-final/`。

---

## 九、分批执行策略（重要）

> 不建议六个任务一次性丢给 CC——任务二/三含**设计决策**（造型长什么样、召唤数值），CC 无法替你定，自由发挥必返工。分三批：

### 批次 A（已细化到可执行粒度，可一次性给 CC）——确定性改动，无设计争议
1. **任务一（器官重新分类）**：逐文件/方法/参考行号已给出。
2. **任务四（UI 重设计）**：迁移目标/复用方法/改动点已给出。
3. **任务五（Tooltip）**：单文件单方法，改动点已给出。

**给 CC 的批次 A 一次性指令（复制即用）：**
```
项目根：F:\Project\BinGames\TEngine\UnityProject
按设计规格书一次性完成任务一、任务四、任务五，全部改完再统一验收，中途不要停下问我：
- 任务一 器官重新分类：严格按"三、任务一"4 个文件改动点。
- 任务四 UI 重设计：按"六、任务四"建 BattleSandboxUIToolkit + UXML，迁移沙盒/木桩。
- 任务五 Tooltip：按"七、任务五"给 AddCodexRow 注册 hover。
约束：1) 遵守 CLAUDE.md（tengine-dev skill、证据留证）；2) 每改完一文件 read 确认无编译错误；3) 全部改完 manage_editor play + read_console 零 Error 再逐项 manage_scene screenshot；4) 截图进 production/qa/evidence/ 对应目录；5) 任一"看得到/验得出/不破坏"不达标就修到达标。
```

### 批次 B（需先定设计，再给 CC）——含设计决策
4. **任务二（3D 差异化）**：先定 24 个基元/器官/基因的造型方案（几何形状/朝向/挂件），再让 CC 实现。
5. **任务三（召唤）**：先定召唤物种类/数值/触发方式，再让 CC 实现。

### 批次 C（最后）
6. **任务六（整体验收）**：A、B 全完成后统一回归。

---

## 十、给 Claude Code CLI 的执行须知

- 改文件前先读目标文件，确认行号/上下文未漂移（本规格书行号为参考，以实际为准）。
- 每完成一个任务立即截图验收，不达标不进入下一项。
- 编译错误必须先清零再验收。
- 所有截图进 `production/qa/evidence/`，命名 `<任务号>-<场景>.png`。
- 严格遵守 `CLAUDE.md`：任务分级（L1-L4）、使用 `tengine-dev` skill、证据留证规则。
