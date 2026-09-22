# uGUI 布局与 Canvas 缩放（旧链：登录/启动/热更/主菜单）

> **适用场景**：`UnityEngine.UI`（Canvas/RectTransform/LayoutGroup）布局排版、多分辨率适配；本项目共享根 Canvas 的配置边界。不适用于 UI Toolkit 新面板（见 [ui-toolkit.md](ui-toolkit.md)）。此文件讲**布局/缩放**，UIWindow 生命周期 API 见 [ui-lifecycle.md](ui-lifecycle.md)。

---

## 一、LayoutGroup 陷阱：加了组件不等于生效

`HorizontalLayoutGroup`/`VerticalLayoutGroup` 只在 **`Control Child Size Width/Height`** 打开时才会真正接管子节点的位置和尺寸。这两个勾选默认是关的——组件加了但没勾，子节点会停在它们各自原始的 RectTransform 值上（工具生成/复制粘贴出来的子节点常常是默认值 `anchorMin=anchorMax=pivot=(0.5,0.5)`、`anchoredPosition=(0,0)`），也就是**全部叠在父节点正中心同一个点**。这是"两栏挤在一起""标签和控件叠在一起"的头号原因。

- 打开 `Control Child Width`/`Height` 后，子节点的最终尺寸由 `LayoutElement`（`minWidth`/`preferredWidth`/`flexibleWidth`）和 `childForceExpandWidth`/`Height` 共同决定。
- 常用配方：一行"标签 + 控件"（Toggle/Slider/Button）——标签 `flexibleWidth=1`（吃满剩余空间），控件保留自己的 `preferredWidth`/`minWidth`、`flexibleWidth=0`（紧凑宽度）；`childForceExpandWidth` 必须是 **false**（`true` 会让所有子节点无视各自 flexibleWidth 强行平分空间，等于白设置）。
- **行高同理**：行自己的 `LayoutElement.preferredHeight` 只有在父级 `VerticalLayoutGroup.childControlHeight=true` 时才生效，否则行会停在 Prefab 里原始的 `sizeDelta.y`（可能是远大于内容实际需要的旧值），表现为"每一行拉得很高，内容却挤在一角"。
- **`childForceExpandHeight=true` 会把行内的按钮/Toggle 也拉伸到跟整行一样高，四周贴边**：即使控件自己的 `LayoutElement.preferredHeight` 明确写了一个更小的数（例如按钮 34、行高 60），只要行的 `HorizontalLayoutGroup.childForceExpandHeight=true`，控件还是会被强制拉伸到吃满整行高度——文字元素靠 `Text.alignment` 能在超尺寸容器里居中看不出来，但按钮/Toggle 的可见背景图直接铺满整个控件的 RectTransform，会表现为"按钮贴着行的上下边缘、看起来比设计的尺寸大很多"。行内需要"控件比行矮一圈、四周都留白"时，把该行的 `childForceExpandHeight` 设成 **false**，控件会用自己的 `preferredHeight`，再配合 `childAlignment=MiddleLeft`/`MiddleCenter` 自动垂直居中、留出上下间隙。
- 排查方法：`PrefabUtility.LoadPrefabContents` 读出目标节点的 `HorizontalLayoutGroup`/`VerticalLayoutGroup` 的 `childControlWidth`/`childControlHeight`/`childForceExpandWidth` 和每个子节点的 `anchoredPosition`/实际 `rect.height`——多个子节点 `anchoredPosition` 相同就是叠在一起的直接证据，行高远大于其 `LayoutElement.preferredHeight` 就是 `childControlHeight` 没开的证据。

## 二、Button 内部 Label 陷阱

`Button` 的子 `Text`/TMP 标签必须 **锚点撑满父节点**（`anchorMin=(0,0)`、`anchorMax=(1,1)`、`offsetMin=offsetMax=Vector2.zero`），文字居中靠 `alignment=MiddleCenter`，不能靠标签自身的局部坐标去"摆放"。凡是标签保留了非零的 `anchoredPosition` 偏移（常见于复制粘贴模板忘记清零），不管这个 Button 之后被 LayoutGroup 挪到哪个位置，文字永远停在相对按钮中心偏移的老地方——这正是"确认/取消按钮文字飘在别处"的成因。

