using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BinGames.Sim;
using BinGames.Sim.Combat;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using LocText = GameConfig.fg.LocText;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// EMG1-TXT-01 生物残留第 1 层（FG19 FGR-EMG-230 第 1～5、8、9 行的文本层，FGR-EMG-231、232；FGT-EMG-072 阶段 1 部分、FGT-FW-008 扩展）。
    /// 起真实系统：真实 fg.TbLocText / fg.TbStatusTag / fg.TbCombatComponent → 真实组件目录、标签目录、图鉴正文、读法构建、存读档恢复入口、视觉库。
    /// A 题材词表：FG19 FGR-EMG-232 列的禁用词全部进 <see cref="ThemeLexicon"/>（解析设计文档，不抄一份）；全部 emg.* 文本键（中英）零禁用词、零内部 ID；
    ///   全部 fg.TbLocText 中文零“燃爆”（统一“爆燃”，FGR-EMG-181）；审计器对照：新词嵌在句中能抓到，正式名不误伤。
    /// B 第 11 节文案已换：爆炸反应装甲（名 / 描述 / 机制标记“反应装甲”）、罐弹抛洒器、处决（阈值 = 表里的处决阈值）、信号劫持（旧图鉴说明）；
    ///   描述不写“远程攻击不触发”、不写块数 / 补块（第 3 层归 EMG1-FW-01）；英文能容纳（B16）。
    /// C 旧存档：装着 func_spikes 的蓝图真实文件存读档 → 显示新名（中英）、反伤比例 / 固定值 / 触及与表一致且与存档前相同；存档文件里只有内部 ID、不存显示名。
    /// D 视觉 ID：4 个旧键与机械新键解析到同一个 Mesh（含 旧键::后缀 组合）、新键在白名单里且不是未知键回退；
    ///   图鉴占位标记只出现在有占位文本的组件上（DEBT-EMG1TXT01-01，B22）。
    /// 计时 / 倍速 / 家园不适用：本卡只改文本、显示名与视觉 ID 的名字，不碰任何按时间推进的状态。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 EmgTextResidueSelfCheck）；单段：bash tools/unity-validate.sh GameLogic.EditorTools.EmgTextResidueSelfCheck.RunFromMenu。
    /// </summary>
    public static class EmgTextResidueSelfCheck
    {
        private const int Slot = 0;
        private const string SpikesZh = "爆炸反应装甲";
        private const string SpikesEn = "Explosive Reactive Armor";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;

        [MenuItem("BinGames/QA/自检/生物残留第 1 层文案")]
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
            Line("\n[生物残留第 1 层] FGR-EMG-230～232：禁用词表、第 11 节文案、旧存档显示新名、视觉 ID 别名、图鉴占位标记（EMG1-TXT-01）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-emg1txt01-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                StatusTagCatalog.Reload();
                FirmwareKinds.ResetForTests();
                FirmwareCatalog.Invalidate();
                MechanicCodex.ResetForTests();
                Directory.CreateDirectory(_dir);
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_mechanics.json");
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                GameSettings.SetLanguage(GameLanguage.ZhCn);

                Step(CheckLexicon);
                Step(CheckSection11Texts);
                Step(CheckOldSave);
                Step(CheckVisualIds);
            }
            catch (Exception e)
            {
                Fail($"生物残留自检抛异常：{e}");
            }
            finally
            {
                MechanicCodex.ResetForTests();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
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
            Line($"  · [生物残留第 1 层] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 题材词表 ──────────────────────────────────────────────────────────

        /// <summary>FG19 FGR-EMG-232 “禁用词至少含：……。”里列的词（从设计文档解析，文档改了这里跟着变）。</summary>
        internal static List<string> Fgr232Words(string fg19)
        {
            Match m = Regex.Match(fg19 ?? string.Empty, @"\*\*FGR-EMG-232[^\n]*\n[^\n]*禁用词至少含：([^。\n]+)");
            return m.Success ? m.Groups[1].Value.Split('、').Select(w => w.Trim()).Where(w => w.Length > 0).ToList() : new List<string>();
        }

        internal static string AuditHit(string zh, string en)
        {
            string z = ThemeLexicon.FirstHit(zh);
            if (z != null) return "中文禁用词 " + z;
            Match e = FgFirmwareMigrationSelfCheck.EnglishBioWords.Match(en ?? string.Empty);
            if (e.Success) return "英文禁用词 " + e.Value;
            Match id = FgFirmwareMigrationSelfCheck.InternalContentId.Match((zh ?? string.Empty) + " " + (en ?? string.Empty));
            return id.Success ? "内部 ID " + id.Value : null;
        }

        private static void CheckLexicon()
        {
            Line("  · A. FGR-EMG-232 禁用词全部进词表（与 TERM-MIGRATION §6.7 的逐词一致由 DesignDocsAuditSelfCheck 查）；全部 emg.* 文本键中英零禁用词、零内部 ID；全部文本零“燃爆”");
            string fg19 = ReadDoc("TEngine/UnityProject/DesignDocs/fullgame/FG19_Emergence_Channels.md");
            List<string> words = Fgr232Words(fg19);
            var all = new HashSet<string>(ThemeLexicon.AllWords, StringComparer.Ordinal);
            List<string> missing = words.Where(w => !all.Contains(w)).ToList();
            List<string> notBiting = words.Where(w => ThemeLexicon.FirstHit("界面文字：" + w + "已就绪") == null).ToList();
            Expect(words.Count >= 16 && missing.Count == 0 && notBiting.Count == 0,
                $"FG19 FGR-EMG-232 列 {words.Count} 个禁用词（{string.Join("、", words)}）全在题材词表里、嵌在句中都能命中" + Detail(missing.Concat(notBiting).ToList()));

            IReadOnlyList<LocText> texts = ConfigSystem.Instance.Tables.TbLocText.DataList;
            List<LocText> emg = texts.Where(t => t.Key.StartsWith("emg.", StringComparison.Ordinal)).ToList();
            var bad = new List<string>();
            foreach (LocText t in emg)
            {
                string hit = AuditHit(t.Zh, t.En);
                if (hit != null)
                {
                    bad.Add($"{t.Key}：{hit}");
                }
            }
            Expect(emg.Count >= 100 && bad.Count == 0, $"全部 {emg.Count} 条 emg.* 文本键（中英）零禁用生物词、零内部 ID" + Detail(bad));

            List<string> ranbao = texts.Where(t => (t.Zh ?? string.Empty).Contains("燃爆")).Select(t => t.Key).ToList();
            bool baoran = texts.Any(t => t.Key == "reaction.deflagrate.name" && t.Zh == "爆燃");
            Expect(texts.Count >= 1400 && ranbao.Count == 0 && baoran, $"全部 {texts.Count} 条文本中文零“燃爆”，反应名统一“爆燃”（FGR-EMG-181）" + Detail(ranbao));

            // 审计器对照：旧文案（第 11 节换掉的写法）必须被抓到；新文案与正式名不误伤。
            string[] mustHit = { "凋亡信号", "命中处留下糖膜", "把酶雾扔到落点", "感染：死亡时生成友方单位", "寄生吻器", "吸血", "增殖腺", "菌网", "Apoptosis signal", "Infected", "parasite", "enzyme mist" };
            string[] mustPass = { SpikesZh, "罐弹抛洒器", "处决", "信号劫持", "胶膜", "回收修复", "爆燃", SpikesEn, "Canister Lobber", "Reactive Armor", "Execute" };
            List<string> missed = mustHit.Where(x => AuditHit(x, x) == null).ToList();
            List<string> wrong = mustPass.Where(x => AuditHit(x, x) != null).ToList();
            Expect(missed.Count == 0 && wrong.Count == 0, "审计器对照：旧生物写法（中 / 英）都能抓到；新名“爆炸反应装甲 / 罐弹抛洒器 / 信号劫持”等不误伤" + Detail(missed.Select(x => "漏抓 " + x).Concat(wrong.Select(x => "误伤 " + x)).ToList()));
        }

        // ── B. 第 11 节文案 ──────────────────────────────────────────────────────

        private static void CheckSection11Texts()
        {
            Line("  · B. 第 11 节文案：爆炸反应装甲 / 反应装甲 / 罐弹抛洒器 / 处决 / 信号劫持；不写远程不触发；爆炸反应装甲写块数与补块（EMG1-FW-01 起第 3 层）；英文能容纳（B16）");
            ComponentCatalog.TryGet(ComponentCatalog.FuncSpikesId, out MechanicalContentDef spikes);
            CarrierReadings.TryGetComponent(ComponentCatalog.FuncSpikesId, out GameConfig.fg.CombatComponent row);
            string flat = row == null ? "?" : row.Damage.ToString("0.#", CultureInfo.InvariantCulture);
            string d = spikes?.Description ?? string.Empty;
            Expect(spikes != null && row != null && spikes.DisplayName == SpikesZh && d.Contains("反伤范围内的攻击者") && d.Contains("起爆") && d.Contains(flat) && d.Contains("一半")
                   && Mathf.Approximately(row.Thorns, 0.5f) && !d.Contains("远程") && !d.Contains("尖刺") && d.Contains("补回 1 块") && d.Contains("块用完时不反伤"),
                $"组件名“{spikes?.DisplayName}”；说明“{d}”：写反伤范围与起爆、数值 = 表（{flat} + 一半），不写“远程攻击不触发”；EMG1-FW-01 块数机制生效同一提交换成第 3 层（写块数与补块，FGR-EMG-233 第 6 条）");

            string thorns = StatusTagCatalog.NameOf("Thorns");
            GameSettings.SetLanguage(GameLanguage.En);
            string thornsEn = StatusTagCatalog.NameOf("Thorns");
            ComponentCatalog.TryGet(ComponentCatalog.FuncSpikesId, out MechanicalContentDef spikesEn);
            ComponentCatalog.TryGet(ComponentCatalog.CompSprayerId, out MechanicalContentDef sprayerEn);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(thorns == "反应装甲" && thornsEn == "Reactive Armor" && StatusTagCatalog.IsMarker("Thorns") && StatusTagCatalog.TryGet("Thorns", out _),
                $"机制标记 Thorns（内部 ID 不改）显示名：{thorns} / {thornsEn}");

            ComponentCatalog.TryGet(ComponentCatalog.CompSprayerId, out MechanicalContentDef sprayer);
            string sd = sprayer?.Description ?? string.Empty;
            Expect(sprayer != null && sprayer.DisplayName == "罐弹抛洒器" && sd.Contains("化学罐弹") && sd.Contains("落地破裂") && !sd.Contains("喷出"),
                $"喷洒器改名“{sprayer?.DisplayName}”：{sd}");

            CarrierReadings.TryGetTagEffect("Dark", out _, out string effect, out float amount);
            string pct = Mathf.RoundToInt(amount * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            string exec = GameText.Get("firmware.fw_execute.desc");
            Expect(effect == "execute" && exec.Contains("结构完整度") && exec.Contains(pct) && exec.Contains("击毁") && FirmwareKinds.DisplayName("fw_execute") == "处决",
                $"处决（名不变）说明：“{exec}”，阈值 {pct} = 表里处决阈值");

            string infected = CodexText("StatusDescription", SimStatus.Infected);
            string parasited = CodexText("StatusDescription", SimStatus.Parasited);
            string infectedName = CodexText("StatusName", SimStatus.Infected);
            Expect(infected.Contains("信号劫持") && infectedName == "信号劫持" && ThemeLexicon.FirstHit(infected) == null && ThemeLexicon.FirstHit(parasited) == null
                   && parasited.Contains("信号") && CodexText("StatusName", SimStatus.Parasited) != infectedName,
                $"图鉴状态说明改信号劫持口径（状态位 Infected / Parasited 不改）：“{infected}”“{parasited}”");

            string enName = spikesEn?.DisplayName ?? string.Empty;
            string enDesc = spikesEn?.Description ?? string.Empty;
            string sprayName = sprayerEn?.DisplayName ?? string.Empty;
            int longestName = ConfigSystem.Instance.Tables.TbLocText.DataList.Where(t => t.Key.EndsWith(".name", StringComparison.Ordinal) && !t.Key.StartsWith("component.", StringComparison.Ordinal))
                .Max(t => (t.En ?? string.Empty).Length);
            Expect(enName == SpikesEn && sprayName == "Canister Lobber" && enName.Length <= longestName && enDesc.Length <= 200 && !Regex.IsMatch(enName + enDesc + sprayName, @"\p{IsCJKUnifiedIdeographs}")
                   && !GameText.ContainsMarker(enName + enDesc + sprayName),
                $"英文：“{enName}”（{enName.Length} 字符 ≤ 现有最长名 {longestName}）、“{sprayName}”；说明 {enDesc.Length} 字符 ≤ 200，不夹中文、不缺键");
        }

        /// <summary>CodexTaxonomy 是热更层 internal 类（旧图鉴字典），自检经反射调它的公开静态方法。</summary>
        private static string CodexText(string method, SimStatus status)
        {
            Type t = typeof(MechanicCodex).Assembly.GetType("GameLogic.Progression.CodexTaxonomy");
            return t?.GetMethod(method)?.Invoke(null, new object[] { status }) as string ?? string.Empty;
        }

        // ── C. 旧存档 ────────────────────────────────────────────────────────────

        private static void CheckOldSave()
        {
            Line("  · C. 旧存档：装着 func_spikes 的蓝图真实文件存读档（正式恢复入口）→ 显示新名、数值不变；存档只存内部 ID");
            GameClock.ResetSession();
            CampaignState s = CampaignState.CreateNew("emg1txt01", "Standard", 1028);
            CampaignSession.Set(Slot, s);
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, ComponentCatalog.FuncSpikesId, null, Array.Empty<string>());
            CombatReading before = CarrierReadings.Build(board.PrimaryId, board.UtilityId, board.ChassisId, Array.Empty<string>(), false);
            s.BlueprintRecords = new[] { new BlueprintRecord { BlueprintId = "bp_emg1txt01_spikes", DisplayName = "bp_emg1txt01_spikes", ActiveVersion = 1, Versions = new[] { board.ToVersion(1, 0f) } } };
            CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            string saved = string.Join("\n", Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("codex_mechanics.json", StringComparison.Ordinal)).Select(File.ReadAllText));

            RestoreResult rr = CampaignRestoreOrchestrator.Restore(Slot);
            BlueprintVersionRecord ver = rr.State?.BlueprintRecords?.FirstOrDefault(x => x.BlueprintId == "bp_emg1txt01_spikes")?.Versions?.FirstOrDefault();
            BlueprintCircuitBoard lb = ver == null ? null : BlueprintCircuitBoard.FromVersion(ver);
            CombatReading after = lb == null ? new CombatReading() : CarrierReadings.Build(lb.PrimaryId, lb.UtilityId, lb.ChassisId, Array.Empty<string>(), false);
            CarrierReadings.TryGetComponent(ComponentCatalog.FuncSpikesId, out GameConfig.fg.CombatComponent row);
            ComponentCatalog.TryGet(lb?.UtilityId, out MechanicalContentDef zh);
            GameSettings.SetLanguage(GameLanguage.En);
            ComponentCatalog.TryGet(lb?.UtilityId, out MechanicalContentDef en);
            string enName = en?.DisplayName;
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(rr.Success && lb != null && lb.UtilityId == ComponentCatalog.FuncSpikesId && zh?.DisplayName == SpikesZh && enName == SpikesEn,
                $"读档后蓝图功能槽仍是 func_spikes，显示“{zh?.DisplayName}” / “{enName}”");
            Expect(row != null && before.Thorns > 0f && Mathf.Approximately(after.Thorns, before.Thorns) && Mathf.Approximately(after.ThornsFlat, before.ThornsFlat)
                   && Mathf.Approximately(after.ThornsReach, before.ThornsReach) && Mathf.Approximately(after.Thorns, row.Thorns) && Mathf.Approximately(after.ThornsFlat, row.Damage)
                   && Mathf.Approximately(after.ThornsReach, row.ThornsReach),
                $"反伤数值不变：比例 {after.Thorns} / 固定 {after.ThornsFlat} / 触及 {after.ThornsReach} = 存档前 = 表");
            Expect(saved.Length > 0 && saved.Contains(ComponentCatalog.FuncSpikesId) && !saved.Contains(SpikesZh) && !saved.Contains("尖刺外装"),
                "存档文件只存内部 ID func_spikes、不存显示名（旧存档读进来一样按新表显示）");
        }

        // ── D. 视觉 ID 与占位标记 ────────────────────────────────────────────────

        private static void CheckVisualIds()
        {
            Line("  · D. 视觉 ID 旧键作别名（同一 Mesh，含组合 marker）、新键在白名单；图鉴占位标记只在占位组件上（DEBT-EMG1TXT01-01）");
            Mesh unknown = SimVisualLibrary.BuildForArtId("emg1txt01/not-a-visual");
            var bad = new List<string>();
            foreach (KeyValuePair<string, string> kv in SimVisualLibrary.LegacyAliases)
            {
                Mesh oldMesh = SimVisualLibrary.BuildForArtId(kv.Key);
                Mesh newMesh = SimVisualLibrary.BuildForArtId(kv.Value);
                if (oldMesh == null || !ReferenceEquals(oldMesh, newMesh) || ReferenceEquals(newMesh, unknown) || !kv.Value.StartsWith("mech/", StringComparison.Ordinal)
                    || !SimVisualLibrary.AllArtIds.Contains(kv.Value) || !SimVisualLibrary.AllArtIds.Contains(kv.Key) || SimVisualLibrary.Canonical(kv.Key) != kv.Value
                    || Regex.IsMatch(kv.Value, "spore|mycel|lyso|spine"))
                {
                    bad.Add($"{kv.Key} → {kv.Value}");
                }
            }
            bool marker = ReferenceEquals(SimVisualLibrary.BuildForArtId("org/spine::relay"), SimVisualLibrary.BuildForArtId("mech/reactive_armor::relay"))
                          && !ReferenceEquals(SimVisualLibrary.BuildForArtId("org/spine::relay"), unknown);
            bool others = SimVisualLibrary.Canonical("summon/phage") == "summon/phage" && SimVisualLibrary.Canonical("org/spine@mech") == "org/spine@mech";
            Expect(SimVisualLibrary.LegacyAliases.Count == 4 && bad.Count == 0 && marker && others,
                "org/spine、org/lyso、summon/spore、summon/mycelium 换机械命名（mech/*），旧键仍能解析到同一个 Mesh（含 org/spine::relay 组合）；未列入的键原样" + Detail(bad));

            MechanicCodexEntry spikes = MechanicCodex.Find(MechanicCodex.ComponentEntryId(ComponentCatalog.FuncSpikesId));
            MechanicCodexEntry gun = MechanicCodex.Find(MechanicCodex.ComponentEntryId(ComponentCatalog.CompGunId));
            string note = ComponentCatalog.ArtPlaceholderNote(ComponentCatalog.FuncSpikesId);
            string sb = MechanicCodex.Body(spikes, null);
            string gb = MechanicCodex.Body(gun, null);
            int marked = ComponentCatalog.All.Keys.Count(id => ComponentCatalog.ArtPlaceholderNote(id) != null);
            Expect(note != null && !GameText.ContainsMarker(note) && sb.Contains(note) && MechanicCodex.Title(spikes) == SpikesZh && gb.Length > 0 && !gb.Contains(note)
                   && ComponentCatalog.ArtPlaceholderNote(ComponentCatalog.CompGunId) == null && ComponentCatalog.ArtPlaceholderNote(null) == null && marked == 1,
                $"图鉴“{MechanicCodex.Title(spikes)}”正文带占位标记“{note}”；连射器没有；全目录只有 {marked} 个组件带占位标记（删掉文本键即视为已替换）");
        }

        // ── 小工具 ──────────────────────────────────────────────────────────────

        private static string ReadDoc(string rel)
        {
            // 影子工程（.unity-validate-clone）里 dataPath 不在仓库内：从当前目录往上找仓库根（与 FgFirmwareMigrationSelfCheck 同一做法）。
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    string p = Path.Combine(dir.FullName, rel.Replace('/', Path.DirectorySeparatorChar));
                    return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
                }
            }
            return null;
        }

        private static string Detail(List<string> items) => items.Count == 0 ? string.Empty : "：" + string.Join("；", items.Take(12));

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool ok, string what)
        {
            if (ok)
            {
                _pass++;
                Line("    ✓ " + what);
            }
            else
            {
                Fail(what);
            }
        }

        private static void Fail(string what)
        {
            _fail++;
            Line("    ✗ " + what);
        }

        private static void Line(string s) => _report?.AppendLine(s);
    }
}
