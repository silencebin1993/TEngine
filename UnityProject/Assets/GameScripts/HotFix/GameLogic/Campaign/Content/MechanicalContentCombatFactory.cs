using System.Collections.Generic;
using ComposeEngine;
using ComposeEngine.Builtin.Catalog;
using ComposeEngine.Builtin.Modules;
using ComposeEngine.Core;

namespace GameLogic.Campaign.Content
{
    /// <summary>ER4-CONTENT-01 STORY-EXECUTION-CARDS.md 第3条："对旧 org_/gene_ 键逐一做 Facade 映射；
    /// 找不到等价实现时给当前 Story 明确新增入口，不能把旧特效换个中文名冒充。"
    ///
    /// 本类是"找不到等价实现"那几条（铸造重炮/静默标记器/标记跳转固件）的明确新增入口——只用
    /// ComposeEngine.Builtin.Modules 里已经存在、已被 130+ 条 <c>OrganelleCatalog</c>/<c>GeneCatalog</c>
    /// 条目验证过的构件（<see cref="BallisticsModule"/>/<see cref="Actuator"/>/<see cref="ChainModule"/>/
    /// <see cref="TagAttach"/>），按与既有条目完全相同的组合手法（<see cref="CompositeModule"/>）拼出新
    /// 攻击方式，不新建任何 IModule 实现类，不改 <see cref="OrganelleCatalog"/>/<see cref="GeneCatalog"/>
    /// 这两张被 130+ 条既有内容依赖、经过大量回归验证的共享字典（新增条目风险 vs. 独立新文件风险不对等，
    /// 见 ER4-CONTENT-01 证据文档"范围裁剪"一节）。
    ///
    /// 这几条新增内容目前只接到<b>独立 Engine.Fire 验证</b>（<see cref="ComposeStandalone"/>），
    /// 尚未接入 <c>CarrierCompiler</c> 正式装配链/真实战斗目标查询——那需要装配台 UI（ER4-FAC-01）、
    /// 电路板/固件编辑（ER4-PRIM-02～04）和基元正式战斗接线（ER4-PRIM-05）逐一落地后才有意义，
    /// 提前塞进共享 Catalog 只会在那些 Story 开工前产生一堆"能查到但打不出正确效果"的死内容。</summary>
    public static class MechanicalContentCombatFactory
    {
        /// <summary>comp_cannon 铸造重炮：复用 org_emitter 同款 BallisticsModule+Actuator(Projectile)
        /// 组合，Speed 系数刻意低于 org_emitter（1.3）表达"重炮弹速慢、单发威力大"的机械定位；
        /// 精确的射程/伤害/冷却/瞄准线数值需要 Luban 参数化 + CarrierCompiler 正式接线才有意义，
        /// 登记 DEBT-ER4CONTENT01-05（承接 ER4-PRIM-05/ER4-FAC-01）。</summary>
        public static IModule CreateCannonAttack() =>
            new CompositeModule("comp_cannon_attack", "铸造重炮攻击",
                new BallisticsModule(speed: 0.6f),
                new Actuator(shape: "Heavy", pattern: AttackPattern.Projectile));

        /// <summary>func_marker 静默标记器：TagAttach("Marked") + AuraModule(标记判定半径的最小近似) +
        /// Actuator(Aura)——真正的"标记主目标及6米内至多两个敌人、持续6秒"需要独立的目标选择/计时
        /// 状态机，不是一次性 Packet 变换能表达的，登记 DEBT-ER4CONTENT01-06（承接 ER6-REACT-01）。</summary>
        public static IModule CreateMarkerAttack() =>
            new CompositeModule("func_marker_attack", "静默标记器标记",
                new TagAttach("Marked"),
                new AuraModule(6f),
                new Actuator(shape: "Field", pattern: AttackPattern.Aura));

        /// <summary>fw_marktag 标记跳转固件：ChainModule(2) 复用内核已实现的连锁命中
        /// （<c>JobDamage.Chain</c>，与既有 org_synapsearc 同款字段），TagAttach("Marked") 标出"只应
        /// 跳向已标记目标"的设计意图——ChainModule 本身不支持"只跳有 Marked 标签的目标"这个过滤条件
        /// 与"每跳60%衰减"这个自定义衰减率，两者都需要新的宿主侧结算逻辑，登记 DEBT-ER4CONTENT01-06
        /// （承接 ER6-REACT-01，与静默标记器共用同一条 DEBT——两者是同一个反应的两半）。</summary>
        public static IModule CreateMarkJumpAttack() =>
            new CompositeModule("fw_marktag_attack", "标记跳转固件",
                new ChainModule(2),
                new TagAttach("Marked"),
                new Actuator(shape: "Arc", pattern: AttackPattern.Chain));

        /// <summary>独立验证入口：不经 <c>SandboxAssembler</c>（它只接受已注册进
        /// <c>OrganelleCatalog</c>/<c>GeneCatalog</c> 的 id），直接用 <see cref="Engine"/> 跑一条
        /// "EnergyCore(baseEnergy) → tailModule" 最小装配链，返回真实 <see cref="HitEvent"/>——
        /// execute_code 可直接调用本方法断言新内容确实能被 ComposeEngine 正确组装、产出结构合法的事件，
        /// 而不是只存在于数据表里。</summary>
        public static HitEvent ComposeStandalone(IModule tailModule, float baseEnergy = 10f)
        {
            var modules = new List<IModule> { new EnergyCore(baseEnergy), tailModule };
            var engine = new Engine();
            ReactionCatalog.RegisterDefaults(engine);
            FireResult result = engine.Fire(modules, new List<IContract>(), new WorldState(), seed: 1);
            return result.Events.Count > 0 ? result.Events[0] : new HitEvent();
        }
    }
}