## 三、Canvas 缩放陷阱（本项目最容易踩、后果最大）

1. 全项目**共享一个根 Canvas**：场景里的 `UIRoot`（`Assets/TEngine/Settings/Prefab/UIRoot.prefab` 实例，框架资产，**只读禁改**）下的 "UICanvas"。每个 `UIWindow` 自己 Prefab 根节点上的 `Canvas` 组件都是**嵌套在这个根 Canvas 之下的子 Canvas**，不是独立的屏幕级根 Canvas——不要被"看起来像根节点"的 Prefab 结构误导。
2. **嵌套 Canvas 的 `renderMode` 赋值会被 Unity 转发到根 Canvas**：在某个 UIWindow 的脚本里给自己的（嵌套）Canvas 设置 `canvas.renderMode = ...`，实际改写的是全局共享根 Canvas，会波及场景里其它所有窗口。**禁止**在任何单个 UIWindow 里设置 `renderMode`。
3. **嵌套 Canvas 上的 `CanvasScaler` 完全不参与实际缩放计算**——Unity 只认根 Canvas 的 `CanvasScaler`。给某个 UIWindow 自己的 Canvas 加/改 `CanvasScaler` 是纯摆设，不会有任何视觉效果，也不会报错，容易让人误以为"设置生效了"。
4. 参考分辨率只能在**根 Canvas 上改一次**，且只能在启动流程里做一次，不能指望每个窗口各自"顺手"改一遍（改了也无效，见上条）。本项目正确值是 **1920×1080**（与 UI Toolkit 侧 `BattleHudPanelSettings` 一致），入口在 `GameApp.FixUiRootReferenceResolution()`（`GameApp.cs`，`StartGameLogic()` 里 `GameRoot.Startup()` 之后、任何 `GameModule.UI` 调用之前）。**禁止**依赖 `UIModule.UIRoot` 静态属性做这类早期修正——它要等 `UIModule` 单例真正 `OnInit()` 过一次才会被赋值，在模块首次被访问之前读它只会拿到 `null`；早期修正请直接 `GameObject.Find("UIRoot")`，和 `UIModule.OnInit()` 用同一种查法。
5. 验证一个面板是否会溢出画布，不要只看它自己的 `sizeDelta` 数值，要跟**根 Canvas 的 `RectTransform.rect.width`** 比：`面板.sizeDelta.x / 根Canvas.rect.width` 应该是一个 <1 的合理比例（例如 0.3～0.7），否则不管内部子元素排得多整齐，整个面板本身相对画布还是会显得"比画布大一圈"。

## 四、ScrollRect Content 陷阱

`ScrollRect.content` 如果没有水平拉伸锚点（`anchorMin=(0,1)`、`anchorMax=(1,1)`，而不是定点锚），它自己的 `RectTransform.rect.width` 就会停在 Prefab 里原始的默认值，不会跟着 `ScrollRect.viewport` 的实际宽度走。Content 上如果挂了 `HorizontalLayoutGroup` 排布"两栏"，该 LayoutGroup 会拿这个过小的、从未更新过的宽度当"可分配空间"，把子节点全部挤扁——两栏因此被压缩到远小于各自 `preferredWidth` 的实际宽度，右边一大截空间空着没用上（表现为"内容都靠左，右边空了一大块"）。垂直滚动场景下 Content 锚点应该是：`anchorMin=(0,1)`、`anchorMax=(1,1)`、`pivot=(0.5,1)`、`sizeDelta.x=0`（水平拉伸，只在垂直方向由 `ContentSizeFitter` 撑高）。

另外 `ScrollRect` 必须有真正的 `Viewport`（带 `RectMask2D`/`Mask` 的子节点，`ScrollRect.viewport` 指向它，`ScrollRect.content` 是这个 Viewport 的子节点）。`viewport` 留空虽然不报错（Unity 会退化用 ScrollRect 自己的 RectTransform 顶替），但没有真正的裁切遮罩，内容超出可视区域时既不会被正确裁掉、也不会真的"滚动"，会直接越界渲染到别的元素上——这正是"内容被越界裁剪"的成因。

