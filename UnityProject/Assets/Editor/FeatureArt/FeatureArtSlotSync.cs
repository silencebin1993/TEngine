using System.Collections.Generic;
using System.Linq;
using GameLogic.ArtBinding;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-003 R8/R9：从代码扫 organ/shape/summon/player 四域生成期望槽位，
    /// 与已有 catalog 合并——按 id 匹配，只增槽、标 retired，绝不清空/覆盖已有 location，
    /// Brief 字段也不覆盖已存在的槽（防止吞掉人工修订）。enemy 域本批不做（R8，避免臆造槽位）。
    /// story-010 D6：look/prompt 是唯一例外——每次同步无条件覆盖已存在槽的这两个字段
    /// （LOOK-PROMPTS.md 是唯一权威文案来源，不是人工可改的 Brief，不参与"防止吞掉人工修订"规则）。</summary>
    public static class FeatureArtSlotSync
    {
        static readonly string[] SummonKeys = { "spore", "phage", "mycelium" };
        static readonly string[] ShapeRoles = { "projectile", "muzzle", "hit", "explode", "indicator" };
        static readonly HashSet<string> SyncedDomains = new HashSet<string> { "organ", "shape", "summon", "player", "enemy" };

        // 中文名权威来源：DesignDocs/最新改动需求/组合引擎-正名与全阶段变化词宪法.md §3.2；
        // Melee 不在该表（Delivery 只列 6 个），沿用 FeatureArtBindingWindow 既有的"近战"分组名。
        // 只改 titleZh 显示文案，不改 key/id——槽 id、文件名、文件夹名仍用英文 shape 名。
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

        // ---- story-010 D6：LOOK-PROMPTS.md 原文抄录（look/prompt 唯一权威来源）----

        const string GeoLockHead = "显微镜下的风格化细胞器官，不透明哑光粘土玩具，实心单色，均匀棚灯，强剪影，低面数，闭合体积，无破洞。纯色背景，主体居中约占画面 65%。";
        const string GeoLockTail = "不要玻璃、透明、半透明、液体折射、粒子、烟雾、文字、UI、水印、景深、漂浮碎件。不要画整只细胞全身，只要这一件。功能端指向画面右侧。轴心在体积中心。";
        const string FxLockHead = "俯视培养皿战斗特效，鲜艳但短促，一眼能从白模挤出片里认出来。不要过曝白闪，不要铺满全屏。";
        const string FxLockTail = "短生命周期；+X 为飞行/挥击前方；不要自带相机、不要 AudioListener。";

        static string GeoPrompt(string body) => GeoLockHead + body + GeoLockTail;
        static string FxPrompt(string body) => FxLockHead + body + FxLockTail;

        const string PlayerChassisLook = "胖梭形胶囊，前端略尖";
        const string PlayerChassisBody = "玩家细胞本体：胖梭形胶囊，前端略尖像蝌蚪头，没有四肢，表面光滑有浅槽，体积像一颗大葡萄。";
        const string PlayerChassisMaterialLook = "用 SimBioGlass，勾 GPU Instancing";

        static readonly Dictionary<string, (string Look, string Body)> OrganArt = new Dictionary<string, (string, string)>
        {
            ["org_emitter"] = ("圆泡右侧一根喷嘴", "代谢喷射器官：浑圆母泡右侧伸出一根粗短喷嘴，口朝右，像细胞版水枪，单件闭合。"),
            ["org_cilia"] = ("梭体前方一丛硬毛", "近战纤毛：梭形主体，右端密生一丛粗硬纤毛（≤8 根、要粗），像刷子戳出去，毛必须连在体上。"),
            ["org_spine"] = ("圆球插满短棘", "反刺外壳：圆球周身均匀短锥棘（11 根左右），像微型蒺藜，棘与球一体，无漂浮刺。"),
            ["org_phago"] = ("厚唇裂口的大圆", "吞噬口器：偏大圆体，右侧一张厚唇裂口，口缘不规则，像变形虫的大口，闭合体积无牙床细节堆砌。"),
            ["org_lensbeam"] = ("扁晶+朝右细管", "聚焦光束器官：扁椭圆晶状体，右侧伸出一根细直导管，像镜头接激光管，实心不透明。"),
            ["org_enzyme"] = ("葡萄串腺、底有滴口", "酶腺：几颗粘在一起的圆泡（3～5），底部一个朝下的滴口，像葡萄串腺体，全部粘连。"),
            ["org_osmotic"] = ("扁环膜包核", "渗透场核：中心小球，外一圈扁环膜像游泳圈，环与核相连，不要离散粒子环。"),
            ["org_orbitcilia"] = ("赤道一圈短纤毛", "环带纤毛：中球，赤道一圈粗短纤毛（6 根），像行星环但必须长在球上。"),
            ["org_wave"] = ("朝右张开的新月瓣", "波形瓣：一个朝右张开的新月/扇壳，厚实，像细胞膜被推出去的一波，单片闭合。"),
            ["org_pseudopod"] = ("宽掌伪足朝右", "伪足：从圆体向右摊开的宽掌状肉瓣，像拍击的手掌，边缘圆钝，连在母体上。"),
            ["org_drill"] = ("螺旋锥头朝右", "钻头器官：右端螺旋锥，左端圆柄，像细胞钻头，螺纹要粗、圈数少（3 圈）。"),
            ["org_bud"] = ("母体侧粘着小芽", "芽殖：较大圆体右侧粘着 1～2 颗小圆芽，芽必须贴住母体，禁止拆开的双胞胎。"),
            ["org_mycelium"] = ("扁锚+贴地根须", "菌丝锚：扁平垫状主体，向下放射 6～8 根粗短根须贴地，不要立起来的树。"),
        };

        static readonly Dictionary<string, (string Look, string Body)> SummonArt = new Dictionary<string, (string, string)>
        {
            ["spore"] = ("带小尖帽的小球", "跟随芽：比玩家小很多的圆球，顶上一个短锥尖帽，像孢子幼体，单件。"),
            ["phage"] = ("头球+尾柄", "噬菌体简化形：右端圆头，左端一根粗柄，像微型注射器，两段必须连体。"),
            ["mycelium"] = ("扁垫+贴地根", "定点菌毯炮台：比玩家扁，贴地垫 + 一圈短根须，不要细丝网。"),
        };

        static readonly Dictionary<string, (string Look, string Body)> EnemyArt = new Dictionary<string, (string, string)>
        {
            ["vis_1"] = ("软圆、无口", "最弱浮游团：光滑圆球，略扁，无刺无口，像食物颗粒。"),
            ["vis_2"] = ("扁盘带短刺边", "刺膜虫：扁圆盘，边缘一圈短刺，像微型海胆饼。"),
            ["vis_3"] = ("椭圆+一条粗尾", "扫尾虫：椭圆身体右侧一条粗尾，整体像逗号。"),
            ["vis_4"] = ("细梭、头尖", "追猎者：细长梭，右端尖，像小鱼，无鳍碎件。"),
            ["vis_5"] = ("头大尾柄", "噬菌形敌：圆头+短柄，比玩家的噬菌召唤更粗笨。"),
            ["vis_6"] = ("厚甲半球", "硬壳：半球甲，表面几块凸起板块，像甲虫背，闭合。"),
            ["vis_7"] = ("少根粗触须", "导电体：球上 ≤4 根粗触须，禁止发丝。"),
            ["vis_8"] = ("破口不对称", "腐败团：圆体缺一块不规则破口，边缘加厚，仍是单 mesh。"),
            ["vis_9"] = ("更尖的梭", "游隼：比追猎更细更尖的梭，棱更利。"),
            ["vis_10"] = ("长棘球", "毒棘：球上少量（7）更长的棘，棘要粗。"),
            ["vis_11"] = ("扁垫小敌", "小菌毯：扁平贴地小垫，短根须。"),
            ["vis_20"] = ("碎块多面体", "残块：不规则凸多面体，像咬剩的壳，闭合。"),
            ["vis_50"] = ("同族放大+脊", "精英一级：在硬壳半球上加一条中脊，体量明显更大。"),
            ["vis_51"] = ("双脊甲", "精英二级：双脊甲壳，比一级更张扬。"),
            ["vis_52"] = ("冠刺甲", "精英三级：甲壳顶一圈短冠刺。"),
            ["vis_90"] = ("巨体+张口", "首领：明显最大的厚唇裂口体，张口朝右，剪影必须碾压小兵。"),
        };

        static readonly Dictionary<string, string> ShapeLanguage = new Dictionary<string, string>
        {
            ["Bolt"] = "代谢弹：蜜黄小锥滴，尖朝飞行方向。",
            ["Beam"] = "青白细束：一根实心棒，不要电弧碎丝。",
            ["Arc"] = "橙红扇瓣：厚实楔块，不要线框。",
            ["Field"] = "青绿厚环带：扁环，内孔大。",
            ["Wave"] = "水色扩散环：细环，像水面一圈。",
            ["Spore"] = "紫十字孢：小十字或小星，实体。",
            ["Melee"] = "猩红短弧：身前一截厚月牙，不要剑模型。",
        };

        static readonly Dictionary<string, string> ShapeRoleBody = new Dictionary<string, string>
        {
            ["projectile"] = "这是飞行/持续中的本体，体积小、俯视能认。",
            ["muzzle"] = "这是开火瞬间贴在细胞右前方的一小朵爆，只一帧到三帧。",
            ["hit"] = "这是打在目标身上的短促溅开，不要画在玩家脚下。",
            ["explode"] = "这是落点炸开的一圈，环形可读，不要火球蘑菇云。",
            ["indicator"] = "这是未开火时的装配预览标记，半透明贴在玩家脚下，静态不飞行，不要做成和实弹一样的高饱和实心表现。",
        };

        /// <summary>返回新增槽数量；已存在槽的 retired 标记就地更新，look/prompt 无条件覆盖。</summary>
        public static int Sync(FeatureArtCatalogData data)
        {
            var expected = BuildExpectedSlots();
            var expectedIds = new HashSet<string>(expected.Select(s => s.id));
            var existingById = new Dictionary<string, FeatureArtSlot>();
            foreach (var s in data.slots)
            {
                existingById[s.id] = s;
            }

            // 源头已不存在但槽仍在 catalog 里 → 标 retired；location/Brief 原样保留。
            foreach (var slot in data.slots)
            {
                if (!SyncedDomains.Contains(slot.domain))
                {
                    continue;
                }

                slot.retired = !expectedIds.Contains(slot.id);
            }

            int added = 0;
            foreach (var exp in expected)
            {
                if (existingById.TryGetValue(exp.id, out var existing))
                {
                    existing.retired = false;
                    existing.look = exp.look;
                    existing.prompt = exp.prompt;
                    continue;
                }

                data.slots.Add(exp);
                added++;
            }

            return added;
        }

        static List<FeatureArtSlot> BuildExpectedSlots()
        {
            var list = new List<FeatureArtSlot>();
            AddOrganSlots(list);
            AddShapeSlots(list);
            AddSummonSlots(list);
            AddPlayerSlots(list);
            AddEnemySlots(list);
            return list;
        }

        static void AddOrganSlots(List<FeatureArtSlot> list)
        {
            foreach (var kv in OrganelleCatalog.All)
            {
                var def = kv.Value;
                if (def.IsRetired || def.ArtId == null)
                {
                    continue;
                }

                OrganArt.TryGetValue(def.Id, out var art);

                list.Add(new FeatureArtSlot
                {
                    id = $"organ.{def.Id}.mesh",
                    domain = "organ",
                    key = def.Id,
                    role = "",
                    bindKind = "InstancedMesh",
                    titleZh = $"{def.DisplayName} · 本体网格",
                    purpose = "装备/激活该器官时，玩家（或载体）外形换成这套网格。",
                    howTo = $"Prefab 放到 Raw/Actor/Organ/，文件名 organ_{def.Id}（保证 location 唯一）；一个 MeshFilter；不要粒子、不要相机、不要 AudioListener。",
                    expected = "沙盒激活该器官后，本体剪影与其它器官可辨；没拖资源时仍是现在的白模，游戏能跑。",
                    constraints = "禁粒子/相机；材质须 GPU Instancing。",
                    look = art.Look ?? "",
                    prompt = art.Body != null ? GeoPrompt(art.Body) : "",
                    folderHint = "Assets/GameRes/Raw/Actor/Organ",
                    location = "",
                    package = "",
                    retired = false,
                });
            }
        }

        static void AddShapeSlots(List<FeatureArtSlot> list)
        {
            foreach (var shape in FxRecipeCatalog.ShapeKeys)
            {
                ShapeLanguage.TryGetValue(shape, out var language);
                foreach (var role in ShapeRoles)
                {
                    ShapeRoleBody.TryGetValue(role, out var roleBody);
                    var body = (language ?? "") + (roleBody ?? "");

                    list.Add(new FeatureArtSlot
                    {
                        id = $"shape.{shape}.{role}",
                        domain = "shape",
                        key = shape,
                        role = role,
                        bindKind = "PooledPrefab",
                        titleZh = $"{ShapeZh(shape)} · {RoleZh(role)}",
                        purpose = PurposeFor(shape, role),
                        howTo = HowToFor(shape, role),
                        expected = "绑定后现网对应 Shape 攻击改用该 Prefab（池化）；空槽维持现有白模挤出表现。",
                        constraints = "固定容量对象池，禁止每发裸 Instantiate 不回收；Prefab 不得自行 Destroy 破坏池。",
                        look = language ?? "",
                        prompt = language != null && roleBody != null ? FxPrompt(body) : "",
                        folderHint = $"Assets/GameRes/Raw/Effects/{FolderFor(role)}",
                        location = "",
                        package = "",
                        retired = false,
                    });
                }
            }
        }

        static void AddSummonSlots(List<FeatureArtSlot> list)
        {
            foreach (var key in SummonKeys)
            {
                string anchorNote = key == "mycelium"
                    ? "菌丝体贴地，网格应贴 XZ，不要悬空根。"
                    : "轴心居中。";

                SummonArt.TryGetValue(key, out var art);

                list.Add(new FeatureArtSlot
                {
                    id = $"summon.{key}.mesh",
                    domain = "summon",
                    key = key,
                    role = "",
                    bindKind = "InstancedMesh",
                    titleZh = $"召唤 · {key}",
                    purpose = $"{key} 召唤机制生成的实体外形。",
                    howTo = $"Prefab 放到 Raw/Actor/Summon/，文件名 summon_{key}；单 MeshFilter；{anchorNote}",
                    expected = "触发该召唤后场上实体换为该网格；空槽则维持现有白模。",
                    constraints = "面数按实例化预算控制，走 InstancedMesh，不得逐实例 Instantiate。",
                    look = art.Look ?? "",
                    prompt = art.Body != null ? GeoPrompt(art.Body) : "",
                    folderHint = "Assets/GameRes/Raw/Actor/Summon",
                    location = "",
                    package = "",
                    retired = false,
                });
            }
        }

        static void AddPlayerSlots(List<FeatureArtSlot> list)
        {
            list.Add(new FeatureArtSlot
            {
                id = "player.chassis.mesh",
                domain = "player",
                key = "chassis",
                role = "",
                bindKind = "InstancedMesh",
                titleZh = "玩家本体 · 网格",
                purpose = "玩家默认本体外形（未装备特殊器官时的基础网格）。",
                howTo = "Prefab 放到 Raw/Actor/Player/，文件名 player_chassis；单 MeshFilter；轴心在体积中心；朝向 +X 为口器/瞄准前方（与现网弹道 +X 锥一致）。",
                expected = "沙盒进关后玩家本体换为该网格；空槽则维持现有白模胶囊。",
                constraints = "面数按实例化预算控制，禁复杂骨骼作为万敌路径。",
                look = PlayerChassisLook,
                prompt = GeoPrompt(PlayerChassisBody),
                folderHint = "Assets/GameRes/Raw/Actor/Player",
                location = "",
                package = "",
                retired = false,
            });
            list.Add(new FeatureArtSlot
            {
                id = "player.chassis.material",
                domain = "player",
                key = "chassis",
                role = "",
                bindKind = "MaterialOverride",
                titleZh = "玩家本体 · 材质",
                purpose = "玩家本体可选换材质（不换网格）。",
                howTo = "Material 资源放 Raw/Actor/Player/ 或复用现有材质；必须勾选 GPU Instancing，建议继续 BinGames/SimBioGlass 着色器。",
                expected = "换材质后玩家外观 Look 改变，网格不变；空槽维持现有材质。",
                constraints = "必须支持 GPU Instancing，否则批处理失败。",
                look = PlayerChassisMaterialLook,
                prompt = "",
                folderHint = "Assets/GameRes/Raw/Actor/Player",
                location = "",
                package = "",
                retired = false,
            });
        }

        /// <summary>story-005 R10：enemy 域，16 条敌人 VisualId 族——复用
        /// <see cref="FeatureArtVisualBinder.EnemyVisualFamilies"/> 同一份数据源，不在 Editor 侧另抄一份表。
        /// 多个 Luban TbCellEnemy 行共享同一个 VisualId，槽位按这 16 个建，不按 Luban EnemyId 建。</summary>
        static void AddEnemySlots(List<FeatureArtSlot> list)
        {
            foreach (var family in FeatureArtVisualBinder.EnemyVisualFamilies)
            {
                EnemyArt.TryGetValue(family.Key, out var art);

                list.Add(new FeatureArtSlot
                {
                    id = $"enemy.{family.Key}.mesh",
                    domain = "enemy",
                    key = family.Key,
                    role = "",
                    bindKind = "InstancedMesh",
                    titleZh = $"敌人 · {family.TitleZh}",
                    purpose = "该 VisualId 族敌人的外形；多个具体敌人共享同一视觉族，不必每个敌人单独建模。",
                    howTo = $"Prefab 放到 Raw/Actor/Enemy/，文件名 enemy_{family.Key}；单 MeshFilter；可换色/缩放复用，精英/首领已有独立 ScaleMul，不必再做超大模型。",
                    expected = "该族任意敌人生成后外形换为该网格；空槽维持现有白模（球体+颜色区分）。",
                    constraints = "面数按万敌实例化预算控制；禁止逐敌人 Instantiate。",
                    look = art.Look ?? "",
                    prompt = art.Body != null ? GeoPrompt(art.Body) : "",
                    folderHint = "Assets/GameRes/Raw/Actor/Enemy",
                    location = "",
                    package = "",
                    retired = false,
                });
            }
        }

        static string RoleZh(string role)
        {
            switch (role)
            {
                case "projectile": return "弹道/持续体";
                case "muzzle": return "开火瞬间";
                case "hit": return "命中";
                case "explode": return "爆炸/落点";
                case "indicator": return "瞄准预览";
                default: return role;
            }
        }

        static string FolderFor(string role)
        {
            switch (role)
            {
                case "projectile": return "Projectile";
                case "muzzle": return "Muzzle";
                case "hit": return "Hit";
                case "explode": return "Explode";
                case "indicator": return "Indicator";
                default: return role;
            }
        }

        static string PurposeFor(string shape, string role)
        {
            switch (role)
            {
                case "projectile": return $"飞行/持续体，{shape} 攻击的弹道或光束表现。";
                case "muzzle": return $"{shape} 开火瞬间（枪口/挥击起手）表现。";
                case "hit": return $"{shape} 命中目标时的表现。";
                case "explode": return $"{shape} 落点/爆炸圈表现。";
                case "indicator": return $"{shape} 未开火时的装配预览（瞄准指示器）表现。";
                default: return "";
            }
        }

        static string HowToFor(string shape, string role)
        {
            switch (role)
            {
                case "projectile":
                    return $"Prefab 放到 Raw/Effects/Projectile/，文件名 shape_{shape}_projectile；朝向 +X 向前；寿命由现网 Flight/Persistent 驱动，Prefab 不要自己 Destroy 断池。";
                case "muzzle":
                    return $"Prefab 放到 Raw/Effects/Muzzle/，文件名 shape_{shape}_muzzle；短生命周期；挂在玩家前方。";
                case "hit":
                    return $"Prefab 放到 Raw/Effects/Hit/，文件名 shape_{shape}_hit；打在结算点，不是玩家脚下。";
                case "explode":
                    return $"Prefab 放到 Raw/Effects/Explode/，文件名 shape_{shape}_explode；可与 hit 共用美术，但槽位分开方便换。";
                case "indicator":
                    return $"Prefab 放到 Raw/Effects/Indicator/，文件名 shape_{shape}_indicator；半透明材质，静态展示，只同步位置/朝向不同步缩放；未绑定回退白模半透明片。";
                default:
                    return "";
            }
        }
    }
}
