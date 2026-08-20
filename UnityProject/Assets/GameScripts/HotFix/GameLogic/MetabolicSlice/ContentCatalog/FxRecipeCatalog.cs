using System.Collections.Generic;
using UnityEngine;

namespace GameLogic.MetabolicSlice.ContentCatalog
{
    /// <summary>共享网格种类（story-009 G6 → 010 J5 扩展至七选一）：
    /// Circle/Streak/Wedge/Ring 已有，010 新增 Cone/Cross/Band。</summary>
    public enum FxMeshKind { Circle, Streak, Wedge, Ring, Cone, Cross, Band }

    /// <summary>生命周期类型（story-009 G2a）：只存类型，不存数值——数值仍来自
    /// <see cref="FxRecipeCatalog.Global"/>.PersistentLife 或
    /// <see cref="GameLogic.MetabolicSlice.Combat.ComposeMotionMath"/>.MotionFlightDuration
    /// 这两个同步不变量来源，配方表不得覆盖它们。</summary>
    public enum FxLifeKind { Persistent, Flight }

    /// <summary>单个 Shape 的表现配方（story-009 G2b：逐配方系数，缺省即不用，0 代表该 Shape 不需要该系数）。</summary>
    public sealed class FxShapeRecipe
    {
        public string Shape;
        public Color Color;
        public FxMeshKind Mesh;
        public FxLifeKind Life;

        /// <summary>Bolt/Spore 用：直径 = radius * DiameterCoef。</summary>
        public float DiameterCoef;

        /// <summary>Beam 专用：长 = radius * LengthCoef。</summary>
        public float LengthCoef;

        /// <summary>Beam 专用：宽 = radius * WidthCoef。</summary>
        public float WidthCoef;
    }

    /// <summary>
    /// story-009：<see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/> 的
    /// 白模弹道配方表驱动载体（Preflight G1 选②代码侧 Catalog，手法同
    /// <see cref="StageSkinCatalog"/>/<see cref="GeneCatalog"/>）。
    ///
    /// **可配 / 派生严格分开（G2）**：<see cref="Combat.MetabolicSliceBridge.DamageAreaRadius"/>、
    /// <see cref="Combat.MetabolicSliceBridge.ExplodeRadiusMult"/>、
    /// <see cref="Combat.ComposeMotionMath.MotionFlightDuration"/>、`segments = round(Count)`
    /// **禁止出现在本表**，本表只承载纯表现量。<see cref="ShapeKind"/> 私有枚举（G5）仍是 key 空间，
    /// 本表是内容；新增 Shape 仍须同时加枚举项与本表行，缺行由消费方的完备性检查报错（见
    /// <see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/> 静态构造）。
    /// </summary>
    public static class FxRecipeCatalog
    {
        /// <summary>全局唯一段（G2c）：<see cref="EnsurePool"/> 建共享 <see cref="Mesh"/> 时一次性消费的构建参数
        /// 与对象池大小，不按配方缓存——若做成逐配方就必须按 key 缓存网格，直接触发 XL＋内存泄漏面。</summary>
        public static class Global
        {
            public const int PoolSize = 32;
            public const float MarkerY = 0.19f;
            public const int CircleSegments = 20;
            public const float ArcHalfAngleDeg = 40f;
            public const int ArcSegments = 10;
            public const float RingInnerRatio = 0.65f;
            /// <summary>story-010 J1：Band 是「厚环带」，内圈明显小于 <see cref="RingInnerRatio"/>、段数也不同——
            /// 若沿用 Ring 的参数，两者顶点集合逐位相同，Field 与 Wave 会长得一模一样（Acceptance 3/4 要防的正是这个）。</summary>
            public const float BandInnerRatio = 0.3f;
            public const int BandSegments = 24;
            public const float PersistentLife = 0.5f;
            /// <summary>story-010 J2：指示器独立 8 位池（禁止与弹道 32 位池共用，避免被密集开火挤掉）。</summary>
            public const int IndicatorPoolSize = 8;
            /// <summary>story-005（scene-3d-content）：Compose 弹道/瞄准指示白模的绝对挤出高度——
            /// 参照 story-004 Zone=0.18f（薄标记不挡视线），因这是主动战斗读出的标记而略高，
            /// 但远低于 Obstacle=0.7f。供 <see cref="GameLogic.Battle.Feedback.WhiteboxComposeProjectileFeedback"/>
            /// 与 <see cref="GameLogic.Battle.Feedback.WhiteboxComposeAimIndicator"/> 共享。</summary>
            public const float MarkerHeight = 0.22f;
        }

        // ── Shape 配方（逐 Shape 行：底色 + 网格键 + 生命类型 + per-recipe 系数）──

