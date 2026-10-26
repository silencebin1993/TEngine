# FG19B 物质与反应表（FG19 附录）

> 版本：0.2
> 状态：草案 · 2026-10-23 用户已同意方向 · 第 4 步（M9 完成并合并云端 M10 当天）起生效；生效前工人不得按本文施工
> 上位文档：[FG19](FG19_Emergence_Channels.md)。端口、事件、物质 ID、表名、产物封闭清单、调参前缀一律以 FG19 为准，本附录不另起命名；需要的新取值集中在第 9 节，16 条已于 2026-10-26 全部并入 FG19。姊妹附录：FG19A 固件与部件通道映射（同事并行编写，文件名以 `FG19A_` 开头）。
> 编号：需求前缀沿用 `FGR-EMG`。本附录不新开需求号，条款按“FG19B §节号”引用，进卡片时引用 FG19 的需求号。测试沿用 `FGT-EMG-030～037`（第 11 节给具体断言），旅程沿用 `FGJ-EMG-2`。
> 来源提案：[DESIGN.md](../../../../production/design/build-variety-theme-fit/DESIGN.md) 提案 A（第 2 节）、提案 F（第 5.6 节）。事实底稿：[research/](../../../../production/design/build-variety-theme-fit/research/) 下 02（反应与标签，全文）、03（§4B 内核事件、§8 地形、§9 实体）、05（§4 云端 M10）。提案与底稿冲突处以底稿为准。
> 阶段：除标“阶段 4”的行外，全部属于 FG-EMG 阶段 2“世界”（FG19 第 12 节）。
> 契约：[FG00](FG00_Completeness_Addendum.md)、[00 契约](../detailed/00_Implementation_Completeness_Contract.md)。

---

## 一页速览

1. 17 种物质的属性、存续、上限、接触效果与视觉见第 1 节。现有液洼、燃烧区、油迹、喷淋池、酸沼、陷阱场地全部有映射（§1.5）。
2. 交互规则共 61 行（`fg.TbChannelRule`），分 7 类：物质 × 物质 18 行、物质 × 能量 20 行、事件 × 物质 8 行、沾染 × 物质 / 能量 8 行、相态 × 能量 3 行、属性 × 场 2 行、物质 × 实体 2 行。完整矩阵中的空格都写了“无”，并注明是有意留白还是待定（§2）。
3. 现有 16 条具名反应、2 条装配反应全部去倍率：每条的主结果都属于四类之一，**全表不使用 `side.mult`**，破甲 / 穿甲也不当主结果（§4）。标记跳转不设规则行，由固件自己的条件连锁计入图鉴（§4.1 A1）。云端 REACT-01 的 4 条、FG11 计划的 4 条都在这 16 条之内，没有额外反应。
4. 新增 12 个具名交互进图鉴：阶段 2 有浮油、冰封、冲淡、淬火、蒸干、焦烟、尘爆、冷却蒸腾、磁扫；阶段 4 有焦土、熔坑、冻结桥。连同原来 18 条，共 30 个图鉴条目。
5. 状态位：新沾染只用 26～29 四位，32 位够用，前提是相态状态另存，不占 `Status` 位（§3.2）。
6. 物质斑块上限 1024 = 世界源保留 96 + 17 种物质各自软上限合计 928。每步规则结算 ≤ 32 条、斑块评估 ≤ 69 块（§8）。

---

## 0. 读法与约定

### 0.1 本附录负责什么

- 负责（FGR-EMG-020 FG19B 部分）：
  - `fg.TbMatter` 17 行；
  - `fg.TbChannelRule` 全部行，包括 16 条现有具名反应的改写、熔穿过载的端口化（标记跳转改由 FG19A 固件声明承接，只在本附录登记图鉴计数口径），以及新增规则；
  - `fg.TbPhaseRule` 7 行；
  - `fg.TbFieldCarrierRule`；
  - `fg.TbEntityProfile`：材质与实体的默认属性、载体动能系数、破片参数；
  - 状态位分配；
  - 敌方 / 中立场地迁移的行为对照；
  - 物质层容量与每步预算的需求侧上限。
- 不负责：
  - 固件、部件、底盘、敌方单位的读写声明与幅度（FG19A）；
  - 内核数据结构、扩容方式、快照布局与实测预算（ADR-EMG-001）；
  - Story 卡片（[build-variety-theme-fit/](../../../../production/design/build-variety-theme-fit/) 目录另一份文件）。
- 本附录写到固件 ID、单位 ID，只是为了说明“谁能当来源”。**规则表本身不出现任何内容 ID**（FGR-EMG-013）。

### 0.2 路径简写

同 FG19 §0.1：`GL/`、`Sim/`、`tools/`、`origin:`。行号为本机 TEngine `37d6ee21` 与根仓本机版本；`origin:` 为云端 `origin/projecta/0.2-fullgame` 版本，本机 M9 完成前不合并，行号会偏移，按符号找。

### 0.3 单位与口径

| 量 | 单位 / 口径 |
|---|---|
| 份量 | 份。1 份 = 1 平方米标准厚度的物质：斑块面积（m²）= 份量，半径 = √(份量 / π)。合并时份量相加，即面积守恒（FGR-EMG-082） |
| 浓度 | 只用于气体层：浓度 = 份量 / 当前面积。生成时为 1.0，扩散后下降，低于 `emg.gas.min_conc`（0.2）时消失 |
| 温度 | 度；常温 `emg.heat.ambient` = 20 |
| 热量 | 与现有武器积热同一单位（例：熔穿过载每发 65 = 基础 40 + 25，底稿 02 §3.3） |
| 累积量 | 7 个相态累积量统一归一到 0～1，进入阈值 1.0，与云端受寒 0～1 同口径（origin: `Sim/Combat/CombatTypes.cs` 中 `CombatClarityUnit` 注释，约 `:578-613`） |
| 电荷 | 斑块与实体同一单位：注入 1.0 = 让零抗性单位的电荷从 0 到满 |
| 动能 | 单次命中动能 = 伤害 × 载体动能系数（§6.3）；撞击动能 = 质量 × 速度² × 0.05（FGR-EMG-093） |
| 强度属性 | 0～1（高 0.8～1.0、中 0.5、低 0.2、无 0） |
| 数值 | 全部是初值，由 FG15-BAL-01 根据模糊测试器与试玩数据调整。所有附带结果都是 `side.damage` 固定伤害或“无”，不使用 `side.mult` |

### 0.4 表与列约定

- 数据源：新文件 `tools/cell_tables/fgdata_matter.py`（本附录的 5 张表 + 本附录的 `emg.*` 调参），由 `gen_all.py` 汇总；FG19A 的数据进 `tools/cell_tables/fgdata_emg.py`（ADR-EMG-001 ①，原建议名 `fgdata_emg_matter.py` 作废）。新数据源文件一律写全路径。
- `tools/fgdata_reaction.py` 的 `_R`、`_A`（`:60-103`）保留为“具名条目”来源（ID、名字键、图鉴）。倍率列在阶段 2 退役（`damageMult` 一律读作 1.0）；删列还是留空由 ADR-EMG-001 决定。
- `fg.TbChannelRule` 各列（FGR-EMG-020 第 2 条）在本附录里的约定：
  - `id`：行 ID，`reaction_<英文短名>`。现有 17 条（16 条具名 + 熔穿过载）原样保留；`reaction_marktag` 没有规则行，只作 `named` 图鉴条目（§4.1 A1）。
  - `stableKey`：生成器对 `id` 计算 FNV-1a 32 位，算法与 `NamedReactionCatalog.StableKey` 相同（`GL/Campaign/Content/NamedReactionCatalog.cs:157-170`），表里不手填。`id` 不变，稳定键就不变。
  - `named`：本行计入哪个图鉴条目，填某个 `reaction_*`，不进图鉴的填 `none`。同一个具名交互可以有多行（例：水斑块、冷却液斑块都能“短路”）。触发次数与首次发现按 `named` 的稳定键记，因此现有 16 条的存档计数键不变（FGR-EMG-086）。
  - `pairKind`、`a`、`b`：只填端口 ID（FG19 §2.4～2.7）或沾染位名。`condition` 只读端口量、斑块 / 实体字段和 `fg.TbEntityProfile` 列。
  - `mainKind` ∈ {S, T, B, P}；`product` 只能取 FG19 §2.9 的封闭清单。
  - `params`：产物参数。其中“伴生”一项也只能取封闭清单（第 9 节第 3 条）。
  - `consume`：消耗；`side`：`side.damage N` 或 `none`。
  - `cost.heat`：开火者代价（热量），本行在开火时可判定成立时记给开火者，**取代**相关指令的表值热量，不叠加。全表只有 P03 填 25（§4.1 A2），其余行填 0（第 9 节第 16 条）。
  - `priority`：同一斑块 / 单位同时满足多条时，从小到大只结算一条，其余留给下一个节拍（FGR-EMG-084）。
  - `stage`：生效阶段。
  - 文本键：现有 16 + 2 条沿用 `reaction.<短名>.name`；`.desc` 的值在第 4 步按新主结果重新生成（现生成器 `_desc` 写死“×倍率”，`tools/fgdata_reaction.py:118-133`）。新行用 `emg.rule.<短名>.name/.desc`。

---

## 1. 物质清单与属性（`fg.TbMatter`）

### 1.1 属性总表

已与 FG19 FGR-EMG-080 的定性锚点逐格核对，方向全部一致。

| 码 | 端口 ID | 名称 | 层 | 可燃 | 导电 | 冷却 | 腐蚀 | 粘滞 | 遮蔽 | 湿滑 | 磁性 | 固态 | 反射 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | `mat.oil` | 油 | 地 | 0.9 | 0 | 0 | 0 | 0.2 | 0 | 0.8 | 0 | 否 | 0 |
| 2 | `mat.water` | 水 | 地 | 0（灭火由规则表达） | 0.9 | 0.5 | 0 | 0 | 0 | 0.2 | 0 | 否 | 0 |
| 3 | `mat.coolant` | 冷却液 | 地 | 0（同上） | 0.5 | 0.9 | 0 | 0 | 0 | 0.2 | 0 | 否 | 0 |
| 4 | `mat.cryo` | 液氮 | 地 | 0 | 0 | 1.0 | 0 | 0 | 0.2 | 0 | 0 | 否 | 0 |
| 5 | `mat.acid` | 酸液 | 地 | 0 | 0.8 | 0 | 0.9 | 0 | 0 | 0 | 0 | 否 | 0 |
| 6 | `mat.polymer` | 聚合胶 | 地 | 0.5 | 0 | 0 | 0 | 0.9 | 0 | 0 | 0 | 否 | 0 |
| 7 | `mat.cured` | 固化胶块 | 地 | 0.2 | 0 | 0 | 0 | —（固态） | 0 | 0 | 0 | 是：阻路、掩体 | 0 |
| 8 | `mat.molten` | 熔融金属 | 地 | 0（点燃源：1000 度热源） | 0.9 | 0 | 0.5 | 0.5 | 0 | 0 | 0.5 | 否 | 0 |
| 9 | `mat.slag` | 冷渣 | 地 | 0 | 0.5 | 0 | 0 | —（固态） | 0 | 0 | 0.8 | 是：低矮掩体 | 0 |
| 10 | `mat.fire` | 明火 | 地 | —（燃烧中） | 0 | — | 0 | 0 | 0.2 | 0 | 0 | 否 | 0 |
| 11 | `mat.smoke` | 烟 | 气 | 0 | 0 | 0 | 0 | 0 | 0.9 | 0 | 0 | 否 | 0 |
| 12 | `mat.steam` | 蒸汽 | 气 | 0 | 0.2 | 0（带热） | 0 | 0 | 0.5 | 0 | 0 | 否 | 0 |
| 13 | `mat.debris` | 金属碎屑 | 地 | 0 | 0.5 | 0 | 0 | 0 | 0 | 0.2 | 0.9 | 否 | 0 |
| 14 | `mat.dust` | 导电粉尘 | 气 | 0.5（粉尘爆燃） | 0.5 | 0 | 0 | 0 | 0.2 | 0 | 0.2 | 否 | 0 |
| 15 | `mat.ice` | 冰 | 地 | 0 | 0 | 0.5 | 0 | 0 | 0 | 0.8 | 0 | 是：可站立的桥面，受动能碎 | 0.2 |
| 16 | `mat.glass` | 玻化层 | 地 | 0 | 0 | 0 | 0 | 0 | 0 | 0.2 | 0 | 是：薄，可碎 | 0.8 |
| 17 | `mat.sludge` | 化工污泥 | 地 | 0.2 | 0.2 | 0 | 0.2 | 0.5 | 0 | 0 | 0 | 否 | 0 |

- 两处写法细化（不改方向）：
  1. 锚点里水、冷却液的“可燃 = 灭火”不做成属性值（可燃 0）。灭火由规则 M12、M13、C02 和接触效果“着火度清零”表达。
  2. 熔融金属的“可燃 = 点燃源”同理：可燃 0，靠 1000 度定温热交换点燃别的东西（§1.4 第 2 条）。
- 传导图节点（FGR-EMG-091，强度 ≥ 0.3）：水、冷却液、酸液、熔融金属、冷渣、金属碎屑、导电粉尘。蒸汽与化工污泥导电只有 0.2，不进传导图。因此淤塞（M14）把水变成污泥，就等于把这片电网断开。
- 题材口径沿用 FG19 FGR-EMG-080 末条：烟、蒸汽、碎屑、熔融金属、冰、玻化层、固化胶块只能由规则产生，固件不能凭空排放。

### 1.2 存续、衰减、上限与接触效果

“接触效果”按每秒计：单位站在斑块里、或在沾染保留期内都生效，每个接触节拍（0.25 秒）结算 1/4。数值沿用现有标签效果（`tools/fgdata_reading.py:271-276` `STATUS_EFFECTS`）和云端澄净参数，所以迁移本身不改数值。

| 物质 | 默认存续 | 衰减方式 | 扩散 | 沾染位 / 保留秒 | 初始温度 | 耐久 | 接触效果（每秒） | 软上限 | 合并阈值 |
|---|---|---|---|---|---|---|---|---|---|
| 油 | 20 秒 | 蒸干 | 0 | 1 油污 / 3 | 20 | — | 无伤害；湿滑：被推距离 ×1.8、转向 −30% | 88 | 0.5 |
| 水 | 15 秒 | 蒸干 | 0 | 2 浸湿 / 3 | 20 | — | 武器散热 ×1.5；`phs.overheat` 衰减 ×4；着火度清零（灭火）；腐蚀累积 ×0.5 | 64 | 0.5 |
| 冷却液 | 12 秒 | 蒸干 | 0 | 2 浸湿 / 3 | 10 | — | 同水，散热 ×2 | 72 | 0.5 |
| 液氮 | 6 秒 | 吸热蒸发 | 0 | 26 霜冻 / 2 | −196（定温） | — | `phs.chill` +0.6（等于云端液氮池：每 0.5 秒节拍 0.6 × 0.5）；着火度清零 | 48 | 0.5 |
| 酸液 | 12 秒 | 蒸干 | 0 | 6 腐蚀 / 3 | 20 | — | `phs.corrode` +0.25；持续伤害 3（沿用 `Acid` 每秒 3） | 72 | 0.5 |
| 聚合胶 | 15 秒 | 蒸干 | 0 | 14 胶膜 / 3 | 20 | — | 移动 −30%（沿用 `SugarFilm` 0.3）；弹体减速见 §5 | 48 | 0.5 |
| 固化胶块 | 野外 30 秒；家园 / 前哨长期留存 | 保持 | 0 | — | 20 | 120 | 阻路：不可进入；挡弹：吸收，扣耐久 | 40 | 不合并 |
| 熔融金属 | 10 秒 | 冷却成冷渣 | 0 | 27 熔渣 / 2 | 1000 | — | `en.heat` 30；`phs.ignite` +0.5；持续伤害 6 | 32 | 0.5 |
| 冷渣 | 野外 60 秒；家园 / 前哨长期留存 | 保持 | 0 | — | 新生时等于凝固温度，之后回到常温 | 80 | 移动 −30%；低矮掩体（§5 `fieldrule_solid_absorb`） | 48 | 不合并 |
| 明火 | 6 + 0.3 × 份量 秒（≤ 20） | 燃尽 | 0（靠热交换引燃相邻可燃物） | 0 燃烧（只作显示，见 §3.1） | 800（定温） | — | `phs.ignite` +0.8 × (0.3 + 实体可燃)；`en.heat` 20；燃烧时每 2 秒向气体层排 1 份烟 | 96 | 0.5 |
| 烟 | 8 秒 | 扩散稀释 | 半径 +0.3 米 / 秒 | 29 遮蔽 / 0.5 | 60 | — | 遮蔽 0.9：失锁（§5 `fieldrule_obscure_unit`） | 56 | 0.3 |
| 蒸汽 | 5 秒 | 扩散稀释 | +0.4 米 / 秒 | 29 遮蔽 / 0.5 | 100 | — | 部分遮蔽；温度 ≥ 80 时 `en.heat` 5 | 56 | 0.3 |
| 金属碎屑 | 45 秒 | 保持 | 0 | 28 碎屑 / 4 | 20（热碎屑保留温度） | — | 磁化累积 ×1.5；可拾取为阶段 4 预留（§1.4 第 13 条） | 64 | 0.5 |
| 导电粉尘 | 10 秒 | 扩散稀释 | +0.3 米 / 秒 | 12 扬尘 / 3 | 20 | — | 无伤害；遮蔽低，不失锁 | 40 | 0.3 |
| 冰 | 野外 25 秒；家园 / 前哨长期留存 | 保持 | 0 | — | −10 | 40 | 湿滑：被推距离 ×1.8、转向 −50%；`phs.chill` +0.1；可站立（覆盖水面时成桥，阶段 4，V08） | 40 | 不合并 |
| 玻化层 | 野外 60 秒；家园 / 前哨长期留存 | 保持 | 0 | — | 300，之后回到常温 | 20 | 反射（§5）；湿滑低 | 32 | 不合并 |
| 化工污泥 | 20 秒 | 蒸干 | 0 | 13 污染 / 3 | 20 | — | 持续伤害 2（沿用 `Poison` 每秒 2）；移动 −20%；`phs.corrode` +0.05 | 32 | 0.5 |

- “默认存续”只用于规则产物，以及没写时长的世界源。固件排放的斑块按 FG19A 声明的秒数；阶段 2 的等价迁移期沿用旧区域时长（如读法驻留区约 3 秒，`tools/fgdata_reading.py:143`）。
- 沾染保留秒默认 3，与现有 `reading.status_seconds` = 3.0 一致（底稿 02 §2.4）。敌方单位行如果自带状态时长（云端 `status_s`），以敌方行为准。

### 1.3 玩家名与视觉（形状纹样为主、颜色为辅，B15）

