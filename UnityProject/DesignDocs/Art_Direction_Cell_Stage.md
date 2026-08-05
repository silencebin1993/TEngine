# 《文明织造》美术选型文案（细胞阶段优先）

> 版本：**v1.1**  
> 定稿日期：2026-08-05（v1.0） / 管线修订：2026-08-05（v1.1）  
> 定稿方式：全部采纳原文推荐项  
> 依据：`Cell_Stage_Spec.md`、`GDD_Starter_Pack.md` §13、`Game_Framework_Design.md`  
> 地位：细胞阶段美术基线；后续 Art Bible / 白模 / 部件表以此为准  
> **v1.1 增量**：锁定 **Cursor 文生图 → Tripo 图生 3D → Unity 表现** 管线，并拆开「几何原画」与「半透明表现」两套约束

---

## 0. 定稿结论

**细胞阶段采用「显微镜下的风格化 3D / 2.5D」：半透明湿润、低面数有机体、功能可读的固定槽位拼装；不做孢子式自由拖拽造物，也不做像素主视觉。**

三条视觉承诺：

1. **尺度母题可继承**：细胞膜 → 皮肤/甲壳 → 城墙 → 舰壳 → 星云膜。  
2. **构筑一眼可见**：六条路线有独立剪影与材质语义，截图即可猜 build。  
3. **规模即演出**：万敌时靠体积分层、发光层级、生态染色表达「从猎物到霸主」。

### 0.1 关键分层（产能硬规则）

| 层 | 工具 | 产出 | 视觉目标 |
|----|------|------|----------|
| A. 几何原画 | **Cursor 文生图** | 不透明粘土感概念图 / 多视图 | 剪影准、体积闭合、边缘干净 → **给 Tripo 用** |
| B. 网格 | **Tripo** 图生 3D（优先多视图） | 低面 mesh（smart low-poly / quad） | 拓扑可用、体积正确 |
| C. 表现 | Unity Built-in + 自定义 shader / MPB | 半透明湿润、边缘光、内核脉冲、路线色 | **半透明只在这一层做**，绝不画进 Tripo 输入图 |

> **禁止**：把「玻璃 / 透明 / 半透明 / 液体折射」画进 Cursor→Tripo 输入图。  
> Tripo 会把透明读成破洞、薄壳或糊体；湿润半透明统一用 shader 叠。

---

## 1. 硬约束（不可违背）

| 约束 | 对美术的含义 |
|------|----------------|
| 单局约 60 分钟、敌人峰值数千～上万 | 大量单位走 **GPU 实例化**；忌复杂骨骼/布料/每单位独立材质 |
| Built-in RP，无 Entities Graphics | 实例化网格 / 广告牌 / 简单自定义 shader |
| 玩法核心：吞噬 + 局内突变构筑 | 体积可读、吞噬反馈、选卡后外形真变 |
| 六条路线混搭 | 外形可叠加，不做不可组合的全身模型 |
| 后续还有器官→生物→文明→废土→星海 | 造形语言必须能「换皮扩尺度」 |
| 独立开发产能 | 模块复用与参数换色优先 |
| Cursor → Tripo | 几何原画必须 **不透明、实底、均匀光、强剪影**（细则 §12） |

万敌可读性（Spec §16）：玩家始终最高对比度；敌人按体积/颜色/轮廓分层。

---

## 2. 差异策略（玩法不新时靠什么眼前一亮）

| 层级 | 做什么 | 不做 |
|------|--------|------|
| A. 第一眼气质 | 湿润微观、半透明、生物发光、流体环境（**运行时 shader**） | 霓虹竞技场默认脸、写实医学片 |
| B. 构筑可视化 | 选卡 → 身体立刻长部件；结算「路线剪影」可分享 | 只改数字、外形不变 |
| C. 时期换脸 | 6 时期换水色/粒子/敌人调色板 | 每时期重做整张地图 |
| D. 终局尺度感 | 霸主体量与光晕碾压全场 | 依赖长过场 CG |

