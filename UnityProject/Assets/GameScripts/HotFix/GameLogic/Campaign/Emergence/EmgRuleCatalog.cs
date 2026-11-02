using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>
    /// EMG2-RULE-01（FG19 FGR-EMG-084 / 085；ADR-EMG-001 ⑥）：把 fg.TbChannelRule 编成内核规则 <see cref="EmgRule"/>。只在地点载入 / 表重载时编译（O(规则数)），不按帧。
    /// 内核只拿到端口码 / 物质码 / 阈值 / 产物码与几个结算方式码，不拿规则名与任何内容 ID（FGR-EMG-013）；规则名只用于计数稳定键与文本。
    /// 超过 emg.rule.capacity（≤ 256）或有不认识的码时整表拒绝（Problems 非空，内核保留原表）。
    /// </summary>
    public static class EmgRuleCatalog
    {
        public sealed class Compiled
        {
            public readonly List<EmgRule> Rules = new List<EmgRule>();
            public readonly List<string> Ids = new List<string>();
            public readonly Dictionary<int, string> TextByKey = new Dictionary<int, string>();
            public readonly List<string> Problems = new List<string>();
            /// <summary>EMG2-REACT-01：带开火者代价的那一行（FG19B §0.4：全表只有 P03 填 cost.heat）——代价、条件阈值（注热 ÷ 300、动能）、计入的图鉴条目。没有 = CostHeat 0。</summary>
            public float CostHeat, CostC1, CostC2;
            public string CostNamed;
            public bool Ok => Problems.Count == 0;
        }

        private static Compiled _current;

        public static Compiled Current => _current ??= Compile(Load(), EmgRuleConst.Capacity);

        public static void Reload() => _current = null;

        private static IReadOnlyList<GameConfig.fg.ChannelRule> Load()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbChannelRule?.DataList;
            }
            catch (Exception e)
            {
                Log.Error("[EmgRuleCatalog] 读取 fg.TbChannelRule 失败：" + e.Message);
                return null;
            }
        }

        private static readonly string[] Pairs = { "", "matter_matter", "matter_energy", "event_matter", "coat_matter", "phase_energy", "prop_field", "matter_entity" };
        private static readonly string[] Conds = { "", "overlap", "overlap_dry", "temp_ge", "temp_lt", "temp_ge_conc_lt", "temp_ge_conc_ge", "temp_le_for", "temp_gt_for", "charge_ge",
            "charge_ge_conc_ge", "kinetic_ge", "expire", "expire_temp_lt", "overheat_in",
            "inject_ge", "coats", "same_hit" }; // EMG2-REACT-01：单位层沾染行与 P03（与内核 EmgRuleConst.Cond* 同序，只追加）
        private static readonly string[] Ops = { "deferred", "convert", "moveover", "dilute", "douse", "erode", "split", "spawn", "burst", "arc", "fragment", "vent",
            "coatspawn", "coatarc", "stick" }; // EMG2-REACT-01：单位层沾染行（与内核 EmgRuleConst.Op* 同序，只追加）

        /// <summary>
        /// 编译。<paramref name="capacity"/> = emg.rule.capacity（夹到 ≤ 256）；行数超过它整表拒绝（卡片负向“规则超 256 行被表校验拒绝”）。
        /// deferred 行（承接卡接上结算）照样编进来、占容量、过校验，内核不结算。
        /// </summary>
        public static Compiled Compile(IReadOnlyList<GameConfig.fg.ChannelRule> rows, int capacity)
        {
            var c = new Compiled();
            capacity = Math.Min(Math.Max(1, capacity), EmgRuleConst.Capacity);
            if (rows == null)
            {
                c.Problems.Add("fg.TbChannelRule 没有数据");
                return c;
            }
            if (rows.Count > capacity)
            {
                c.Problems.Add($"fg.TbChannelRule {rows.Count} 行超过容量 emg.rule.capacity = {capacity}，整表拒绝");
                return c;
            }
            EmgCompiled ports = EmgCatalog.Current;
            foreach (GameConfig.fg.ChannelRule r in rows)
            {
                if (r == null)
                {
                    continue;
                }
                int pair = Array.IndexOf(Pairs, r.PairKind);
                int op = Array.IndexOf(Ops, r.Op);
                int cond = Array.IndexOf(Conds, r.Cond);
                int main = "STBP".IndexOf(r.MainKind ?? "?", StringComparison.Ordinal) + 1;
                if (pair <= 0 || op < 0 || main <= 0 || (op != EmgRuleConst.OpDeferred && cond <= 0))
                {
                    c.Problems.Add($"{r.Id}：pairKind / op / cond / mainKind 不认识（{r.PairKind} / {r.Op} / {r.Cond} / {r.MainKind}）");
                    continue;
                }
                if (!TryCode(ports, r.A, out ushort a) || !TryCode(ports, r.B, out ushort b))
                {
                    c.Problems.Add($"{r.Id}：端口 {r.A} / {r.B} 未登记");
                    continue;
                }
                byte product = ProductMatter(r.Product);
                byte by = r.ByProduct == "none" ? (byte)0 : EmgMatterConst.MatterOfPort(PortCode(ports, r.ByProduct));
                if (op != EmgRuleConst.OpDeferred && (main == EmgRuleConst.MainS || main == EmgRuleConst.MainT) && product == 0)
                {
                    c.Problems.Add($"{r.Id}：S / T 类结算方式 {r.Op} 的产物 {r.Product} 不是物质");
                    continue;
                }
                if (r.Side == "side.mult" && r.SideValue > 1.25f + 1e-4f)
                {
                    c.Problems.Add($"{r.Id}：附带倍率 {r.SideValue} 超过 1.25（FGR-EMG-085）");
                    continue;
                }
                int key = NamedReactionCatalog.StableKey(r.Named != "none" ? r.Named : r.Id);
                c.Rules.Add(new EmgRule
                {
                    Key = key, IdKey = NamedReactionCatalog.StableKey(r.Id), Pair = (byte)pair, Cond = (byte)Math.Max(0, cond), Op = (byte)op, Main = (byte)main,
                    A = a, B = b, C1 = r.C1, C2 = r.C2, P1 = r.P1, P2 = r.P2, P3 = r.P3, P4 = r.P4, Life = r.Life, LifeMul = r.LifeMul, Product = product, By = by,
                    ByAmount = r.ByAmount, ByTemp = r.ByTemp, ByLife = r.ByLife, ConsA = r.ConsumeA, ConsB = r.ConsumeB,
                    Side = r.Side == "side.damage" ? EmgRuleConst.SideDamage : r.Side == "side.mult" ? EmgRuleConst.SideMult : EmgRuleConst.SideNone, SideValue = r.SideValue,
                    Priority = (short)r.Priority, Stage = (byte)r.Stage,
                });
                c.Ids.Add(r.Id);
                if (r.CostHeat > 0f && c.CostHeat <= 0f)
                {
                    c.CostHeat = r.CostHeat;
                    c.CostC1 = r.C1;
                    c.CostC2 = r.C2;
                    c.CostNamed = r.Named != "none" ? r.Named : r.Id;
                }
                c.TextByKey[NamedReactionCatalog.StableKey(r.Id)] = r.TextKey;
                if (!c.TextByKey.ContainsKey(key))
                {
                    c.TextByKey[key] = r.TextKey;
                }
            }
            return c;
        }

        /// <summary>
        /// EMG2-REACT-01（FG19B §4.1 A2，FGR-EMG-016 / 021）：带开火者代价的规则行（P03 熔穿过载）“在开火时可判定成立”——主组件是重型射弹载体（作战组件表 carrier = projectile、
        /// 细分 = 重型），且生效固件里有写 en.heat 注入的声明。成立时这些固件的表值热量被代价取代：<paramref name="replaced"/> = 被取代的表值热量之和，<paramref name="cost"/> = 代价。
        /// 只按载体类别与端口判定，不认组件 / 固件 ID（取代旧“铸造重炮 + 过载”配对）。
        /// </summary>
        public static bool CostRuleApplies(string primaryId, IEnumerable<string> firmwareIds, out float replaced, out float cost)
        {
            replaced = 0f;
            cost = Current.CostHeat;
            if (!(cost > 0f) || firmwareIds == null || !IsHeavyProjectile(primaryId))
            {
                return false;
            }
            float injected = 0f;
            var one = new string[1];
            foreach (string id in Signal.FirmwareKinds.ExpandMixed(firmwareIds))
            {
                one[0] = id;
                float amt = string.IsNullOrEmpty(id) ? 0f : EmgCatalog.PartWriteAmount("en.heat", EmgRole.Inject, one);
                if (amt > 0f)
                {
                    replaced += Signal.FirmwareKinds.HeatOf(id);
                    injected += amt;
                }
            }
            // P03 c1：同一次命中注热 ≥ c1 × 结构积热换算（0.15 × 300 = 45，与内核 EmgMeltOverloadHit 同一阈值与容差）；注热不够这行开火时就不成立、不收代价。
            // shortcut: 动能 c2（≥ 40）要看命中伤害，开火时不判，命中时由内核判；重型射弹单发动能远高于 40，以后有轻量重型射弹时再按组件伤害补判。
            float scale = Math.Max(1f, Combat.CombatSite.Tuning("emg.heat.unit_overheat_scale", EmgRuleConst.DefaultUnitOverheatScale));
            if (injected <= 0f || injected + 1e-3f < Current.CostC1 * scale)
            {
                replaced = 0f;
                return false;
            }
            return true;
        }

        /// <summary>主组件是重型射弹载体（作战组件表 carrier = projectile、细分 = 重型）。</summary>
        public static bool IsHeavyProjectile(string primaryId) =>
            CarrierReadings.TryGetComponent(primaryId, out GameConfig.fg.CombatComponent comp) && comp.Carrier == "projectile" && comp.SubtypeKey == HeavyProjectileSubtype;

        /// <summary>重型射弹载体的细分（作战组件表 subtypeKey；FG19B §6.3“重型射弹（重炮）”）。</summary>
        public const string HeavyProjectileSubtype = "component.subtype.heavy";

        /// <summary>
        /// EMG2-REACT-01（取代 BlueprintCircuitCompiler.DetectReactionId 与 MechanicalReactionCatalog 的 ID 配对，ADR ⑪）：这套装配按端口能判定成立的具名反应——
        /// ① 带开火者代价的规则行在开火时成立（<see cref="CostRuleApplies"/>）→ 该行计入的图鉴条目（熔穿过载）；
        /// ② 有固件“以 evt.marked 为条件、写 grm.mod.chain”（只跳已标记的连锁，FG19A fw_marktag 的端口形状）→ 标记跳转（没有规则行，FGR-EMG-084）。都没有返回 null。
        /// </summary>
        public static string AssemblyReactionId(string primaryId, IEnumerable<string> firmwareIdsIn)
        {
            List<string> ids = Signal.FirmwareKinds.ExpandMixed((firmwareIdsIn ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)));
            if (CostRuleApplies(primaryId, ids, out _, out _))
            {
                return Current.CostNamed;
            }
            foreach (string id in ids)
            {
                var one = new[] { id };
                if (EmgCatalog.PartReads("evt.marked", EmgRole.Condition, one) && EmgCatalog.PartWriteAmount("grm.mod.chain", EmgRole.Modify, one) > 0f)
                {
                    return Content.MechanicalReactionCatalog.ReactionMarkJumpId;
                }
            }
            return null;
        }

        /// <summary>规则计数键（named 或 id 的稳定键）→ 说明文本（因果链“规则”节点、悬停用）；查不到返回 null。</summary>
        public static string TextOfKey(int key) =>
            key != 0 && Current.TextByKey.TryGetValue(key, out string tk) && !string.IsNullOrEmpty(tk) ? GameText.Get(tk) : null;

        private static bool TryCode(EmgCompiled ports, string id, out ushort code)
        {
            code = 0;
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            if (id.StartsWith("coat:", StringComparison.Ordinal) && int.TryParse(id.Substring(5), out int bit) && bit >= 0 && bit < 32)
            {
                code = (ushort)(EmgRuleConst.CoatBase + bit);
                return true;
            }
            code = PortCode(ports, id);
            return code != 0;
        }

        private static ushort PortCode(EmgCompiled ports, string id) =>
            ports != null && id != null && ports.PortIndexById.TryGetValue(id, out int i) && i >= 0 && i < ports.Ports.Length ? ports.Ports[i].Code : (ushort)0;

        /// <summary>产物 → 物质码：mat.* 按端口码 − 400；ter.block = 固化胶块（FG19B M16 产物落地为 mat.cured 固态斑块）；其余（topo / bhv / ter.mark / ter.bridge）0。</summary>
        private static byte ProductMatter(string product)
        {
            if (string.IsNullOrEmpty(product))
            {
                return 0;
            }
            if (product == "ter.block")
            {
                return EmgMatterConst.Cured;
            }
            if (!product.StartsWith("mat.", StringComparison.Ordinal))
            {
                return 0;
            }
            return EmgMatterConst.MatterOfPort(PortCode(EmgCatalog.Current, product));
        }
    }
}
