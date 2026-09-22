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
- 一个宿主 GameObject 只挂一个 `UIDocument`；多面板复用同一个 `PanelSettings`，用 `sortingOrder` 分层：HUD 0、运行中枢 2、装配 4、战术 5、萌生 7、覆盖面板 10、表型工坊 11、结算 12（见 `UI_WORKFLOW_GUIDE.md` 第4节）。

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

## 五、验收

按 `UI_PIPELINE.md` 第6节验收闸门（功能/数据态/16:9 等分辨率/可读性/输入/性能/Unity 导入无报错）。不要用反复进 Play 截图代替验收——本仓库默认用 `execute_code`/反射读 `resolvedStyle` 或 Unity Test Runner 断言（见仓库根 `CLAUDE.md`「验收」节）。

## 六、交叉引用

| 主题 | 文档 |
| --- | --- |
| 旧 uGUI/UIWindow 生命周期 | [ui-lifecycle.md](ui-lifecycle.md) |
| 完整 SOP（立项卡/Figma/变更流程） | `TEngine/UnityProject/Docs/UI/UI_PIPELINE.md` |
| 页面职责/sortingOrder/接线规则 | `TEngine/UnityProject/Docs/UI/UI_WORKFLOW_GUIDE.md` |
