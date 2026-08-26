using System.Collections.Generic;
using System.Linq;
using GameLogic.ArtBinding;
using GameLogic.MetabolicSlice.ContentCatalog;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>story-003 R8/R9：从代码扫 organ/shape/summon/player 四域生成期望槽位，
    /// 与已有 catalog 合并——按 id 匹配，只增槽、标 retired，绝不清空/覆盖已有 location，
    /// Brief 字段也不覆盖已存在的槽（防止吞掉人工修订）。enemy 域本批不做（R8，避免臆造槽位）。</summary>
    public static class FeatureArtSlotSync
    {
        static readonly string[] SummonKeys = { "spore", "phage", "mycelium" };
        static readonly string[] ShapeRoles = { "projectile", "muzzle", "hit", "explode" };
        static readonly HashSet<string> SyncedDomains = new HashSet<string> { "organ", "shape", "summon", "player", "enemy" };

        /// <summary>返回新增槽数量；已存在槽的 retired 标记就地更新。</summary>
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
                foreach (var role in ShapeRoles)
                {
                    list.Add(new FeatureArtSlot
                    {
                        id = $"shape.{shape}.{role}",
                        domain = "shape",
                        key = shape,
                        role = role,
                        bindKind = "PooledPrefab",
                        titleZh = $"{shape} · {RoleZh(role)}",
                        purpose = PurposeFor(shape, role),
                        howTo = HowToFor(shape, role),
                        expected = "绑定后现网对应 Shape 攻击改用该 Prefab（池化）；空槽维持现有白模挤出表现。",
                        constraints = "固定容量对象池，禁止每发裸 Instantiate 不回收；Prefab 不得自行 Destroy 破坏池。",
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
                default:
                    return "";
            }
        }
    }
}