| 物质 | 中文 / 英文 | 文本键 | 纹样（主） | 颜色（辅） |
|---|---|---|---|---|
| 油 | 油 / Oil | `emg.port.mat.oil.name` | 斜向虹彩油膜条纹 | 黑褐 |
| 水 | 水 / Water | `emg.port.mat.water.name` | 同心波纹 | 蓝（沿用 `fg.TbFluid` 的水色 `#3C8DDC`，`tools/fgdata_pipes.py:25`） |
| 冷却液 | 冷却液 / Coolant | `emg.port.mat.coolant.name` | 十字格纹（沿用流体图标 cross） | 青绿 `#3FD0C0` |
| 液氮 | 液氮 / Liquid Nitrogen | `emg.port.mat.cryo.name` | 六角霜花 + 白雾边 | 白蓝 |
| 酸液 | 酸液 / Acid | `emg.port.mat.acid.name` | 菱形格（沿用流体图标 diamond） | 黄绿 `#A6D93A` |
| 聚合胶 | 聚合胶 / Polymer Gel | `emg.port.mat.polymer.name` | 蜂窝气泡 | 琥珀 |
| 固化胶块 | 固化胶块 / Cured Block | `emg.port.mat.cured.name` | 硬边实心块 + 裂纹耐久 | 深琥珀 |
| 熔融金属 | 熔融金属 / Molten Metal | `emg.port.mat.molten.name` | 发光裂纹流动 | 橙红 |
| 冷渣 | 冷渣 / Slag | `emg.port.mat.slag.name` | 粗颗粒堆 | 灰黑；热渣带暗红辉光 |
| 明火 | 明火 / Open Flame | `emg.port.mat.fire.name` | 三角火舌动画 | 橙黄 |
| 烟 | 烟 / Smoke | `emg.port.mat.smoke.name` | 体积烟团 | 灰 |
| 蒸汽 | 蒸汽 / Steam | `emg.port.mat.steam.name` | 卷云 | 白 |
| 金属碎屑 | 金属碎屑 / Metal Debris | `emg.port.mat.debris.name` | 散点碎片 | 银灰 |
| 导电粉尘 | 导电粉尘 / Conductive Dust | `emg.port.mat.dust.name` | 闪点微粒 | 黄褐 |
| 冰 | 冰 / Ice | `emg.port.mat.ice.name` | 冰裂纹 | 淡蓝 |
| 玻化层 | 玻化层 / Glazed Crust | `emg.port.mat.glass.name` | 斜向高光条 | 透明青 |
| 化工污泥 | 化工污泥 / Chemical Sludge | `emg.port.mat.sludge.name` | 气泡点 + 浑浊边 | 暗绿褐 |

- 说明文本键 `emg.port.mat.<名>.desc`，悬停句式沿用 FGR-EMG-181：“油 · 来自 A 车 · 遇明火会整片燃烧”。
- 首批特效与模型都是占位，标占位角标并登记 DEBT（B22）。出图类工作平时只写或改出图计划（jobs、方案、台账），不询问出图时机；用户明确指示当前工作是出图时，才按计划批量出图（2026-10-26 用户批准；执行约束见 FG19 第 11 节导语）。

### 1.4 通用机制（物质自身的属性，不是交互规则，不占规则行）

1. **沾染**：单位在斑块里时挂上该物质的沾染位，离开后保留“沾染保留秒”（FGR-EMG-083）。地面层斑块不沾染离地实体（`chs.airborne`：悬浮、飞行、无人机、云端空中单位），气体层斑块照常沾染。
2. **热交换**：每个传导节拍（`emg.conduct.tick` 0.25 秒），重叠的两块斑块按下式交换温度：ΔT_a = `emg.heat.exchange`（0.4）× 重叠比 × (T_b − T_a) × m_b ÷ (m_a + m_b)。
   - 重叠比 = 重叠面积 ÷ 较小面积；m = 份量。
   - 定温源（明火 800、液氮 −196、世界源斑块）视 m_b 为无穷大。
   - 地面层与气体层之间按 0.5 倍交换。
   - 例：油与明火重叠 0.3，每节拍升温约 94 度，两个节拍（0.5 秒）越过 180 度点燃阈值，触发 E01 爆燃。
3. **环境回温**：只对高于常温的斑块生效，每秒向 20 度回落 `emg.heat.cool_rate`（40 度）。熔融金属约 10 秒降到 600 度以下，转为冷渣（E11）。冰与液氮不靠环境回温，按自身存续结束。
4. **能量注入斑块**：`en.heat` 注入量 Q 使斑块升温 ΔT = Q × `emg.heat.patch_gain`（10）÷ 份量。`en.elec` 注入直接加斑块电荷。对单位注入的热同时进武器积热（现有）与结构积热 `phs.overheat`（Q ÷ `emg.heat.unit_overheat_scale` 300，FGR-EMG-092）。
5. **电的传导**（FGR-EMG-091）：
   - 每个传导节拍，从带电节点出发有界扩散：每跳 × 0.7 × 接收节点导电，最多 6 跳，低于 `emg.conduct.q_min`（0.05）停止，每节拍节点访问 ≤ `emg.conduct.max_visits`（256）。
   - 站在导电斑块里的单位是节点。单位与斑块之间互相传，单位从斑块分得 `emg.conduct.unit_share`（0.6）× 斑块电荷。
   - 斑块电荷每节拍 × `emg.conduct.patch_decay`（0.5）。
   - 半径 > 8 米或常驻的斑块，只在注入点 `emg.conduct.big_patch_radius`（8 米）内传导，防止整片酸沼一次电遍全场。
6. **磁化**：只存在于实体（单位、残骸、破片、弹体、哨戒桩）。物质斑块的磁性只通过“属性 × 场”规则起作用（F02 磁扫）和场 × 载体表（§5），不保存磁化量。
7. **动能与固态耐久**：固态斑块（固化胶块、冷渣、冰、玻化层）记“累计动能”。动能来源：
   - 撞击 `evt.impact`；
   - 二次起爆（`topo.burst`）；
   - 抛射弹落地（伤害 × 动能系数）；
   - 破片落点；
   - 底盘特性（履带碾过、多足震地，FG19 FGR-EMG-140）。
   累计动能 ≥ 耐久时由规则 E18～E20 结算。固化胶块碎了没有产物（有意：胶块碎了只剩地面），斑块结束并发 `evt.expire`。被弹体命中时，阻路 / 掩体类固态斑块扣耐久 = 伤害（`evt.absorb`）。
8. **遮蔽**：遮蔽 ≥ `emg.obscure.full`（0.7）时完全失锁；在 `emg.obscure.partial`（0.4）到 0.7 之间时部分失锁（锁定距离上限 6 米）；低于 0.4 无效果。细则见 §5 `fieldrule_obscure_unit`。
9. **湿滑 / 粘滞**：湿滑放大击退、牵引、推铲造成的位移与转向惯性；粘滞降低移动速度与弹体速度。两者都是物质属性，不发事件。
10. **气体扩散与场推动**：
    - 气体层斑块每秒半径增加“扩散”值，份量不变、浓度下降，到 0.2 以下消失。
    - 推斥 / 牵引 / 涡旋场把气体斑块整体推动；涡旋把粉尘往中心收拢，浓度回升，可以聚到尘爆浓度。
    - 这一条要把 FG19 FGR-EMG-112 的“6 种实体”扩到气体斑块，已并入 FG19 FGR-EMG-112（第 9 节第 4 条）。
11. **常驻世界源斑块**（酸沼、首领灌注、管线液洼）：
    - 不被规则转化或消耗，不衰减，不合并，按定温参与热交换，在规则里视为份量无穷大；
    - 与它重叠的普通斑块照常按规则结算，产物生成在重叠区；
    - 首领灌注、管线液洼占世界源保留额（§8.1）；
    - 酸沼实现为**虚拟世界源**（ADR-EMG-001 ⑤、⑭ 第 13 条）：不实体化为斑块、不进空间哈希、不占斑块槽，按“点是否在危害区内”查询；涉及酸沼的规则（通电、电解、固化、乳化）只在注入点 8 米内求值。
12. **浮油覆盖**：油斑块铺在水 / 冷却液斑块上时（M01、M02），给水斑块记“被油覆盖”标志。M12、M13 的条件要求“未被油覆盖”，所以水面上的油火要先烧完油，水才灭得了火。
13. **可拾取（阶段 4 预留）**：金属碎屑被拾取时发 `evt.pickup`（Code = 碎屑），份量换算为废料，1 份 = 0.5 废料。现在 FG19A 没有任何部件 / 底盘声明拾取碎屑，也没有固件读 `evt.pickup` 的碎屑来源，所以本条阶段 2 不施工、不进 FGT-EMG-031 断言；等 FG19A 给拆解钳（`comp_claw`）或 `trait.wheel.tow` 补上拾取声明并指定承接卡后再开（待定，见第 12 节第 15 项）。

### 1.5 现有场地物 → 物质映射

| 现有场地物 | 出处 | 映射到 | 迁移说明 |
|---|---|---|---|
| 读法驻留区：漏油 | `fw_oilleak`，`linger`（`tools/fgdata_reading.py:143`） | `mat.oil` | 等价迁移期保留原时长与半径 |
| 读法驻留区：冷却液 / 蔓延 / 冲击波（浸湿） | `fw_coolant`、`fw_spread`、`fw_shockwave` | `mat.coolant` | 按 FG19 FGR-EMG-080 来源列；蔓延的“真实扩散”由 FG19A 声明 |
| 读法驻留区：液氮 | `fw_nitrogen` | `mat.cryo` | 冻结改为 `phase_chill`（§3.3） |
| 读法驻留区：酸液 | `fw_acid`、`fw_corrode` | `mat.acid` | — |
| 读法驻留区：粘合剂 | `fw_adhesive` | `mat.polymer` | — |
| 读法驻留区：子母弹污染坑 | `fw_cluster`（`Poison`） | `mat.sludge` | 持续伤害 2/s 沿用 |
| 拖尾：燃迹 | `fw_burntrail` | `mat.fire` | — |
| 拖尾：减速、连网减速网 | `fw_trail`、weave | **保留为读法区域，不是物质** | FGR-EMG-081 末条 |
| 扬尘（命中挂标签，没有区域） | `fw_arc`、`fw_tractor`（`Earth`） | `mat.dust` 斑块（气体层） | FG19A 已定为排放斑块：`fw_arc` 落点排 1 份、`fw_tractor` 落点排 0.5 份（`mat.dust` emit）；单位进入斑块后照常挂扬尘沾染 |
| 反应残留燃烧区 | 爆燃残留 3 秒、半径 2 米（`tools/fgdata_reaction.py:63-64`） | `mat.fire`（E01 产物） | 改为整片油斑块变火 |
| 管线泄漏液洼（`ZoneKindLeak` = 2） | `Sim/Combat/CombatLogic.Reading.cs:1218` `UpsertLeakZone`；热更 `GL/Campaign/Logistics/PipeLeakService.cs` | 水 → `mat.water`；原油、燃油 → `mat.oil`；冷却液 → `mat.coolant`；酸液 → `mat.acid`（流体编号见 `tools/fgdata_pipes.py:24-30`，现标签 `tools/fgdata_leak.py:26-32`） | 世界源斑块（§1.4 第 11 条），热更层对账与 48 摊上限保留；已反应阶段 = E01 产物明火，存续取 max(E01, `logistics.leak.react_seconds` 12)；`PipeLeakService.Burn`（`:646-690`）：热更层不遍历斑块，改读内核上报的燃烧格（ADR-EMG-001 ⑤；FGR-EMG-087） |
| 陷阱场地（`ZoneKindField` = 1） | `tools/fgdata_structures.py:139-148` | 油膜带 → `mat.oil`；冷却液带 → `mat.coolant`；液氮 → `mat.cryo`；酸 → `mat.acid`；粘合剂 → `mat.polymer`；火 → `mat.fire`；减速 → 保留区域；电磁场 → 保留区域，并登记为 `en.elec` 注入源 | 陷阱自带每秒伤害（酸 2、火 3、电 1）作为该来源的接触伤害参数保留 |
| 云端喷淋池·酸液池、液氮池（`ZoneKindPool` = 3） | origin: `Sim/Combat/CombatTypes.cs:79`；单位参数 origin: `tools/cell_tables/fgdata_enemy.py:268-279` | `mat.acid` / `mat.cryo`，**改为中立** | 对照见第 7 节 |
| 云端油迹（`ZoneKindSlick` = 4） | origin: `CombatTypes.cs:82` | `mat.oil` | 喷淋池 + 油迹共用的上限 192（origin: `:84`）并入物质斑块上限 |
| 云端冲洗规则 | origin: `CombatTypes.cs:77-78` 注释（澄净专属分支） | 规则 M06、M07（冲淡）、M18（蒸干） | 删除专属分支（FGR-EMG-087） |
| 洗礼塔灌注流体 | origin: `GL/Campaign/Regions/BaptismTowerBoss.cs:715-761` | 世界源 `mat.oil` / `mat.acid` / `mat.water` | 第 7 节 |
| 酸沼 | origin: `Sim/Combat/CombatKernel.cs:667` `SetAcidMarsh` | 常驻世界源 `mat.acid`（虚拟世界源，不进哈希，§1.4 第 11 条） | 第 7 节 |
| 熔渣热区（铸造） | `GL/Campaign/Env/SlagHeatService.cs:16-24`；`Sim/Combat/CombatKernel.cs:613` `SetSlagZones` | **保留为危害区**（区内散热 × 0.7），登记为 `en.heat` 世界源 | 更正 FG19：它是热区，不是冷渣来源（第 9 节第 10 条） |
| 旧细胞时代 `JobZone` / `ZoneState` | `Sim/Jobs/JobZone.cs` | 不迁 | 正式版不走这条路径（底稿 02 §6.1 末行，待核实） |

---

## 2. 交互规则（`fg.TbChannelRule`）

### 2.1 编号与类别

| 前缀 | `pairKind` | 行数 | 说明 |
|---|---|---|---|
| M | `matter_matter` | 18 | 两块斑块重叠 |
| E | `matter_energy` | 20 | 斑块越过温度 / 电荷 / 动能阈值 |
| V | `event_matter` | 8 | 事件发生在某种斑块上 |
| C | `coat_matter` | 8 | 单位的沾染位 × 物质 / 能量 / 另一沾染位（扩展写法，第 9 节第 2 条） |
| P | `phase_energy` | 3 | 相态 × 能量 |
| F | `prop_field` | 2 | 属性 × 场 |
| W | `matter_entity` | 2 | 物质 × 实体（新取值，已并入 FG19 FGR-EMG-084，第 9 节第 1 条） |
| 合计 | | 61 | ≤ `emg.rule.capacity` 256 |

- 原 X01 `reaction_marktag`（事件 × 事件）已删：FG19A 的 `fw_marktag` 自己写“只跳已标记”的 `grm.mod.chain`，再加一条规则会让同一次命中多跳一次（§4.1 A1）。

### 2.2 物质 × 物质 完整矩阵（17 × 17，上三角）

图例：

| 记号 | 含义 |
|---|---|
| 合 | 同物质合并（FGR-EMG-082）；“不合”= 固态斑块保持形状，不合并 |
| Mxx | 物质 × 物质规则，见 §2.3 |
| 热→Exx | 没有直接规则；重叠时的热交换（§1.4 第 2 条）让一方越过温度阈值，由能量规则 Exx 结算 |
| 热 | 只有热交换，不越过任何有规则的阈值（有意） |
| 无a | 有意留白：物理上不相干 |
| 无b | 有意留白：气体层对另一方只通过遮蔽 / 热起作用，不互相转化 |
| 无c | 有意留白：为可读性和控制规则数量不做（玩家难以预期，或会让已有规则自我抵消） |
| 无d | 待定：阶段 2 根据模糊测试器“平庸组合”报告决定是否补规则 |

| | 1油 | 2水 | 3冷却液 | 4液氮 | 5酸 | 6胶 | 7固胶 | 8熔融 | 9冷渣 | 10明火 | 11烟 | 12蒸汽 | 13碎屑 | 14粉尘 | 15冰 | 16玻化 | 17污泥 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **1油** | 合 | M01 | M02 | 热 | M03 | 无c | 无a | 热→E01 | 热→E01（仅新生热渣） | 热→E01 | 无b | 热 | 无a | 无c | 热 | 热→E01（仅新生） | 无c |
| **2水** | · | 合 | 无c | M04 | M06 | M08 | 无a | M10 | 热→E07（仅新生） | M12 | 无b | 热 | 无d | M14 | 热→E09（冰份量大时） | 热→E07（仅新生） | 无c |
| **3冷却液** | · | · | 合 | M05 | M07 | M09 | 无a | M11 | 热→E08（仅新生） | M13 | 无b | 热 | 无d | M15 | 热 | 热→E08（仅新生） | 无c |
| **4液氮** | · | · | · | 合 | 无c | 无d | 无a | 热→E11 | 热 | M18 | 无b | 热 | 无a | 无c | 热 | 热 | 无c |
| **5酸** | · | · | · | · | 合 | M16 | 无c | 热 | M17 | 无c | 无b | 热 | 无c | 无a | 热 | 无a | 无c |
| **6胶** | · | · | · | · | · | 合 | 无a | 热→E02 | 热→E02（仅新生） | 热→E02 | 无b | 热 | 无c | 无c | 热 | 热→E02（仅新生） | 无c |
| **7固胶** | · | · | · | · | · | · | 不合 | 热→E03 | 热→E03（仅新生） | 热→E03 | 无b | 热 | 无a | 无a | 热 | 热→E03（仅新生） | 无a |
| **8熔融** | · | · | · | · | · | · | · | 合 | 热 | 热 | 无b | 热 | 无c | 热→E04/E05 | 热→E13 | 热 | 热→E06 |
| **9冷渣** | · | · | · | · | · | · | · | · | 不合 | 热 | 无b | 热 | 无a | 热→E04/E05（仅新生） | 热→E13 | 热 | 热→E06（仅新生） |
| **10明火** | · | · | · | · | · | · | · | · | · | 合 | 无b | 热 | 热 | 热→E04/E05 | 热→E13 | 热 | 热→E06 |
| **11烟** | · | · | · | · | · | · | · | · | · | · | 合 | 无c | 无b | 无c | 无b | 无b | 无b |
| **12蒸汽** | · | · | · | · | · | · | · | · | · | · | · | 合 | 无b | 无c | 热→E13 | 无b | 无b |
| **13碎屑** | · | · | · | · | · | · | · | · | · | · | · | · | 合 | 无c | 无a | 无a | 无c |
| **14粉尘** | · | · | · | · | · | · | · | · | · | · | · | · | · | 合 | 无b | 无b | 无b |
| **15冰** | · | · | · | · | · | · | · | · | · | · | · | · | · | · | 不合 | 无a | 无a |
| **16玻化** | · | · | · | · | · | · | · | · | · | · | · | · | · | · | · | 不合 | 无a |
| **17污泥** | · | · | · | · | · | · | · | · | · | · | · | · | · | · | · | · | 合 |

几个有意留白的理由（玩家可能会问）：

