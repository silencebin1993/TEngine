# 游戏 UI 生产流水线

> 适用范围：本项目运行时 UI。新战斗界面使用 Unity UI Toolkit；启动、登录和热更新等既有 Canvas 界面只做维护，除非明确立项迁移。本文是设计、美术、策划和程序的共同交付合同。

## 1. 成功标准

每一个页面都必须同时满足以下条件：

- **看得懂**：正文不小于 14 px；战斗高频数值不小于 18 px；文字与背景保持足够对比，不能依赖背景恰好较暗才可读。
- **点得到**：触控主按钮热区不小于 44 x 44 px；桌面端同样保留明确 hover、按下、禁用与键盘提示状态。
- **找得到**：一个页面只允许一个视觉主行动；返回、关闭和取消的位置固定；同一类物品的查看、选择和装备操作不改变位置。
- **装得下**：可变数量的内容一律使用 `ScrollView` 或虚拟化列表；固定数量才可以做固定槽位。不能用缩小字号或叠放卡片解决溢出。
- **不挡玩法**：战斗 HUD 只保留扫视信息；构筑、商店、图鉴、暂停等信息密集内容使用模态层并夺取输入；关闭时恢复游戏输入。
- **可维护**：显示结构在 UXML，样式在 USS，数据和事件在 C#。禁止把业务判断散入 USS 或把视觉数值散入 C#。

## 2. 项目内技术边界

| 范围 | 规范 | 当前落点 |
| --- | --- | --- |
| 战斗 HUD、抽卡、构筑、商店、图鉴、结算 | Unity UI Toolkit | `Assets/GameRes/Raw/UI/BattleUI/` 和对应 `Assets/GameScripts/HotFix/GameLogic/UI/` |
| 启动、登录、更新 | 既有 uGUI Canvas Prefab | `Assets/GameRes/Raw/UI/*.prefab`、`Assets/Launcher/Resources/UIWindow/` |
| 运行时页面排序 | 单一 `PanelSettings` 下以明确 `sortingOrder` 分层 | `BattleHudPanelSettings`，覆盖层当前为 10 |
| 战斗模态输入 | 打开时 `InputRouter.SetModalUi(true)`，关闭时恢复 | `BattleOverlayUIToolkit.SetPanel` |
| 旧窗口生命周期 | 只走 `UIModule` / `UIWindow` 的加载、显隐和销毁 | `Assets/GameScripts/HotFix/GameLogic/Module/UIModule/` |

不要将 UI Toolkit 根节点塞进 `UIWindow` 的 Canvas Prefab，也不要让新页面同时由二者控制。混用的唯一允许场景是同一局游戏中并存的旧启动链与新战斗链，它们必须互不持有对方节点。

## 3. 页面设计 SOP

### 3.1 立项卡（设计开始前）

每个新页面先创建一张页面卡，缺一项不进入视觉稿：

```text
页面：
玩家目标：玩家进入后要完成的唯一主要任务。
入口：从哪里打开；是否可被快捷键打开。
退出：关闭、返回、Esc、点击遮罩分别做什么。
数据：读哪些模型；哪些字段会实时变化。
主行动：唯一的成功操作。
可变内容：列表数据源、空态、加载态、错误态、上限与排序。
阻断性：是否暂停玩法；是否必须夺取输入；能否与其他面板共存。
埋点：打开、曝光、主行动、取消、错误的事件名。
验收：需要覆盖的语言、分辨率、输入方式和极端数据。
```

### 3.2 先交互，后视觉

1. 在 Figma 先画灰阶线框和页面流，标明入口、返回、关闭、空态、加载态、报错和确认行为。
2. 由策划确认主路径后，再应用本项目 token 和组件库。视觉稿不能引入未批准的交互。
3. 高保真稿必须包含默认、hover、按下、禁用、选中、长文、空列表和溢出七种状态。
4. 程序按 UXML/USS 重建，不使用截图当整页背景；图片只承担插画、图标、材质或九宫格边框。
5. 进入 Unity 后由设计在真实数据、真实分辨率和真实输入下验收，验收的是体验，不是静态截图。

## 4. Figma 规范

Figma 是推荐的设计源，而不是 Unity 的自动导入源。当前工作流采用“Figma 做规格，Unity 重建结构”的方式；不要期待 Figma 文件自动生成可运行 UI。

### 文件结构

```text
00_Cover
01_Foundations        色彩、字号、间距、圆角、阴影、动效
02_Components         Button / Chip / Card / Modal / ListItem / Tooltip
03_Flows              页面流、线框和交互说明
04_Screens             每个实际页面及其全状态
05_Handoff             导出切图、标注、版本记录
```

### 命名与映射