---

## 3. 画风 — 已定：C+F

**低面风格化 3D 单位 + 2D UI/卡面。**

- 单位/玩家：**运行时**半透明有机 mesh，边缘光 + 内核高亮  
- **几何原画**：哑光粘土 / 树脂玩具感，不透明，便于 Tripo 抽深度  
- 环境：简单体积 + 程序化流体/颗粒（非开放世界地形）  
- UI/卡面：2D，与战场精度分离（卡面可另走「插画稿」，不进 Tripo）  
- **不做**：像素主视觉、写实显微镜风、每敌人独立高模  

参考气质：孢子细胞关的半透明可爱 × 高可读剪影 × 科学染色切片 —— 不是 Vampire Survivors 霓虹弹幕。

---

## 4. 造物 — 已定：方案 M

**固定槽位模块 + 强剪影演化。选卡即装配，无造物台。**

孢子式自由拖拽（S）降级为远期器官/生物阶段再议。

规则：

1. 槽位：`核/体` · `膜/外壳` · `口器/捕食` · `运动器` · `附属/孢子位` · `领域/菌毯锚`（后两者随路线解锁）  
2. 选卡 = 改槽位部件或叠材质参数  
3. 混搭：同槽高稀有度覆盖；副路线只加着色层 + 小饰件  
4. 结算生成「物种剪影 + 路线色带」供分享与图鉴  
5. **细胞阶段不做**：自由旋转/缩放部件、物理关节、造物沙盒  

**对 Tripo 的含义**：每个槽位部件单独出图、单独生模；禁止一次生成「整只细胞 + 六件外挂」的全身拼贴图（深度歧义、缝合灾难）。

---

## 5. 六路线视觉母版 — 已定

| 路线 | 剪影关键词 | 材质/光（运行时） | 几何原画色（不透明） | 战场痕迹 |
|------|------------|-------------------|----------------------|----------|
| 吞噬扩张 Devour | 大、圆、张口、不规则外缘 | 厚膜、暗沉、吞咽拉伸 | 深酒红 / 褐紫实色 | 尸体残渣、涟漪 |
| 机动猎食 Agile | 梭形、尖、细长鞭毛 | 高对比边缘、残影 | 青绿实色 + 白刃边 | 穿体残线、标记 |
| 电化统治 Electric | 刺突、丝状触须 | 青白高光、脉冲描边 | 青瓷蓝实色 + 亮黄脊 | 导电网、连锁弧 |
| 孢子繁殖 Spore | 多泡、分体、**附着**小泡（非飘尘） | 雾状半透明、孢子尘 | 淡紫实色多泡簇 | 附属体云、感染斑 |
| 菌毯筑巢 Nest | 扁平、锚点、地毯延伸 | 糙面、菌丝纹理 | 橄榄绿 / 土褐糙面 | 地面染色领地 |
| 异化污染 Corrupt | 不对称、破口、棘 | 病态色偏、噪点闪烁 | 黄绿病态实色 + 黑棘 | 污染雾、灾变闪 |

混搭：主路线定骨架+主色；副路线 +1 槽位部件 +1 种粒子。

**几何原画注意（Spore / Electric）**：

- Spore：小泡必须 **粘在主体上**，禁止漂浮粒子环（Tripo 会生成一堆碎 mesh）。  
- Electric：触须要粗、闭合、数量少（≤6）；禁止发丝级细丝。

---

## 6. 其他表现 — 已定汇总

| 项 | 定稿 |
|----|------|
| 摄像机 | 轻微透视顶视（培养皿感） |
| 玩家识别 | 高亮描边 + 内核脉冲 + 体积阴影圈 |
| 吞噬反馈 | 分级：小目标吸附吸入；同级 ≤0.4s 撕咬；精英专属 pose |
| 突变演出 | 确认瞬间 0.6–1.0s 突变，战场暂停（暂不做悬停预览） |
| 环境 | 单培养皿场景 × 6 套时期参数换装 |
| UI | 有机切片风（半透明面板 + 膜状边框）；选卡侧可有外形预览 |
| VFX | 分级：玩家/精英全特效，小怪简易；禁过曝白闪 |
| 动画 | 玩家少量骨骼/顶点；小怪抖动/广告牌；仅首领独立状态机 |
| 孢子感替代 | 全部采纳（见下） |

