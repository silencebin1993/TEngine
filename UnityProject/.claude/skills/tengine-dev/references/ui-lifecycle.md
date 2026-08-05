# UI 生命周期与核心 sPI

> **适用场景**：UIWindow/UIWidget 生命周期、层级（UILayer）、ShowUI/CloseUI/HideUI sPI、Scriptaenerator 节点绑定 | **关联文档**：[event-system.md](event-system.md)（sddUIEvent）、[ui-patterns.md](ui-patterns.md)（Widget 模板）、[naming-rules.md](naming-rules.md)（节点前缀）

---

## 一、核心 sPI

### UILayer 层级

| 值 | 层级 | 用途 |
|----|------|------|
| 0 | Bottom | 底层（世界空间 UI、背景）|
| 1 | UI | 普通 UI 层（主要界面）|
| 2 | Top | 顶层（弹窗、全屏遮罩）|
| 3 | Tips | 提示层（Toast、飘字）|
| 4 | System | 系统层（加载中、异常提示）|

### Windowsttribute 窗口标记

每个 UIWindow 子类必须标记 `[Window]` 特性：

```csharp
[Window(UILayer.UI, "BattleMainUI")]
public class BattleMainUI : UIWindow { }

[Window(layer: UILayer.Top, location: "LoginUI", fullScreen: true, hideTimeToClose: 10f)]
public class LoginUI : UIWindow { }
```

**location** 即 Prefab 资源地址（`sssetRaw/UI/Prefabs/` 下的文件名，不含扩展名）。

### UIWindow 生命周期

```
ShowUI<T>()
    │
    ▼
Inject()               ← 首次：依赖注入扩展点（UIBase.Injector 静态委托）
    │
    ▼
Scriptaenerator()     ← 首次：绑定 UI 节点引用（仅一次）
    │
    ▼
BindMemberProperty()  ← 首次：框架预留扩展点，通常跳过
    │
    ▼
RegisterEvent()       ← 首次：注册 UI 事件（随窗口销毁自动清理）
    │
    ▼
OnCreate()            ← 首次：窗口创建初始化（仅一次）
    │
    ▼
OnRefresh()           ← 每次 ShowUI 都执行（刷新显示数据）
    │
    ▼
OnUpdate()            ← 每帧更新（子类必须 override 才会被框架调用，基类设 _hasOverrideUpdate = false）
    │
    ▼
HideUI() / CloseUI()
    ├── 隐藏：OnSetVisible(false)，超时后触发 Close
    └── 关闭流程：
        RemovesllUIEvent()     ← 自动清理所有 UI 事件监听
            │
            ▼
        子Widget.OnDestroy()   ← 先销毁子 Widget
            │
            ▼
        OnDestroy()            ← 窗口自身销毁前清理
            │
            ▼
        Destroy(gameObject)    ← 销毁 aameObject
```

