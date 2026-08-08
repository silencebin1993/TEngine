# 细胞玩家基底：30 分钟绑 3 根骨（非美术向）

> 配套：`Art_Direction_Cell_Stage.md` §6 动画分级  
> 对象：开局圆泡 `player_base_core`（及同拓扑的低面有机体）  
> Blender：**4.0+**（菜单名以 4.x 中文/英文并列）  
> 目标：能让「核呼吸 / 口缘张合」真动顶点；**不是**学完建模

---

## 0. 你最终会得到什么

| 产物 | 路径建议 |
|------|----------|
| 绑骨 + 权重后的源文件 | `GameRes/Art/Cell/Meshes/player_base_core_rig.blend` |
| 进 Unity 的 FBX | `GameRes/Art/Cell/Meshes/player_base_core_rig.fbx` → 再拷/烘焙到 `Raw/` |
| 两段测试动作 | Idle 呼吸、Bite 张口（各 ≤1s 循环） |

三根骨语义（点选部位就按这个理解）：

```
Root     —— 球心，整只细胞跟着转/缩放（几乎不单独动）
Core     —— 内核凸起，上下微胀 =「心跳」
MawTip   —— 靠前/靠「口」一侧的膜缘，前后拉 =「张合」
```

小怪**不要**绑这套；玩家先会这三根就够。鞭毛等部件另模另骨，挂槽位，不塞进圆泡骨架。

---

## 1. 开始前 5 分钟

