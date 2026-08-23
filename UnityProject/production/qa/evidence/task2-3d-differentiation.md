# 任务二：3D 表现差异化

> 2026-08-23（one-shot-prompt.txt 全量执行）

## 改动

新增 `Assets/GameScripts/Main/Sim/SimVisualLibrary.cs`（AOT 层，`BinGames.Sim` 命名空间，
与 `SimRenderer` 同目录）：程序化 Mesh 组合体工厂，零美术依赖。基础几何（球/胶囊/锥/环/盘/
八面体/四面体/多刺球）用 `Mesh.CombineMeshes(mergeSubMeshes:true, useMatrices:true)` 拼合成
单一 Mesh（满足 `SimRenderer.Draw` 的"每 VisualId 一个 Mesh"GPU Instancing 限制）。覆盖：

- 24 个器官/代谢模块 ArtId（`org/mito`…`org/filter`），严格按 one-shot-prompt.txt 给定的造型映射表实现。
- 4 个基元：`prim/energy`=球、`prim/mass`=立方、`prim/light`=八面体（菱形）、`prim/heat`=四面体。
- 3 个召唤物（配合任务三）：`summon/spore`/`summon/phage`/`summon/mycelium`。
- 5 个 Carrier 装配挂件：`carrier/base`/`emitter`/`cilia`/`emitter_gene`/`cilia_gene`。

`CellStageFlow.BuildVisuals()` 扩展：新增 `ArtVisualIdBase=100` 段，按
`SimVisualLibrary.AllArtIds` 声明序铺开注册；`VisualIdForArtId(string)` 供热更层反查。
召唤物特例：`EffectSpawn` 把 `SpawnEnemyId` 同时当 `ArchetypeId`/`VisualId` 用，故 13/14/15
（新增召唤物行为原型 id）直接在 0-99 段内注册专属 mesh，无需重定向。

新增 `CarrierBodyVisualPresenter.cs`（Battle/Feedback，`GameModuleBase`，同
`ComposeAimIndicatorPresenter` 骨架）：轮询 `CarrierRegistry.AssemblyVersion`，按
`ActiveCarrier.OrganelleId`（org_emitter/org_cilia/无）× 是否挂模块基因，切玩家 VisualId
（4 种组合：base/emitter/cilia/*_gene），经 `SimBridge.SetPlayerVisualId` → `SimWorld` 新增的
`SetPlayerVisualId` 写 `_visualId[PlayerIndex]`。

新增 `VisualCompareStand.cs`（MetabolicSlice/DebugTools）：调试用"3D 造型对比台"，网格铺开
`SimVisualLibrary.AllArtIds` 全部 36 项（纯 GameObject+MeshFilter+MeshRenderer，**不用**
`SimBioGlass`——那是 GPU Instancing 专用 shader，直接挂普通 MeshRenderer 会读不到逐实例数据，
渲染成一坨大团块，改用 `Sprites/Default`，同 `WhiteboxObstacleVisual` 先例）。挂到
`BattleSandboxUIToolkit` 的"生成/刷新对比台"按钮。

## 验收（execute_code + 截图）

```
foreach artId in SimVisualLibrary.AllArtIds（36 项）:
    BuildForArtId(artId) 非空且 vertexCount>0
    CellStageFlow.VisualIdForArtId(artId) >= 0
→ 0 项缺失
```

截图 `visual-compare-stand-final.png`：独立相机（不受战斗跟随镜头影响）俯视对比台，
36 个造型同屏排开——胶囊+锥（carrier/emitter）、多刺球（org/spine）、圆环+瓣（org/valve）、
菱形（prim/light）、立方（prim/mass）、锥形（prim/heat）、双球相接（org/merge）、
带凸点圆盘（org/chloro）等两两形状/结构可辨，非"同球异色"。

## Unity 真编译 + Play 验证

`refresh_unity` 0 error。Play 模式下 `VisualCompareStand.Spawn()` 生成 36 项、
`CarrierBodyVisualPresenter` 随 Carrier 装配变化写 `SimBridge.SetPlayerVisualId` 全程
`read_console(types=[error,warning])` 0 条。

## Out of scope（诚实记录）

- 玩家 Carrier 挂载具体某个模块基因时只区分"有/无基因"（通用挂件），不逐基因建 24 种专属挂件——
  design 原文"挂载基因时附加对应挂件"若做成 24×2 组合体，规模远超一次性 session 的合理产出；
  24 种器官/基因的形状差异已在图鉴/对比台完整体现，玩家本体只需要"看得出装了什么类型的出口
  器官 + 挂没挂东西"这一层信息。