## 五、Slider Fill/Handle 退化尺寸陷阱

`Slider.fillRect`/`Slider.handleRect` 如果锚点/尺寸配错（常见错误：定点锚 `anchorMin=anchorMax=(0,0)` 配 `sizeDelta=(N,0)`，高度锁死为 0），Slider 运行时驱动 `fillRect.anchorMax.x` 从 0 到 1 变化时会长成一个"尖峰"而不是正常的矩形填充条，Handle 同理会变成一条看不见的线。

**更深一层的坑（2026-09-21 连续踩了两次才摸清）**：Unity 的 `Slider.UpdateVisuals()` 对 Handle **每次刷新都会无条件把 anchorMin/anchorMax 的另一根轴强制设成 0~1（拉伸）**——不管 Prefab 里怎么配 Handle 自己的锚点，运行时都会被这个内置逻辑覆盖回拉伸状态。所以**不能指望靠改 Handle 自己的锚点/sizeDelta 让它变成一个定高的小方块/圆点**——改了也会在下一次刷新（比如 `SetValueWithoutNotify`）时被重置回拉伸，看起来改了但没生效或者叠加出更怪的尺寸（sizeDelta 会加在拉伸高度之上，越改越高）。

**正确做法**：不要跟 Handle 的锚点较劲，改它的**父容器**（"Handle Slide Area"，以及 Fill 对应的"Fill Area"）——把父容器本身的高度缩到想要的滑块高度（例如 `anchorMin=(0,0.5)`、`anchorMax=(1,0.5)`、`sizeDelta.y=20`，水平仍然拉伸铺满滑轨），Handle/Fill 拉伸到这个已经缩小的父容器里，自然就是矮的、不变形的。Handle 自己的 `sizeDelta.y` 这时候应该清 **0**（拉伸铺满父容器高度即可，不要再叠加额外尺寸）。

## 六、验证必须走真实点击路径，不能用 `SetActive` 绕过控制器

2026-09-21 修 `ScrollRect` 加 `Viewport` 那次，插入了新的层级节点却漏改 `ScriptGenerator()` 里的硬编码路径常量，导致 `FindChildComponent` 全部找空、`ScriptGenerator` 抛 `NullReferenceException`——但验证时用 `GameObject.SetActive(true)` 直接切视图、绕开了真正的 `UIWindow.InternalCreate -> ScriptGenerator` 流程，RectTransform 数值看起来完全正常，实际上窗口在真实冷启动时一开就崩，直到用户实测才发现。

**教训**：改了 Prefab 层级结构后，验证必须走真实入口——`btn.onClick.Invoke()`（模拟真实点击，走真正注册的委托）或完整重走 `ShowUIAsync`/`SetView` 流程，不能为了"省事绕开某个已知问题"就用 `SetActive` 之类的手段跳过控制器逻辑本身。**每次改完都要 `read_console` 检查一遍真的没有新增 Error/Exception**，不能只看 RectTransform 数值对不对——布局数值正确不代表脚本没崩溃，这两件事要分开验证。

## 七、验证前先排除"脏场景"假阳性

连续多轮编辑 C# 脚本触发的自动重编译，会强制退出当前 Play（Unity 编辑脚本自动重编译的副作用），如果这个退出发生在不巧的时机，场景里标记 `DontDestroyOnLoad` 的对象（例如 `UIRoot`）可能残留或被重复实例化，导致下一次 Play 看到的是**陈旧/重复**的对象而不是干净冷启动的结果——这会让明明已经修好的 bug看起来"改了却没生效"。修完代码要做真实验证前，先强制重载场景排除这个假阳性：

```csharp
var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scene.path, UnityEditor.SceneManagement.OpenSceneMode.Single);
```

再 Stop → Play，等待真实启动流程跑完（HybridCLR/YooAsset 加载需要几百毫秒到几秒的真实等待，不要在 `manage_editor play` 返回后立刻检查），再读取 RectTransform/Canvas 数据断言。

## 八、ScrollRect 鼠标滚轮失灵陷阱

