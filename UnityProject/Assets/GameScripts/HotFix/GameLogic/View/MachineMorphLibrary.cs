using System.Collections.Generic;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.View
{
    /// <summary>
    /// FG1-VFX-01 机身形变部件库（FGR-FW-020、022；美术规则 02 §3、05 §4）——占位低模，程序生成，**正式美术到位前的占位**（B22，DEBT-FG1VFX01-01）。
    ///
    /// ── 结构 ──
    /// 每类状态（形变态 / 喷口态 / 线圈态）一个独立挂点组，组里两类部件：
    /// - 底盘件：与组件无关的整机轮廓变化（形变态＝两侧散热鳍扇形展开；喷口态＝尾部两具喷口 + 两侧外露管线；线圈态＝环绕机身的电磁线圈环）；
    /// - 组件件：每个作战组件在这一类状态下自己的变化（连射器炮管发红 / 重炮长管发红 / 冲刺器撞角发红 / 标记器天线升起……共 6 组件 × 3 状态）。
    /// 每个部件又分“实体”（金属，Standard）与“发光”（自发光色，无光照）两件，上帝视角下靠轮廓变化 + 自发光辨认（FGR-FW-022）。
    ///
    /// ── 实例化（FGR-FW-022 / FG02 第 7 章）──
    /// 每个部件 = **一个 GameObject、一个 MeshFilter、一张网格、一份材质**（外形槽只取第一个 MeshFilter 的已知坑不会踩：每件本来就只有一个）。
    /// 网格按（组件, 类别, 件）缓存、所有机器共用同一个 Mesh 实例；材质按（类别, 件）共用、全部开 GPU Instancing——同屏 N 台机器的同一部件
    /// 由引擎合批为实例化绘制，不给每台机器生成独立网格或材质。数量上限：网格 ≤ 3 类 × (1 底盘 + 6 组件) × 2 件 + 1 信号光柱 = 43，材质 = 1 实体 + 3 发光（光柱共用线圈态的青蓝发光材质）。
    ///
    /// ── 坐标 ──
    /// 部件挂在机器命中盒（胶囊，中心在原点、高 2、半径 0.5）下面，与 <see cref="PlaceholderSilhouette"/> 同一局部坐标：+Z 朝前、地面 y = -1。
    /// 三角面数：每个组件的一套状态（底盘件 + 组件件）≤ 1,000（美术规则 02 §2 作战组件预算，自检核对）。
    /// </summary>
    public static class MachineMorphLibrary
    {
        public enum PartRole : byte
        {
            Solid = 0,
            Glow = 1,

            /// <summary>接入信号光柱（美术规则 05 §4“接入口射出一道青蓝色信号光柱”）：只有底盘件的形变态有，只在信号真的接在这台机器上时显示
            /// （AI 自带限制器类固件常驻形变态时不显示，免得像是被接入了——FGR-BASE-020）。材质与线圈态发光共用同一份青蓝材质。</summary>
            Signal = 2,
        }

        /// <summary>信号光柱部件的节点名（<see cref="MachineMorphView"/> 按它找到光柱、随接入状态开关）。</summary>
        public static readonly string SignalBeamName = ChassisKey + "." + PartRole.Signal + MachineMorphView.PlaceholderTag;

        /// <summary>底盘件的“组件键”（与任何作战组件无关）。</summary>
        public const string ChassisKey = "chassis";

        /// <summary>每个组件一套状态（底盘件 + 组件件）的三角面上限（美术规则 02 §2：作战组件 ≤ 1,000）。</summary>
        public const int TriangleBudgetPerState = 1000;

        public static readonly Color SolidColor = new Color(0.86f, 0.89f, 0.92f, 1f);   // 玩家机队冷白装甲（美术规则 03）
        public static readonly Color LimiterGlow = new Color(1f, 0.28f, 0.1f, 1f);      // 枪管发红：红热
        public static readonly Color FluidGlow = new Color(1f, 0.62f, 0.12f, 1f);        // 喷口内壁橙红热光
        public static readonly Color EmGlow = new Color(0.25f, 0.92f, 1f, 1f);           // 线圈青蓝光环

        private const string GlowShader = "BinGames/SimInstancedUnlit";
        private const string SolidShader = "Standard";

        private static readonly Dictionary<(string Comp, MorphMask Cat, PartRole Role), Mesh> Meshes = new Dictionary<(string, MorphMask, PartRole), Mesh>();
        private static readonly HashSet<(string, MorphMask, PartRole)> Empty = new HashSet<(string, MorphMask, PartRole)>();
        private static Material _solid;
        private static readonly Material[] GlowMaterials = new Material[3];

        /// <summary>当前缓存的网格份数（自检：机器数翻倍时不变）。</summary>
        public static int MeshCount => Meshes.Count;

        public static int MaterialCount
        {
            get
            {
                int n = _solid != null ? 1 : 0;
                foreach (Material m in GlowMaterials)
                {
                    n += m != null ? 1 : 0;
                }
                return n;
            }
        }

        public static Color GlowColor(MorphMask category) => category switch
        {
            MorphMask.Limiter => LimiterGlow,
            MorphMask.Fluid => FluidGlow,
            _ => EmGlow,
        };

        /// <summary>（组件, 类别, 件）的共享网格；这一件没有几何（例如某组件在某类状态下只有发光件）时返回 null。</summary>
        public static Mesh MeshFor(string componentKey, MorphMask category, PartRole role)
        {
            var key = (componentKey ?? ChassisKey, category, role);
            if (Meshes.TryGetValue(key, out Mesh cached) && cached != null)
            {
                return cached;
            }
            if (Empty.Contains(key))
            {
                return null;
            }
            var b = new Builder();
            Build(b, key.Item1, category, role);
            if (b.VertexCount == 0)
            {
                Empty.Add(key);
                return null;
            }
            Mesh mesh = b.ToMesh($"morph_{key.Item1}_{MachineMorph.Describe(category)}_{role}".ToLowerInvariant());
            Meshes[key] = mesh;
            return mesh;
        }

        /// <summary>这一类状态、这一件的共享材质（GPU Instancing 已开）。</summary>
        public static Material MaterialFor(MorphMask category, PartRole role)
        {
            if (role == PartRole.Signal)
            {
                return MaterialFor(MorphMask.Electromagnetic, PartRole.Glow); // 信号光柱与线圈光环同为青蓝（信号色），共用一份材质
            }
            if (role == PartRole.Solid)
            {
                if (_solid == null)
                {
                    _solid = NewMaterial(SolidShader, SolidColor, "MorphSolid");
                }
                return _solid;
            }
            int i = MachineMorph.IndexOf(category);
            if (i < 0)
            {
                return null;
            }
            if (GlowMaterials[i] == null)
            {
                GlowMaterials[i] = NewMaterial(GlowShader, GlowColor(category), "MorphGlow_" + MachineMorph.Describe(category));
            }
            return GlowMaterials[i];
        }

        /// <summary>某组件一套状态（底盘件 + 组件件，实体 + 发光 + 形变态的信号光柱）的三角面数。</summary>
        public static int StateTriangles(string componentId, MorphMask category)
        {
            int n = 0;
            foreach (string key in new[] { ChassisKey, componentId })
            {
                foreach (PartRole role in new[] { PartRole.Solid, PartRole.Glow, PartRole.Signal })
                {
                    Mesh m = MeshFor(key, category, role);
                    n += m != null ? m.triangles.Length / 3 : 0;
                }
            }
            return n;
        }

        /// <summary>释放全部网格与材质（整个世界卸载时，各地点的表现对象此时已销毁）。</summary>
        public static void ReleaseAll()
        {
            foreach (Mesh m in Meshes.Values)
            {
                if (m != null)
                {
                    UnityObjects.Release(m);
                }
            }
            Meshes.Clear();
            Empty.Clear();
            if (_solid != null)
            {
                UnityObjects.Release(_solid);
            }
            _solid = null;
            for (int i = 0; i < GlowMaterials.Length; i++)
            {
                if (GlowMaterials[i] != null)
                {
                    UnityObjects.Release(GlowMaterials[i]);
                }
                GlowMaterials[i] = null;
            }
        }

        private static Material NewMaterial(string shaderName, Color color, string name)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Log.Error($"[MachineMorphLibrary] 找不到着色器 {shaderName}，形变部件改用 Standard。");
                shader = Shader.Find(SolidShader);
            }
            var m = new Material(shader)
            {
                name = name,
                color = color,
                enableInstancing = true,
                hideFlags = HideFlags.DontSave,
            };
            return m;
        }

        // ── 几何 ────────────────────────────────────────────────────────────────────

        private const float BodyHalfX = 0.75f;   // 占位剪影车体半宽（PlaceholderSilhouette：1.5 × 1.8）
        private const float BodyHalfZ = 0.9f;
        private const float BodyTopY = -0.37f;

        private static void Build(Builder b, string key, MorphMask cat, PartRole role)
        {
            if (role == PartRole.Signal)
            {
                if (key == ChassisKey && cat == MorphMask.Limiter)
                {
                    BuildSignalBeam(b);
                }
                return;
            }
            bool solid = role == PartRole.Solid;
            if (key == ChassisKey)
            {
                BuildChassis(b, cat, solid);
                return;
            }
            switch (key)
            {
                case ComponentCatalog.CompGunId: BuildGun(b, cat, solid); break;
                case ComponentCatalog.CompBeamId: BuildBeam(b, cat, solid); break;
                case ComponentCatalog.CompRepairBeamId: BuildRepair(b, cat, solid); break;
                case ComponentCatalog.CompCannonId: BuildCannon(b, cat, solid); break;
                case ComponentCatalog.FuncDashId: BuildDash(b, cat, solid); break;
                case ComponentCatalog.FuncMarkerId: BuildMarker(b, cat, solid); break;
            }
        }

        /// <summary>整机轮廓：形变态向两侧加宽，喷口态向后加长，线圈态加一圈环——三类占不同方位，叠加时互不遮挡。</summary>
        private static void BuildChassis(Builder b, MorphMask cat, bool solid)
        {
            switch (cat)
            {
                case MorphMask.Limiter:
                    // 两侧各 3 片散热鳍 / 外壳板，从车体侧后方向外扇形打开、平摊着（俯视看得到整片面积，车宽从 1.5 米变成约 3.4 米）。
                    // 三片高度错开几厘米，重叠处不闪面。
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var pivot = new Vector3(side * (BodyHalfX - 0.05f), -0.5f, -0.2f);
                        float[] degs = { 45f, 70f, 95f };
                        for (int k = 0; k < degs.Length; k++)
                        {
                            float a = degs[k] * Mathf.Deg2Rad;
                            var dir = new Vector3(side * Mathf.Sin(a), 0f, -Mathf.Cos(a));
                            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                            Vector3 lift = Vector3.up * (0.04f * k);
                            if (solid)
                            {
                                b.Box(pivot + lift + dir * 0.5f, new Vector3(0.36f, 0.05f, 0.95f), yaw);
                            }
                            else
                            {
                                b.Box(pivot + lift + dir * 1.02f + Vector3.up * 0.03f, new Vector3(0.36f, 0.05f, 0.14f), yaw); // 鳍尖热光
                            }
                        }
                    }
                    break;
                case MorphMask.Fluid:
                    foreach (float side in new[] { -1f, 1f })
                    {
                        if (solid)
                        {
                            b.Cylinder(new Vector3(side * 0.5f, -0.6f, -BodyHalfZ - 0.25f), Vector3.forward, 0.2f, 0.6f, 10); // 尾部喷口
                            b.Box(new Vector3(side * (BodyHalfX + 0.1f), -0.42f, -0.1f), new Vector3(0.12f, 0.12f, 1.5f), 0f); // 外露管线
                            b.Box(new Vector3(side * (BodyHalfX + 0.1f), -0.42f, 0.62f), new Vector3(0.12f, 0.12f, 0.12f), 0f);
                        }
                        else
                        {
                            b.Cylinder(new Vector3(side * 0.5f, -0.6f, -BodyHalfZ - 0.57f), Vector3.forward, 0.16f, 0.06f, 10); // 喷口内壁热光
                            b.Box(new Vector3(side * 0.5f, -0.6f, -BodyHalfZ - 0.95f), new Vector3(0.24f, 0.12f, 0.7f), 0f);     // 短促热气
                        }
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (solid)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            float a = (45f + i * 90f) * Mathf.Deg2Rad;
                            b.Box(new Vector3(Mathf.Sin(a) * 1.05f, -0.3f, Mathf.Cos(a) * 1.05f), new Vector3(0.12f, 0.3f, 0.12f), i * 90f + 45f); // 线圈支架
                        }
                    }
                    else
                    {
                        b.Ring(new Vector3(0f, -0.12f, 0f), 1.18f, 1.4f, 0.07f, 28); // 环绕机身的线圈光环（俯视是一个圆）
                        BuildArcs(b, 1.29f, -0.12f, 6);                              // 美术规则 05 §4：线圈表面细小电弧
                    }
                    break;
            }
        }

        /// <summary>接入口（车顶中后部）竖直射出的青蓝信号光柱 + 底座光圈；俯视是车顶上一个青蓝亮点加一圈光，斜视是一根光柱。</summary>
        private static void BuildSignalBeam(Builder b)
        {
            var port = new Vector3(0f, BodyTopY, -0.35f);
            b.Cylinder(port + Vector3.up * 1.3f, Vector3.up, 0.09f, 2.6f, 8);
            b.Ring(port + Vector3.up * 0.5f, 0.16f, 0.34f, 0.02f, 12); // 悬在车顶上方、高过炮管与剪影件：俯视战略镜头下光柱只是一个点，靠这圈光认出“信号在这台”
        }

        /// <summary>
        /// 线圈表面的细小电弧：沿半径 <paramref name="radius"/> 的线圈均匀取 <paramref name="count"/> 处，每处三段折线（细长条，径向左右错开、上下起伏），
        /// 贴着线圈往外跳一点。静态几何（占位；正式美术可换成闪烁的贴图 / 粒子，归 DEBT-FG1VFX01-01）。
        /// </summary>
        private static void BuildArcs(Builder b, float radius, float y, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / count;
                var radial = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
                var tangent = new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
                Vector3 p0 = new Vector3(0f, y, 0f) + radial * (radius - 0.02f) + Vector3.up * 0.08f;
                Vector3 p1 = p0 + tangent * 0.1f + radial * 0.12f + Vector3.up * 0.06f;
                Vector3 p2 = p1 - tangent * 0.12f + radial * 0.1f - Vector3.up * 0.04f;
                Vector3 p3 = p2 + tangent * 0.1f + radial * 0.12f + Vector3.up * 0.05f;
                Segment(b, p0, p1);
                Segment(b, p1, p2);
                Segment(b, p2, p3);
            }
        }

        private static void Segment(Builder b, Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            var flat = new Vector3(d.x, 0f, d.z);
            float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            b.Box((from + to) * 0.5f, new Vector3(0.035f, 0.035f + Mathf.Abs(d.y), flat.magnitude + 0.03f), yaw);
        }

        private static void BuildGun(Builder b, MorphMask cat, bool solid)
        {
            switch (cat)
            {
                case MorphMask.Limiter:
                    foreach (float x in new[] { -0.13f, 0f, 0.13f })
                    {
                        if (solid)
                        {
                            b.Box(new Vector3(x, -0.12f, 0.95f), new Vector3(0.09f, 0.09f, 0.35f), 0f); // 炮管前伸
                        }
                        else
                        {
                            b.Box(new Vector3(x, -0.12f, 0.62f), new Vector3(0.1f, 0.1f, 0.5f), 0f);    // 枪管发红
                        }
                    }
                    break;
                case MorphMask.Fluid:
                    foreach (float side in new[] { -1f, 1f })
                    {
                        if (solid)
                        {
                            b.Box(new Vector3(side * 0.3f, -0.12f, 0.55f), new Vector3(0.12f, 0.16f, 0.3f), 0f); // 炮管两侧冷却液排口
                        }
                        else
                        {
                            b.Box(new Vector3(side * 0.38f, -0.12f, 0.55f), new Vector3(0.05f, 0.12f, 0.22f), 0f);
                        }
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (!solid)
                    {
                        b.Ring(new Vector3(0f, -0.12f, 0.7f), 0.2f, 0.3f, 0.05f, 14, Vector3.forward); // 炮管外的加速线圈
                        b.Ring(new Vector3(0f, -0.12f, 0.95f), 0.2f, 0.3f, 0.05f, 14, Vector3.forward);
                    }
                    break;
            }
        }

        private static void BuildBeam(Builder b, MorphMask cat, bool solid)
        {
            switch (cat)
            {
                case MorphMask.Limiter:
                    if (solid)
                    {
                        b.Box(new Vector3(0f, -0.1f, 0.95f), new Vector3(0.42f, 0.24f, 0.3f), 0f); // 发射器前伸
                    }
                    else
                    {
                        b.Box(new Vector3(0f, -0.1f, 1.12f), new Vector3(0.36f, 0.2f, 0.05f), 0f); // 聚焦口红热
                    }
                    break;
                case MorphMask.Fluid:
                    if (solid)
                    {
                        b.Box(new Vector3(0f, 0.08f, 0.45f), new Vector3(0.1f, 0.1f, 0.8f), 0f); // 顶部冷却管
                    }
                    else
                    {
                        b.Box(new Vector3(0f, 0.08f, 0.9f), new Vector3(0.14f, 0.14f, 0.12f), 0f);
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (!solid)
                    {
                        foreach (float z in new[] { 0.6f, 0.85f, 1.1f })
                        {
                            b.Ring(new Vector3(0f, -0.1f, z), 0.26f, 0.36f, 0.04f, 14, Vector3.forward); // 同心聚焦线圈环
                        }
                    }
                    break;
            }
        }

        private static void BuildRepair(Builder b, MorphMask cat, bool solid)
        {
            switch (cat)
            {
                case MorphMask.Limiter:
                    if (solid)
                    {
                        b.Box(new Vector3(0f, -0.05f, 0.85f), new Vector3(0.36f, 0.3f, 0.2f), 0f); // 发射头前推
                    }
                    else
                    {
                        b.Box(new Vector3(0f, -0.05f, 0.98f), new Vector3(0.3f, 0.24f, 0.05f), 0f);
                    }
                    break;
                case MorphMask.Fluid:
                    if (solid)
                    {
                        b.Cylinder(new Vector3(0.32f, -0.05f, 0.6f), Vector3.forward, 0.08f, 0.4f, 8); // 侧挂喷嘴
                    }
                    else
                    {
                        b.Cylinder(new Vector3(0.32f, -0.05f, 0.82f), Vector3.forward, 0.07f, 0.05f, 8);
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (!solid)
                    {
                        b.Ring(new Vector3(0f, -0.05f, 0.85f), 0.24f, 0.34f, 0.05f, 14, Vector3.forward);
                    }
                    break;
            }
        }

        private static void BuildCannon(Builder b, MorphMask cat, bool solid)
        {
            switch (cat)
            {
                case MorphMask.Limiter:
                    if (solid)
                    {
                        b.Box(new Vector3(0f, -0.14f, 1.45f), new Vector3(0.22f, 0.22f, 0.3f), 0f); // 炮口制退器前伸
                    }
                    else
                    {
                        b.Box(new Vector3(0f, -0.14f, 0.85f), new Vector3(0.2f, 0.2f, 0.9f), 0f);   // 长炮管发红
                    }
                    break;
                case MorphMask.Fluid:
                    foreach (float side in new[] { -1f, 1f })
                    {
                        if (solid)
                        {
                            b.Box(new Vector3(side * 0.2f, -0.14f, 1.4f), new Vector3(0.14f, 0.18f, 0.16f), 0f); // 制退器排气口
                        }
                        else
                        {
                            b.Box(new Vector3(side * 0.33f, -0.14f, 1.4f), new Vector3(0.14f, 0.1f, 0.12f), 0f);
                        }
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (!solid)
                    {
                        foreach (float z in new[] { 0.6f, 0.95f, 1.3f })
                        {
                            b.Ring(new Vector3(0f, -0.14f, z), 0.2f, 0.3f, 0.05f, 14, Vector3.forward); // 沿炮管的电磁加速线圈
                        }
                    }
                    break;
            }
        }

        private static void BuildDash(Builder b, MorphMask cat, bool solid)
        {
            switch (cat)
            {
                case MorphMask.Limiter:
                    if (solid)
                    {
                        b.Box(new Vector3(0f, -0.62f, BodyHalfZ + 0.3f), new Vector3(1.3f, 0.3f, 0.2f), 0f); // 撞角前伸
                    }
                    else
                    {
                        b.Box(new Vector3(0f, -0.62f, BodyHalfZ + 0.42f), new Vector3(1.2f, 0.12f, 0.06f), 0f); // 撞角刃口发红
                    }
                    break;
                case MorphMask.Fluid:
                    foreach (float side in new[] { -1f, 1f })
                    {
                        if (solid)
                        {
                            b.Cylinder(new Vector3(side * 0.35f, -0.4f, -BodyHalfZ + 0.1f), Vector3.forward, 0.12f, 0.35f, 8); // 推进器喷口
                        }
                        else
                        {
                            b.Box(new Vector3(side * 0.35f, -0.4f, -BodyHalfZ - 0.25f), new Vector3(0.18f, 0.12f, 0.4f), 0f);
                        }
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (!solid)
                    {
                        b.Ring(new Vector3(0f, -0.62f, BodyHalfZ + 0.3f), 0.5f, 0.62f, 0.05f, 16); // 撞角外的电磁环
                    }
                    break;
            }
        }

        private static void BuildMarker(Builder b, MorphMask cat, bool solid)
        {
            var mast = new Vector3(-0.45f, BodyTopY, -0.5f);
            switch (cat)
            {
                case MorphMask.Limiter:
                    if (solid)
                    {
                        b.Box(mast + new Vector3(0f, 0.55f, 0f), new Vector3(0.07f, 1.1f, 0.07f), 0f); // 天线升起
                        b.Box(mast + new Vector3(0f, 1.05f, 0f), new Vector3(0.5f, 0.05f, 0.08f), 0f);
                    }
                    else
                    {
                        b.Box(mast + new Vector3(0f, 1.15f, 0f), new Vector3(0.18f, 0.18f, 0.18f), 0f); // 指示头亮起
                    }
                    break;
                case MorphMask.Fluid:
                    if (solid)
                    {
                        b.Box(mast + new Vector3(0.15f, 0.1f, 0f), new Vector3(0.18f, 0.2f, 0.18f), 0f); // 天线基座排口
                    }
                    else
                    {
                        b.Box(mast + new Vector3(0.26f, 0.1f, 0f), new Vector3(0.05f, 0.14f, 0.14f), 0f);
                    }
                    break;
                case MorphMask.Electromagnetic:
                    if (!solid)
                    {
                        b.Ring(mast + new Vector3(0f, 0.45f, 0f), 0.2f, 0.3f, 0.04f, 14); // 天线外的场环
                    }
                    break;
            }
        }

        /// <summary>程序网格拼装：长方体、圆柱、圆环（每面独立法线）。只在第一次用到某件时构建一次。</summary>
        private sealed class Builder
        {
            private readonly List<Vector3> _v = new List<Vector3>(256);
            private readonly List<Vector3> _n = new List<Vector3>(256);
            private readonly List<int> _t = new List<int>(512);

            public int VertexCount => _v.Count;

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name, hideFlags = HideFlags.DontSave };
                m.SetVertices(_v);
                m.SetNormals(_n);
                m.SetTriangles(_t, 0);
                m.RecalculateBounds();
                m.UploadMeshData(false);
                return m;
            }

            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                int i = _v.Count;
                _v.Add(a); _v.Add(b); _v.Add(c); _v.Add(d);
                _n.Add(n); _n.Add(n); _n.Add(n); _n.Add(n);
                _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
                _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
            }

            private void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;
                int i = _v.Count;
                _v.Add(a); _v.Add(b); _v.Add(c);
                _n.Add(n); _n.Add(n); _n.Add(n);
                _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
            }

            /// <summary>长方体：中心、尺寸（局部 X 宽 / Y 高 / Z 长）、绕 Y 轴偏航角。</summary>
            public void Box(Vector3 c, Vector3 size, float yawDeg)
            {
                Quaternion q = Quaternion.Euler(0f, yawDeg, 0f);
                Vector3 h = size * 0.5f;
                Vector3 P(float x, float y, float z) => c + q * new Vector3(x * h.x, y * h.y, z * h.z);
                Quad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1));     // 前
                Quad(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1)); // 后
                Quad(P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1));     // 右
                Quad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1)); // 左
                Quad(P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1));     // 顶
                Quad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1)); // 底
            }

            /// <summary>圆柱：中心、轴向、半径、长度、分段数（两端封口）。</summary>
            public void Cylinder(Vector3 c, Vector3 axis, float radius, float length, int segments)
            {
                Basis(axis, out Vector3 u, out Vector3 w);
                Vector3 half = axis.normalized * (length * 0.5f);
                for (int i = 0; i < segments; i++)
                {
                    float a0 = i * Mathf.PI * 2f / segments;
                    float a1 = (i + 1) * Mathf.PI * 2f / segments;
                    Vector3 r0 = (u * Mathf.Cos(a0) + w * Mathf.Sin(a0)) * radius;
                    Vector3 r1 = (u * Mathf.Cos(a1) + w * Mathf.Sin(a1)) * radius;
                    Quad(c - half + r0, c - half + r1, c + half + r1, c + half + r0);
                    Tri(c + half, c + half + r0, c + half + r1);
                    Tri(c - half, c - half + r1, c - half + r0);
                }
            }

            /// <summary>圆环（扁平环形截面）：中心、内外半径、半厚度、分段数、环所在平面的法线（默认竖直，俯视是一个圆）。</summary>
            public void Ring(Vector3 c, float inner, float outer, float halfThickness, int segments, Vector3 normal = default)
            {
                Vector3 axis = normal == default ? Vector3.up : normal.normalized;
                Basis(axis, out Vector3 u, out Vector3 w);
                Vector3 up = axis * halfThickness;
                for (int i = 0; i < segments; i++)
                {
                    float a0 = i * Mathf.PI * 2f / segments;
                    float a1 = (i + 1) * Mathf.PI * 2f / segments;
                    Vector3 d0 = u * Mathf.Cos(a0) + w * Mathf.Sin(a0);
                    Vector3 d1 = u * Mathf.Cos(a1) + w * Mathf.Sin(a1);
                    Vector3 i0 = c + d0 * inner, i1 = c + d1 * inner, o0 = c + d0 * outer, o1 = c + d1 * outer;
                    // 顺序保证 Cross(b - a, c - a) 朝外（Unity 以从外侧看顺时针为正面）。
                    Quad(o0 + up, o1 + up, i1 + up, i0 + up);         // 顶
                    Quad(i0 - up, i1 - up, o1 - up, o0 - up);         // 底
                    Quad(o1 - up, o1 + up, o0 + up, o0 - up);         // 外
                    Quad(i0 - up, i0 + up, i1 + up, i1 - up);         // 内
                }
            }

            private static void Basis(Vector3 axis, out Vector3 u, out Vector3 w)
            {
                Vector3 n = axis.normalized;
                Vector3 seed = Mathf.Abs(Vector3.Dot(n, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
                u = Vector3.Cross(n, seed).normalized;
                w = Vector3.Cross(n, u).normalized;
            }
        }
    }
}
