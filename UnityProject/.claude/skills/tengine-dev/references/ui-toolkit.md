# UI Toolkit 面板开发（战斗/经营新面板）

> **适用场景**：UIDocument/VisualElement/UXML/USS 新面板（战斗 HUD、抽卡、构筑、商店、图鉴、结算等）；不适用于登录/启动/热更等既有 uGUI Canvas 链（见 [ui-lifecycle.md](ui-lifecycle.md)）| **关联文档**：`TEngine/UnityProject/Docs/UI/UI_PIPELINE.md`（生产合同，权威）、`UI_WORKFLOW_GUIDE.md`（页面职责/sortingOrder 表）——本文件只是压缩索引，实现前必须读这两份原文，不能只看这里。

---

## 一、先判断技术边界

新面板 = UI Toolkit；旧 Canvas 链（登录/启动/热更）= uGUI，两者不混用，不把 UI Toolkit 根节点塞进 `UIWindow` 的 Canvas Prefab。落点表见 `UI_PIPELINE.md` 第2节。拿不准先读那份文件，不要凭经验猜。

## 二、文件组织（不可省略 UXML/USS）

```
Assets/GameRes/Raw/UI/<Feature>/
  <Feature>Screen.uxml
  <Feature>Screen.uss
  templates/<Component>.uxml
Assets/GameScripts/HotFix/GameLogic/UI/<Feature>/
  <Feature>UIToolkit.cs
  <Feature>Presenter.cs   （仅页面复杂到需要拆分时）
```

- UXML 节点 `name` 用 `camelCase`；USS 类用 `kebab-case`；改名必须同步改绑定代码。
- 不在 UXML 写 `style` 属性；定位/尺寸/颜色/状态一律走 USS 类。
- `AddToClassList`/`RemoveFromClassList` 表示视觉状态；禁止 `element.style.*` 写死布局数值（仅数据驱动的运行时数值，如血条宽度百分比，允许直接写 `style`）。
- 一个宿主 GameObject 只挂一个 `UIDocument`；多面板复用同一个 `PanelSettings`，用 `sortingOrder` 分层（完整表见 `UI_WORKFLOW_GUIDE.md` 第4节，以那里为准）。

## 三、列表与滚动

| 内容数量 | 默认实现 |
| --- | --- |
| 1–5 个、需同时比较 | 固定横排/网格 |
| 多于 5 个或不确定 | `ScrollView`；数据量大用虚拟化 `ListView` |
| 单次抉择 2–4 个选项 | 同屏卡片，不滚动 |

## 四、常见错误（已实锤的真实事故，2026-09-21 EconomyHudToolkit/StrategyClockHudToolkit）

| 错误 | 后果 | 正确做法 |
| --- | --- | --- |
| 全代码 `new VisualElement()` 手搭整页树，不建 UXML | 结构/样式/数据混在一起，改一处炸全页 | 结构进 UXML，样式进 USS，C# 只做绑定 |
| `style.position = Position.Absolute` + 硬编码 `top/left` | 分辨率一换就错位、重叠 | 用 USS 类 + Flex 布局 |
| 同一 GameObject 挂第二个 `UIDocument` | `AddComponent<UIDocument>` 第二次返回 null → NRE | 复用宿主已有 `UIDocument`/`PanelSettings` |
| 用魔法数字 sortingOrder 手动"避让"重叠面板 | 面板一多就顺序打架 | 按 `UI_WORKFLOW_GUIDE.md` 分层表走 |
| 内联 `style` 写在 UXML 里 | 视觉散落，无法复用 | 样式一律进 USS |

## 五、窗口布局配方（2026-09-23 蓝图编辑器重做沉淀）

参考坐标：`BattleHudPanelSettings` = ScaleWithScreenSize 1920×1080、match 0.5。16:9 下面板坐标恒为 1920×1080；超宽屏宽变大高变小（2560×1080 → 2194×926），5:4 反之（1280×1024 → 1585×1268）。

**骨架**：窗口 = 页眉（标题 + 状态 + 关闭）/ 主体（可多栏，各栏自己滚动）/ 页脚（撤销、反馈、主按钮）。关闭与保存永远不放进滚动区。

**《地球归还》窗口直接套共享样式 `Raw/UI/Common/MachineWindow.uss`**，不要再抄一份按钮/字段样式：UXML 先写 `<Style src="../Common/MachineWindow.uss" />` 再引本页 USS；外观用 `mw-window` `mw-header` `mw-title` `mw-status` `mw-body` `mw-footer` `mw-section-title` `mw-hint` `mw-btn` `mw-btn-primary` `mw-btn-danger` `mw-close-btn` `mw-entry-btn` `mw-field`（已含下面的表单覆盖）；本页 USS 只写位置、栏宽和专属控件。范例：`Raw/UI/CircuitBoard/`（三栏编辑器）、`Raw/UI/PrimitiveCraft/`（单栏表单）。下面的片段说明布局原理：

