using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>AC-THEME-002（DEBT-ER4CONTENT01-14 占位期）：玩家、静默、铸造三方在灰度和小尺寸下靠轮廓区分。
    /// 此前三方全是同一个胶囊体，只靠颜色和头顶标记区分。正式低模到位前，用几个无碰撞体的基本体拼出三类剪影：
    /// - 玩家机器：矮宽的车辆轮廓（车体 + 驾驶舱），底盘再细分——轮式四轮、履带两条履带加炮管、悬浮底部裙盘；
    /// - 静默阵营：细高的“天线”轮廓（底座小盘 + 细杆），侦察机顶部圆球、干扰机顶部圆环；
    /// - 铸造阵营：方正厚重的块体轮廓，步进炮加腿与前伸炮管、厚甲机前挡板、维修机顶部十字；供能节点是粗圆柱塔、
    ///   主核心是圆形反应堆塔（粗基座 + 顶部球体）。
    ///
    /// 原胶囊保留碰撞体（点选、命中射线、接管都不变），只隐藏它自己的网格；部件共用胶囊的材质实例，
    /// 死亡变灰、选中高亮等既有改色逻辑自动作用到整个剪影。部件全部低于头顶标记（世界高度约 2.3 以下）。</summary>
    public static class PlaceholderSilhouette
    {
        public enum Faction
        {
            Player,
            Silent,
            Foundry,
        }

        /// <summary>剪影部件的父节点名（自检按它取部件）。</summary>
        public const string PartsName = "Silhouette";

        /// <summary>FG9-DATA-01：阵营来自敌人表 faction（静默 → 天线轮廓；其它阵营暂用铸造的方正轮廓，正式剪影在美术阶段替换）。</summary>
        public static Faction FactionOfEnemy(string enemyTypeId) =>
            EnemyDefs.TryGet(enemyTypeId, out GameConfig.fg.MechEnemy row) && row.Faction == EnemyDefs.FactionSilent ? Faction.Silent : Faction.Foundry;

        private static string SilhouetteOf(string enemyTypeId) =>
            EnemyDefs.TryGet(enemyTypeId, out GameConfig.fg.MechEnemy row) ? row.Silhouette : null;

        /// <summary>给玩家机器挂剪影。<paramref name="hitbox"/> 是原来的胶囊（中心在其自身原点、高 2、半径 0.5）。</summary>
        public static void ApplyMachine(GameObject hitbox, Renderer hitboxRenderer, string machineChassisId)
        {
            Transform parts = Begin(hitbox, hitboxRenderer);
            Material mat = hitboxRenderer.sharedMaterial;
            Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.62f, 0f), new Vector3(1.5f, 0.5f, 1.8f));
            Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.2f, -0.3f), new Vector3(0.8f, 0.4f, 0.7f));
            string archetype = ChassisCatalog.ResolveArchetype(machineChassisId);
            if (archetype == ChassisCatalog.ChassisTrackId)
            {
                Part(parts, mat, PrimitiveType.Cube, new Vector3(-0.88f, -0.76f, 0f), new Vector3(0.32f, 0.42f, 1.95f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0.88f, -0.76f, 0f), new Vector3(0.32f, 0.42f, 1.95f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.18f, 0.55f), new Vector3(0.14f, 0.14f, 0.9f));
            }
            else if (archetype == ChassisCatalog.ChassisHoverId)
            {
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.93f, 0f), new Vector3(1.7f, 0.04f, 1.7f));
            }
            else
            {
                foreach (float x in new[] { -0.8f, 0.8f })
                {
                    foreach (float z in new[] { -0.55f, 0.55f })
                    {
                        Part(parts, mat, PrimitiveType.Cylinder, new Vector3(x, -0.75f, z), new Vector3(0.5f, 0.1f, 0.5f), new Vector3(0f, 0f, 90f));
                    }
                }
            }
        }

        /// <summary>给敌人挂剪影（FG9-DATA-01：阵营与剪影键来自敌人表 faction / silhouette，不按敌人 ID）。</summary>
        public static void ApplyEnemy(GameObject hitbox, Renderer hitboxRenderer, string enemyTypeId)
        {
            Transform parts = Begin(hitbox, hitboxRenderer);
            Material mat = hitboxRenderer.sharedMaterial;
            string silhouette = SilhouetteOf(enemyTypeId);
            if (FactionOfEnemy(enemyTypeId) == Faction.Silent)
            {
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.9f, 0f), new Vector3(0.55f, 0.03f, 0.55f));
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.05f, 0f), new Vector3(0.16f, 0.75f, 0.16f));
                if (silhouette == "silent_ring")
                {
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.55f, 0f), new Vector3(0.95f, 0.03f, 0.95f));
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.85f, 0f), Vector3.one * 0.3f);
                }
                // FG9-FAC-01 静默扩充（低矮、吸波、天线密集；B15 形状区分，占位几何体）
                else if (silhouette == "silent_node")
                {
                    // 监听节点：低矮方座 + 三根天线
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.6f, 0f), new Vector3(1.4f, 0.5f, 1.4f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(-0.4f, 0.3f, 0f), new Vector3(0.08f, 0.9f, 0.08f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0.4f, 0.3f, 0f), new Vector3(0.08f, 0.9f, 0.08f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.5f, 0.4f), new Vector3(0.08f, 1.1f, 0.08f));
                }
                else if (silhouette == "silent_prism")
                {
                    // 磁暴棱柱：竖立的菱形棱柱
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.2f, 0f), new Vector3(0.6f, 1.2f, 0.6f), new Vector3(0f, 45f, 0f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.95f, 0f), new Vector3(0.9f, 0.03f, 0.9f));
                }
                else if (silhouette == "silent_hunter")
                {
                    // 回声猎手：贴地的扁平楔形 + 一根收起的短桅
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.7f, 0.1f), new Vector3(0.9f, 0.25f, 1.4f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.3f, -0.4f), new Vector3(0.06f, 0.35f, 0.06f));
                }
                else if (silhouette == "silent_decoy" || silhouette == "silent_relay")
                {
                    // 诱饵中继 / 中继节点：细高中继塔 + 顶部环
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.4f, 0f), new Vector3(0.12f, 1.2f, 0.12f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 1.5f, 0f), new Vector3(0.7f, 0.03f, 0.7f));
                    if (silhouette == "silent_relay")
                    {
                        Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.6f, 0f), new Vector3(1.6f, 0.4f, 1.6f));
                    }
                }
                else if (silhouette == "silent_listener")
                {
                    // 寂听者：高瘦主体 + 前伸的断链束发射管
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.85f, 0f), Vector3.one * 0.55f);
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.85f, 0.6f), new Vector3(0.1f, 0.5f, 0.1f), new Vector3(90f, 0f, 0f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 1.4f, 0f), new Vector3(1.2f, 0.03f, 1.2f));
                }
                else if (silhouette == "silent_mind")
                {
                    // 寂听主脑：宽大低平的碟形主体 + 密集天线环
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.5f, 0f), new Vector3(3.2f, 0.4f, 3.2f));
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.2f, 0f), new Vector3(2.2f, 0.9f, 2.2f));
                    for (int k = 0; k < 6; k++)
                    {
                        float a = k * Mathf.PI / 3f;
                        Part(parts, mat, PrimitiveType.Cylinder, new Vector3(Mathf.Cos(a) * 1.4f, 0.9f, Mathf.Sin(a) * 1.4f), new Vector3(0.07f, 0.8f, 0.07f));
                    }
                }
                else
                {
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.85f, 0f), Vector3.one * 0.5f);
                }
                return;
            }

            // FG10-FAC-01 澄净（管线、罐体、喷洒；B15 形状区分，占位几何体，美术阶段替换 DEBT-FG10FAC01-01）。
            if (silhouette != null && silhouette.StartsWith("clarity_", System.StringComparison.Ordinal))
            {
                ApplyClarity(parts, mat, silhouette);
                return;
            }
            if (silhouette == "foundry_node")
            {
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.1f, 0f), new Vector3(1.1f, 0.9f, 1.1f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 1.0f, 0f), new Vector3(0.5f, 0.4f, 0.5f));
                return;
            }
            if (silhouette == "foundry_reactor")
            {
                // 主核心：圆形反应堆塔（粗圆柱基座 + 顶部球体），俯视是圆，与方正的车辆/铸造机都不同。
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.4f, 0f), new Vector3(1.7f, 0.6f, 1.7f));
                Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.55f, 0f), Vector3.one * 0.9f);
                return;
            }
            if (silhouette == "foundry_strider")
            {
                // 步进炮：两条腿把机身架高，前伸长炮管。
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f), new Vector3(1.2f, 0.7f, 1.0f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(-0.45f, -0.68f, 0f), new Vector3(0.2f, 0.6f, 0.2f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0.45f, -0.68f, 0f), new Vector3(0.2f, 0.6f, 0.2f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0f, 0.85f), new Vector3(0.22f, 0.22f, 1.3f));
                return;
            }
            // ── FG9-FAC-02 铸造扩充（厚重、履带、炮塔；B15 形状区分，占位几何体，美术阶段替换 DEBT-FG9FAC02-01）──
            if (silhouette == "foundry_aa")
            {
                // 对空高炮：低矮炮座 + 两根竖直朝天的长炮管（伏笔“平时炮口朝天”）。
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.65f, 0f), new Vector3(1.6f, 0.35f, 1.6f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.15f, 0f), new Vector3(0.9f, 0.6f, 0.9f));
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(-0.22f, 1.1f, 0f), new Vector3(0.14f, 1.1f, 0.14f));
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0.22f, 1.1f, 0f), new Vector3(0.14f, 1.1f, 0.14f));
                return;
            }
            if (silhouette == "foundry_airburst")
            {
                // 空爆炮台：方形炮塔 + 四联装短管（斜指前上方）。
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.5f, 0f), new Vector3(1.5f, 0.6f, 1.5f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.05f, 0f), new Vector3(1.0f, 0.5f, 1.0f));
                for (int k = 0; k < 4; k++)
                {
                    float x = (k % 2 == 0 ? -0.18f : 0.18f);
                    float y = (k < 2 ? 0.2f : 0.42f);
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(x, y, 0.55f), new Vector3(0.1f, 0.4f, 0.1f), new Vector3(60f, 0f, 0f));
                }
                return;
            }
            if (silhouette == "foundry_minelayer")
            {
                // 布雷车：低矮履带车身 + 尾部斜置的布雷滑槽。
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.55f, 0f), new Vector3(1.3f, 0.5f, 1.6f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(-0.7f, -0.7f, 0f), new Vector3(0.18f, 0.3f, 1.7f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0.7f, -0.7f, 0f), new Vector3(0.18f, 0.3f, 1.7f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.2f, -0.9f), new Vector3(0.5f, 0.12f, 0.7f), new Vector3(-25f, 0f, 0f));
                return;
            }
            if (silhouette == "foundry_loader")
            {
                // 装填机：小车身 + 背上一排弹筒。
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.6f, 0f), new Vector3(1.0f, 0.45f, 1.2f));
                for (int k = -1; k <= 1; k++)
                {
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(k * 0.28f, -0.1f, 0f), new Vector3(0.22f, 0.3f, 0.22f));
                }
                return;
            }
            if (silhouette == "foundry_siege_heavy")
            {
                // 攻城重甲：巨型摇臂履带机（四条摇臂腿，末端各一个短履带舱；灰模 02-3）+ 正面厚装甲板 + 背部散热口。
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.2f, 0f), new Vector3(2.4f, 1.2f, 2.6f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.2f, 1.4f), new Vector3(2.7f, 1.5f, 0.25f));
                for (int k = 0; k < 4; k++)
                {
                    float x = k % 2 == 0 ? -1.5f : 1.5f;
                    float z = k < 2 ? 1.0f : -1.0f;
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(x, -0.4f, z), new Vector3(0.3f, 0.9f, 0.3f), new Vector3(0f, 0f, x < 0f ? 20f : -20f));
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(x * 1.15f, -0.85f, z), new Vector3(0.5f, 0.35f, 0.9f));
                }
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.55f, -1.35f), new Vector3(1.4f, 0.5f, 0.1f)); // 背部散热口
                return;
            }
            // 铸造通用：方正高块体（比玩家车辆高一截、窄一圈）。
            Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.33f, 0f), new Vector3(1.2f, 1.3f, 1.1f));
            if (silhouette == "foundry_shield")
            {
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.35f, 0.62f), new Vector3(1.5f, 1.2f, 0.16f));
            }
            else if (silhouette == "foundry_cross")
            {
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.4f, 0f), new Vector3(0.8f, 0.12f, 0.22f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.4f, 0f), new Vector3(0.22f, 0.12f, 0.8f));
            }
        }

        /// <summary>澄净剪影：横卧 / 竖立的罐体 + 管线 + 喷嘴（俯视圆罐，与铸造的方块、静默的细杆都不同）。</summary>
        private static void ApplyClarity(Transform parts, Material mat, string silhouette)
        {
            switch (silhouette)
            {
                case "clarity_sprayer":
                    // 喷洒车：低底盘 + 横卧罐体 + 前伸喷杆。
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.7f, 0f), new Vector3(1.3f, 0.35f, 1.8f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.15f, -0.15f), new Vector3(0.9f, 0.7f, 0.9f), new Vector3(90f, 0f, 0f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.2f, 1.05f), new Vector3(0.12f, 0.35f, 0.12f), new Vector3(70f, 0f, 0f));
                    break;
                case "clarity_pump":
                    // 液氮泵车：底盘 + 两只竖罐 + 前伸喷嘴。
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.7f, 0f), new Vector3(1.4f, 0.35f, 1.8f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(-0.35f, 0.05f, -0.3f), new Vector3(0.55f, 0.6f, 0.55f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0.35f, 0.05f, -0.3f), new Vector3(0.55f, 0.6f, 0.55f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.25f, 1.0f), new Vector3(0.16f, 0.35f, 0.16f), new Vector3(80f, 0f, 0f));
                    break;
                case "clarity_crawler":
                    // 油污爬行者：贴地扁圆罐 + 尾部漏油管。
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, -0.72f, 0f), new Vector3(1.1f, 0.45f, 1.3f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.85f, -0.75f), new Vector3(0.12f, 0.25f, 0.12f), new Vector3(90f, 0f, 0f));
                    break;
                case "clarity_drone":
                    // 灭活无人机：悬在高处的小罐体 + 两片旋翼盘（空中单位，比地面单位高一截）。
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.9f, 0f), new Vector3(0.45f, 0.25f, 0.45f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(-0.55f, 1.05f, 0f), new Vector3(0.6f, 0.02f, 0.6f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0.55f, 1.05f, 0f), new Vector3(0.6f, 0.02f, 0.6f));
                    break;
                case "clarity_swarm":
                    // 腐蚀蜂群：三只小球挤在一起（一群小东西）。
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(-0.3f, -0.75f, 0f), Vector3.one * 0.4f);
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0.3f, -0.75f, 0.1f), Vector3.one * 0.35f);
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, -0.7f, -0.3f), Vector3.one * 0.38f);
                    break;
                case "clarity_tank":
                    // 沉淀巨罐：粗大竖罐 + 顶部枢轴 + 一条长喷淋臂。
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0f), new Vector3(2.6f, 1.0f, 2.6f));
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 1.1f, 0f), new Vector3(0.5f, 0.15f, 0.5f));
                    Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 1.15f, 1.8f), new Vector3(0.18f, 0.12f, 3.2f));
                    break;
                default:
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.3f, 0f), new Vector3(1.1f, 0.6f, 1.1f));
                    break;
            }
        }

        private static Transform Begin(GameObject hitbox, Renderer hitboxRenderer)
        {
            Transform existing = hitbox.transform.Find(PartsName);
            if (existing != null)
            {
                Release(existing.gameObject);
            }
            var root = new GameObject(PartsName);
            root.transform.SetParent(hitbox.transform, false);
            hitboxRenderer.enabled = false; // 胶囊只当命中盒；碰撞体保留。
            return root.transform;
        }

        private static void Part(Transform parent, Material mat, PrimitiveType type, Vector3 localPosition, Vector3 localScale,
            Vector3 localEuler = default)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = type.ToString();
            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                Release(collider); // 部件不挡点选射线：命中只认原胶囊。
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void Release(Object obj)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(obj);
            }
            else
            {
                Object.DestroyImmediate(obj);
            }
        }
    }
}
