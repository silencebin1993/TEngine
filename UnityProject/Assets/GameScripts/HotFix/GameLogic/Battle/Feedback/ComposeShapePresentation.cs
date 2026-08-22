using ComposeEngine.Core;

namespace GameLogic.Battle.Feedback
{
    /// <summary>
    /// story-005：<see cref="GameLogic.MetabolicSlice.Carrier.CarrierCompiler"/> 链尾恒定只产
    /// Bolt（org_emitter）/Melee（org_cilia）两种 <see cref="HitEvent.Shape"/>，装不同 Module 基因后
    /// 判定字段（Damage/Scale/Count/Spin/Orbit/ExplodeOnHit）确实在变，但表现层从不读这些字段区分
    /// 几何模板，玩家看不出差异（001 R8/R4 根因）。
    ///
    /// 纯热更表现层的二次映射：只读 <see cref="HitEvent"/> 已有字段，输出一个"表现 Shape"覆盖
    /// <see cref="ComposeCastSignal.Shape"/>，供 <see cref="WhiteboxComposeProjectileFeedback"/> 选
    /// <see cref="GameLogic.MetabolicSlice.ContentCatalog.FxRecipeCatalog"/> 网格。不碰 Sim/ComposeEngine，
    /// 不新增 ComposeEngine 维度，不写回 evt 任何字段（只读纯函数）。
    /// </summary>
    public static class ComposeShapePresentation
    {
        /// <summary>
        /// Melee-tail（org_cilia）恒 Melee，不参与映射。Bolt-tail（org_emitter）按当前 HitEvent 的
        /// Spin/Orbit/ExplodeOnHit/Count 细分成 Wave/Spore/Arc，其余情形保留 Bolt。优先级固定
        /// （Preflight R4 决策表原文顺序）：Spin/Orbit 绕轨 > 命中爆炸 > 多段散射 > 保留原样。
        ///
        /// 备注：R4 决策原文举例"org_flagella → Orbit!=0"，但 org_flagella 对应的
        /// <see cref="ComposeEngine.Builtin.Modules.OrbitSpin"/> 实测写的是 Packet.Spin（自旋角速度），
        /// 全仓库没有任何 producer 会写 evt.Orbit（恒为 0，仅 Actuator 透传）——只判 Orbit 会让 Wave 分支
        /// 永远达不到，正式战斗凑不出 R10 验收③要求的"≥4 种可区分弹道"。两个字段同判，既满足决策字面
        /// 条件，也覆盖当前唯一可达的绕轨/自旋 producer。
        /// </summary>
        public static string Resolve(HitEvent evt)
        {
            if (evt.Shape != "Bolt")
            {
                return evt.Shape;
            }

            if (evt.Spin != 0f || evt.Orbit != 0f)
            {
                return "Wave";
            }
            if (evt.ExplodeOnHit)
            {
                return "Spore";
            }
            if (evt.Count > 1f)
            {
                return "Arc";
            }
            return "Bolt";
        }
    }
}
