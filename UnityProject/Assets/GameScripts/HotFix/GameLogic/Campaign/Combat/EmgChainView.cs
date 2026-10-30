using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Emergence;
using GameLogic.Localization;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>一条因果链的显示行（<see cref="EmgChainView.BuildChains"/> 产出）。</summary>
    public sealed class EmgChainLine
    {
        public int Chain;
        public string Text;
        /// <summary>各节点的地面位置（x, z），从先到后；叠加层按它画连线。</summary>
        public readonly List<Vector2> Points = new List<Vector2>(8);
        public int Nodes;
        public bool DepthCut;
    }

    /// <summary>
    /// EMG1-CHAIN-01（FG19 FGR-EMG-180 / 181）：因果链与战斗中悬停的文字。只在玩家打开因果链叠加或悬停时调用：读内核环 O(环长)，不按帧读、不读全部单位。
    /// - 链文字：“由 A 车（漏油）出手 → 命中时放出第 1 代子弹药 → 规则：标记跳转（B）→ 链深已达上限”，全部走文本键；同一链上连续相同的节点聚合（“规则：标记跳转 ×200”）。
    /// - 悬停三段（是什么 / 谁造成的 / 接下来会怎样）：残骸、子弹药、破片（阶段 1 部分）；带程序的己方机器另附程序（FG-GAP-171）。
    /// </summary>
    public static class EmgChainView
    {
        private static readonly List<EmgChainRec> Scratch = new List<EmgChainRec>(EmgConst.ChainRing);
        private static readonly List<int> Chains = new List<int>(8);

        /// <summary>最近 <paramref name="maxChains"/> 条链（按最近一次有记录的先后），每条一行。返回条数。</summary>
        public static int BuildChains(CombatSite site, CampaignState state, int maxChains, List<EmgChainLine> into)
        {
            into.Clear();
            if (site == null || site.IsDisposed || maxChains <= 0)
            {
                return 0;
            }
            CombatKernel k = site.Kernel;
            int n = k.EmgChainRecordCount;
            Chains.Clear();
            for (int i = 0; i < n && Chains.Count < maxChains; i++)
            {
                if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Chain > 0 && !Chains.Contains(r.Chain))
                {
                    Chains.Add(r.Chain);
                }
            }
            foreach (int chain in Chains)
            {
                Scratch.Clear();
                for (int i = n - 1; i >= 0; i--)
                {
                    if (k.TryGetEmgChainRecent(i, out EmgChainRec r) && r.Chain == chain)
                    {
                        Scratch.Add(r);
                    }
                }
                into.Add(Line(site, state, chain, Scratch));
            }
            return into.Count;
        }

        /// <summary>一条链的文字（节点从先到后；连续相同的节点聚合计数）。</summary>
        public static EmgChainLine Line(CombatSite site, CampaignState state, int chain, List<EmgChainRec> recs)
        {
            var line = new EmgChainLine { Chain = chain };
            var sb = new StringBuilder(96);
            string sep = GameText.Get("emg.chain.sep");
            for (int i = 0; i < recs.Count; i++)
            {
                EmgChainRec r = recs[i];
                int same = 1;
                while (i + 1 < recs.Count && recs[i + 1].Kind == r.Kind && recs[i + 1].Key == r.Key && recs[i + 1].SrcUnit == r.SrcUnit)
                {
                    same++;
                    i++;
                }
                if (sb.Length > 0)
                {
                    sb.Append(sep);
                }
                sb.Append(NodeText(site, state, r, same));
                line.Points.Add(new Vector2((float)r.Pos.x, (float)r.Pos.y));
                line.Nodes++;
                line.DepthCut |= r.Kind == EmgChainNode.DepthCut;
            }
            line.Text = sb.ToString();
            return line;
        }

        /// <summary>一个节点的文字（<paramref name="count"/> &gt; 1 = 聚合）。</summary>
        public static string NodeText(CombatSite site, CampaignState state, in EmgChainRec r, int count)
        {
            string text;
            switch (r.Kind)
            {
                case EmgChainNode.Shot:
                {
                    string fw = ProgramFirmware(site, r.SrcUnit);
                    text = string.IsNullOrEmpty(fw)
                        ? GameText.Format("emg.chain.node.shot_bare", PartyName(site, state, r.SrcUnit))
                        : GameText.Format("emg.chain.node.shot", PartyName(site, state, r.SrcUnit), fw);
                    break;
                }
                case EmgChainNode.Inject:
                    text = GameText.Format("emg.chain.node.inject", PartyName(site, state, r.SrcUnit), EmgProgramTreeText.EventName((EmgEventKind)r.Key));
                    break;
                case EmgChainNode.Trigger:
                    text = GameText.Format("emg.chain.node.trigger", EmgProgramTreeText.EventName((EmgEventKind)r.Key), r.Aux.ToString(CultureInfo.InvariantCulture));
                    break;
                case EmgChainNode.Rule:
                {
                    string rid = NamedReactionCatalog.IdOfKey(r.Key);
                    string name = (rid != null ? NamedReactionCatalog.NameOf(rid) : null) ?? GameText.Get("emg.tree.unknown");
                    return count > 1
                        ? GameText.Format("emg.chain.node.rule_count", name, count.ToString(CultureInfo.InvariantCulture))
                        : GameText.Format("emg.chain.node.rule", name, PartyName(site, state, r.Other));
                }
                case EmgChainNode.Wreck:
                    text = GameText.Format("emg.chain.node.wreck", KindName(r.Aux), PartyName(site, state, r.Other));
                    break;
                case EmgChainNode.Fragment:
                    text = GameText.Format("emg.chain.node.fragment", PartyName(site, state, r.SrcUnit), r.Aux.ToString(CultureInfo.InvariantCulture));
                    break;
                case EmgChainNode.DepthCut:
                    text = GameText.Get("emg.chain.node.depthcut");
                    break;
                case EmgChainNode.Relay:
                    text = GameText.Get("emg.chain.node.relay");
                    break;
                case EmgChainNode.Doctrine:
                    text = GameText.Get("emg.chain.node.doctrine");
                    break;
                case EmgChainNode.Merge:
                    text = GameText.Format("emg.chain.node.merge", r.Chain.ToString(CultureInfo.InvariantCulture), r.Parent.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    text = GameText.Get("emg.tree.unknown");
                    break;
            }
            return count > 1 ? GameText.Format("emg.chain.counted", text, count.ToString(CultureInfo.InvariantCulture)) : text;
        }

        /// <summary>单位名（机器 = “A 车”这类编号名；敌人 = 内容名；查不到 = “不明来源”）。</summary>
        public static string PartyName(CombatSite site, CampaignState state, int unitId)
        {
            if (site == null || site.IsDisposed)
            {
                return GameText.Get("emg.chain.party.unknown");
            }
            ReactionLogParty p = ReactionFeedback.Party(site, state, unitId);
            return p.Known ? ReactionLog.PartyLabel(p) : GameText.Get("emg.chain.party.unknown");
        }

        /// <summary>来源单位种类名（残骸“是什么”）。</summary>
        public static string KindName(byte kind)
        {
            switch ((CombatUnitKind)kind)
            {
                case CombatUnitKind.Machine: return GameText.Get("emg.hover.kind.machine");
                case CombatUnitKind.Enemy: return GameText.Get("emg.hover.kind.enemy");
                case CombatUnitKind.Turret: return GameText.Get("emg.hover.kind.turret");
                default: return GameText.Get("emg.hover.kind.other");
            }
        }

        /// <summary>出手的机器此刻程序里的固件名（“漏油、燃迹”）；不是机器或没有程序返回空串。</summary>
        public static string ProgramFirmware(CombatSite site, int unitId)
        {
            if (site == null || !site.TryGetMachineOfUnit(unitId, out int logicId) || !site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info) || info.Program == null)
            {
                return string.Empty;
            }
            var names = new List<string>(4);
            foreach (EmgProgramStep s in info.Program.Steps)
            {
                string name = EmgProgramTreeText.FirmwareName(s.FirmwareId);
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
            return string.Join(GameText.Get("emg.chain.list_sep"), names);
        }

        private static string Owners(CombatSite site, CampaignState state, int4 o)
        {
            var names = new List<string>(4);
            for (int q = 0; q < 4; q++)
            {
                if (o[q] != 0)
                {
                    names.Add(PartyName(site, state, o[q]));
                }
            }
            return names.Count > 0 ? string.Join(GameText.Get("emg.chain.list_sep"), names) : GameText.Get("emg.chain.party.unknown");
        }

        private static string Three(string what, string who, string next) =>
            GameText.Format("emg.hover.label.what", what) + "\n" + GameText.Format("emg.hover.label.who", who) + "\n" + GameText.Format("emg.hover.label.next", next);

        private static string F0(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        /// <summary>残骸悬停（第 <paramref name="index"/> 块）：是什么（来源种类、耐久、质量）、谁造成的（击毁者 + 打碎过它的单位，最多 4 个）、接下来（挡弹 / 减速 / 崩破片门槛 / 剩余秒数）。</summary>
        public static TooltipContent WreckContent(CombatSite site, CampaignState state, int index)
        {
            if (site == null || site.IsDisposed || !site.Kernel.TryGetWreck(index, out EmgWreck w))
            {
                return null;
            }
            CombatKernel k = site.Kernel;
            string what = GameText.Format("emg.hover.wreck.what", KindName(w.SrcKind), F0(w.Hp), F0(w.MaxHp), F0(w.Mass));
            string who = GameText.Format("emg.hover.wreck.who", Owners(site, state, k.EmgWreckOwners(w.Id)));
            float threshold = k.EntityParams.FragThreshold;
            double left = w.Until - k.Time;
            string next = double.IsInfinity(w.Until) || left > 1e6
                ? GameText.Format("emg.hover.wreck.next_persist", F0(threshold))
                : GameText.Format("emg.hover.wreck.next", F0(threshold), F0((float)System.Math.Max(0, left)));
            return new TooltipContent { Title = GameText.Get("emg.hover.wreck.title"), Body = Three(what, who, next) };
        }

        /// <summary>子弹药 / 破片悬停（弹体下标 <paramref name="index"/>）。不是子弹药 / 破片返回 null。</summary>
        public static TooltipContent ProjectileContent(CombatSite site, CampaignState state, int index)
        {
            if (site == null || site.IsDisposed || !site.Kernel.TryGetProjectile(index, out CombatProjectile p))
            {
                return null;
            }
            string owner = PartyName(site, state, p.Owner);
            if ((p.Flags & EmgProjFlags.Fragment) != 0)
            {
                return new TooltipContent
                {
                    Title = GameText.Get("emg.hover.frag.title"),
                    Body = Three(GameText.Format("emg.hover.frag.what", F0(p.Damage)), GameText.Format("emg.hover.frag.who", owner), GameText.Format("emg.hover.frag.next", F0(p.Life))),
                };
            }
            if ((p.Flags & EmgProjFlags.Sub) == 0)
            {
                return null;
            }
            string who = GameText.Format("emg.hover.sub.who", owner);
            int4 chainOwners = site.Kernel.EmgChainOwners(p.Chain);
            if (chainOwners.y != 0)
            {
                who += GameText.Get("emg.chain.clause_sep") + GameText.Format("emg.hover.chain_owners", Owners(site, state, chainOwners)); // 跨机结果：链上的各台机器
            }
            string next = GameText.Format(p.Depth >= EmgConst.MaxDepth ? "emg.hover.sub.next_cut" : "emg.hover.sub.next", F0(p.Life));
            return new TooltipContent
            {
                Title = GameText.Format("emg.hover.sub.title", p.Gen.ToString(CultureInfo.InvariantCulture)),
                Body = Three(GameText.Format("emg.hover.sub.what", F0(p.Damage), p.Depth.ToString(CultureInfo.InvariantCulture)), who, next),
            };
        }

        /// <summary>
        /// FG-GAP-171：带程序的己方机器悬停附它当前的程序树与编译原因（复用程序树预览 <see cref="EmgProgramTreeText.BuildRows"/>，同一套行与原因文字）。
        /// 程序登记进本地点程序表被拒（表满）时标题写“未生效、只用基础攻击”并列出原因，不当成在运行的程序。不是机器 / 空程序返回 null。
        /// </summary>
        public static string MachineProgramLine(CombatSite site, int unitId)
        {
            if (site == null || !site.TryGetMachineOfUnit(unitId, out int logicId) || !site.TryGetMachineWeapon(logicId, out MachineWeaponInfo info))
            {
                return null;
            }
            return ProgramHoverText(info.Program, info.ProgramNoteKey);
        }

        private static readonly List<EmgTreeRow> HoverRows = new List<EmgTreeRow>(16);

        /// <summary>程序悬停正文（标题行 + 程序树行，按代缩进）。<paramref name="noteKey"/> = 战斗地点给出的登记原因（目前只有表满）。空程序返回 null。</summary>
        public static string ProgramHoverText(EmgProgramResult program, string noteKey)
        {
            if (program == null || program.IsEmpty || program.Steps.Count == 0)
            {
                return null;
            }
            bool rejected = noteKey == EmgProgramCompiler.NoteTableFull;
            EmgProgramTreeText.BuildRows(program, HoverRows, null, rejected);
            var sb = new System.Text.StringBuilder(256);
            sb.Append(GameText.Get(rejected ? "emg.hover.program_rejected" : "emg.hover.program"));
            foreach (EmgTreeRow row in HoverRows)
            {
                sb.Append('\n').Append(' ', 2 * System.Math.Max(0, row.Depth)).Append(row.Text);
            }
            return sb.ToString();
        }
    }
}
