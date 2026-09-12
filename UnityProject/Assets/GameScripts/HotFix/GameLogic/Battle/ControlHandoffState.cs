using BinGames.Sim;
using Unity.Mathematics;

namespace GameLogic.Battle
{
    /// <summary>
    /// M1-06：一段「意识上次在哪、附在谁身上」的控制记忆。
    ///
    /// 它跨越三种断点：受控实体死亡、场景卸载重进、退出游戏后读档。
    /// 三者对身份的要求不一样，所以这里同时带两种标识：
    /// <list type="bullet">
    /// <item><see cref="ControlledUnitId"/>：精确，但只在**同一个 SimWorld 实例**内有效。
    /// 世界一重建就作废，落盘之后更是毫无意义。</item>
    /// <item><see cref="ControlledLogicId"/>：热更层分配的弱标识，世界重建后按同样顺序
    /// 生成的单位还能对上，是读档恢复真正依赖的那一个。</item>
    /// </list>
    /// 两者都对不上时还有 <see cref="FallbackAnchor"/> 兜底——控制权明确为无，
    /// 但镜头至少知道该看哪儿，不会甩到原点。
    ///
    /// 值类型且 <c>default</c> 即"无记录"，所以旧存档、缺字段、读盘失败都天然落到安全默认值。
    /// </summary>
    public struct ControlHandoffState
    {
        /// <summary>是否有任何可用记录。false 时调用方应当走"世界自带默认受控实体"的路径。</summary>
        public bool HasRecord;

        /// <summary>稳定实体身份。只在同一世界实例内可信；跨世界/跨进程一律解析失败并回退到 LogicId。</summary>
        public SimEntityId ControlledUnitId;

        /// <summary>受控实体的热更层逻辑 id。0 表示没有可用的跨世界标识。</summary>
        public int ControlledLogicId;

        /// <summary>最后一个有效受控实体的位置。无控制实体时的战略回退视角。</summary>
        public float2 FallbackAnchor;

        /// <summary><see cref="FallbackAnchor"/> 是否真的被写过。没写过时不要拿 (0,0) 当锚点用。</summary>
        public bool HasAnchor;

        /// <summary>明确的"无记录"。旧存档与读盘失败都应当落到这里。</summary>
        public static ControlHandoffState None => default;
    }
}