```css
.x-root   { position: absolute; left: 24px; top: 72px; bottom: 96px; width: 1240px; flex-direction: column; }
/* 高度用 top+bottom 锚定，不写死 height/max-height：矮屏自动压缩，由栏内 ScrollView 吸收 */
.x-header { flex-direction: row; align-items: center; flex-shrink: 0; height: 48px; }
.x-title  { flex-shrink: 0; }                     /* 否则会被旁边 flex-grow 的状态文字挤窄 */
.x-body   { flex-direction: row; flex-grow: 1; flex-shrink: 1; min-height: 0; }
.x-col-fixed { width: 220px; flex-shrink: 0; }
.x-col-flex  { flex-grow: 1; flex-shrink: 1; min-width: 0; }   /* min-width:0 是收缩的前提 */
.x-footer { flex-direction: row; align-items: center; flex-shrink: 0; height: 56px; }
```

**表单字段（DropdownField/TextField）必须覆盖默认主题**——Unity 默认给带 label 的字段设 `min-width:150px` 的标签，"底盘"两个字也占 150px，把控件挤出栏外：

```css
.x-field { min-width: 0; flex-shrink: 1; }
.x-field > .unity-base-field__label { min-width: 56px; width: 56px; }
.x-field > .unity-base-field__input { min-width: 0; flex-shrink: 1; }
.x-field .unity-base-popup-field__text { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
```

**等分按钮行**：`flex-grow: 1; flex-basis: 0; min-width: 0; margin: 0 2px;` + 容器 `margin: 0 -2px`（USS 没有 `gap`/`:last-child`）。两列网格：`flex-wrap: wrap` + 子项 `width: 48%; margin-right: 2%`（49%+2% 会溢出换行）。

**浮动窗口的根节点不要 `picking-mode="Ignore"`**：背景上的点击和滚轮会穿透到战场。只有全屏定位层/透明容器才设 Ignore。

## 六、控件与 USS 陷阱（都是实测过的，Unity 会静默吞掉错误，不报错）

| 陷阱 | 症状 | 正确做法 |
| --- | --- | --- |
| C# 切换 `is-hidden`/`xx-hidden`，但本面板 USS 没定义这个类 | "关闭"点了没反应、面板从开局就常驻（战术指挥事故） | 隐藏类必须在**本面板自己的** USS 里定义 `display: none`；验收读 `resolvedStyle.display` |
| `-unity-text-overflow: ellipsis` | 属性名不存在被忽略，文字硬截断无省略号 | 标准名 `text-overflow: ellipsis` + `overflow: hidden` + `white-space: nowrap` |
| `:last-child` `:first-child` `:nth-child` `:not()` `gap` `z-index` `display:grid` `box-shadow` | 整条规则/属性静默失效 | 负外边距、显式类名、元素顺序/`BringToFront` |
| DropdownField 选项含 `/` | 弹出菜单把一项拆成两级子菜单 | 换全角 `／`；统一走 `GameLogic.UI.Common.DropdownChoices.Apply` |
| 选项含 ` #xx` ` %x` ` &x` | 被当菜单快捷键**剥掉**："聚焦镜（补印 #b76e）"只显示"聚焦镜（补印" | 同上，`Apply` 会换成全角 |
| 两个选项文字相同 | `DropdownField.index` 是 `choices.IndexOf(value)`，永远只能选中第一项 | 选项必须唯一，实例带短 ID 后缀（`DropdownChoices.ShortId` 取 GUID **末** 4 位，前缀都一样） |
| 空选项列表留空白框 | 玩家看不出是"没有"还是"坏了" | `Apply(dd, choices, "仓内没有芯片")` 显示占位并禁用；对应按钮同步 `SetEnabled(false)` |
| 定时刷新（如每 0.2s）里 `SetValueWithoutNotify` 回写当前值，又要求"选好再点设置按钮" | 玩家选的值等不到点按钮就被覆盖 | 下拉框选中即生效（`RegisterValueChangedCallback`），不做两步式 |
| 把状态枚举 `ToString()` 给玩家看 | 界面出现 `WaitingResources` | 写中文映射函数 |
| 世界输入（相机滚轮、点选）"鼠标在不在 UI 上"只认登记过的窗口，或用屏幕像素比 `worldBound` | 鼠标在面板上滚轮却缩放相机；非 1080p 分辨率下判定错位 | 已统一在 `UiWindowFocus`：对所有 UIDocument 面板 `panel.Pick` + 沿祖先找实体控件/有背景窗口；坐标一律 `RuntimePanelUtils.ScreenToPanel` |
| 刷新顺序：先刷网格高亮、后算校验 | 高亮永远滞后一次刷新 | 先算派生数据（校验/预览），再刷所有视图 |

