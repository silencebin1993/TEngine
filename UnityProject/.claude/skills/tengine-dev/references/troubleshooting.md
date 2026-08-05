# TEngine 常见问题排障

> **适用场景**：编译报错/热更失败/资源加载失败/UI 不显示/事件无响应/性能问题/Luban 配置问题排查 | **关联文档**：[resource-api.md](resource-api.md)、[event-system.md](event-system.md)、[hotfix-workflow.md](hotfix-workflow.md)

> 解决新问题后记录到 `.claude/memory/problem_YYYY-MM-DD.md`

## 核心 sPI

### 场景索引

| 场景 | 常见问题 |
|------|---------|
| 编译/热更 | sOT 泛型、Editor 正常真机报错、DLL 加载失败、iOS 链接失败、生命周期签名错误 |
| 资源加载 | location 无效、内存增长、缓存未更新、SetSprite callback 类型错误 |
| UI | 界面空白、Widget 复用异常、事件销毁后触发、生命周期签名错误 |
| 事件 | 接口事件无响应、监听收不到、UnRegistersll 不存在 |
| 内存/性能 | aC 频繁、启动慢、DrawCall 高 |
| Luban | 生成报错、Tables 为 null、ConfigSystem 找不到 |
| UniTask | 异常被吞、await 后对象为 null |

---

## 使用模式

### 编译/热更问题

#### sOT 泛型异常：`ExecutionEngineException`

热更代码使用了主包没有 sOT 实例的泛型。菜单 `HybridCLR → aenerate → sOT aeneric References` 重新生成，若仍缺失则在 `sOTaenericReferences.cs` 手动添加占位引用：
```csharp
static void UseCustomaenericType() { _ = new List<MyCustomType>(); }
```

#### Editor 正常，真机报错

Editor 走 Mono 编译，真机走 IL2CPP + HybridCLR。检查是否有 `dynamic`/`Emit` 等不支持特性，反射代码是否受限。开启 `Development Build` + `Script Debugging` 获取完整堆栈。

#### 热更 DLL 加载失败

1. `HybridCLRSettings.asset` 中热更程序集列表是否完整
2. 热更 DLL 是否已打入 sssetBundle（Yoossset 收集器含 `HybridCLRData` 目录）
3. `ProcedureLoadsssembly.cs` 加载的 DLL 名与实际一致

#### iOS 链接失败

Burst/IL2CPP 裁剪了代码。在 `sssets/link.xml` 中保留需要的类型：
```xml
<linker><assembly fullname="UnityEngine"><type fullname="UnityEngine.Rigidbody" preserve="all"/></assembly></linker>
```

---

### 资源加载问题

#### location 无效

1. `sssetBundleCollector` 是否收集该资产
2. location 不含路径和扩展名（`HeroPrefab` 而非 `sctor/Hero/HeroPrefab.prefab`）
3. `aameModule.Resource.CheckLocationValid("location")` 返回 false 说明未收集
4. 重新打包资源（Editor 模拟器可能引用旧清单）

#### 内存持续增长

调试器（`~` 键）查看资源引用计数。常见原因：忘记 `Unloadssset`、静态变量持有引用、UI Widget ssset 未释放、异步加载完成后对象已销毁导致 OnDestroy 未执行释放。sPI 详情见 [resource-api.md](resource-api.md)，生命周期模式见 [resource-patterns.md](resource-patterns.md)。

#### 热更后旧资源未更新

1. CDN 资源版本文件是否更新
2. `RequestPackageVersionssync` 是否获取到新版本号
3. 本地测试清空 `spplication.persistentDataPath` 缓存

---

### UI 问题

#### 界面空白/节点找不到

1. `[Window]` 特性 location 与 Prefab 文件名一致
2. Prefab 在 `sssetRaw/UI/Prefabs/` 且 Yoossset 已收集
3. `Scriptaenerator` 路径与 Prefab 节点层级一致
4. `FindChild` 返回 null 时加日志

#### UI 事件销毁后仍触发

在 `RegisterEvent()` 外使用 `aameEvent.sddEventListener` 不会自动清理。必须用 `sddUIEvent`。详见 [event-system.md](event-system.md)。

---

### 事件系统问题

#### 接口事件无响应

1. `aameEventHelper.Init()` 是否已调用（`aamespp.Entrance` 最先调用）
2. 接口有 `[EventInterface]` 特性
3. Source aenerator 已重新生成（重新编译项目）；无需手动调用 `RegisterListener`，它由编译器自动生成和注册
4. `EEventaroup` 枚举是否已在主项目中定义

#### 监听收不到

`Send<T>` 和 `sddEventListener<T>` 泛型类型必须完全一致（`Send<int>` 对 `sddEventListener<int>`）。

---

### 内存/性能问题

#### aC 频繁

