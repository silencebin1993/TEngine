using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BinGames.EditorTools.CellArt;
using GameLogic.ArtBinding;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-010 左树 + 010→011 右栏重画：右栏默认单行绑定框、prompt 整段可见、CellArt 概念图只读
    /// 反显、页尾死说明（抄 OBJECT-NOTES）、左树默认全展开 + 点文件夹行进第一个叶子、Odin 上色（PANEL-UX §7～§10）。
    /// 运行时 location 键、catalog JSON 字段、collector、Resolver/Binder/VfxPool 零改动。
    /// 菜单：BinGames → 功能美术绑定（<see cref="FeatureArtBindingMenu"/> 转调 <see cref="Open"/>，未改）。</summary>
    public sealed class FeatureArtBindingWindow : OdinMenuEditorWindow
    {
        const string RawPrefix = "Assets/GameRes/Raw/";

        // ---- story-017：InstancedMesh 槽「网格/预制体/模型」下拉 ----
        const int MeshKindMesh = 0;
        const int MeshKindPrefab = 1;
        const int MeshKindModel = 2;
        static readonly string[] MeshKindLabels = { "网格", "预制体", "模型" };

        /// <summary>PANEL-UX §3 器官→Family/Shape 映射（UI 呈现用，禁止从 OrganelleDef.AttackFamily 派生——
        /// 那是战斗侧枚举，与本表分组不是一回事，见 preflight-decisions.md「关键陷阱」）。</summary>
        static readonly (string OrganId, string GroupZh, string ShapeKey)[] AttackMethodEntries =
        {
            ("org_emitter", "远程", "Bolt"),
            ("org_lensbeam", "远程", "Beam"),
            ("org_orbitcilia", "远程", "Bolt"),
            ("org_cilia", "近战", "Melee"),
            ("org_spine", "近战", "Melee"),
            ("org_phago", "近战", "Melee"),
            ("org_pseudopod", "近战", "Melee"),
            ("org_drill", "近战", "Melee"),
            ("org_enzyme", "场", "Field"),
            ("org_osmotic", "场", "Field"),
            ("org_wave", "波", "Wave"),
            ("org_bud", "召唤类", "Spore"),
            ("org_mycelium", "召唤类", "Spore"),
        };

        static readonly string[] ShapeOrder = { "Bolt", "Beam", "Melee", "Field", "Wave", "Spore", "Arc" };

        static readonly (string Key, string TitleZh)[] SummonEntries =
        {
            ("spore", "跟随芽体"),
            ("phage", "追击噬菌"),
            ("mycelium", "固着炮台"),
        };

        // ---- PANEL-UX §8 配色 ----

        static readonly Color ColorSelected = HexColor("#3D7EFF");
        static readonly Color ColorEmpty = new Color(0.55f, 0.55f, 0.55f);
        static readonly Color ColorBound = HexColor("#3D9B5C");
        static readonly Color ColorBad = HexColor("#C23B3B");

        static readonly Dictionary<string, Color> FamilyColor = new Dictionary<string, Color>
        {
            ["远程"] = HexColor("#E8A317"),
            ["近战"] = HexColor("#C23B3B"),
            ["场"] = HexColor("#3D9B5C"),
            ["波"] = HexColor("#2AA8C4"),
            ["召唤类"] = HexColor("#8B5CF6"),
        };

        static readonly Dictionary<string, Color> ShapeColor = new Dictionary<string, Color>
        {
            ["Bolt"] = HexColor("#D4A017"),
            ["Beam"] = HexColor("#A8D4E6"),
            ["Melee"] = HexColor("#C23B3B"),
            ["Field"] = HexColor("#3D9B5C"),
            ["Wave"] = HexColor("#4EC5D4"),
            ["Spore"] = HexColor("#9B59B6"),
            ["Arc"] = HexColor("#E07A3D"),
        };

        // 中文名权威来源：DesignDocs/最新改动需求/组合引擎-正名与全阶段变化词宪法.md §3.2；
        // Melee 不在该表（Delivery 只列 6 个），沿用本文件既有的"近战"分组名（见 AttackMethodEntries）。
        // 只改显示文案，不改 shape 这个内部 key——槽 id / 文件名 / 文件夹名仍是英文，勿在此表之外改动它们。
        static readonly Dictionary<string, string> ShapeZhMap = new Dictionary<string, string>
        {
            ["Bolt"] = "弹丸",
            ["Beam"] = "束",
            ["Melee"] = "近战",
            ["Field"] = "场",
            ["Wave"] = "波",
            ["Spore"] = "孢子云",
            ["Arc"] = "弧链",
        };

        static string ShapeZh(string shapeKey) =>
            ShapeZhMap.TryGetValue(shapeKey, out var zh) ? zh : shapeKey;

        static Texture2D _dotEmpty;
        static Texture2D _dotBound;
        static Texture2D _dotBad;

        // ---- OBJECT-NOTES.md 逐字抄的页尾死说明（只读；不进 JSON、不走 SlotSync）----

        const string PlayerNote =
            "目的：开局就能认出的「你」。器官换皮叠在这颗底盘上，不是每装一个器官换一具全身。\n\n" +
            "注意：单 MeshFilter；胖梭、前端略尖；不要四肢、不要半透明玻璃当几何（半透明留给 SimBioGlass 材质）。材质必须勾 GPU Instancing。面数按实例化预算，不要上复杂骨骼当万敌路径。\n\n" +
            "动画：底盘本身不做循环变形动画。Idle 用材质/轻微呼吸即可；攻击演出走「开火时间轴」那四段特效，不要在底盘 Mesh 上播整套骨骼攻击。\n\n" +
            "源文件：Cell Art player_base_core（已有概念图 + 四向三视图）。三视图只供 Tripo/对形，不要把正交图当成品 Mesh 拖进 Raw。";

        static readonly Dictionary<string, string> OrganNotes = new Dictionary<string, string>
        {
            ["org_emitter"] =
                "远程主炮。外形必须一眼看到右侧喷嘴。弹体是蜜黄小锥滴，和纤毛环带共用 Bolt——改弹道两边一起变。喷嘴口朝 +X。不要做整只细胞+手臂持枪。\n\n" +
                "动画：没有「举枪」骨骼。开火看枪口一小朵，飞行看弹体，打中看命中，炸圈看爆炸。",
            ["org_lensbeam"] =
                "聚焦器官：扁晶 + 朝右细管。弹道语言是 Beam（青白实心细棒），不要电弧碎丝、不要做成探照灯体积光。管子是几何的一部分，不要拆成粒子。\n\n" +
                "动画：Beam 是持续体，Prefab 寿命跟现网 Persistent，不要自己 Destroy。",
            ["org_orbitcilia"] =
                "赤道一圈短纤毛，像长在球上的行星环。攻击语言与喷射器同为 Bolt，只换外形不换弹种。纤毛必须连在体上，禁止漂浮环。",
            ["org_cilia"] =
                "身前一丛硬毛戳出去。Melee 特效是身前一段猩红厚月牙，不要做剑模型。毛 ≤8 根、要粗。和刺突/吞噬/伪足/钻共用 Melee 语言。\n\n" +
                "动画：近战没有飞行弹；枪口=挥击起手，弹体=挥击体，命中打在目标上。不要把月牙挂在玩家脚下。",
            ["org_spine"] =
                "周身短棘的反刺壳，像微型蒺藜。棘与球一体。近战语言同上。不要做成会掉刺的发射器（那是远程）。",
            ["org_phago"] =
                "厚唇裂口，口朝右。这是近战口器外形，不是首领那颗更大的裂口体。不要堆牙床细节。吞噬玩法门控在器官激活，美术只负责「看得出是一张嘴」。",
            ["org_pseudopod"] =
                "宽掌肉瓣朝右拍。边缘圆钝，必须连在母体。不要做成独立手套。挥击仍走 Melee 月牙，掌是身体不是武器 Mesh。",
            ["org_drill"] =
                "螺旋锥头朝右，螺纹粗、约 3 圈。是冲刺/钻入的外形，不要做成电钻工具或独立钻头道具。",
            ["org_enzyme"] =
                "葡萄串腺，底有朝下滴口。场语言是青绿扁环（Field），内孔要大。腺体几何不要做成喷雾粒子罐——雾是特效槽的事。\n\n" +
                "动画：场是持续环，贴地或绕身由现网 Field 逻辑管，Prefab 不要自带旋转脚本抢控制。",
            ["org_osmotic"] =
                "中心核 + 外圈扁环膜，环必须连着核。与酶雾共用 Field。不要离散粒子环当网格。",
            ["org_wave"] =
                "朝右张开的新月/扇壳，单片闭合。波语言是水色细扩散环，像水面一圈，不要做海啸墙。",
            ["org_bud"] =
                "母体侧粘 1～2 颗小芽，禁止拆开的双胞胎。本页第三块是跳到召唤实体，不是再做一套远程弹。命中/爆炸仍可绑 Spore 语言（紫十字孢）。\n\n" +
                "动画：器官本身不「生孩子动画」；实体出场看召唤物 Mesh。不要在器官 Prefab 里 Instantiate 芽体。",
            ["org_mycelium"] =
                "扁锚 + 向下 6～8 根粗短根须贴地，不要立起来的树。网格应贴 XZ。同样链到召唤实体页。",
        };

        const string ShapeCommon =
            "这是共享皮肤。改这里，所有使用该 Shape 的攻击器官一起变。五格都是池化 Prefab；+X 为前方；短生命周期；不要相机、不要 AudioListener、不要自己 Destroy 断池。";

        const string ShapeRoles =
            "五角色补一句：弹体=飞行/持续中本体；枪口=开火瞬间贴在细胞右前方 1～3 帧；命中=打在目标身上；爆炸=落点一圈，不要火球蘑菇云；瞄准预览=未开火时半透明贴玩家脚下的装配预览，静态不飞行，别做得跟实弹一样浓。";

        static readonly Dictionary<string, string> ShapeNotes = new Dictionary<string, string>
        {
            ["Bolt"] = ShapeCommon + "\n\n蜜黄小锥滴，尖朝飞行方向。喷射器与环带共用。体积小，俯视能认。\n\n" + ShapeRoles,
            ["Beam"] = ShapeCommon + "\n\n青白实心细棒。是「一根管子」不是电弧。持续体，勿自毁。\n\n" + ShapeRoles,
            ["Arc"] = ShapeCommon + "\n\n预留橙红厚扇瓣。暂无底盘器官时仍留卡，不要删。\n\n" + ShapeRoles,
            ["Field"] = ShapeCommon + "\n\n青绿扁环，内孔大。酶雾/渗透共用。不要做成贴地水渍贴图一张。\n\n" + ShapeRoles,
            ["Wave"] = ShapeCommon + "\n\n水色细环扩散。波形器专用。环形可读，不要实心圆盘。\n\n" + ShapeRoles,
            ["Spore"] = ShapeCommon + "\n\n紫十字/小星。芽殖与菌丝的召唤物命中语言。实体 Mesh 在「召唤实体」，不要把实体做成这颗孢。\n\n" + ShapeRoles,
            ["Melee"] = ShapeCommon + "\n\n猩红短厚月牙，身前一截。五件近战器官共用。禁止剑、斧、爪独立武器。\n\n" + ShapeRoles,
        };

        const string SummonCommon = "这是器官「生出来的单位」，InstancedMesh，和玩家一样只抽网格。不要做成技能特效。";

        static readonly Dictionary<string, string> SummonNotes = new Dictionary<string, string>
        {
            ["spore"] = SummonCommon + "\n\n比玩家小很多，顶上短锥尖帽。跟在玩家附近，不要做成第二玩家。源文件可对 minion_spore_bud。无独立攻击骨架；命中语言走 Spore。",
            ["phage"] = SummonCommon + "\n\n圆头+粗柄，像微型注射器，两段连体。比敌人里「噬菌形」更利落。目前源文件板没有一对一概念图，空着即可。",
            ["mycelium"] = SummonCommon + "\n\n比玩家扁，贴地垫 + 一圈短根须。必须贴地，不要细丝网、不要悬空。和菌丝锚器官是「锚 / 炮台」两件东西，网格可以像，但不要共用同一个 Raw 文件名（location 会撞）。",
        };

        const string EnemyCommon = "一族共用一份网格，换色/缩放区分个体。精英/首领先放大剪影，不必另做复杂零件。不要为每个 EnemyId 做精模。";

        const string EnemyAnim =
            "动画：杂兵不要走路循环骨骼；位移由模拟层带。需要蠕动就用极简 1～2 骨或顶点，不要人形骨骼。攻击演出优先复用 Shape 特效，不要每族一套 Animator。";

        static readonly Dictionary<string, string> EnemyNotes = new Dictionary<string, string>
        {
            ["vis_1"] = EnemyCommon + "\n\n最弱食物剪影，软圆无口。概念图有 enemy_blob_food。\n\n" + EnemyAnim,
            ["vis_2"] = EnemyCommon + "\n\n扁盘短刺边，刺要粗。\n\n" + EnemyAnim,
            ["vis_3"] = EnemyCommon + "\n\n椭圆+一条粗尾，整体像逗号。\n\n" + EnemyAnim,
            ["vis_4"] = EnemyCommon + "\n\n细梭头尖，像小鱼，无鳍碎件。\n\n" + EnemyAnim,
            ["vis_5"] = EnemyCommon + "\n\n圆头短柄，比玩家噬菌召唤更粗笨。源文件板可能无一对一图。\n\n" + EnemyAnim,
            ["vis_6"] = EnemyCommon + "\n\n半球甲，板块闭合。\n\n" + EnemyAnim,
            ["vis_7"] = EnemyCommon + "\n\n≤4 根粗触须，禁止发丝。\n\n" + EnemyAnim,
            ["vis_8"] = EnemyCommon + "\n\n缺一块破口，仍是单 mesh。\n\n" + EnemyAnim,
            ["vis_9"] = EnemyCommon + "\n\n比追猎更尖的梭。无一对一图就空着。\n\n" + EnemyAnim,
            ["vis_10"] = EnemyCommon + "\n\n少量更长的粗棘。\n\n" + EnemyAnim,
            ["vis_11"] = EnemyCommon + "\n\n扁垫小敌，贴地。\n\n" + EnemyAnim,
            ["vis_20"] = EnemyCommon + "\n\n咬剩的凸多面体。\n\n" + EnemyAnim,
            ["vis_50"] = EnemyCommon + "\n\n在硬壳思路上加脊/冠，体量明显更大。概念图可对 devourer / whip_king / volt_hunter，只作形参考，不必抄名字。\n\n" + EnemyAnim,
            ["vis_51"] = EnemyCommon + "\n\n在硬壳思路上加脊/冠，体量明显更大。概念图可对 devourer / whip_king / volt_hunter，只作形参考，不必抄名字。\n\n" + EnemyAnim,
            ["vis_52"] = EnemyCommon + "\n\n在硬壳思路上加脊/冠，体量明显更大。概念图可对 devourer / whip_king / volt_hunter，只作形参考，不必抄名字。\n\n" + EnemyAnim,
            ["vis_90"] = EnemyCommon + "\n\n最大的厚唇裂口体，剪影碾压小兵。可对 boss_prokaryote_p1。不要和玩家吞噬体做成同一个 Raw 文件。\n\n" + EnemyAnim,
        };

        FeatureArtCatalogData _data;
        bool _dirty;
        CellArtRegistry _registry;
        bool _registryDirty;
        string _lastLog = "";
        bool _lastLogError;
        List<HealthIssue> _healthIssues;
        readonly Dictionary<string, string> _addViewKeys = new Dictionary<string, string>();
        readonly Dictionary<string, CellArtAsset> _workingAssets = new Dictionary<string, CellArtAsset>();

        /// <summary>story-017：InstancedMesh 槽「网格/预制体/模型」下拉的显示态。空槽时按用户上次选择
        /// 记忆（默认模型）；已绑时每帧被 <see cref="ResolveMeshAssetKind"/> 覆盖为磁盘实测结果，
        /// 不持久化进 catalog（零改动 schema）。</summary>
        readonly Dictionary<string, int> _instancedMeshKindOverride = new Dictionary<string, int>();

        readonly Dictionary<string, OrganPage> _organPages = new Dictionary<string, OrganPage>();
        readonly Dictionary<string, ShapePage> _shapePages = new Dictionary<string, ShapePage>();
        readonly Dictionary<string, SimpleMeshPage> _summonPages = new Dictionary<string, SimpleMeshPage>();
        FeatureArtSourceLibraryPage _sourceLibraryPage;
        public readonly FeatureArtSourceLibraryState SourceLib = new FeatureArtSourceLibraryState();

        public FeatureArtCatalogData Data => _data;
        public List<HealthIssue> HealthIssues => _healthIssues;
        public bool IsRegistryDirty => _registryDirty;

        public static void Open()
        {
            var w = GetWindow<FeatureArtBindingWindow>();
            w.titleContent = new GUIContent("Feature Art");
            w.minSize = new Vector2(980, 560);
            w.Show();
        }

        protected override OdinMenuTree BuildMenuTree()
        {
            if (_data == null)
            {
                Reload();
            }

            _organPages.Clear();
            _shapePages.Clear();
            _summonPages.Clear();

            var tree = new OdinMenuTree(false);
            tree.Config.DrawSearchToolbar = true;
            tree.Config.UseCachedExpandedStates = false;
            tree.DefaultMenuStyle.SelectedColorDarkSkin = ColorSelected;
            tree.DefaultMenuStyle.SelectedColorLightSkin = ColorSelected;

            tree.Add("使用说明", new GuidePage());
            tree.Add("混元生3D", new FeatureArtHunyuanSettingsPage());
            tree.Add("健康检查", new HealthCheckPage(this));
            _sourceLibraryPage = new FeatureArtSourceLibraryPage(this);
            tree.Add(FeatureArtSourceLibraryPage.MenuPath, _sourceLibraryPage);
            tree.Add("玩家/本体", new PlayerPage(this), StatusIcon(FindSlot("player.chassis.mesh")));

            foreach (var entry in AttackMethodEntries)
            {
                var summonKey = entry.OrganId == "org_bud" ? "spore" : entry.OrganId == "org_mycelium" ? "mycelium" : null;
                var page = new OrganPage(this, entry.OrganId, entry.GroupZh, entry.ShapeKey, summonKey);
                _organPages[entry.OrganId] = page;
                var slot = FindSlot($"organ.{entry.OrganId}.mesh");
                var titleZh = slot?.titleZh?.Replace(" · 本体网格", "") ?? entry.OrganId;
                tree.Add($"结构器官/攻击器官/{entry.GroupZh}/{titleZh}", page, StatusIcon(slot));
            }

            foreach (var shape in ShapeOrder)
            {
                var page = new ShapePage(this, shape);
                _shapePages[shape] = page;
                tree.Add($"弹道语言/{ShapeZh(shape)}", page);
            }

            foreach (var s in SummonEntries)
            {
                var slot = FindSlot($"summon.{s.Key}.mesh");
                SummonNotes.TryGetValue(s.Key, out var note);
                var page = new SimpleMeshPage(this, $"summon.{s.Key}.mesh", s.TitleZh, "召唤类",
                    FamilyColor["召唤类"], FeatureArtCellArtBridge.SummonCellArtId(s.Key), note);
                _summonPages[s.Key] = page;
                tree.Add($"召唤实体/{s.TitleZh}", page, StatusIcon(slot));
            }

            var families = GameLogic.ArtBinding.FeatureArtVisualBinder.EnemyVisualFamilies;
            for (var i = 0; i < families.Length; i++)
            {
                var fam = families[i];
                var group = i < 12 ? "杂兵" : i < 15 ? "精英" : "首领";
                var slot = FindSlot($"enemy.{fam.Key}.mesh");
                EnemyNotes.TryGetValue(fam.Key, out var note);
                var page = new SimpleMeshPage(this, $"enemy.{fam.Key}.mesh", fam.TitleZh, null,
                    default, FeatureArtCellArtBridge.EnemyCellArtId(fam.Key), note);
                tree.Add($"敌人/{group}/{fam.TitleZh}", page, StatusIcon(slot));
            }

            var structuralSlots = new (string Key, string TitleZh, (string Id, string TitleZh, string Note)[] Organs)[]
            {
                ("armor", "装甲", new[]
                {
                    ("org_carapace", "硬化甲层", "常驻：体表增生一层硬甲，减少受到的伤害 12%。"),
                    ("org_thick_membrane", "增厚膜", "常驻：细胞膜增厚，最大生命 +32。"),
                    ("org_calm_membrane", "静默膜", "常驻：体表分泌物让敌人不容易注意到你，仇恨倍率 −20%。"),
                }),
                ("motility", "运动", new[]
                {
                    ("org_flagellum_boost", "加速鞭毛", "常驻：额外长出一对鞭毛，移动速度 +12%。"),
                    ("org_stamina_sac", "体力囊", "常驻：储存更多体力，体力上限 +25，回复速度 +15%。"),
                }),
                ("vital", "生命", new[]
                {
                    ("org_regen_gland", "再生腺", "常驻：持续修复受损的细胞结构，生命回复 +0.8/秒。"),
                    ("org_efficient_gut", "高效消化道", "常驻：吸收营养的效率更高，营养质获取 +18%。"),
                }),
                ("appendage", "附肢", new[]
                {
                    ("org_chemoreceptor", "化学感受器", "常驻：更容易嗅到周围的营养物质，拾取半径 +30%。"),
                }),
            };
            foreach (var s in structuralSlots)
            {
                var slotId = $"structural.{s.Key}.mesh";
                var slot = FindSlot(slotId);
                var titleZh = slot?.titleZh ?? s.TitleZh;
                var page = new SimpleMeshPage(this, slotId, titleZh, null, default, null, null);
                tree.Add($"结构器官/{titleZh}", page, StatusIcon(slot));

                foreach (var organ in s.Organs)
                {
                    var organPage = new StructuralOrganInfoPage(organ.TitleZh, titleZh, organ.Note);
                    tree.Add($"结构器官/{titleZh}/{organ.TitleZh}", organPage);
                }
            }

            foreach (var item in tree.EnumerateTree(false))
            {
                if (item.ChildMenuItems.Count > 0)
                {
                    item.Toggled = true;
                }
            }

            tree.Selection.SelectionChanged += changeType =>
            {
                if (changeType != SelectionChangedType.ItemAdded)
                {
                    return;
                }

                var selected = tree.Selection.FirstOrDefault();
                if (selected == null || selected.Value != null || selected.ChildMenuItems.Count == 0)
                {
                    return;
                }

                selected.Toggled = true;
                foreach (var child in selected.GetChildMenuItemsRecursive(false))
                {
                    if (child.Value != null)
                    {
                        child.Select(false);
                        break;
                    }
                }
            };

            return tree;
        }

        protected override void OnBeginDrawEditors()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("刷新", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    if ((_dirty || _registryDirty) && !EditorUtility.DisplayDialog("未保存", "有未保存修改，丢弃并刷新？", "丢弃", "取消"))
                    {
                        // keep unsaved
                    }
                    else
                    {
                        Reload();
                        ForceMenuTreeRebuild();
                    }
                }

                GUI.enabled = _dirty || _registryDirty;
                if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    Save();
                }

                GUI.enabled = true;

                if (GUILayout.Button("从代码同步槽位", EditorStyles.toolbarButton, GUILayout.Width(110)))
                {
                    RunSync();
                }

                if (GUILayout.Button("打开源文件板", EditorStyles.toolbarButton, GUILayout.Width(90)))
                {
                    JumpToSourceLibrary();
                }

                GUILayout.FlexibleSpace();
                var label = $"{_data?.slots?.Count ?? 0} 槽" + (_dirty || _registryDirty ? " · 未保存" : "");
                GUILayout.Label(label, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(_lastLog))
            {
                SirenixEditorGUI.MessageBox(_lastLog, _lastLogError ? MessageType.Error : MessageType.None);
            }
        }

        public FeatureArtSlot FindSlot(string id) => _data?.slots?.FirstOrDefault(s => s.id == id);

        public CellArtRegistry Registry
        {
            get
            {
                if (_registry == null)
                {
                    LoadRegistry();
                }

                return _registry;
            }
        }

        public float ContentMaxWidth()
        {
            var max = position.width - MenuWidth - 36f;
            if (float.IsNaN(max) || float.IsInfinity(max) || max < 200f)
            {
                return 480f;
            }

            return max > 1600f ? 1600f : max;
        }

        public string GetAddViewKey(string cellArtId) =>
            !string.IsNullOrEmpty(cellArtId) && _addViewKeys.TryGetValue(cellArtId, out var key) ? key : "";

        public void SetAddViewKey(string cellArtId, string key)
        {
            if (!string.IsNullOrEmpty(cellArtId))
            {
                _addViewKeys[cellArtId] = key ?? "";
            }
        }

        public CellArtAsset GetWorkingAsset(string cellArtId, string titleZh)
        {
            var registry = Registry;
            registry.assets ??= new List<CellArtAsset>();
            var existing = registry.assets.FirstOrDefault(a => a.id == cellArtId);
            if (existing != null)
            {
                existing.views ??= new Dictionary<string, string>();
                return existing;
            }

            if (_workingAssets.TryGetValue(cellArtId, out var draft))
            {
                return draft;
            }

            draft = new CellArtAsset
            {
                id = cellArtId,
                name_zh = string.IsNullOrEmpty(titleZh) ? cellArtId : titleZh,
                kind = "module",
                slot = "none",
                route = "none",
                rarity = "common",
                concept = "",
                views = new Dictionary<string, string>(),
                anim_clips = new List<CellArtAnimClip>(),
                status = "todo",
                notes = "FeatureArt 页内建档",
                needs_review = true,
            };
            _workingAssets[cellArtId] = draft;
            return draft;
        }

        public void CommitWorkingAsset(CellArtAsset asset)
        {
            if (asset == null)
            {
                return;
            }

            var registry = Registry;
            registry.assets ??= new List<CellArtAsset>();
            if (registry.assets.Any(a => ReferenceEquals(a, asset) || a.id == asset.id))
            {
                return;
            }

            registry.assets.Add(asset);
            _workingAssets.Remove(asset.id);
        }

        public void MarkRegistryDirty() => _registryDirty = true;

        public void JumpToSourceLibrary(string cellArtId = null)
        {
            if (!string.IsNullOrEmpty(cellArtId))
            {
                if (_workingAssets.TryGetValue(cellArtId, out var draft))
                {
                    CommitWorkingAsset(draft);
                    _registryDirty = true;
                }

                SourceLib.SelectedId = cellArtId;
            }

            if (_sourceLibraryPage == null)
            {
                ForceMenuTreeRebuild();
            }

            SelectPageObject(_sourceLibraryPage);
        }

        public void RunRegistryScan(bool apply)
        {
            try
            {
                var data = Registry;
                if (data == null)
                {
                    _lastLog = "登记表未加载。";
                    return;
                }

                if (_registryDirty)
                {
                    CellArtRegistryService.Save(data);
                    _registryDirty = false;
                }

                var actions = CellArtRegistryService.Scan(data, apply);
                if (apply)
                {
                    CellArtRegistryService.Save(data);
                    _registryDirty = false;
                    _workingAssets.Clear();
                }

                _lastLog = actions.Count == 0
                    ? "扫盘：没有新文件"
                    : $"{(apply ? "已入列" : "预览")} {actions.Count} 项：\n" + string.Join("\n", actions.Take(30))
                      + (actions.Count > 30 ? $"\n…共 {actions.Count}" : "");
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        public void WriteRegistryBoard()
        {
            try
            {
                var data = Registry;
                if (data == null)
                {
                    _lastLog = "登记表未加载。";
                    return;
                }

                CellArtRegistryService.WriteBoardHtml(data);
                _lastLog = $"已生成 {CellArtRegistryService.BoardAbs}";
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
            }
        }

        public void OpenRegistryBoard()
        {
            try
            {
                var data = Registry;
                if (data == null)
                {
                    _lastLog = "登记表未加载。";
                    return;
                }

                if (!File.Exists(CellArtRegistryService.BoardAbs))
                {
                    CellArtRegistryService.WriteBoardHtml(data);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = CellArtRegistryService.BoardAbs,
                    UseShellExecute = true,
                });
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
            }
        }

        /// <summary>Odin 右栏 Layout 时 currentViewWidth 可能是 Infinity，MessageBox 会按无限宽量高，
        /// Layout/Repaint 来回跳就闪。用窗口实际右栏宽夹住。</summary>
        public void DrawWrappedBox(string text, MessageType type)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(GUILayout.MaxWidth(ContentMaxWidth()), GUILayout.ExpandWidth(true)))
            {
                SirenixEditorGUI.MessageBox(text, type);
            }
        }

        public void Log(string message)
        {
            _lastLog = message ?? "";
            _lastLogError = false;
        }

        public void LogError(string message)
        {
            _lastLog = message ?? "";
            _lastLogError = true;
        }

        public void MarkDirty() => _dirty = true;

        public void RunHealthCheck() => _healthIssues = FeatureArtHealthCheck.Run(_data);

        public void JumpToShape(string shapeKey)
        {
            if (_shapePages.TryGetValue(shapeKey, out var page))
            {
                SelectPageObject(page);
            }
        }

        public void JumpToOrgan(string organId)
        {
            if (_organPages.TryGetValue(organId, out var page))
            {
                SelectPageObject(page);
            }
        }

        public void JumpToSummon(string key)
        {
            if (_summonPages.TryGetValue(key, out var page))
            {
                SelectPageObject(page);
            }
        }

        /// <summary>Odin 此版本无 TrySelectMenuItemWithObject，改用 EnumerateTree 按 Value 引用查找 + Select。</summary>
        void SelectPageObject(object page)
        {
            var item = MenuTree.EnumerateTree(false).FirstOrDefault(i => ReferenceEquals(i.Value, page));
            item?.Select(false);
        }

        public List<(string OrganId, string TitleZh)> OrgansUsingShape(string shapeKey)
        {
            var result = new List<(string, string)>();
            foreach (var e in AttackMethodEntries)
            {
                if (e.ShapeKey != shapeKey)
                {
                    continue;
                }

                var slot = FindSlot($"organ.{e.OrganId}.mesh");
                result.Add((e.OrganId, slot?.titleZh?.Replace(" · 本体网格", "") ?? e.OrganId));
            }

            return result;
        }

        public List<string> OrgansSharingShape(string shapeKey, string excludeOrganId)
        {
            var result = new List<string>();
            foreach (var e in AttackMethodEntries)
            {
                if (e.ShapeKey != shapeKey || e.OrganId == excludeOrganId)
                {
                    continue;
                }

                var slot = FindSlot($"organ.{e.OrganId}.mesh");
                result.Add(slot?.titleZh?.Replace(" · 本体网格", "") ?? e.OrganId);
            }

            return result;
        }

        /// <summary>拖拽绑定：Raw 前缀校验 + bindKind 校验，写 location（story-003 原逻辑，未改，
        /// 核心校验/写入逻辑下沉到 <see cref="TryBindCore"/>）。</summary>
        public void TryBind(FeatureArtSlot slot, UnityEngine.Object picked)
        {
            if (slot == null || picked == null)
            {
                return;
            }

            if (TryBindCore(slot, picked, out var reason))
            {
                _dirty = true;
                _lastLog = $"{slot.id} → location={slot.location}";
            }
            else
            {
                _lastLog = reason;
            }
        }

        /// <summary>story-018 静态桥：不依赖窗口实例状态的纯校验+写入逻辑，供
        /// <see cref="FeatureArtPackageIngest"/> 复用同一套 Raw 前缀 / bindKind 校验，
        /// 禁止第二套校验逻辑或复制粘贴本方法。</summary>
        public static bool TryBindCore(FeatureArtSlot slot, UnityEngine.Object picked, out string reason)
        {
            reason = null;
            if (slot == null || picked == null)
            {
                reason = "槽位或资源为空。";
                return false;
            }

            try
            {
                var path = AssetDatabase.GetAssetPath(picked);
                if (string.IsNullOrEmpty(path) || !path.StartsWith(RawPrefix, StringComparison.Ordinal))
                {
                    reason = $"拒绝：{slot.id} 所选资源不在 {RawPrefix} 下（{path}）。";
                    return false;
                }

                if (!ValidateKind(slot.bindKind, picked, out var vReason))
                {
                    reason = $"拒绝：{slot.id} {vReason}";
                    return false;
                }

                slot.location = Path.GetFileNameWithoutExtension(path);
                slot.package = "";
                return true;
            }
            catch (Exception e)
            {
                reason = e.Message;
                Debug.LogError(e);
                return false;
            }
        }

        /// <summary>拖拽/清空/复制共用的字段绘制（story-003「保存/清空」逻辑原样复用）。
        /// story-011 D2：成品槽默认单行 ObjectField，禁止再传自定义 Height。
        /// story-017：InstancedMesh 槽改走 <see cref="DrawInstancedMeshBindField"/>（类型下拉 + ObjectField），
        /// 其它 bindKind（MaterialOverride / PooledPrefab）保持本方法原逻辑不变。</summary>
        public void DrawBindField(FeatureArtSlot slot)
        {
            if (slot == null)
            {
                SirenixEditorGUI.MessageBox("未同步", MessageType.None);
                return;
            }

            if (slot.bindKind == "InstancedMesh")
            {
                DrawInstancedMeshBindField(slot);
                return;
            }

            var type = ObjectFieldType(slot.bindKind);
            var current = ResolveCurrentAsset(slot, type);
            EditorGUI.BeginChangeCheck();
            var picked = EditorGUILayout.ObjectField(current, type, false);
            if (EditorGUI.EndChangeCheck() && picked != null)
            {
                TryBind(slot, picked);
            }

            DrawClearAndCopyRow(slot);
        }

        /// <summary>story-017：外形槽（InstancedMesh）同一行画「网格/预制体/模型」下拉 + 对应类型的
        /// ObjectField。已绑定按 <see cref="DetectInstancedMeshKind"/> 自动切档（下拉锁定，反映磁盘现状，
        /// 不得手改；要换类型先清空）；空槽用 <c>_instancedMeshKindOverride</c> 记住上次手选，默认预制体。
        /// 禁止在此对该槽用 <c>typeof(UnityEngine.Object)</c>——那会把文件夹送进拾取器。</summary>
        void DrawInstancedMeshBindField(FeatureArtSlot slot)
        {
            int kind;
            UnityEngine.Object current;
            bool locked = !string.IsNullOrEmpty(slot.location);
            if (locked)
            {
                kind = DetectInstancedMeshKind(slot, out current);
                _instancedMeshKindOverride[slot.id] = kind;
            }
            else
            {
                current = null;
                if (!_instancedMeshKindOverride.TryGetValue(slot.id, out kind))
                {
                    kind = MeshKindPrefab;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(locked))
                {
                    EditorGUI.BeginChangeCheck();
                    var newKind = EditorGUILayout.Popup(kind, MeshKindLabels, GUILayout.Width(56));
                    if (!locked && EditorGUI.EndChangeCheck())
                    {
                        _instancedMeshKindOverride[slot.id] = newKind;
                        kind = newKind;
                    }
                }

                var type = InstancedMeshObjectFieldType(kind);
                EditorGUI.BeginChangeCheck();
                var picked = EditorGUILayout.ObjectField(current, type, false);
                if (EditorGUI.EndChangeCheck() && picked != null)
                {
                    TryBind(slot, picked);
                }
            }

            DrawClearAndCopyRow(slot);
            DrawRebakeRow(slot);
        }

        void DrawRebakeRow(FeatureArtSlot slot)
        {
            if (slot == null || string.IsNullOrEmpty(slot.folderHint) || string.IsNullOrEmpty(slot.location))
            {
                return;
            }

            if (!FeatureArtHunyuanGenerate.TryCanonical(slot, out var folder, out var name, out _))
            {
                return;
            }

            var package = FeatureArtHunyuanGenerate.PackageDir(folder, name);
            var prefabPath = FeatureArtGamePrefabBaker.PrefabAssetPath(package, name);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("从整包重烘 Prefab", GUILayout.Width(140)))
                {
                    if (FeatureArtGamePrefabBaker.TryBakeOne(package, name, out var log))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                        if (prefab != null)
                        {
                            TryBind(slot, prefab);
                            SaveCatalogNow();
                        }

                        _lastLog = log;
                    }
                    else
                    {
                        _lastLog = log;
                    }
                }

                EditorGUILayout.LabelField("母带 FBX 改完后点；材质球 organ_*_runtime 会保留。",
                    EditorStyles.miniLabel);
            }
        }

        void DrawClearAndCopyRow(FeatureArtSlot slot)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空", GUILayout.Width(48)))
                {
                    slot.location = "";
                    _dirty = true;
                    _lastLog = $"{slot.id} 已清空 location（look/prompt 保留）。";
                }

                if (GUILayout.Button("复制提示词", GUILayout.Width(80)))
                {
                    EditorGUIUtility.systemCopyBuffer = slot.prompt ?? "";
                    _lastLog = $"{slot.id} 提示词已复制。";
                }
            }
        }

        static Type InstancedMeshObjectFieldType(int kind) => kind == MeshKindMesh ? typeof(Mesh) : typeof(GameObject);

        /// <summary>已绑定槽按 <see cref="ResolveCurrentAsset"/> 的解析结果反推下拉档位：
        /// 先按 GameObject 类型解析（内部已按 .prefab &gt; .fbx/.obj 优先级挑同名资源），命中就按扩展名分
        /// 预制体/模型；GameObject 解析落空再按 Mesh 类型解析，命中即网格；两路都空（坏 location）时
        /// 回退预制体档，<paramref name="asset"/> 为 null，故 ObjectField 显示为空、不崩关。</summary>
        static int DetectInstancedMeshKind(FeatureArtSlot slot, out UnityEngine.Object asset)
        {
            asset = ResolveCurrentAsset(slot, typeof(GameObject));
            if (asset != null)
            {
                var path = AssetDatabase.GetAssetPath(asset);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                return ext == ".prefab" ? MeshKindPrefab : MeshKindModel;
            }

            asset = ResolveCurrentAsset(slot, typeof(Mesh));
            return asset != null ? MeshKindMesh : MeshKindPrefab;
        }

        /// <summary>story-017：文件夹（<see cref="DefaultAsset"/>）在任何 bindKind 下都不是合法绑定目标——
        /// 混元整包 <c>{canonical}/</c> 只是磁盘布局，不是可绑资产。放在 switch 前统一挡，不必每个 case 各写一遍。</summary>
        static bool ValidateKind(string bindKind, UnityEngine.Object obj, out string reason)
        {
            if (obj is DefaultAsset || AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(obj)))
            {
                reason = "拒绝文件夹：绑定的是包内模型/资源，不是整包目录。";
                return false;
            }

            switch (bindKind)
            {
                case "InstancedMesh":
                    if (obj is Mesh)
                    {
                        reason = null;
                        return true;
                    }

                    if (obj is GameObject go && go.GetComponentInChildren<MeshFilter>(true) != null)
                    {
                        reason = null;
                        return true;
                    }

                    reason = "InstancedMesh 需要 Mesh 或含 MeshFilter 的 GameObject。";
                    return false;
                case "MaterialOverride":
                    if (obj is Material)
                    {
                        reason = null;
                        return true;
                    }

                    reason = "MaterialOverride 需要 Material。";
                    return false;
                case "PooledPrefab":
                    if (obj is GameObject)
                    {
                        reason = null;
                        return true;
                    }

                    reason = "PooledPrefab 需要 GameObject Prefab。";
                    return false;
                default:
                    reason = $"未知 bindKind：{bindKind}";
                    return false;
            }
        }

        public static Type ObjectFieldType(string bindKind)
        {
            switch (bindKind)
            {
                case "MaterialOverride": return typeof(Material);
                case "PooledPrefab": return typeof(GameObject);
                default: return typeof(UnityEngine.Object);
            }
        }

        /// <summary>story-017 + Prefab 烘焙管线：同名候选优先 <c>.prefab</c>（游戏成品），
        /// 再 <c>.fbx/.obj</c>（母带），再其它（Mesh 资源）。禁止返回文件夹。</summary>
        public static UnityEngine.Object ResolveCurrentAsset(FeatureArtSlot slot, Type type)
        {
            if (slot == null || string.IsNullOrEmpty(slot.location))
            {
                return null;
            }

            var guids = AssetDatabase.FindAssets(slot.location, new[] { "Assets/GameRes/Raw" });
            UnityEngine.Object best = null;
            var bestRank = int.MaxValue;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != slot.location || AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath(path, type);
                if (asset == null)
                {
                    continue;
                }

                var rank = RawAssetPriorityRank(path);
                if (rank < bestRank)
                {
                    best = asset;
                    bestRank = rank;
                }
            }

            return best;
        }

        static int RawAssetPriorityRank(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".prefab":
                    return 0;
                case ".fbx":
                case ".obj":
                    return 1;
                default:
                    return 2;
            }
        }

        static bool HasFilenameConflict(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return false;
            }

            var guids = AssetDatabase.FindAssets("", new[] { "Assets/GameRes/Raw" });
            var count = 0;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == location)
                {
                    count++;
                }
            }

            return count > 1;
        }

        /// <summary>PANEL-UX §8：空槽灰点 / 已绑绿点 / 撞名坏点，画成左树 Icon（非文字后缀，才能真正上色）。</summary>
        static Texture2D StatusIcon(FeatureArtSlot slot)
        {
            if (slot == null || slot.retired)
            {
                return null;
            }

            if (string.IsNullOrEmpty(slot.location))
            {
                return _dotEmpty ??= MakeDot(ColorEmpty);
            }

            return HasFilenameConflict(slot.location) ? (_dotBad ??= MakeDot(ColorBad)) : (_dotBound ??= MakeDot(ColorBound));
        }

        static Texture2D MakeDot(Color color)
        {
            var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color[64];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        static Color HexColor(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        /// <summary>PANEL-UX §8 身份色芯片（Family · Shape），禁止灰字 miniLabel 冒充芯片。</summary>
        static void DrawChip(string text, Color color)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(6, 6, 2, 2),
            };
            style.normal.textColor = Color.white;
            var size = style.CalcSize(new GUIContent(text));
            var rect = GUILayoutUtility.GetRect(size.x, size.y, GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, text, style);
        }

        void RunSync()
        {
            try
            {
                var added = FeatureArtSlotSync.Sync(_data);
                _dirty = true;
                _lastLog = $"同步完成：新增 {added} 槽（look/prompt 已按 LOOK-PROMPTS 覆盖；location 不动）。";
                ForceMenuTreeRebuild();
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        void Reload()
        {
            try
            {
                _data = FeatureArtCatalogIO.Load();
                _dirty = false;
                _lastLog = $"已加载 {_data.slots.Count} 槽。";
            }
            catch (Exception e)
            {
                _data = null;
                _lastLog = e.Message;
                Debug.LogError(e);
            }

            LoadRegistry();
        }

        void LoadRegistry()
        {
            _workingAssets.Clear();
            _addViewKeys.Clear();
            try
            {
                _registry = CellArtRegistryService.Load();
                CellArtRegistryService.EnsureFolders(_registry);
                _registryDirty = false;
            }
            catch (Exception e)
            {
                _registry = new CellArtRegistry
                {
                    assets = new List<CellArtAsset>(),
                    dirs = new CellArtDirs(),
                };
                _registryDirty = false;
                Debug.LogError(e);
            }
        }

        public void SaveCatalogNow()
        {
            if (_data == null)
            {
                return;
            }

            FeatureArtCatalogIO.Save(_data);
            _dirty = false;
        }

        void Save()
        {
            try
            {
                var parts = new List<string>();
                if (_dirty && _data != null)
                {
                    FeatureArtCatalogIO.Save(_data);
                    parts.Add(FeatureArtCatalogIO.AbsolutePath);
                }

                if (_registryDirty && _registry != null)
                {
                    CellArtRegistryService.Save(_registry);
                    parts.Add("Art/Cell/registry.json");
                }

                _dirty = false;
                _registryDirty = false;
                _lastLog = parts.Count == 0 ? "没有要保存的修改。" : "已保存 " + string.Join("；", parts);
            }
            catch (Exception e)
            {
                _lastLog = e.Message;
                Debug.LogError(e);
            }
        }

        // ---- 右栏页面（Odin 通过反射画这些 POCO；OnInspectorGUI 承载复用的绑定/校验逻辑）----

        sealed class GuidePage
        {
            [OnInspectorGUI]
            void Draw()
            {
                SirenixEditorGUI.Title("使用说明", null, TextAlignment.Left, true);
                SirenixEditorGUI.MessageBox(
                    "选攻击器官 → 看/复制外形提示词做模型（对照概念图）→ 看/复制开火提示词做特效 → 拖进同一页。\n\n" +
                    "1. 工具栏『从代码同步槽位』补齐新功能的空槽（look/prompt 每次都会按 LOOK-PROMPTS 覆盖，location 不动）；\n" +
                    "2. 选中左树『攻击器官』下的器官，同一页拖外形预制体、复制开火四段提示词；\n" +
                    "3. 三视图下勾要发给混元的图（默认只发概念图），选模型后「用三视图生成模型并绑定」（先在左树「混元生3D」填 Key）；会落整包并自动烘焙游戏 Prefab（Unity Standard + 整包贴图，不要自定义 shader）。对象框应显示 Prefab；\n" +
                    "4. 点工具栏『保存』；\n" +
                    "5. Play 模式或『健康检查』核对。\n\n" +
                    "源文件登记（概念图 / fbx / 扫盘 / 图板）在左树『源文件库』，与成品换皮同一扇窗。工具栏『打开源文件板』会跳到该页。各功能页也可直接改概念图/三视图，都写 registry.json，不是 catalog。",
                    MessageType.Info);
                SirenixEditorGUI.MessageBox(
                    "空槽 = 白模，游戏照常能跑；拖 Assets/GameRes/Art/ 下资源会被拒绝——Art 是源文件，不进 YooAsset 热更包。\n" +
                    "location = 拖入资源文件名（去扩展名），Raw 全树文件名须全局唯一，撞名会被『健康检查』标红。\n" +
                    "成品默认绑 Prefab；FBX/OBJ 是母带。外形槽也可手拖 Mesh / Prefab / FBX；不要拖文件夹。混元整包是磁盘布局。",
                    MessageType.None);
            }
        }

        sealed class HealthCheckPage
        {
            readonly FeatureArtBindingWindow _window;

            public HealthCheckPage(FeatureArtBindingWindow window) => _window = window;

            [Button("运行健康检查")]
            void Run() => _window.RunHealthCheck();

            [OnInspectorGUI]
            void Draw()
            {
                SirenixEditorGUI.Title("健康检查", null, TextAlignment.Left, true);
                var issues = _window.HealthIssues;
                if (issues == null)
                {
                    SirenixEditorGUI.MessageBox("尚未运行。", MessageType.Info);
                    return;
                }

                if (issues.Count == 0)
                {
                    var boundCount = _window.Data?.slots?.Count(s => !s.retired && !string.IsNullOrEmpty(s.location)) ?? 0;
                    SirenixEditorGUI.MessageBox($"全部通过，{boundCount} 个已绑定槽零异常", MessageType.Info);
                    return;
                }

                foreach (var issue in issues)
                {
                    SirenixEditorGUI.MessageBox($"{issue.SlotId}: {issue.Message}", MessageType.Error);
                }
            }
        }

        sealed class PlayerPage
        {
            readonly FeatureArtBindingWindow _window;

            public PlayerPage(FeatureArtBindingWindow window) => _window = window;

            FeatureArtSlot MeshSlot => _window.FindSlot("player.chassis.mesh");
            FeatureArtSlot MaterialSlot => _window.FindSlot("player.chassis.material");

            [OnInspectorGUI, PropertyOrder(-50)]
            void DrawIdentity()
            {
                SirenixEditorGUI.Title("玩家 / 本体", null, TextAlignment.Left, true);
                var slot = MeshSlot;
                if (slot != null && !string.IsNullOrEmpty(slot.look))
                {
                    EditorGUILayout.LabelField(slot.look, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(-40)]
            void DrawBind()
            {
                EditorGUILayout.LabelField("外形绑定", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("网格", EditorStyles.miniBoldLabel);
                        _window.DrawBindField(MeshSlot);
                    }

                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("材质", EditorStyles.miniBoldLabel);
                        var mat = MaterialSlot;
                        if (mat != null && !string.IsNullOrEmpty(mat.look))
                        {
                            EditorGUILayout.LabelField(mat.look, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandWidth(true));
                        }

                        _window.DrawBindField(mat);
                    }
                }

                EditorGUILayout.Space(2);
                var prompt = MeshSlot?.prompt;
                if (!string.IsNullOrEmpty(prompt))
                {
                    _window.DrawWrappedBox(prompt, MessageType.Info);
                }

                if (GUILayout.Button("复制生模提示词", GUILayout.Width(110)))
                {
                    EditorGUIUtility.systemCopyBuffer = prompt ?? "";
                    _window.Log("player.chassis 生模提示词已复制。");
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(-30)]
            void DrawConcept()
            {
                EditorGUILayout.LabelField("概念图 / 三视图", EditorStyles.boldLabel);
                FeatureArtCellArtBridge.DrawViews(_window, FeatureArtCellArtBridge.PlayerCellArtId, "玩家 / 本体", MeshSlot);
                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(50)]
            void DrawNotes()
            {
                EditorGUILayout.LabelField("给制作的说明", EditorStyles.boldLabel);
                _window.DrawWrappedBox(PlayerNote, MessageType.None);
            }
        }

        sealed class OrganPage
        {
            readonly FeatureArtBindingWindow _window;
            readonly string _organId;
            readonly string _groupZh;
            readonly string _shapeKey;
            readonly string _summonKey;

            public OrganPage(FeatureArtBindingWindow window, string organId, string groupZh, string shapeKey, string summonKey)
            {
                _window = window;
                _organId = organId;
                _groupZh = groupZh;
                _shapeKey = shapeKey;
                _summonKey = summonKey;
            }

            FeatureArtSlot MeshSlot => _window.FindSlot($"organ.{_organId}.mesh");
            FeatureArtSlot ShapeSlot(string role) => _window.FindSlot($"shape.{_shapeKey}.{role}");

            [OnInspectorGUI, PropertyOrder(-50)]
            void DrawIdentity()
            {
                var slot = MeshSlot;
                var title = slot?.titleZh?.Replace(" · 本体网格", "") ?? _organId;
                SirenixEditorGUI.Title(title, null, TextAlignment.Left, true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawChip(_groupZh, FamilyColor.TryGetValue(_groupZh, out var fc) ? fc : Color.gray);
                    GUILayout.Space(4);
                    DrawChip(ShapeZh(_shapeKey), ShapeColor.TryGetValue(_shapeKey, out var sc) ? sc : Color.gray);
                }

                if (slot != null && !string.IsNullOrEmpty(slot.look))
                {
                    EditorGUILayout.LabelField(slot.look, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(-40)]
            void DrawShapeBind()
            {
                EditorGUILayout.LabelField("外形绑定", EditorStyles.boldLabel);
                _window.DrawBindField(MeshSlot);

                EditorGUILayout.Space(2);
                var prompt = MeshSlot?.prompt;
                if (!string.IsNullOrEmpty(prompt))
                {
                    _window.DrawWrappedBox(prompt, MessageType.Info);
                }

                if (GUILayout.Button("复制生模提示词", GUILayout.Width(110)))
                {
                    EditorGUIUtility.systemCopyBuffer = prompt ?? "";
                    _window.Log($"{_organId} 生模提示词已复制。");
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(-30)]
            void DrawConcept()
            {
                EditorGUILayout.LabelField("概念图 / 三视图", EditorStyles.boldLabel);
                FeatureArtCellArtBridge.DrawViews(_window, FeatureArtCellArtBridge.OrganCellArtId(_organId),
                    MeshSlot?.titleZh?.Replace(" · 本体网格", "") ?? _organId, MeshSlot);
                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(0)]
            void DrawFireTimeline()
            {
                if (_summonKey != null)
                {
                    EditorGUILayout.LabelField("召唤链接", EditorStyles.boldLabel);
                    _window.DrawWrappedBox($"链到召唤实体 / {_summonKey}", MessageType.None);
                    if (GUILayout.Button($"跳到召唤实体 / {_summonKey}"))
                    {
                        _window.JumpToSummon(_summonKey);
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("仍可绑定该 Shape 的命中/爆炸（召唤物命中语言）", EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawRoleColumn("命中", "hit");
                        DrawRoleColumn("爆炸", "explode");
                    }

                    EditorGUILayout.Space(4);
                    return;
                }

                EditorGUILayout.LabelField("开火时间轴", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRoleColumn("枪口", "muzzle");
                    DrawRoleColumn("弹体", "projectile");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRoleColumn("命中", "hit");
                    DrawRoleColumn("爆炸", "explode");
                }

                var sharedWith = _window.OrgansSharingShape(_shapeKey, _organId);
                var info = sharedWith.Count > 0
                    ? $"{ShapeZh(_shapeKey)} 语言与 {string.Join("、", sharedWith)} 共用；改这里两边一起变。"
                    : $"{ShapeZh(_shapeKey)} 语言当前仅本器官使用。";
                _window.DrawWrappedBox(info, MessageType.Info);

                if (GUILayout.Button($"跳到弹道语言 / {ShapeZh(_shapeKey)}"))
                {
                    _window.JumpToShape(_shapeKey);
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(50)]
            void DrawNotes()
            {
                EditorGUILayout.LabelField("给制作的说明", EditorStyles.boldLabel);
                if (OrganNotes.TryGetValue(_organId, out var note))
                {
                    _window.DrawWrappedBox(note, MessageType.None);
                }
            }

            void DrawRoleColumn(string labelZh, string role)
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(200)))
                {
                    EditorGUILayout.LabelField(labelZh, EditorStyles.miniBoldLabel);
                    _window.DrawBindField(ShapeSlot(role));
                }
            }
        }

        sealed class ShapePage
        {
            readonly FeatureArtBindingWindow _window;
            readonly string _shapeKey;

            public ShapePage(FeatureArtBindingWindow window, string shapeKey)
            {
                _window = window;
                _shapeKey = shapeKey;
            }

            [OnInspectorGUI, PropertyOrder(-10)]
            void DrawHeader()
            {
                SirenixEditorGUI.Title($"弹道语言 · {ShapeZh(_shapeKey)}（{_shapeKey}）", null, TextAlignment.Left, true);
                var usedBy = _window.OrgansUsingShape(_shapeKey);
                if (usedBy.Count == 0)
                {
                    SirenixEditorGUI.MessageBox("暂无器官使用该 Shape（预留）。", MessageType.None);
                }
                else
                {
                    EditorGUILayout.LabelField("被谁使用：", EditorStyles.miniBoldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        foreach (var (organId, titleZh) in usedBy)
                        {
                            if (GUILayout.Button(titleZh, GUILayout.Width(90)))
                            {
                                _window.JumpToOrgan(organId);
                            }
                        }
                    }
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(0)]
            void DrawRoles()
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRole("枪口", "muzzle");
                    DrawRole("弹体", "projectile");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRole("命中", "hit");
                    DrawRole("爆炸", "explode");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawRole("瞄准预览", "indicator");
                }

                EditorGUILayout.Space(4);

                // 不定长提示词独立成行，禁止与上方定宽绑定列挤在同一个 HorizontalScope 里（PANEL-UX §11.1）。
                DrawRolePrompt("枪口", "muzzle");
                DrawRolePrompt("弹体", "projectile");
                DrawRolePrompt("命中", "hit");
                DrawRolePrompt("爆炸", "explode");
                DrawRolePrompt("瞄准预览", "indicator");

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(10)]
            void DrawConcept()
            {
                EditorGUILayout.LabelField("概念图 / 三视图", EditorStyles.boldLabel);
                FeatureArtCellArtBridge.DrawViews(_window, FeatureArtCellArtBridge.ShapeCellArtId(_shapeKey), _shapeKey,
                    _window.FindSlot($"shape.{_shapeKey}.projectile"));
                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(50)]
            void DrawNotes()
            {
                EditorGUILayout.LabelField("给制作的说明", EditorStyles.boldLabel);
                if (ShapeNotes.TryGetValue(_shapeKey, out var note))
                {
                    _window.DrawWrappedBox(note, MessageType.None);
                }
            }

            void DrawRole(string labelZh, string role)
            {
                var slot = _window.FindSlot($"shape.{_shapeKey}.{role}");
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(220)))
                {
                    EditorGUILayout.LabelField(labelZh, EditorStyles.miniBoldLabel);
                    _window.DrawBindField(slot);
                }
            }

            void DrawRolePrompt(string labelZh, string role)
            {
                var slot = _window.FindSlot($"shape.{_shapeKey}.{role}");
                if (slot == null || string.IsNullOrEmpty(slot.prompt))
                {
                    return;
                }

                EditorGUILayout.LabelField($"{labelZh} 提示词", EditorStyles.miniBoldLabel, GUILayout.ExpandWidth(true));
                _window.DrawWrappedBox(slot.prompt, MessageType.None);
                if (GUILayout.Button("复制", GUILayout.Width(48)))
                {
                    EditorGUIUtility.systemCopyBuffer = slot.prompt;
                    _window.Log($"{slot.id} 提示词已复制。");
                }

                EditorGUILayout.Space(2);
            }
        }

        sealed class SimpleMeshPage
        {
            readonly FeatureArtBindingWindow _window;
            readonly string _slotId;
            readonly string _titleZh;
            readonly string _groupZh;
            readonly Color _groupColor;
            readonly string _cellArtId;
            readonly string _tailNote;

            public SimpleMeshPage(FeatureArtBindingWindow window, string slotId, string titleZh, string groupZh,
                Color groupColor, string cellArtId, string tailNote)
            {
                _window = window;
                _slotId = slotId;
                _titleZh = titleZh;
                _groupZh = groupZh;
                _groupColor = groupColor;
                _cellArtId = cellArtId;
                _tailNote = tailNote;
            }

            FeatureArtSlot Slot => _window.FindSlot(_slotId);

            [OnInspectorGUI, PropertyOrder(-50)]
            void DrawIdentity()
            {
                var slot = Slot;
                SirenixEditorGUI.Title(_titleZh, null, TextAlignment.Left, true);
                if (!string.IsNullOrEmpty(_groupZh))
                {
                    DrawChip(_groupZh, _groupColor);
                }

                if (slot != null && !string.IsNullOrEmpty(slot.look))
                {
                    EditorGUILayout.LabelField(slot.look, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(-40)]
            void DrawBind()
            {
                EditorGUILayout.LabelField("外形绑定", EditorStyles.boldLabel);
                var slot = Slot;
                _window.DrawBindField(slot);

                EditorGUILayout.Space(2);
                var prompt = slot?.prompt;
                if (!string.IsNullOrEmpty(prompt))
                {
                    _window.DrawWrappedBox(prompt, MessageType.Info);
                }

                if (GUILayout.Button("复制生模提示词", GUILayout.Width(110)))
                {
                    EditorGUIUtility.systemCopyBuffer = prompt ?? "";
                    _window.Log($"{_slotId} 生模提示词已复制。");
                }

                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(-30)]
            void DrawConcept()
            {
                EditorGUILayout.LabelField("概念图 / 三视图", EditorStyles.boldLabel);
                FeatureArtCellArtBridge.DrawViews(_window, _cellArtId, _titleZh, Slot);
                EditorGUILayout.Space(4);
            }

            [OnInspectorGUI, PropertyOrder(50)]
            void DrawNotes()
            {
                if (string.IsNullOrEmpty(_tailNote))
                {
                    return;
                }

                EditorGUILayout.LabelField("给制作的说明", EditorStyles.boldLabel);
                _window.DrawWrappedBox(_tailNote, MessageType.None);
            }
        }

        /// <summary>story-007：结构槽下的具体器官子节点。只读信息页——这些器官没有独立外形绑定，
        /// 外形跟随所在槽位（同标签共用外观，<c>DESIGN.md</c> §3/§7 已定案），不要复用/改动
        /// <see cref="SimpleMeshPage"/> 的绑定逻辑。</summary>
        sealed class StructuralOrganInfoPage
        {
            readonly string _titleZh;
            readonly string _slotTitleZh;
            readonly string _note;

            public StructuralOrganInfoPage(string titleZh, string slotTitleZh, string note)
            {
                _titleZh = titleZh;
                _slotTitleZh = slotTitleZh;
                _note = note;
            }

            [OnInspectorGUI]
            void Draw()
            {
                SirenixEditorGUI.Title(_titleZh, null, TextAlignment.Left, true);
                SirenixEditorGUI.MessageBox(_note, MessageType.None);
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(
                    $"外形与同标签其它器官共用（见上一级槽位「{_slotTitleZh}」），抽到本器官会替换当前装备的同标签器官。",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }
    }
}