## 七、验收

按 `UI_PIPELINE.md` 第6节验收闸门（功能/数据态/16:9 等分辨率/可读性/输入/性能/Unity 导入无报错）。不要用反复进 Play 截图代替验收——本仓库默认用 `execute_code`/反射读 `resolvedStyle` 或 Unity Test Runner 断言（见仓库根 `CLAUDE.md`「验收」节）。

**第一步永远是布局探针**（`Assets/Editor/QA/UiToolkitLayoutProbe.cs`，免 Play，一次调用）：

```csharp
// UnityMCP execute_code（Roslyn，禁止 using，写全名）
return BinGames.EditorTools.UiToolkitLayoutProbe.Probe(
    "Assets/GameRes/Raw/UI/<Feature>/<Feature>Panel.uxml", "<PanelRootName>");
```

它会：①体检同目录 USS（上表里的静默失效写法、UXML 用了但没定义的 `*hidden` 类）；②移除 `*hidden` 类、往所有下拉/文本框塞超长中英混排文字；③在 1920×1080 / 1280×720 / 2560×1080 / 1280×1024 四种分辨率下报告"超出屏幕 / 越界（ScrollView 内容除外）/ 文字被硬截断 / 塌成 0 宽"。**PASS 之前不要进 Play。**

**需要看真实画面时**（仅在布局探针 PASS 后、验收明确要求视觉时）：
- 编辑模式下运行时面板**不出帧**（离屏 RenderTexture 也是空的），必须进 Play。
- UnityMCP `manage_camera screenshot` 走相机渲染，**不含 UI Toolkit 覆盖层**，截出来只有场景。用 `execute_code` 调 `UnityEngine.ScreenCapture.CaptureScreenshot("Temp/UiProbe/shot.png")`，下一次工具调用时再 `Read` 该文件（帧末才写盘）；截图只写 `Temp/`，禁止写 `Assets/Screenshots/`。
- 进 ProjectA 归还谷地不必点主菜单：`CampaignState.CreateNew(guid,"Standard",seed)` → `MachineRegistry.ResetForNewCampaign()` → `CampaignSession.Set(99, state)`（99 = 非玩家槽位）→ `GameApp.MountGameplayUi()` → `GameRoot.StartHomeValley()`；面板开关走 `GameRoot.HomeValley.SetXxxPanelOpen(true)`。结束后删掉 `persistentDataPath/Campaigns/campaign_slot99.json(.bak)`，玩家槽位 0–2 不碰。
- **Play 中改了 C# 会触发域重载**，所有静态状态（`GameRoot.HomeValley` 等）变 null，之前的会话状态全部作废：先停 Play、`refresh_unity` 编译、重开场景再进。
- 按钮行为走真实回调：`btn.clickable` 反射调 `Invoke(EventBase)`，不要直接调私有方法绕过 UI。
- 滚轮/点击是否被 UI 拦截：反射调 `UiWindowFocus.IsPointerOverPickableUi(new Vector2(x, y))`（屏幕左上原点），面板背景、面板内空白、战场空地、面板关闭后同一点都要测。

## 八、交叉引用

| 主题 | 文档 |
| --- | --- |
| 布局探针源码 | `Assets/Editor/QA/UiToolkitLayoutProbe.cs`（菜单 Tools/BinGames/UI Toolkit 布局探针） |
| 下拉框填充工具 | `Assets/GameScripts/HotFix/GameLogic/UI/Common/DropdownChoices.cs` |
| 世界输入 UI 拦截 | `Assets/GameScripts/HotFix/GameLogic/UI/Common/PanelDragManipulator.cs`（`UiWindowFocus`） |
| 旧 uGUI/UIWindow 生命周期 | [ui-lifecycle.md](ui-lifecycle.md) |
| 完整 SOP（立项卡/Figma/变更流程） | `TEngine/UnityProject/Docs/UI/UI_PIPELINE.md` |
| 页面职责/sortingOrder/接线规则 | `TEngine/UnityProject/Docs/UI/UI_WORKFLOW_GUIDE.md` |