**孢子感替代（已定，必做方向）：**

1. 开局极简圆泡 → 60 分钟后严重异形  
2. 图鉴「我的物种」剪影墙  
3. 时期切换旁白 + 水色突变  
4. 附属体/菌毯染色战场  
5. 结算短镜头拉远，预埋尺度跃迁  

---

## 7. 跨阶段造形语法 — 已定锁定

| 不变量 | 细胞 | 未来映射 |
|--------|------|----------|
| 半透明膜 + 内核 | 细胞膜/核（运行时） | 护盾/反应堆/舰核 |
| 功能部件外挂 | 口器、鞭毛、孢子囊 | 武器、引擎、无人机舱 |
| 路线主色 | 六色体系 | 文明涂装/舰级标识 |
| 领地染色 | 菌毯 | 领土/星区控制 |

细胞阶段不允许与后期造形脱钩。

---

## 8. 产能与管线

### 8.1 P0 最小可卖相资产包

1. 玩家基底 1 + 每路线核心部件 2–3（约 15–20 模块）  
2. 敌人：8–12 外形原型 × 换色/饰件（覆盖 30 敌人），不按 EnemyId 各做一模  
3. 首领 1 × 三阶段换装  
4. 1 培养皿场景 + 6 套时期参数  
5. UI：HUD + 选卡 + 结算物种剪影  
6. VFX：吞噬 / 升级突变 / 六路线各 1 签名特效  

### 8.2 技术对齐

- 外观映射 `ArchetypeId` 分批实例化（`SimRenderer`）  
- 共用 shader + MPB / 实例 buffer  
- 资源：`GameRes/Art/` 源文件 → `GameRes/Raw/` 运行时  
- 源文件建议目录：`GameRes/Art/Cell/Concepts/`（Cursor）→ `GameRes/Art/Cell/Meshes/`（Tripo 导出）→ 烘焙进 `Raw/`  
- **资产对照表**：Unity 菜单 `BinGames → 美术资源板`（工具栏 `Cell Art`）；数据在 `GameRes/Art/Cell/registry.json`
### 8.3 白模验收三关（画完精模前必须过）

1. 10 米外剪影：六路线可区分  
2. 1000 单位同屏：玩家一眼可找  
3. 选卡前后截图：能看出「变强/变怪」  

### 8.4 Cursor → Tripo → Unity（标准作业）

```
1. Cursor：用 §12 模板生成「几何原画」（1:1，实底，不透明）
2. 自检清单（不过关不进 Tripo）：
   □ 主体占画面约 60–75%，居中，不裁切
   □ 纯色背景，主体与背景对比强
   □ 无文字 / UI / 水印 / 框架
   □ 无透明、无玻璃、无折射、无强阴影
   □ 无漂浮粒子、无烟雾、无景深虚化
   □ 体积闭合（无开放薄片、无破碎散件）
3. Tripo：
   - 普通模块：单视图 image-to-model 即可
   - 玩家基底 / 首领 / 关键部件：尽量 front+left+back+right 多视图
   - 开启 smart_low_poly + quad（游戏向）
   - texture_alignment 优先 geometry（要形准，不要贴图炫）
4. Blender 轻修：合并碎件、封洞、统一朝向、目标面数落到实例化预算
5. Unity：上半透明湿润 shader + 路线色 MPB；卡面另走 2D 插画，不复用几何原画当卡面
```

---

## 9. 明确不做（细胞阶段）

