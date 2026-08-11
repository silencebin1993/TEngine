# 《代谢切片》美术 AI 静图出图规范

> **用途：** 先用 AI 批量出 **静图 PNG**，再考虑动画。  
> **服从：** 《代谢切片-冻结总案-基元与美术》**v1.1**——命名 / ArtId / 尺寸以冻结总案为准；膜器/管壁已并入器官（仅 `org/`）。  
> **阶段：** v1 只交付静图；不动效、不要求真 3D 运行时资源。

---

## 0. 一句话

出的是 **俯视、低面数、透明底、剪影可读** 的「微距细胞零件贴纸」，不是卡牌立绘、不是法术图标、不是写实电镜照片。

---

## 1. 先做什么（顺序冻结）

| 优先级 | 内容 | 数量目标 | 备注 |
|--------|------|----------|------|
| **P0** | 风格锚点（Style Anchor） | 1 张主锚 + 可选 2 张辅锚 | **全部后续图必须参考此锚** |
| **P1** | 槽底板 `slot/*` | 6 | cytoplasm / membrane / peri / secretory / acidfen / crystal |
| **P1** | UI：`slot_empty` `slot_locked` | 2 | |
| **P2** | 器官英雄 8 | mito / emitter / vacuole / lens / scatter / swell / flagella / lyso | 先这 8 |
| **P3** | 其余器官 | 补满 org 表（含 spine/slime/receptor/insulate/valve/filter） | 原膜器/管壁已并入 org |
| **P4** | 基因 11 | | |
| **P5** | 地砖 / 残留 / 试剂 / 卡框 | 按冻结总案 §8.4 | **无** 独立 mem/、edge/ 目录 |

**本阶段禁止：** 角色全身、场景大图、法阵、文字 UI、动图 GIF 当主交付。

---

## 2. 统一视觉宪法（每张图必须遵守）

### 2.1 风格关键词（正向）

```
low-poly microscopic organelle, flat geometric facets, soft subsurface scattering,
petri-dish specimen, top-down orthographic view, readable game UI icon,
clean silhouette, limited color palette, soft top lighting, transparent background
```

中文等价（给只吃中文的工具）：

```
低面数微距细胞器，几何切面，培养皿标本感，正交俯视，游戏UI可读图标，
清晰剪影，有限色板，顶光，透明背景
```

### 2.2 负向词（尽量每张都加）

```
text, watermark, logo, frame, card border, magic rune, fantasy staff, pentagram,
realistic electron microscope photo, noisy texture, blurry, isometric character,
side view, three-quarter portrait, hands, face, full body, busy background,
glowing aura overload, particle spam, drop shadow under whole icon (optional ban)
```

### 2.3 相机与构图（死规定）

| 项 | 规定 |
|----|------|
| 视角 | **正交俯视**（top-down orthographic），禁止侧视/3/4 人像角 |
| 主体占比 | 主体占画布 **60%–75%**，四周留透明边，勿贴边 |
| 光源 | **顶光微距**，阴影短而软；全库同一光向 |
| 面数感 | 可见 **4–12 个大色块切面**；禁止照片级噪点 |
| 背景 | **必须透明**（或纯色后期抠；交付只要透明 PNG） |
| 文字 | **禁止**任何字母/汉字/数字刻在图上 |

### 2.4 色板（Tag 可读）

底调：青灰胞质感（低饱和）。高亮按化学语义：

| 语义 | 主色倾向 | 用于 |
|------|----------|------|
| 能量 / Source | 暖黄琥珀 | mito、蓄能高亮区 |
| 热 / Fire | 橙红 | perox、`_hot` 变体 |
| 湿 / Water | 青蓝 | aqua、潮相关 |
| 酸 | 黄绿 | acidfen、lyso 暗示 |
| 电 / Shock | 蓝紫 | ion |
| 光 / Light | 浅绿金 | chloro |
| 中性结构 | 青灰白 | 膜、管道、晶格 |

