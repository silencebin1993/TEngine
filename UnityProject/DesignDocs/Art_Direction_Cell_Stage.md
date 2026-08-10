# 《文明织造》美术选型文案（细胞阶段优先）

> 版本：**v1.3**  
> 定稿日期：2026-08-05（v1.0） / 管线：2026-08-05（v1.1） / 提示词与资产清单：2026-08-06（v1.2） / 材质 LookDev：2026-08-10（v1.3）  
> 定稿方式：全部采纳原文推荐项  
> 依据：`Cell_Stage_Spec.md`、`GDD_Starter_Pack.md` §13、`Game_Framework_Design.md`、`CellEnemy.visualId`  
> 地位：细胞阶段美术基线；后续 Art Bible / 白模 / 部件表以此为准  
> **v1.1**：锁定 **Cursor 文生图 → Tripo 图生 3D → Unity 表现**；拆开几何原画与半透明表现  
> **v1.2**：提示词专为 **Cursor Generate Image · 仅 `auto` 模型**；补全战场可出现 mesh 清单；英雄资产强制多视图  
> **v1.3**：运行时材质定为 **阳光培养皿 · BioGlass + 吉卜力鲜艳生命色**（详见 `Material_LookDev_BioGlass.md`；shader `BinGames/SimBioGlass`）

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