| 不做 | 原因 |
|------|------|
| 孢子式自由拖拽造物台 | UX 叠床架屋；组合爆炸；实例化不友好 |
| 写实解剖/医学可视化 | 脏、难读 |
| 像素主视觉 | 跨阶段差；同质化 |
| 每张卡独立全身模型 | 产能不可承受 |
| 全单位高精度骨骼 | 与万敌预算冲突 |
| 每时期全新场景 | 制作风险 |
| 半透明/玻璃感原画进 Tripo | 深度估歪、破洞、糊壳 |
| 一次生成「全身+全部件」拼贴给 Tripo | 缝合失败、实例化无法拆槽位 |

---

## 10. 定稿决策记录

```
【画风】     C+F（风格化低面 3D + 2D UI）
【造物】     M（固定槽位模块 + 强剪影）
【摄像机】   轻微透视顶视
【玩家识别】 描边 + 内核 + 阴影圈
【吞噬反馈】 分级
【突变演出】 确认时演出（暂无悬停预览）
【环境】     单场景参数换装
【UI】       有机切片
【VFX】      分级
【动画】     分级（仅首领完整骨骼状态机）
【孢子替代】 全部五条
【跨阶段】   锁定造形语法
【路线色】   接受第五节母版
【资产管线】 Cursor 几何原画 → Tripo → Unity 半透明（v1.1）
```

---

## 11. 下一步

1. ~~升 v1.0~~ **已完成**  
2. ~~Cursor / Tripo 管线与提示词~~ **v1.1 已写入 §12**  
3. 补一页 **Visual Identity Statement**（一句话视觉法则 + 3 条原则）→ 可写入正式 Art Bible  
4. ~~用 §12 模板先出玩家基底 + 六路线签名部件~~ **已出并登记进 `Art/Cell/registry.json`**  
5. Tripo 导出后：`manage.py link <id> --mesh Meshes/...`，再进 Unity 跑白模三关（§8.3）  
6. 开 **部件表**（槽位 × 路线 × 稀有度），与 Luban/内容表对齐；新部件先 `manage.py add` 再画图  

---

## 12. Cursor 文生图提示词（Tripo 专用几何原画）

> 用法：整段英文复制进 Cursor「生成图片」；`aspect_ratio` 用 **1:1**。  
> 目标不是好看插画，而是 **让 Tripo 抽准体积**。

### 12.1 万能前缀 / 后缀（每次必带）

**Prefix（放最前）：**

```text
Game-ready concept turnaround for image-to-3D, single solid creature prop centered,
opaque matte clay material, closed manifold volume, clear readable silhouette,
soft even studio lighting, no hard shadows, pure light-gray background (#D8D8D8),
orthographic three-quarter front view, subject fills 70% of frame, high contrast edges,
stylized low-poly organic game asset, toy-like smooth forms,
```

**Suffix（放最后，作否定约束）：**

```text
-- avoid: transparency, glass, translucent, refraction, liquid water, fog, smoke,
particles, floating debris, glow bloom, neon, lens flare, depth of field, motion blur,
text, watermark, UI, frame, collage, multiple objects, cutaway, cross-section,
photoreal microscope, blood gore, anatomy chart, tiny hair-thin filaments
```

### 12.2 玩家基底（核/体）— 开局圆泡

```text
[Prefix]
a simple round single-cell organism, fat soft sphere body, one slightly brighter
opaque core bump on the surface (not a hole), thick smooth membrane edge, chubby cute,
solid coral-pink matte clay, no limbs yet, perfectly closed volume
[Suffix]
```

### 12.3 六路线签名部件（单独生模，再挂槽位）

**Devour · 大口膜（膜/口器）：**

```text
[Prefix]
a devourer mouth module for a cell creature: wide irregular circular maw attached
to a thick rounded collar, soft folded lip ridges, one large bite opening,
solid deep wine-red matte clay, chunky silhouette, no teeth spikes thinner than 5% of body
[Suffix]
```

**Agile · 梭形鞭（运动器）：**

```text
[Prefix]
an agile propulsion module: sleek spindle body with 2–3 thick tapered flagella
fused to the rear, sharp forward tip, teal-green matte clay with pale edge ridges,
streamlined closed volume, flagella thick enough for game low-poly
[Suffix]
```