| 格 | 理由 |
|---|---|
| 酸 × 固化胶块：无c | 不然粘滞阻路（M16）会被自己剩下的酸拆掉 |
| 酸 × 明火：无c | 酸不可燃。沾酸的**单位**遇热走 C05“腐蚀灼烧” |
| 酸 × 玻化层：无a | 玻化层耐酸，可以当防酸地面，这是有意给的玩法 |
| 冷却液 × 冰：热 | 冷却液冰点 −30 度，冰只有 −10 度，冻不住冷却液；要冻冷却液只能直接上液氮（M05） |
| 水 × 碎屑、冷却液 × 碎屑：无d | “湿碎屑生锈结块”待模糊测试器数据决定 |
| 液氮 × 聚合胶：无d | “冻胶成块”会和粘滞抢同一个产物，待数据决定 |
| 熔融 × 碎屑：无c | 不让碎屑熔入，避免份量在两种物质间来回复制 |

### 2.3 物质 × 物质 规则表（M01～M18）

| 行 | `id` | `named` | a | b | 条件 | 主 | 产物 | 参数 | 消耗 | 附带 | 优先 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| M01 | `reaction_floatoil` | `reaction_floatoil`（新·浮油） | `mat.oil` | `mat.water` | 重叠比 ≥ 0.2 | S | `mat.oil` | 油斑块移到水斑块中心，半径 = max(油, 水)，份量不变；水斑块记“被油覆盖” | 无 | none | 61 |
| M02 | `reaction_floatoil_coolant` | `reaction_floatoil` | `mat.oil` | `mat.coolant` | 同 M01 | S | `mat.oil` | 同 M01 | 无 | none | 62 |
| M03 | `reaction_dissolve` | `reaction_dissolve` | `mat.oil` | `mat.acid` | 重叠比 ≥ 0.2 | S | `mat.sludge` | 油斑块整片变污泥，份量 = 油 + 0.5 × 酸；失去可燃性，变成粘滞地面 | 酸：0.5 × 油份量 | none | 54 |
| M04 | `reaction_freeze` | `reaction_freeze`（新·冰封） | `mat.water` | `mat.cryo` | 重叠比 ≥ 0.2 | S | `mat.ice` | 水斑块整片结冰（T −10，耐久 40）；液氮份量 < 0.3 × 水时只冻结重叠部分（分出一块冰） | 液氮：0.5 × 水份量 | none | 40 |
| M05 | `reaction_freeze_coolant` | `reaction_freeze` | `mat.coolant` | `mat.cryo` | 同 M04 | S | `mat.ice` | 同 M04 | 液氮：0.7 × 冷却液份量 | none | 41 |
| M06 | `reaction_dilute` | `reaction_dilute`（新·冲淡） | `mat.acid` | `mat.water` | 重叠比 ≥ 0.2 | S | `mat.water` | 每节拍酸份量减少 0.5 × 水份量，减掉的部分并入水斑块；酸 ≤ 0 时消失（= 云端“浸湿冲淡酸池”） | 酸 | none | 52 |
| M07 | `reaction_dilute_coolant` | `reaction_dilute` | `mat.acid` | `mat.coolant` | 同 M06 | S | `mat.coolant` | 同 M06，并入冷却液 | 酸 | none | 53 |
| M08 | `reaction_syrup` | `reaction_syrup` | `mat.polymer` | `mat.water` | 重叠比 ≥ 0.2 | S | `mat.polymer` | 聚合胶吸水胀大：份量 + 0.6 × 水，水斑块消失。减速区变大 | 水：全部 | none | 55 |
| M09 | `reaction_syrup_coolant` | `reaction_syrup` | `mat.polymer` | `mat.coolant` | 同 M08 | S | `mat.polymer` | 同 M08 | 冷却液：全部 | none | 56 |
| M10 | `reaction_quench` | `reaction_quench`（新·淬火） | `mat.molten` | `mat.water` | 重叠比 ≥ 0.1 | S | `mat.slag` | 熔融整片变冷渣（T 400，耐久 80）；伴生：气体层蒸汽，份量 = 1.5 × 消耗的水，T 100 | 水：1.0 × 熔融份量 | `side.damage 6`（蒸汽爆，一次，重叠区内单位） | 24 |
| M11 | `reaction_quench_coolant` | `reaction_quench` | `mat.molten` | `mat.coolant` | 同 M10 | S | `mat.slag` | 同 M10 | 冷却液：1.0 × 熔融份量 | `side.damage 6` | 25 |
| M12 | `reaction_steam` | `reaction_steam` | `mat.fire` | `mat.water` | 重叠比 ≥ 0.2，且水斑块未被油覆盖 | S | `mat.steam` | 明火份量减少，减量 = 重叠的水份量（≤ 0 时熄灭）；伴生蒸汽，份量 = 1.5 × 消耗的水，T 100，存续 5 秒。蒸汽的遮蔽让穿过 / 身处其中的寻的与锁定失效（§5） | 水：与灭掉的火等量 | none | 18 |
| M13 | `reaction_steam_coolant` | `reaction_steam` | `mat.fire` | `mat.coolant` | 同 M12 | S | `mat.steam` | 同 M12 | 冷却液：等量 | none | 19 |
| M14 | `reaction_mudify` | `reaction_mudify` | `mat.water` | `mat.dust` | 重叠比 ≥ 0.2 | S | `mat.sludge` | 水斑块整片变泥浆污泥，份量 = 水 + 0.5 × 粉尘；导电降到 0.2，退出传导图（带电水洼当场断电） | 粉尘：全部 | none | 57 |
| M15 | `reaction_mudify_coolant` | `reaction_mudify` | `mat.coolant` | `mat.dust` | 同 M14 | S | `mat.sludge` | 同 M14 | 粉尘：全部 | none | 58 |
| M16 | `reaction_sticky` | `reaction_sticky` | `mat.polymer` | `mat.acid` | 重叠比 ≥ 0.2 | T | `ter.block` | 聚合胶整片固化为固化胶块：面积 × 0.8，耐久 120，挡弹、不可进入。生成时圈内单位按 `Id` 升序推到离圆心最近的边缘（确定性，防软锁，B11；与 FG19 FGR-EMG-102 固态斑块条、FGT-EMG-032、ADR-EMG-001 ⑯ 一致），之后不可进入；伴生：胶块外沿接触的单位另受 `bhv.stick` 2 秒。家园 / 前哨长期留存（FGR-EMG-150） | 酸：0.5 × 胶份量 | none | 50 |
| M17 | `reaction_etchslag` | none | `mat.slag` | `mat.acid` | 重叠比 ≥ 0.2 | S | `mat.debris` | 冷渣耐久每节拍 −20，归零时变金属碎屑，份量 = 0.6 × 冷渣 | 酸：每节拍 0.2 × 冷渣份量 | none | 60 |
| M18 | `reaction_boiloff` | `reaction_boiloff`（新·蒸干） | `mat.cryo` | `mat.fire` | 重叠比 ≥ 0.2 | S | `mat.steam` | 液氮全部气化为冷雾：`mat.steam`，T −50，存续 4 秒，遮蔽照常；明火份量 −50%（= 云端“燃烧区蒸干液氮池”） | 液氮：全部 | none | 21 |

### 2.4 物质 × 能量 完整矩阵（17 × 4）

| 物质 | `en.heat` 热 | `en.elec` 电 | `en.mag` 磁 | `en.kinetic` 动能 |
|---|---|---|---|---|
| 油 | E01 爆燃（T ≥ 180） | 无a：油绝缘；沾油单位走 C04 | 无b：磁化只在实体（§1.4 第 6 条），下同 | 无c：不做飞溅 |
| 水 | E07 沸腾（≥ 100）；E09 结冰（≤ 0） | E14 短路 | 无b | 无c |
| 冷却液 | E08 沸腾（≥ 130）；E10 结冰（≤ −30） | E15 短路 | 无b | 无c |
| 液氮 | 吸热蒸发（通用，§1.2） | 无a | 无b | 无c |
| 酸液 | 无a：酸不燃不沸，有意 | E16 电解 | 无b | 无c |
| 聚合胶 | E02 焦烟（≥ 220） | 无a：绝缘 | 无b | 无c |
| 固化胶块 | E03 软化（≥ 250） | 无a | 无b | 固态破坏，无产物（有意，§1.4 第 7 条） |
| 熔融金属 | E11 凝固（< 600） | 传导节点（无转化） | 无d：磁场吸动熔融金属，待定 | 无c |
| 冷渣 | E12 重熔（≥ 900） | 传导节点 | 无b | E20 渣碎 |
| 明火 | 定温热源（通用） | 无a | 无b | 无c：不做“爆炸吹灭” |
| 烟 | 无a | 无a | 无b | 无c |
| 蒸汽 | 到期冷凝见 V01 | 无a：导电 0.2，不入图 | 无b | 无c |
| 金属碎屑 | 热碎屑：保留温度，磁扫时带火（F02） | 传导节点 | 见 F02 磁扫（属性 × 场） | 无c |
| 导电粉尘 | E04 玻化 / E05 尘爆（≥ 300，按浓度） | E17 电火花尘爆；传导节点 | 无b | 无c |
| 冰 | E13 融化（> 0） | 无a | 无b | E18 冰碎 |
| 玻化层 | 无a：本附录不做重熔 | 无a | 无b | E19 玻璃碎 |
| 化工污泥 | E06 慢燃（≥ 260） | 无a：导电 0.2 | 无b | 无c |

### 2.5 物质 × 能量 规则表（E01～E20）

| 行 | `id` | `named` | a | b | 条件 | 主 | 产物 | 参数 | 消耗 | 附带 | 优先 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| E01 | `reaction_deflagrate` | `reaction_deflagrate` | `mat.oil` | `en.heat` | T ≥ 180 | S | `mat.fire` | 整片油斑块就地变明火，半径不变，存续 = 6 + 0.3 × 份量 秒（≤ 20）；管线液洼来源取 max(本值, 12)（`tools/fgdata_leak.py:41`） | 油：全部 | `side.damage 8`（一次，斑块内全部单位，中立） | 10 |
| E02 | `reaction_smolder` | `reaction_smolder`（新·焦烟） | `mat.polymer` | `en.heat` | T ≥ 220 | S | `mat.smoke` | 聚合胶不起明火，阴燃成浓烟：气体层烟，份量 = 2 × 胶，T 60 | 胶：全部 | none | 12 |
| E03 | `reaction_soften` | none | `mat.cured` | `en.heat` | T ≥ 250 | S | `mat.polymer` | 固化胶块软化回聚合胶，阻路解除；继续受热则走 E02 冒烟 | — | none | 14 |
| E04 | `reaction_vitrify` | `reaction_vitrify` | `mat.dust` | `en.heat` | T ≥ 300 且浓度 < 0.6 | S | `mat.glass` | 薄尘落地熔成玻化层：地面层，面积 = 粉尘当前面积 × 0.6，T 300，耐久 20 | 粉尘：全部 | none | 16 |
| E05 | `reaction_dustburst` | `reaction_dustburst`（新·尘爆） | `mat.dust` | `en.heat` | T ≥ 300 且浓度 ≥ `emg.dust.burst_conc`（0.6） | P | `topo.burst` | 在粉尘中心二次起爆（§2.9）：半径 = 粉尘半径 + 1 米（≤ 5），范围内实体动能 30，范围内斑块 +300 度（可连锁点燃油、玻化薄尘） | 粉尘：全部 | `side.damage 30` | 15 |
| E06 | `reaction_sludgefire` | none | `mat.sludge` | `en.heat` | T ≥ 260 | S | `mat.fire` | 慢燃：明火存续 × 2，燃烧排烟 × 2 | 污泥：全部 | none | 13 |
| E07 | `reaction_boil` | none | `mat.water` | `en.heat` | T ≥ 100 | S | `mat.steam` | 每节拍 30% 份量变蒸汽（T 100），剩下的水温回到 99 | 水：每节拍 30% | none | 22 |
| E08 | `reaction_boil_coolant` | none | `mat.coolant` | `en.heat` | T ≥ 130 | S | `mat.steam` | 同 E07 | 冷却液：每节拍 30% | none | 23 |
| E09 | `reaction_icing` | `reaction_freeze` | `mat.water` | `en.heat` | T ≤ 0 持续 0.5 秒 | S | `mat.ice` | 整片结冰（T −10，耐久 40） | — | none | 42 |
| E10 | `reaction_icing_coolant` | `reaction_freeze` | `mat.coolant` | `en.heat` | T ≤ −30 持续 0.5 秒 | S | `mat.ice` | 同 E09 | — | none | 43 |
| E11 | `reaction_solidify` | none | `mat.molten` | `en.heat` | T < 600 | S | `mat.slag` | 熔融凝固为冷渣，T = 当时温度，耐久 80 | — | none | 26 |
| E12 | `reaction_remelt` | none | `mat.slag` | `en.heat` | T ≥ 900 | S | `mat.molten` | 冷渣重熔 | — | none | 27 |
| E13 | `reaction_thaw` | none | `mat.ice` | `en.heat` | T > 0 持续 1 秒 | S | `mat.water` | 冰融成水，份量 × 0.8，T 1。桥上单位的保底见 V08 与 FGR-EMG-151 | — | none | 28 |
| E14 | `reaction_conduct` | `reaction_conduct` | `mat.water` | `en.elec` | 斑块电荷 ≥ 0.2 | P | `topo.arc` | 导电水洼：带电期间每传导节拍（0.25 秒）对斑块里的全部单位放电（含己方、中立），每个单位 `phs.charge` + 0.6 × 斑块电荷；电弧在单位之间连线显示。麻痹由 `phase_charge` 自然产生（§3.3），不另写产物 | — | `side.damage 2`（每单位每节拍） | 30 |
| E15 | `reaction_conduct_coolant` | `reaction_conduct` | `mat.coolant` | `en.elec` | 斑块电荷 ≥ 0.3（导电只有中） | P | `topo.arc` | 同 E14 | — | `side.damage 2` | 31 |
| E16 | `reaction_electrolysis` | `reaction_electrolysis` | `mat.acid` | `en.elec` | 斑块电荷 ≥ 0.4；每块每秒最多 1 次 | S | `mat.debris` | 电解析出：在酸斑块中心生成金属碎屑，份量 = 0.2 × 酸。碎屑导电、磁性高，进传导图，可被 F02 磁扫吸起 | 酸：20% | none | 33 |
| E17 | `reaction_dustburst_spark` | `reaction_dustburst` | `mat.dust` | `en.elec` | 斑块电荷 ≥ 0.3 且浓度 ≥ 0.6 | P | `topo.burst` | 同 E05 | 粉尘：全部 | `side.damage 30` | 35 |
| E18 | `reaction_icebreak` | none | `mat.ice` | `en.kinetic` | 累计动能 ≥ 耐久 40 | P | `topo.fragment` | 冰碎：冰碴破片 3 片（§2.9）；伴生水，份量 × 0.5 | 冰：全部 | none | 73 |
| E19 | `reaction_glassbreak` | none | `mat.glass` | `en.kinetic` | 累计 ≥ 20 | P | `topo.fragment` | 玻璃破片 5 片 | 全部 | none | 74 |
| E20 | `reaction_slagbreak` | none | `mat.slag` | `en.kinetic` | 累计 ≥ 80 | S | `mat.debris` | 冷渣碎成碎屑，份量 × 0.6 | 全部 | none | 75 |

### 2.6 物质 × 事件：矩阵与规则表（V01～V08）

能落在斑块位置上的事件有 10 类，矩阵如下。其余 19 类事件（`evt.fire`、`evt.hit`、`evt.pierce`、`evt.bounce`、`evt.split`、`evt.prox`、`evt.timer`、`evt.hurt`、`evt.cooled`、`evt.marked`、`evt.uplink`、`evt.unlink`、`evt.react`、`evt.hijack`、`evt.seize`、`evt.release`、`evt.beat`、`evt.ping`，以及 `evt.leave`）与 17 种物质的交叉一律**无（有意）**：

- 这些事件没有“落在哪块斑块上”的语义，由固件程序读写（FG19A）；
- 命中带进物质的效果走 §5 场 × 载体表；
- `evt.leave` 只结束接触，沾染保留由 §1.2 的保留秒处理。

| 物质 | `evt.expire` | `evt.phase`（斑块相变） | `evt.overheat` | `evt.impact` | `evt.death` / `evt.kill` | `evt.enter` | `evt.absorb` | `evt.pickup` | `evt.push` |
|---|---|---|---|---|---|---|---|---|---|
| 油 | 无a | — | 无c | 无c | 无b：阵亡排油由敌方 / 部件声明写 | 沾染（通用） | — | 无a | 湿滑（通用） |
| 水 | 无a | — | V05 | 无c | 无b | 沾染 | — | 无a | 无a |
| 冷却液 | 无a | — | V04 | 无c | 无b | 沾染 | — | 无a | 无a |
| 液氮 | 无a | — | 无c | 无c | 无b | 沾染 | — | 无a | 无a |
| 酸液 | V07（阶段 4） | — | 无c | 无c | 无b | 沾染 | — | 无a | 无a |
| 聚合胶 | 无a | — | 无c | 无c | 无b | 沾染 | — | 无a | 无a |
| 固化胶块 | 无a | — | 无c | 动能（E 通用） | 无a | 阻挡 | 扣耐久（通用） | 无a | 无a |
| 熔融金属 | 无a | V06（阶段 4） | 无c | 无c | 无b | 沾染 | — | 无a | 无a |
| 冷渣 | 无a | — | 无c | 动能 → E20 | 无a | — | 扣耐久（通用） | 无a | 无a |
| 明火 | V03（阶段 4） | — | 无c | 无c | 无b | 着火（通用） | — | 无a | 无a |
| 烟 | 无a | — | 无c | 无c | 无a | 遮蔽 | — | 无a | 无a |
| 蒸汽 | V01 | — | 无c | 无c | 无a | 遮蔽 | — | 无a | 无a |
| 金属碎屑 | 无a | — | 无c | 无c | 无a | 沾染 | — | 拾取（阶段 4 预留，§1.4 第 13 条） | 无a |
| 导电粉尘 | 无a | — | 无c | 无c | 无a | 沾染 | — | 无a | 无a |
| 冰 | V02 | V08（阶段 4） | 无c | 动能 → E18 | 无a | 湿滑 | — | 无a | 湿滑（通用） |
| 玻化层 | 无a | — | 无c | 动能 → E19 | 无a | — | — | 无a | 无a |
| 化工污泥 | 无a | — | 无c | 无c | 无a | 沾染 | — | 无a | 无a |