同一张图主色 **≤3 色族**，避免彩虹杂烩。

### 2.5 与 3D 战场的衔接（静图阶段就要埋）

- 造型按「可被低面 3D 复刻」设计：大块面、硬边、少雕饰  
- 全库同一俯视焦距；以后可从 3D 回烘替换，**剪影尽量可替换**  
- 不要画成只有正面才好看的立绘

---

## 3. 分类出图规格

### 3.1 器官 `org/{id}.png`（主交付）

| 项 | 值 |
|----|-----|
| 尺寸 | **512×512** PNG，RGBA |
| 内容 | 单个器官，居中，透明底（含原膜缘器官、管器官） |
| 可选变体 | `org/{id}_hot.png`（同构图，偏红/过热） |
| 槽内用 | 引擎侧缩小；或另导出 **128×128** 清晰版 `org/{id}_slot.png`（可选） |

**剪影验收：** 缩到 64×64 仍能认出「不是别的器」。

**角色暗示（画在形体上，不靠文字）：**

| 角色 | 视觉暗示 |
|------|----------|
| Source | 内核亮、向外辐射块面 |
| Relay | 通道/腔体/分叉感 |
| Transform | 透镜、裂口、环形运动暗示 |
| Sink | 开口朝外/喷射口/刺列 |
| Edge（管器官） | 短管/阀段造型，仍出在 org/ |

膜缘限定器官（spine/slime/receptor）可偏「边饰」构图，程序旋转四方。  
**禁止**再使用 `mem/`、`edge/` 目录。

### 3.2 槽位 `ui/slot/{id}.png` 与 UI

| 资源 | 尺寸 | 要求 |
|------|------|------|
| 槽底板 | **256×256** | 俯视方格或扁菱；类型靠轮廓+色；**不要**画上器官 |
| `ui/slot_empty.png` | 256×256 | 淡、可投放感 |
| `ui/slot_locked.png` | 256×256 | 锁定/禁飞 |

空槽与有内容的区分靠程序叠器官图；底板保持简洁。

### 3.3 基因 `gene/{id}.png`

- **512×512**，质粒 / DNA 环 / 几何基因芯片感  
- 比器官更「符号」，但仍禁止符文魔法阵  
- 主色点题（燃=红、潮=蓝、雷=紫…）

### 3.4 其他（P5 再开）

按冻结总案：地砖 128 tileable、残留 256、试剂 512、卡框 512×768。  
卡框可后做；**卡面主图直接复用 org/gene**，不要为卡单独重画一套立绘。

---

## 4. 提示词模板（直接复制）

### 4.1 风格锚点（只做一次）

```
Game UI icon, single low-poly microscopic cell organelle specimen,
flat geometric polygonal facets, soft top light, orthographic top-down,
centered, transparent background, cyan-gray cytoplasm tint, readable silhouette,
petri dish aesthetic, no text, no frame, no magic runes
```

出图后 **锁定 1 张** 为 Style Reference / 垫图，后续全部带上。

### 4.2 单器官模板

```
[STYLE REF], orthographic top-down, transparent background,
low-poly [器官英文/形态描述], [1句功能形态],
palette: [主色], soft subsurface, clean silhouette,
game inventory icon, no text, no card border, no side view
```

**实例 — 线粒体 `org/mito`：**

```
[STYLE REF], orthographic top-down, transparent background,
low-poly mitochondria organelle, oblong with inner geometric cristae folds,
warm amber energy core, cyan-gray shell, soft top light,
readable game UI icon, no text, no frame, no magic
```

**实例 — 晶状聚焦 `org/lens`：**

```
[STYLE REF], orthographic top-down, transparent background,
low-poly crystalline lens organelle, concentric polygonal rings focusing to a bright center,
cool glass cyan and white, beam-ready silhouette, soft top light,
game UI icon, no text, no frame
```

