using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// AC-THEME-002（DEBT-ER4CONTENT01-14 占位期）：玩家、静默、铸造三方在灰度和小尺寸下靠轮廓可区分。
    /// batchmode 是 -nographics，不能渲染截图；这里把每个剪影部件的包围盒栅格化成 16×16 的侧视/正视/俯视
    /// 三张“缩略剪影”（无颜色＝灰度），断言跨阵营两两重合度低，并逐项核对：胶囊只留碰撞体、部件无碰撞体、
    /// 部件共用胶囊材质（改色逻辑作用到整个剪影）、部件低于头顶标记、不沉到地下。并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class SilhouetteSelfCheck
    {
        private const int Grid = 16;
        private const float Extent = 1.25f;          // 栅格覆盖胶囊局部坐标 [-1.25, 1.25]
        private const float MaxCrossFactionIoU = 0.7f;
        private const float BadgeClearanceTop = 1.35f; // 胶囊中心在世界 y=1，头顶标记最低约 2.35

        private static StringBuilder _report;
        private static int _fail;

        private sealed class Case
        {
            public string Label;
            public PlaceholderSilhouette.Faction Faction;
            public Action<GameObject, Renderer> Apply;
            public bool[] Mask;
            public Bounds Bounds;
        }

        [MenuItem("BinGames/自检：敌我剪影")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[剪影] 玩家/静默/铸造三方轮廓（ER8 收尾 AC-THEME-002）");
            var cases = new List<Case>
            {
                Machine("轮式机", HomeValleyLayout.Erc001ChassisId),
                Machine("履带战斗机", HomeValleyLayout.Erc003ChassisId),
                Machine("悬浮机", ChassisCatalog.ChassisHoverId),
                Enemy("静默侦察机", EnemyCatalog.ScoutId),
                Enemy("静默干扰机", EnemyCatalog.JammerId),
                Enemy("步进炮", EnemyCatalog.StriderId),
                Enemy("厚甲机", EnemyCatalog.ArmorBotId),
                Enemy("维修机", EnemyCatalog.RepairBotId),
                Enemy("供能节点", FoundryOutpostLayout.BossNodeTypeId),
                Enemy("主核心", FoundryOutpostLayout.BossCoreTypeId),
            };
            var created = new List<GameObject>();
            try
            {
                var structural = new List<string>();
                foreach (Case c in cases)
                {
                    GameObject hitbox = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    hitbox.hideFlags = HideFlags.HideAndDontSave;
                    created.Add(hitbox);
                    Renderer renderer = hitbox.GetComponent<Renderer>();
                    c.Apply(hitbox, renderer);
                    string problem = Inspect(c, hitbox, renderer);
                    if (problem != null)
                    {
                        structural.Add($"{c.Label}：{problem}");
                    }
                }
                Expect(structural.Count == 0, "10 类剪影：胶囊只留碰撞体、部件无碰撞体且共用胶囊材质、低于头顶标记、不沉到地下" +
                                              (structural.Count == 0 ? string.Empty : "——" + string.Join("；", structural)));

                var similar = new List<string>();
                float worst = 0f;
                string worstPair = string.Empty;
                for (int i = 0; i < cases.Count; i++)
                {
                    for (int j = i + 1; j < cases.Count; j++)
                    {
                        if (cases[i].Faction == cases[j].Faction)
                        {
                            continue;
                        }
                        float iou = IoU(cases[i].Mask, cases[j].Mask);
                        if (iou > worst)
                        {
                            worst = iou;
                            worstPair = $"{cases[i].Label}/{cases[j].Label}";
                        }
                        if (iou >= MaxCrossFactionIoU)
                        {
                            similar.Add($"{cases[i].Label}/{cases[j].Label} {iou:F2}");
                        }
                    }
                }
                Expect(similar.Count == 0, $"跨阵营任意两类的缩略剪影重合度都低于 {MaxCrossFactionIoU:F1}（最高 {worst:F2}，{worstPair}）" +
                                           (similar.Count == 0 ? string.Empty : "——过于相似：" + string.Join("；", similar)));

                // 三方各自的轮廓类别：玩家矮宽、静默细高、铸造方正高块。
                var wrongClass = new List<string>();
                foreach (Case c in cases)
                {
                    float h = c.Bounds.size.y;
                    float w = Mathf.Max(c.Bounds.size.x, c.Bounds.size.z);
                    float mean = MeanSideWidth(c);
                    bool ok = c.Faction == PlaceholderSilhouette.Faction.Player ? h <= 1.1f && w >= 1.5f
                        : c.Faction == PlaceholderSilhouette.Faction.Silent ? h >= 1.8f && mean <= 0.55f
                        : h >= 1.2f && mean >= 0.8f;
                    if (!ok)
                    {
                        wrongClass.Add($"{c.Label}(高 {h:F2} 宽 {w:F2} 平均侧宽 {mean:F2})");
                    }
                }
                Expect(wrongClass.Count == 0, "轮廓类别：玩家机器矮而宽、静默细高（竖杆为主）、铸造方正厚重" +
                                              (wrongClass.Count == 0 ? string.Empty : "——不符：" + string.Join("；", wrongClass)));
            }
            catch (Exception e)
            {
                Fail($"剪影自检抛异常：{e}");
            }
            finally
            {
                foreach (GameObject go in created)
                {
                    if (go != null)
                    {
                        Object.DestroyImmediate(go);
                    }
                }
            }
            return _fail;
        }

        private static Case Machine(string label, string chassisId) => new Case
        {
            Label = label,
            Faction = PlaceholderSilhouette.Faction.Player,
            Apply = (go, r) => PlaceholderSilhouette.ApplyMachine(go, r, chassisId),
        };

        private static Case Enemy(string label, string enemyTypeId) => new Case
        {
            Label = label,
            Faction = PlaceholderSilhouette.FactionOfEnemy(enemyTypeId),
            Apply = (go, r) => PlaceholderSilhouette.ApplyEnemy(go, r, enemyTypeId),
        };

        /// <summary>核对结构并生成缩略剪影；有问题返回描述，否则 null。</summary>
        private static string Inspect(Case c, GameObject hitbox, Renderer hitboxRenderer)
        {
            if (hitboxRenderer.enabled)
            {
                return "胶囊网格没有隐藏";
            }
            Collider hitCollider = hitbox.GetComponent<Collider>();
            if (hitCollider == null || !hitCollider.enabled)
            {
                return "胶囊碰撞体丢失（点选/命中会失效）";
            }
            Transform parts = hitbox.transform.Find(PlaceholderSilhouette.PartsName);
            MeshFilter[] filters = parts != null ? parts.GetComponentsInChildren<MeshFilter>(true) : new MeshFilter[0];
            if (filters.Length == 0)
            {
                return "没有剪影部件";
            }
            if (parts.GetComponentsInChildren<Collider>(true).Length > 0)
            {
                return "部件带碰撞体（会挡点选射线）";
            }
            if (parts.GetComponentsInChildren<Renderer>(true).Any(r => r.sharedMaterial != hitboxRenderer.sharedMaterial))
            {
                return "部件没有共用胶囊材质（死亡变灰/选中高亮不会作用到剪影）";
            }

            c.Mask = new bool[Grid * Grid * 3];
            bool first = true;
            foreach (MeshFilter f in filters)
            {
                Bounds b = LocalBounds(f, hitbox.transform);
                if (first)
                {
                    c.Bounds = b;
                    first = false;
                }
                else
                {
                    c.Bounds.Encapsulate(b);
                }
                // 圆形部件按椭圆栅格化：球体三个视图都是圆，竖直圆柱俯视是圆；其余（方块、横放的轮子）按矩形。
                string mesh = f.sharedMesh.name;
                bool upright = Quaternion.Angle(f.transform.rotation, hitbox.transform.rotation) < 1f;
                bool sphere = mesh.StartsWith("Sphere", StringComparison.Ordinal);
                bool cylinder = upright && mesh.StartsWith("Cylinder", StringComparison.Ordinal);
                Rasterize(c.Mask, 0, b.min.x, b.max.x, b.min.y, b.max.y, sphere); // 侧视 X-Y
                Rasterize(c.Mask, 1, b.min.z, b.max.z, b.min.y, b.max.y, sphere); // 正视 Z-Y
                Rasterize(c.Mask, 2, b.min.x, b.max.x, b.min.z, b.max.z, sphere || cylinder); // 俯视 X-Z
            }
            if (c.Bounds.max.y > BadgeClearanceTop)
            {
                return $"部件高出头顶标记（顶 {c.Bounds.max.y:F2}）";
            }
            if (c.Bounds.min.y < -1.05f)
            {
                return $"部件沉到地面以下（底 {c.Bounds.min.y:F2}）";
            }
            return null;
        }

        /// <summary>部件网格包围盒的 8 个角换到胶囊局部坐标后取轴对齐包围盒（不依赖渲染，-nographics 可用）。</summary>
        private static Bounds LocalBounds(MeshFilter filter, Transform hitbox)
        {
            Bounds mesh = filter.sharedMesh.bounds;
            Matrix4x4 toLocal = hitbox.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var result = new Bounds(toLocal.MultiplyPoint3x4(mesh.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = mesh.center + Vector3.Scale(mesh.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                result.Encapsulate(toLocal.MultiplyPoint3x4(corner));
            }
            return result;
        }

        private static void Rasterize(bool[] mask, int view, float u0, float u1, float v0, float v1, bool ellipse)
        {
            int i0 = Cell(u0), i1 = Cell(u1), j0 = Cell(v0), j1 = Cell(v1);
            float cu = (u0 + u1) * 0.5f, cv = (v0 + v1) * 0.5f;
            float ru = Mathf.Max((u1 - u0) * 0.5f, 1e-4f), rv = Mathf.Max((v1 - v0) * 0.5f, 1e-4f);
            bool any = false;
            for (int i = i0; i <= i1; i++)
            {
                for (int j = j0; j <= j1; j++)
                {
                    if (ellipse)
                    {
                        float du = (CellCenter(i) - cu) / ru, dv = (CellCenter(j) - cv) / rv;
                        if (du * du + dv * dv > 1f)
                        {
                            continue;
                        }
                    }
                    mask[view * Grid * Grid + j * Grid + i] = true;
                    any = true;
                }
            }
            if (!any)
            {
                mask[view * Grid * Grid + Cell(cv) * Grid + Cell(cu)] = true; // 比一格还小的圆也至少占一格。
            }
        }

        private static float CellCenter(int index) => -Extent + (index + 0.5f) * (2f * Extent / Grid);

        private static int Cell(float coord) =>
            Mathf.Clamp(Mathf.FloorToInt((coord + Extent) / (2f * Extent) * Grid), 0, Grid - 1);

        private static float IoU(bool[] a, bool[] b)
        {
            int inter = 0, union = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] && b[i]) inter++;
                if (a[i] || b[i]) union++;
            }
            return union == 0 ? 0f : (float)inter / union;
        }

        /// <summary>侧视剪影每一行的平均宽度（世界单位）：细杆类很窄，方块类接近整个身宽。</summary>
        private static float MeanSideWidth(Case c)
        {
            int filled = 0, rows = 0;
            for (int j = 0; j < Grid; j++)
            {
                int row = 0;
                for (int i = 0; i < Grid; i++)
                {
                    if (c.Mask[j * Grid + i]) row++;
                }
                if (row > 0)
                {
                    filled += row;
                    rows++;
                }
            }
            return rows == 0 ? 0f : (float)filled / rows * (2f * Extent / Grid);
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
