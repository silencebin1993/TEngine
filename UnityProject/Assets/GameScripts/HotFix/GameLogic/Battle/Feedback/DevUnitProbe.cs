using UnityEngine;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// 开发期单位探针（2026-09-14）。挂在 <see cref="DevUnitGoMirror"/> 镜像出来的每个单位上，
    /// 把这一帧的模拟事实原样摊在 Inspector 里。
    ///
    /// **为什么是 Inspector 而不是屏幕 UI**：玩家明确要求「开发阶段在 Hierarchy 能看到是谁、
    /// UID 是多少，而不是在 game 窗口加影响视觉的测试 UI」。Hierarchy 里点中一个单位就能读全部
    /// 状态，既不挡画面，也能在暂停时逐帧看。
    ///
    /// 全部字段都是**只读快照**：本组件不写模拟、不做判断，改这里的值对游戏没有任何影响。
    /// 「移速慢」这类问题已经报了四轮、三次根因互不相同，靠描述判不出来——
    /// 这里同时给出 <see cref="MaxSpeed"/>（上限）与 <see cref="Speed"/>（实测），
    /// 一眼能分清是"上限被改小了"还是"根本没在全速跑"。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DevUnitProbe : MonoBehaviour
    {
        [Header("身份")]
        [Tooltip("稳定实体 ID。报问题时给这个号，它与 GameObject 名字里的 #N 一致。")]
        public string UID;
        [Tooltip("热更层逻辑 ID（装配登记、奖励归因用）。0 = 没登记过。")]
        public int LogicId;
        [Tooltip("渲染造型 id。两名可控友军刻意用同一个——差异只应来自装配的器官。")]
        public int VisualId;
        [Tooltip("行为原型 id。-1 = 内核默认原型（玩家本体）。它决定加速度/索敌/AI 攻击数值。")]
        public int ArchetypeId;

        [Header("归属")]
        public string Faction;
        [Tooltip("Player=玩家正在直控；Commanded=在执行 RTS 命令（含原地守备）；AI=自由 AI。")]
        public string IntentSource;
        [Tooltip("当前持有的命令。None 表示没有命令——按现在的规矩这种单位会走自由 AI 漫游。")]
        public string Command;

        [Header("移动（排查移速问题看这两行）")]
        [Tooltip("速度上限。三具可控身体应当相同。")]
        public float MaxSpeed;
        [Tooltip("本帧实测速度。停着=0；明显小于上限又不为 0，通常是 AI 漫游的半速系数。")]
        public float Speed;

        [Header("生命")]
        public float Health;
        public float Radius;
    }
}
