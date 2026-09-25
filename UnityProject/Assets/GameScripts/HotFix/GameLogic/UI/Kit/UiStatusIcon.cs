using GameLogic.Localization;
using GameLogic.Notifications;
using UnityEngine.UIElements;

namespace GameLogic.UI.Kit
{
    /// <summary>FG00 B05 列出的实体状态（全部）。</summary>
    public enum UiEntityStatus : byte
    {
        Working = 0,
        Idle = 1,
        NoMaterial = 2,
        NoPower = 3,
        NoFluid = 4,
        OutputBlocked = 5,
        Disabled = 6,
        Damaged = 7,
        Destroyed = 8,
        Upgrading = 9,
    }

    /// <summary>
    /// FG0-UX-01（FGR-ARC-007 状态图标集、FGR-UX-004、FG00 B05/B15、FGT-UX-007 基础版）：
    /// 每个状态 = 独有形状 + 语义颜色 + 名称文本键。形状两两不同，色盲或灰度下仍能区分。
    /// 宿主是 UXML 里一个带 <c>uk-status</c> 类的空元素；<see cref="Build"/> 往里加两件形状子元素（由 USS 决定长相）。
    /// </summary>
    public static class UiStatusIcon
    {
        public const string ShapeA = "uk-shape-a";
        public const string ShapeB = "uk-shape-b";

        private static readonly string[] ShapeClasses =
        {
            "uk-shape-circle", "uk-shape-ring", "uk-shape-hollow-square", "uk-shape-diamond", "uk-shape-hollow-diamond",
            "uk-shape-square", "uk-shape-bar", "uk-shape-slash", "uk-shape-cross", "uk-shape-plus",
        };

        private static readonly string[] ToneClasses = { "uk-tone-good", "uk-tone-info", "uk-tone-warn", "uk-tone-danger", "uk-tone-muted" };

        public static int StatusCount => ShapeClasses.Length;

        /// <summary>状态 → 形状类（下标即 <see cref="UiEntityStatus"/> 数值）。</summary>
        public static string ShapeOf(UiEntityStatus status) => ShapeClasses[(int)status];

        /// <summary>状态 → 语义颜色类（红 = 危险，琥珀 = 警告，蓝 = 信息，绿 = 正常，灰 = 停用）。</summary>
        public static string ToneOf(UiEntityStatus status)
        {
            switch (status)
            {
                case UiEntityStatus.Working: return "uk-tone-good";
                case UiEntityStatus.Idle:
                case UiEntityStatus.Upgrading: return "uk-tone-info";
                case UiEntityStatus.NoMaterial:
                case UiEntityStatus.NoPower:
                case UiEntityStatus.NoFluid:
                case UiEntityStatus.OutputBlocked: return "uk-tone-warn";
                case UiEntityStatus.Damaged:
                case UiEntityStatus.Destroyed: return "uk-tone-danger";
                default: return "uk-tone-muted";
            }
        }

        public static string NameKey(UiEntityStatus status)
        {
            switch (status)
            {
                case UiEntityStatus.Working: return "status.working.name";
                case UiEntityStatus.Idle: return "status.idle.name";
                case UiEntityStatus.NoMaterial: return "status.no_material.name";
                case UiEntityStatus.NoPower: return "status.no_power.name";
                case UiEntityStatus.NoFluid: return "status.no_fluid.name";
                case UiEntityStatus.OutputBlocked: return "status.output_blocked.name";
                case UiEntityStatus.Disabled: return "status.disabled.name";
                case UiEntityStatus.Damaged: return "status.damaged.name";
                case UiEntityStatus.Destroyed: return "status.destroyed.name";
                default: return "status.upgrading.name";
            }
        }

        /// <summary>通知等级的图标：紧急 = 叉（红），警告 = 菱形（琥珀），信息 = 圆（蓝）。三者形状互不相同。</summary>
        public static void SetLevel(VisualElement host, NotifyLevel level)
        {
            switch (level)
            {
                case NotifyLevel.Urgent: Apply(host, "uk-shape-cross", "uk-tone-danger"); break;
                case NotifyLevel.Warning: Apply(host, "uk-shape-diamond", "uk-tone-warn"); break;
                default: Apply(host, "uk-shape-circle", "uk-tone-info"); break;
            }
            if (host != null && host.pickingMode == PickingMode.Position)
            {
                string key = level == NotifyLevel.Urgent ? "notify.tier.urgent" : level == NotifyLevel.Warning ? "notify.tier.warning" : "notify.tier.info";
                UiTooltip.Attach(host, () => new TooltipContent { Title = GameText.Get(key) });
            }
        }

        public static string LevelShape(NotifyLevel level) =>
            level == NotifyLevel.Urgent ? "uk-shape-cross" : level == NotifyLevel.Warning ? "uk-shape-diamond" : "uk-shape-circle";

        /// <summary>往宿主里加两件形状子元素（幂等）。</summary>
        public static void Build(VisualElement host)
        {
            if (host == null || host.Q(className: ShapeA) != null)
            {
                return;
            }
            host.AddToClassList("uk-status");
            var a = new VisualElement { pickingMode = PickingMode.Ignore };
            a.AddToClassList(ShapeA);
            var b = new VisualElement { pickingMode = PickingMode.Ignore };
            b.AddToClassList(ShapeB);
            host.Add(a);
            host.Add(b);
        }

        public static void Set(VisualElement host, UiEntityStatus status)
        {
            Apply(host, ShapeOf(status), ToneOf(status));
            // 运行时悬停提示（VisualElement.tooltip 只在编辑器界面生效）：图标不能没有说明（FGR-UX-030）。
            string key = NameKey(status);
            UiTooltip.Attach(host, () => new TooltipContent { Title = GameText.Get(key) });
        }

        private static void Apply(VisualElement host, string shape, string tone)
        {
            if (host == null)
            {
                return;
            }
            Build(host);
            foreach (string c in ShapeClasses)
            {
                host.EnableInClassList(c, c == shape);
            }
            foreach (string c in ToneClasses)
            {
                host.EnableInClassList(c, c == tone);
            }
        }
    }
}