        private static readonly Dictionary<string, FxShapeRecipe> _shapes = new Dictionary<string, FxShapeRecipe>
        {
            ["Bolt"] = new FxShapeRecipe
            {
                Shape = "Bolt", Color = new Color(1f, 0.95f, 0.6f, 0.95f),
                Mesh = FxMeshKind.Cone, Life = FxLifeKind.Flight, DiameterCoef = 0.35f,
            },
            ["Beam"] = new FxShapeRecipe
            {
                Shape = "Beam", Color = new Color(0.6f, 0.95f, 1f, 0.85f),
                Mesh = FxMeshKind.Streak, Life = FxLifeKind.Persistent, LengthCoef = 1.5f, WidthCoef = 0.25f,
            },
            ["Arc"] = new FxShapeRecipe
            {
                Shape = "Arc", Color = new Color(1f, 0.5f, 0.15f, 0.55f),
                Mesh = FxMeshKind.Wedge, Life = FxLifeKind.Flight,
            },
            ["Field"] = new FxShapeRecipe
            {
                Shape = "Field", Color = new Color(0.25f, 0.9f, 0.65f, 0.4f),
                Mesh = FxMeshKind.Band, Life = FxLifeKind.Persistent,
            },
            ["Wave"] = new FxShapeRecipe
            {
                Shape = "Wave", Color = new Color(0.5f, 0.85f, 1f, 0.8f),
                Mesh = FxMeshKind.Ring, Life = FxLifeKind.Flight,
            },
            ["Spore"] = new FxShapeRecipe
            {
                Shape = "Spore", Color = new Color(0.78f, 0.5f, 1f, 0.9f),
                Mesh = FxMeshKind.Cross, Life = FxLifeKind.Flight, DiameterCoef = 0.3f,
            },
            ["Melee"] = new FxShapeRecipe
            {
                Shape = "Melee", Color = new Color(0.95f, 0.25f, 0.2f, 0.9f),
                Mesh = FxMeshKind.Circle, Life = FxLifeKind.Flight, DiameterCoef = 0.4f,
            },
        };

        /// <summary>story-010 Required 1：表 key 只读枚举，供消费方做「表 → 枚举」的反向完备性检查
        /// （009 的静态构造只查了「枚举 → 表」一个方向）。</summary>
        public static IEnumerable<string> ShapeKeys => _shapes.Keys;

        /// <summary>id 未收录（包括未知 Shape 字符串）时返回 false，调用方按 H3/G3 结论自行回落 Bolt。</summary>
        public static bool TryGetShapeRecipe(string shape, out FxShapeRecipe recipe)
        {
            if (shape == null)
            {
                recipe = null;
                return false;
            }
            return _shapes.TryGetValue(shape, out recipe);
        }

        // ── 元素 Tag 染色（对照冻结总案 §3.4 元素词表全 10 项，逐字迁自 story-007 D1~D4）──

        /// <summary>数组顺序即优先级（战斗强调型 Fire/Shock/Acid/Ice 优先于环境覆盖型 Steam/Wet/Water/Oil，
        /// Light/Dark 零 producer 排最后），first-match-wins，不做多色混合（story-009 G4：顺序仍由这个
        /// 有序数组单独承载，不与下面的颜色 Dictionary 合并——Dictionary 枚举序不是契约）。</summary>
        public static readonly string[] ElementPriorityOrder =
        {
            "Fire", "Shock", "Acid", "Ice", "Steam", "Wet", "Water", "Oil", "Light", "Dark",
        };

        private static readonly Dictionary<string, Color> _elementColors = new Dictionary<string, Color>
        {
            ["Fire"] = new Color(1f, 0.35f, 0.1f, 0.9f),
            ["Shock"] = new Color(0.75f, 0.35f, 1f, 0.9f),
            ["Acid"] = new Color(0.55f, 0.85f, 0.15f, 0.9f),
            ["Ice"] = new Color(0.6f, 0.9f, 1f, 0.9f),
            ["Steam"] = new Color(0.85f, 0.85f, 0.9f, 0.8f),
            ["Wet"] = new Color(0.3f, 0.55f, 1f, 0.85f),
            ["Water"] = new Color(0.15f, 0.4f, 0.85f, 0.85f),
            ["Oil"] = new Color(0.35f, 0.25f, 0.15f, 0.9f),
            ["Light"] = new Color(1f, 0.95f, 0.6f, 0.9f),
            ["Dark"] = new Color(0.25f, 0.1f, 0.35f, 0.9f),
        };

        /// <summary>未收录的 tag 返回 <see cref="Color.white"/>（与旧 switch default 行为一致）。</summary>
        public static Color GetElementColor(string tag) => tag != null && _elementColors.TryGetValue(tag, out var c) ? c : Color.white;

        /// <summary>爆炸环兜底默认色（无元素 Tag 命中时使用，不借用任何单一 Shape 的底色，story-007 D8）。</summary>
        public static readonly Color DefaultExplodeColor = new Color(1f, 0.55f, 0.15f, 0.6f);
    }
}