| 行 | `id` | `named` | a | b | 条件 | 主 | 产物 | 参数 | 消耗 | 附带 | 优先 | 阶段 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| V01 | `reaction_condense` | none | `evt.expire` | `mat.steam` | 到期时 T < 100 | S | `mat.water` | 在地面层落下水斑块，份量 = 0.3 × 蒸汽（烟幕散去后地上留一层水，可以接着结冰或通电） | — | none | 90 | 2 |
| V02 | `reaction_icemelt` | none | `evt.expire` | `mat.ice` | 冰的寿命到 | S | `mat.water` | 份量 × 0.8 | — | none | 91 | 2 |
| V03 | `reaction_scorch` | `reaction_scorch`（新·焦土） | `evt.expire` | `mat.fire` | 地点是家园 / 前哨，且同一位置累计燃烧 ≥ 20 秒 | T | `ter.mark` | 写“焦土”地面标记（FGR-EMG-153，受 `emg.terrain.marks_max` 限制）；焦土上的可燃斑块点燃阈值 −40 度 | — | none | 95 | 4 |
| V04 | `reaction_steamvent` | `reaction_steamvent`（新·冷却蒸腾） | `evt.overheat` | `mat.coolant` | 过热单位站在冷却液斑块里 | S | `mat.steam` | 单位脚下喷出蒸汽：半径 2.5，份量 20，T 100，等于自带烟幕；冷却液份量 −5 | 冷却液 5 份 | none | 92 | 2 |
| V05 | `reaction_steamvent_water` | `reaction_steamvent` | `evt.overheat` | `mat.water` | 同 V04 | S | `mat.steam` | 同 V04 | 水 5 份 | none | 93 | 2 |
| V06 | `reaction_meltpit` | `reaction_meltpit`（新·熔坑） | `evt.phase` | `mat.molten` | 新相 = 冷渣；地面是“可熔地面”（读 `ter.ground`）；同一位置累计熔融份量 ≥ 30 | T | `ter.mark` | 熔坑（FGR-EMG-152：不可通行、更新寻路、家园上不能建造，≤ `emg.terrain.pit_max`） | — | none | 96 | 4 |
| V07 | `reaction_meltpit_acid` | `reaction_meltpit` | `evt.expire` | `mat.acid` | 可熔地面；同一位置累计酸份量 ≥ 40 | T | `ter.mark` | 同 V06 | — | none | 97 | 4 |
| V08 | `reaction_icebridge` | `reaction_icebridge`（新·冻结桥） | `evt.phase` | `mat.ice` | 新相 = 冰；斑块覆盖水面（`ter.water`）面积 ≥ 60% | T | `ter.bridge` | 冻结桥（FGR-EMG-151：寻路镜像标为可通行；融化时把桥上单位推回陆地；≤ `emg.terrain.bridge_max`） | — | none | 98 | 4 |

### 2.7 沾染 × 物质 / 能量 规则表（C01～C08，单位层）

只有 `a` 列是单位身上的沾染位（§3.1），`b` 列是端口或另一沾染位。这正是旧“两个标签配对”在新体系里的位置：仍然只认通道，不认固件。

| 行 | `id` | `named` | a（沾染） | b | 条件 | 主 | 产物 | 参数 | 消耗 | 附带 | 优先 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| C01 | `reaction_deflagrate_coat` | `reaction_deflagrate` | 油污（1） | `en.heat` | 单次注入 ≥ 20，或进入明火 / 熔融斑块，或进入燃烧相 | S | `mat.fire` | 脚下生成明火：半径 1.5，份量 7，存续 6 秒；本单位着火度置满 | 油污沾染 | `side.damage 8` | 11 |
| C02 | `reaction_steam_coat` | `reaction_steam` | 浸湿（2） | `en.heat` | 同 C01 | S | `mat.steam` | 脚下蒸汽：半径 1.5，份量 7，T 100；着火度清零并退出燃烧 | 浸湿沾染 | none | 20 |
| C03 | `reaction_conduct_coat` | `reaction_conduct` | 浸湿（2） | `en.elec` | 单次注入 ≥ 0.1 | P | `topo.arc` | 本单位电荷注入 × 2；电弧从它跳向 4 米内最多 3 个导电（`prop.conductive` ≥ 0.3）或浸湿的单位（含己方），每跳 × 0.7 | 浸湿沾染 | `side.damage 3`（每跳） | 32 |
| C04 | `reaction_insulate` | `reaction_insulate` | 油污（1） | `en.elec` | 单次注入 ≥ 0.1 | P | `topo.arc` | 闪络：本单位不得电荷，这股电改跳 4 米内最近的另一个导体（单位 / 导电斑块 / 残骸 / 哨戒桩，含己方），量 × 0.8；附近没有导体就消散 | 不消耗 | none | 29 |
| C05 | `reaction_causticburn` | `reaction_causticburn` | 腐蚀（6） | `en.heat` | 单次注入 ≥ 20，或进入明火 / 熔融斑块 | P | `topo.trail` | 燃烧的酸液沿途滴落：4 秒内本单位每移动 1.5 米，在身后留一块明火（半径 1.0，份量 3，存续 3 秒）；静止时每秒一块 | 腐蚀沾染 | none | 17 |
| C06 | `reaction_sticky_coat` | `reaction_sticky` | 胶膜（14） | 腐蚀（6）沾染 | 两种沾染同时在身上（站进酸斑块，或被带酸载荷命中） | B | `bhv.stick` | 胶膜固化：原地粘住 2.5 秒，能开火、不能移动、不受击退；结束后胶膜清除 | 胶膜沾染 | none | 51 |
| C07 | `reaction_mudify_coat` | `reaction_mudify` | 扬尘（12） | 浸湿（2）沾染 | 两种沾染同时在身上 | B | `bhv.slow` | 泥浆附着：移动 −35%，4 秒；期间 `prop.conductive` 视为 0，不进传导图，短路 / 电弧跳不到它 | 扬尘、浸湿 | none | 59 |
| C08 | `reaction_electrolysis_coat` | `reaction_electrolysis` | 腐蚀（6） | `en.elec` | 单次注入 ≥ 0.2 | S | `mat.debris` | 脚下析出碎屑：半径 1，份量 3 | 腐蚀沾染 | none | 34 |

### 2.8 相态 × 能量、属性 × 场、物质 × 实体（P、F、W）

| 行 | `id` | `named` | `pairKind` | a | b | 条件 | 主 | 产物 | 参数 | 消耗 | 附带 | 优先 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| P01 | `reaction_shatter` | `reaction_shatter` | `phase_energy` | `phs.frozen` | `en.kinetic` | 单次动能 ≥ 24 × 目标 `brittle`（§6.1） | P | `topo.fragment` | 碎裂：破片 6 片（目标半径 > 1.5 米时 8 片），立即退出冻结、进入免疫窗；目标 `prop.integrity` ≤ 0.25 时直接击毁，不留残骸，全部碎成破片 | 冻结 | `side.damage 20`（对目标） | 70 |
| P02 | `reaction_thermalshock` | `reaction_thermalshock` | `phase_energy` | `phs.frozen` | `en.heat` | 单次注入 ≥ 30，或被推进明火 / 熔融斑块 | P | `topo.fragment` | 热震：立即解冻，冻裂的外装甲崩成装甲破片 4 片（§2.9）；伴随把目标 `phs.corrode` 置 1.0，进入破甲相态（与腐蚀破甲是同一相态，不叠加，取更长的；装甲清零属于相态期间的附带效果，不是本行产物） | 冻结 | none | 71 |
| P03 | `reaction_meltoverload` | `reaction_meltoverload` | `phase_energy` | `phs.overheat` | `en.kinetic` | 同一次命中：注入结构积热 ≥ 0.15 且动能 ≥ 40 | S | `mat.molten` | 熔穿：命中点溅出熔融金属 4 份（T 1000，落在目标脚下）；同时把目标 `phs.overheat` 直接置 1.0，进入熔穿相态（`phase_overheat`：身下再漏熔融金属）。旧“本发额外穿甲 +30%”（`Sim/Combat/CombatLogic.cs:1163-1175`）退役，不再作为任何行的结果（预期差异，第 12 节第 14 项）。开火者代价 `cost.heat` = 25，取代该指令表值热量（§4.1 A2） | — | none | 72 |
| F01 | `reaction_annihilate` | `reaction_annihilate` | `prop_field` | `prop.integrity` | `fld.deflect` | 单位处于与它敌对的偏折场内，且完整度 ≤ `emg.react.annihilate_integrity`（0.15） | P | `topo.burst` | 压溃起爆：单位被击毁、不留残骸，原地起爆半径 3 米，动能 20 | — | `side.damage 40` | 80 |
| F02 | `reaction_magsweep` | `reaction_magsweep`（新·磁扫） | `prop_field` | `prop.magnetic` | `fld.magnet` | 非固态斑块、磁性 ≥ 0.6（即金属碎屑）与磁场重叠 | P | `topo.fragment` | 碎屑被吸起：每 2 份 1 片，最多 8 片，以 14 米 / 秒飞向磁场中心，沿途命中；碎屑温度 ≥ 300 时破片带火（命中注入 `en.heat` 30）。这就是“磁场把燃烧碎屑吸成火团” | 碎屑：按片数 | none | 81 |
| W01 | `reaction_hemolysis` | `reaction_hemolysis` | `matter_entity`（已并入 FG19 FGR-EMG-084） | `mat.acid` | `ent.wreck` | 残骸圆心在酸斑块内 ≥ 2 秒 | S | `mat.debris` | 残骸溶解：完整度每秒 −25%，归零时变金属碎屑，份量 = 0.5 × 残骸质量（≤ 20），之后不能再被回收修复拆解 | 酸：每秒 10% | none | 85 |
| W02 | `reaction_sepsis` | `reaction_sepsis` | `matter_entity`（已并入 FG19 FGR-EMG-084） | `mat.sludge` | `ent.wreck` | 残骸在污泥斑块内 ≥ 3 秒；或在污泥斑块内 ≥ 1 秒且受单次动能 ≥ 15（锈透的残骸一碰就塌） | P | `topo.fragment` | 锈蚀崩塌：残骸崩解为锈蚀破片 4 片（§2.9）；伴生金属碎屑，份量 = 0.3 × 残骸质量（≤ 12）。**不做接触传播**：连锁只靠物理途径，锈蚀破片（6 × 2.5 = 15）打到同一片污泥里的相邻残骸，满足第二个条件就让它崩塌；污泥外的残骸要单次 ≥ 40 才走“残骸重击”。链深 ≤ 3（破片链深规则） | — | none | 86 |

### 2.9 产物参数

**破片 `topo.fragment`**（FGR-EMG-114：破片在弹体池里，固定按 2 代处理、不执行程序，命中发 `evt.hit` 并让链深 + 1；池满按 FGR-EMG-063 顺延）

| 来源 | 片数 | 每片伤害 | 速度 | 射程 | 方向 |
|---|---|---|---|---|---|
| 碎裂 P01 | 6（大目标 8） | 10 | 18 米 / 秒 | 7 米 | 均匀放射，朝命中方向偏 30° |
| 热震装甲破片 P02 | 4 | 6 | 14 | 4 | 放射 |
| 冰碎 E18 | 3 | 4 | 14 | 4 | 放射 |
| 玻璃破片 E19 | 5 | 8 | 16 | 5 | 放射 |
| 锈蚀崩塌 W02 | 4 | 6 | 12 | 5 | 放射 |
| 磁扫 F02 | ≤ 8 | 5 | 14 | 到磁场中心 | 指向场心 |
| 残骸重击（残骸受单次动能 ≥ 40，FGR-EMG-114） | 2 | 5 | 16 | 5 | 背离受击方向 |
| 爆炸反应装甲起爆（FGR-EMG-233，部件声明产物，2026-10-26 用户批准；片数取 `func_spikes` 声明 `amount`） | 3 | 4 | 16 | 4 | 从宿主朝攻击者一侧外缘放出，以攻击者方向为中心 ±30° 扇面均布 |

- 破片动能系数 2.5（§6.3）。只有碎裂破片（10 × 2.5 = 25 ≥ 24）能接着碎裂相邻的冻结目标（钢、合金、陶瓷、玻璃材质；复合材质阈值 28.8，破片碎不动），这是有意设计的“一锤碎一片”，由链深 3 封顶。锈蚀破片（6 × 2.5 = 15）只够让污泥里的锈蚀残骸崩塌（W02 第二个条件），碎不了冻结目标。反应装甲破片（4 × 2.5 = 10）同样碎不了冻结目标；它不需要新规则行，与场、物质、残骸的互作全部走第 5 节现有的破片规则（磁场弯向场心、力场推动、带火 / 带水 / 带寒、玻化层与冰反弹、固态斑块吸收）。

**其他产物**

| 产物 | 用在 | 参数 |
|---|---|---|
| `topo.burst` | E05、E17 尘爆 | 半径 = 粉尘半径 + 1（≤ 5 米）；动能 30；范围内斑块 +300 度 |
| `topo.burst` | F01 湮灭 | 半径 3 米；动能 20 |
| `topo.arc` | E14、E15 | 每传导节拍对斑块内全部单位放电 |
| `topo.arc` | C03 | 4 米内 ≤ 3 跳，每跳 × 0.7 |
| `topo.arc` | C04 | 4 米内 1 跳，× 0.8 |
| `topo.arc` | `phase_charge` 放电 | 0.5 电荷 → 4 米内 ≤ 3 个导体 |
| `topo.trail` | C05 | 4 秒，每 1.5 米一块明火 |
| `topo.trail` | §5 带油带火弹体 | 每 2 米一块明火，半径 0.8，≤ 6 块 |
| `topo.redirect` | `phase_magnetize` 弱磁源 | 转向 45°/秒 × 被吸物磁性 |
| `topo.redirect` | §5 磁场 | 转向 90°/秒 × 磁性 |
| `topo.reflect` | §5 玻化层 / 冰面 | 只在场 × 载体表出现 |
| `bhv.stick` | C06 | 2.5 秒 |
| `bhv.stick` | M16 伴生 | 2 秒（胶块外沿接触的单位；圈内单位生成时已按 `Id` 升序推到边缘） |
| `bhv.slow` | C07 | −35%，4 秒 |
| `bhv.freeze`、`bhv.stun` | 相态规则 | §3.3 |
| `mat.molten` | P03 | 命中点 4 份，T 1000 |
| `bhv.lockloss` | §5 遮蔽 | 烟 0.5 秒 / 蒸汽 0.3 秒 |
| `ter.block` | M16 | 固化胶块耐久 120 |
| `ter.mark` | V03、V06、V07 | 阶段 4 |
| `ter.bridge` | V08 | 阶段 4 |

- `ter.slick`（湿滑）、`ter.cover`（掩体）由物质属性直接提供：油、冰的湿滑；固化胶块、冷渣、残骸的掩体。本附录没有规则以它们为产物。
- **本附录不用 `bhv.breach`**：破甲 / 穿甲只是承伤数值变化，不改目标行为（提案 A 第 3 类只列失锁、过热停机、被劫持、被推走，DESIGN.md 第 2 节）。装甲清零只作为 `phase_corrode`、`phase_overheat` 相态期间的附带效果存在，任何规则行的 `product` 都不取它（第 9 节第 14 条：已从 FG19 §2.9 的 B 类清单删除）。

### 2.10 优先级分段与同时满足

- 分段：
  - 10～29：热与燃烧（点燃先于灭火：沾油又浸湿的单位遇热先爆燃，对应“浮在水上的火”）；
  - 29～35：电（C04 绝缘 29 先于 C03 短路 32：又沾油又浸湿时先闪络）；
  - 40～43：冷；
  - 50～62：化学；
  - 70～75：相态与机械；
  - 80～81：场；
  - 85～86：实体；
  - 90～98：事件。
- 优先级只在“同一斑块 / 同一单位”内比较。不同斑块各自结算：油浮在水上遇火时，油斑块走 E01，水斑块因“被油覆盖”不走 M12，等油烧完再灭火。

---

## 3. 状态标签、沾染与相变

### 3.1 现有 26 个主标签与区域减速位的新口径

现有位号、ID 一律不改，位不复用（FGR-EMG-210）。“旧效果”出自 `tools/fgdata_reading.py:271-276`，“旧配料”出自底稿 02 §3.2。

| 位 | ID | 机械名 | 旧效果 | 新口径 | 是否还是反应配料 |
|---|---|---|---|---|---|
| 0 | `Fire` | 燃烧 | 每秒 4 × 层 | `phs.burning` 的显示位。沾到明火 = 注入着火度；持续伤害 4 × L（L ≤ 3，由着火度推出，等价“4 × 层”） | 改为相态 |
| 1 | `Oil` | 油污 | 无 | `mat.oil` 的沾染位 | C01、C04 |
| 2 | `Wet` | 浸湿 | 无 | `mat.water` 与 `mat.coolant` 共用的沾染位 | C02、C03、C07 |
| 3 | `Frozen` | 冻结 | 易伤 +25% | `phs.frozen` 的显示位；易伤取消，改为定身（FG19 第 14 节第 7 项，待用户确认） | 改为相态（P01、P02） |
| 4 | `Shock` | 电击 | 易伤 +10% | 不再是持续标签：电击 = 一次 `en.elec` 注入。本位保留为“0.5 秒内受过电”的闪烁提示；易伤取消，改为电荷累积 → 麻痹（第 12 节第 3 项待确认） | 由 `en.elec` 取代 |
| 5 | `Slow` | 减速 | 减速 40% | 不变（拖尾读法区域） | 否 |
| 6 | `Acid` | 腐蚀 | 每秒 3 × 层 | `mat.acid` 的沾染位（持续伤害沿用）+ `phs.corrode` 累积 | C05、C06、C08 |
| 7 | `Charged` | 充能 | 无 | `phs.charge` 的显示位 | 改为相态 |
| 8 | `Magnet` | 磁化 | 无 | `phs.magnetized` 的显示位（磁化度另存） | 改为相态 |
| 9 | `Blood` | 回收 | 回收修复 20% | “可回收残骸”标记（FG19 第 11 节第 7 行） | 否：回路溶断、连锁锈蚀改读 `ent.wreck` |
| 10 | `Dark` | 处决 | ≤ 15% 直接击毁 | 不变（`fw_execute` 读 `prop.integrity`） | 否：湮灭改读 `prop.integrity` |
| 11 | `Mirror` | 偏折 | 无 | 只作偏折场存在的显示标记 | 否：湮灭改读 `fld.deflect` |
| 12 | `Earth` | 扬尘 | 无 | `mat.dust` 的沾染位 | C07 |
| 13 | `Poison` | 污染 | 每秒 2 × 层 | `mat.sludge` 的沾染位（持续伤害沿用） | 否：连锁锈蚀改读 `mat.sludge` × `ent.wreck` |
| 14 | `SugarFilm` | 胶膜 | 减速 30% | `mat.polymer` 的沾染位（减速沿用） | C06 |
| 15 | `Capillary` | 渗流 | 无 | 蔓延的扩散标记（FG19A） | 否 |
| 16 | `Haste` | 加速 | 无 | 与物质无关（FG19A） | 否 |
| 17 | `Drifting` | 侧偏 | 无 | `grm.mod.drift` 的显示（FG19A） | 否 |
| 18 | `WallImpact` | 撞壁 | 无 | 改由 `evt.impact` 表达（FG-GAP-151 建议） | 否 |
| 19 | `ReceptorMemory` | 锁定 | 无 | 与 `grm.mod.homing` 相关（FG19A） | 否 |
| 20 | `InheritPattern` | 协同 | 无 | `grm.inherit` / `sqd.*`（FG19A） | 否 |
| 21 | `Marked` | 标记 | 无 | `evt.marked` 的显示（与 `MarkedUntil` 的关系待核实，底稿 02 §2.1） | 否：`fw_marktag` 读 `evt.marked`（FG19A），不设规则行 |
| 22 | `Physical` | 动能 | 无 | **退出反应**：动能改为 `en.kinetic` 能量通道（每次命中产生，FGR-EMG-093）；位保留、不复用 | 否 |
| 23 | `Shield` | 护盾 | 无 | `evt.absorb`（与物质无关） | 否 |
| 24 | `Displace` | 位移 | 无 | `evt.push` | 否 |
| 25 | `Catalyst` | 增幅 | 无 | `fw_amplify` 的改造由 FG19A 负责 | 否 |
| 31 | （非标签） | 区域减速 | 取区域值 | 不变 | 否 |

