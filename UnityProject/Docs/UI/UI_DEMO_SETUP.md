# UI Demo 验证步骤

## 产物

- `Assets/GameRes/Raw/UI/UiDemo/UiDemo.uxml`：页面结构。
- `Assets/GameRes/Raw/UI/UiDemo/UiDemo.uss`：低装饰、高可读性视觉规范。
- `Assets/GameScripts/HotFix/GameLogic/UI/UiDemo/UiDemoController.cs`：滚动列表、选择、购买确认、导航与反馈的交互验证。
- `Assets/GameRes/Art/UI/Demo/ui_demo_reference_v1.png`：生图生成的视觉方向参考；不用于烘焙文字或交互。

## 在 Unity 中运行

1. 打开一个仅用于验证的场景，新建空物体 `UiDemo`。
2. 添加 `UIDocument` 和 `UiDemoController` 组件。
3. 为 `UIDocument` 指定项目现有的 `BattleHudPanelSettings`，并把 `UiDemo.uxml` 指定为 `Visual Tree Asset`。
4. 聚焦 Unity Editor，等待 UXML、USS、C# 和图片全部重导入；先处理 Console 中任何报错。
5. 进入 Play Mode，验证：
   - 商店列表可滚动，文本不重叠也不缩小。
   - 点击商品会更新详情和购买按钮；资源不足时按钮禁用。
   - 购买必须经过确认弹窗，成功后资源和商品状态同步变化。
   - 顶栏导航复用同一内容面板；技能始终位于底部固定位置。
   - 分别检查 16:9、16:10、21:9 与目标移动分辨率。

## 与 Figma 的验证状态

已创建 Figma 文件：<https://www.figma.com/design/b34enECqDqLwXBgPLObpdy>。

已验证 Figma 新文件创建、库发现、画布读取和变量 API；Starter 方案随后触发 MCP 调用限额，故 token、组件和页面的 Figma 写入需要额度恢复后继续。Unity 侧不依赖该限额，可先完成本 Demo 的可读性和操作验证。