| Figma 名称 | Unity 映射 | 示例 |
| --- | --- | --- |
| `Button/Primary/L` | USS 类 | `.btn-primary` + `.btn-lg` |
| `Card/Item/Default` | UXML Template | `templates/ItemCard.uxml` |
| `Overlay/Confirm` | UXML 根节点 | `ConfirmDialog` |
| `Icon/Shop/24` | 单个 SVG 或透明 PNG | `icon_shop_24.svg` |
| `Art/Panel/9Slice` | Sprite，九宫格配置 | `panel_bio_9slice.png` |

Figma 变量名与 USS token 同名，例如 `color/text/primary` 对应 `--text`，`space/16` 对应 `--space-16`。任何新 token 必须先在 Figma Foundations 和共享 USS 中同时登记，不能仅在单页创建临时颜色。

### 切图规则

- 图标优先 SVG；无法用矢量表达的图标使用透明 PNG，按展示尺寸的 2 倍导出。
- 面板和按钮边框使用九宫格图；不要导出一张整页背景来“保真”。
- 每个文件只表达一个用途，命名使用小写 `snake_case`，例如 `icon_mutagen_24.svg`。
- 导入 Unity 后设置正确的 Sprite 类型、PPU、压缩与九宫格边界；同类图标打入同一个 Sprite Atlas。
- UI 原图、导出图和 Unity 引用必须可追溯：在 `05_Handoff` 记录 Figma 节点链接、导出文件名和目标 Unity 路径。

## 5. UI Toolkit 实现规范

### 文件组织

```text
Assets/GameRes/Raw/UI/<Feature>/
  <Feature>Screen.uxml
  <Feature>Screen.uss
  templates/
    <Component>.uxml
Assets/GameScripts/HotFix/GameLogic/UI/<Feature>/
  <Feature>UIToolkit.cs
  <Feature>Presenter.cs              （仅页面复杂到需要拆分时）
```

- UXML 节点 `name` 使用 `camelCase`；USS 类使用 `kebab-case`；名称是 C# 查询契约，改名必须同步改绑定代码。
- 不在 UXML 写 `style` 属性。定位、尺寸、颜色、可见性与状态都定义在 USS 类中。
- 通用样式优先补到共享 `BattleUI.uss`，页面私有样式留在功能页面的 USS。禁止复制粘贴一整段按钮、滚动条或卡片样式。
- `Button` 的点击、`TextField` 的变更和列表元素的绑定，在控制器 `CacheNodes` / `BindEvents` 中集中注册；销毁时对称清理。
- 只通过 `AddToClassList` / `RemoveFromClassList` 表示视觉状态。不要用 `element.style.*` 写临时视觉数值。
- 无交互的全屏根节点必须 `PickingMode.Ignore`；显示模态窗时，只有遮罩和可操作面板可以接收点击。
- 模态层必须只有一个显隐入口。该入口负责显示、输入锁定、暂停状态、焦点和关闭后的恢复，禁止每个按钮各自修改其中一部分。

### 列表与滚动规则

| 内容数量 | 默认实现 |
| --- | --- |
| 1–5 个、玩家需要同时比较 | 固定横排或网格 |
| 多于 5 个或数量不确定 | `ScrollView`；若数据量大，使用虚拟化 `ListView` |
| 可筛选、搜索、排序的收藏内容 | 搜索栏 + 筛选/排序 + 可滚动列表 + 空态 |
| 单次抉择 2–4 个选项 | 同屏卡片；不使用滚动 |
| 单行会超过容器高度的说明 | 自适应换行；全文放详情或 tooltip，不截断关键数值 |

## 6. 验收闸门

页面满足以下条件才可以标记完成：

1. **功能**：每个入口、主行动、取消、Esc、遮罩、返回和快捷键均符合页面卡定义。
2. **数据**：加载、空、单项、满列表、超长名称、最大数值、不可购买/不可用均已验证。
3. **布局**：16:9、16:10、21:9 及目标移动分辨率不裁切；安全区内关键操作不被遮挡。
4. **可读性**：战斗中可在短暂扫视内读取生命、资源、可行动作与危险提示；正文没有低于 14 px 的关键信息。
5. **输入**：鼠标、键盘、手柄或触控（按目标平台）均没有双重触发、穿透或死锁。
6. **性能**：没有每帧重建整个列表或重复注册事件；图标使用图集；页面关闭后不保留无效监听。
7. **Unity 导入**：聚焦 Unity Editor 让资源完成重导入，Console 无 UXML/USS 解析错误或警告。

## 7. 变更流程

任何 UI 改动按以下方式提出和落地：

1. 用“页面 + 玩家目标 + 要改的状态 + 验收结果”描述需求，例如：`商店：让玩家更快比较 6 个商品；价格不足时按钮禁用并说明缺口。`
2. 先更新页面卡与 Figma 组件/页面状态，确认不会改变其他页面的共用行为。
3. 同步变更 Unity 的 UXML、USS 和绑定代码；涉及公共组件时，列出受影响页面并回归测试。
4. 由设计在目标分辨率实际操作验收；发现问题按严重度记录为阻断、主要、一般，不以“代码能跑”替代验收。