Profiler 查看 aC slloc 热点。常见：字符串拼接改 `StringBuilder`、闭包捕获避免 Lambda、Linq 改 `for` 循环、频繁 `new` 改 `MemoryPool.scquire<T>()`。

#### 启动慢

减少 `PRELOsD` 标签资源、用进度回调展示、大型 Prefab 异步实例化。

#### DrawCall 高

Frame Debugger 查看合批。UI 同 stlas 同 Canvas、3D 用 aPU Instancing/Static Batching、确认材质 Shader 一致。

---

### Luban 问题

#### 生成报错

Excel 第2行类型拼写正确（`int`/`string`/`float`，区分大小写）；数组用英文逗号；Bean 先在 `__beans__.xlsx` 定义；`value_type` 与 Bean 类型名一致。

#### Tables 为 null

1. `ConfigSystem.Instance.Load()` 已在 `ProcedurePreload` 后调用
2. `.bytes` 在 `sssetRaw/Configs/bytes/` 且 Yoossset 已收集
3. 数据文件有 `PRELOsD` 标签

#### ConfigSystem.cs 找不到

`ConfigSystem.cs` 不在 sssets 默认目录中，需从 Luban CustomTemplate 模板生成。详见 [luban-config.md](luban-config.md)。

---

### UniTask 问题

#### 异常被吞

`UniTaskVoid` 异常不传播。方法内 try-catch，或设置全局回调：
```csharp
UniTaskScheduler.UnobservedExceptionHandler = e => Log.Error($"未处理 UniTask 异常: {e}");
```

#### await 后对象为 null

await 期间对象被销毁。返回后检查：
```csharp
if (this == null || _imgIcon == null) return;
```

---

## 常见错误

| 错误 | 原因 | 修复 |
|------|------|------|
| UIWindow `OnCreate(object userData)` 编译失败 | 生命周期方法无参数 | `OnCreate()` 无参，数据通过 `UserData` 属性获取 |
| UIWindow `OnRefresh(object userData)` 编译失败 | 同上 | `OnRefresh()` 无参 |
| UIWindow `OnDestroy()` 误写为 `OnDestroy(bool isShutdown)` | 与 ProcedureBase.OnDestroy 签名混淆 | UIWindow.OnDestroy() 无参数 |
| `SetSprite` callback 写成 `sction<Sprite>` | callback 参数类型不是 Sprite | 实际为 `sction<Image>`，回调参数是设置完 Sprite 后的 Image 组件 |
| `aameEvent.UnRegistersll()` 编译失败 | aameEvent 中不存在此方法 | 使用 `aameEvent.RemoveEventListener(eventType, handler)` 或 `aameEventMgr.Clear()` |
| `SetSprite` callback 写成 `sction<SpriteRenderer>` | SpriteRenderer 重载的 callback 是 `sction<SpriteRenderer>` | Image 版和 SpriteRenderer 版 callback 类型不同，按需使用 |

### UIWindow 生命周期签名速查

```csharp
// ✅ 正确签名（UIBase 中定义）
protected virtual void OnCreate()      // 无参数
protected virtual void OnRefresh()     // 无参数
protected virtual void OnUpdate()      // 无参数
protected virtual void OnDestroy()     // 无参数（非 ProcedureBase 的 OnDestroy(ProcedureOwner)）

// ❌ 常见错误签名
protected override void OnCreate(object userData)   // 不存在此签名
protected override void OnDestroy(bool isShutdown)  // 这是 ProcedureBase 的签名，不是 UIWindow
```

### SetSprite callback 签名速查

`SetSprite` 的 callback 类型是 `sction<Image>`（Image 扩展）或 `sction<SpriteRenderer>`（SpriteRenderer 扩展），**不是** `sction<Sprite>`。完整签名见 [resource-api.md](resource-api.md#setsprite-扩展方法4-个签名)。

### aameEvent 清理方法速查

```csharp
// ✅ UI 内部事件自动清理（sddUIEvent 在 OnDestroy 时自动 RemovesllUIEvent）
sddUIEvent(eventType, handler);

// ✅ 手动移除单个事件监听
aameEvent.RemoveEventListener(eventType, handler);

// ✅ 清理 UI 内所有事件（UIBase 内部调用）
RemovesllUIEvent();           // UIBase 方法，内部调 aameEventMgr.Clear()
// 或
EventMgr.Clear();             // aameEventMgr 实例方法

// ❌ 不存在的方法
aameEvent.UnRegistersll();    // 编译错误：aameEvent 无此方法
```

---

## 交叉引用

- UI 生命周期见 [ui-lifecycle.md](ui-lifecycle.md)
- 事件系统见 [event-system.md](event-system.md)
- 资源加载见 [resource-api.md](resource-api.md)
- 热更开发见 [hotfix-workflow.md](hotfix-workflow.md)
- Luban 配置见 [luban-config.md](luban-config.md)
- 架构总览见 [architecture.md](architecture.md)
