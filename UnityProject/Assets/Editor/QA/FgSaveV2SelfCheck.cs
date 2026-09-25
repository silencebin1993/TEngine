using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Primitive;
using GameLogic.Localization;
using GameLogic.Settings;
using Luban;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-SAVE-01 存档 v2 骨架的自动验收（FGR-ARC-008；FGR-SYS-001～004、007；FGT-SYS-001、002 基础版本）。
    ///
    /// 全部断言走真实的 <see cref="CampaignSaveService"/> 读写真实文件（临时目录，不碰玩家槽位），
    /// 行为坏了会失败：
    /// - 格式与状态域：v2 头部、卡片参与校验、16 个新状态域逐个往返、旧正文缺域时补齐、登记表与字段一一对应。
    /// - 逐级升级：注入假想的 v2→v3→v4 升级链，证明升级器按顺序真的执行、结果正确、原文件不动、下次存档保留旧文件；
    ///   负向：缺级、升级器抛异常 / 返回空、重复 / 越界登记、比当前更新的存档。
    /// - Demo 存档：用真实 Demo 存档文件（Fixtures/DemoSave_v1_slot.json.txt）与 v1 写法各读一次——明确提示、
    ///   不读入、不参与"继续"、文件逐字节不变；覆盖前另存保留。
    /// - 写入中途强制结束（模拟崩溃点，清理代码不执行）：主档保持上一版，可继续读；主档被截断时从备份恢复，
    ///   坏主档另存保留（永不自动删除存档）。
    /// - 内容迁移：已移除内容转成废料（经资源账本）、合成预留先取消、蓝图里的保留并通知、未登记的退 0 并通知、
    ///   重复读档不重复发放、表不可用时什么都不转换。
    /// - 存档卡：新字段与中英文文字、各状态无缺键标记；暂停与倍速不影响存档结果；存 / 读 / 列表耗时。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgSaveV2SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static string _dir;

        private const string RemovedId = "organ_removed_selfcheck";
        private const string UnlistedId = "organ_unlisted_selfcheck";

        [MenuItem("BinGames/自检：FG 存档 v2")]
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
            Line("\n[存档v2] 存档 v2 骨架（FG0-SAVE-01）");

            GameLanguage originalLanguage = GameSettings.Language;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsave-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;

                CheckFormatAndDomains();
                CheckMigrationChain();
                CheckMigrationJsonTool();
                CheckNewerSave();
                CheckDemoSaves();
                CheckCrashAndBackup();
                CheckPreserveGuards();
                CheckContentReconcile();
                CheckLoadNoticeOverflow();
                CheckCardFields();
                CheckTimeScaleIndependence();
                CheckPerformance();
            }
            catch (Exception e)
            {
                Fail($"存档 v2 自检抛异常：{e}");
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                CampaignSaveService.CrashPointForTests = SaveCrashPoint.None;
                CampaignSaveMigrations.OverrideForTests = null;
                SaveContentReconciler.ResetForTests();
                MachineRegistry.ResetForNewCampaign(); // 恢复编排器会把机器登记表换成测试存档的内容
                GameSettings.SetLanguage(originalLanguage);
                GameLogic.Core.StrategyClock.Reset();
                GameLogic.Core.InputRouter.SetGameplayPaused(false);
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch
                {
                    // 临时目录清理失败不影响结论。
                }
            }
            return _fail;
        }

        // ── 1. 格式与状态域 ─────────────────────────────────────────────────

        private static void CheckFormatAndDomains()
        {
            Line("  · v2 格式与新状态域（FGR-ARC-008）");
            Expect(CampaignSaveService.CurrentSchemaVersion == 2 && CampaignSaveMigrations.FirstFullGameSchemaVersion == 2
                   && CampaignSaveMigrations.LastDemoSchemaVersion == 1,
                "正式版存档格式 v2，Demo 为 v1");
            SaveMigrationChain prod = CampaignSaveMigrations.Production;
            Expect(prod.MinVersion == 2 && prod.TargetVersion == CampaignSaveService.CurrentSchemaVersion && prod.MissingSteps.Length == 0,
                $"正式升级链 v{prod.MinVersion}→v{prod.TargetVersion} 没有缺级（缺：{string.Join(",", prod.MissingSteps)}）");

            int slot = 0;
            CampaignState s = CampaignState.CreateNew("fgsave-format", "Standard", 13579);
            Expect(s.World != null && s.World.WorldSeed == 13579 && s.World.GeneratorVersion == 0 && s.Progress.Act == 1 && s.Clock.Day == 0,
                "新建战役：世界种子 = 战役种子，生成器版本 0（Demo 固定地图），第 1 幕，时钟未接入（Day=0）");
            FillDomains(s);
            string expectDomains = DomainsJson(s);
            SaveResult saved = CampaignSaveService.Save(slot, s, SaveReason.Manual);
            string file = File.ReadAllText(CampaignSaveService.SlotPath(slot));
            TestEnvelope env = JsonUtility.FromJson<TestEnvelope>(file);
            Expect(saved.Success && env.SchemaVersion == 2 && env.ProductVersion == "0.2" && !string.IsNullOrEmpty(env.CardJson)
                   && env.Checksum == CampaignSaveService.ComputeChecksum(env.PayloadJson, env.CardJson),
                $"写出的文件：SchemaVersion={env.SchemaVersion}、ProductVersion={env.ProductVersion}、带卡片头、校验和覆盖正文与卡片");

            LoadResult loaded = CampaignSaveService.Load(slot);
            Expect(loaded.Success && DomainsJson(loaded.State) == expectDomains,
                $"17 个新状态域逐个往返一致（世界种子 / 生成器版本 / 区块差异 / 幕 / 日 / 读档通知 / 通知历史（FG0-UX-01）/ 各域版本号）");

            // 登记表与 CampaignState 字段一一对应：新增域忘了登记会在这里失败。
            var domainTypes = new HashSet<Type>(typeof(CampaignFgStateDomains).Assembly.GetTypes()
                .Where(t => t.Namespace == "GameLogic.Campaign" && t.IsClass && t.Name.EndsWith("State", StringComparison.Ordinal)
                            && t.GetField("DomainVersion") != null));
            string[] fields = typeof(CampaignState).GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => domainTypes.Contains(f.FieldType)).Select(f => f.Name).OrderBy(n => n).ToArray();
            string[] registered = CampaignFgStateDomains.All.Select(d => d.FieldName).OrderBy(n => n).ToArray();
            Expect(fields.Length == 17 && fields.SequenceEqual(registered) && CampaignFgStateDomains.All.All(d => !string.IsNullOrEmpty(d.OwnerStory)),
                $"CampaignState 上 {fields.Length} 个状态域全部登记且写明承接 Story（{string.Join("、", fields)}）");

            // 旧正文缺域（比如 v2 早期存档还没有某个域）：读档补空域，不是 null。
            string payload = SaveMigrationJson.RemoveField(SaveMigrationJson.RemoveField(env.PayloadJson, "Grid"), "World");
            WriteEnvelope(slot, 2, payload, env.CardJson);
            LoadResult missing = CampaignSaveService.Load(slot);
            Expect(missing.Success && missing.State.Grid != null && missing.State.World != null && missing.State.World.ChunkDiffs != null
                   && CampaignFgStateDomains.All.All(d => d.Get(missing.State) != null),
                "正文缺少 Grid / World 域时读档补成空域，任何域都不是 null");
        }

        private static void FillDomains(CampaignState s)
        {
            s.World.GeneratorVersion = 3;
            s.World.WorldSettingsId = "selfcheck";
            s.World.ChunkDiffs = new[]
            {
                new ChunkDiffRecord { SurfaceId = "surface_main", ChunkX = 2, ChunkY = -1, DiffPayload = "{\"tiles\":[1,2]}" },
                new ChunkDiffRecord { SurfaceId = "surface_main", ChunkX = -5, ChunkY = 4, DiffPayload = "x" },
            };
            s.Progress.Act = 2;
            s.Progress.IsPostgame = true;
            s.Clock.GameSeconds = 12345.5;
            s.Clock.Day = 11;
            int v = 10;
            foreach (CampaignFgStateDomains.DomainInfo d in CampaignFgStateDomains.All)
            {
                object o = d.Get(s);
                o.GetType().GetField("DomainVersion").SetValue(o, v++);
            }
            s.SaveHistory.Notices = new[]
            {
                new SaveNoticeRecord { NoticeId = "n1", TextKey = "save.notice.unknown_content", Args = new[] { "a" }, FromContentVersion = 1, ToContentVersion = 1, CreatedAtUtc = "2026-09-25T00:00:00Z" },
            };
        }

        private static string DomainsJson(CampaignState s)
        {
            s.NormalizeForSave();
            return string.Join("|", CampaignFgStateDomains.All.Select(d => d.FieldName + "=" + JsonUtility.ToJson(d.Get(s))));
        }

        // ── 2. 逐级升级（FGT-SYS-002 基础版本）────────────────────────────

        private sealed class TestStep : ISaveMigrationStep
        {
            private readonly Func<string, string> _fn;
            private readonly List<int> _log;

            public TestStep(int from, string description, Func<string, string> fn, List<int> log)
            {
                FromVersion = from;
                Description = description;
                _fn = fn;
                _log = log;
            }

            public int FromVersion { get; }
            public string Description { get; }

            public string Migrate(string payloadJson)
            {
                _log?.Add(FromVersion);
                return _fn(payloadJson);
            }
        }

        private static void CheckMigrationChain()
        {
            Line("  · 逐级升级器（FGR-SYS-004 / FGT-SYS-002）");
            int slot = 1;
            CampaignState s = CampaignState.CreateNew("fgsave-migrate", "Standard", 2468);
            s.Scrap = 321;
            s.TechData = 17;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);

            // 伪造"v2 旧格式"：废料字段在 v2 叫 OldScrap，科技数据在 v4 改成两倍单位。
            TestEnvelope env = ReadEnvelope(slot);
            string v2Payload = SaveMigrationJson.RenameField(env.PayloadJson, "Scrap", "OldScrap");
            WriteEnvelope(slot, 2, v2Payload, env.CardJson);
            byte[] original = File.ReadAllBytes(CampaignSaveService.SlotPath(slot));

            var calls = new List<int>();
            var chain = new SaveMigrationChain(2, 4, new ISaveMigrationStep[]
            {
                new TestStep(3, "v3→v4 科技数据单位 ×2", j =>
                {
                    int td = int.Parse(SaveMigrationJson.GetRaw(j, "TechData"), CultureInfo.InvariantCulture);
                    return SaveMigrationJson.SetRaw(j, "TechData", (td * 2).ToString(CultureInfo.InvariantCulture));
                }, calls),
                new TestStep(2, "v2→v3 OldScrap 改名 Scrap", j => SaveMigrationJson.RenameField(j, "OldScrap", "Scrap"), calls),
            });
            CampaignSaveMigrations.OverrideForTests = chain;
            try
            {
                CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
                LoadResult r = CampaignSaveService.Load(slot);
                Expect(meta.State == CampaignSlotState.Ready && r.Success && r.FileSchemaVersion == 2 && r.State.SchemaVersion == 4
                       && calls.SequenceEqual(new[] { 2, 3 }) && r.State.Scrap == 321 && r.State.TechData == 34,
                    $"v2 存档经 v2→v3→v4 按顺序升级（执行顺序 {string.Join("→", calls)}）：废料 {r.State?.Scrap}（改名后保留）、科技数据 {r.State?.TechData}（×2）");
                Expect(File.ReadAllBytes(CampaignSaveService.SlotPath(slot)).SequenceEqual(original),
                    "读档升级不改动磁盘上的原文件");

                SaveResult resave = CampaignSaveService.Save(slot, r.State, SaveReason.Manual);
                string[] kept = CampaignSaveService.PreservedFiles(slot);
                Expect(resave.Success && ReadEnvelope(slot).SchemaVersion == 4 && kept.Any(k => k.Contains(".keep-v2-") && File.ReadAllBytes(k).SequenceEqual(original)),
                    $"升级后第一次存档写成 v4，旧 v2 文件另存保留（{Path.GetFileName(resave.PreservedPath ?? "无")}），逐字节等于原文件");

                // 负向：缺级。
                WriteEnvelope(slot, 2, v2Payload, env.CardJson);
                byte[] before = File.ReadAllBytes(CampaignSaveService.SlotPath(slot));
                CampaignSaveMigrations.OverrideForTests = new SaveMigrationChain(2, 4, new ISaveMigrationStep[]
                {
                    new TestStep(2, "只有 v2→v3", j => SaveMigrationJson.RenameField(j, "OldScrap", "Scrap"), null),
                });
                CampaignSlotMetadata missMeta = CampaignSaveService.GetSlotMetadata(slot);
                LoadResult miss = CampaignSaveService.Load(slot);
                string missText = CampaignSlotText.CardText(missMeta);
                Expect(missMeta.State == CampaignSlotState.Incompatible && missMeta.FailureReason == SaveFailureReason.MigrationMissing
                       && miss.Outcome == LoadOutcome.Incompatible && miss.MigrationFailedFromVersion == 3 && miss.State == null
                       && missText.Contains("缺少从存档格式") && CampaignSlotText.StartsNewInSlot(missMeta),
                    $"缺少 v3→v4 升级器：卡片显示“{Flat(missText)}”，读档拒绝，不给出半个战役；按钮只能“新建于此槽”（先确认，原文件另存保留）");

                // 负向：升级器抛异常 / 返回空。
                CampaignSaveMigrations.OverrideForTests = new SaveMigrationChain(2, 3, new ISaveMigrationStep[]
                {
                    new TestStep(2, "抛异常", j => throw new InvalidOperationException("boom"), null),
                });
                LoadResult boom = CampaignSaveService.Load(slot);
                CampaignSaveMigrations.OverrideForTests = new SaveMigrationChain(2, 3, new ISaveMigrationStep[]
                {
                    new TestStep(2, "返回空", j => string.Empty, null),
                });
                LoadResult empty = CampaignSaveService.Load(slot);
                Expect(boom.Outcome == LoadOutcome.Corrupt && boom.Reason == SaveFailureReason.MigrationFailed && boom.MigrationFailedFromVersion == 2
                       && empty.Outcome == LoadOutcome.Corrupt && empty.Reason == SaveFailureReason.MigrationFailed
                       && File.ReadAllBytes(CampaignSaveService.SlotPath(slot)).SequenceEqual(before),
                    "升级器抛异常 / 返回空：读档失败（原因：升级失败），原文件逐字节不变");
                string failText = CampaignSlotText.ReasonText(SaveFailureReason.MigrationFailed, 2);
                Expect(failText.Contains("2") && Readable(failText), $"升级失败的玩家文字：“{failText}”");
            }
            finally
            {
                CampaignSaveMigrations.OverrideForTests = null;
            }

            Expect(Throws(() => new SaveMigrationChain(2, 4, new ISaveMigrationStep[] { new TestStep(2, "a", j => j, null), new TestStep(2, "b", j => j, null) }))
                   && Throws(() => new SaveMigrationChain(2, 4, new ISaveMigrationStep[] { new TestStep(4, "越界", j => j, null) }))
                   && Throws(() => new SaveMigrationChain(2, 4, new ISaveMigrationStep[] { new TestStep(1, "Demo 不迁移", j => j, null) })),
                "升级链拒绝重复登记、越界登记与“从 Demo v1 升级”的升级器");
        }

        private static void CheckMigrationJsonTool()
        {
            const string json = "{\"A\":1,\"Name\":\"x}\\\"{,\",\"Nested\":{\"A\":2,\"B\":[1,{\"C\":\"]\"}]},\"Z\":true}";
            string renamed = SaveMigrationJson.RenameField(json, "A", "Alpha");
            string set = SaveMigrationJson.SetRaw(json, "New", "[1,2]");
            string replaced = SaveMigrationJson.SetRaw(json, "Z", "false");
            string removedMid = SaveMigrationJson.RemoveField(json, "Nested");
            string removedLast = SaveMigrationJson.RemoveField(json, "Z");
            bool ok = SaveMigrationJson.GetRaw(renamed, "Alpha") == "1" && SaveMigrationJson.GetRaw(renamed, "A") == null
                      && SaveMigrationJson.GetRaw(renamed, "Nested").Contains("\"A\":2")
                      && SaveMigrationJson.GetRaw(json, "Name") == "\"x}\\\"{,\""
                      && SaveMigrationJson.GetRaw(set, "New") == "[1,2]"
                      && SaveMigrationJson.GetRaw(replaced, "Z") == "false"
                      && !SaveMigrationJson.HasField(removedMid, "Nested") && SaveMigrationJson.GetRaw(removedMid, "Z") == "true"
                      && !SaveMigrationJson.HasField(removedLast, "Z") && SaveMigrationJson.GetRaw(removedLast, "Nested") != null
                      && Throws(() => SaveMigrationJson.GetRaw("{\"A\":1,\"B\":\"unterminated", "A"))
                      && Throws(() => SaveMigrationJson.RenameField(json, "A", "Z"));
            Expect(ok, "升级器 JSON 工具：只改顶层成员；字符串里的括号 / 引号 / 转义不误判；截断的 JSON 抛异常；改名撞名抛异常");
        }

        // ── 3. 比当前更新的存档 ───────────────────────────────────────────

        private static void CheckNewerSave()
        {
            Line("  · 读取版本号更新的存档（负向）");
            int slot = 2;
            CampaignState s = CampaignState.CreateNew("fgsave-newer", "Standard", 1);
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            TestEnvelope env = ReadEnvelope(slot);
            WriteEnvelope(slot, 5, env.PayloadJson, env.CardJson);
            byte[] before = File.ReadAllBytes(CampaignSaveService.SlotPath(slot));
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            LoadResult r = CampaignSaveService.Load(slot);
            string text = CampaignSlotText.CardText(meta);
            Expect(meta.State == CampaignSlotState.Incompatible && meta.FailureReason == SaveFailureReason.Newer && r.Outcome == LoadOutcome.Incompatible
                   && r.State == null && text.Contains("更新的游戏版本") && text.Contains("5")
                   && CampaignSlotText.StartsNewInSlot(meta) && CampaignSlotText.ActionLabel(meta) == "新建于此槽"
                   && CampaignSaveService.ResolveContinueSlot() != slot,
                $"v5 存档：卡片“{Flat(text)}”，拒绝读取，不参与“继续”；按钮“{CampaignSlotText.ActionLabel(meta)}”（先确认，原文件另存保留）");
            Expect(File.ReadAllBytes(CampaignSaveService.SlotPath(slot)).SequenceEqual(before), "拒绝读取后文件逐字节不变");
            SaveResult over = CampaignSaveService.Save(slot, CampaignState.CreateNew("fgsave-newer-over", "Standard", 2), SaveReason.NewCampaign);
            Expect(over.Success && over.PreservedPath != null && over.PreservedPath.Contains(".keep-v5-") && File.ReadAllBytes(over.PreservedPath).SequenceEqual(before),
                "在这个槽位新建战役：更新版本的旧文件先另存为 .keep-v5-*，不会被覆盖掉");
            Clear(slot);
        }

        // ── 4. Demo 存档 ─────────────────────────────────────────────────

        private static void CheckDemoSaves()
        {
            Line("  · Demo（0.1）存档：明确提示、不迁移、不删除");
            string fixture = Path.Combine(Application.dataPath, "Editor/QA/Fixtures/DemoSave_v1_slot.json.txt");
            if (!File.Exists(fixture))
            {
                Fail($"缺少真实 Demo 存档夹具 {fixture}");
                return;
            }
            int slot = 0;
            for (int i = 0; i < CampaignSaveService.SlotCount; i++)
            {
                Clear(i); // 只留 Demo 存档，才能验证"只有 Demo 存档时"的“继续”原因
            }
            Directory.CreateDirectory(_dir);
            File.Copy(fixture, CampaignSaveService.SlotPath(slot), true);
            byte[] demoBytes = File.ReadAllBytes(CampaignSaveService.SlotPath(slot));

            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            LoadResult r = CampaignSaveService.Load(slot);
            RestoreResult restore = CampaignRestoreOrchestrator.Restore(slot);
            string card = CampaignSlotText.CardText(meta);
            Expect(meta.State == CampaignSlotState.DemoSave && r.Outcome == LoadOutcome.DemoSave && r.State == null && r.Reason == SaveFailureReason.DemoSave
                   && !restore.Success && restore.LoadOutcome == LoadOutcome.DemoSave,
                "真实 Demo 存档（2026-09-22 Demo 写出）：槽位显示 Demo 存档，读档与恢复编排都返回 DemoSave，不产生战役状态");
            Expect(card.Contains("Demo（0.1）") && card.Contains("请新建战役") && card.Contains("不会被删除")
                   && CampaignSlotText.ActionEnabled(meta) && CampaignSlotText.StartsNewInSlot(meta) && CampaignSlotText.ActionLabel(meta) == "新建于此槽"
                   && !GameText.ContainsMarker(card),
                $"存档卡提示：“{Flat(card)}”；按钮“{CampaignSlotText.ActionLabel(meta)}”（不能读取，只能先确认再在此槽新建，Demo 槽位不会被永久占住）");
            Expect(CampaignSaveService.ResolveContinueSlot() < 0 && CampaignSaveService.AnyDemoSave()
                   && CampaignSlotText.ContinueUnavailable(true).Contains("Demo"),
                $"只有 Demo 存档时“继续”不可用，原因：“{CampaignSlotText.ContinueUnavailable(true)}”");
            Expect(File.ReadAllBytes(CampaignSaveService.SlotPath(slot)).SequenceEqual(demoBytes), "读取 / 列表之后 Demo 文件逐字节不变");

            // v1 写法（Demo 代码的格式，正文是完整 CampaignState）也识别为 Demo。
            int slot2 = 1;
            Clear(slot2);
            CampaignState legacy = CampaignState.CreateNew("demo-v1", "Standard", 9);
            legacy.SchemaVersion = 1;
            string payload = JsonUtility.ToJson(legacy);
            File.WriteAllText(CampaignSaveService.SlotPath(slot2), JsonUtility.ToJson(new TestEnvelope
            {
                SchemaVersion = 1, ContentVersion = 1, Checksum = "demo", WrittenAtUtc = "2026-09-20T01:02:03Z", PayloadJson = payload,
            }));
            Expect(CampaignSaveService.GetSlotMetadata(slot2).State == CampaignSlotState.DemoSave && CampaignSaveService.Load(slot2).Outcome == LoadOutcome.DemoSave,
                "按 Demo 写法（v1 头部）写出的存档同样识别为 Demo 存档");

            // 覆盖确认 + 覆盖时另存保留。
            string confirm = CampaignSlotText.OverwriteConfirm(meta);
            Expect(confirm.Contains("另存为") && confirm.Contains("campaign_slot0.json.keep-*") && !GameText.ContainsMarker(confirm),
                $"在 Demo 槽位新建的确认框写明原文件会另存保留：“{Flat(confirm)}”");
            SaveResult over = CampaignSaveService.Save(slot, CampaignState.CreateNew("fgsave-over-demo", "Standard", 5), SaveReason.NewCampaign);
            Expect(over.Success && over.PreservedPath != null && over.PreservedPath.Contains(".keep-demo-v1-")
                   && File.ReadAllBytes(over.PreservedPath).SequenceEqual(demoBytes)
                   && CampaignSaveService.GetSlotMetadata(slot).State == CampaignSlotState.Ready,
                $"确认后新建：Demo 文件另存为 {Path.GetFileName(over.PreservedPath ?? "无")}（逐字节相同），槽位变为可读的 v2 存档");

            // 备份是 Demo、主档坏了：不能提供"读取备份"。
            File.Copy(fixture, CampaignSaveService.BakPath(slot2), true);
            File.WriteAllText(CampaignSaveService.SlotPath(slot2), "{\"SchemaVersion\":2,\"Payl");
            CampaignSlotMetadata bad = CampaignSaveService.GetSlotMetadata(slot2);
            string badText = CampaignSlotText.CardText(bad);
            Expect(bad.State == CampaignSlotState.Corrupt && !bad.HasBackup && bad.BackupState == CampaignSlotState.DemoSave
                   && !CampaignSaveService.RestoreFromBak(slot2) && badText.Contains("备份也无法使用")
                   && CampaignSlotText.StartsNewInSlot(bad) && CampaignSlotText.ActionLabel(bad) == "新建于此槽",
                $"主档损坏且备份是 Demo 存档：不提供读取备份（“{Flat(badText)}”），按钮是“新建于此槽”");
            Clear(slot);
            Clear(slot2);
        }

        // ── 5. 写入中途强制结束 / 备份恢复（FGT-SYS-001 基础版本）────────────

        private static void CheckCrashAndBackup()
        {
            Line("  · 写入中途强制结束与备份恢复（FGR-SYS-003 / FGT-SYS-001）");
            int slot = 0;
            Clear(slot);
            CampaignState s = CampaignState.CreateNew("fgsave-crash", "Standard", 77);
            s.Scrap = 77;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            string goodA = StateJson(CampaignSaveService.Load(slot).State);

            foreach (SaveCrashPoint point in new[] { SaveCrashPoint.MidTempWrite, SaveCrashPoint.AfterTempWritten })
            {
                s.Scrap = 88;
                bool crashed = false;
                CampaignSaveService.CrashPointForTests = point;
                try
                {
                    CampaignSaveService.Save(slot, s, SaveReason.Manual);
                }
                catch (SimulatedSaveCrashException)
                {
                    crashed = true;
                }
                finally
                {
                    CampaignSaveService.CrashPointForTests = SaveCrashPoint.None;
                }
                // "下次启动"：全新的读档调用。
                LoadResult next = CampaignSaveService.Load(slot);
                bool tmpLeft = File.Exists(CampaignSaveService.TempPath(slot));
                Expect(crashed && next.Success && next.State.Scrap == 77 && StateJson(next.State) == goodA && tmpLeft
                       && CampaignSaveService.GetSlotMetadata(slot).State == CampaignSlotState.Ready,
                    $"在 {point} 处强制结束：主档仍是上一次完整存档（废料 {next.State?.Scrap}），可直接读取；残留的临时文件不影响读取");
            }
            s.Scrap = 99;
            SaveResult after = CampaignSaveService.Save(slot, s, SaveReason.Manual);
            Expect(after.Success && !File.Exists(CampaignSaveService.TempPath(slot)) && CampaignSaveService.Load(slot).State.Scrap == 99,
                "崩溃后下一次正常存档成功，残留临时文件被这次写入接管");

            // 第一次存档就被强制结束：槽位仍是空槽，不会出现半个存档。
            int fresh = 2;
            Clear(fresh);
            CampaignSaveService.CrashPointForTests = SaveCrashPoint.MidTempWrite;
            try
            {
                CampaignSaveService.Save(fresh, s, SaveReason.NewCampaign);
            }
            catch (SimulatedSaveCrashException)
            {
            }
            finally
            {
                CampaignSaveService.CrashPointForTests = SaveCrashPoint.None;
            }
            Expect(!File.Exists(CampaignSaveService.SlotPath(fresh)) && CampaignSaveService.GetSlotMetadata(fresh).State == CampaignSlotState.Empty
                   && CampaignSaveService.Save(fresh, s, SaveReason.NewCampaign).Success,
                "首次存档写到一半被强制结束：槽位仍是空槽，之后可正常新建");
            Clear(fresh);

            // 主档被截断（非原子文件系统 / 磁盘损坏）：从备份恢复，坏主档另存保留。
            s.Scrap = 111;
            CampaignSaveService.Save(slot, s, SaveReason.Manual); // 现在 bak = 废料 99，主档 = 111
            string goodBak = StateJson(CampaignSaveService.Load(slot).State);
            s.Scrap = 222;
            CampaignSaveService.Save(slot, s, SaveReason.Manual); // bak = 111，主档 = 222
            string main = CampaignSaveService.SlotPath(slot);
            string text = File.ReadAllText(main);
            File.WriteAllText(main, text.Substring(0, text.Length / 2));
            byte[] torn = File.ReadAllBytes(main);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            LoadResult corrupt = CampaignSaveService.Load(slot);
            string card = CampaignSlotText.CardText(meta);
            Expect(meta.State == CampaignSlotState.Corrupt && meta.FailureReason == SaveFailureReason.Truncated && meta.HasBackup
                   && corrupt.Outcome == LoadOutcome.Corrupt && corrupt.State == null
                   && card.Contains("文件不完整") && card.Contains("可以读取上一版备份") && CampaignSlotText.ActionLabel(meta) == "读取备份"
                   && CampaignSlotText.ActionEnabled(meta),
                $"主档被截断：卡片“{Flat(card)}”，按钮“{CampaignSlotText.ActionLabel(meta)}”");
            bool restored = CampaignSaveService.RestoreFromBak(slot, out string keptCorrupt);
            LoadResult back = CampaignSaveService.Load(slot);
            Expect(restored && back.Success && back.State.Scrap == 111 && keptCorrupt != null && File.Exists(keptCorrupt)
                   && File.ReadAllBytes(keptCorrupt).SequenceEqual(torn),
                $"读取备份：恢复到上一版（废料 {back.State?.Scrap}），截断的主档另存为 {Path.GetFileName(keptCorrupt ?? "无")}（永不自动删除）");
            Expect(StateJson(back.State) == goodBak, "恢复出的备份与倒数第二次完整存档读出的状态逐字段一致");

            // 主档丢失、备份还在：不显示成空槽。
            File.Delete(main);
            CampaignSlotMetadata lost = CampaignSaveService.GetSlotMetadata(slot);
            LoadResult lostLoad = CampaignSaveService.Load(slot);
            Expect(lost.State == CampaignSlotState.Corrupt && lost.FailureReason == SaveFailureReason.MainMissing && lost.HasBackup
                   && lostLoad.Reason == SaveFailureReason.MainMissing && CampaignSlotText.CardText(lost).Contains("主存档文件丢失")
                   && CampaignSaveService.RestoreFromBak(slot) && CampaignSaveService.Load(slot).Success,
                "主档丢失但备份在：显示“主存档文件丢失 + 可以读取备份”，读取备份后可继续");

            // 其余损坏原因各有稳定文字。
            File.WriteAllText(main, string.Empty);
            SaveFailureReason emptyReason = CampaignSaveService.GetSlotMetadata(slot).FailureReason;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            TestEnvelope env = ReadEnvelope(slot);
            WriteEnvelope(slot, 2, env.PayloadJson.Replace("\"Scrap\":222", "\"Scrap\":999"), env.CardJson, env.Checksum);
            SaveFailureReason tamperedPayload = CampaignSaveService.GetSlotMetadata(slot).FailureReason;
            WriteEnvelope(slot, 2, env.PayloadJson, env.CardJson.Replace("\"Act\":1", "\"Act\":3"), env.Checksum);
            SaveFailureReason tamperedCard = CampaignSaveService.GetSlotMetadata(slot).FailureReason;
            WriteEnvelope(slot, 2, "{\"Scrap\":", env.CardJson);
            // 头部可读、读档才失败：读档前列表只确认备份文件存在（不整份校验）；读档失败后按读档结果显示为损坏并完整校验备份。
            string fixture = Path.Combine(Application.dataPath, "Editor/QA/Fixtures/DemoSave_v1_slot.json.txt");
            File.Copy(fixture, CampaignSaveService.BakPath(slot), true);
            CampaignSlotMetadata listed = CampaignSaveService.GetSlotMetadata(slot);
            bool listedUnverified = listed.State == CampaignSlotState.Ready && listed.HasBackup && !listed.BackupVerified;
            SaveFailureReason badPayload = CampaignSaveService.Load(slot).Reason;
            Expect(emptyReason == SaveFailureReason.EmptyFile && tamperedPayload == SaveFailureReason.Checksum && tamperedCard == SaveFailureReason.Checksum
                   && badPayload == SaveFailureReason.Payload,
                $"空文件 → {emptyReason}；改了正文 → {tamperedPayload}；改了卡片 → {tamperedCard}；正文坏但校验对 → {badPayload}");
            CampaignSlotMetadata afterFail = CampaignSaveService.GetSlotMetadata(slot);
            Expect(listedUnverified && afterFail.State == CampaignSlotState.Corrupt && afterFail.FailureReason == SaveFailureReason.Payload
                   && afterFail.BackupVerified && !afterFail.HasBackup && afterFail.BackupState == CampaignSlotState.DemoSave
                   && CampaignSlotText.StartsNewInSlot(afterFail) && CampaignSaveService.ResolveContinueSlot() != slot,
                "主档头部可读时列表只确认备份文件存在；读档失败后槽位显示为损坏（不参与“继续”）并完整校验备份，发现是 Demo 存档 → 不提供读取备份");
            var reasonTexts = new List<string>();
            foreach (SaveFailureReason reason in Enum.GetValues(typeof(SaveFailureReason)))
            {
                if (reason == SaveFailureReason.None)
                {
                    continue;
                }
                string t = CampaignSlotText.ReasonText(reason, 3);
                if (!Readable(t))
                {
                    reasonTexts.Add($"{reason}=“{t}”");
                }
            }
            Expect(reasonTexts.Count == 0, "每个损坏原因都有可读的中文文字（无缺键、无英文枚举）" + (reasonTexts.Count == 0 ? string.Empty : "：" + string.Join("；", reasonTexts)));

            // 在"主档损坏、备份完好"的槽位新建战役（玩家确认覆盖）：旧主档与完好的备份都另存保留，不被 File.Replace 挤掉。
            Clear(slot);
            s.Scrap = 5;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            s.Scrap = 6;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            byte[] goodBakBytes = File.ReadAllBytes(CampaignSaveService.BakPath(slot));
            string t6 = File.ReadAllText(main);
            File.WriteAllText(main, t6.Substring(0, t6.Length - 40));
            byte[] tornBytes = File.ReadAllBytes(main);
            SaveResult fresh2 = CampaignSaveService.Save(slot, CampaignState.CreateNew("fgsave-over-torn", "Standard", 8), SaveReason.NewCampaign);
            string[] keeps = CampaignSaveService.PreservedFiles(slot);
            Expect(fresh2.Success && keeps.Any(k => File.ReadAllBytes(k).SequenceEqual(goodBakBytes)) && keeps.Any(k => File.ReadAllBytes(k).SequenceEqual(tornBytes))
                   && CampaignSaveService.GetSlotMetadata(slot).State == CampaignSlotState.Ready,
                $"在坏档槽位新建战役：坏主档与完好备份都另存保留（{string.Join("、", keeps.Select(Path.GetFileName))}）");
            Clear(slot);
        }

        // ── 5b. 覆盖前的保留：主档丢失时新建、外来备份、坏主档、File.Replace 被打断、读档失败后读取备份 ──────

        private static void CheckPreserveGuards()
        {
            Line("  · 永不自动删除存档：主档丢失时新建 / 外来备份 / 坏主档 / 替换被打断 / 读档失败后读取备份（FGR-SYS-003）");
            int slot = 1;
            string fixture = Path.Combine(Application.dataPath, "Editor/QA/Fixtures/DemoSave_v1_slot.json.txt");

            // (1) 主档丢失、备份可读：卡片是"读取备份"；玩家仍选择在此新建（确认框）→ 新建 + 两次常规存档后，旧备份以 keep-bak 保留。
            Clear(slot);
            CampaignState old = CampaignState.CreateNew("fgsave-lost-old", "Standard", 61);
            CampaignSaveService.Save(slot, old, SaveReason.Manual);
            old.Scrap = 62;
            CampaignSaveService.Save(slot, old, SaveReason.Manual);
            byte[] oldBak = File.ReadAllBytes(CampaignSaveService.BakPath(slot));
            File.Delete(CampaignSaveService.SlotPath(slot));
            CampaignSlotMetadata lost = CampaignSaveService.GetSlotMetadata(slot);
            string lostConfirm = CampaignSlotText.OverwriteConfirm(lost);
            SaveResult n1 = CampaignSaveService.Save(slot, CampaignState.CreateNew("fgsave-lost-new", "Standard", 63), SaveReason.NewCampaign);
            AutoSaveTwice(slot);
            string[] keeps = CampaignSaveService.PreservedFiles(slot);
            Expect(lost.State == CampaignSlotState.Corrupt && lost.HasBackup && !CampaignSlotText.StartsNewInSlot(lost) && lostConfirm.Contains("keep-*")
                   && n1.Success && keeps.Any(k => File.ReadAllBytes(k).SequenceEqual(oldBak)) && BakHolds(slot, "fgsave-lost-new"),
                $"主档丢失、备份可读的槽位新建战役并自动存档两次：旧战役的备份另存保留（{string.Join("、", keeps.Select(Path.GetFileName))}），现在的备份属于新战役");

            // (2) 主档丢失、备份读不了（Demo / 截断）：不显示成空槽（原因 + 备份也无法使用），按钮“新建于此槽”；新建 + 两次常规存档后旧备份逐字节保留。
            foreach ((string label, Action<string> makeBak) in new (string, Action<string>)[]
                     {
                         ("Demo 存档", p => File.Copy(fixture, p, true)),
                         ("截断的 v2 存档", p => File.WriteAllText(p, "{\"SchemaVersion\":2,\"Payl")),
                     })
            {
                Clear(slot);
                Directory.CreateDirectory(_dir);
                makeBak(CampaignSaveService.BakPath(slot));
                byte[] bakBytes = File.ReadAllBytes(CampaignSaveService.BakPath(slot));
                CampaignSlotMetadata m = CampaignSaveService.GetSlotMetadata(slot);
                string card = CampaignSlotText.CardText(m);
                LoadResult lr = CampaignSaveService.Load(slot);
                SaveResult created = CampaignSaveService.Save(slot, CampaignState.CreateNew("fgsave-lost-badbak", "Standard", 64), SaveReason.NewCampaign);
                AutoSaveTwice(slot);
                Expect(m.State == CampaignSlotState.Corrupt && m.FailureReason == SaveFailureReason.MainMissing && !m.HasBackup && m.BackupState != CampaignSlotState.Empty
                       && card.Contains("主存档文件丢失") && card.Contains("备份也无法使用") && CampaignSlotText.StartsNewInSlot(m) && CampaignSlotText.ActionLabel(m) == "新建于此槽"
                       && lr.Outcome == LoadOutcome.Corrupt && lr.Reason == SaveFailureReason.MainMissing
                       && created.Success && CampaignSaveService.PreservedFiles(slot).Any(k => File.ReadAllBytes(k).SequenceEqual(bakBytes)),
                    $"主档丢失、备份是{label}：卡片“{Flat(card)}”，不当空槽；在此新建并自动存档两次后，旧备份逐字节保留");
            }

            // (3) 常规存档时 bak 不属于本战役（外来文件）：先另存再替换。
            Clear(slot);
            CampaignSaveService.Save(slot, CampaignState.CreateNew("fgsave-foreign-other", "Standard", 65), SaveReason.Manual);
            byte[] foreign = File.ReadAllBytes(CampaignSaveService.SlotPath(slot));
            File.Move(CampaignSaveService.SlotPath(slot), CampaignSaveService.BakPath(slot));
            CampaignState mine = CampaignState.CreateNew("fgsave-foreign-mine", "Standard", 66);
            CampaignSaveService.Save(slot, mine, SaveReason.Manual);
            mine.Scrap = 1;
            CampaignSaveService.Save(slot, mine, SaveReason.Manual);
            Expect(CampaignSaveService.PreservedFiles(slot).Any(k => File.ReadAllBytes(k).SequenceEqual(foreign)) && BakHolds(slot, "fgsave-foreign-mine"),
                "常规存档遇到不属于本战役的备份：先另存为 keep-bak，再由本战役上一版接替备份");

            // (4) 当前格式但已损坏的主档（被外部截断）：常规存档不让它挤掉完好的备份。
            Clear(slot);
            CampaignState x = CampaignState.CreateNew("fgsave-torn-autosave", "Standard", 67);
            x.Scrap = 41;
            CampaignSaveService.Save(slot, x, SaveReason.Manual);
            x.Scrap = 42;
            CampaignSaveService.Save(slot, x, SaveReason.Manual);
            byte[] goodBak = File.ReadAllBytes(CampaignSaveService.BakPath(slot));
            string main = CampaignSaveService.SlotPath(slot);
            string t42 = File.ReadAllText(main);
            File.WriteAllText(main, t42.Substring(0, t42.Length - 40));
            byte[] torn = File.ReadAllBytes(main);
            x.Scrap = 43;
            SaveResult auto = CampaignSaveService.Save(slot, x, SaveReason.Manual);
            Expect(auto.Success && File.ReadAllBytes(CampaignSaveService.BakPath(slot)).SequenceEqual(goodBak)
                   && auto.PreservedPath != null && auto.PreservedPath.Contains(".keep-corrupt-") && File.ReadAllBytes(auto.PreservedPath).SequenceEqual(torn)
                   && CampaignSaveService.Load(slot).State.Scrap == 43,
                $"主档是当前格式但被截断时自动存档：坏主档另存为 {Path.GetFileName(auto.PreservedPath ?? "无")}，完好备份原样保留，新存档可读");

            // (5) File.Replace 执行到一半被打断的磁盘状态：主档已不在，只剩完整的 .tmp 与 .bak。
            Clear(slot);
            x.Scrap = 51;
            CampaignSaveService.Save(slot, x, SaveReason.Manual);
            x.Scrap = 52;
            CampaignSaveService.Save(slot, x, SaveReason.Manual); // bak = 51
            x.Scrap = 53;
            CampaignSaveService.Save(slot, x, SaveReason.Manual); // main = 53，bak = 52
            File.Move(main, CampaignSaveService.TempPath(slot));
            byte[] tmpBytes = File.ReadAllBytes(CampaignSaveService.TempPath(slot));
            CampaignSlotMetadata cut = CampaignSaveService.GetSlotMetadata(slot);
            LoadResult cutLoad = CampaignSaveService.Load(slot);
            bool restored = CampaignSaveService.RestoreFromBak(slot);
            LoadResult afterRestore = CampaignSaveService.Load(slot);
            Expect(cut.State == CampaignSlotState.Corrupt && cut.FailureReason == SaveFailureReason.MainMissing && cut.HasBackup
                   && cutLoad.Outcome == LoadOutcome.Corrupt && cutLoad.State == null
                   && restored && afterRestore.Success && afterRestore.State.Scrap == 52
                   && File.Exists(CampaignSaveService.TempPath(slot)) && File.ReadAllBytes(CampaignSaveService.TempPath(slot)).SequenceEqual(tmpBytes),
                $"替换被打断（只剩 .tmp + .bak）：显示“主存档丢失 + 可以读取备份”，读档不偷用 .tmp；读取备份后回到上一版（废料 {afterRestore.State?.Scrap}），.tmp 原样不动");

            // (6) 头部与校验和都完好、恢复编排在结构校验时失败（正文里重复 ID）+ 备份完好：列表 / “继续” / 按钮一致走“读取备份”。
            Clear(slot);
            x.Scrap = 31;
            CampaignSaveService.Save(slot, x, SaveReason.Manual);
            x.Scrap = 32;
            CampaignSaveService.Save(slot, x, SaveReason.Manual); // bak = 31
            TestEnvelope env = ReadEnvelope(slot);
            string dupPayload = env.PayloadJson.Replace("\"UnlockedContentIds\":[]", "\"UnlockedContentIds\":[\"dup_selfcheck\",\"dup_selfcheck\"]");
            WriteEnvelope(slot, CampaignSaveService.CurrentSchemaVersion, dupPayload, env.CardJson);
            byte[] dupBytes = File.ReadAllBytes(main);
            CampaignSlotMetadata beforeLoad = CampaignSaveService.GetSlotMetadata(slot);
            RestoreResult failedRestore = CampaignRestoreOrchestrator.Restore(slot);
            CampaignSlotMetadata failed = CampaignSaveService.GetSlotMetadata(slot);
            Expect(dupPayload != env.PayloadJson && beforeLoad.State == CampaignSlotState.Ready && !failedRestore.Success
                   && failed.State == CampaignSlotState.Corrupt && failed.FailureReason == SaveFailureReason.Payload && failed.HasBackup && failed.BackupVerified
                   && CampaignSlotText.ActionLabel(failed) == "读取备份" && !CampaignSlotText.StartsNewInSlot(failed)
                   && CampaignSaveService.ResolveContinueSlot() != slot && CampaignSlotText.CardText(failed).Contains("可以读取上一版备份"),
                $"头部完好、结构校验失败（{failedRestore.FailedStep}）：卡片“{Flat(CampaignSlotText.CardText(failed))}”，不参与“继续”，按钮“{CampaignSlotText.ActionLabel(failed)}”");
            // 与 MainMenuUI.OnSlotActionClicked 同一分支：meta.HasBackup → RestoreFromBak，再走正式恢复入口。
            bool back = failed.HasBackup && CampaignSaveService.RestoreFromBak(slot, out _);
            RestoreResult again = CampaignRestoreOrchestrator.Restore(slot);
            string keptDup = CampaignSaveService.PreservedFiles(slot).FirstOrDefault(k => k.Contains(".keep-corrupt-"));
            Expect(back && again.Success && again.State.Scrap == 31 && keptDup != null && File.ReadAllBytes(keptDup).SequenceEqual(dupBytes)
                   && CampaignSaveService.GetSlotMetadata(slot).State == CampaignSlotState.Ready,
                $"点“读取备份”：坏主档另存为 {Path.GetFileName(keptDup ?? "无")}，经正式恢复入口读到上一版（废料 {again.State?.Scrap}），槽位恢复可读");
            MachineRegistry.ResetForNewCampaign();
            Clear(slot);
        }

        /// <summary>模拟新战役进入游戏后的两次自动存档（第二次会触发 File.Replace 覆盖 bak）。</summary>
        private static void AutoSaveTwice(int slot)
        {
            LoadResult r = CampaignSaveService.Load(slot);
            if (!r.Success)
            {
                Fail($"槽位 {slot} 新建后读不出来：{r.Outcome}/{r.Reason}");
                return;
            }
            r.State.Scrap += 1;
            CampaignSaveService.Save(slot, r.State, SaveReason.Manual);
            r.State.Scrap += 1;
            CampaignSaveService.Save(slot, r.State, SaveReason.Manual);
        }

        private static bool BakHolds(int slot, string campaignId) =>
            File.Exists(CampaignSaveService.BakPath(slot)) && File.ReadAllText(CampaignSaveService.BakPath(slot)).Contains(campaignId);

        // ── 6b. 读档通知超过字幕上限时合并 ─────────────────────────────────

        private static void CheckLoadNoticeOverflow()
        {
            Line("  · 读档通知进入游戏后的字幕（FGR-SYS-004）");
            try
            {
                GameLogic.Campaign.Feedback.FeedbackCues.ResetForTests();
                SaveNoticeRecord[] few = MakeNotices(3);
                int raisedFew = SaveContentReconciler.RaiseLoadNotices(few);
                string[] fewTexts = GameLogic.Campaign.Feedback.FeedbackCues.ActiveCaptions
                    .Where(c => c.Cue == GameLogic.Campaign.Feedback.FeedbackCueId.SaveContentMigrated).Select(c => c.Text).ToArray();
                Expect(raisedFew == 3 && fewTexts.Length == 3 && fewTexts.SequenceEqual(few.Select(SaveContentReconciler.Render)),
                    $"3 条通知：逐条显示为字幕（{string.Join("／", fewTexts)}）");

                GameLogic.Campaign.Feedback.FeedbackCues.ResetForTests();
                int max = GameLogic.Campaign.Feedback.FeedbackCues.MaxVisibleCaptions;
                SaveNoticeRecord[] many = MakeNotices(max + 3);
                int raisedMany = SaveContentReconciler.RaiseLoadNotices(many);
                string[] manyTexts = GameLogic.Campaign.Feedback.FeedbackCues.ActiveCaptions
                    .Where(c => c.Cue == GameLogic.Campaign.Feedback.FeedbackCueId.SaveContentMigrated).Select(c => c.Text).ToArray();
                string summary = manyTexts.LastOrDefault() ?? string.Empty;
                Expect(raisedMany == max && manyTexts.Length == max && manyTexts[0] == SaveContentReconciler.Render(many[0])
                       && summary.Contains("另有 4 条") && summary.Contains("存档历史") && !GameText.ContainsMarker(string.Join("", manyTexts)),
                    $"{many.Length} 条通知（字幕上限 {max}）：前 {max - 1} 条逐条显示，第一条没有被挤掉，最后合并为“{summary}”");
            }
            finally
            {
                GameLogic.Campaign.Feedback.FeedbackCues.ResetForTests();
            }
        }

        private static SaveNoticeRecord[] MakeNotices(int count) =>
            Enumerable.Range(1, count).Select(i => new SaveNoticeRecord
            {
                NoticeId = "selfcheck-notice-" + i,
                TextKey = "save.notice.craft_cancelled",
                Args = new[] { i.ToString(CultureInfo.InvariantCulture) },
            }).ToArray();

        // ── 6. 内容迁移：已移除内容 → 废料 + 通知（FGT-SYS-002）──────────────

        private static void CheckContentReconcile()
        {
            Line("  · 表内容变更：已移除内容转成废料并通知（FGR-SYS-004 / FGT-SYS-002）");
            Expect(SaveContentReconciler.LoadError == null, $"fg.TbRemovedContent 读取成功（错误：{SaveContentReconciler.LoadError ?? "无"}）");
            var overlap = SaveContentReconciler.Rows.Where(row => SaveContentReconciler.IsLivePrimitive(row.Id)).Select(row => row.Id).ToList();
            Expect(overlap.Count == 0, "已移除内容表里没有仍在游戏中的内容 ID" + (overlap.Count == 0 ? string.Empty : "：" + string.Join("、", overlap)));

            int slot = 1;
            CampaignState s = BuildReconcileState(out string reservedPart, out string queueId);
            int scrapBefore = s.Scrap;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);

            SaveContentReconciler.OverrideForTests(RemovedTable((RemovedId, "primitive_chip", "enemy.scout.name", 7, 2)),
                id => id != RemovedId && id != UnlistedId);
            try
            {
                RestoreResult restored = CampaignRestoreOrchestrator.Restore(slot);
                CampaignState st = restored.State;
                string[] leftIds = st?.PrimitiveChips.Select(c => c.CardDefId).ToArray() ?? Array.Empty<string>();
                CraftQueueItemRecord queue = st == null ? null : PrimitiveCraftStation.Find(st, queueId);
                ResourceTransactionRecord tx = st?.ResourceTransactions.FirstOrDefault(t => t.OwnerId == SaveContentReconciler.LedgerOwner && t.TransactionId.Contains(RemovedId));
                Expect(restored.Success && st.Scrap == scrapBefore + 35
                       && leftIds.Count(id => id == RemovedId) == 1 && leftIds.Count(id => id == UnlistedId) == 0 && leftIds.Contains(PrimitiveInventory.DefaultChipContentId)
                       && st.PrimitiveChips.Single(c => c.CardDefId == RemovedId).State == PrimitiveChipState.Draft,
                    $"经正式恢复入口读档：5 件已移除内容（仓中 3、待领取 1、合成预留 1）→ 35 废料（{scrapBefore}→{st?.Scrap}）；装进蓝图的 1 件保留；未登记的 1 件移除；仍在游戏里的保留");
                Expect(tx != null && tx.State == ResourceTransactionState.Committed && Math.Abs(tx.Requested + 35f) < 0.001f,
                    $"废料经资源账本入账（事务 {tx?.TransactionId}，状态 {tx?.State}）");
                Expect(queue != null && queue.State == CraftQueueState.Cancelled && st.PrimitiveChips.All(c => c.ReservedByTransactionId != queueId),
                    "用到已移除内容的合成任务先被取消（材料解锁、事务取消），再转换");

                string[] keys = restored.Notices.Select(n => n.TextKey).OrderBy(k => k).ToArray();
                SaveNoticeRecord removedNotice = restored.Notices.FirstOrDefault(n => n.TextKey == "save.notice.content_removed" && n.Args[0] == "enemy.scout.name");
                SaveNoticeRecord unlistedNotice = restored.Notices.FirstOrDefault(n => n.TextKey == "save.notice.content_removed" && n.Args[0] == "save.notice.unknown_content");
                Expect(keys.SequenceEqual(new[] { "save.notice.content_removed", "save.notice.content_removed", "save.notice.craft_cancelled", "save.notice.installed_kept" })
                       && st.SaveHistory.Notices.Length == 4,
                    $"读档产生 4 条通知并写入存档历史：{string.Join("、", keys)}");
                string zh = SaveContentReconciler.Render(removedNotice);
                string zhUnknown = SaveContentReconciler.Render(unlistedNotice);
                GameSettings.SetLanguage(GameLanguage.En);
                string en = SaveContentReconciler.Render(removedNotice);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                Expect(zh.Contains("静默侦察机") && zh.Contains("×5") && zh.Contains("35 废料") && zhUnknown.Contains("未知内容") && zhUnknown.Contains("0 废料")
                       && en.Contains("Silent Scout") && en.Contains("35 scrap") && !GameText.ContainsMarker(zh + zhUnknown + en),
                    $"通知文字：“{zh}”／“{zhUnknown}”／“{en}”");

                // 幂等：保存后再读，不重复发放、不重复通知。
                CampaignSaveService.Save(slot, st, SaveReason.Manual);
                LoadResult again = CampaignSaveService.Load(slot);
                Expect(again.Success && again.Notices.Length == 0 && again.State.Scrap == st.Scrap && again.State.SaveHistory.Notices.Length == 4,
                    "重复读档：废料不再增加，不再产生新通知，历史仍是 4 条");
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
            }

            // 已移除内容表不可用：什么都不转换（不能把玩家物品换成 0）。
            CampaignState t2 = BuildReconcileState(out _, out _);
            int chips = t2.PrimitiveChips.Length;
            int scrap = t2.Scrap;
            CampaignSaveService.Save(slot, t2, SaveReason.Manual);
            SaveContentReconciler.OverrideForTests(null, id => id != RemovedId && id != UnlistedId);
            try
            {
                LoadResult r = CampaignSaveService.Load(slot);
                Expect(r.Success && r.Notices.Length == 0 && r.State.PrimitiveChips.Length == chips && r.State.Scrap == scrap,
                    "已移除内容表读不出来：物品原样保留、废料不变、没有通知（只记 Error）");
            }
            finally
            {
                SaveContentReconciler.ResetForTests();
            }

            // 正式表 + 正式目录：普通存档读档不产生任何通知、不改任何物品。
            CampaignState plain = CampaignState.CreateNew("fgsave-plain", "Standard", 3);
            PrimitiveInventory.EnsureSeeded(plain);
            int plainChips = plain.PrimitiveChips.Length;
            CampaignSaveService.Save(slot, plain, SaveReason.Manual);
            LoadResult pr = CampaignSaveService.Load(slot);
            Expect(pr.Success && pr.Notices.Length == 0 && pr.State.PrimitiveChips.Length == plainChips && plainChips > 0,
                $"正式内容下读档（{plainChips} 件开局基元）：没有通知，物品不变");

            // 基元芯片以外的内容 ID（建筑类型等）本 Story 只保证不崩、不丢：记录原样保留，转换属于 DEBT-FG0SAVE01-07。
            CampaignState unk = CampaignState.CreateNew("fgsave-unknown-building", "Standard", 4);
            unk.BuildingRecords = (unk.BuildingRecords ?? Array.Empty<BuildingRecord>())
                .Concat(new[] { new BuildingRecord { BuildingId = "bld_selfcheck_unknown", BuildingTypeId = "building_removed_selfcheck", RegionId = unk.CurrentRegionId } })
                .ToArray();
            CampaignSaveService.Save(slot, unk, SaveReason.Manual);
            RestoreResult ur = CampaignRestoreOrchestrator.Restore(slot);
            Expect(ur.Success && ur.State.BuildingRecords.Any(b => b.BuildingId == "bld_selfcheck_unknown" && b.BuildingTypeId == "building_removed_selfcheck"),
                "存档里有表中不存在的建筑类型：经正式恢复入口读档不失败，记录原样保留（不静默丢失）");
            MachineRegistry.ResetForNewCampaign();
            Clear(slot);
        }

        private static CampaignState BuildReconcileState(out string reservedPart, out string queueId)
        {
            CampaignState s = CampaignState.CreateNew("fgsave-reconcile", "Standard", 42);
            s.Scrap = 100;
            var chips = new List<PrimitiveChipRecord>();
            for (int i = 0; i < 3; i++)
            {
                chips.Add(new PrimitiveChipRecord { PartId = $"pchip_rm_bag{i}", CardDefId = RemovedId, State = PrimitiveChipState.Bag });
            }
            chips.Add(new PrimitiveChipRecord { PartId = "pchip_rm_pending", CardDefId = RemovedId, State = PrimitiveChipState.Pending });
            chips.Add(new PrimitiveChipRecord { PartId = "pchip_rm_draft", CardDefId = RemovedId, State = PrimitiveChipState.Draft, DraftBlueprintId = "bp_selfcheck", DraftSlot = 3 });
            chips.Add(new PrimitiveChipRecord { PartId = "pchip_rm_reserved", CardDefId = RemovedId, State = PrimitiveChipState.Bag });
            chips.Add(new PrimitiveChipRecord { PartId = "pchip_live", CardDefId = PrimitiveInventory.DefaultChipContentId, State = PrimitiveChipState.Bag });
            chips.Add(new PrimitiveChipRecord { PartId = "pchip_unlisted", CardDefId = UnlistedId, State = PrimitiveChipState.Bag });
            s.PrimitiveChips = chips.ToArray();
            reservedPart = "pchip_rm_reserved";
            PrimitiveCraftStation.CraftOpResult q = PrimitiveCraftStation.TryEnqueueDisassemble(s, reservedPart);
            queueId = q.QueueItemId;
            if (!q.Success)
            {
                Fail($"构造合成预留失败：{q.FailureReason}");
            }
            return s;
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

        // ── 7. 存档卡新字段（FGR-SYS-007）────────────────────────────────

        private static void CheckCardFields()
        {
            Line("  · 存档卡新字段（FGR-SYS-007）");
            int slot = 2;
            CampaignState s = CampaignState.CreateNew("fgsave-card", "Standard", 424242);
            s.Progress.Act = 2;
            s.Progress.IsPostgame = true;
            s.Progress.IsSandbox = true;
            s.Clock.Day = 12;
            s.PlaySeconds = 3725f;
            s.CurrentRegionId = GameLogic.Campaign.Regions.HomeValleyLayout.RegionId;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            Expect(meta.State == CampaignSlotState.Ready && meta.Act == 2 && meta.Day == 12 && meta.WorldSeed == 424242 && meta.IsPostgame && meta.IsSandbox
                   && meta.DifficultyId == "Standard" && meta.ProductVersion == "0.2" && meta.ThumbnailPath == null,
                "元数据带幕 / 第几日 / 种子 / 后日谈 / 沙盒 / 难度 / 游戏版本（缩略图待 FG15-SYS-01）");
            string zh = CampaignSlotText.CardText(meta);
            GameSettings.SetLanguage(GameLanguage.En);
            string en = CampaignSlotText.CardText(meta);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(zh.Contains("槽位 3") && zh.Contains("第 2 幕") && zh.Contains("第 12 日") && zh.Contains("种子 424242") && zh.Contains("后日谈")
                   && zh.Contains("沙盒") && zh.Contains("标准难度") && zh.Contains("1 小时 02 分") && zh.Contains("归还谷地") && zh.Contains("保存于")
                   && !zh.Contains(meta.CampaignId),
                $"中文卡片：“{Flat(zh)}”");
            Expect(en.Contains("Slot 3") && en.Contains("Act 2") && en.Contains("Day 12") && en.Contains("Seed 424242") && en.Contains("Epilogue")
                   && en.Contains("Sandbox") && en.Contains("Standard"),
                $"英文卡片字段：“{Flat(en)}”");

            s.Clock.Day = 0;
            s.Progress.IsPostgame = false;
            s.Progress.IsSandbox = false;
            CampaignSaveService.Save(slot, s, SaveReason.Manual);
            string noDay = CampaignSlotText.CardText(CampaignSaveService.GetSlotMetadata(slot));
            Expect(!noDay.Contains("日｜") && !noDay.Contains("后日谈") && !noDay.Contains("沙盒") && noDay.Contains("第 2 幕"),
                $"时钟未接入（Day=0）时不显示第几日，未进入后日谈 / 沙盒时不显示标记：“{Flat(noDay)}”");

            // 各状态、两种语言都没有缺键标记。
            var metas = new List<CampaignSlotMetadata>
            {
                new CampaignSlotMetadata { SlotIndex = 0, State = CampaignSlotState.Empty },
                CampaignSaveService.GetSlotMetadata(slot),
                new CampaignSlotMetadata { SlotIndex = 1, State = CampaignSlotState.DemoSave, WrittenAtUtc = "2026-09-22T07:04:35Z" },
                new CampaignSlotMetadata { SlotIndex = 1, State = CampaignSlotState.Incompatible, FailureReason = SaveFailureReason.Newer, SchemaVersion = 9 },
                new CampaignSlotMetadata { SlotIndex = 1, State = CampaignSlotState.Incompatible, FailureReason = SaveFailureReason.MigrationMissing, SchemaVersion = 2 },
                new CampaignSlotMetadata { SlotIndex = 1, State = CampaignSlotState.Corrupt, FailureReason = SaveFailureReason.Checksum, HasBackup = true, BackupState = CampaignSlotState.Ready, BackupWrittenAtUtc = "2026-09-25T01:00:00Z" },
                new CampaignSlotMetadata { SlotIndex = 1, State = CampaignSlotState.Corrupt, FailureReason = SaveFailureReason.Truncated, BackupState = CampaignSlotState.Empty },
            };
            var bad = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (CampaignSlotMetadata m in metas)
                {
                    string t = CampaignSlotText.CardText(m) + CampaignSlotText.ActionLabel(m) + CampaignSlotText.OverwriteConfirm(m);
                    if (GameText.ContainsMarker(t))
                    {
                        bad.Add($"{lang}/{m.State}/{m.FailureReason}");
                    }
                }
                if (GameText.ContainsMarker(CampaignSlotText.ContinueUnavailable(true) + CampaignSlotText.ContinueUnavailable(false)))
                {
                    bad.Add($"{lang}/继续");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(bad.Count == 0, "7 种卡片状态 × 中英文：卡片、按钮、覆盖确认、“继续”原因都没有缺键标记" + (bad.Count == 0 ? string.Empty : "：" + string.Join("；", bad)));
            Clear(slot);
        }

        // ── 8. 暂停与倍速（B09）──────────────────────────────────────────

        /// <summary>用游戏真实的速度与暂停机制（<see cref="GameLogic.Core.StrategyClock"/> 倍率、
        /// <see cref="GameLogic.Core.InputRouter.SetGameplayPaused"/> 战略暂停）逐档存读档。不改
        /// <c>Time.timeScale</c>：游戏不用它，而且在 batchmode 里改它会把 ProjectSettings/TimeManager.asset 写脏。</summary>
        private static void CheckTimeScaleIndependence()
        {
            Line("  · 暂停与倍速下存读档（B09）");
            int slot = 0;
            string reference = null;
            var diffs = new List<string>();
            var tried = new List<string>();
            var modes = new List<(string name, bool paused, float speed)> { ("战略暂停", true, 1f) };
            modes.AddRange(GameLogic.Core.StrategyClock.AllowedMultipliers.Select(m => ($"{m}x", false, m)));
            foreach ((string name, bool paused, float speed) in modes)
            {
                GameLogic.Core.StrategyClock.SetSpeed(speed);
                GameLogic.Core.InputRouter.SetGameplayPaused(paused, strategic: true);
                CampaignState s = CampaignState.CreateNew("fgsave-time", "Standard", 606);
                s.PlaySeconds = 500f;
                s.Scrap = 42;
                SaveResult r = CampaignSaveService.Save(slot, s, SaveReason.Manual);
                LoadResult l = CampaignSaveService.Load(slot);
                string json = l.Success ? StateJson(l.State) : "失败";
                reference ??= json;
                tried.Add(name);
                if (!r.Success || json != reference)
                {
                    diffs.Add(name);
                }
            }
            GameLogic.Core.StrategyClock.Reset();
            GameLogic.Core.InputRouter.SetGameplayPaused(false);
            Expect(diffs.Count == 0 && tried.Count >= 4,
                $"{string.Join(" / ", tried)} 下存档再读档结果完全一致（存档不依赖游戏速度；3x 档由 FG0-ARCH-01 加入 StrategyClock 后自动纳入本测试）" +
                (diffs.Count == 0 ? string.Empty : "：不一致 " + string.Join("、", diffs)));
            Clear(slot);
        }

        // ── 9. 性能（FGR-SYS-005 初值）──────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · 性能（Editor batchmode / Mono JIT，Windows 11；真机 IL2CPP + HybridCLR 解释执行另测，见 FG15-SYS-02）");
            int slot = 0;
            CampaignState small = CampaignState.CreateNew("fgsave-perf-small", "Standard", 1);
            PrimitiveInventory.EnsureSeeded(small);
            (double ss, double sl, long sb) = Measure(slot, small, 5);
            Line($"    新战役：存档 {ss:F1} ms，读档 {sl:F1} ms，文件 {sb / 1024.0:F1} KB");

            CampaignState big = CampaignState.CreateNew("fgsave-perf-big", "Standard", 2);
            big.ResourceTransactions = Enumerable.Range(0, 40000).Select(i => new ResourceTransactionRecord
            {
                TransactionId = $"tx_{i:D6}", OwnerId = "perf", ResourceType = "Scrap", Requested = 5, Consumed = 5, State = ResourceTransactionState.Committed,
            }).ToArray();
            big.GroundItems = Enumerable.Range(0, 10000).Select(i => new GroundItemRecord
            {
                GroundItemId = $"g_{i:D6}", RegionId = "home_valley", Position = new Vector2(i % 100, i / 100), ResourceType = "Scrap", Amount = 3,
            }).ToArray();
            big.EventLedger = Enumerable.Range(0, 20000).Select(i => new EventLedgerEntry { EventId = $"e_{i:D6}", Category = "perf", Payload = "p" }).ToArray();
            string diff = new string('d', 512);
            big.World.ChunkDiffs = Enumerable.Range(0, 4000).Select(i => new ChunkDiffRecord { SurfaceId = "surface_main", ChunkX = i % 64, ChunkY = i / 64, DiffPayload = diff }).ToArray();
            (double bs, double bl, long bb) = Measure(slot, big, 2);
            var sw = Stopwatch.StartNew();
            CampaignSlotMetadata[] all = CampaignSaveService.GetAllSlotMetadata();
            sw.Stop();
            Line($"    压力档（4 万资源事务 + 1 万地面物 + 2 万事件 + 4000 个 512 字节区块差异）：存档 {bs:F0} ms，读档 {bl:F0} ms，" +
                 $"文件 {bb / 1024.0 / 1024.0:F1} MB，三个槽位列表 {sw.Elapsed.TotalMilliseconds:F0} ms");
            Expect(bs <= 2000 && bl <= 15000 && all[slot].State == CampaignSlotState.Ready,
                $"压力档存档 ≤ 2 秒（{bs:F0} ms）、读档 ≤ 15 秒（{bl:F0} ms）——FGR-SYS-005 初值；主线程阻塞 ≤100 ms 的自动存档属于 FG15-SYS-01（FGT-SYS-003）");
            Clear(slot);
        }

        private static (double save, double load, long bytes) Measure(int slot, CampaignState state, int rounds)
        {
            double save = double.MaxValue;
            double load = double.MaxValue;
            for (int i = 0; i < rounds; i++)
            {
                var sw = Stopwatch.StartNew();
                CampaignSaveService.Save(slot, state, SaveReason.Manual);
                sw.Stop();
                save = Math.Min(save, sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                LoadResult r = CampaignSaveService.Load(slot);
                sw.Stop();
                load = Math.Min(load, sw.Elapsed.TotalMilliseconds);
                if (!r.Success)
                {
                    Fail($"性能测量读档失败：{r.Message}");
                }
            }
            return (save, load, new FileInfo(CampaignSaveService.SlotPath(slot)).Length);
        }

        // ── 工具 ─────────────────────────────────────────────────────────

        /// <summary>与存档头部同名字段的测试镜像（头部类型是 GameLogic 的 internal 类型）。</summary>
        [Serializable]
        private sealed class TestEnvelope
        {
            public int SchemaVersion;
            public int ContentVersion;
            public string Checksum;
            public string WrittenAtUtc;
            public string ProductVersion;
            public string CardJson;
            public string PayloadJson;
        }

        private static TestEnvelope ReadEnvelope(int slot) =>
            JsonUtility.FromJson<TestEnvelope>(File.ReadAllText(CampaignSaveService.SlotPath(slot)));

        /// <summary>按真实格式写一个存档（校验和按正文与卡片重算；传入 <paramref name="checksum"/> 时用它，模拟篡改）。</summary>
        private static void WriteEnvelope(int slot, int schema, string payload, string card, string checksum = null)
        {
            Directory.CreateDirectory(_dir);
            var env = new TestEnvelope
            {
                SchemaVersion = schema,
                ContentVersion = CampaignSaveService.CurrentContentVersion,
                Checksum = checksum ?? CampaignSaveService.ComputeChecksum(payload, card),
                WrittenAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ProductVersion = "0.2",
                CardJson = card,
                PayloadJson = payload,
            };
            File.WriteAllText(CampaignSaveService.SlotPath(slot), JsonUtility.ToJson(env));
        }

        private static string StateJson(CampaignState s)
        {
            if (s == null)
            {
                return "null";
            }
            s.NormalizeForSave();
            SaveReason keep = s.LastSaveReason;
            s.LastSaveReason = SaveReason.Manual;
            string json = JsonUtility.ToJson(s);
            s.LastSaveReason = keep;
            return json;
        }

        private static void Clear(int slot)
        {
            foreach (string p in new[] { CampaignSaveService.SlotPath(slot), CampaignSaveService.BakPath(slot), CampaignSaveService.TempPath(slot) }
                         .Concat(CampaignSaveService.PreservedFiles(slot)))
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }

        /// <summary>玩家可读的中文：非空、无缺键标记、不含英文单词（产品名 Demo 除外）。</summary>
        private static bool Readable(string text) =>
            !string.IsNullOrWhiteSpace(text) && !GameText.ContainsMarker(text)
            && !System.Text.RegularExpressions.Regex.IsMatch(text.Replace("Demo", string.Empty), "[A-Za-z]{4,}");

        private static string Flat(string s) => (s ?? string.Empty).Replace("\n", " / ");

        private static bool Throws(Action action)
        {
            try
            {
                action();
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
