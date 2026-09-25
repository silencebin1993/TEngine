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

        public static Faction FactionOfEnemy(string enemyTypeId) =>
            enemyTypeId == EnemyCatalog.ScoutId || enemyTypeId == EnemyCatalog.JammerId ? Faction.Silent : Faction.Foundry;

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

        /// <summary>给敌人挂剪影（阵营由敌人类型决定）。</summary>
        public static void ApplyEnemy(GameObject hitbox, Renderer hitboxRenderer, string enemyTypeId)
        {
            Transform parts = Begin(hitbox, hitboxRenderer);
            Material mat = hitboxRenderer.sharedMaterial;
            if (FactionOfEnemy(enemyTypeId) == Faction.Silent)
            {
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.9f, 0f), new Vector3(0.55f, 0.03f, 0.55f));
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.05f, 0f), new Vector3(0.16f, 0.75f, 0.16f));
                if (enemyTypeId == EnemyCatalog.JammerId)
                {
                    Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, 0.55f, 0f), new Vector3(0.95f, 0.03f, 0.95f));
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.85f, 0f), Vector3.one * 0.3f);
                }
                else
                {
                    Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.85f, 0f), Vector3.one * 0.5f);
                }
                return;
            }

            if (enemyTypeId == FoundryOutpostLayout.BossNodeTypeId)
            {
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.1f, 0f), new Vector3(1.1f, 0.9f, 1.1f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 1.0f, 0f), new Vector3(0.5f, 0.4f, 0.5f));
                return;
            }
            if (enemyTypeId == FoundryOutpostLayout.BossCoreTypeId)
            {
                // 主核心：圆形反应堆塔（粗圆柱基座 + 顶部球体），俯视是圆，与方正的车辆/铸造机都不同。
                Part(parts, mat, PrimitiveType.Cylinder, new Vector3(0f, -0.4f, 0f), new Vector3(1.7f, 0.6f, 1.7f));
                Part(parts, mat, PrimitiveType.Sphere, new Vector3(0f, 0.55f, 0f), Vector3.one * 0.9f);
                return;
            }
            if (enemyTypeId == EnemyCatalog.StriderId)
            {
                // 步进炮：两条腿把机身架高，前伸长炮管。
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.05f, 0f), new Vector3(1.2f, 0.7f, 1.0f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(-0.45f, -0.68f, 0f), new Vector3(0.2f, 0.6f, 0.2f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0.45f, -0.68f, 0f), new Vector3(0.2f, 0.6f, 0.2f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0f, 0.85f), new Vector3(0.22f, 0.22f, 1.3f));
                return;
            }
            // 铸造通用：方正高块体（比玩家车辆高一截、窄一圈）。
            Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.33f, 0f), new Vector3(1.2f, 1.3f, 1.1f));
            if (enemyTypeId == EnemyCatalog.ArmorBotId)
            {
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, -0.35f, 0.62f), new Vector3(1.5f, 1.2f, 0.16f));
            }
            else if (enemyTypeId == EnemyCatalog.RepairBotId)
            {
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.4f, 0f), new Vector3(0.8f, 0.12f, 0.22f));
                Part(parts, mat, PrimitiveType.Cube, new Vector3(0f, 0.4f, 0f), new Vector3(0.22f, 0.12f, 0.8f));
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
