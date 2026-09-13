using GameLogic.Control;
using GameLogic.Core;
using UnityEngine.UIElements;

namespace GameLogic.UI.Battle
{
    /// <summary>
    /// 直控三个量（代谢 / 热债 / 冷却）的 HUD 绑定（M2-03c）。
    ///
    /// ── 为什么单独一个静态类，而不是写进 <c>BattleHudToolkit</c> ──
    /// <c>BattleHudToolkit</c> 是 <c>MonoBehaviour</c>，Edit 模式起不来，写在它里面的绑定逻辑
    /// 就只能靠进 Play 手点验收——而本仓明令默认不走交互式 Play 验收。
    /// 拆成纯函数之后，自检可以拿**真实的 BattleHud.uxml 实例**把同一段生产代码跑一遍，
    /// 断言的是真节点上的真显示状态，不是反射探针（同 M2-03a §4 "不可注入就没法验收"的理由）。
    ///
    /// ── 为什么只在直控视角下显示 ──
    /// 战略视角看的是"整片战场怎么调度"，这三个量说的是"我现在这具身体还能不能再按一下"。
    /// 在战略视角下常驻一块具体身体的代谢条，既没有可操作性，又会让玩家以为那是全局资源。
    /// 判据取 <see cref="InputRouter.Scope"/>——M2-01 已经把"现在是哪个视角"收敛成这一处真相，
    /// 各处自己去猜镜头状态正是那一段在纠正的错误。
    /// </summary>
    public static class DirectVitalsHudBinding
    {
        /// <summary>UXML 中整块容器的节点名。</summary>
        public const string BlockName = "DirectVitalsBlock";

        /// <summary>UXML 中文本节点名。</summary>
        public const string TextName = "DirectVitalsText";

        /// <summary>过载态附加的样式类（红边 + 底色）。</summary>
        public const string OverloadedClass = "vitals-overloaded";

        /// <summary>
        /// 这一帧该不该显示。两个条件缺一不可：**在直控视角**，且**真的有一具受控身体**。
        /// </summary>
        public static bool ShouldShow(in UnitVitalsView vitals)
        {
            return vitals.Valid && InputRouter.Scope == InputScope.Direct;
        }

        /// <summary>
        /// 一行文案。三个量放一行而不是三条进度条：它们是"还能不能再按"的同一个判断的三个分量，
        /// 拆成三块会在左栏占掉四行高度，而 HUD 左栏已经有六组信息在抢位置。
        /// </summary>
        public static string BuildText(in UnitVitalsView vitals)
        {
            if (!vitals.Valid)
            {
                return "代谢 --";
            }

            string heat = vitals.Overloaded
                ? $"热债 {vitals.Heat:F0}/{vitals.HeatThreshold:F0} 过载"
                : $"热债 {vitals.Heat:F0}/{vitals.HeatThreshold:F0}";

            return $"代谢 {vitals.Metabolism:F0}/{vitals.MetabolismMax:F0}　{heat}" +
                   $"　主 {FormatCooldown(vitals.PrimaryCooldown)}" +
                   $"　功 {FormatCooldown(vitals.UtilityCooldown)}";
        }

        /// <summary>冷却文案。就绪时写"就绪"而不是 0.0s——玩家要的是能不能按，不是一个恒零的数字。</summary>
        public static string FormatCooldown(float secondsLeft)
        {
            return secondsLeft > 0.05f ? $"{secondsLeft:F1}s" : "就绪";
        }

        /// <summary>
        /// 把快照写到真实节点上。<b>生产 HUD 与自检走的是同一个方法</b>——
        /// 两份实现会立刻漂移，而漂移的那一份正好是没人验收的那一份。
        /// </summary>
        public static void Apply(VisualElement block, Label text, in UnitVitalsView vitals)
        {
            if (block == null)
            {
                return;
            }

            if (!ShouldShow(vitals))
            {
                // 隐藏时不写文本：留着上一具身体的数字，切回来的那一帧会闪一次旧值。
                block.style.display = DisplayStyle.None;
                block.EnableInClassList(OverloadedClass, false);
                return;
            }

            block.style.display = DisplayStyle.Flex;
            if (text != null)
            {
                text.text = BuildText(vitals);
            }

            block.EnableInClassList(OverloadedClass, vitals.Overloaded);
        }
    }
}