### 3.2 新沾染位分配与位数结论

| 位 | 新标签 ID（建议） | 名称 | 来源 | 保留秒 |
|---|---|---|---|---|
| 26 | `Frost` | 霜冻 | `mat.cryo` | 2 |
| 27 | `MoltenSplash` | 熔渣 | `mat.molten` | 2 |
| 28 | `DebrisCoat` | 碎屑 | `mat.debris` | 4 |
| 29 | `Obscured` | 遮蔽 | `mat.smoke`、`mat.steam` | 0.5（= `emg.smoke.lockloss_seconds`） |
| 30 | — | 空，预留 | — | — |

- 结论：**32 位够用**。前提是 7 个相态的状态与累积量另存在实体的“相态块”里，不占 `Status` 位（第 9 节第 7 条，已并入 FG19 FGR-EMG-020 第 5 条）。
  - 冻结、燃烧、充能、磁化复用现有显示位 3、0、7、8；
  - 麻痹、破甲、熔穿、被劫持没有位，由界面直接读相态块显示图标（B05）。
- 如果 ADR 决定相态也走 `Status` 位，还差 4 位，需要把 `Status` 扩到 64 位（FGR-EMG-020 第 5 条）。
- 弹体沾染不占单位位：在弹体结构里另加 1 字节，8 位依次为带电、带火、带油、带水、带寒、带酸、带胶、带尘（FGR-EMG-120），每种每发只记一次。
- 新增的 4 行要登记进 `fg.TbStatusTag`（名字、形状、颜色），并过 `tools/fgdata.py` 的 `STATUS_EFFECTS` 交叉校验（云端新增，底稿 05 §4.3 末表）。

### 3.3 相态规则表（`fg.TbPhaseRule`，7 行）

| `id` | 累积量 → 相态 | enter | exit | 衰减 / 秒 | cap | minDwell | guard | 抗性列 | 进入时 | 相态期间 | 增益来源（每秒，或每次） |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `phase_chill` | `phs.chill` → `phs.frozen` | 1.0 | 计时退出 | 0.25（燃烧中 ×3、武器积热过半 ×2） | 1.0 | 2.5 | 1.5 | 寒 | 发 `evt.phase`；寒量清零 | `bhv.freeze`：不能移动、不能开火；受动能 → P01；受热 → P02 | 液氮接触 0.6；带寒载荷命中 0.3；冰面 0.1。进入冻结前减速 = 0.6 × 寒量 |
| `phase_overheat` | `phs.overheat` → `phs.meltdown` | 1.0 | 0.4 | 0.05（浸湿时 ×4） | 1.5 | 4 | 8 | 热 | 身下生成熔融金属 6 份 | 每 2 秒再漏 2 份熔融金属（主结果 S）；附带：正面装甲失效 | `en.heat` 注入 ÷ 300；明火接触 20 热 / 秒；熔融接触 30；P03 直接置 1.0 |
| `phase_charge` | `phs.charge` → `phs.stun` | 1.0 | 计时退出 | 0.4 | 1.2 | 1.0 | 2.0 | 电 | 放电 0.5 给 4 米内 ≤ 3 个导体（`topo.arc`），电荷清零 | `bhv.stun`：不能行动 | 导电水洼每节拍 0.6 × 斑块电荷；电晕场 0.3；电弧、C03 |
| `phase_corrode` | `phs.corrode` → `phs.breach` | 1.0 | 0.3 | 0.08 | 1.5 | 4 | 3 | 腐蚀 | 装甲脱落：身下生成金属碎屑 2 份 | 装甲脱落后本单位质量 × 0.6，被推、被牵引、被撞的位移随之变大（主结果走 `fieldrule_force_unit` 与击退的“位移 = 力 ÷ 质量”，即更容易被推走）；附带：装甲清零 | 酸接触 0.25；带酸载荷 0.15；污泥 0.05；浸湿时增益 × 0.5；P02 直接置 1.0 |
| `phase_magnetize` | `phs.magnetize` → `phs.magnetized` | 1.0 | 0.4 | 0.15 | 1.5 | 3 | 2 | 磁 | — | 成为弱磁源：4 米内碎屑、破片、弹体不分阵营转向（`topo.redirect`） | 磁场 0.2；`en.mag` 注入；增益 × 实体磁性；沾碎屑时 × 1.5 |
| `phase_ignite` | `phs.ignite` → `phs.burning` | 1.0 | 0.2 | 0.3 | 1.5 | 2 | 0（被扑灭后 1 秒） | 热 | — | 持续伤害 4 × L，L = 1 + ⌊(着火度 − 1) ÷ 0.25⌋，≤ 3；对自身 `en.heat` 10 / 秒；站着的可燃斑块每节拍 +30 度（燃烧的敌人走过油区就会引燃） | 明火接触 0.8 × (0.3 + 可燃)；带火载荷 0.3；C01 直接置满。进入水 / 冷却液 / 液氮：清零并立即退出 |
| `phase_hack` | `phs.hack` → `phs.hijacked` | 1.0 | 阵亡时判定 | 0.05 | 1.0 | — | — | 入侵 | — | FGR-EMG-163 | 写 `hij.hack` 的固件（阶段 4） |

- 数值依据：
  - `phase_chill` 与云端受寒完全同数：减速上限 0.6、衰减 0.25、冻结 2.5 秒、解冻免疫 1.5 秒（origin: `tools/cell_tables/fgdata_clarity.py:51-55`）。云端“受寒 / 冻结”因此是本表的等价实现，没有行为差异。
  - `phase_corrode` 的浸湿 × 0.5 与云端 `hazard.acid_marsh.wet_mult` 同值（origin: `fgdata_clarity.py:24`）。
  - `phase_ignite` 的 L 档在着火度 1.0 / 1.25 / 1.5 时为 1 / 2 / 3，等价于现在“燃烧 4 × 层数、最多 3 层”（底稿 02 §2.4），迁移不改燃烧伤害上限。
- 抗性：累积速度 × (1 − 抗性)，抗性 = 1 即免疫（FGR-EMG-103）。

### 3.4 相变阈值链（从一种状态走到下一种）

**单位**

| 起点 | 阈值 / 触发 | 结果 | 再往下 |
|---|---|---|---|
| 寒量（液氮、带寒载荷、冰面） | ≥ 1.0 | 冻结 2.5 秒（不能动、不能开火） | 受单次动能 ≥ 24 × `brittle` → **碎裂**（P01，破片可连锁碎裂相邻冻结目标）；受单次热 ≥ 30 → **热震**（P02，外装甲崩成破片并进入破甲）；都没有 → 解冻，免疫 1.5 秒 |
| 结构积热（`en.heat`、明火、熔融、重型带热命中） | ≥ 1.0（或 P03 熔穿过载直接置满） | **熔穿**：身下漏出熔融金属 6 份，之后每 2 秒 2 份；附带正面装甲失效 | 熔融降到 600 度以下 → 冷渣（掩体、可导电、磁性高）→ 受酸 → 碎屑（M17）、受动能 → 碎屑（E20）→ 碎屑遇磁场 → 破片（F02 磁扫） |
| 电荷（导电水洼、电晕场、电弧） | ≥ 1.0 | **麻痹** 1 秒，并向 4 米内导体放电 | 放电进导电水洼 → 再次 E14，受 guard 2 秒限制不会永久麻痹 |
| 腐蚀度（酸、污泥；或 P02 热震直接置满） | ≥ 1.0 | **破甲** 4 秒：装甲脱落成碎屑 2 份，质量 × 0.6 更容易被推走；附带装甲清零 | 脱落的碎屑遇磁场 → F02 磁扫 |
| 磁化度（磁场、`en.mag`） | ≥ 1.0 | **磁化**：弱磁源 | 把附近弹体（含敌方）弯向自己，等于“吸弹”；也把碎屑、破片吸过来 |
| 着火度（明火、带火载荷、沾油遇热） | ≥ 1.0 | **燃烧**（4 × L） | 站在油上 → 油升温 → E01 爆燃 → 更大的火 |
| 入侵度（阶段 4） | ≥ 1.0 且阵亡 | **被劫持** | FGR-EMG-163 |

**斑块（斑块的相变就是物质转化，FGR-EMG-102）**

| 链 | 阈值 |
|---|---|
| 水 → 冰 → 水 → 蒸汽 → 水 | ≤ 0 度（或直接遇液氮 M04）；> 0 度持续 1 秒；≥ 100 度；蒸汽到期时 < 100 度落成水（V01） |
| 冷却液 → 冰 / 蒸汽 | ≤ −30 度（实际只靠液氮 M05）；≥ 130 度 |
| 油 → 明火 → 烟（+ 焦土，阶段 4） | ≥ 180 度；燃烧时每 2 秒排烟；在家园累计燃烧 ≥ 20 秒到期 → 焦土（V03） |
| 聚合胶 → 固化胶块 → 聚合胶 → 烟 | 遇酸（M16）；≥ 250 度；≥ 220 度 |
| 粉尘 → 玻化层 / 尘爆 | ≥ 300 度，浓度 < 0.6 → 玻化；≥ 0.6 → 尘爆。涡旋场可以把粉尘聚浓 |
| 熔融金属 → 冷渣 → 熔融金属 | < 600 度；≥ 900 度 |
| 冷渣 → 碎屑 | 遇酸耐久归零；或累计动能 ≥ 80 |
| 化工污泥 → 明火 | ≥ 260 度（慢燃） |
| 冰 → 破片 + 水；玻化层 → 破片 | 累计动能 ≥ 40；≥ 20 |

---

## 4. 反应去倍率重做

### 4.1 逐条改写总表（16 条具名 + 2 条装配）

- 旧数据见 `tools/fgdata_reaction.py:60-103`（底稿 02 §3.2、§3.3）。
- ID 与 `StableKey` 全部不变，开放方式全部由“按阵营批次”改为“首次发现即开放”（FGR-EMG-190）。
- 每条都写了“由哪些端口规则自然产生”：表里只有端口，没有任何内容 ID。“来源举例”至少列两类不同的拥有者，用来说明这条交互在正常游戏里有源（FGR-EMG-015 规则三），不是给某两个固件开的特例。

| # | ID | 名称 | 旧配料 → 旧结果 | 新主结果 | 规则行 | 附带 | 自然产生路径（端口 × 端口） | 来源举例（每侧 ≥ 2 类） |
|---|---|---|---|---|---|---|---|---|
| 1 | `reaction_steam` | 蒸汽（沿用） | 燃烧 + 浸湿 ×1.0，消耗浸湿，不留区域 | **S** `mat.steam`，烟幕失锁由 §5 遮蔽产生 | M12、M13、C02 | 无 | 水 / 冷却液斑块 × 明火斑块；浸湿沾染 × `en.heat` | 水侧：冷却液固件、管线泄漏、冲击波、蒸汽冷凝落水；火侧：燃迹、陷阱火带、爆燃产物、燃烧单位 |
| 2 | `reaction_deflagrate` | 爆燃（沿用，标杆） | 燃烧 + 油污 ×1.5，附燃烧，燃烧区 3 秒 / 2 米 | **S** `mat.fire`（整片） | E01、C01 | `side.damage 8` | 油斑块 × `en.heat`（≥ 180 度）；油污沾染 × `en.heat` | 油侧：漏油固件、泄漏燃油 / 原油、油污爬行者、洗礼塔油泵；热侧：明火、熔融金属、燃烧单位、带火弹体、写 `en.heat` 的指令、新生热渣 |
| 3 | `reaction_conduct` | 短路（沿用） | 浸湿 + 电击 ×1.3 | **P** `topo.arc`（麻痹由 `phase_charge` 产生） | E14、E15、C03 | 每节拍 `side.damage 2` | 水 / 冷却液斑块 × `en.elec`；浸湿沾染 × `en.elec` | 水侧：同 #1；电侧：电晕场、电弧链、电磁陷阱、麻痹放电、洗礼塔通电 |
| 4 | `reaction_shatter` | **碎裂**（原“脆裂”，建议改名） | 冻结 + 动能 ×1.4，**不可达** | **P** `topo.fragment` | P01 | `side.damage 20` | `phs.frozen` × `en.kinetic`（单次 ≥ 24） | 冻结侧：液氮斑块、带寒载荷、液氮泵车、冰面；动能侧：每一次命中（重炮、格斗）、尘爆、碎裂破片、撞击 |
| 5 | `reaction_sticky` | **固化**（原“粘滞”，建议改名） | 腐蚀 + 胶膜 ×1.0（反而去掉了胶膜的减速） | **T** `ter.block`（固化胶块） | M16、C06（B `bhv.stick`） | 无 | 聚合胶斑块 × 酸斑块；胶膜沾染 × 腐蚀沾染 | 胶侧：粘合剂固件、陷阱粘合剂带、胶结胀大；酸侧：酸液固件、腐蚀弹头、喷洒车、酸沼、泄漏酸液 |
| 6 | `reaction_electrolysis` | 电解（沿用） | 电击 + 腐蚀 ×1.25 | **S** `mat.debris` | E16、C08 | 无 | 酸斑块 × `en.elec` | 同 #3 电侧与 #5 酸侧 |
| 7 | `reaction_insulate` | 绝缘（沿用） | 电击 + 油污 ×0.6（克制，少掉的伤害算给目标） | **P** `topo.arc`（闪络改跳） | C04 | 无 | 油污沾染 × `en.elec` | 同 #2 油侧与 #3 电侧 |
| 8 | `reaction_thermalshock` | 热震（沿用） | 燃烧 + 冻结 ×1.4 | **P** `topo.fragment`（冻裂的外装甲崩成破片；破甲为相态附带） | P02 | 无 | `phs.frozen` × `en.heat`（单次 ≥ 30） | 同 #4 冻结侧与 #2 热侧 |
| 9 | `reaction_causticburn` | 腐蚀灼烧（沿用） | 燃烧 + 腐蚀 ×1.1，附燃烧 | **P** `topo.trail`（明火拖尾） | C05 | 无 | 腐蚀沾染 × `en.heat` | 同 #5 酸侧与 #2 热侧 |
| 10 | `reaction_hemolysis` | 回路溶断（沿用） | 回收 + 腐蚀 ×1.3 | **S** `mat.debris`（残骸溶成碎屑） | W01 | 无 | 酸斑块 × `ent.wreck` | 酸侧：同 #5；残骸侧：任何单位阵亡（FGR-EMG-113） |
| 11 | `reaction_syrup` | 胶结（沿用） | 浸湿 + 胶膜 ×1.15 | **S** `mat.polymer`（胀大） | M08、M09 | 无 | 水 / 冷却液斑块 × 聚合胶斑块 | 同 #1 水侧与 #5 胶侧 |
| 12 | `reaction_dissolve` | **乳化**（原“溶蚀”，建议改名） | 腐蚀 + 油污 ×1.2 | **S** `mat.sludge` | M03 | 无 | 油斑块 × 酸斑块 | 同 #2 油侧与 #5 酸侧 |
| 13 | `reaction_annihilate` | 湮灭（沿用） | 偏折 + 处决 ×1.6 | **P** `topo.burst` | F01 | `side.damage 40` | `prop.integrity`（≤ 0.15）× `fld.deflect` | 场侧：偏折固件、静默阵营偏折场；完整度侧：任何受伤单位 |
| 14 | `reaction_vitrify` | 玻化（沿用） | 扬尘 + 燃烧 ×1.3 | **S** `mat.glass` | E04 | 无 | 粉尘斑块 × `en.heat`（浓度 < 0.6） | 尘侧：曲射、牵引、多足震地（预留）；热侧：同 #2 |
| 15 | `reaction_mudify` | 淤塞（沿用） | 扬尘 + 浸湿 ×0.9（克制） | **S** `mat.sludge`（断开导电），单位层是 B `bhv.slow` | M14、M15、C07 | 无 | 水 / 冷却液斑块 × 粉尘斑块；扬尘沾染 × 浸湿沾染 | 同 #14 尘侧与 #1 水侧 |
| 16 | `reaction_sepsis` | 连锁锈蚀（沿用） | 污染 + 回收 ×1.35 | **P** `topo.fragment`（残骸锈蚀崩塌；连锁只靠破片动能，不做接触传播） | W02 | 无 | 污泥斑块 × `ent.wreck` | 污泥侧：子母弹污染坑、乳化、淤塞；残骸侧：同 #10 |
| A1 | `reaction_marktag` | 标记跳转（沿用） | 标记器 + 连射器 / 切割束 + 信号核标记跳转（`GL/Campaign/Content/MechanicalReactionCatalog.cs:80`） | **P** 连锁跳转（由 FG19A `fw_marktag` 写的 `grm.mod.chain`“只跳已标记”产生） | 无规则行 | 无 | 固件读 `evt.marked`、写 `grm.mod.chain`（条件：已标记），不经 `fg.TbChannelRule` | 标记侧：标记器部件、锁定缓存、广角等任何写 `evt.marked` 的来源；跳转侧：`fw_marktag` |
| A2 | `reaction_meltoverload` | 熔穿过载（沿用） | 铸造重炮机 + 过载：额外穿甲 +30%，每发热量 65（`MechanicalReactionCatalog.cs:95`） | **S** `mat.molten`（命中点溅出熔融金属，目标直接进入熔穿） | P03 | 无 | `phs.overheat`（本发注入 ≥ 0.15）× `en.kinetic`（≥ 40） | 热侧：写 `en.heat` 的指令；动能侧：重型射弹（重炮 55 × 2.0 = 110）、加重后的格斗（刺锤 14 × 2.2 × `fw_flare` 2 = 61.6；不加重的格斗最高 30.8，不够） |