**Electric · 刺突冠（附属）：**

```text
[Prefix]
an electric crown module: short radial spikes and 4 thick tendril stubs on a ring base,
cerulean matte clay with pale yellow ridge lines painted on (not glowing),
chunky sci-fi organism part, all spikes thick and closed
[Suffix]
```

**Spore · 多泡簇（附属/孢子位）：**

```text
[Prefix]
a spore-cluster module: 5–7 attached bubble buds fused onto a round base,
lavender matte clay, bubbles stuck to the body (none floating),
cluster reads as one connected mesh
[Suffix]
```

**Nest · 菌毯锚（领域锚）：**

```text
[Prefix]
a nest-anchor module: flat disc pad with short mycelium stubs and two peg anchors underneath,
olive-brown rough matte clay, low wide silhouette, carpet-like top texture painted on,
closed solid form
[Suffix]
```

**Corrupt · 破口棘（膜/外壳）：**

```text
[Prefix]
a corrupt shell module: asymmetric cracked carapace plate with a few thick black thorns,
sickly yellow-green matte clay, broken rim but still one closed solid piece,
menacing chunky silhouette, no dust or particles
[Suffix]
```

### 12.4 多视图板（玩家基底 / 首领优先）

在 Prefix 后追加，并明确「同一物体四视图」：

```text
same single opaque clay creature shown as a clean 2x2 turnaround sheet on one image:
top-left front, top-right left side, bottom-left back, bottom-right right side,
identical scale and lighting, centered in each quadrant, pure light-gray background,
no labels, no arrows, no perspective distortion
```

> Tripo 多视图接口顺序：`front → left → back → right`。若 Cursor 一次出四宫格，需裁成四张再上传；或分四次生成（推荐：四次更稳）。

### 12.5 敌人原型（换色复用）

```text
[Prefix]
a small hostile microbe enemy, [SHAPE: round|spindle|crab-like|jellyfish-blob],
solid [COLOR] matte clay, one signature feature: [FEATURE],
readable from top-down game camera, chunky low-poly toy form
[Suffix]
```

### 12.6 Cursor 参数建议

| 参数 | 值 | 原因 |
|------|----|------|
| 比例 | `1:1` | Tripo 友好；主体居中 |
| 风格词 | `matte clay / toy / low-poly organic` | 压住写实与玻璃感 |
| 一次一物 | 强制 | 避免多物体深度竞争 |
| 卡面/宣传图 | **另开提示词**，不要复用几何原画 | 卡面要半透明插画，会污染 Tripo |

### 12.7 Tripo 侧参数建议（游戏实例化）

| 项 | 建议 |
|----|------|
| 输入 | PNG；尽量 ≥ 1024，能到 2K 更好；主体抠干净 |
| 单图 / 多图 | 模块单图；英雄资产四视图 |
| `smart_low_poly` | true |
| `quad` | true |
| `texture_alignment` | `geometry` |
| 导出 | FBX 或 GLB → Blender 清一次 → Unity |

---

## 附录：与已有文案对齐

| 已有结论 | 本文落点 |
|----------|----------|
| GDD：风格化科幻进化，非写实 | 低面有机 3D；几何原画粘土感 |
| GDD：功能可视化 | 槽位部件 = 功能外挂；部件单独生模 |
| GDD：前中期可读、后期规模感 | 动画/VFX 分级 |
| Spec：路线专属视觉为 P1 | 第五节母版；P0 至少主色+1 签名部件 |
| Spec：万敌可读性 | 玩家识别与体积分层为硬需求 |
| 框架：GPU 实例化 | 槽位 Archetype，禁自由组合网格 |
| 产能：Cursor + Tripo | §0.1 / §8.4 / §12 |

---

*本文为细胞阶段美术基线。视觉承诺不变；v1.1 只锁定生成管线。若要改已定画风/造物项，开变更说明并升版本号（v1.2+）。*