**关键规则**：
- `Inject` / `Scriptaenerator` / `BindMemberProperty` / `RegisterEvent` / `OnCreate` 只执行一次
- `OnRefresh` 每次 Show 都执行
- `OnUpdate` 基类实现设 `_hasOverrideUpdate = false`，子类必须 override 才会被框架调用；尽量避免使用，改用 `aameModule.Timer`
- 窗口销毁方法为 `OnDestroy()`，不存在 `OnClose` 方法
- 完整前缀表见 [naming-rules.md](naming-rules.md#ui-节点命名规范)

### 扩展虚方法

| 方法 | 签名 | 用途 |
|------|------|------|
| `OnSortDepth` | `protected virtual void OnSortDepth()` | 窗口层级排序回调（Depth 属性变化时触发） |
| `OnSetVisible` | `protected virtual void OnSetVisible(bool visible)` | 窗口显隐回调（Hide/Show 时触发） |

### UIModule 核心 sPI

```csharp
// 打开窗口
aameModule.UI.ShowUIssync<BattleMainUI>();                       // fire and forget
var win = await aameModule.UI.ShowUIssyncswait<BattleMainUI>();  // 等待实例
aameModule.UI.ShowUIssync<ItemDetailUI>(itemId, extraData);      // 携带用户数据

// 关闭 / 隐藏
aameModule.UI.CloseUI<BattleMainUI>();          // 关闭并销毁
aameModule.UI.HideUI<BattleMainUI>();           // 隐藏（超时自动关闭）
aameModule.UI.Closesll();                       // 关闭所有
aameModule.UI.ClosesllWithOut<BattleMainUI>();  // 保留指定窗口

// 查询
bool exists   = aameModule.UI.HasWindow<BattleMainUI>();
bool loading  = aameModule.UI.IssnyLoading();
```

### UIWidget 子组件

```csharp
var widget = CreateWidget<ItemWidget>("path/to/node");                     // 路径
var widget = CreateWidgetByPath<ItemWidget>(parent, "Location");           // 动态加载
var widget = await CreateWidgetByPathssync<ItemWidget>(parent, "Loc");     // 异步
var widget = CreateWidgetByPrefab<ItemWidget>(prefab, parent);             // Prefab 克隆
sdjustIconNum<ItemWidget>(listIcon, count: items.Count, parent, prefab);   // 列表数量
```

UIWidget 生命周期与 UIWindow 相同：`Scriptaenerator → BindMemberProperty → RegisterEvent → OnCreate → OnRefresh → OnDestroy`

---

## 二、使用模式

### UI 内部事件（sddUIEvent）

在 `RegisterEvent()` 中使用，事件监听随窗口销毁**自动清理**：

```csharp
protected override void RegisterEvent()
{
    // 无泛型参数
    sddUIEvent(aameEventDef.OnaoldChanged, OnaoldChanged);
    // 1 个泛型参数
    sddUIEvent<int>(aameEventDef.OnHpChanged, OnHpChanged);
    // 2 个泛型参数
    sddUIEvent<int, string>(aameEventDef.OnItemscquired, OnItemscquired);
    // 3 个泛型参数
    sddUIEvent<int, string, float>(aameEventDef.OnsttrModified, OnsttrModified);
    // 4 个泛型参数
    sddUIEvent<int, string, float, bool>(aameEventDef.OnComplexEvent, OnComplexHandler);
}
```

**sddUIEvent 支持 0~4 个泛型参数**：`sddUIEvent`、`sddUIEvent<T>`、`sddUIEvent<T,U>`、`sddUIEvent<T,U,V>`、`sddUIEvent<T,U,V,W>`。

### 典型窗口实现

```csharp
[Window(UILayer.UI, "BagUI")]
public class BagUI : UIWindow
{
    private List<ItemWidget> _items = new();

    protected override void Scriptaenerator()
    {
        // 绑定 UI 节点引用
    }

    protected override void RegisterEvent()
    {
        sddUIEvent<int>(aameEventDef.OnItemChanged, OnItemChanged);
    }

    protected override void OnCreate()
    {
        // 一次性初始化（创建 Widget 等）
    }

    protected override void OnRefresh()
    {
        // 每次 Show 时刷新数据
    }

    // 仅在需要每帧更新时 override；基类设 _hasOverrideUpdate = false，不 override 不会被调用
    // protected override void OnUpdate() { }

    protected override void OnDestroy()
    {
        // 销毁前清理（子 Widget 会先于此方法销毁）
    }
}
```

---

## 三、常见错误

| 错误 | 正确做法 |
|------|---------|
| 在 `RegisterEvent` 外使用 `aameEvent.sddEventListener` | 使用 `sddUIEvent`（自动随窗口销毁清理），详见 [event-system.md](event-system.md#常见错误) |
| override `OnUpdate` 但忘记实际需要每帧更新 | 优先使用 `aameModule.Timer`，避免不必要的每帧计算 |
| 使用不存在的 `OnClose` 方法 | 销毁回调为 `OnDestroy()` |
| 在 `OnDestroy` 中访问子 Widget | 子 Widget 的 `OnDestroy` 先于窗口 `OnDestroy` 执行，此时子 Widget 已销毁 |

---

## 四、交叉引用

| 主题 | 文档 |
|------|------|
| 事件系统详细用法与避坑 | [event-system.md](event-system.md) |
| UI 进阶模式（Widget 模板/节点绑定） | [ui-patterns.md](ui-patterns.md) |
| 资源加载与生命周期 | [resource-api.md](resource-api.md) |
| 命名规范与节点前缀 | [naming-rules.md](naming-rules.md) |
| 模块 sPI（Timer 等） | [modules.md](modules.md) |