1. 安装 [Blender LTS](https://www.blender.org/download/)（装完用默认快捷键即可）。  
2. Tripo 导出 **FBX 或 GLB**，放到  
   `TEngine/UnityProject/Assets/GameRes/Art/Cell/Meshes/`  
   （若还没有 mesh：先按美术方向 §8.4 出 `cell_base_core` 再回来；下面用占位球也能练一遍流程。）  
3. 打开 Blender → **File → New → General**（文件 → 新建 → 常规）。  
4. 删掉默认立方体：选中 → `X` → **Delete**。

界面认三个区即可：

- 中间：3D 视口  
- 右上：Outliner（大纲，物体列表）  
- 右下：Properties（属性面板，图标一排）

---

## 2. 导入模型（约 2 分钟）

**File → Import → FBX**（或 **glTF 2.0**）  
选 `Meshes/` 里的圆泡。

导入后立刻做三件事：

1. **选中 mesh** → `S` 看大小；若巨大/极小，在右侧 **Object Properties（橙色方块）→ Scale** 调到直径大约 2（Unity 里再乘 `Radius`）。  
2. **Object → Set Origin → Origin to Geometry**（物体 → 设置原点 → 原点到几何中心）——球心对准物体原点。  
3. **Object → Apply → Rotation & Scale**（物体 → 应用 → 旋转和缩放）——`Ctrl+A` → **Rotation & Scale**。  
   > 不 Apply，后面自动权重会歪。

在 Outliner 把 mesh 改名成 `Body`（双击名字）。

---

## 3. 摆 3 根骨（约 10 分钟）——「手动点选部位」就是这一步

### 3.1 建骨架

1. 光标放到球心：选中 `Body` → `Shift+S` → **Cursor to Selected**（游标到选中）。  
2. `Shift+A` → **Armature**（骨架）。  
3. Outliner 里改名为 `Armature_Cell`。  
4. 选中骨架 → 右侧 **Object Data Properties（绿小人图标）** → 勾选 **In Front**（在前面）——骨头不会被 mesh 挡住。  
5. 切到 **Edit Mode**（编辑模式）：选中骨架 → 左上模式菜单 **Object Mode → Edit Mode**，或按 `Tab`。

默认已有一根骨 `Bone`。我们改成三根层级：

```
Root
 ├─ Core
 └─ MawTip
```

### 3.2 改第一根 = Root

1. 选中唯一那根骨（点中段或尾球）。  
2. 左侧改名：按 `F2`（或右侧 Bone Properties 骨头图标）→ 命名 `Root`。  
3. 骨骼方向：细胞顶视游戏里，习惯 **骨头朝上（+Z）或朝「口」一侧**；圆泡建议：  
   - `Root` **尾（根球）在球心**，**头略朝上**一点点（几乎短根，只当锚）。  
4. 调长度：选中骨头 → `G` 移头/尾；或选中 **头球（尖端）** 再 `G`。  
   Root 很短即可（约为半径的 10–20%）。

### 3.3 挤出 Core（内核）

1. 选中 `Root` 的 **头球**。  
2. `E` 挤出 → 往 **核凸起方向** 拉一小段（若核在表面前方/上方，就朝那边）。  
3. `F2` 命名 `Core`。  
4. 长度大约到「核鼓包中心」为止——你在视口里目视对准凸起即可，这就是「点选部位」。

### 3.4 挤出 MawTip（口缘）

1. **先再选中 `Root` 的头球**（不要从 Core 挤，否则成父子链错了）。  
   - 若误从 Core 挤出：选中新骨 → **Bone Properties → Relations → Parent** 改成 `Root`，并清 Connected（见下）。  
2. 更稳妥：选中 `Root` → `Shift+E` 或选头球 `E`，朝 **口器/膜缘** 方向挤出。  
3. 命名 `MawTip`。  
4. 把头球放到「将来会张口」的那一侧膜缘内侧（圆泡还没大口时，就选几何上略扁或概念图里口的那一面）。

**断开「必须连在父骨头上」**（方便口缘独立前后动）：

1. 选中 `MawTip` → **Bone Properties（绿骨图标）→ Relations**  
2. Parent 保持 `Root`  
3. **取消勾选 Connected**（相连）——这样 MawTip 尾可以不钉死在 Root 头上。  
4. 把 `MawTip` 整根 `G` 挪到口缘附近；尾靠近表面内侧，头更靠外。

`Core` 可保持 Connected（从 Root 长出去），也可同样取消 Connected 后挪到核心——两种都行；核在表面鼓包时，**取消 Connected 再挪到鼓包中心**更准。

### 3.5 检查层级

Outliner 展开 `Armature_Cell` 应类似：

```
Armature_Cell
  Root
    Core
    MawTip
```

顶视图（`Numpad 7`）确认：口缘骨在「前方」，核骨在核位置。游戏摄像机是顶视，**前方 = 你规定的移动朝向**；统一后写进笔记（例如「−Z 为前」），Unity 导入时才不拧。

---

## 4. 绑到身上 + 自动权重（约 5 分钟）

1. 切回 **Object Mode**（`Tab` 或模式菜单）。  
2. **先点 mesh `Body`，再 `Shift` 点骨架 `Armature_Cell`**（骨架必须是最后选中的 Active）。  
3. `Ctrl+P` → **With Automatic Weights**（自动权重）。  
4. 若弹出 *Bone Heat Weighting: failed*：  
   - 常见原因：没 Apply 缩放、网格有破洞/非流形。  
   - 应急：`Ctrl+P` → **With Empty Groups**，再手动刷（见附录 B）；或回 Tripo/Blender 修封闭体积后再自动权重。

成功后：选中 `Body` → 看 **Modifier（扳手图标）** 应有 **Armature**，Object 指向 `Armature_Cell`。

---

## 5. 试姿势（约 5 分钟）——确认「理解部位」对不对

1. 选中骨架 → 模式改为 **Pose Mode**（姿态模式）。  
2. 点 `Core` → `S` 略放大 / `G` 轻移：圆泡核附近应鼓起来。  
3. 点 `MawTip` → 沿口方向 `G`：口缘应局部凸出，像轻张嘴。  
4. `Root` 一般只在动画里做整体微转；测试时动一下确认全身跟着走即可。  
5. 复位：选中所有骨 `A` → `Pose → Clear → Transforms`（或 `Alt+G` / `Alt+R` / `Alt+S`）。

**若动 Core 时半个身子跟着飞**：权重过散 → 附录 B 刷权重。  
**若口缘完全不动**：MawTip 权重几乎为 0 → 刷权重或把骨再贴进表面一点后，删掉 Armature 修改器与顶点组，重新 `Ctrl+P` 自动权重。

---

## 6. 录两段极简动作（约 5 分钟）

时间轴在最下方。若没有：**Window → Timeline**。

### Idle（呼吸）

1. Pose Mode，帧 `1`：`Core` 缩放约 `1.0` → 选中 `Core` → `I` → **Scale**（插入缩放关键帧）。  
2. 帧 `30`：`S` 到约 `1.08` → 再 `I` → **Scale**。  
3. 帧 `60`：回到 `1.0` → `I` → **Scale**。  
4. 时间轴播放（空格）应循环鼓胀。  
5. 选中骨架 → **Object Data → Animation** 或 **Dope Sheet → Action Editor**，把 Action 命名为 `Idle_Breath`。

### Bite（张口，可选）

1. **Dope Sheet → Action Editor → New**（新建动作）命名 `Bite_Open`。  
2. 帧 `1`：`MawTip` 原位 → `I` → **Location**。  
3. 帧 `8`：往口外拉一点 → `I` → **Location**。  
4. 帧 `20`：收回 → `I` → **Location**。

细胞阶段先有 Idle 即可；Bite 给吞噬演出预留。

---

## 7. 导出 FBX 给 Unity（约 3 分钟）

1. Object Mode。  
2. 选中 **骨架 + Body**（或全选 `A`）。  
3. **File → Export → FBX**。  
4. 右侧关键勾选：  
   - **Selected Objects**（仅选中）  
   - **Transform → Apply Scalings: FBX All**（或 Blender 默认，保持一致即可）  
   - **Geometry → Apply Modifiers**（可选；Armature 通常导出时保留）  
   - **Armature → Add Leaf Bones**：建议 **关掉**（少余骨）  
   - **Bake Animation**：若要带 Idle/Bite，勾上；只出绑骨也可先不勾，动作以后再导  
5. 存为 `Meshes/player_base_core_rig.fbx`。  
6. Unity 导入后：选模型 → Rig → **Animation Type: Generic** → Apply；在 Hierarchy 里应能看到 `Root / Core / MawTip`。

半透明湿润材质仍按美术方向在 Unity 叠，**不要**指望 Blender 材质进游戏。

---

## 8. 和管线怎么接

```
Tripo 静态 mesh
  → 本手册绑 3 骨 + Idle
  → Art/Cell/Meshes/*.fbx
  → registry: manage.py link … 或手改 mesh/anim 字段
  → 拷到 Raw（YooAsset）后给玩家专用渲染（非万敌 Instanced 批）
```

注意：当前 `SimRenderer` 是 **静态 mesh 实例化**，**不播骨骼**。  
这套 rig 先服务：

- 玩家单独 GameObject / 少量单位  
- 吞噬、突变镜头  
- 白模验收「会动」

万敌小怪继续静态 + Shader/缩放。等玩家表现位定了，再决定是否做「GPU skinning 实例」——那是后话，别现在扩。

---

## 9. 常见翻车

| 现象 | 处理 |
|------|------|
| 自动权重失败 | Apply 旋转缩放；修非流形；或 Empty Groups + 手刷 |
| 骨头在肉里看不见 | Armature → Viewport Display → In Front |
| 一动全网变形 | 权重刷掉远处影响；或减少骨影响半径 |
| Unity 朝向错 | 导出前统一「口朝 −Z」；Unity 里转父节点补偿 |
| 动作导出去丢了 | 导出勾 Bake Animation；Action 要在 NLA 或激活 Action 里 |

---

## 附录 A. 没有 mesh 时用占位球练手

`Shift+A` → **Mesh → UV Sphere** → Shade Smooth → 改名 `Body` → 从 §3 做到 §6。流程通了再换成 Tripo 模型。

## 附录 B. 手动刷权重（自动权重不准时）

1. 选中 `Body` → **Weight Paint**（权重绘制）模式。  
2. 右上/侧边 **Vertex Groups** 选 `Core` 或 `MawTip`。  
3. 笔刷：**Add** 画红（跟骨）、**Subtract** 减影响。  
4. 原则：  
   - `Root`：全身淡影响即可  
   - `Core`：只红核鼓包  
   - `MawTip`：只红口缘一圈  
5. 刷完回 Pose Mode 再试。

## 附录 C. 部件（大口膜 / 鞭毛）怎么绑

- **另文件**绑，不要塞进圆泡的同一套骨。  
- 大口膜：2 骨即可（`MawRoot` + `MawLip`）。  
- 鞭毛：`WhipRoot` + 沿长度 2～3 节。  
- 运行时挂到玩家槽位节点下，播自己的短动画。

---

## 验收清单（做完打勾）

- [ ] Pose 里动 `Core`，只有核附近鼓  
- [ ] 动 `MawTip`，只有口缘动  
- [ ] Idle 循环肉眼能看出「在呼吸」  
- [ ] FBX 进 Unity 可见三根骨，Generic 无报错  
- [ ] `registry.json` 里该资产 `anim` / `status` 已标到 `blender` 或以上  

---

*本文只服务细胞阶段玩家轻骨骼。改动画分级策略时，与 `Art_Direction_Cell_Stage.md` 同步升版说明。*
