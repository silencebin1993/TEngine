using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Sim.Combat;
using FwRow = GameConfig.fg.FirmwareKind;
using LocText = GameConfig.fg.LocText;
using StatusTag = GameConfig.fg.StatusTag;
using TbFirmwareKind = GameConfig.fg.TbFirmwareKind;
using TbRemovedContent = GameConfig.fg.TbRemovedContent;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.Settings;
using Luban;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-FW-01 44 条固件数据迁移的自动验收（FG02 FGR-FW-001～003、FGR-FW-030；FGT-FW-002（部分）、FGT-FW-008；卡片负向“旧存档里的固件实例按新表显示”）。
    /// 全部读正式表（fg.TbFirmwareKind / fg.TbStatusTag / fg.TbLocText），起真实系统——行为坏了会失败：
    /// A 源数据 = 运行时表（逐字段）；B 名表 = 设计案 5.4（名称 / 类别 / 种类 / 旧 ID 与 42 条旧基因一一对应）；C 目录由表生成（改表 → 目录跟着变、切语言、表坏了）；
    /// D 机器电路装配（FGT-FW-002：6 条核心装不进机器与炮塔；38 条常规真实装配 + 保存 + 编译（FG2-FW-02 起含装甲击穿）；未解锁 / 未破解的拒绝与原因）；
    /// E 接入口插入（44 条都能插、路径数不变 → DEBT-FG1SIG02-02 关闭；形变按形变列）；F 热量与能耗（预览 = 蓝图版本 = 内核重炮积热；双态预览能耗行，FG-GAP-028）；
    /// G 状态标签（表 = FG02 3.4；等价实现模块真实贴上的标签 = 表；编译产出的标签都能叫出名字或是反应产物）；
    /// H 旧存档按新表显示（真实文件存读档、换语言名字跟着表走；表里退役一条固件时的对账；表坏了不把固件当已移除内容转废料）；
    /// I 题材审计扩展到全部 FG 文本键（FGT-FW-008）；J 负向；K 暂停与倍速；L 性能。已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgFirmwareMigrationSelfCheck
    {
        private const int Slot = 0;

        /// <summary>英文玩家文本里的旧生物题材词（中文词表见 <see cref="ThemeLexicon"/>）。“cell”在英文里是地图格子，不算。</summary>
        internal static readonly Regex EnglishBioWords = new Regex(
            @"\b(genes?|genetic|organs?|organelles?|metabolism|metabolic|phagocyt\w*|biomass|mitochondri\w*|chloroplasts?|spores?|mycel\w*|cilia|pseudopod\w*|flagell\w*|hemoly\w*)\b",
            RegexOptions.IgnoreCase);

        /// <summary>内部 ID 形态：旧基因 / 旧器官 / 固件 / 组件内容 ID 漏进玩家文本。</summary>
        internal static readonly Regex InternalContentId = new Regex(@"\b(gene|org|organ|fw|comp|func|struct)_[a-z]");

        /// <summary>旧引擎 19 条反应的产物标签（反应本身的命名归 FG2-FW-03，不在状态标签表里）。</summary>
        private static readonly string[] ReactionProducts =
        {
            "Steam", "Deflagrate", "Conduct", "Shatter", "Sticky", "Electrolysis", "Insulate", "ThermalShock", "CausticBurn",
            "Hemolysis", "Syrup", "Dissolve", "Annihilate", "Vitrify", "Mudify", "Sepsis",
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG 44 条固件数据迁移")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[固件迁移] 44 条固件数据迁移（FG2-FW-01）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfw01-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                SaveContentReconciler.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "热更层在 Editor 下是 Mono JIT，真机走 HybridCLR 解释执行（数字只作量级参考，真机复测归 FG15-SYS-02）");

                Step(CheckSourceParity);
                Step(CheckDesignRoster);
                Step(CheckCatalogFromTable);
                Step(CheckMachineCircuit);
                Step(CheckUplinkAndPaths);
                Step(CheckHeatAndPower);
                Step(CheckStatusTags);
                Step(CheckSaveCompat);
                Step(CheckTextAudit);
                Step(CheckNegatives);
                Step(CheckPauseSpeed);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"固件迁移自检抛异常：{e}");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
                FirmwareCatalog.Invalidate();
                SaveContentReconciler.ResetForTests();
                SignalCoreService.ExpeditionUnderwayOverrideForTests = null;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            Line($"  · [固件迁移] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 源数据 = 运行时表 ───────────────────────────────────────────────────

        private static void CheckSourceParity()
        {
            Line("  · A. 源数据 fgdata_firmware.py 与运行时 fg.TbFirmwareKind / fg.TbStatusTag 逐字段一致（改了源却没重新生成会失败）");
            (int code, string output) = RunPython(LocateRepo(), "tools/cell_tables/fgdata.py --dump");
            var fk = new List<string[]>();
            var st = new List<string[]>();
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                string[] f = raw.Split('\t');
                if (f[0] == "FK")
                {
                    fk.Add(f);
                }
                else if (f[0] == "ST")
                {
                    st.Add(f);
                }
            }
            IReadOnlyList<FwRow> rows = FirmwareKinds.Rows;
            var diffs = new List<string>();
            foreach (string[] f in fk)
            {
                FwRow row = rows.FirstOrDefault(r => r.Id == f[1]);
                string[] mine = row == null ? null : Fields(row);
                if (mine == null || f.Length - 1 != mine.Length)
                {
                    diffs.Add(f[1] + "（缺行或列数不同）");
                    continue;
                }
                for (int i = 0; i < mine.Length; i++)
                {
                    if (!SameValue(f[i + 1], mine[i]))
                    {
                        diffs.Add($"{f[1]}.第{i + 1}列 源={f[i + 1]} 表={mine[i]}");
                    }
                }
            }
            Expect(code == 0 && fk.Count == FirmwareCatalog.ExpectedCount && rows.Count == FirmwareCatalog.ExpectedCount && diffs.Count == 0,
                $"fg.TbFirmwareKind {rows.Count} 行 × 26 列与源 {fk.Count} 行逐字段一致{Detail(diffs)}");

            IReadOnlyList<StatusTag> tags = StatusTagCatalog.Rows;
            var tdiff = st.Where(f => f.Length < 7 || tags.All(t => !(t.Id == f[1] && t.NameKey == f[2] && t.AliasOf == f[3] && t.Shape == f[4] && t.Color == f[5] && t.Kind == f[6])))
                .Select(f => f[1]).ToList();
            Expect(StatusTagCatalog.LoadError == null && st.Count > 0 && st.Count == tags.Count && tdiff.Count == 0,
                $"fg.TbStatusTag {tags.Count} 行与源 {st.Count} 行逐字段一致{Detail(tdiff)}");
        }

        private static string[] Fields(FwRow r) => new[]
        {
            r.Id, r.Kind, r.NameKey, F(r.Cooldown), r.Protocol, r.Faction, r.Category, r.LegacyId, r.DescKey, r.Rarity,
            r.Load.ToString(CultureInfo.InvariantCulture), r.Power.ToString(CultureInfo.InvariantCulture), F(r.Heat),
            r.ReadProjectileKey, r.ReadMeleeKey, r.ReadSummonKey, r.ReadAuraKey, r.ReadFieldKey,
            r.Tags, r.Morph, r.Icon, r.Source, r.AcquireKey, r.Cracked ? "True" : "False", r.Scrap.ToString(CultureInfo.InvariantCulture),
            r.ReadFields, // FG2-FW-02：读法字段
        };

        private static string F(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);

        private static bool SameValue(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return true;
            }
            return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                   && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && Math.Abs(x - y) < 1e-5;
        }

        // ── B. 名表 = 设计案 5.4 ──────────────────────────────────────────────────

        private static void CheckDesignRoster()
        {
            Line("  · B. 名表以设计案 5.4 为准（FGR-FW-002）：44 行名称 / 类别 / 种类逐行对上；旧 ID 与旧引擎 42 条基因一一对应；类别 17 / 10 / 9 / 8；核心 6 条");
            string design = ReadDoc("TEngine/UnityProject/DesignDocs/ProjectA_FullGame_Design.md");
            string sec = DesignDocsAuditSelfCheck.Section(design ?? string.Empty, "### 5.4") ?? string.Empty;
            List<string> table = DesignDocsAuditSelfCheck.Table(sec, "| 旧 ID | 新名 |").Skip(2).ToList();
            var catMap = new Dictionary<string, string> { ["引信与弹芯"] = "fuse", ["限制器与击发逻辑"] = "limiter", ["流体改道"] = "fluid", ["电磁场控"] = "em" };
            var problems = new List<string>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in table)
            {
                string[] c = line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                if (c.Length < 5)
                {
                    problems.Add("表格行格式不对：" + line);
                    continue;
                }
                string legacy = c[0];
                string name = c[1].Replace("**(已定)**", string.Empty).Trim();
                FwRow row = legacy.StartsWith("gene_", StringComparison.Ordinal)
                    ? FirmwareKinds.Rows.FirstOrDefault(r => r.LegacyId == legacy)
                    : FirmwareKinds.Rows.FirstOrDefault(r => r.LegacyId == "none" && GameText.Get(r.NameKey, GameLanguage.ZhCn) == name);
                if (row == null)
                {
                    problems.Add($"设计案的 {legacy} {name} 在表里没有对应行");
                    continue;
                }
                seenIds.Add(row.Id);
                string zh = GameText.Get(row.NameKey, GameLanguage.ZhCn);
                if (zh != name) problems.Add($"{row.Id} 名称 {zh} ≠ 设计案 {name}");
                if (!catMap.TryGetValue(c[2], out string cat) || cat != row.Category) problems.Add($"{row.Id} 类别 {row.Category} ≠ 设计案 {c[2]}");
                bool core = c[3].Contains("核心");
                if (core != (row.Kind == "core")) problems.Add($"{row.Id} 种类 {row.Kind} ≠ 设计案 {c[3]}");
            }
            Expect(design != null && table.Count == FirmwareCatalog.ExpectedCount && seenIds.Count == FirmwareCatalog.ExpectedCount && problems.Count == 0,
                $"设计案 5.4 名表 {table.Count} 行与 fg.TbFirmwareKind 逐行一致（名称走文本键）{Detail(problems)}");

            var legacySet = new HashSet<string>(FirmwareKinds.Rows.Where(r => r.LegacyId != "none").Select(r => r.LegacyId), StringComparer.Ordinal);
            var geneSet = new HashSet<string>(GeneCatalog.AllModuleIds, StringComparer.Ordinal);
            var newBuilt = FirmwareKinds.Rows.Where(r => r.LegacyId == "none").Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Expect(legacySet.SetEquals(geneSet) && legacySet.Count == 42 && !GeneCatalog.AllIds.Any()
                   && newBuilt.SequenceEqual(new[] { FirmwareCatalog.FwArmorPierceId, FirmwareCatalog.FwMarkTagId }),
                $"旧 ID 列 = 旧引擎全部 {geneSet.Count} 条基因（一条不漏、一条不重）；只有设计案标“新建”的装甲击穿、标记跳转没有旧 ID" +
                Detail(geneSet.Except(legacySet).Select(g => "漏迁 " + g).Concat(legacySet.Except(geneSet).Select(g => "表里多了 " + g)).ToList()));

            var counts = FirmwareKinds.Rows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count());
            var coreIds = FirmwareKinds.Rows.Where(r => r.Kind == "core").Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Expect(counts.TryGetValue("fuse", out int a) && a == 17 && counts.TryGetValue("limiter", out int b) && b == 10
                   && counts.TryGetValue("fluid", out int c2) && c2 == 9 && counts.TryGetValue("em", out int d) && d == 8
                   && coreIds.SequenceEqual(new[] { "fw_amplify", "fw_capacitor", "fw_execute", "fw_marktag", "fw_overload", "fw_swarm" })
                   && FirmwareKinds.Rows.Where(r => r.Kind == "core").All(r => r.Category == "limiter" && r.Rarity == "epic"),
                "类别条数 引信 17 / 限制器 10 / 流体 9 / 电磁 8；核心 6 条（过载、电容蓄力、标记跳转、集群协议、反应增幅、处决），全在限制器类、精良（FGR-FW-003）");
        }

        // ── C. 目录由表生成 ──────────────────────────────────────────────────────

        private static void CheckCatalogFromTable()
        {
            Line("  · C. 固件目录由表生成：44 条内容定义逐字段来自表与文本键；改表 → 目录、聚合目录、蓝图成本跟着变；切换语言 → 名字跟着变；表坏了 → 目录为空且可见报错");
            var problems = new List<string>();
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                if (!FirmwareCatalog.TryGet(r.Id, out MechanicalContentDef d))
                {
                    problems.Add(r.Id + " 不在目录");
                    continue;
                }
                bool core = r.Kind == "core";
                bool ok = d.Category == MechanicalContentCategory.Firmware && d.DisplayName == GameText.Get(r.NameKey) && d.Description == GameText.Get(r.DescKey)
                          && d.Load == r.Load && d.ScrapCost == r.Scrap && d.IconId == r.Icon && d.IconId == "icon_" + r.Id
                          && d.LegacyFacadeId == (r.LegacyId == "none" ? null : r.LegacyId)
                          && d.AiPermission == (core ? MechanicalContentAiPermission.PlayerOnly : MechanicalContentAiPermission.PlayerAndAllyAi)
                          && d.SourceDetail == GameText.Get(r.AcquireKey) && d.LockedHintText == d.SourceDetail && string.IsNullOrEmpty(d.SfxId)
                          && d.Source == ExpectedSource(r.Source) && !GameText.ContainsMarker(d.DisplayName + d.Description + d.SourceDetail + d.ValuesSummary)
                          && d.ValuesSummary.Contains(r.Power.ToString(CultureInfo.InvariantCulture));
                if (!ok) problems.Add(r.Id);
            }
            Expect(FirmwareCatalog.All.Count == FirmwareCatalog.ExpectedCount && problems.Count == 0
                   && MechanicalContentFacade.CountByCategory(MechanicalContentCategory.Firmware) == FirmwareCatalog.ExpectedCount,
                $"目录 {FirmwareCatalog.All.Count} 条 = 聚合目录里的固件 {MechanicalContentFacade.CountByCategory(MechanicalContentCategory.Firmware)} 条，逐字段来自表（名称 / 描述 / 负载 / 废料 / 图标 / 等价实现 / AI 许可 / 来源 / 获取途径）{Detail(problems)}");

            CampaignState s = NewState(8801);
            BlueprintCircuitBoard gunSplit = GunBoard(FirmwareCatalog.FwSplitId);
            int scrapBefore = gunSplit.ComputeScrapCost();
            TbFirmwareKind edited = TableFrom(FirmwareKinds.Rows.Where(r => r.Id != "fw_bridge").Select(r =>
                r.Id == FirmwareCatalog.FwSplitId ? With(r, nameKey: "firmware.fw_fork.name", load: 3, scrap: 99) : r));
            FirmwareKinds.OverrideTableForTests(edited);
            try
            {
                bool splitOk = FirmwareCatalog.TryGet(FirmwareCatalog.FwSplitId, out MechanicalContentDef split) && split.DisplayName == "分叉" && split.Load == 3 && split.ScrapCost == 99
                               && MechanicalContentFacade.TryGet(FirmwareCatalog.FwSplitId, out MechanicalContentDef fsplit) && fsplit.Load == 3;
                int scrapAfter = gunSplit.ComputeScrapCost();
                Expect(splitOk && FirmwareCatalog.All.Count == 43 && !FirmwareCatalog.TryGet("fw_bridge", out _) && FirmwareKinds.KindOf("fw_bridge") == FirmwareKind.NotFirmware
                       && !MechanicalContentFacade.TryGet("fw_bridge", out _) && scrapAfter == scrapBefore - 8 + 99
                       && FirmwareKinds.DisplayName(FirmwareCatalog.FwSplitId) == "分叉",
                    $"注入改过的表（分裂改名键 / 负载 3 / 废料 99，删掉桥接）：目录 {FirmwareCatalog.All.Count} 条、聚合目录与蓝图废料成本 {scrapBefore}→{scrapAfter} 都跟着表变，桥接不再是固件");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
            Expect(FirmwareCatalog.All.Count == FirmwareCatalog.ExpectedCount && FirmwareCatalog.TryGet("fw_bridge", out _),
                "恢复正式表后目录回到 44 条");

            GameSettings.SetLanguage(GameLanguage.En);
            string en = FirmwareCatalog.TryGet(FirmwareCatalog.FwSplitId, out MechanicalContentDef enDef) ? enDef.DisplayName : null;
            string enFacade = MechanicalContentFacade.TryGet("fw_coolant", out MechanicalContentDef enCool) ? enCool.DisplayName : null;
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string zh = FirmwareCatalog.TryGet(FirmwareCatalog.FwSplitId, out MechanicalContentDef zhDef) ? zhDef.DisplayName : null;
            Expect(en == "Split" && enFacade == "Coolant" && zh == "分裂", $"切换语言：名字跟着文本键走（英文 {en} / {enFacade}，切回中文 {zh}），不是写死的中文");

            FirmwareKinds.OverrideTableForTests(null);
            try
            {
                Expect(FirmwareCatalog.All.Count == 0 && FirmwareCatalog.LoadError != null && FirmwareKinds.KindOf(FirmwareCatalog.FwHomingId) == FirmwareKind.NotFirmware
                       && MechanicalContentFacade.CountByCategory(MechanicalContentCategory.Firmware) == 0,
                    $"表没加载上：目录为空、LoadError 可见（{FirmwareCatalog.LoadError}），不会拿旧数据悄悄顶上");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
            _ = s;
        }

        private static MechanicalContentSource ExpectedSource(string source) => source switch
        {
            "base" => MechanicalContentSource.BaseBlueprint,
            "terminal" => MechanicalContentSource.SilentRuinsSalvage,
            "cache" => MechanicalContentSource.FoundryOptionalCache,
            "relic" => MechanicalContentSource.RelicTerminal,
            "salvage" => MechanicalContentSource.RegionSalvage,
            "elite" => MechanicalContentSource.EliteDrop,
            "boss" => MechanicalContentSource.FactionBoss,
            _ => (MechanicalContentSource)(-1),
        };

        // ── D. 机器电路装配（FGT-FW-002）────────────────────────────────────────

        private static void CheckMachineCircuit()
        {
            Line("  · D. 机器电路：6 条核心固件装不进机器电路与炮塔、只能进信号核（FGT-FW-002）；38 条常规固件（FG2-FW-02 起含装甲击穿）经正式固件槽入口装上、校验通过、落盘、编译生效");
            CampaignState s = NewState(8802, unlockAll: true);
            var coreBad = new List<string>();
            var regularBad = new List<string>();
            int installed = 0;
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                BlueprintCircuitBoard b = GunBoard(null);
                CircuitOpResult set = b.TrySetFirmware(s, 0, r.Id);
                if (r.Kind == "core")
                {
                    bool turret = !FirmwareKinds.CanInstall(s, r.Id, FirmwareHost.Turret, out string tk) && tk == "signal.reason.core_turret";
                    bool signal = FirmwareKinds.CanInstall(s, r.Id, FirmwareHost.SignalCore, out _);
                    if (set.Success || set.Code != BlueprintCircuitBoard.CoreSignalOnlyCode || !turret || !signal) coreBad.Add(r.Id);
                    continue;
                }
                if (!set.Success)
                {
                    regularBad.Add($"{r.Id}：{set.Code} {set.Message}");
                    continue;
                }
                CircuitValidationResult v = b.Validate();
                BlueprintVersionRecord ver = b.ToVersion(1, 0f);
                BlueprintCircuitPreview p = BlueprintCircuitCompiler.CompilePreview(b);
                if (!v.IsValid || !ver.OrderedFirmwareIds.Contains(r.Id) || !p.FirmwareIds.Contains(r.Id) || !p.HasCombatOutput || p.PathCount <= 0
                    || !FirmwareKinds.CanInstall(s, r.Id, FirmwareHost.Turret, out _))
                {
                    regularBad.Add($"{r.Id}：校验 {v.IsValid} / 落盘 {ver.OrderedFirmwareIds.Contains(r.Id)} / 编译 {p.FirmwareIds.Contains(r.Id)}");
                    continue;
                }
                installed++;
            }
            Expect(coreBad.Count == 0, $"6 条核心固件：固件槽拒绝（{BlueprintCircuitBoard.CoreSignalOnlyCode}）、炮塔拒绝（core_turret）、信号核接受{Detail(coreBad)}");
            // FG2-FW-02：装甲击穿有了原生读法（破甲，DEBT-FG1SIG06-07 关闭），与其余 37 条一样经正式入口装上。
            Expect(regularBad.Count == 0 && installed == 38,
                $"常规固件 {installed} / 38 条（含 FG2-FW-02 起可装的装甲击穿）经 TrySetFirmware → Validate → ToVersion → CompilePreview 真实装上并生效；炮塔判定同样放行{Detail(regularBad)}");

            CampaignState fresh = NewState(8803);
            BlueprintCircuitBoard g = GunBoard(null);
            CircuitOpResult raw = g.TrySetFirmware(fresh, 0, "fw_scatter");
            CircuitOpResult locked = g.TrySetFirmware(fresh, 0, "fw_pierce");
            CircuitOpResult baseOk = g.TrySetFirmware(fresh, 1, FirmwareCatalog.FwHomingId);
            Expect(!raw.Success && raw.Code == "raw_signal_only" && !locked.Success && locked.Code == "firmware_locked"
                   && locked.Message.Contains(GameText.Get("firmware.acquire.relic")) && baseOk.Success,
                $"新开局：敌方加密的霰射未破解 → {raw.Code}（只能由信号裸跑）；中立的贯穿还没拿到 → {locked.Code}，原因写明获取途径；基础蓝图库的寻的直接可装");
            Expect(SignalCoreService.PrintableFirmware(fresh).OrderBy(x => x, StringComparer.Ordinal)
                       .SequenceEqual(new[] { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwOverloadId, FirmwareCatalog.FwSplitId, FirmwareCatalog.FwTrailId }),
                $"新开局可刻印的固件 = 基础蓝图库 4 条（{string.Join("、", SignalCoreService.PrintableFirmware(fresh).Select(FirmwareKinds.DisplayName))}），其余 40 条要按获取途径拿到");
        }

        // ── E. 接入口插入与路径 ─────────────────────────────────────────────────────

        private static void CheckUplinkAndPaths()
        {
            Line("  · E. 接入口：44 条都能插进接入口生效；插入前后路径数不变（没有改变电路拓扑的固件 → DEBT-FG1SIG02-02 关闭）；机身形变按表的形变列");
            NewState(8804, unlockAll: true);
            BlueprintCircuitBoard b = GunBoard(null);
            CircuitOpResult up = b.TrySetUplink(2);
            int basePaths = UplinkCompiler.PathCountAfter(b, Array.Empty<string>());
            var bad = new List<string>();
            var morphBad = new List<string>();
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                int after = UplinkCompiler.PathCountAfter(b, new[] { r.Id });
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(b, new[] { r.Id });
                if (after != basePaths || !p.UplinkOnPath || !p.UplinkFirmwareIds.Contains(r.Id) || p.PathCount != basePaths)
                {
                    bad.Add($"{r.Id}（路径 {basePaths}→{after}，生效 {p.UplinkFirmwareIds.Contains(r.Id)}）");
                }
                MorphMask expected = r.Morph == "limiter" ? MorphMask.Limiter : r.Morph == "fluid" ? MorphMask.Fluid : r.Morph == "em" ? MorphMask.Electromagnetic : MorphMask.None;
                if (MachineMorph.MaskOf(p) != expected || MachineMorph.BitOf(r.Id) != expected)
                {
                    morphBad.Add($"{r.Id}={MachineMorph.MaskOf(p)}（应 {expected}）");
                }
            }
            Expect(up.Success && basePaths > 0 && bad.Count == 0,
                $"44 条逐条插进 2 号格接入口：都生效，路径数都保持 {basePaths}（UplinkCompiler.PathCountAfter 与真实编译一致）{Detail(bad)}");
            int em = FirmwareKinds.Rows.Count(r => r.Morph == "em");
            Expect(morphBad.Count == 0 && em == 8,
                $"机身形变 = 表的形变列：引信 17 条常态、限制器 10 条形变态、流体 9 条喷口态、电磁 {em} 条线圈态（正式电磁固件替代了 FG1-VFX-01 的注入）{Detail(morphBad)}");
        }

        // ── F. 热量与能耗 ─────────────────────────────────────────────────────────

        private static void CheckHeatAndPower()
        {
            Line("  · F. 热量与能耗（FGR-FW-001）：预览 = 蓝图版本 = 表；Demo 数字不变；重炮内核每发积热 = 热量预算（IC-REQ-010）；双态预览有能耗行与差异（FG-GAP-028）");
            CampaignState s = NewState(8805, unlockAll: true);
            var bad = new List<string>();
            foreach (FwRow r in FirmwareKinds.Rows.Where(x => x.Kind != "core" && x.Id != FirmwareCatalog.FwArmorPierceId))
            {
                BlueprintCircuitBoard b = GunBoard(null);
                b.TrySetFirmware(s, 0, r.Id);
                BlueprintCircuitPreview p = BlueprintCircuitCompiler.CompilePreview(b);
                BlueprintVersionRecord v = b.ToVersion(1, 0f);
                if (Math.Abs(p.HeatBudget - r.Heat) > 1e-4f || p.PowerCost != r.Power || v.PowerCost != r.Power || Math.Abs(v.HeatBudget - r.Heat) > 1e-4f)
                {
                    bad.Add($"{r.Id}（热 {p.HeatBudget}/{r.Heat} 电 {p.PowerCost}/{v.PowerCost}/{r.Power}）");
                }
            }
            Expect(bad.Count == 0, $"37 条常规固件装在连射器上：预览热量预算、每发耗电与蓝图版本落盘的 HeatBudget / PowerCost 都等于表里的 heat / power{Detail(bad)}");

            float gun0 = BlueprintCircuitCompiler.CompilePreview(GunBoard(null)).HeatBudget;
            BlueprintCircuitBoard gunUp = GunBoard(null);
            gunUp.TrySetUplink(2);
            float gunOver = UplinkCompiler.CompileUplinked(gunUp, new[] { FirmwareCatalog.FwOverloadId }).HeatBudget;
            BlueprintCircuitBoard cannonUp = CannonBoard(null);
            cannonUp.TrySetUplink(2);
            float cannon = BlueprintCircuitCompiler.CompilePreview(cannonUp).HeatBudget;
            BlueprintCircuitPreview melt = UplinkCompiler.CompileUplinked(cannonUp, new[] { FirmwareCatalog.FwOverloadId });
            Expect(gun0 == 0f && Mathf.Approximately(gunOver, 15f) && Mathf.Approximately(cannon, 40f) && Mathf.Approximately(melt.HeatBudget, 65f)
                   && melt.ReactionId == MechanicalReactionCatalog.ReactionMeltOverloadId,
                $"Demo 数字不变：连射器 {gun0}、连射器 + 过载 {gunOver}、重炮 {cannon}、重炮 + 过载（熔穿过载）{melt.HeatBudget}");

            // 重炮 + 冷却液（每发 +4）：预览 44；内核武器参数由正式翻译 CombatSite.MachineWeaponFrom 生成，真实开一发积热 44。
            BlueprintCircuitBoard cool = CannonBoard(FirmwareKinds.Rows.First(r => r.Id == "fw_coolant").Id);
            BlueprintCircuitPreview cp = BlueprintCircuitCompiler.CompilePreview(cool);
            float shot = FireCannonOnce(CombatSite.MachineWeaponFrom(cp), out CombatWeapon w1);
            cool.TrySetUplink(2);
            BlueprintCircuitPreview cm = UplinkCompiler.CompileUplinked(cool, new[] { FirmwareCatalog.FwOverloadId });
            float shotMelt = FireCannonOnce(CombatSite.MachineWeaponFrom(cm), out CombatWeapon w2);
            Expect(Mathf.Approximately(cp.HeatBudget, 44f) && Mathf.Approximately(w1.HeatPerShot, 44f) && Mathf.Approximately(shot, 44f)
                   && Mathf.Approximately(cm.HeatBudget, 69f) && Mathf.Approximately(w2.HeatPerShot + w2.OverloadExtraHeat, 69f) && Mathf.Approximately(shotMelt, 69f),
                $"重炮 + 冷却液：预览 {cp.HeatBudget}、内核参数 {w1.HeatPerShot}、真实开火积热 {shot}；再接入过载（熔穿过载）：预览 {cm.HeatBudget}、真实开火 {shotMelt}——预览与实战同一个数");

            // 双态预览：插入有能耗的固件时两栏各有“每发耗电”行，差异里有耗电变化；插入 0 能耗固件时没有耗电差异。
            UplinkDualPreview dual = UplinkCompiler.CompileDual(gunUp, new[] { "fw_arcchain" });
            UplinkDualPreview zero = UplinkCompiler.CompileDual(gunUp, new[] { "fw_coolant" });
            string powerLineAi = GameText.Format("circuit.uplink.line.heat_power", "0.0", "0");
            string powerLineUp = GameText.Format("circuit.uplink.line.heat_power", "0.0", "3");
            Expect(dual.AiLines.Any(l => l.Text == powerLineAi) && dual.UplinkedLines.Any(l => l.Text == powerLineUp)
                   && dual.Diff.Any(d => d.Kind == UplinkDiffKind.PowerChanged && d.Text == GameText.Format("circuit.uplink.diff.power", "0", "3"))
                   && !zero.Diff.Any(d => d.Kind == UplinkDiffKind.PowerChanged) && zero.Diff.Any(d => d.Kind == UplinkDiffKind.HeatChanged),
                "双态预览（FG-GAP-028）：插入电弧 → 两栏热量行“热量预算 0.0 · 每发耗电 0 / 3”、差异“耗电 0 → 3”；插入冷却液（耗电 0、积热 4）→ 只有热量差异");
            _ = s;
        }

        /// <summary>裸内核里放一台装这把武器的机器与一个靶子，瞄准就绪后经内核开火入口开一发，返回这一发的积热。</summary>
        private static float FireCannonOnce(CombatWeapon weapon, out CombatWeapon echoed)
        {
            using var k = new CombatKernel(CombatSite.ConfigFromTuning(), 16);
            int wi = k.AddWeapon(weapon);
            k.TryGetWeapon(wi, out echoed);
            int m = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Machine, Faction = CombatFaction.Player, Behavior = CombatBehavior.HoldFire,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled, Position = new double2(0, 0), Home = new double2(0, 0),
                Radius = 0.8f, Health = 400f, MaxHealth = 400f, Weapon = wi, BehaviorProfile = -1, Priority = 1,
            });
            int t = k.Spawn(new CombatSpawn
            {
                ExtKey = -1, Kind = CombatUnitKind.Enemy, Faction = CombatFaction.Hostile, Behavior = CombatBehavior.None,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable, Position = new double2(0, 8), Home = new double2(0, 8),
                Radius = 0.8f, Health = 5000f, MaxHealth = 5000f, Weapon = -1, BehaviorProfile = -1, Priority = 1,
            });
            k.SetWeaponState(m, 0f, false, 1.0, 0);
            CombatFireResult r = k.FireAt(m, t, 2.0);
            return r == CombatFireResult.Ok && k.TryGetUnit(m, out CombatUnitView v) ? v.Heat : -1f;
        }

        // ── G. 状态标签（FGR-FW-030）──────────────────────────────────────────────

        private static void CheckStatusTags()
        {
            Line("  · G. 状态标签（FGR-FW-030）：fg.TbStatusTag = FG02 3.4 名表；每条固件的标签 = 它的等价实现模块真实贴上的标签；编译产出的标签都能叫出名字（或是反应产物，归 FG2-FW-03）");
            string fg02 = ReadDoc("TEngine/UnityProject/DesignDocs/fullgame/FG02_Firmware_Reactions_Morph.md");
            string sec = DesignDocsAuditSelfCheck.Section(fg02 ?? string.Empty, "### 3.4") ?? string.Empty;
            var docMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in DesignDocsAuditSelfCheck.Table(sec, "| 旧标签 | 新名 |").Skip(2))
            {
                string[] c = line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                for (int i = 0; i + 1 < c.Length; i += 2)
                {
                    foreach (string id in c[i].Split('/').Select(x => x.Trim()).Where(x => x.Length > 0))
                    {
                        docMap[id] = c[i + 1];
                    }
                }
            }
            var mismatch = new List<string>();
            foreach (StatusTag t in StatusTagCatalog.Rows.Where(x => x.Kind == "status"))
            {
                if (!docMap.TryGetValue(t.Id, out string docName) || GameText.Get(t.NameKey, GameLanguage.ZhCn) != docName)
                {
                    mismatch.Add($"{t.Id}={GameText.Get(t.NameKey, GameLanguage.ZhCn)}（文档 {docName ?? "无"}）");
                }
            }
            mismatch.AddRange(docMap.Keys.Where(id => !StatusTagCatalog.IsStatus(id)).Select(id => id + " 在文档里有、表里不是状态标签"));
            // 机制标记（不在头顶显示）：FG02 3.4 第二张表“| 机制标记 | 名称 |”。
            var markerDoc = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in DesignDocsAuditSelfCheck.Table(sec, "| 机制标记 | 名称 |").Skip(2))
            {
                string[] c = line.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                for (int i = 0; i + 1 < c.Length; i += 2)
                {
                    if (c[i].Length > 0)
                    {
                        markerDoc[c[i]] = c[i + 1];
                    }
                }
            }
            var markers = StatusTagCatalog.Rows.Where(x => x.Kind == "marker").ToList();
            mismatch.AddRange(markers.Where(m => !markerDoc.TryGetValue(m.Id, out string nm) || nm != GameText.Get(m.NameKey, GameLanguage.ZhCn))
                .Select(m => $"机制标记 {m.Id} 与 FG02 3.4 不一致"));
            mismatch.AddRange(markerDoc.Keys.Where(id => !StatusTagCatalog.IsMarker(id)).Select(id => $"文档的机制标记 {id} 表里没有"));
            var primary = StatusTagCatalog.Rows.Where(t => t.AliasOf == "none" && t.Kind == "status").ToList();
            bool looks = primary.All(t => !string.IsNullOrEmpty(StatusTagCatalog.ShapeOf(t.Id)) && Regex.IsMatch(StatusTagCatalog.ColorOf(t.Id) ?? string.Empty, "^#[0-9A-Fa-f]{6}$")
                                          && GameText.Get(t.NameKey, GameLanguage.En) != GameText.Get(t.NameKey, GameLanguage.ZhCn))
                         && primary.Select(t => t.Shape + t.Color).Distinct().Count() == primary.Count;
            Expect(docMap.Count >= 30 && markers.Count >= 8 && mismatch.Count == 0 && looks,
                $"FG02 3.4 名表 {docMap.Count} 个状态标签（含补齐的 14 个与 5 个同义写法）+ {markerDoc.Count} 个机制标记与 fg.TbStatusTag 逐条同名；{primary.Count} 个主状态标签都有形状 + 颜色（组合不重复）与中英文名{Detail(mismatch)}");
            Expect(StatusTagCatalog.DisplayNames(new[] { "Burning", "Fire", "Apoptosis", "Dark", "Plasma", "ExplodeOnHit" }).SequenceEqual(new[] { "燃烧", "处决" })
                   && StatusTagCatalog.IsMarker("ExplodeOnHit") && StatusTagCatalog.ShapeOf("ExplodeOnHit") == null
                   && StatusTagCatalog.NameOf("Plasma") == null && StatusTagCatalog.Canonical("Shocked") == "Shock" && StatusTagCatalog.ShapeOf("Light") == StatusTagCatalog.ShapeOf("Mirror"),
                "同义写法按主标签合并（Burning / Fire → 一个“燃烧”），查不到的标签不显示（不漏内部字符串）");

            var probeBad = new List<string>();
            int probed = 0;
            foreach (FwRow r in FirmwareKinds.Rows.Where(x => x.LegacyId != "none"))
            {
                string[] actual = FirmwareCatalog.ProbeModuleTags(r.Id);
                string[] declared = FirmwareKinds.TagsOf(r.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                string[] actualStatus = actual?.Where(StatusTagCatalog.IsStatus).ToArray();
                string[] unregistered = actual?.Where(t => !StatusTagCatalog.TryGet(t, out _)).ToArray() ?? Array.Empty<string>();
                if (actual == null || !actualStatus.SequenceEqual(declared) || unregistered.Length > 0)
                {
                    probeBad.Add($"{r.Id}：表 [{string.Join(",", declared)}] 模块 [{string.Join(",", actual ?? Array.Empty<string>())}]");
                }
                probed++;
            }
            Expect(probed == 42 && probeBad.Count == 0,
                $"行为探针：42 条有等价实现的固件逐条实例化模块跑一步，真实贴上的状态标签与表的“产生的状态标签”完全一致，另贴的机制标记都已登记{Detail(probeBad)}");

            NewState(8806, unlockAll: true);
            BlueprintCircuitBoard b = GunBoard(null);
            b.TrySetUplink(2);
            var unknown = new HashSet<string>(StringComparer.Ordinal);
            int seen = 0;
            foreach (FwRow r in FirmwareKinds.Rows)
            {
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(b, new[] { r.Id });
                foreach (string tag in p.Paths.SelectMany(x => x.Tags))
                {
                    seen++;
                    if (!StatusTagCatalog.TryGet(tag, out _) && !ReactionProducts.Contains(tag))
                    {
                        unknown.Add(tag);
                    }
                }
            }
            Expect(seen > 0 && unknown.Count == 0,
                $"44 条逐条插进接入口编译，产出的 {seen} 个标签实例全部能在标签表里叫出名字，或是反应产物（反应命名归 FG2-FW-03）{Detail(unknown.ToList())}");
        }

        // ── H. 旧存档按新表显示（卡片负向）────────────────────────────────────────

        private static void CheckSaveCompat()
        {
            Line("  · H. 旧存档里的固件实例按新表显示：真实文件存读档（正式恢复入口）；表里退役一条固件时的对账；表坏了不把固件当已移除内容转废料");
            CampaignState s = NewState(8807, unlockAll: true);
            string homing = Print(s, FirmwareCatalog.FwHomingId);
            string trailBag = Print(s, FirmwareCatalog.FwTrailId);
            string trailCore = Print(s, FirmwareCatalog.FwTrailId);
            string coolant = Print(s, "fw_coolant");
            string scatter = Print(s, "fw_scatter");
            Equip(s, homing, 0);
            Equip(s, trailCore, 1);
            BlueprintCircuitBoard board = GunBoard(FirmwareCatalog.FwTrailId);
            board.TrySetFirmware(s, 1, "fw_coolant");
            AddBlueprint(s, "bp_selfcheck_fw01", board);
            CampaignSaveService.Save(Slot, s, SaveReason.Manual);

            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            CampaignState l = rr.State;
            string[] ids = { FirmwareCatalog.FwHomingId, FirmwareCatalog.FwTrailId, "fw_coolant", "fw_scatter" };
            bool chipsBack = l != null && new[] { homing, trailBag, trailCore, coolant, scatter }.All(pid => l.PrimitiveChips.Any(c => c.PartId == pid))
                             && SignalCoreService.SlotContentId(l, 0) == FirmwareCatalog.FwHomingId && SignalCoreService.SlotContentId(l, 1) == FirmwareCatalog.FwTrailId;
            string zhNames = string.Join("、", ids.Select(FirmwareKinds.DisplayName));
            GameSettings.SetLanguage(GameLanguage.En);
            string enNames = string.Join("、", ids.Select(FirmwareKinds.DisplayName));
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            BlueprintVersionRecord ver = l?.BlueprintRecords?.FirstOrDefault(x => x.BlueprintId == "bp_selfcheck_fw01")?.Versions?.FirstOrDefault();
            BlueprintCircuitPreview vp = ver == null ? null : BlueprintCircuitCompiler.CompilePreview(BlueprintCircuitBoard.FromVersion(ver));
            Expect(rr.Success && chipsBack && zhNames == "寻的、拖尾、冷却液、霰射" && enNames == "Homing、Trail、Coolant、Scatter Shot"
                   && vp != null && vp.FirmwareIds.Contains(FirmwareCatalog.FwTrailId) && vp.FirmwareIds.Contains("fw_coolant") && Mathf.Approximately(vp.HeatBudget, 4f),
                $"读档后 5 枚芯片、信号核两槽、蓝图固件槽原样；名字按新表（中文 {zhNames} / 英文 {enNames}，存档里不存名字）；蓝图编译按新表数值（积热 {vp?.HeatBudget}）");

            // 表里退役一条（拖尾不在新表里）：仓里的转废料（按已移除内容表退还）、信号核里的保留并通知、蓝图照样编译（这一件不生效）。
            FirmwareKinds.OverrideTableForTests(TableFrom(FirmwareKinds.Rows.Where(r => r.Id != FirmwareCatalog.FwTrailId)));
            SaveContentReconciler.OverrideForTests(RemovedTable((FirmwareCatalog.FwTrailId, "primitive_chip", "firmware.fw_trail.name", 3, 3)));
            try
            {
                int scrap0 = l.Scrap;
                CampaignSaveService.Save(Slot, l, SaveReason.Manual);
                RestoreResult r2 = CampaignRestoreOrchestrator.Restore(Slot);
                CampaignState m = r2.State;
                string[] keys = r2.Notices.Select(n => n.TextKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();
                BlueprintVersionRecord mv = m?.BlueprintRecords?.FirstOrDefault(x => x.BlueprintId == "bp_selfcheck_fw01")?.Versions?.FirstOrDefault();
                BlueprintCircuitPreview mp = mv == null ? null : BlueprintCircuitCompiler.CompilePreview(BlueprintCircuitBoard.FromVersion(mv));
                Expect(r2.Success && m.Scrap == scrap0 + 3 && m.PrimitiveChips.All(c => c.PartId != trailBag) && m.PrimitiveChips.Any(c => c.PartId == trailCore)
                       && keys.SequenceEqual(new[] { "save.notice.content_removed", "save.notice.installed_kept" })
                       && m.PrimitiveChips.Any(c => c.PartId == homing) && m.PrimitiveChips.Any(c => c.PartId == coolant)
                       && mp != null && mp.PathCount > 0 && Mathf.Approximately(mp.HeatBudget, 4f),
                    $"新表退役拖尾：仓里那枚转 3 废料（{scrap0}→{m?.Scrap}）并通知，信号核里那枚保留并通知“请重新配置”，其余固件不受影响；蓝图照样编译（退役的那件不生效）");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
                SaveContentReconciler.ResetForTests();
            }

            // 负向：固件表坏了（读不出来）——固件芯片“查不到”是表坏了，不是内容被移除：一枚都不转废料。
            CampaignSaveService.Save(Slot, l, SaveReason.Manual);
            FirmwareKinds.OverrideTableForTests(null);
            try
            {
                int chips0 = l.PrimitiveChips.Count(c => c.CardDefId.StartsWith("fw_", StringComparison.Ordinal));
                RestoreResult r3 = CampaignRestoreOrchestrator.Restore(Slot);
                CampaignState n = r3.State;
                int chips1 = n?.PrimitiveChips.Count(c => c.CardDefId.StartsWith("fw_", StringComparison.Ordinal)) ?? -1;
                Expect(r3.Success && chips1 == chips0 && n.Scrap == l.Scrap && r3.Notices.All(x => x.TextKey != "save.notice.content_removed"),
                    $"固件表读不出来时读档：{chips1}/{chips0} 枚固件芯片原样保留、废料不变、没有“内容已移除”通知（只记 Error）");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        // ── I. 题材审计扩展到 FG 文本键（FGT-FW-008）──────────────────────────────

        private static void CheckTextAudit()
        {
            Line("  · I. FGT-FW-008：全部 fg.TbLocText 中英文本零旧生物词、零内部 ID；44 条固件的名称 / 描述 / 5 种载体读法 / 获取途径齐全且互不相同");
            IReadOnlyList<LocText> texts = ConfigSystem.Instance.Tables.TbLocText.DataList;
            var bad = new List<string>();
            foreach (LocText t in texts)
            {
                string hit = AuditHit(t.Zh, t.En);
                if (hit != null)
                {
                    bad.Add($"{t.Key}：{hit}");
                }
            }
            int fwKeys = texts.Count(t => t.Key.StartsWith("firmware.", StringComparison.Ordinal) || t.Key.StartsWith("tag.", StringComparison.Ordinal));
            Expect(texts.Count >= 1400 && fwKeys >= 44 * 7 && bad.Count == 0,
                $"扫描 fg.TbLocText 全部 {texts.Count} 条（其中固件 / 标签 {fwKeys} 条）中英文：零旧生物词（{ThemeLexicon.LegacyBioWords.Length + ThemeLexicon.FgNameTableWords.Length} 个中文词 + 英文词表）、零内部 ID{Detail(bad)}");

            var fwBad = new List<string>();
            var carriers = (FirmwareCarrier[])Enum.GetValues(typeof(FirmwareCarrier));
            foreach (MechanicalContentDef d in FirmwareCatalog.All.Values)
            {
                string[] zhReads = carriers.Select(c => GameText.Get(FirmwareKinds.ReadingKey(d.Id, c), GameLanguage.ZhCn)).ToArray();
                string[] enReads = carriers.Select(c => GameText.Get(FirmwareKinds.ReadingKey(d.Id, c), GameLanguage.En)).ToArray();
                string all = string.Join("|", new[] { d.DisplayName, d.Description, d.SourceDetail, d.LockedHintText, d.ValuesSummary }.Concat(zhReads));
                if (zhReads.Distinct().Count() != 5 || enReads.Distinct().Count() != 5 || zhReads.Any(string.IsNullOrWhiteSpace) || enReads.Any(string.IsNullOrWhiteSpace)
                    || GameText.ContainsMarker(all + string.Join("|", enReads)) || AuditHit(all, string.Join("|", enReads)) != null)
                {
                    fwBad.Add(d.Id);
                }
            }
            var zhNames = FirmwareCatalog.All.Keys.Select(id => GameText.Get(FirmwareKinds.Rows.First(r => r.Id == id).NameKey, GameLanguage.ZhCn)).ToList();
            var enNames = FirmwareCatalog.All.Keys.Select(id => GameText.Get(FirmwareKinds.Rows.First(r => r.Id == id).NameKey, GameLanguage.En)).ToList();
            Expect(fwBad.Count == 0 && zhNames.Distinct().Count() == 44 && enNames.Distinct().Count() == 44,
                $"44 条固件：中英名称各不重复；目录玩家字段 + 5 种载体读法（中英各 5 句、互不相同）零缺失标记、零禁用词、零内部 ID{Detail(fwBad)}");

            // 扫描器自己要能抓到违规（否则上面的“零”没有意义）。
            string[] mustHit = { "装上这条基因", "Gene splice", "gene_taxis 已刻印", "发生溶血", "the organ fires", "fw_split 已装配" };
            string[] mustPass = { "寻的", "Homing", "每发耗电：3", "200 cells from the edge" };
            Expect(mustHit.All(x => AuditHit(x, x) != null) && mustPass.All(x => AuditHit(x, x) == null),
                "审计器对照：旧生物词（中 / 英）、内部 ID 都能抓到；正式名与“格子（cell）”不误伤");
        }

        private static string AuditHit(string zh, string en)
        {
            string z = ThemeLexicon.FirstHit(zh);
            if (z != null) return "中文禁用词 " + z;
            Match e = EnglishBioWords.Match(en ?? string.Empty);
            if (e.Success) return "英文禁用词 " + e.Value;
            Match id = InternalContentId.Match((zh ?? string.Empty) + " " + (en ?? string.Empty));
            return id.Success ? "内部 ID " + id.Value : null;
        }

        // ── J. 负向 ────────────────────────────────────────────────────────────────

        private static void CheckNegatives()
        {
            Line("  · J. 负向：不是固件 / 空 ID 的查询全部安全退化；表里文本键缺失时界面显示缺失标记而不是内部 ID");
            string[] probes = { null, string.Empty, "organ_focus", "gene_taxis", "fw_nope" };
            bool safe = probes.All(id => !FirmwareCatalog.TryGet(id, out _) && FirmwareKinds.KindOf(id) == FirmwareKind.NotFirmware && FirmwareKinds.DisplayName(id) == null
                                         && FirmwareKinds.PowerOf(id) == 0 && FirmwareKinds.HeatOf(id) == 0f && FirmwareKinds.ReadingKey(id, FirmwareCarrier.Aura) == null
                                         && FirmwareKinds.MorphOf(id) == "none" && FirmwareKinds.TagsOf(id).Length == 0 && FirmwareKinds.LegacyIdOf(id) == null
                                         && FirmwareCatalog.ProbeModuleTags(id) == null && !FirmwareKinds.IsCrackedByDefault(id));
            Expect(safe, "null / 空串 / 基元芯片 / 旧基因 ID / 不存在的固件：不是固件，名称 null、数值 0、读法 null、形变常态、没有标签——旧基因 ID 不能当固件 ID 用");

            FirmwareKinds.OverrideTableForTests(TableFrom(FirmwareKinds.Rows.Select(r => r.Id == FirmwareCatalog.FwSplitId ? With(r, nameKey: "firmware.nope.name") : r)));
            try
            {
                string name = FirmwareCatalog.TryGet(FirmwareCatalog.FwSplitId, out MechanicalContentDef d) ? d.DisplayName : null;
                Expect(GameText.ContainsMarker(name) && !name.Contains("fw_split"),
                    $"表里名称键写错：显示缺失标记“{name}”（自检和冒烟会抓到），不回退成内部 ID");
            }
            finally
            {
                FirmwareKinds.ResetForTests();
            }
        }

        // ── K. 暂停与倍速 ──────────────────────────────────────────────────────────

        private static void CheckPauseSpeed()
        {
            Line("  · K. 暂停与 0.5x～3x：固件数据与编译结果与时钟无关（本 Story 不新增计时；核心固件冷却读表，计时本身由 FG1-SIG-03 自检覆盖）");
            NewState(8808, unlockAll: true);
            BlueprintCircuitBoard b = GunBoard("fw_nitrogen");
            b.TrySetUplink(2);
            string Snapshot()
            {
                BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(b, new[] { "fw_capacitor" });
                return $"{p.HeatBudget}|{p.PowerCost}|{p.TotalNormalizedDamage:F4}|{string.Join(",", p.FirmwareIds)}|{FirmwareKinds.CoreCooldownSeconds("fw_capacitor")}|{FirmwareKinds.DisplayName("fw_nitrogen")}";
            }
            string baseline = Snapshot();
            var diffs = new List<string>();
            GameClock.SetPaused(true);
            if (Snapshot() != baseline) diffs.Add("暂停");
            GameClock.SetPaused(false);
            foreach (float speed in new[] { 0.5f, 1f, 2f, 3f })
            {
                GameClock.SetSpeed(speed);
                if (Snapshot() != baseline) diffs.Add(speed + "x");
            }
            GameClock.SetSpeed(1f);
            Expect(diffs.Count == 0, $"暂停 / 0.5x / 1x / 2x / 3x 下编译结果、冷却秒数与名称逐字相同（{baseline}）{Detail(diffs)}");
        }

        // ── L. 性能 ────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · L. 性能：目录冷构建、逐条查询、真实编译（目录不按帧重建；查询 O(1)）");
            var sw = Stopwatch.StartNew();
            FirmwareCatalog.Invalidate();
            int n = FirmwareCatalog.All.Count;
            sw.Stop();
            double buildMs = sw.Elapsed.TotalMilliseconds;

            string[] ids = FirmwareCatalog.All.Keys.ToArray();
            int iterations = 100000;
            float sink = 0f;
            sw.Restart();
            for (int i = 0; i < iterations; i++)
            {
                string id = ids[i % ids.Length];
                sink += (int)FirmwareKinds.KindOf(id) + FirmwareKinds.HeatOf(id) + FirmwareKinds.PowerOf(id);
                sink += FirmwareKinds.DisplayName(id).Length;
            }
            sw.Stop();
            double perLookupUs = sw.Elapsed.TotalMilliseconds * 1000.0 / iterations;

            int builds = FirmwareCatalog.Revision;
            for (int i = 0; i < 1000; i++)
            {
                _ = FirmwareCatalog.All.Count;
            }
            bool noRebuild = FirmwareCatalog.Revision == builds;

            NewState(8809, unlockAll: true);
            BlueprintCircuitBoard b = GunBoard(null);
            b.TrySetUplink(2);
            sw.Restart();
            foreach (string id in ids)
            {
                UplinkCompiler.CompileUplinked(b, new[] { id });
            }
            sw.Stop();
            double compileMs = sw.Elapsed.TotalMilliseconds / ids.Length;
            PerfLines.Add($"目录冷构建 {n} 条 {buildMs:F2} ms；查询（种类 + 热量 + 能耗 + 名称）{perLookupUs:F3} µs/次（{iterations} 次，校验和 {sink:F0}）；接入编译 {compileMs:F3} ms/条（{ids.Length} 条）");
            ExpectPerf(n == 44 && noRebuild,
                $"目录冷构建 {buildMs:F2} ms（< 50）、查询 {perLookupUs:F3} µs/次（< 5）、接入编译 {compileMs:F3} ms/条（< 20，只在保存 / 接入 / 预览时发生，不按帧）；连续访问 1000 次不重建目录",
                PerfGate.Lt(buildMs, 50.0, "目录冷构建 ms"), PerfGate.Lt(perLookupUs, 5.0, "查询 µs"), PerfGate.Lt(compileMs, 20.0, "接入编译 ms/条"));
        }

        // ── 工具 ──────────────────────────────────────────────────────────────────

        private static CampaignState NewState(int seed, bool unlockAll = false)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgfw01-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.BuildingRecords = new[]
            {
                new BuildingRecord
                {
                    BuildingId = HomeValleyLayout.RegionId + ":" + HomeValleyLayout.BuildingTypeAssemblyStation,
                    BuildingTypeId = HomeValleyLayout.BuildingTypeAssemblyStation,
                    RegionId = HomeValleyLayout.RegionId,
                    ConstructionState = BuildingConstructionState.Operational,
                    PowerState = BuildingPowerState.Powered,
                },
            };
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            if (unlockAll)
            {
                // 按正常游戏的解锁记录（解析台破解 / 带回人类遗产）把 44 条都记为已获得、已破解。
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            }
            return s;
        }

        private static BlueprintCircuitBoard GunBoard(string firmware) =>
            BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null,
                firmware == null ? Array.Empty<string>() : new[] { firmware });

        private static BlueprintCircuitBoard CannonBoard(string firmware) =>
            BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompCannonId, null, null,
                firmware == null ? Array.Empty<string>() : new[] { firmware });

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        private static string Print(CampaignState s, string firmwareId)
        {
            SignalCoreResult r = SignalCoreService.TryPrintFirmwareChip(s, firmwareId);
            if (!r.Success)
            {
                Fail($"测试准备：刻印 {firmwareId} 失败（{r.Message}）");
            }
            return r.CreatedId;
        }

        private static void Equip(CampaignState s, string partId, int slot)
        {
            SignalCoreService.ExpeditionUnderwayOverrideForTests = () => false;
            try
            {
                SignalCoreResult r = SignalCoreService.TryEquip(s, partId, slot);
                if (!r.Success)
                {
                    Fail($"测试准备：装入信号核 {slot + 1} 号槽失败：{r.Message}");
                }
            }
            finally
            {
                SignalCoreService.ExpeditionUnderwayOverrideForTests = null;
            }
        }

        private static FwRow With(FwRow r, string nameKey = null, int? load = null, int? scrap = null)
        {
            TbFirmwareKind one = TableFrom(new[] { r }, nameKey, load, scrap);
            return one.DataList[0];
        }

        /// <summary>按行序列化成 Luban 二进制再构造表（与正式加载同一个反序列化入口）。</summary>
        private static TbFirmwareKind TableFrom(IEnumerable<FwRow> rows, string nameKey = null, int? load = null, int? scrap = null)
        {
            List<FwRow> list = rows.ToList();
            var buf = new ByteBuf();
            buf.WriteSize(list.Count);
            foreach (FwRow r in list)
            {
                buf.WriteString(r.Id);
                buf.WriteString(r.Kind);
                buf.WriteString(nameKey ?? r.NameKey);
                buf.WriteFloat(r.Cooldown);
                buf.WriteString(r.Protocol);
                buf.WriteString(r.Faction);
                buf.WriteString(r.Category);
                buf.WriteString(r.LegacyId);
                buf.WriteString(r.DescKey);
                buf.WriteString(r.Rarity);
                buf.WriteInt(load ?? r.Load);
                buf.WriteInt(r.Power);
                buf.WriteFloat(r.Heat);
                buf.WriteString(r.ReadProjectileKey);
                buf.WriteString(r.ReadMeleeKey);
                buf.WriteString(r.ReadSummonKey);
                buf.WriteString(r.ReadAuraKey);
                buf.WriteString(r.ReadFieldKey);
                buf.WriteString(r.Tags);
                buf.WriteString(r.Morph);
                buf.WriteString(r.Icon);
                buf.WriteString(r.Source);
                buf.WriteString(r.AcquireKey);
                buf.WriteBool(r.Cracked);
                buf.WriteInt(scrap ?? r.Scrap);
                buf.WriteString(r.ReadFields); // FG2-FW-02：读法字段
            }
            return new TbFirmwareKind(buf);
        }

        private static TbRemovedContent RemovedTable(params (string id, string kind, string nameKey, int refund, int ver)[] rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (var r in rows)
            {
                buf.WriteString(r.id);
                buf.WriteString(r.kind);
                buf.WriteString(r.nameKey);
                buf.WriteInt(r.refund);
                buf.WriteInt(r.ver);
            }
            return new TbRemovedContent(buf);
        }

        private static string ReadDoc(string relative)
        {
            string path = Path.Combine(LocateRepo(), relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return Directory.GetCurrentDirectory();
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (Exception e)
            {
                return (-1, e.Message);
            }
        }

        private static string Detail(List<string> items) =>
            items == null || items.Count == 0 ? string.Empty : "；问题：" + string.Join("；", items.Take(8)) + (items.Count > 8 ? $"……共 {items.Count} 条" : string.Empty);

        // ── 报告 ──────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