- 两条装配反应的过渡豁免（FGR-EMG-021）：P03 上线、FG19A `fw_marktag` 改为端口声明后撤销，`MechanicalReactionCatalog` 的 ID 配对识别退役。行为回归要求（FGJ-M1）：
  - 熔穿过载：重炮伤害 55（`GL/Campaign/Regions/FracturedCityLayout.cs:111` `CannonBaseDamage`，由 `GL/Campaign/Combat/CombatSite.cs:693` 写入武器）× 2.0 = 110 ≥ 40 → 命中触发。注入：FG19A `fw_overload` 已改为每次命中注入 `en.heat` 45～60、无积热门槛，首发即满足“本发注入 ≥ 45（= 0.15 × 300）”，阈值不变。热量：每发 65 = 基础 40 + 25，这个 25 **不进过载声明的 `heat`**（否则所有载体上的过载每发都多 25），而是 P03 规则行的开火者代价 `cost.heat` = 25，**取代该指令表值 15**，不叠加（现状 `GL/Campaign/Combat/CombatSite.cs:698-700`）；内核在开火时按现有 `OverloadExtraHeat` 计入（与 `Sim/Combat/CombatLogic.cs:1164` 开火计热一致），判定条件 = 载体类别为重型射弹且根节点有 `en.heat` 写端，不按固件 ID。于是重炮 + 过载每发 65、其他载体 + 过载每发 = 基础 + 15。门控冷却：**保留** `ReactionSpent`（`Sim/Combat/CombatLogic.cs:1162-1163`，FG1-SIG-03）原语义——只有信号带进来的核心固件发动过一次后被压住，直到热更层按冷却重新下发武器参数；被压住期间 P03 不结算、`cost.heat` 也不计，回到表值 15；普通装配不受门控，每发都结算（与现状一致）。主结果从“本发穿甲 +0.3”改为“溅出熔融金属 + 目标进入熔穿”，穿甲断言标预期差异（第 12 节第 14 项）。
  - 标记跳转：**只有一套实现**，就是 FG19A `fw_marktag` 自己写的“只跳已标记、至多 2 跳”连锁；本附录不再设规则行（原 X01 已删），所以不会出现“固件自跳 2 次再被规则加 1 跳”。图鉴计数：内核结算一次“只跳已标记”过滤的连锁跳转、且确实跳到已标记目标时，发 `evt.react`（Code = `reaction_marktag` 的稳定键），计数与首次发现按这个稳定键记（FGR-EMG-086）。跳数断言：标记跳转 + 已标记目标 → 跳数等于声明值 2，不多不少。AI 驾驶时是否触发，取决于 AI 能否装 `fw_marktag`（FGR-SIG-090 与 FG19A 的口径），本附录不另加判定。
  - 预期差异：两条的触发面都变大（任何标记源 + `fw_marktag` 都能跳，不再要求标记器 + 连射器 / 切割束装配；任何重型带热命中都能熔穿），回归断言按“原组合结果一致（熔穿过载的穿甲项除外）+ 新组合可触发”写。

### 4.2 云端 REACT-01 的 4 条、FG11 计划的 4 条核对

| 来源 | 反应 | 在 16 条里的序号 | 本附录处理 | 说明 |
|---|---|---|---|---|
| 云端 FG10-REACT-01（只开放命名，没改任何倍率或规则行，底稿 05 §4.3） | 爆燃 | #2 | E01、C01 | 洗礼塔“火区引爆油区”自然适配（底稿 02 §4.2 结论） |
| 同上 | 腐蚀灼烧 | #9 | C05 | 打不到澄净（腐蚀抗性 1）这一点不变（底稿 05 §4.3） |
| 同上 | 胶结 | #11 | M08、M09 | — |
| 同上 | 溶蚀 → 乳化 | #12 | M03 | 打不到澄净这一点也不变：澄净单位不会沾酸 |
| FG11-REACT-01 计划（`cards:975-981`，Gated） | 电解 | #6 | E16、C08 | — |
| 同上 | 玻化 | #14 | E04 | — |
| 同上 | 回路溶断 | #10 | W01 | **配料重定**：不再需要超频固件挂“回收”，有残骸就能打出 |
| 同上 | 连锁锈蚀 | #16 | W02 | **配料重定**：同上 |

- 结论：8 条全部在 16 条之内，没有第 17 条“化工反应”或“超频反应”。
- 回改云端 REACT-01 的范围只剩三项：开放通知、图鉴打法提示 `reaction.<id>.tip`、`ReactionBatchService` 改为首次发现记录（FGR-EMG-190）。`.tip` 文本里是否写了倍率口径：待核实（底稿 02 §9.1）。
- FG11-REACT-01 在第 4 步改写为“四条已随 FG-EMG 阶段 2 首次发现开放，本卡只核对超频阵营带来的新端口源（FGR-EMG-192）”。

### 4.3 “不是固件 ID 特例”的证明口径

1. **表形状**：61 行的 `a`、`b`、`condition` 只含端口 ID、沾染位名、斑块 / 实体字段、`fg.TbEntityProfile` 列名。自检 `FgEmergenceRuleAuditSelfCheck` 扫非 owner 列，出现 `fw_` / `comp_` / `func_` / `struct_` / `chassis_` / `enemy_` 开头的字符串即失败（FGR-EMG-013）。
2. **有源**：每条规则两侧各有 ≥ 2 类正常游戏可得的写者（§4.1“来源举例”列、§1.5）。模糊测试器在固定用例集里每行至少触发一次（FGR-EMG-015 规则四、FGR-EMG-221）。
3. **对称性**：同一端口的不同来源走同一行。例：玩家的漏油、云端油污爬行者、洗礼塔油泵、管线燃油泄漏产生的都是 `mat.oil`，遇热都走 E01，内核不知道油是谁排的（只记 Owner 与因果链，用于归因，FGR-EMG-180）。
4. **固件不被点名**：旧版“标记跳转要标记器 + 连射器 + 标记跳转固件”这类三方 ID 识别退役，改成标记跳转固件自己读 `evt.marked`、写“只跳已标记”的 `grm.mod.chain`（FG19A），任何写 `evt.marked` 的来源都能配合它。

### 4.4 FG-GAP-052 / 053 / 054 的处理

| 缺口 | 现状 | 处理 | 关闭证据 |
|---|---|---|---|
| FG-GAP-052（`gap:63`） | 脆裂永远打不出来：44 条固件都不挂 `Physical` | 动能改为能量通道，每次命中都按载体系数产生（FGR-EMG-093、§6.3），P01 读 `en.kinetic`。`Physical` 位退出反应 | FGT-EMG-032 碎裂用例 + “未冻结目标受同样重击不碎裂”的对照 |
| FG-GAP-053（`gap:64`） | 蒸汽、粘滞 ×1.0，没有增益 | 蒸汽 → S 蒸汽烟幕（M12、M13、C02 + §5 遮蔽失锁）；粘滞 → T 固化阻路（M16）+ B 粘住（C06） | FGT-EMG-032 蒸汽失锁、聚合胶遇酸阻路两例 |
| FG-GAP-054（`gap:65`） | 跨阵营批次（6 条）没有 Story 负责开放命名 | 批次机制退役，所有具名交互首次结算即开放（FGR-EMG-190）；6 条全部有源（蒸汽、碎裂、固化、绝缘、湮灭、淤塞，见 §4.1） | FGT-EMG-062 首次发现 + FGT-EMG-030“每个 `named` 条目都有可触发行” |

- 三条的承接都改为 FG-EMG 阶段 2，第 4 步改登记册（FG19 第 19 节）。

### 4.5 命名建议（FG19 第 14 节第 13 项）

| ID | 现名 | 建议 | 理由 |
|---|---|---|---|
| `reaction_shatter` | 脆裂 | 碎裂 | 主结果是碎成破片飞散；与 FG19 正文用词一致 |
| `reaction_sticky` | 粘滞 | 固化 | 主结果是胶块阻路；“粘滞”留给减速语义 |
| `reaction_dissolve` | 溶蚀 | 乳化 | 主结果是油酸混成污泥；“溶蚀”更像腐蚀伤害 |
| 其余 13 条 | — | 沿用 | 新主结果仍然贴切 |
| 新增 12 条 | — | 浮油、冰封、冲淡、淬火、蒸干、焦烟、尘爆、冷却蒸腾、磁扫、焦土、熔坑、冻结桥 | 全部是机械 / 工业用语；已对照 FGR-EMG-232 禁用词表 |

- 只改文本键的值，ID 不改。英文名建议：Shatter、Cure、Emulsify。

---

## 5. 场 × 载体（`fg.TbFieldCarrierRule`）

- 字段按 FGR-EMG-122。“阵营”指场 / 物质的阵营与载荷阵营的关系；物质斑块中立，所以物质来源的行一律填 `any`。
- 抛射弹（`grm.mod.lob`）飞行中不受地面层斑块影响，落地时按落点结算。无人机本体按离地处理（不被地面层沾染），它打出的子弹按弹体处理。
- 每发弹体每种沾染只记一次（`once`）。

| `id` | source | target | effect | amount | once | faction |
|---|---|---|---|---|---|---|
| `fieldrule_corona_payload` | `fld.corona` | 弹体、无人机子弹、破片 | 带电 | 命中注入 `en.elec` 0.25 | 是 | own |
| `fieldrule_corona_unit` | `fld.corona` | 单位 | `phs.charge` | 0.3 / 秒 | 否 | hostile |
| `fieldrule_magnet_payload` | `fld.magnet` | 弹体、无人机子弹、破片 | `topo.redirect` 转向场心 | 90°/秒 × 被吸物磁性 | 否 | any（自己的磁场也会弯自己的弹） |
| `fieldrule_magnet_unit` | `fld.magnet` | 单位、残骸 | 吸引位移 + `phs.magnetize` | 位移 = 力 × 磁性 ÷ 质量；磁化 0.2 / 秒 | 否 | 单位 hostile，残骸 any |
| `fieldrule_deflect_payload` | `fld.deflect` | 弹体 | `topo.reflect`，并改为场方阵营 | — | 是 | hostile（沿用 `Sim/Combat/CombatLogic.cs:1591`） |
| `fieldrule_force_payload` | `fld.pull`、`fld.push`、`fld.vortex` | 弹体、无人机子弹、破片 | 位移 = 力 ÷ 质量 | — | 否 | 弹体 hostile，破片 any |
| `fieldrule_force_unit` | `fld.pull`、`fld.push`、`fld.vortex` | 单位、残骸 | 位移（`CanDisplace`；固定底盘、哨戒桩不动）；≥ `emg.impact.v_min` 撞上障碍时发 `evt.impact` | — | 否 | 单位 hostile，残骸 any |
| `fieldrule_fire_payload` | `mat.fire`、`mat.molten` | 弹体、无人机子弹、破片 | 带火：命中注入 `en.heat` 30、`phs.ignite` 0.3；已带油则 `topo.trail` | 拖尾每 2 米一块，≤ 6 块 | 是 | any |
| `fieldrule_oil_payload` | `mat.oil` | 弹体、无人机子弹 | 带油：命中使目标沾油污；**弹体已带火时，向这块油注入 +200 度，立即 E01 爆燃** | — | 是 | any |
| `fieldrule_water_payload` | `mat.water`、`mat.coolant` | 弹体、无人机子弹、破片 | 带水：命中使目标沾浸湿；去掉带火 | — | 是 | any |
| `fieldrule_cryo_payload` | `mat.cryo` | 弹体、无人机子弹、破片 | 带寒：命中 `phs.chill` +0.3；去掉带火 | — | 是 | any |
| `fieldrule_acid_payload` | `mat.acid` | 弹体、无人机子弹 | 带酸：命中沾腐蚀、`phs.corrode` +0.15 | — | 是 | any |
| `fieldrule_polymer_payload` | `mat.polymer` | 弹体、无人机子弹 | 速度 × 0.6，持续 1 秒；带胶：命中沾胶膜 | — | 是 | any |
| `fieldrule_dust_payload` | `mat.dust` | 弹体、无人机子弹 | 带尘：命中沾扬尘；**弹体已带火时，向这块粉尘注入 +300 度（E04 / E05）** | — | 是 | any |
| `fieldrule_smoke_payload` | `mat.smoke` | 弹体 | 寻的修饰失效 | 0.5 秒（`emg.smoke.lockloss_seconds`） | 是 | any |
| `fieldrule_steam_payload` | `mat.steam` | 弹体 | 寻的修饰失效 | 0.3 秒 | 是 | any |
| `fieldrule_obscure_unit` | `mat.smoke`、`mat.steam` | 单位 | `bhv.lockloss`：烟里（遮蔽 ≥ 0.7）的单位不能被烟外单位锁定，只有 3 米内能看到；蒸汽里（0.4～0.7）锁定距离上限 6 米；身在其中的单位自己的寻的失效 | — | 否 | any |
| `fieldrule_glass_payload` | `mat.glass` | 抛射落地、破片、带 `grm.mod.bounce` 的弹体 | `topo.reflect`：发 `evt.bounce`，按斑块边缘法线反射；落地的抛射弹再弹一段 = 原射程 × 0.4 | — | 每块 1 次 | any |
| `fieldrule_ice_payload` | `mat.ice` | 破片、抛射落地 | `topo.reflect`：再弹一段 = 原射程 × 0.25 | — | 每块 1 次 | any |
| `fieldrule_solid_absorb` | `mat.cured`、`mat.slag` | 弹体、破片 | 吸收：发 `evt.absorb`，扣斑块耐久 = 伤害。固化胶块挡所有穿过的弹体；冷渣只吸收射向其中、或其后 1.5 米内单位的弹体（低矮掩体） | — | 是 | any |

- 两处取舍：
  1. **直射弹体飞越玻化层和冰面时不反射**，只有低平载荷会被反射，防止地面斑块变成一堵挡子弹的墙（第 12 节第 6 项）。
  2. FG19 FGR-EMG-120 写的“穿过冷却液的弹体降温（本发不计积热）”：热量在出手时已经计过，事后无法返还。本附录改为“去掉带火沾染”，热量不返还（第 9 节第 10 条、第 12 节第 7 项）。

---

## 6. 实体与材质默认值（`fg.TbEntityProfile`）

### 6.1 材质（单位与残骸的默认属性来源）

| `id` | 导电 | 可燃 | 磁性 | 抗性：寒 | 热 | 电 | 腐蚀 | 磁 | 入侵 | `brittle`（碎裂阈值倍率） |
|---|---|---|---|---|---|---|---|---|---|---|
| `material.steel` 钢 | 0.9 | 0.1 | 0.9 | 0 | 0.1 | 0 | 0 | 0 | 0 | 1.0 |
| `material.alloy` 合金 | 0.7 | 0.05 | 0.3 | 0 | 0.3 | 0 | 0.3 | 0.5 | 0 | 1.0 |
| `material.composite` 复合 | 0.2 | 0.5 | 0.05 | 0.2 | 0 | 0.5 | 0.2 | 0.9 | 0 | 1.2（更难碎） |
| `material.ceramic` 陶瓷 | 0 | 0 | 0 | 0.3 | 0.6 | 0.8 | 0.6 | 1.0 | 0 | 0.6（更易碎） |
| `material.glass` 玻璃 | 0 | 0 | 0 | 0 | 0.4 | 0.9 | 0.9 | 1.0 | 0 | 0.4 |

### 6.2 实体种类默认值（FGR-EMG-111）

| 实体 | 质量 | 导电 | 可燃 | 磁性 | 备注 |
|---|---|---|---|---|---|
| 弹体 | 1；重型射弹 4；子弹药 = 母弹 ÷ 份数 | 0.9 | 0.1 | 0.9 | 光束类导电、磁性按 0（不被磁场弯） |
| 无人机 | 0.5 | 0.5 | 0.1 | 0.5 | 按离地处理；不吃伤害、不被选中（现状不变） |
| 哨戒桩 | 6（不位移） | 0.9 | 0.05 | 0.9 | 插地，可导电 |
| 机器 | 按底盘：轮式 6、履带 10、悬浮 4、固定 20（不位移）、多足 8、飞行 3、船体 12（后三者预留） | 按材质（默认钢） | 按材质 | 按材质 | 底盘 ID 只出现在 `fg.TbEntityProfile` 的底盘行，规则不读 |
| 敌人 | 8 × 半径²，限制在 1～80；首领 200（沿用 `CanDisplace` 不位移） | 按材质 | 按材质 | 按材质 | 敌人行新增 `material` 列（FGR-EMG-111） |
| 炮塔 | 20（不位移） | 按材质（默认钢） | — | — | — |
| 残骸 | = 原单位质量 | 继承 | 继承 | 继承 | 完整度 = 原最大耐久 × `emg.wreck.hp_ratio` |
| 破片 | 0.2 | 0.9 | 0 | 0.9 | §2.9 |

- 阵营默认材质（建议，第 4 步写进 FG09 / 敌方表时确认）：

| 阵营 | 材质 | 抗性覆盖 |
|---|---|---|
| 静默 `silent` | 复合 | — |
| 铸造 `foundry` | 钢 | — |
| 澄净 `clarity` | 陶瓷 | 腐蚀抗性 1，取代 `acid_proof` 的免疫位（FGR-EMG-103）；寒抗 0.5（第 12 节第 4 项）；`adapt_insulate` → 电抗性 1，仅在反制生效当天 |
| 超频 `overclock` | 合金 | — |
| 舰队 `fleet` | 钢 | — |

  阵营名取自 `tools/fgdata_enemy.py:25`。
- 精英：在阵营默认基础上全部抗性 +0.2（≤ 1）。首领：寒 0.8、入侵 1、电 0.5（FG19 第 14 节第 8 项）。首领阶段切换、反制锁定日（绝缘涂层电抗 1）可以在运行时改写抗性（洗礼塔泄压，§7 第 11 行），接口为 ADR-EMG-001 ⑦ 的 `SetResist`（每单位抗性覆盖，覆盖本表默认值）。

### 6.3 载体动能系数（FGR-EMG-093；`fg.TbSubPayload` 的同名列可以按子形态覆盖，由 FG19A 定）

| 载体 / 来源 | 动能系数 | 例：单次动能 |
|---|---|---|
| 射弹（连射器等） | 1.0 | 连射器 8 → 8（不够碎裂 24） |
| 重型射弹（重炮） | 2.0 | 重炮 55 → 110（碎裂、熔穿过载、残骸重击都够） |
| 光束（切割束） | 0 | 0 |
| 格斗 | 2.2 | 液压刺锤 14 → 30.8（够碎裂钢 / 合金 24、复合 28.8，是近战碎冰主力；不够熔穿过载 40）；推铲 9 → 19.8、旋刃环 8 → 17.6（都不够碎裂） |
| 无人机子弹 | 0.5 | 无人机 0.5 × 母组件伤害 10 = 5 → 2.5 |
| 力场脉冲 | 0.2 | 电晕场 7 → 1.4；震荡脉冲器 6 → 1.2 |
| 布区落区 | 0.3 | 喷洒器 6 → 1.8 |
| 破片 | 2.5 | 碎裂破片 10 → 25；锈蚀破片 6 → 15 |
| 撞击（`evt.impact`） | 质量 × 速度² × 0.05 | 推铲推 10 质量残骸、5 米 / 秒 → 12.5 |

