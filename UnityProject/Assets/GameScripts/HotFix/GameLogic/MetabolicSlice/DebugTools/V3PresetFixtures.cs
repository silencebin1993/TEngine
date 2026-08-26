using System.Collections.Generic;
using ComposeEngine.Core;

namespace GameLogic.MetabolicSlice.DebugTools
{
    /// <summary>
    /// organ-gene-rebalance-v3 story-008 Required 4：沙盒预设模板追加 10 组 v3 现象（原 7 组
    /// <see cref="LookDevFixtures"/> LookDev 对照保留不动，自由装配多选面板同样不受影响——本类只是
    /// 往 <c>SandboxPresetList</c> 追加按钮源，复用 <see cref="SandboxAssembler.Compose"/> 真实链
    /// （与 <see cref="Carrier.CarrierCompiler"/> 固定头尾同一口径），不手写 HitEvent）。取
    /// <see cref="EmergenceSmoke"/> 20 爽点中覆盖 5 类底盘 + 6 个新 Module（Ripple/Rhythm/Catalyst/
    /// Weave + Composite 降级的 Magnet）+ Tag 涌现管道最有代表性的 10 条，预设按钮只加载 A 侧
    /// （<see cref="BattleSandboxUIToolkit"/> 沿用既有 <c>SandboxAssembler.OverridesFromEvent(fixture.A)</c>
    /// 单值口径，B 侧留同值占位，不引入第二套面板协议）。
    /// </summary>
    public static class V3PresetFixtures
    {
        private static IReadOnlyList<LookDevFixture> _cache;

        public static IReadOnlyList<LookDevFixture> All => _cache ??= Build();

        private static List<LookDevFixture> Build()
        {
            return new List<LookDevFixture>
            {
                Make("v3① 波形器+扩散波+膨胀泡", "越扩越大的扇形潮（爽点01）", "org_wave", "gene_ripple", "gene_swell"),
                Make("v3② 渗透压场+节律+溶酶壳", "身周节拍外爆（爽点02）", "org_osmotic", "gene_rhythm", "gene_lyso"),
                Make("v3③ 菌丝锚+趋化导引", "炮台追踪弹（爽点03）", "org_mycelium", "gene_taxis"),
                Make("v3④ 酶雾腺+雷桥+编织", "坑间跳电成网（爽点05）", "org_enzyme", "gene_volt", "gene_weave"),
                Make("v3⑤ 芽殖体+群体感应+回旋外壳", "小弟打出回旋弹（爽点09）", "org_bud", "gene_swarm", "gene_return"),
                Make("v3⑥ 纤毛钻+粘液拖尾+涡旋", "冲刺留伤路并吸怪（爽点10）", "org_drill", "gene_slime", "gene_vortex"),
                Make("v3⑦ 酶雾腺+潮洼+燃径+催化", "蒸汽区更大更久（爽点13，Tag管道）", "org_enzyme", "gene_tide", "gene_pyro", "gene_catalyst"),
                Make("v3⑧ 酶雾腺+油膜+燃径", "爆燃区（爽点16，Tag管道）", "org_enzyme", "gene_oilfilm", "gene_pyro"),
                Make("v3⑨ 纤毛刺+霜膜", "任意物理命中碎裂加伤（爽点18，Tag管道）", "org_cilia", "gene_frostfilm"),
                Make("v3⑩ 分泌喷射器+磁聚+纺锤分裂", "多弹聚一点炸（爽点19）", "org_emitter", "gene_magnet", "gene_spindle"),
            };
        }

        private static LookDevFixture Make(string name, string axisLabel, string organId, params string[] geneIds)
        {
            HitEvent evt = SandboxAssembler.Compose(geneIds, new[] { organId }, default(SandboxOverrides), seed: 1);
            return new LookDevFixture(name, axisLabel, evt, evt);
        }
    }
}