**v1.3 色彩修正**：在半透明 BioGlass 之上采用吉卜力式**高饱和生命色**（蜜黄/草绿/珊瑚/天蓝）；污染与残块**降饱和脏色**对比。膜边液体扰动仅在单 Pass shader 解析实现，禁止 GrabPass。完整契约见 `Material_LookDev_BioGlass.md`。

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
【资产管线】 Cursor 几何原画（仅 auto）→ Tripo → Unity 半透明（v1.2）
```

---

## 11. 下一步

1. ~~升 v1.0~~ **已完成**  
2. ~~Cursor / Tripo 管线与提示词~~ **v1.1–v1.2 已写入 §12**  
3. 补一页 **Visual Identity Statement**（一句话视觉法则 + 3 条原则）→ 可写入正式 Art Bible  
4. ~~玩家基底 + 六路线签名~~；**v1.2 扩到敌人原型 / 精英 / 首领三阶段 / 残块**（见 §12.8 与 `registry.json`）  
5. Tripo 导出后：登记 `mesh` 路径，再进 Unity 跑白模三关（§8.3）  
6. 开 **部件表**（槽位 × 路线 × 稀有度），与 Luban/内容表对齐  
7. 玩家基底绑骨：按 `Blender_Cell_Rig_30min.md`（3 骨：Root / Core / MawTip；小怪不绑）  

---

## 12. Cursor 文生图提示词（仅 `auto` · Tripo 几何原画）

> **模型硬规则：只用 Cursor Generate Image 的 `auto`。禁止切 Flux / GPT Image / Ideogram 等付费档。**  
> 用法：把下面「整段英文」一次贴进生成框；`aspect_ratio = 1:1`。  
> 目标不是好看插画，而是 **让 Tripo 抽准闭合体积**。半透明湿润只在 Unity shader 做。

### 12.0 `auto` 模型专用写法（必读）

`auto` 对长否定列表与抽象词不敏感，按这六条写：

1. **第一句定相机**：写死 `three-quarter view, camera slightly above eye level`——禁止只写 `orthographic` / `top-down`（顶视会压扁，Tripo 出饼）。  
2. **材质用玩具词**：`opaque matte plasticine clay toy`，不要写 glass / jelly / wet / translucent / microscope。  
3. **一次一物**：禁止「细胞 + 六件外挂」；部件与敌人分图。  
4. **薄结构加粗**：鞭毛/触须/刺 `at least 8% of body width`；孢子泡必须 `fused / stuck`。  
5. **英雄资产出多视图**：玩家基底、首领优先分四次生成 front/left/back/right；四宫格偶发被安全策略拦截。模块/小怪用单张 3/4。  
6. **提示词禁写品牌**：不要写 `Tripo` / `image-to-3D` / `3D scanning`——`auto` 会把它们画成画面字母水印。改说 `clay toy prop` / `product photo`。

### 12.1 万能前缀 / 后缀（每次必带）

**Prefix：**

```text
Product photo of ONE solid clay toy prop.
Three-quarter view, camera slightly above eye level, subject fills 70% of frame, fully visible not cropped.
Opaque matte plasticine clay, closed smooth manifold volume, chunky stylized low-poly organic game asset.
Pure seamless light-gray background (#D8D8D8), soft even studio lighting, no hard shadows, high-contrast silhouette.
```

**Suffix：**

```text
No transparency, no glass, no jelly shine, no glow, no neon, no bloom, no particles, no smoke, no fog,
no floating debris, no text, no letters, no logo, no watermark, no UI, no frame, no collage,
no multiple separate objects, no cutaway, no cross-section, no photoreal microscope, no blood, no hair-thin filaments.
```

### 12.2 玩家基底（核/体）— 开局圆泡

**单视图（先看体积）：**

```text
[Prefix]
A chubby round single-cell creature body: fat soft sphere like a smooth ball of clay,
one opaque brighter coral core bump fused on the front surface (a raised dome, NOT a hole),
thick rounded membrane rim, no limbs, solid coral-pink matte clay, cute toy proportions.
[Suffix]
```

**四视图板（进 Tripo 优先用这张，裁成 front/left/back/right）：** 见 §12.4，主体描述同上。

### 12.3 六路线签名部件（单独生模）

**Devour · 大口膜（maw）：**

```text
[Prefix]
A devourer mouth module: thick rounded collar ring with one wide irregular bite opening in front,
soft folded lip ridges, solid deep wine-red matte clay, chunky silhouette,
opening is a shallow carved recess not a through-hole, no thin teeth.
[Suffix]
```

**Agile · 梭形鞭（motility）：**

```text
[Prefix]
An agile propulsion module: sleek spindle body with exactly 3 thick tapered flagella fused to the rear,
sharp forward tip, solid teal-green matte clay with pale painted edge ridges (not glowing),
each flagellum at least 8% of body width, one connected closed mesh.
[Suffix]
```

**Electric · 刺突冠（appendage）：**

```text
[Prefix]
An organic electric crown module for a microbe: short ring base with 6 thick stubby spikes
and 4 short tendril stubs fused outward, solid cerulean-blue matte clay,
pale yellow ridge lines painted as matte pigment (NOT glowing, NOT neon),
looks like a cell organ not an altar, all parts thick and closed.
[Suffix]
```

**Spore · 多泡簇（appendage）：**

```text
[Prefix]
A spore-cluster module: 6 bubble buds of mixed sizes fused onto one round mound base,
solid lavender matte clay, every bubble stuck to the base (none floating in air),
reads as a single connected mesh.
[Suffix]
```

**Nest · 菌毯锚（territory）：**

```text
[Prefix]
A nest-anchor module: low flat disc pad with short thick mycelium stubs on top
and two peg anchors underneath, solid olive-brown rough matte clay,
wide low silhouette, carpet texture painted on, closed solid form.
[Suffix]
```

**Corrupt · 破口棘壳（membrane）：**

```text
[Prefix]
A corrupt shell module: asymmetric cracked carapace plate with 3–4 thick black thorns,
solid sickly yellow-green matte clay, broken rim but still ONE closed solid piece (no holes through),
menacing chunky silhouette.
[Suffix]
```

### 12.4 多视图板（玩家基底 / 首领强制）

把 Prefix 换成下面整段（已含相机与布局），再接主体描述 + Suffix：

```text
Clean 2x2 turnaround sheet of the SAME single opaque clay toy on one image.
Top-left: front view. Top-right: left side view. Bottom-left: back view. Bottom-right: right side view.
Identical scale and even lighting in every quadrant, each copy centered, pure light-gray background,
no labels, no arrows, no perspective distortion, no perspective foreshortening, orthographic-looking views.
Absolutely no text, no letters, no logo, no watermark.
```

> 四宫格偶发被内容安全拦截时，改「分四次单视图」（front/left/back/right 各一张）更稳。  
> Tripo 多视图顺序：`front → left → back → right`。

### 12.5 敌人 / 精英 / 首领 / 残块（战场 mesh 全表）

原则：**按剪影做原型，同原型换色覆盖多个 `visualId`**（对齐 §8.1「8–12 外形原型」）。完整提示词见 §12.8；下表是对照。

| 资产 id | 覆盖 visualId / 用途 | 视图 |
|---------|----------------------|------|
| `enemy_blob_food` | 1 浮游食团 | 单 3/4 |
| `enemy_spiky_cell` | 2 刺膜细胞 | 单 |
| `enemy_cilia_sweeper` | 3 扫尾纤毛体；9 游隼纤毛（加长梭） | 单 |
| `enemy_hunter` | 4 追猎原虫 | 单 |
| `enemy_swarm_dot` | 5 噬菌群；18 电泳群体 | 单 |
| `enemy_hardshell` | 6 硬壳核胞；17 钙化巨壳（放大换色） | 单 |
| `enemy_jelly_conductive` | 7 导电水母体 | 单 |
| `enemy_spore_rot` | 8 腐败孢团；12 分裂酵母 | 单 |
| `enemy_spine_shooter` | 10 毒棘漂虫 | 单 |
| `enemy_mycelium_pad` | 11 簇生菌丝 | 单 |
| `enemy_sucker` | 13 虹吸口虫；19 寄生噬体 | 单 |
| `enemy_mimic_sac` | 14 拟态囊胞 | 单 |
| `enemy_acid_drop` | 15 游离酸滴 | 单 |
| `enemy_cannibal` | 16 吞噬同族 | 单 |
| `enemy_bomb_seed` | 20 灾变胚种 | 单 |
| `enemy_corpse_chunk` | 21 组织残块 | 单 |
| `elite_devourer` … `elite_aggregate` | 50–57 八精英各一剪影 | 单 |
| `boss_prokaryote_p1/p2/p3` | 90 原核霸主三阶段 | **四视图板** |

### 12.6 Cursor 参数（`auto`）

| 参数 | 值 | 原因 |
|------|----|------|
| 模型 | **仅 `auto`** | 费用；其它档禁用 |
| 比例 | `1:1` | Tripo 友好 |
| 风格词 | `plasticine clay toy` | 压写实与玻璃 |
| 一次一物 | 强制 | 深度不打架 |
| 卡面/宣传 | **另开提示词** | 半透明插画污染 Tripo |

### 12.7 Tripo 侧参数

| 项 | 建议 |
|----|------|
| 输入 | PNG ≥1024；主体居中；四视图先裁齐 |
| 单图 / 多图 | 模块与小怪单图；基底/首领多视图 |
| `smart_low_poly` / `quad` | true |
| `texture_alignment` | `geometry`（要形准） |
| 导出 | FBX/GLB → Blender 封洞清碎件 → Unity |

### 12.8 完整生图提示词目录（复制即用）

下列每条 = Prefix 主体句 + 已嵌入关键形状；生成时仍须前后各贴 §12.1 Prefix/Suffix（四视图资产改用 §12.4 头）。

**普通敌人原型**

```text
# enemy_blob_food (visualId 1)
A tiny soft food blob microbe, fat irregular round lump, solid warm beige matte clay, no limbs, cute edible look.

# enemy_spiky_cell (2)
A spiky defensive cell, round body with 8 short thick spikes fused outward, solid magenta matte clay, spikes stubby.

# enemy_cilia_sweeper (3/9)
A horizontal sweeper microbe, flattened oval body with a row of 5 thick cilia paddles along one side, solid cyan matte clay.

# enemy_hunter (4)
A hunter protozoan, streamlined teardrop body with two short thick antenna stubs, solid orange-red matte clay, aggressive silhouette.

# enemy_swarm_dot (5/18)
A tiny swarm microbe, simple plump oval with one small ridge, solid electric-blue matte clay, readable as a pack unit.

# enemy_hardshell (6/17)
A hard-shell nucellus, thick armored sphere with plate seams painted on, solid slate-gray matte clay, heavy chunky volume.

# enemy_jelly_conductive (7)
A conductive jellyfish-blob, round dome body with 4 thick short tentacle stubs fused under, solid pale blue matte clay, tentacles stubby not hair-thin.

# enemy_spore_rot (8/12)
A rotting spore clump, lumpy multi-bulb mass fused together, solid purple-brown matte clay, one connected piece.

# enemy_spine_shooter (10)
A spine shooter microbe, round body with one thick forward cannon spike, solid green matte clay, chunky.

# enemy_mycelium_pad (11)
A stationary mycelium pad, low wide star-shaped mat with short stubs, solid forest-green matte clay, flat silhouette.

# enemy_sucker (13/19)
A sucker mouth parasite, chubby body with one thick funnel mouth fused in front, solid rust matte clay.

# enemy_mimic_sac (14)
A mimic food sac that looks almost like the beige food blob but with a hidden seam ridge, solid warm beige matte clay with one dark painted seam.

# enemy_acid_drop (15)
A free acid drop, smooth teardrop droplet shape standing upright, solid lime matte clay, closed volume.

# enemy_cannibal (16)
A cannibal microbe, oversized round body with a wide shallow maw recess, solid deep red matte clay, bulkier than hunter.

# enemy_bomb_seed (20)
A catastrophe seed, cracked egg-like oval with thick ridge lines, solid charcoal matte clay with sickly yellow painted cracks (not glowing).

# enemy_corpse_chunk (21)
A torn tissue chunk, irregular meaty lump with soft folds, solid dull pink-brown matte clay, single closed prop.
```

**精英（50–57）**

```text
# elite_devourer (50)
An elite macro-devourer: huge fat sphere with oversized shallow maw and thick collar, solid dark wine matte clay, bossy silhouette.

# elite_whip_king (51)
An elite whip-king: long spindle body with 4 very thick rear flagella, solid teal matte clay, high-speed hunter look.

# elite_volt_hunter (52)
An elite volt hunter: armored dome with 6 thick spike stubs and painted yellow ridge lines (matte pigment not glow), solid cerulean clay.

# elite_spore_mother (53)
An elite spore mother: large central bulb with 8 attached smaller bulbs fused on, solid lavender matte clay, all stuck together.

# elite_gatekeeper (54)
An elite nest gatekeeper: wide fortified disc with two thick pillar stubs, solid olive-brown matte clay, immovable look.

# elite_molt_hunter (55)
An elite molt hunter: crab-like oval with a cracked outer shell plate and inner smoother core showing, solid amber matte clay, still one piece.

# elite_siphon_brain (56)
An elite siphon brain: large brainy lobe body with three thick sucker tubes fused forward, solid purple matte clay.

# elite_filth_aggregate (57)
An elite filth aggregate: cluster of 5–6 fused lumpy blobs into one mass, solid sickly yellow-green matte clay, can read as breakable cluster but still connected.
```

**首领三阶段（各出一张 §12.4 四视图板）**

```text
# boss_prokaryote_p1 增殖
A giant prokaryote boss phase 1: massive round core with several budding child spheres fused on the surface, solid deep indigo matte clay, intimidating toy boss.

# boss_prokaryote_p2 极化
Same giant prokaryote boss phase 2: massive round core with asymmetric thick plate armor halves (one side ridged, one side smooth), solid indigo clay with pale yellow matte painted seams (not glowing).

# boss_prokaryote_p3 崩坏
Same giant prokaryote boss phase 3: massive aggressive teardrop hunter body, cracked shell ridges, forward maw recess, solid near-black indigo matte clay with sickly yellow cracks painted on.
```

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
| 费用：只用 auto | §12 硬规则 |

---

*本文为细胞阶段美术基线。视觉承诺不变；v1.2 优化 `auto` 提示词并补全战场 mesh 生图目录。若要改已定画风/造物项，开变更说明并升版本号（v1.3+）。*