- 伤害基线（取机器真实结算伤害，不是射程）：重炮 55（`GL/Campaign/Regions/FracturedCityLayout.cs:111` `CannonBaseDamage`，`GL/Campaign/Combat/CombatSite.cs:693` 写入）；液压刺锤 14、推铲 9、蜂群无人机舱 10、电晕场 7、喷洒器 6、旋刃环 8、震荡脉冲器 6（`tools/cell_tables/fgdata_reading.py` 的 `COMBAT_COMPONENTS` 伤害列，`:347` 起，刺锤 `:364`、电晕场 `:370`、喷洒器 `:372`）；连射器约 8 / 发、切割束 12 / 秒（表内伤害列为 0，按 Demo 手写组件与电路编译结果，底稿 07 §1.2）。
- 更正：上一稿把 `tools/cell_tables/fgdata_defense.py:82-90` `TURRET_PROFILES` 的射程列（18、14、26、5、14）当成了伤害，已全部重算。格斗系数由 1.5 调到 2.2，否则刺锤 14 × 1.5 = 21 < 24，打不碎冻结目标，与 FG19A `comp_ram` “近战碎冰主力”矛盾；碎裂阈值 24、熔穿过载阈值 40 不变。

---

## 7. 敌方 / 中立场地改为物质斑块后的行为对照

- “回归”列给第 5 步回归基线（M10 收尾）对应自检的处理口径：凡是依赖倍率或阵营的断言都标“预期差异”，不算退化（FG19 第 12 节）。
- 云端行号都是 `origin:`。

| # | 现状（出处） | 物质斑块后 | 差异 | 回归 |
|---|---|---|---|---|
| 1 | 喷淋池·酸液池（喷洒车、沉淀巨罐；`ZoneKindPool`，`CombatTypes.cs:79`；半径 2.5 / 8 秒 / 每秒 6，`tools/cell_tables/fgdata_enemy.py:268-271`）；属于敌方阵营，只打己方单位与炮塔、建筑 | 敌方单位声明排放的 `mat.acid`（`ownerKind = enemy`，FG19A），中立；每秒 6 作为这条声明的接触伤害参数保留。对炮塔、建筑**照常结算**：不复用跳过炮塔 / 建筑的中立区域分支，炮塔在内核、建筑走受击聚合脏列表（ADR-EMG-001 ⑤），迁移前后一致。迁移由 EMG2-MAT-02 承接 | ①别的阵营单位走进去也会被腐蚀；澄净自己腐蚀抗性 1，仍然不受影响（与现状一致）。②能被水 / 冷却液冲淡（M06、M07，= 云端冲洗）。③新：可以通电电解出碎屑（E16）、遇聚合胶固化成阻路（M16）、遇油乳化（M03） | `FgClarityFactionSelfCheck` 喷淋池用例：伤害相同，阵营改中立，标预期差异 |
| 2 | 液氮池（液氮泵车；半径 2.5 / 6 秒 / 受寒 0.6，`clarity.pool.chill_tick_frac` 0.5） | `mat.cryo` 中立；受寒走 `phase_chill`，数值相同 | ①**澄净单位踩进自家液氮池也会受寒**（建议澄净寒抗 0.5，第 12 节第 4 项）。②遇明火蒸干（M18，= 云端）。③新：遇水冰封（M04）、遇熔融骤冷 | 冻结时长、解冻免疫用例数值不变 |
| 3 | 冲洗规则（`CombatTypes.cs:77-78` 注释，内核澄净专属分支） | M06、M07、M18 通用表 | 任何来源的水都能冲淡任何来源的酸；专属分支删除 | 冲洗用例改走通用规则，结果一致 |
| 4 | 油迹（油污爬行者；`ZoneKindSlick`，半径 1.2、挂油 10 秒，整片反应同液洼） | `mat.oil` 中立，遇热 E01 爆燃整片 | 等价；新：浮油（M01）、乳化（M03）、带火弹体穿过即点燃（§5） | 油迹点燃用例一致（`SlicksIgnited` 计数改按 E01 稳定键计） |
| 5 | 油污爬行者自爆，以及被火击毁时的中立爆燃（`deflag = 1.5` 倍率，`fgdata_enemy.py:272-275`；`CombatClarityPending.Kind = 1`） | 自爆本体保留为敌方行为（伤害 40 不变）。“被火击毁 → 爆燃”改为：敌方声明 `evt.death` → 排放 `mat.oil` 15 份；正在燃烧时由 E01 / C01 自然爆燃 | **去掉 ×1.5**：伤害从 40 × 1.5 = 60 变为爆燃附带 8 + 燃烧区持续伤害（预期变弱），由 FG15-BAL-01 复核（第 12 节第 5 项） | 标预期差异 |
| 6 | 分裂液（沉淀巨罐阵亡分出 3 滩，是**单位**：耐久 60、速度 1.2、存活 20 秒、每秒 8，`fgdata_enemy.py:287-290`） | 仍然是单位：会爬、会追机器，属于行为，不是物质。可选：分裂液阵亡 / 到期时留下 `mat.acid` 小斑块（半径 1，敌方声明） | 不选可选项时无差异 | 不变 |
| 7 | 受寒 / 冻结（`CombatClarityUnit.Chill / FrozenT / ThawGuardT`，`CombatTypes.cs:578-613`） | `phase_chill`，数值相同（§3.3） | 推广到所有单位与所有寒源；本机 `Frozen` 标签的易伤 +25% 取消（FG19 第 14 节第 7 项） | 澄净受寒用例一致 |
| 8 | 空中单位（灭活无人机 `Air = 1`：不受地形与地面区域影响；格斗 / 力场 / 布区打不到） | `chs.airborne` 语义：地面层斑块不接触、不沾染；气体层（烟、蒸汽、粉尘）照常作用 | 新：灭活无人机在烟里失锁 | 地面区域对空用例一致；新增“空中单位在烟里失锁”正向用例 |
| 9 | 洗礼塔灌注（中立流体区域，`GL/Campaign/Regions/BaptismTowerBoss.cs:715-761`；半径 6 格 / 40 秒 / ≤ 8 片，`fgdata_clarity.py:33-37`） | 世界源斑块 `mat.oil` / `mat.acid` / `mat.water`，中立，占世界源保留额，片数上限仍由首领自己控制 | 等价。新：玩家可以对灌注片做任何物质交互（冰封水片、给油片打带火弹提前引爆、在酸片上铺胶固化成掩体） | 灌注节拍、片数、预警一致 |
| 10 | 洗礼塔引发（`TickReact`；`TriggerOf` `:1049`：水 → 电区，油 / 酸 → 火区；区域每秒伤害 `react_dps` 8，`fgdata_clarity.py:41`） | 油片：注入 `en.heat` +300 度 → E01 整片爆燃；水片：注入 `en.elec` 1.0 → E14 导电水洼；酸片：盖一块存续 2 秒的世界源明火 → 片里沾酸的单位触发 C05 拖火，所有单位受明火接触 | ①去掉倍率，“当场起爆燃 / 腐蚀灼烧 / 短路”改为四类主结果。②`react_dps` 8 保留为世界源火 / 电的接触伤害参数（等价迁移；是否退役见第 12 节第 12 项）。③“它自己的小兵同样挨”仍成立（中立）；澄净小兵腐蚀抗性 1，不会沾酸，不触发 C05，与现状“腐蚀灼烧打不到澄净”一致 | `FgBaptismTowerSelfCheck` 反应阶段：“起反应”断言改为“油片变明火 / 水片带电放弧 / 酸片拖火”，标预期差异 |
| 11 | 洗礼塔泄压（封闭伤害 ×0.5、泄压 ×2；`SetImmuneMask` 切换冻结位，`BaptismTowerBoss.cs:835-839`） | 首领受伤倍率是首领机制，不是反应，保留。冻结免疫改为寒抗 1 ↔ 0 切换（FGR-EMG-103），经 ADR-EMG-001 ⑦ 的运行时抗性覆盖 `SetResist` 实现（原 `SetImmuneMask` 调用方改调它：Frozen → 寒抗 1，清位 = 覆盖回 0）。“先冻再烧 = 热震”改由 P02 热震（外装甲崩成破片 + 进入破甲相态）实现 | 热震从 ×1.4 变为崩出装甲破片 + 破甲相态（泄压期本来就 ×2，破甲附带叠在首领倍率之上） | 热震自检改断言“冻结 + 受热 → 解冻、崩出装甲破片、进入破甲”（云端复核修复已把它改成“冰冻 + 会烧的区域”，底稿 05 §4.3） |
| 12 | 酸沼（`SetAcidMarsh`，`Sim/Combat/CombatKernel.cs:667`；每秒损失最大耐久 2%，浸湿 × 0.5，防腐涂层抗性 0.9；只对机器，炮塔与建筑不受影响，`fgdata_clarity.py:23-26`） | 常驻世界源 `mat.acid`，实现为虚拟世界源（不实体化为斑块、不进空间哈希、不占斑块槽，按点是否在危害区内查询；不被规则转化、不衰减、不合并，§1.4 第 11 条；ADR-EMG-001 ⑭ 第 13 条，由 EMG2-MAT-02 承接）；接触伤害保留“按最大耐久 2% / 秒”（参数来自 `fg.TbHazard`，不进 `fg.TbMatter`）；浸湿 × 0.5 与腐蚀抗性同口径；作用对象沿用“机器与敌人” | 新：酸沼可以被通电（传导限制在注入点 8 米内）、电解出碎屑；能和聚合胶固化成阻路（在沼泽里铺掩体）；能和油乳化。水不会把整片沼泽冲淡：水斑块被沼泽吸收，期间里面的单位按浸湿 × 0.5 | 酸沼腐蚀速率、涂层用例一致 |
| 13 | 防腐涂层（`CombatData.PlayerAcidResist`，`CombatData.cs:98`） | 机器腐蚀抗性的运行时下限 0.9，由任务蓝图设置（不经固件） | 等价 | 一致 |
| 14 | 腐蚀蜂群（`comp_shovel` + `fw_acid` 装配）、灭活无人机投放（格网再污染） | 蜂群：命中按敌方装配的酸液声明沾腐蚀。投放再污染是热更层格网污染等级（`PurificationService`），**不是物质斑块，不迁** | 无 | 不变 |
| 15 | 反制服务 `ClarityCounterService`（按固件**标签**占比判反制，`ClarityCounterService.cs:125-170`） | 第 4 步改为按“声明写端口”占比（例：写 `en.heat` / `mat.fire` 的指令占比 ≥ 0.3 → 液氮压制），由 EMG2-BOSS-01 承接（FG10-FAC-01 只在第 4 步改承接行） | 口径从标签换成端口，阈值不变 | 反制用例改输入口径 |
| 16 | 陷阱场地（FG6-DEF-02，`tools/fgdata_structures.py:139-148`） | 按 §1.5 映射；电磁场保留为区域，并作为 `en.elec` 注入源；减速保留为区域 | 陷阱铺的油 / 冷却液 / 酸 / 胶 / 火从“挂标签区域”变成真物质，能和别的物质反应 | FGJ-M6 火墙等价（FGT-EMG-035） |
| 17 | 管线泄漏液洼（`UpsertLeakZone` / `PipeLeakService`） | 世界源斑块，热更层对账与 48 摊上限保留；已反应阶段 = E01 产物明火；`PipeLeakService.Burn`：热更层不遍历斑块，改读内核上报的燃烧格（ADR-EMG-001 ⑤） | 新：泄漏的水 / 冷却液能冲淡酸、能被冻成冰、能通电 | “泄漏遇火烧产线”等价（FGT-EMG-035） |
| 18 | 熔渣热区（铸造，`SlagHeatService`，区内散热 × 0.7） | 保留为危害区，登记为 `en.heat` 世界源（满足有源） | 无。若阶段 2 想物质化，改为常驻 `mat.slag`（定温 600），由 ADR 决定 | 不变 |
| 19 | 静默偏折场（`TryReflectProjectile`，`CombatLogic.cs:1591`） | `fld.deflect`（`fieldrule_deflect_payload`，等价） | 新：F01 湮灭对双方都成立：玩家残血单位走进静默偏折场同样会被压溃（敌人也用同一套，FGR-EMG-160） | 反射用例一致 |

---

## 8. 性能与容量（需求侧上限；实现与实测由 ADR-EMG-001 定）

### 8.1 每类物质的斑块上限

- 总上限 `emg.patch.capacity` = 1024 / 地点（FG19 FGR-EMG-082），划分如下：

| 用途 | 上限 | 说明 |
|---|---|---|
| 世界源保留 `emg.patch.reserve_world` | 96 | 管线液洼 48（`logistics.leak.max_puddles`）+ 首领灌注 ≤ 8 + 余量 40（原给危害带 / 常驻斑块的 16 已回收进余量：酸沼是虚拟世界源，不占斑块槽，§1.4 第 11 条）；世界源不挤占战斗斑块，战斗斑块也不挤占世界源 |
| 战斗产生（17 种物质软上限之和） | 928 | 见 §1.2“软上限”列：油 88、水 64、冷却液 72、液氮 48、酸 72、胶 48、固化胶块 40、熔融 32、冷渣 48、明火 96、烟 56、蒸汽 56、碎屑 64、粉尘 40、冰 40、玻化 32、污泥 32 |
| 其中家园 / 前哨长期留存固态 | ≤ `emg.persist.patch_max` 128 | 固化胶块、冷渣、冰、玻化层，计入各自软上限 |

- 单块半径上限 `emg.patch.max_radius` 8 米（世界源除外）。合并后超过时不再合并，在旁边另起一块。
- 软上限的作用：某种物质已到上限时，同种新斑块先就近并入 6 米内的同种斑块（不受重叠阈值限制）；仍然不行，就让**同种**里最早的提前到期。这样一种物质刷屏不会挤掉别的物质。总上限满时按 FGR-EMG-082（最早的同层斑块提前到期）处理。所有降级都计数，并在调试层显示。

### 8.2 合并与衰减

| 类别 | 物质 | 合并 | 衰减 |
|---|---|---|---|
| 液体 | 油、水、冷却液、酸、胶、污泥、熔融 | 同物质、重叠比 ≥ 0.5；份量相加，温度、电荷按份量加权（FGR-EMG-082） | 存续最后 25% 时间里半径线性缩到 0（“快干了”可读）；熔融按温度转冷渣 |
| 低温 | 液氮 | 同上 | 与 > 0 度的斑块重叠时每节拍份量 −5%（吸热蒸发）；遇明火走 M18 |
| 燃烧 | 明火 | 同上 | 燃料份量按初始份量 ÷ 存续，每秒消耗；燃尽发 `evt.expire` |
| 气体 | 烟、蒸汽、粉尘 | 重叠比 ≥ 0.3 | 半径按扩散值增大、浓度下降，< 0.2 或存续到期即消失 |
| 碎屑 | 金属碎屑 | 重叠比 ≥ 0.5 | 存续到期消失；被磁扫消耗；被拾取消耗（阶段 4 预留） |
| 固态 | 固化胶块、冷渣、冰、玻化层 | **不合并**（保持形状，阻路与桥面不会突然变形） | 耐久归零或寿命到；家园 / 前哨长期留存 |
| 世界源 | 液洼、灌注片、酸沼 | 不合并 | 由来源控制（热更层对账 / 首领 / 危害带） |

### 8.3 每步评估预算（60 Hz 固定步；斑块节拍、传导节拍都是 0.25 秒 = 15 步）

| 项 | 需求侧上限 | 做法 |
|---|---|---|
| 斑块节拍 | 每步结算 ≤ ⌈1024 ÷ 15⌉ = 69 块（`emg.patch.per_step`） | 每块斑块自带 `NextTick`（生成时刻不同，天然错相）；到期斑块按 (`NextTick`, `Id`) 升序取前 69 块结算，其余顺延到下一步、下一步优先，顺序固定（FGR-EMG-213）。不用“按斑块下标轮转分桶”：斑块会压实、合并，下标分桶会漏拍或重拍（ADR-EMG-001 ⑤） |
| 邻接查询 | 每块 ≤ `emg.patch.max_neighbors` 8 块 | 空间哈希，格边长 4 米（FGR-EMG-081），只取最近 8 块 |
| 规则查找 | O(1) | 17 × 17 物质对表 + 17 × 4 能量阈值表 + 沾染对表，预先编成下标表，不按行扫描 |
| 规则结算 | 每步 ≤ `emg.rule.settle_per_step` 32 条 | 超出的留到下一节拍并计数（与“同时满足只结算一条”同一做法） |
| 产物生成 | 每步 ≤ `emg.patch.spawn_per_step` 32 块 | 超出的进生成队列顺延，生成时才开始计寿命（FGR-EMG-063） |
| 单位接触 | 每步 ≤ ⌈400 ÷ 15⌉ = 27 个单位，每个 ≤ 4 块 | 400 = 敌人 200 + 机器 120 + 炮塔 80（FG15 规模预算） |
| 传导 | 每节拍节点访问 ≤ 256，分桶后每步 ≤ 18 | 6 跳、衰减 0.7、`q_min` 0.05、大斑块 8 米 |
| 相态 | 每节拍只处理累积量非零的单位 / 残骸（≤ 656 = 400 + 256），分桶后每步 ≤ 44 | — |
| 弹体 × 斑块 | 每发弹体每步 1 次“哈希格物质掩码”查询（17 位）；掩码命中才做圆相交（≤ 4 块），预计每步 ≤ 1500 次掩码 + ≤ 600 次相交 | 掩码在斑块增删时更新；监听掩码（FGR-EMG-062）里没有人读的物质事件不发 |
| 场 × 实体 | ≤ 64 个场 × 哈希内实体 | FGR-EMG-112 |

- 时间预算：本附录不再单列总数，以 ADR-EMG-001 ⑬ 的“远征地点涌现新增 0.70 ms / 步”分解为准。其中与本附录相关的四项：斑块节拍 + 规则 + 哈希重建 0.15、弹体 × 斑块 / 场 / 障碍查询 0.08、传导（摊到每步）+ 相变 0.08、场对实体位移 / 残骸 / 破片 / 动态通行 0.07，合计 0.38 ms / 步（“涌现满载场景”，FGR-EMG-203，p99）；其余 0.32 ms 是程序执行、事件总线与因果链。0.70 ms 是 FG19 FGR-EMG-200 全世界涌现新增 1.0 ms 中分给远征地点的部分（家园 0.20、前哨与室内 0.10）。上一稿的“≤ 0.45 ms”作废。帧率按 120 帧口径；FG15 写的 60 帧与之冲突，登记 FG-GAP-154，**待用户确认**（FG19 第 14 节第 1 项）。
- 热更层：本附录的所有机制都在 `Sim/` 内核，热更层只读规则计数脏列表与少数玩法事件（焦土、熔坑、冻结桥走“地面标记”玩法事件，受每步 64 条限制）。热更层每帧开销与斑块数、单位数无关（FGR-EMG-201）。管线液洼对账沿用现有每 0.5 秒 O(液洼数) 的做法。