用 `RectMask2D` 当 Viewport 的裁切方案（第四节推荐的做法）有一个副作用：`RectMask2D` 本身不需要、也不提供任何可被射线检测命中的图形。如果 Viewport 底下的内容（行背景等）大多把 `Image.raycastTarget` 设成 `false`（常见做法，为了不挡住按钮点击），那么鼠标悬停在内容区域滚动滚轮时，**根本没有任何东西能接住这个事件**——Unity 的滚轮事件要先命中一个可射线检测的图形，再从命中点往上冒泡去找 `ScrollRect`（它自己实现了 `IScrollHandler`）。表现是：拖动滚动条能用（滚动条自己的图形是可命中的），但鼠标悬停在内容上滚轮完全没反应。

**修法**：给 Viewport 加一个 `Image` 组件（颜色 alpha=0，完全透明，纯粹用来接鼠标事件），`raycastTarget=true`，和已有的 `RectMask2D` 共存（互不冲突，一个管裁切一个管命中）。

**验证方法**：不能只靠人工拿鼠标滚一下——用 `ExecuteEvents.ExecuteHierarchy(viewportGameObject, pointerEventData, ExecuteEvents.scrollHandler)` 模拟真实滚轮事件，检查 `ScrollRect.verticalNormalizedPosition` 滚动前后确实变了，这跟真实鼠标滚轮走的是同一条事件冒泡路径。

## 九、视觉美观检查清单（不影响对错，但直接决定好不好看）

这些坑不会报错、不会重叠、不会崩溃，纯数值断言（宽高/是否重叠）也测不出来，但会让界面显得很业余。每做完一类新控件，对照这份清单过一遍：

| 控件/场景 | 常见坑 | 正确做法 |
| --- | --- | --- |
| 文字（标签/描述） | `Text` 默认 `alignment=UpperLeft`，贴左上角 | 单行文字一律显式设 `MiddleLeft`（按需 `MiddleCenter`） |
| 一行内容（Label+控件） | `HorizontalLayoutGroup.padding` 默认 0，文字/控件贴着行的左右边缘 | 每行给 12~16px 左右内边距 |
| Toggle 复选框 | 内部 `Background`/`Checkmark` 图形常年是左上角定点锚（`anchorMin=anchorMax=(0,1)`），Toggle 外框一旦被 LayoutGroup 拉高，复选框图形就贴在顶部不跟着居中 | 内部可见图形改成垂直居中定点锚 `anchorMin=anchorMax=(x,0.5)`，不要指望它跟着外框被动拉伸 |
| Button 内部文字 | 同上一类问题，Label 局部坐标写死偏移（见第二节） | 撑满父节点，见第二节 |
| Slider 进度条填充 | `Image.type=Simple` 会把圆角胶囊形的填充条整体拉伸变形（圆角被压扁/拉成椭圆），数值较小、填充条较窄时尤其明显 | sprite 自带 9-slice border（例如 Unity 内置 `UISprite`）时，`Image.type` 必须是 `Sliced` 不能是 `Simple`，边框保持原比例，只拉伸中间 |
| Slider 滑块/填充条高度 | 见第五节，不能直接改 Handle 自己的锚点/尺寸 | 缩小父容器（Handle Slide Area/Fill Area）高度 |
| 看起来是一整块却内部空荡的行/卡片 | 高度被 `LayoutElement.preferredHeight` 设得比内容实际需要的大很多 | 量一下 preferredHeight 是否真的等于内容需要的高度，不要为了留白好看随手抄一个偏大的数字 |

**通用方法论**：这类丑但不报错的坑，光看 `rect.width`/`rect.height` 数值往往看不出来（数值本身可能是对的，比如 Toggle 外框高度确实等于行高，但图形没跟着居中）。要用 `GetWorldCorners()` 算真实的居中偏差（例如 `Mathf.Abs(容器中点Y - 内容中点Y)` 应接近 0），或者直接截图人工看一眼——纯数值断言只能证明没有重叠/没有溢出，证明不了好不好看。

## 十、交叉引用

| 主题 | 文档 |
| --- | --- |
| UIWindow 生命周期/UIWidget API | [ui-lifecycle.md](ui-lifecycle.md) |
| UI Toolkit 新面板 | [ui-toolkit.md](ui-toolkit.md) |