**实例 — 纺锤散射 `org/scatter`：**

```
[STYLE REF], orthographic top-down, transparent background,
low-poly spindle scatter organelle, central body with 3-4 geometric split nozzles,
split motif, limited teal-orange accents, clean silhouette,
game UI icon, no text, no frame
```

### 4.3 槽底板模板

```
[STYLE REF], orthographic top-down tile, 256 style square cell culture well floor,
low-poly [槽类型形态], empty slot plate only, no organelle on top,
muted [色], subtle border, transparent outside, game UI, no text
```

### 4.4 过热变体

对已通过的正常图做 **img2img / edit**，低改动：

```
same composition and silhouette, overheated variant, warmer red-orange rim light,
slightly more emissive core, keep shape identical, transparent background
```

---

## 5. 文件与命名（交付纪律）

```text
art/
  _anchor/style_anchor.png          # 风格锚点（不进游戏也可）
  org/{id}.png                      # 全部器官（含 spine/insulate 等）
  org/{id}_hot.png                  # 可选
  org/{id}_slot.png                 # 可选 128
  gene/{id}.png
  ui/slot/{id}.png                  # 与冻结 ArtId 对齐
  ui/slot_empty.png
  ui/slot_locked.png
```

- 文件名 = ArtId **最后一段**；小写；禁止空格  
- **禁止** `mem/`、`edge/` 目录  
- 只交 **PNG RGBA**；不要 JPG 当主资源  
- 一张图一个主体；禁止拼贴四宫格交付

---

## 6. 验收清单（过才进库）

每张图勾完再改名入库：

- [ ] 透明底干净（无灰底、无棋盘格烤进图）  
- [ ] 俯视，不是侧视/半侧  
- [ ] 无文字、无卡框、无法阵  
- [ ] 缩到小图标仍可辨  
- [ ] 与风格锚点同一光感与面数语言  
- [ ] 主色 ≤3 族，符合 Tag 色板  
- [ ] 文件名 = 冻结 ArtId  

**不合格常见原因：** 写成卡牌插画、背景培养皿画太满抢主体、发光粒子糊剪影、角度不统一。

---

## 7. 推荐工作流（人 + AI）

```text
1. 出风格锚点 → 锁定
2. 按 P2 英雄 8 器逐张文生图（都垫锚点）
3. 人工筛选：每 Id 留 1 张主图
4. 统一过一遍抠图/裁边（主体居中、边距一致）
5. 需要则 img2img 出 _hot
6. 批量检查 64px 可读性
7. 再铺 P3–P5
```

工具不绑定：Midjourney / Flux / SD / Ludo 均可，**锚点一致比工具重要**。  
动画（sprite sheet）等静图库稳定后再做，不在本规范范围。

---

## 8. 首批必出清单（复制打勾）

**锚点**

- [ ] `_anchor/style_anchor.png`

**槽**

- [ ] `cytoplasm` [ ] `membrane` [ ] `perinuclear` [ ] `secretory` [ ] `acidfen` [ ] `crystal`  
- [ ] `slot_empty` [ ] `slot_locked`

**器官 P2**

- [ ] `mito` [ ] `emitter` [ ] `vacuole` [ ] `lens`  
- [ ] `scatter` [ ] `swell` [ ] `flagella` [ ] `lyso`

---

## 9. 给生图 AI 的短指令（可整段粘贴）

```text
按《代谢切片美术AI静图出图规范》出静图。
先做 1 张 style anchor（低面数、正交俯视、透明底、培养皿标本感），锁定后所有图必须参考。
然后按 P1 槽底板 + P2 八个器官出 PNG。
禁止文字/卡框/法术符文/侧视立绘。
命名与尺寸严格按规范；每张附带是否通过 §6 验收。
```

---

*与冻结总案冲突时，以冻结总案的 ArtId / 尺寸 / 种类表为准；本文只规范「怎么用 AI 把静图画对」。*
