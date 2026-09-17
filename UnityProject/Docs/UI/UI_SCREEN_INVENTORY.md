# 现有战斗 UI 清单与改造顺序

## 已识别的可运行功能

| 页面/组件 | 玩家任务 | 当前实现 | 必须遵循的交互 |
| --- | --- | --- | --- |
| Battle HUD | 战斗中读取生命、进化、资源、技能和环境 | `BattleHud.uxml` + `BattleHudToolkit` | 常驻但不遮挡中央战场；只显示高频信息 |
| Draft | 在候选中做一次构筑选择 | `BattleDraftUI.uxml` + `BattleDraftUIToolkit` | 2–4 张卡同屏比较；选定有确认反馈；不做滚动 |
| Carrier / Bag | 查看和装配载体、基因、器官与物品 | `BattleCarrierUI.uxml` + `BattleCarrierUIToolkit` | 多项目用滚动/网格；详情与操作按钮固定在可预期位置 |
| Germination | 选择或培育成长结果 | `BattleGerminationUI.uxml` + `BattleGerminationUIToolkit` | 展示成本、收益、不可用原因与确认/取消 |
| Deck | 浏览已有卡牌与路线 | `BattleOverlays.uxml` + `BattleOverlayUIToolkit` | 路线和卡牌是可变集合，使用可滚动列表；提供详情 tooltip |
| Shop | 比较并购买商品 | `BattleOverlays.uxml` + `BattleOverlayUIToolkit` | 商品槽位、价格、可买状态明确；刷新是次级操作；商品多于展示位时扩展为滚动/分页 |
| Codex | 搜索、筛选与阅读已发现内容 | `BattleOverlays.uxml` + `BattleOverlayUIToolkit` | 搜索 + 分类 tab + 可滚动条目；空搜索和未发现状态必须可解释 |
| Pause | 恢复、查看构筑、进入代谢或放弃本局 | `BattleOverlays.uxml` + `BattleOverlayUIToolkit` | 暂停后锁输入；放弃是危险操作，必须二次确认 |
| Result | 回顾本局并进入下一局 | `BattleResultUI.uxml` + `BattleResultUIToolkit` | 突出结果、解锁和下一步；长统计放折叠或滚动区 |

## 改造优先级

1. **P0：一致性与阻断问题**
   - 所有模态页统一关闭、Esc、遮罩和输入锁定规则。
   - 所有可变内容统一滚动/空态/长文规则。
   - 移除 UXML 内联样式，迁移为 USS 类，避免后续组件无法覆盖。

2. **P1：核心体验**
   - HUD：继续保持信息三级层次，补齐目标平台的安全区与极端数值测试。
   - Shop / Deck / Carrier：统一物品卡、稀有度、价格、禁用与详情交互。
   - Draft / Germination：保证决策选项可以并排比较，行动按钮有明确主次。

3. **P2：完成度**
   - 图鉴搜索无结果、首次发现、长文本和 tooltip 的完整状态。
   - 结算页的奖励演示、解锁反馈、复盘统计和下一局入口。
   - 使用导出的图标、九宫格边框、图集和音效/动效补全商业表现。

## 组件基线

以下模板已存在，后续页面必须优先复用或把差异上提到共享模板，而不是复制一份：

| 组件 | 现有路径 | 应承担的职责 |
| --- | --- | --- |
| `SkillSlot` | `BattleUI/templates/SkillSlot.uxml` | 技能、冷却、按键和可用状态 |
| `TagChip` | `BattleUI/templates/TagChip.uxml` | 分类、环境和短标签 |
| `SlotCell` | `BattleUI/templates/SlotCell.uxml` | 装配槽位和占用状态 |
| `ShopSlot` | `BattleUI/templates/ShopSlot.uxml` | 商品信息、价格与购买动作 |
| `DraftCard` | `BattleUI/templates/DraftCard.uxml` | 单次抉择的卡牌比较 |
| `BagItem` | `BattleUI/templates/BagItem.uxml` | 背包/收藏物的列表项或网格项 |

新增组件前先回答三个问题：它是否会在两个以上页面复用；它的数据和交互是否稳定；它能否由现有组件的变体表达。三个答案均为否时才做页面私有结构。

