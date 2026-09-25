# 游戏 UI 生产规范（当前工程）

## 1. 先盘点功能，再画页面

1. 从玩法代码列出玩家的状态、信息、选择、确认、失败原因和入口。
2. 写入 `UI_FUNCTION_COVERAGE.md`：每一行必须能追到一个领域模块或公开方法。
3. 把开发工具、未实现玩法、仅记录但尚未生效的字段标注为非正式入口，不能作为可点击的玩家功能。
4. 多条目内容默认使用 `ScrollView`；一行只放短摘要，详情在同一面板展开或进入详情页。

## 2. 页面职责与导航

| 页面 | 唯一职责 | 打开方式 |
| --- | --- | --- |
| 运行中枢 | 开局、跨玩法导航、显示设置、阶段路线 | 启动后默认显示 |
| 战斗 HUD | 局内状态、阶段、技能、环境反馈 | 运行中常驻 |
| 覆盖面板 | 卡组、商店、图鉴、暂停 | 运行中枢或快捷键 |
| 表型工坊 | 蓝图、模板版本、回巢改造 | 运行中枢“表型工坊” |
| 器官与基因 | 载体和基因装配 | 运行中枢“器官与基因” |
| 萌生腔 | 用模板生成新单位 | 运行中枢或工坊模板页 |
| 结算 | 本局回顾、重开、返回中枢 | 局结束时自动显示 |

一个按钮只做一件事。跨页面跳转必须先关闭当前模态；新页面若夺取输入，使用 `InputRouter.SetModalUi` 作为唯一输入所有权。

## 3. 资源拆分与命名

- 一张页面一份 `*.uxml`，一份同职责 `*.uss`，一份 `*UIToolkit.cs` 控制器。
- 在同一个 YooAsset 收集目录下，所有文件必须使用唯一文件基名。例如 `UiDemo.uxml` 的样式应命名为 `UiDemoStyles.uss`，不能也叫 `UiDemo.uss`；否则会产生地址冲突并阻止启动。
- 结构写在 UXML，视觉写在 USS，领域行为写在 C#。UXML 不写内联样式；新 USS 不使用 `gap`、`z-index` 或阴影等 UI Toolkit 不稳定属性。
- 目录约定：资源放 `Assets/GameRes/Raw/UI/<Feature>/`，控制器放 `Assets/GameScripts/HotFix/GameLogic/UI/<Feature>/`，设计/覆盖文档放 `Docs/UI/`。

## 4. 接线规则

- UI 只能调用领域模块的公开入口：例如模板提交走 `LineageRegistry.CommitTemplate`，萌生走 `GerminationChamberRegistry.Enqueue`，回巢走 `HomecomingRetrofitService`。
- UI 不重写校验、扣费或状态机；失败文案应显示领域模块返回的原因。
- 运行中控制器加载资源时复用 `BattleHudPanelSettings`，并用明确的 `sortingOrder`：HUD 0、战略命令条 1（ER5-CMD-01，归还谷地/破碎都市共用，常驻底部）、运行中枢 2、装配 4（旧细胞阶段面板）、家园装配站生产面板 6（ER4-FAC-01，与"装配 4"是两套不同系统，勿混用）、萌生 7、远征准备面板 8（ER5-EXP-01）、蓝图编辑器 8（ER4-BLP-01，与远征准备不会同屏）、合成台 9（ER4-PRIM-04）、覆盖面板 10（含任务日志/战役地图、信标启动确认）、表型工坊 11、结算 12；当前目标条 3（右上角速度 HUD 正下方，常驻、不拦截点击）；被点击/拖动的浮动窗口经 `UiWindowFocus.BringToFront` 提到浮动层 33～30000；字幕条 `UiWindowFocus.CaptionSortingOrder`（30050，浮动层之上，任何窗口都盖不住）；家园失败面板/胜利页 `UiWindowFocus.ModalSortingOrder`（30100，全屏阻断式模态，必须盖过任何 HUD/面板/浮动窗口；此前固定 20，被点到前面的窗口会盖在失败页上）。
- 新面板默认隐藏，且在阶段不运行时自动隐藏。开发 HUD 不能默认覆盖正式 UI。

## 5. 每次改 UI 的验收顺序

1. 在 Unity 中刷新全部资源并请求脚本编译。
2. 控制台必须不存在新增的 UXML、USS、C#、YooAsset 地址错误。
3. 在 `manage_ui list` 中确认新增 UXML、USS 和 PanelSettings 关系可识别。
4. 进入播放模式，至少预览开局页与新增面板的空状态；检查可读性、遮挡、按钮状态和滚动区域。
5. 对会写玩法状态的按钮，逐一验证成功、失败和关闭页面后的输入恢复。
6. 删除所有 `Assets/Temp*`、`Temp/UiPreview` 等验证产物，避免把截图打进包体。

## 6. Figma 与美术资产的边界

Figma 用于页面结构、组件标注、评审和拆图清单；Unity UXML/USS 才是可运行实现。当前 Demo 阶段优先文字、留白、边界和状态色，不依赖复杂生图切片。进入美术制作后，再将已审批的图标/背景以唯一名称导入 `Art/UI`，在 Unity 中设置导入参数、Sprite 切片和 Atlas。
- 战术指挥/编队详情面板（旧细胞阶段 `TacticalCommandUIToolkit`）已于 2026-09-23 删除；《地球归还》单位指挥只走战略命令条（`RegionCommandBarUIToolkit`）。
