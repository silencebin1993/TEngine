# 材质定案：阳光培养皿 · BioGlass（细胞阶段）

> 版本：v1.0 · 2026-08-10  
> 上位文档：`Art_Direction_Cell_Stage.md`（造形 / Tripo 管线不变）  
> 运行时入口：`BinGames/SimBioGlass`（测试主视觉）· `BinGames/SimInstancedUnlit`（远距 / 压测 LOD）  
> 渲染路径：Built-in RP · `Graphics.RenderMeshInstanced` · 每批 ≤1023

---

## 1. 一句话

**形体走 Bio-Glass（半透明凝胶 + 膜边），色彩走吉卜力式鲜艳生命色；危险用降饱和脏色对比。半透明与液体扰动只在 Unity 单 Pass shader 里做，绝不进 Tripo 原画。**

对外称呼：**阳光培养皿（Sunny Petri · BioGlass）**。

---

## 2. 视觉配方（Look）

| 层（观感） | 实现（单 Pass 伪多层，非多 Draw） | 吉卜力侧 |
|---|---|---|
| 内核 | UV 径向 `r < Core`，提亮 `_Color` | 干净高饱和中心，像阳光透果冻 |
| 胶质 | 中段径向，中等 alpha | 鲜艳但略透，露出水色底 |
| 膜边 | 外圈 soft alpha + rim 高光 | 清晰描边，不脏边 |
| 液体扰动 | **仅膜边**：廉价 sin/hash 扰动半径与 rim，不做 GrabPass / 折射 | 微微「活」着，不砸性能 |

**色板规则**

- 生命/营养/玩家：高饱和、高明度（蜜黄、草绿、珊瑚、天蓝、薄荷绿）。
- 精英：同相再提亮 + 暖金偏。
- 污染/腐蚀/灾变：同相**降饱和 + 偏脏**（褐紫、病绿），禁止与生命色同样鲜艳。
- 同屏主色建议 ≤5（己方、食物、普敌、精英、污染）。

与 `Art_Direction_Cell_Stage.md` §5 六路线色兼容：路线色仍是实例 `_Color`；本 shader 只负责「怎么把这色画成凝胶」。

---

## 3. 性能红线（必须遵守）

| 允许 | 禁止（万人场即炸） |
|---|---|
| 1 Pass · Unlit · GPU Instancing | 多 Pass 描边 / 真多层 mesh |
| 径向伪层 + 解析噪声 | GrabPass / 屏幕折射 / 深度软粒子 |
| 共享 Material + MPB 只改 `_Color` | 每单位独立 Material |
| 关阴影、关 ZWrite、Cull Off | 复杂 lit / PBR / 多贴图采样 |
| 远距切 `SimInstancedUnlit` | 全图永远最高配 BioGlass |

**规模预期（诚实）**

- 设计峰值：数千～上万（与 Spec / `SimRenderer` 一致）→ **BioGlass 默认**。
- 「千万」级：**不能**全员半透明 BioGlass。必须 LOD：近距 BioGlass、中距 Unlit 软圆、远距点/合并 impostor；或降 alpha、关 wobble。本定案不承诺千万全员高配。

片元预算目标：每像素约 **&lt; 30 ALU**、**0 纹理**（噪声全解析）；后续若加噪声贴图须 64² 且可关。

---

## 4. Shader 契约

| Shader | 用途 |
|---|---|
| `BinGames/SimBioGlass` | 细胞阶段默认 LookDev / 正式倾向 |
| `BinGames/SimInstancedUnlit` | LOD / 压测 / 缺 shader 回退 |

| 属性 | 谁写 | 说明 |
|---|---|---|
| `_Color` | **实例**（`MaterialPropertyBlock` / instancing buffer） | 单位染色，与现 `SimRenderer` 一致 |
| `_CoreBright` | 材质 | 内核提亮 |
| `_RimColor` / `_RimPower` | 材质 | 膜边高光 |
| `_EdgeSoft` | 材质 | 外缘软度 |
| `_IdleWobble` / `_WobbleSpeed` | 材质 | 静止轻柔起伏；游动时自动减弱 |
| `_SwimStretch` / `_SwimCompress` | 材质 | 沿速度方向拉长 / 侧向收窄 |
| `_ImpactSquash` | 材质 | 受击侧压扁 |
| `_Motion` / `_Impact` | **实例**（`SimRenderer` MPB） | 游动方向+速度；受击方向+强度 |

网格：继续用现有 **XZ Quad + UV**；圆形剪影由 shader 径向 alpha 完成。

**性能**：多 2 个实例 Vector4 + 片元里少量点积；无多 Pass。Hits 映射为 O(存活数 + HitCount)，万级可接受。

---

## 5. 验收（LookDev 过关标准）

1. `Shader.Find("BinGames/SimBioGlass") == true`，`enableInstancing = true`。  
2. Play「开始漂流」：单位为**软边半透明圆**，非实心方块。  
3. 膜边可见轻微蠕动；关 `_WobbleAmp` 后静帧仍可读。  
4. 玩家色对比仍最高；污染体不「比食物更好看」。  
5. 同屏 ≥1k 时无新增异常卡顿归因于多 Pass / GrabPass（Frame Debugger：每 VisualId 仍为实例化批）。

---

## 6. 明确不做（本版）

- 真折射、焦散、体积雾进单位 shader  
- 多 Pass 描边、扩大 mesh 外壳  
- 把玻璃感画进 Cursor→Tripo 输入图（仍遵 Art Direction §0.1）  
- 为吉卜力去上全场手绘水彩背景（环境另案；单位先定）

---

## 7. 变更记录

| 版本 | 内容 |
|---|---|
| v1.0 | 定案阳光培养皿；落地 `SimBioGlass`；与吉卜力鲜艳色 + BioGlass 合流 |
| v1.1 | 软边；`_Motion`/`_Impact` 游动与受击方向形变（`SimRenderer`） |
