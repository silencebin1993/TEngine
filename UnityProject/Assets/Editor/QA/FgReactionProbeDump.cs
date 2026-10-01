using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ComposeEngine.Core;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using GameLogic.MetabolicSlice.DebugTools;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-FW-03（FGR-FW-040）：把旧反应引擎的行为探针结果导出成一份中文报告（本机证据，写到 production/qa/evidence/，不入库）。
    /// 正式反应表（fg.TbReaction）的触发配对与效果照这份探针填，自检 FgReactionProbeSelfCheck 再逐条比对“表 = 探针”。
    /// </summary>
    public static class FgReactionProbeDump
    {
        [MenuItem("BinGames/QA/导出/旧反应引擎行为探针")]
        public static void Dump()
        {
            var sb = new StringBuilder();
            int code = 0;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                Write(sb);
            }
            catch (Exception e)
            {
                sb.AppendLine("异常：" + e);
                code = 1;
            }
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "production", "qa", "evidence"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "_fg2fw03-legacy-probe.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[FgReactionProbeDump] 写出 " + path + "\n" + sb);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
        }

        public static void Write(StringBuilder sb)
        {
            sb.AppendLine("== 规则层（只注册这一条规则）==");
            foreach (ReactionRule rule in LegacyReactionProbe.RuntimeRules())
            {
                LegacyReactionProbe.RuleProbe p = LegacyReactionProbe.ProbeRule(rule);
                sb.AppendLine($"{p.RuleId}\t需要[{string.Join(",", p.Required)}]\t两者={p.FiresWithBoth}\t只A={p.FiresWithOnlyFirst}\t只B={p.FiresWithOnlySecond}\t目标带B={p.FiresWhenTargetCarries}\t目标带两者={p.FiresWhenTargetCarriesBoth}\t自带[{string.Join(",", p.Inherent)}]" +
                              $"\t名={p.ReactionName}\t伤害×{p.DamageRatio:0.###}\t去[{string.Join(",", p.Removed)}]\t加[{string.Join(",", p.Added)}]\t残留[{string.Join(",", p.Residue)}]\t字段[{p.FieldChanges}]");
            }
            sb.AppendLine();
            sb.AppendLine("== 器官自带标签 ==");
            foreach (string organ in ChassisPrimitiveMatrixSmokeReport.CarrierOrgans)
            {
                sb.AppendLine($"{organ}\t[{string.Join(",", LegacyReactionProbe.OrganTags(organ))}]");
            }
            sb.AppendLine();
            sb.AppendLine("== 基因单装标签（固件表 legacyId，org_emitter）==");
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
            {
                string gene = FirmwareKinds.LegacyIdOf(row.Id);
                if (string.IsNullOrEmpty(gene) || gene == "none")
                {
                    continue;
                }
                sb.AppendLine($"{row.Id}\t{gene}\t表[{string.Join(",", FirmwareKinds.TagsOf(row.Id))}]\t实测[{string.Join(",", LegacyReactionProbe.GeneTags("org_emitter", gene))}]");
            }
            sb.AppendLine();
            sb.AppendLine("== 内容层 + 世界层（每对主标签挑第一组生产者）==");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ReactionRule rule in LegacyReactionProbe.RuntimeRules())
            {
                string[] req = rule.RequiredTags.Select(StatusTagCatalog.Canonical).ToArray();
                string key = string.Join("+", req.OrderBy(t => t, StringComparer.Ordinal));
                if (!seen.Add(key))
                {
                    continue;
                }
                sb.AppendLine($"-- 配对 {key}（首见于 {rule.Id}）");
                foreach ((string organ, string[] genes, string note) in Candidates(req))
                {
                    LegacyReactionProbe.CompiledProbe c = LegacyReactionProbe.ProbeCompiled(organ, genes);
                    sb.AppendLine($"   {organ}+{string.Join("+", genes)}（{note}）\t编译={c.Compiled}{c.Error}\t前[{string.Join(",", c.TagsBefore)}]\t触发[{string.Join(",", c.FiredRules)}]\t名={c.ReactionName}" +
                                  $"\t伤害 {c.DamageBefore:0.###}→{c.DamageAfter:0.###} ×{c.DamageRatio:0.###}\t去[{string.Join(",", c.Removed)}]\t加[{string.Join(",", c.Added)}]\t残留[{string.Join(",", c.Residue)}]" +
                                  $"\t世界 {c.WorldLossWithout:0.#}→{c.WorldLossWith:0.#} ×{c.WorldRatio:0.###}");
                }
            }
        }

        /// <summary>一对主标签的全部单标签生产者组合（固件 legacy 基因；标签来自器官的用那个器官）。</summary>
        public static List<(string Organ, string[] Genes, string Note)> Candidates(string[] canonicalPair)
        {
            var list = new List<(string, string[], string)>();
            if (canonicalPair.Length != 2)
            {
                return list;
            }
            List<(string Fw, string Gene)> a = Producers(canonicalPair[0]);
            List<(string Fw, string Gene)> b = Producers(canonicalPair[1]);
            string organA = OrganProducer(canonicalPair[0]);
            string organB = OrganProducer(canonicalPair[1]);
            if (organA != null && b.Count > 0)
            {
                list.Add((organA, new[] { b[0].Gene }, $"{canonicalPair[0]}←器官 {organA}，{canonicalPair[1]}←{b[0].Fw}"));
            }
            if (organB != null && a.Count > 0)
            {
                list.Add((organB, new[] { a[0].Gene }, $"{canonicalPair[0]}←{a[0].Fw}，{canonicalPair[1]}←器官 {organB}"));
            }
            foreach ((string fa, string ga) in a)
            {
                foreach ((string fb, string gb) in b)
                {
                    if (fa != fb)
                    {
                        list.Add(("org_emitter", new[] { ga, gb }, $"{fa}+{fb}"));
                    }
                }
            }
            return list;
        }

        public static List<(string Fw, string Gene)> Producers(string canonicalTag)
        {
            var list = new List<(string, string)>();
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
            {
                string gene = FirmwareKinds.LegacyIdOf(row.Id);
                if (string.IsNullOrEmpty(gene) || gene == "none")
                {
                    continue;
                }
                if (FirmwareKinds.TagsOf(row.Id).Any(t => StatusTagCatalog.Canonical(t) == canonicalTag))
                {
                    list.Add((row.Id, gene));
                }
            }
            return list;
        }

        public static string OrganProducer(string canonicalTag)
        {
            foreach (string organ in ChassisPrimitiveMatrixSmokeReport.CarrierOrgans)
            {
                if (LegacyReactionProbe.OrganTags(organ).Any(t => StatusTagCatalog.Canonical(t) == canonicalTag))
                {
                    return organ;
                }
            }
            return null;
        }
    }
}