### 8.4 降级顺序（都计数、可见，不静默）

1. 同种斑块就近合并；
2. 同种最早的提前到期；
3. 总上限满时，同层最早的提前到期；
4. 规则结算顺延到下一节拍；
5. 产物生成进队列顺延；
6. 传导截断（访问数到上限，链末端记“传导截断”）。

- 世界源保留额不参与 1～3，常驻斑块永不被挤掉。

---

## 9. 对 FG19 的细化、更正与新取值申请（16 条均已并入 FG19，2026-10-26；以 FG19 当前正文为准）

| # | 内容 | 涉及 FG19 条款 | 理由 | 状态 |
|---|---|---|---|---|
| 1 | `pairKind` 新增一个取值：`matter_entity`（物质 × 实体端口，W01、W02）。原申请的 `event_event` 撤回（X01 已删，标记跳转由 FG19A 固件声明承接） | FGR-EMG-084 | 回路溶断、连锁锈蚀要读残骸实体 | 已并入 FG19（2026-10-26）→ FGR-EMG-084（`matter_entity`；`event_event` 撤回） |
| 2 | `coat_matter` 的 `b` 可以是物质端口、能量端口，或另一个沾染位 | FGR-EMG-084 | 旧“两个标签配对”的单位层反应（C01～C08）需要“沾染 × 能量”“沾染 × 沾染” | 已并入 FG19（2026-10-26）→ FGR-EMG-084 |
| 3 | 规则可以带伴生产物 `params.by`，伴生同样只能取 §2.9 封闭清单，自检一并校验；主结果类由 `mainKind` 决定 | FGR-EMG-085 | 淬火（冷渣 + 蒸汽）、蒸汽（灭火 + 蒸汽）等一次结算产生两种物质 | 已并入 FG19（2026-10-26）→ FGR-EMG-085、FGR-EMG-020 第 2 条 |
| 4 | 推斥 / 牵引 / 涡旋场推动气体层斑块，涡旋把粉尘聚浓 | FGR-EMG-112 | 把“6 种实体受场作用”扩到气体斑块；尘爆要靠它聚浓 | 已并入 FG19（2026-10-26）→ FGR-EMG-112 |
| 5 | `fg.TbMatter` 新增列：`tempInit`、`fixedTemp`、`hp`、`quota`、`mergeOverlap`、`decayMode`、接触效果若干列、`emit`（燃烧排烟）、`pickup`（阶段 4 预留，阶段 2 填 0）、`pattern` | FGR-EMG-080 | §1.2 的数值需要落表 | 已并入 FG19（2026-10-26）→ FGR-EMG-080 |
| 6 | 斑块新增字段：耐久、累计动能、标志（常驻不可变 / 被油覆盖 / 长期留存）、气体浓度 | FGR-EMG-081 | §1.4 第 7、11、12 条 | 已并入 FG19（2026-10-26）→ FGR-EMG-081 |
| 7 | 相态状态与累积量另存于实体“相态块”，不占 `Status` 位；新沾染只占 26～29 | FGR-EMG-020 第 5 条 | §3.2 结论：这样 32 位够用；否则缺 4 位，要扩 64 位 | 已并入 FG19（2026-10-26）→ FGR-EMG-020 第 5 条、FGR-EMG-083 |
| 8 | 弹体结构新增 1 字节沾染位（8 种） | FGR-EMG-120 | §3.2 | 已并入 FG19（2026-10-26）→ FGR-EMG-120（位宽归 ADR-EMG-001 ③） |
| 9 | 总上限 1024 里划出世界源保留额 96 | FGR-EMG-082、FGR-EMG-202 | 世界源不能被战斗斑块挤掉（酸沼、液洼、首领灌注） | 已并入 FG19（2026-10-26）→ FGR-EMG-082 |
| 10 | 三处更正：①`SlagZones` 是熔渣**热区**，不是冷渣来源（FGR-EMG-080 来源列，`SlagHeatService.cs:16-24`）；②FGR-EMG-120“本发不计积热”改为“去掉带火沾染，不返还热量”；③FGR-EMG-086 中“短路 P + B”“蒸汽 S + B”统一写成单一主结果，B 由相态规则 / 场 × 载体表产生 | FGR-EMG-080、086、120 | 以底稿与可实现性为准 | 已并入 FG19（2026-10-26）→ FGR-EMG-080、FGR-EMG-120、FGR-EMG-086 |
| 11 | `emg.conduct.q_min` = 0.05 | 第 18 节 | FG19 留给本附录定 | 已并入 FG19（2026-10-26）→ 第 18 节 |
| 12 | 自检 `FgChannelRuleShapeSelfCheck` 不要求封闭清单里每个产物都被用到（`ter.slick`、`ter.cover` 由物质属性提供） | FGR-EMG-085 | §2.9 末条 | 已并入 FG19（2026-10-26）→ FGR-EMG-085 |
| 13 | `named` 列语义（一个图鉴条目可以对应多行，按 `named` 的稳定键记次数与首次发现）；`reaction_marktag` 是唯一没有规则行的图鉴条目，由“只跳已标记”连锁跳转发 `evt.react` 计数 | FGR-EMG-020 第 2 条、FGR-EMG-183、FGR-EMG-086 | §0.4、§4.1 A1 | 已并入 FG19（2026-10-26）→ FGR-EMG-020 第 2 条、FGR-EMG-084 |
| 14 | 从 §2.9 的 B 类清单删除 `bhv.breach`；FGR-EMG-101 “破甲 `phs.breach`：装甲清零（B `bhv.breach`）”改为“装甲脱落成碎屑（S）+ 质量下降更易被推走；装甲清零为附带”。自检 `FgChannelRuleShapeSelfCheck` 加断言：B 类产物必须改变目标行为（失锁、过热停机、麻痹、冻结、粘住、减速、推走、劫持），不得只是改承伤数值 | FG19 §2.9、FGR-EMG-101 | 用户口径“主结果不能是数值”；DESIGN.md 第 2 节第 3 类只列失锁、过热停机、被劫持、被推走 | 已并入 FG19（2026-10-26）→ 2.9 节、FGR-EMG-101、FGR-EMG-085 自检断言 |
| 15 | `matter_entity` 行的条件可以同时读“残骸在斑块里的停留秒数”和“残骸本次受到的单次动能”（W02 第二个条件），不新增残骸累计动能字段；残骸重击（单次 ≥ 40）不变 | FGR-EMG-114 | 连锁锈蚀去掉接触传播后，靠破片动能连锁 | 已并入 FG19（2026-10-26）→ FGR-EMG-114 |
| 16 | `fg.TbChannelRule` 新增列 `cost.heat`（开火者代价，取代相关指令表值热量，不叠加；只有 P03 填 25）；P03 保留 `ReactionSpent` 门控冷却原语义 | FGR-EMG-016、FGR-EMG-020 第 2 条、FGR-EMG-021 | 熔穿过载的 +25 热量只属于重炮 + 过载组合，不能进过载声明（FG19A §1.2 表后注、§8.1 A14） | 已并入 FG19（2026-10-26）→ FGR-EMG-016、FGR-EMG-020 第 2 条（含 P03 `ReactionSpent` 门控） |

---

## 10. 本附录新增调参键（进 `fg.TbHomeTuning`，前缀 `emg.`；不与 FG19 第 18 节重复）

| 键 | 初值 | 键 | 初值 |
|---|---|---|---|
| `emg.heat.ambient` | 20 度 | `emg.heat.exchange` | 0.4 |
| `emg.heat.patch_gain` | 10 | `emg.heat.cool_rate` | 40 度 / 秒 |
| `emg.heat.unit_overheat_scale` | 300 热量 = 积热 1.0 | （`emg.conduct.q_min` 0.05 已登记在 FG19 第 18 节，此处不重复） | — |
| `emg.conduct.unit_share` | 0.6 | `emg.conduct.patch_decay` | 0.5 / 节拍 |
| `emg.conduct.big_patch_radius` | 8 米 | `emg.conduct.max_visits` | 256 / 节拍 |
| `emg.patch.reserve_world` | 96 | `emg.patch.max_neighbors` | 8 |
| `emg.patch.max_radius` | 8 米 | `emg.patch.spawn_per_step` | 32 |
| `emg.rule.settle_per_step` | 32 | `emg.gas.min_conc` | 0.2 |
| `emg.dust.burst_conc` | 0.6 | `emg.obscure.full` | 0.7 |
| `emg.obscure.partial` | 0.4 | `emg.obscure.partial_range` | 6 米 |
| `emg.obscure.inside_range` | 3 米 | `emg.react.annihilate_integrity` | 0.15 |
| `emg.matter.pickup_scrap` | 0.5 废料 / 份（阶段 4 预留） | `emg.patch.per_step` | 69 块 / 步（ADR-EMG-001 ⑤） |

- 规则阈值（温度、电荷、动能、重叠比）写在 `fg.TbChannelRule.condition`，相态阈值写在 `fg.TbPhaseRule`，破片与系数写在 `fg.TbEntityProfile`，都不另设调参键，避免一个数两处维护。
- 世界级总上限 `emg.patch.world_capacity`、`emg.bus.world_per_step` 由 FG19 第 18 节登记（ADR-EMG-001 ⑬），本附录不重复。
- 新前缀已避开云端占用的 `clarity.*`、`hazard.acid_marsh.*`、`boss.clarity.*`、`raid.clarity.*`（底稿 05 §5.5）。

---

## 11. 验收要点（对 FG19 测试编号的具体断言；每条正向配一条排除假阳性的对照，00 契约）

| 测试 | 本附录给出的断言 |
|---|---|
| FGT-EMG-030 规则表形状 | 61 行的 `mainKind` ∈ S/T/B/P；`product` 与 `params.by` 都在封闭清单；`side` 只有 `side.damage` 或 none（**零个 `side.mult`**）；**零个 `bhv.breach` 产物**，B 类行的产物都改变目标行为；16 + 2 条 `id` 与 `StableKey` 和 `tools/fgdata_reaction.py` 现值一致；除 `reaction_marktag`（由连锁跳转发 `evt.react` 计数）外，每个 `named` 条目至少有 1 行可触发；对照：构造一行“只有 `side.damage`、`mainKind` 为空”的假规则，以及一行“`mainKind` = B、`product` = `bhv.breach`”的假规则，都必须被拒 |
| FGT-EMG-031 物质行为 | 17 种物质各一例：排放、沾染（含己方中立）、按衰减方式到期；固态不合并、液体按 0.5 合并、气体按 0.3 合并；同种软上限满时只挤掉同种（对照：另一种物质的斑块数不变）；世界源不被挤掉 |
| FGT-EMG-032 提案定稿 5 条 | 爆燃（油 + 热 → 整片明火；只有油、只有热都不发生）；导电水洼（水 + 电 → 斑块内含己方单位每节拍得电荷，满 1.0 麻痹；对照：无水只打单体）；碎裂（冻结 + 单次动能 ≥ 24 → 破片；液压刺锤 30.8、重炮 110 都能碎钢材质冻结目标；对照：未冻结的目标、或动能 8 的连射器都不碎）；标记跳转（`fw_marktag` + 已标记目标 → 跳数等于声明值 2，图鉴 `reaction_marktag` 计数 +1；对照：没有已标记目标时不跳、不计数）；蒸汽烟幕（水 + 明火 → 蒸汽，穿过的寻的弹失锁 0.3 秒；对照：无蒸汽时寻的照常）；固化阻路（胶 + 酸 → 固化胶块阻路挡弹，里面的单位被推出；对照：只有胶只减速） |
| FGT-EMG-033 传导 | 三块相连水洼，从第 1 块注电，第 3 块里的单位得到电荷；把第 2 块用粉尘淤塞成污泥（M14）后传不到第 3 块；油与明火重叠 0.3 时 ≤ 0.75 秒点燃；酸沼大斑块只在注入点 8 米内传导 |
| FGT-EMG-034 相变 | 7 行相态的进入、退出、guard、抗性；`phase_chill` 与云端受寒同数（减速、2.5 秒、免疫 1.5 秒）；燃烧 L 档在着火度 1.0 / 1.25 / 1.5 时为 1 / 2 / 3；首领寒抗 0.8 冻不住，泄压时可冻 |
| FGT-EMG-035 区域迁移等价 | FGJ-M6 火墙、管线泄漏遇火烧产线、洗礼塔灌注 / 引发、油迹点燃、喷淋池冲洗：迁移前后的伤害、时长、片数一致；§7 标“预期差异”的项单独列出并说明 |
| FGT-EMG-036 场与载体 | 穿过电晕场的弹体带电；穿过烟的寻的弹失锁；带油弹体穿过明火拖出火线；带火弹体穿过油斑块立即爆燃；落在玻化层上的抛射弹反弹，而直射弹体飞越不反弹（对照）；磁场让弹体偏转、吸起碎屑成破片（F02） |
| FGT-EMG-037 实体与残骸 | 残骸在酸里 2 秒开始溶解、归零变碎屑（W01）；残骸在污泥里 3 秒锈蚀崩塌成破片 + 碎屑（W02）；锈蚀破片打到同一片污泥里停留 ≥ 1 秒的相邻残骸使其崩塌（链深 3 截止）；对照：同样的破片打到污泥外的残骸不崩塌，相邻残骸只“接触”不受击也不崩塌；残骸受单次动能 ≥ 40 崩出 2 片破片 |
| FGT-EMG-004 规则审计 | 61 行的非 owner 列不含任何内容 ID；P03 上线、`fw_marktag` 改为端口声明后装配豁免为 0；FGJ-M1 回归：铸造重炮 + 过载**首发**即熔穿（溅出熔融金属、目标进入熔穿），本机积热 65；对照：其他载体 + 过载每发只加表值 15、不触发 P03；信号带入核心固件被 `ReactionSpent` 压住期间不触发、积热回到表值 |

---

## 12. 待用户确认（本附录范围；与 FG19 第 14 节不重复）

| # | 问题 | 本附录推荐 | 影响 |
|---|---|---|---|
| 1 | 三条改名：脆裂 → 碎裂、粘滞 → 固化、溶蚀 → 乳化 | 改（只改文本，ID 不变） | §4.5、FG02 反应表 |
| 2 | 新增 12 个具名交互全部进图鉴 | 进；阶段 4 的 3 个（焦土、熔坑、冻结桥）随阶段 4 开放 | 图鉴条目 30 个；成就阈值（FG15-SYS-04） |
| 3 | 电击“易伤 +10%”取消，改为电荷累积 → 麻痹（与冻结同性质） | 改 | §3.1 位 4、`phase_charge` |
| 4 | 物质中立后，澄净单位会被自家液氮池冻住 | 允许，澄净寒抗 0.5（打法：“把它的小兵引进它的液氮池”，与洗礼塔借力打力同一口径） | §6.2、§7 第 2 行 |
| 5 | 油污爬行者被火击毁的爆燃伤害由 60 降为附带 8 + 燃烧区 | 接受（去倍率的直接结果），由 FG15-BAL-01 复核 | §7 第 5 行 |
| 6 | 玻化层 / 冰面只反射低平载荷，直射弹体飞越不受影响 | 是 | §5 |
| 7 | 穿过冷却液的弹体：热量不返还，只去掉带火 | 是（FG19 原文“本发不计积热”无法实现） | §5、§9 第 10 条 |
| 8 | 湮灭改为“低完整度单位进敌对偏折场被压溃起爆”，不再需要处决固件；对玩家单位同样成立 | 是 | F01、§7 第 19 行 |
| 9 | 回路溶断、连锁锈蚀改读残骸，可得性提前到“有残骸即可”，不再属于超频批 | 是 | W01、W02、FG11-REACT-01 改卡 |
| 10 | 标记跳转、熔穿过载端口化后触发面扩大 | 接受，回归按“原组合一致 + 新组合可触发” | §4.1 末条 |
| 11 | 酸沼可以通电，传导限制在注入点 8 米内 | 是 | §1.4 第 5 条 |
| 12 | 洗礼塔引发阶段火 / 电区域每秒 8 伤害：保留为世界源参数，还是改由物质接触伤害决定 | 阶段 2 先保留（等价迁移），阶段 2 出口再定 | §7 第 10 行 |
| 13 | 分裂液阵亡 / 到期时是否留酸斑块 | 先不留（保持现状），由模糊测试器“平庸”报告决定 | §7 第 6 行 |
| 14 | 熔穿过载、热震不再以穿甲 / 破甲为主结果：熔穿过载改为溅出熔融金属 + 目标进入熔穿，旧“本发额外穿甲 +30%”退役；热震改为崩出装甲破片，破甲降为相态附带 | 接受（主结果不能是数值的直接结果），FGJ-M1 穿甲断言标预期差异 | P02、P03、§4.1、FGT-EMG-030 |
| 15 | 金属碎屑可拾取换废料：阶段 2 不做，留到阶段 4，由 FG19A 给拆解钳或轮式拖挂补拾取声明 | 预留 | §1.4 第 13 条 |

---

## 13. 未核实（避免后续猜测）

1. 洗礼塔参数里“格”与米的换算（`boss.clarity.fill_radius` 6 格）：沿用 `BaptismTowerBoss` 的现有写法，未核实。
2. 云端喷淋池对建筑的伤害如何结算（热更层，还是内核结构单位）：未读代码；迁移时必须与现状一致。
3. `Marked`、`Shield`、`Displace`、`Catalyst` 标签位有没有别的代码路径挂上（底稿 02 §9.1）。
4. 现有处决对首领是否生效；湮灭 F01 沿用同一判定。
5. `MarkJumpCore`（`Sim/Combat/CombatLogic.Reading.cs:1637`）现有跳转范围与 FG19A `fw_marktag` 写的 8 米是否一致：由 FG19A 对账，本附录不再设 `emg.react.marktag_range`。
6. FG7 天气（雨）是否给露天斑块加水 / 灭火：本附录未纳入，阶段 2 开工时核对 FG07 天气章节，只登记结论（STEP4 修改清单里没有这一项，第 4 步不做）。
7. （已对齐）过载 `fw_overload`：FG19A 已改为每次命中注入 `en.heat` 45～60、无积热门槛；+25 热量走 P03 的 `cost.heat`、取代表值 15，不进过载声明（§4.1 A2、第 9 节第 16 条）。
8. （已定）曲射 / 牵引的扬尘：FG19A 定为排放粉尘斑块（`fw_arc`、`fw_tractor` 写 `mat.dust` emit），尘爆所需的斑块成立（§1.5）。
9. 旧 `SimWorld` / `JobZone` 区域在正式版是否还有调用方（底稿 02 §6.1 末行）；本附录按“不迁”处理。
10. 远征区域永久改变的承载方式（FG19 第 14 节第 15 项）：影响 V03、V06、V07、V08 在远征地点能否写入。
11. 云端 `ReactionBatchService` 的 `.tip` 文本是否隐含倍率口径（底稿 02 §9.1）。
