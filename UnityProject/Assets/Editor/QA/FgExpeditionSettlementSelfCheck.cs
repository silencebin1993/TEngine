using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameConfig;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldGen;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using GameLogic.View;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using F = GameLogic.EditorTools.FgProductionSelfCheck;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG8-EXP-05 返回与结算（FG08 FGR-EXP-031 / 032 / 050；验收 FGT-EXP-008“阵亡与全灭：货物、黑匣子、数据包掉在原地可以捡回；普通战利品 1 日后消失；关键材料永不消失”）的自检——起真实系统断言行为：
    /// A 数据（调参初值、文本中英、通知类型、D-13 判定入口）；
    /// B 阵亡掉落与 D-13 一致性（星球上：货舱的普通战利品带 1 日回收章、关键材料不盖章、携带的关键物 / 黑匣子掉在原地不判遗失、数据包交给队友 → 全灭后留在原地；
    ///   全灭结算列出留在原地的东西；普通战利品到期被回收（提示 → 消失 → 搬运单取消），关键物 / 关键材料 / 黑匣子 / 数据包过期后照样在；再派一台去捡回、带回家园）；
    /// C 到期的暂停 / 0.5x～3x / 存读档 / 家园范围豁免；
    /// D 从远征地点撤离 → 从地点入口沿地形走回家园、到家才结算（DEBT-FG8EXP02-01）：带回的物品与去处、三选一记录、任务、熔合线索、暴露变化、耗时、经历、远征卸货；离家报告“远征结算”一段；
    /// E 观察与否一致、种子集、走回途中真文件存读档；F 返程途中全灭（DEBT-FG8EXP02-04）：东西留在原地、全灭说明、再派队捡回；
    /// G 远征地点里全灭：关键物掉在地点里不判遗失（FG-GAP-133）、撤离面板清单、放弃后再去取回；H 离开前清单（行军条“返回家园”二次确认，负向：附近没东西时一次就走）；
    /// I 室内场地里掉落的遗产 / 黑匣子走近捡起、带回家园入库（DEBT-FG8EXP04-07）；J 远征卸货送到解析台（DEBT-FG4ECO06-04，含解析台没建的负向）；
    /// K 从远征地点入口呼叫投送舱接回（DEBT-FG8DROP01-01）；K2 分批接回（先落地那台的关键物 / 货记进结算且只记一次）；M 性能。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>（段名 FgExpeditionSettlementSelfCheck）；单段：bash tools/unity-validate.sh --segment FgExpeditionSettlementSelfCheck。
    /// </summary>
    public static class FgExpeditionSettlementSelfCheck
    {
        private const int Slot = 11;
        private const int RunSlot = 12;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private static StringBuilder _report;
        private static int _pass;
        private static int _fail;
        private static string _dir;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/QA/自检/FG 返回与结算")]
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
            Line("\n[返回与结算] 阵亡掉落与 D-13、普通战利品 1 日回收、撤离后走回家园到家结算、全灭说明、离开前清单、室内再取回、卸货送解析台、地点入口投送舱接回（FG8-EXP-05）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会改写存档目录与世界）");
                return 0;
            }
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<float> originalDelta = CameraDirector.RealDeltaTime;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgsettle-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                WorldGenContent.Reload();
                FgContentTables.Reload();
                ItemCatalog.Reload();
                AnalysisCatalog.Reload();
                LootCatalog.Reload();
                LootPickCatalog.Reload();
                BuildingOps.Reload();
                MachineRoster.ResetForTests();
                GameClock.ReloadTuning();
                HomeExplorationService.ResetForTests();
                FieldLootService.ResetSessionState();
                LootPickService.ResetSessionState();
                CargoService.ResetSessionState();
                GroundLootExpiry.ResetSessionState();
                ExpeditionSettlementService.ResetSessionState();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                CameraDirector.RealDeltaTime = () => 0.05f;
                GameRoot.BindWorldProviders();

                Step(CheckData);
                Step(CheckDeathDropAndWipe);
                Step(CheckExpiryTiming);
                string control = null;
                Step(() => control = CheckWalkHome(8501, observe: true, full: true));
                Step(() => CheckObservationSeedsAndSave(control));
                Step(CheckWipeOnWalkHome);
                Step(CheckSiteWipe);
                Step(CheckLeaveWarning);
                Step(CheckInterior);
                Step(CheckUnloadToAnalysis);
                Step(CheckPodFromSiteEntrance);
                Step(CheckPodPartialRecall);
                Step(CheckPodRecallInFlightWipe);
                Step(CheckPodRecallInFlightWalkHome);
                Step(CheckPerf);
                foreach (string p in PerfLines)
                {
                    Line("  · " + p);
                }
            }
            finally
            {
                LootPickCatalog.ResetForTests();
                LootCatalog.ResetForTests();
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                WorldGenContent.ResetForTests();
                CameraDirector.RealDeltaTime = originalDelta;
                InputRouter.DebugSetReader(null);
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                try
                {
                    WorldSimulation.UnloadAll();
                }
                catch (Exception e)
                {
                    Fail("收尾卸载世界抛异常：" + e.Message);
                }
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null && originalSlot >= 0)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line(_fail == 0 ? $"  返回与结算：全部 {_pass} 项通过" : $"  返回与结算：{_fail} 项失败 / {_pass} 项通过");
            return _fail;
        }

        // ── A 数据 ──────────────────────────────────────────────────────────────

        private static readonly string[] TextKeys =
        {
            "settle.key_dropped", "settle.key_recovered", "settle.key_recovered_ground", "blackbox.wipe.keys_boxes_left", "blackbox.wipe.keys_left", "settle.left.never",
            "settle.left.countdown", "settle.expire.gone", "settle.expire.warn", "settle.left.row", "settle.name.packet", "settle.name.key", "settle.name.amount", "settle.left.sep",
            "settle.leave.warn", "settle.leave.warn_more", "settle.left.more", "settle.site.left_none", "settle.site.left_title", "settle.site.left_line", "settle.site.wipe_summary",
            "settle.site.evac_summary", "settle.cue.walking_home", "settle.wipe.cue_tail", "settle.section", "settle.via.foot", "settle.via.pod", "settle.via.wiped", "settle.head",
            "settle.item.row", "settle.dest.warehouse", "settle.dest.firmware", "settle.dest.analysis", "settle.dest.analysis_wait", "settle.dest.cargo", "settle.dest.pod",
            "settle.dest.vault", "settle.dest.gallery", "settle.dest.recovered", "settle.items.none", "settle.cargo_kept", "settle.packets", "settle.picks.none", "settle.pick.row",
            "settle.pick.src.elite", "settle.pick.src.event", "settle.pick.src.boss", "settle.pick.unknown", "settle.pick.to_cargo", "settle.pick.to_depot", "settle.quests.none",
            "settle.quest.done", "settle.quest.failed", "settle.clues.none", "settle.clue.row", "settle.exposure", "settle.machine.back", "settle.machine.back_injured",
            "settle.machine.lost", "settle.left.title", "settle.left.none", "settle.wipe.title", "settle.wipe.explain_items", "settle.wipe.explain_expire", "settle.wipe.explain_signal",
            "rules.edit.analysis_on", "rules.edit.analysis_off", "rules.log.unload_analysis", "rules.log.unload_analysis_fail",
        };

        private static void CheckData()
        {
            bool tuning = Math.Abs(GroundLootExpiry.LifeDays - 1f) < 1e-4f && GroundLootExpiry.WarnHours > 0f && ExpeditionSettlementService.LeaveRadius > 0f
                          && ExpeditionSettlementService.LeaveListMax > 0 && ExpeditionSettlementService.LeaveWarnLines > 0
                          && GroundLootExpiry.LifeTicks == GameClock.TicksFor(GameClock.DaySeconds);
            var missing = new List<string>();
            var noEn = new List<string>();
            foreach (string k in TextKeys)
            {
                string zh = GameText.Get(k);
                if (GameText.ContainsMarker(zh) || zh == k)
                {
                    missing.Add(k);
                }
            }
            GameSettings.SetLanguage(GameLanguage.En);
            foreach (string k in TextKeys)
            {
                string en = GameText.Get(k);
                if (GameText.ContainsMarker(en) || en == k || en.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF))
                {
                    noEn.Add(k);
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool notify = NotificationCatalog.TryGetType(GroundLootExpiry.NotifyType, out NotifyTypeDef def) && def != null && def.AwaySection == "event";
            var key = new RegionQuestItemRecord { ContentId = FracturedCityLayout.ProtocolDataboxContentId, SalvageInstanceId = "a:k" };
            var box = new RegionQuestItemRecord { ContentId = BlackBoxService.ContentId, SalvageInstanceId = BlackBoxService.InstanceIdOf(7) };
            ItemDef keyMat = ItemCatalog.Items.FirstOrDefault(i => i.Tier == KeyItemRetention.KeyTier);
            bool retention = KeyItemRetention.OfQuestItem(key) == KeyItemRetention.Kind.KeyMaterial && KeyItemRetention.OfQuestItem(box) == KeyItemRetention.Kind.BlackBox
                             && KeyItemRetention.StaysOnWipe(KeyItemRetention.OfQuestItem(key)) && keyMat != null && !GroundLootExpiry.Expires(keyMat.Id)
                             && GroundLootExpiry.Expires("alloy");
            Expect(tuning && missing.Count == 0 && noEn.Count == 0 && notify && retention,
                $"A 数据：普通战利品 {GroundLootExpiry.LifeDays} 个游戏日后回收（= {GroundLootExpiry.LifeTicks} 步）、提前 {GroundLootExpiry.WarnHours} 小时提示、清单半径 {ExpeditionSettlementService.LeaveRadius} 格 / {ExpeditionSettlementService.LeaveListMax} 条；" +
                $"文本键 {TextKeys.Length} 个中英齐全（缺中文 {string.Join("、", missing)}，缺英文 {string.Join("、", noEn)}）；通知类型 loot_expiry 记进离家报告“事件”段（{notify}）；" +
                $"D-13 判定入口：区域关键物 = 关键材料、黑匣子、全灭留在原地，关键材料（{keyMat?.Id}）永不过期、合金会过期（{retention}）");
        }

        // ── B 阵亡掉落、D-13、全灭结算、到期回收、再取回 ──────────────────────────────

        /// <summary>B 段货舱里关键材料那一行的 ID：用正式格式（CargoService.HeldPrefix 开头），阵亡掉落的地面物按它识别成“货舱掉落”。</summary>
        private const string KeyHeldB = CargoService.HeldPrefix + "settle-b-key";

        private static void CheckDeathDropAndWipe()
        {
            CampaignState s = NewWorld(8505);
            EnsureWarehouse(s);
            CoverEverywhere();
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int b = Spawn(s, CoreNear(s) + new Vector2(2f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, a, b);
            Vector2 field = FieldPoint(s, 90f, 3);
            MoveTo(a, field);
            MoveTo(b, field + new Vector2(40f, 0f));
            WorldSimulation.StepMany(3);
            LootPacketState st = LootPickService.StateOf(s);
            ItemDef keyMat = ItemCatalog.Items.First(i => i.Tier == KeyItemRetention.KeyTier);
            AddHeld(st, a, "settle:b-alloy", LootPickCatalog.KindItem, "alloy", null, 15);
            AddHeld(st, a, KeyHeldB, LootPickCatalog.KindItem, keyMat.Id, null, 1);
            RegionQuestItemRecord key = AddQuest(s, "settle:b-quest", FracturedCityLayout.ProtocolDataboxContentId, HomeValleyLayout.RegionId, RegionQuestItemState.Carried, a, default);
            LootPackageRecord packet = LootPickService.Grant(s, LootPickService.SourceElite, "silent", a, HomeValleyLayout.RegionId, Pos(a));
            WorldSimulation.StepMany(2);

            // B1 阵亡：货舱的东西掉在原地；普通战利品盖 1 日回收章、关键材料不盖；关键物掉在原地（不判遗失）；数据包交给同队 b。
            Vector2 death = Pos(a);
            long dropTick = GameClock.Ticks;
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(3);
            GroundItemRecord alloy = HomeValleyCargo.FindGroundItemBySalvageId(s, "settle:b-alloy");
            GroundItemRecord keyGround = HomeValleyCargo.FindGroundItemBySalvageId(s, KeyHeldB);
            RegionQuestItemRecord boxA = BlackBoxService.FindItem(s, a);
            bool stamped = alloy != null && alloy.ExpireTick >= dropTick + GroundLootExpiry.LifeTicks && alloy.ExpireTick <= dropTick + GroundLootExpiry.LifeTicks + 4
                           && keyGround != null && keyGround.ExpireTick == 0;
            bool keyDown = key.State == RegionQuestItemState.OnGround && key.CarrierLogicId == 0 && key.RegionId == HomeValleyLayout.RegionId && Vector2.Distance(key.Position, death) < 0.6f;
            bool boxDown = boxA != null && boxA.State == RegionQuestItemState.OnGround && Vector2.Distance(boxA.Position, death) < 0.6f;
            bool packetMoved = packet.Holder == b;
            bool keyNote = NotifyDetails(CargoService.NotifyType).Any(d => d.Contains("关键物掉在"));
            Expect(stamped && keyDown && boxDown && packetMoved && keyNote,
                $"B1 FGR-EXP-032 / D-13 阵亡掉落：{Name(a)} 货舱里的合金掉在原地并带 1 个游戏日回收章（到期步 {alloy?.ExpireTick}，阵亡步 {dropTick}），关键材料 {keyMat.Id} 不盖章（{keyGround?.ExpireTick}）；" +
                $"携带的关键物掉在阵亡处、不判遗失（{keyDown}），黑匣子掉在原地（{boxDown}），数据包交给同队 {Name(b)}（{packetMoved}）；通知写明关键物掉在哪（{keyNote}）");

            // B2 全灭（行军途中）：数据包留在原地；全灭结算列出留在原地的东西（倒计时 / 永不消失）、全灭说明。
            Vector2 deathB = Pos(b);
            int wiped0 = ExpeditionMarchService.WipedCount;
            MachineRegistry.ApplyDamage(b, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz);
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            ExpeditionSettlementRecord set = r?.Settlement;
            bool packetDown = packet.Holder == 0 && Vector2.Distance(new Vector2(packet.X, packet.Y), deathB) < 0.6f;
            bool settled = ExpeditionMarchService.WipedCount == wiped0 + 1 && r != null && r.HasSettlement && set.Via == ExpeditionSettlementService.ViaWiped
                           && r.Outcome == AwayReportService.OutcomeWiped && !AwayReportService.IsOpen(s);
            bool leftKinds = set != null && set.Left.Any(e => e.Kind == ExpeditionSettlementService.KindLoot && e.ExpireTick == alloy?.ExpireTick)
                             && set.Left.Any(e => e.Kind == ExpeditionSettlementService.KindKey && e.ExpireTick == 0)
                             // 修复第 2 轮（审查 P1）：家园星球上货舱掉落的关键材料（不盖回收章）本身必须出现在“留在原地”里，不能靠区域关键物顶替。
                             && set.Left.Any(e => e.ItemId == keyMat.Id && e.Kind == ExpeditionSettlementService.KindKey && e.ExpireTick == 0)
                             && set.Left.Any(e => e.Kind == ExpeditionSettlementService.KindBox) && set.Left.Any(e => e.Kind == ExpeditionSettlementService.KindPacket);
            var lines = new List<AwayLine>();
            if (r != null)
            {
                AwayReportView.Build(s, r, lines);
            }
            string countdown = GameText.Format("settle.left.countdown", string.Empty).Trim();
            bool explain = lines.Any(l => l.Text == GameText.Get("settle.wipe.title")) && lines.Any(l => l.Text == GameText.Get("settle.wipe.explain_items"))
                           && lines.Any(l => l.Section == AwayReportView.SectionSettle && l.Action == AwayLineAction.Locate && l.Text.Contains(GameText.Get("settle.left.never")))
                           && lines.Any(l => l.Section == AwayReportView.SectionSettle && l.Action == AwayLineAction.Locate && l.Text.Contains("被敌方回收"))
                           && !lines.Any(l => GameText.ContainsMarker(l.Text))
                           && lines.Where(l => l.Section == AwayReportView.SectionSettle).All(l => l.IsEntry || l.Cls == "ar-row-title" || l.Cls == "ar-row-dim"); // 每一条都能点（FG04 第 4 节）
            Expect(packetDown && settled && leftKinds && explain,
                $"B2 行军途中全灭（DEBT-FG8EXP02-04）：数据包留在 {Name(b)} 倒下的地方（{packetDown}）；按全灭结算远征与离家报告（{settled}）；结算列出留在原地的 {set?.Left.Length} 处（普通战利品带倒计时、货舱掉落的关键材料 {keyMat.Id} / 关键物 / 黑匣子 / 数据包永不消失 {leftKinds}：{string.Join(",", set?.Left.Select(e => e.Kind + ":" + e.ItemId) ?? Array.Empty<string>())}）；" +
                $"离家报告“远征结算”写全灭的处理并可点击定位（{explain}；如“{lines.FirstOrDefault(l => l.Action == AwayLineAction.Locate)?.Text}”）");

            // B3 到期（测试捷径：把合金的到期步拉到 10 游戏秒后，验证正式的每步判定）：先提示、再回收（地上移走、搬运单取消）；关键材料 / 关键物 / 黑匣子 / 数据包照样在。
            alloy.ExpireTick = GameClock.Ticks + GameClock.StepHz * 10;
            alloy.ExpireWarned = false;
            GroundLootExpiry.Touch();
            int warn0 = GroundLootExpiry.Warned;
            int gone0 = GroundLootExpiry.Expired;
            WorldSimulation.StepMany(2);
            bool warned = GroundLootExpiry.Warned == warn0 + 1 && alloy.ExpireWarned && HomeValleyCargo.FindGroundItemBySalvageId(s, "settle:b-alloy") != null
                          && NotifyDetails(GroundLootExpiry.NotifyType).Any(d => d.Contains("就会被敌方回收"));
            bool hauled = s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && HomeValleyWorkOrders.IsActive(o) && o.SourceId == alloy.GroundItemId);
            WorldSimulation.StepMany(GameClock.StepHz * 11);
            bool gone = HomeValleyCargo.FindGroundItemBySalvageId(s, "settle:b-alloy") == null && GroundLootExpiry.Expired == gone0 + 1
                        && !s.WorkOrders.Any(o => o != null && o.Kind == WorkOrderKind.Haul && HomeValleyWorkOrders.IsActive(o) && o.SourceId == alloy.GroundItemId)
                        && NotifyDetails(GroundLootExpiry.NotifyType).Any(d => d.Contains("被敌方回收了"));
            bool kept = HomeValleyCargo.FindGroundItemBySalvageId(s, KeyHeldB) != null && key.State == RegionQuestItemState.OnGround && boxA.State == RegionQuestItemState.OnGround
                        && packet.Holder == 0 && LootPickService.StateOf(s).Packets.Contains(packet);
            Expect(warned && gone && kept,
                $"B3 普通战利品到期：先提示一次“快要被回收”（{warned}，搬运单在 {hauled}），到期从地上移走、搬运单取消、通知（{gone}）；关键材料、关键物、黑匣子、数据包照样在原地（{kept}）");

            // B4 再派一台去捡回、带回家园（FGT-EXP-008“掉在原地可以捡回”）。
            int c = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, c);
            MoveTo(c, death + new Vector2(1f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool picked = key.State == RegionQuestItemState.Carried && key.CarrierLogicId == c && boxA.State == RegionQuestItemState.Carried && boxA.CarrierLogicId == c
                          && HomeValleyCargo.FindGroundItemBySalvageId(s, KeyHeldB) == null && st.Held.Any(h => h.Machine == c && h.ItemId == keyMat.Id);
            MoveTo(c, deathB + new Vector2(1f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool packetBack = packet.Holder == c;
            ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Hold, out _); // 原地待命（不让去程的“前进”把它带离家园范围）
            MoveTo(c, CoreNear(s));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool home = key.State == RegionQuestItemState.Recovered && boxA.State == RegionQuestItemState.Recovered && BlackBoxService.Find(s, a) != null
                        && NotifyDetails(CargoService.NotifyType).Any(d => d.Contains("带回了"));
            Expect(picked && packetBack && home,
                $"B4 掉在原地的东西可以捡回：再派 {Name(c)} 走过去自动捡起关键物、黑匣子、关键材料（{picked}）与数据包（{packetBack}），走回家园范围即带回（关键物 Recovered、黑匣子进陈列馆 {home}）");
        }

        // ── C 到期的暂停 / 倍速 / 存读档 / 家园豁免 ───────────────────────────────────

        private static void CheckExpiryTiming()
        {
            CampaignState s = NewWorld(8506);
            Vector2 field = FieldPoint(s, 80f, 5);
            long reference = -1;
            bool same = true;
            var detail = new List<string>();
            foreach (float speed in new[] { 1f, 0.5f, 2f, 3f })
            {
                GroundItemRecord g = Drop(s, field + new Vector2(speed * 3f, 0f), "settle:t" + speed, 4);
                long due = GameClock.Ticks + GameClock.StepHz * 5;
                g.ExpireTick = due;
                GroundLootExpiry.Touch();
                GameClock.SetSpeed(speed);
                long removedAt = -1;
                for (int i = 0; i < 2000 && removedAt < 0; i++)
                {
                    WorldSimulation.Frame(1f / 60f);
                    if (HomeValleyCargo.FindGroundItemBySalvageId(s, g.SalvageInstanceId) == null)
                    {
                        removedAt = GameClock.Ticks;
                    }
                }
                GameClock.SetSpeed(1f);
                long late = removedAt - due;
                detail.Add($"{speed}x 晚 {late} 步");
                int stepsPerFrame = Mathf.CeilToInt(GameClock.StepHz * speed / 60f) + 1;
                same &= removedAt >= due && late <= stepsPerFrame;
                reference = reference < 0 ? late : reference;
            }
            // 暂停：到期步过了也不推进（时钟不走），恢复后照常回收。
            GroundItemRecord p = Drop(s, field + new Vector2(0f, 6f), "settle:tp", 3);
            p.ExpireTick = GameClock.Ticks + 2;
            GroundLootExpiry.Touch();
            GameClock.SetPaused(true);
            long t0 = GameClock.Ticks;
            for (int i = 0; i < 120; i++)
            {
                WorldSimulation.Frame(1f / 60f);
            }
            bool heldPaused = GameClock.Ticks == t0 && HomeValleyCargo.FindGroundItemBySalvageId(s, "settle:tp") != null;
            GameClock.SetPaused(false);
            WorldSimulation.StepMany(4);
            bool afterPause = HomeValleyCargo.FindGroundItemBySalvageId(s, "settle:tp") == null;
            Expect(same && heldPaused && afterPause,
                $"C1 到期只看统一时钟步：1x / 0.5x / 2x / 3x 都在到期那一步回收（{string.Join("，", detail)}）；暂停时不走（{heldPaused}），恢复后回收（{afterPause}）");

            // C2 家园 / 带仓库的前哨站范围内：自己的地盘，不回收（章清掉，交给搬运单）。
            EnsureWarehouse(s);
            GroundItemRecord near = Drop(s, CoreNear(s), "settle:tdepot", 5);
            near.ExpireTick = GameClock.Ticks + 30;
            GroundLootExpiry.Touch();
            WorldSimulation.StepMany(60);
            GroundItemRecord nearNow = HomeValleyCargo.FindGroundItemBySalvageId(s, "settle:tdepot");
            bool depot = (nearNow == null || nearNow.ExpireTick == 0) && GroundLootExpiry.KeptInDepot > 0;
            Expect(depot, $"C2 负向：家园范围内的战利品不被回收（章清掉 {nearNow?.ExpireTick}，交给搬运单；不在地上 = 已被搬走），豁免 {GroundLootExpiry.KeptInDepot} 次");

            // C3 真文件存读档：到期步 / 已提示进存档，读档后在同一步回收；对照组不读档。
            GroundItemRecord sv = Drop(s, field + new Vector2(-6f, 0f), "settle:tsave", 2);
            sv.ExpireTick = GameClock.Ticks + GameClock.StepHz * 4;
            sv.ExpireWarned = true;
            GroundLootExpiry.Touch();
            long due2 = sv.ExpireTick;
            SaveResult saved = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
            CampaignState back = Reload(Slot);
            GroundItemRecord rs = back != null ? HomeValleyCargo.FindGroundItemBySalvageId(back, "settle:tsave") : null;
            bool restored = saved.Success && rs != null && rs.ExpireTick == due2 && rs.ExpireWarned;
            long removed = -1;
            for (int i = 0; i < GameClock.StepHz * 6 && removed < 0 && back != null; i++)
            {
                WorldSimulation.StepMany(1);
                if (HomeValleyCargo.FindGroundItemBySalvageId(back, "settle:tsave") == null)
                {
                    removed = GameClock.Ticks;
                }
            }
            Expect(restored && removed >= due2 && removed <= due2 + 1,
                $"C3 真文件存读档：读档后到期步（{rs?.ExpireTick} = {due2}）与“已提示”（{rs?.ExpireWarned}）原样，回收发生在第 {removed} 步（到期 {due2}）");
        }

        // ── D 撤离后走回家园、到家结算 ─────────────────────────────────────────────

        /// <summary>真实出征破碎都市 → 在地点里装上关键物、货舱里有货、做过三选一 / 任务 / 线索 / 暴露变化 → 撤离 → 沿地形走回家园 → 到家结算。返回对照用的快照。</summary>
        private static string CheckWalkHome(int seed, bool observe, bool full, int saveAfterSeconds = -1)
        {
            CampaignState s = NewWorld(seed, observe);
            EnsureWarehouse(s);
            CoverEverywhere();
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int b = Spawn(s, CoreNear(s) + new Vector2(2f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, a, b);
            bool arrived = ExpeditionMarchService.ArriveNowForTests(s, observeSite: observe);
            s = CampaignSession.Current;
            if (!arrived)
            {
                Fail($"D{seed} 夹具：没能进入破碎都市");
                return "no-arrival";
            }
            WorldSimulation.StepMany(GameClock.StepHz);
            Vector2 at = Pos(a);
            RegionQuestItemRecord key = AddQuest(s, "settle:d-key-" + seed, FracturedCityLayout.ProtocolDataboxContentId, FracturedCityLayout.RegionId, RegionQuestItemState.OnGround, 0, at);
            bool loaded = FracturedCityRegion.TryCollectQuestItem(s, key.SalvageInstanceId, a).Success;
            // 人类遗产（FG8-EXP-04 承接：“结算清单里列出带回的遗产”）：在地点里装上车，到家入核心保管库。
            var her = HeritageService.All.Skip(1).FirstOrDefault() ?? HeritageService.All.FirstOrDefault();
            RegionQuestItemRecord legacy = her == null ? null
                : AddQuest(s, HeritageService.InstanceIdOf(her.Id), HeritageService.ContentId, FracturedCityLayout.RegionId, RegionQuestItemState.OnGround, 0, Pos(b));
            loaded &= legacy != null && FracturedCityRegion.TryCollectQuestItem(s, legacy.SalvageInstanceId, b).Success;
            LootPacketState st = LootPickService.StateOf(s);
            AddHeld(st, b, "settle:d-alloy", LootPickCatalog.KindItem, "alloy", null, 12);
            string module = LootPickCatalog.Rows.First(r => r.Kind == LootPickCatalog.KindModule).Target;
            AddHeld(st, b, "settle:d-module", LootPickCatalog.KindModule, AnalysisCatalog.UnparsedModuleId, module, 1);
            st.Picks = st.Picks.Concat(new[]
            {
                new LootPickLogRecord { PacketId = "settle:p1", Source = LootPickService.SourceElite, Faction = "silent", ChosenRowId = LootPickCatalog.Rows[0].Id, ChosenTier = "rare", Destination = "cargo", TechData = 3, Tick = GameClock.Ticks, Holder = a },
                // 修复第 2 轮（审查 P2）反例：远征在外期间在家分拣的旧数据包（携带者不是成员 / 直接入库）不算这次远征的三选一。
                new LootPickLogRecord { PacketId = "settle:p-home", Source = LootPickService.SourceElite, Faction = "silent", ChosenRowId = LootPickCatalog.Rows[0].Id, ChosenTier = "rare", Destination = "depot", TechData = 1, Tick = GameClock.Ticks },
            }).ToArray();
            FusionState fusion = s.Research.Fusion;
            fusion.Clues = fusion.Clues.Concat(new[]
            {
                new FusionClueRecord { Serial = 9001, RecipeId = FusionCatalog.Recipes[0].Id, Full = true, SessionKind = "expedition", SiteId = FracturedCityLayout.RegionId, Tick = GameClock.Ticks },
                // 反例：远征在外期间家园突袭产生的线索不进这次远征的结算。
                new FusionClueRecord { Serial = 9002, RecipeId = FusionCatalog.Recipes[Math.Min(1, FusionCatalog.Recipes.Count - 1)].Id, Full = false, SessionKind = "raid", SiteId = HomeValleyLayout.RegionId, Tick = GameClock.Ticks },
            }).ToArray();
            s.ObjectiveRecords = (s.ObjectiveRecords ?? Array.Empty<ObjectiveRecord>()).Concat(new[]
            {
                new ObjectiveRecord { ObjectiveId = "settle:q1", DefId = QuestCatalog.Rows[0].Id, State = ObjectiveState.Completed, OfferedTick = 0, EndedTick = GameClock.Ticks },
            }).ToArray();
            float exposure0 = AwayReportService.Current(s)?.ExposureAtStart ?? -1f;
            s.SignalExposure += 4f;
            int done0 = Rec(a).ExpeditionsCompleted;
            int alloy0 = HomeInventory.Stock(s, "alloy");
            int module0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);

            // 撤离：从地点入口出现在星球上、返程中，携带的关键物还在车上（到家才带回），离家报告还开着。
            ExpeditionReturnService.ReturnResult evac = ExpeditionReturnService.TryConfirmEvacuation();
            s = CampaignSession.Current;
            ExpeditionMarchState m = ExpeditionMarchService.Current(s);
            bool anchorOk = ExpeditionSiteAnchors.TryGet(s, FracturedCityLayout.RegionId, out Vector2 anchor);
            WorldSimulation.StepMany(2);
            bool atEntrance = anchorOk && Rec(a).RegionId == HomeValleyLayout.RegionId && Vector2.Distance(Pos(a), anchor) < 10f && Vector2.Distance(Pos(b), anchor) < 10f;
            bool deferred = key.State == RegionQuestItemState.Carried && key.CarrierLogicId == a && AwayReportService.IsOpen(s) && st.Held.Any(h => h.Machine == b)
                            && Rec(a).ExpeditionsCompleted == done0;
            bool walking = evac.Success && m != null && m.Phase == ExpeditionMarchService.PhaseReturning && ExpeditionMarchService.StatusOf(s) == ExpeditionMarchService.TeamStatus.Returning
                           && m.HasReturnGoal && WorldSimulation.ActiveExpedition == null;
            double eta = ExpeditionMarchService.EtaSeconds(s);
            if (full)
            {
                Expect(loaded && walking && atEntrance && deferred,
                    $"D1 撤离（DEBT-FG8EXP02-01）：队伍从破碎都市入口出现在星球上（离锚点 {Vector2.Distance(Pos(a), anchor):0.0} 格，{atEntrance}），状态“返程中”往最近的家园 / 前哨站走（{walking}，预计 {eta:0} 游戏秒）；" +
                    $"携带的关键物还在 {Name(a)} 车上、离家报告还开着、经历还没记（到家才结算 {deferred}）");
            }

            if (saveAfterSeconds > 0)
            {
                WorldSimulation.StepMany(GameClock.StepHz * saveAfterSeconds);
                SaveResult saved = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
                s = Reload(Slot);
                if (!saved.Success || s == null)
                {
                    Fail($"D{seed} 走回途中存档 / 读档失败");
                    return "save-failed";
                }
                m = ExpeditionMarchService.Current(s);
                key = s.RegionQuestItems.First(q => q.SalvageInstanceId == key.SalvageInstanceId);
                st = LootPickService.StateOf(s);
            }

            long arrivedAt = -1;
            for (int i = 0; i < 600 && arrivedAt < 0; i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz);
                if (ExpeditionMarchService.Current(s)?.Phase == ExpeditionMarchService.PhaseReturned)
                {
                    arrivedAt = ExpeditionMarchService.Current(s).PhaseTick; // 到家（已返回）那一步
                }
            }
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            ExpeditionSettlementRecord set = r?.Settlement;
            string sig = $"到家 {arrivedAt}｜带回 {string.Join(",", set?.Items.Select(i => i.Kind + ":" + i.ItemId + ":" + i.Dest) ?? Array.Empty<string>())}｜" +
                         $"{Mathf.RoundToInt(Pos(a).x)},{Mathf.RoundToInt(Pos(a).y)}";
            if (!full)
            {
                Expect(arrivedAt > 0 && r != null && r.HasSettlement && key.State == RegionQuestItemState.Recovered,
                    $"D{seed}（{(observe ? "观察" : "不观察")}{(saveAfterSeconds > 0 ? $"，走了 {saveAfterSeconds} 秒存读档" : string.Empty)}）：沿地形走回家园并结算（{sig}）");
                return sig;
            }
            bool home = arrivedAt > 0 && key.State == RegionQuestItemState.Recovered && !AwayReportService.IsOpen(s) && r != null && r.HasSettlement && set.Via == ExpeditionSettlementService.ViaFoot
                        && r.Outcome == AwayReportService.OutcomeEvacuated;
            bool items = set != null && set.Items.Any(i => i.Kind == ExpeditionSettlementService.KindKey && i.TargetId == key.SalvageInstanceId && i.Dest == ExpeditionSettlementService.DestRecovered)
                         && set.Items.Any(i => i.Kind == ExpeditionSettlementService.KindHeritage && i.Dest == ExpeditionSettlementService.DestVault) && HeritageService.IsObtained(s, her.Id)
                         && set.Items.Any(i => i.Kind == ExpeditionSettlementService.KindCargo && i.ItemId == "alloy" && i.Amount == 12 && i.Dest == ExpeditionSettlementService.DestWarehouse)
                         && set.Items.Any(i => i.Kind == ExpeditionSettlementService.KindCargo && i.ItemId == AnalysisCatalog.UnparsedModuleId && i.Dest == ExpeditionSettlementService.DestAnalysisWait);
            bool records = set != null && set.Picks.Any(p => p.PacketId == "settle:p1") && set.Quests.Any(q => q.ObjectiveId == "settle:q1" && q.State == (int)ObjectiveState.Completed)
                           && set.Clues.Contains(9001) && !set.Clues.Contains(9002) && !set.Picks.Any(p => p.PacketId == "settle:p-home") && set.HasExposure && Math.Abs(set.ExposureEnd - set.ExposureStart - 4f) < 0.01f && Math.Abs(set.ExposureStart - exposure0) < 0.01f;
            bool exp = set != null && set.Machines.Length == 2 && set.Machines.All(x => x.Alive && x.Expeditions == done0 + 1) && Rec(a).ExpeditionsCompleted == done0 + 1
                       && (Rec(a).ExperienceFlags ?? Array.Empty<string>()).Contains(MachineExperienceFlags.Returned);
            Expect(home && items && records && exp,
                $"D2 FGR-EXP-050 到家才结算（第 {arrivedAt} 步到家）：关键物此刻带回（{key.State}）；结算写带回的物品与去处（关键物 → 已带回、遗产 → 核心保管库、合金 12 → 仓库、未解析模块 → 仓库等解析 {items}）、" +
                $"三选一记录 / 任务 / 熔合线索（在家分拣的三选一与家园突袭线索不算）/ 暴露 {set?.ExposureStart:0.#} → {set?.ExposureEnd:0.#}（{records}）、机器经历（生还归来、累计远征 {done0 + 1} 次 {exp}）");

            // D3 远征卸货（下一个模拟步）：货舱里的货卸进仓库 / 按协议登记。
            WorldSimulation.StepMany(3);
            int heldLeft = st.Held.Count(h => h.Machine == b);
            int alloyGround = s.GroundItems.Where(g => g != null && g.ResourceType == ItemCatalog.ResourceTypeOf("alloy")).Sum(g => g.Amount);
            string lastLog = (s.StandingRules?.Log ?? Array.Empty<RuleLogRecord>()).LastOrDefault()?.Key;
            bool unloaded = heldLeft == 0 && HomeInventory.Stock(s, "alloy") >= alloy0 + 12 && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) == module0 + 1;
            Expect(unloaded, $"D3 到家后按“远征卸货”规则卸货：合金 {alloy0} → {HomeInventory.Stock(s, "alloy")}（地上 {alloyGround}）、未解析模块 {module0} → {HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId)}，" +
                $"货舱剩 {heldLeft} 份，规则日志“{lastLog}”（{unloaded}）");

            // D4 离家报告最前面一段“远征结算”：每一块都在、没有缺字。
            var lines = new List<AwayLine>();
            AwayReportView.Build(s, r, lines);
            List<AwayLine> mine = lines.Where(l => l.Section == AwayReportView.SectionSettle).ToList();
            string head = GameText.Format("settle.head", GameText.Get("settle.via.foot"), AwayReportService.SiteName(r.RegionId), AwayReportService.Duration(r.EndTick - r.StartTick));
            bool view = lines.Count > 0 && lines[0].Text == GameText.Get("settle.section") && mine.Any(l => l.Text == head)
                        && mine.Count(l => l.Text.StartsWith("带回：", StringComparison.Ordinal)) == 4 && mine.Any(l => l.Text.Contains("核心保管库")) && mine.Any(l => l.Text.StartsWith("三选一（精英）", StringComparison.Ordinal))
                        && mine.Any(l => l.Text.StartsWith("任务完成：", StringComparison.Ordinal)) && mine.Any(l => l.Text.StartsWith("熔合线索：", StringComparison.Ordinal))
                        && mine.Any(l => l.Text.StartsWith("暴露：", StringComparison.Ordinal) && l.Text.Contains("+4")) && mine.Count(l => l.Action == AwayLineAction.Machine) == 2
                        && !lines.Any(l => GameText.ContainsMarker(l.Text));
            // 面板（正式界面）：自动打开的离家报告里第一行就是“远征结算”。
            bool panel = false;
            GameObject go = null;
            AwayReportPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                VisualElement root = F.MountUxml(UiKitFolder + "AwayReportPanel.uxml", out go);
                AwayReportPanelUIToolkit p = go.AddComponent<AwayReportPanelUIToolkit>();
                p.SetRowTemplateForTests(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UiKitFolder + "AwayReportRow.uxml"));
                p.BindView(root);
                p.Refresh();
                panel = p.VisibleRowCount == lines.Count && p.RowText(0) == GameText.Get("settle.section") && p.RowText(1) == head;
            }
            finally
            {
                AwayReportPanelUIToolkit.InWorldOverrideForTests = false;
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            Expect(view && panel,
                $"D4 离家报告最前面是“远征结算”一段（{mine.Count} 行：“{head}”、带回 4 行（含遗产 → 核心保管库）、三选一、任务、线索、暴露 +4、两台机器经历、留在原地；无缺字 {view}）；离家报告面板第一行就是它（{panel}）");
            return sig;
        }

        private static void CheckObservationSeedsAndSave(string control)
        {
            string unobserved = CheckWalkHome(8501, observe: false, full: false);
            Expect(control != null && control == unobserved,
                $"E1 FGR-BASE-021 观察与否一致：镜头一直在家园 / 跟进地点 与 镜头不看 → 同一步到家、同样的结算（观察“{control}” / 不观察“{unobserved}”）");
            string saved = CheckWalkHome(8501, observe: true, full: false, saveAfterSeconds: 4);
            // 结算本身进存档：到家后存档 → 读档，离家报告里的“远征结算”一字不差。
            CampaignState now = CampaignSession.Current;
            AwayReportRecord before = AwayReportService.Recent(now).FirstOrDefault();
            string sigBefore = before != null ? JsonUtility.ToJson(before.Settlement) : "none";
            SaveResult res = CampaignAutoSaveService.SaveAuto(SaveReason.ExpeditionResolutionComplete);
            int from = CampaignSession.ActiveSlotIndex;
            CampaignState back = res.Success ? Reload(from, from == RunSlot ? Slot : RunSlot) : null;
            AwayReportRecord after = back != null ? AwayReportService.Recent(back).FirstOrDefault() : null;
            bool kept = after != null && after.HasSettlement && JsonUtility.ToJson(after.Settlement) == sigBefore && before.HasSettlement;
            Expect(kept, $"E2b 远征结算进存档：到家后存读档，离家报告里的“远征结算”原样（{kept}，{sigBefore.Length} 字节）");
            Expect(control != null && control == saved,
                $"E2 走回途中真文件存读档：读档后接着走，到家的步与结算与不读档的对照组一致（对照“{control}” / 存读档“{saved}”）");
            string other = CheckWalkHome(8502, observe: true, full: false);
            string third = CheckWalkHome(8503, observe: true, full: false);
            Expect(!other.StartsWith("no-", StringComparison.Ordinal) && !third.StartsWith("no-", StringComparison.Ordinal),
                $"E3 种子集（B25）：8502 / 8503 两个种子的锚点、路线不同，同样从入口走回家园并结算（“{other}” / “{third}”）");
        }

        // ── F 返程途中全灭 ────────────────────────────────────────────────────────

        private static void CheckWipeOnWalkHome()
        {
            CampaignState s = NewWorld(8507);
            CoverEverywhere();
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, a);
            ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz);
            RegionQuestItemRecord key = AddQuest(s, "settle:f-key", FracturedCityLayout.ProtocolDataboxContentId, FracturedCityLayout.RegionId, RegionQuestItemState.OnGround, 0, Pos(a));
            FracturedCityRegion.TryCollectQuestItem(s, key.SalvageInstanceId, a);
            ExpeditionReturnService.TryConfirmEvacuation();
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz * 3);
            Vector2 death = Pos(a);
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz);
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            bool kept = key.State == RegionQuestItemState.OnGround && key.RegionId == HomeValleyLayout.RegionId && Vector2.Distance(key.Position, death) < 0.6f
                        && !(FracturedCityRegion.Find(s)?.LostQuestSalvageIds ?? Array.Empty<string>()).Contains(key.SalvageInstanceId);
            bool settled = r != null && r.HasSettlement && r.Settlement.Via == ExpeditionSettlementService.ViaWiped && ExpeditionMarchService.Current(s).Phase == ExpeditionMarchService.PhaseNone
                           && r.Settlement.Left.Any(e => e.Kind == ExpeditionSettlementService.KindKey && e.ItemId == key.SalvageInstanceId);
            int c = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, c);
            // 原地待命（倒下的地方就在地点入口旁：不让去程的“前进”在那里直接进入地点）。
            ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Hold, out _);
            MoveTo(c, death + new Vector2(1f, 0f));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            // 捡起后下“返回家园”，快进到家（走正式的到家结算：携带的关键物在结算里带回）。
            bool cmdOk = ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Return, out string cmdWhy);
            bool returned = cmdOk && ExpeditionMarchService.ReturnNowForTests(s);
            bool back = returned && key.State == RegionQuestItemState.Recovered
                        && AwayReportService.Recent(s).FirstOrDefault()?.Settlement.Items.Any(i => i.TargetId == key.SalvageInstanceId && i.Dest == ExpeditionSettlementService.DestRecovered) == true;
            Expect(kept && settled && back,
                $"F 返程途中全灭：携带的关键物掉在倒下的地方（不判遗失 {kept}），按全灭结算、结算列出它（{settled}）；再派一台去捡起、带回家园（{key.State}，携带者 {key.CarrierLogicId}，" +
                $"返回命令 {cmdOk}{cmdWhy}，到家 {returned}，阶段 {ExpeditionMarchService.Current(s)?.Phase}）");
        }

        // ── G 远征地点里全灭 ──────────────────────────────────────────────────────

        private static void CheckSiteWipe()
        {
            CampaignState s = NewWorld(8508);
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, a);
            ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz);
            Vector2 at = Pos(a);
            RegionQuestItemRecord key = AddQuest(s, "settle:g-key", FracturedCityLayout.ProtocolDataboxContentId, FracturedCityLayout.RegionId, RegionQuestItemState.OnGround, 0, at);
            FracturedCityRegion.TryCollectQuestItem(s, key.SalvageInstanceId, a);
            AddHeld(LootPickService.StateOf(s), a, "settle:g-alloy", LootPickCatalog.KindItem, "alloy", null, 8);
            MachineRegistry.ApplyDamage(a, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz);
            bool wiped = GameRoot.FracturedCity != null && GameRoot.FracturedCity.IsWiped;
            bool keyDown = key.State == RegionQuestItemState.OnGround && key.RegionId == FracturedCityLayout.RegionId
                           && !(FracturedCityRegion.Find(s)?.LostQuestSalvageIds ?? Array.Empty<string>()).Contains(key.SalvageInstanceId);
            string list = ExpeditionSettlementService.LeftListText(s, FracturedCityLayout.RegionId);
            string summary = GameText.Format("settle.site.wipe_summary", 1);
            bool listed = list.StartsWith("留在原地（", StringComparison.Ordinal) && list.Contains(GameText.Get("settle.left.never")) && list.Contains("被敌方回收") && !GameText.ContainsMarker(list + summary);
            string longList = list + "\n" + string.Join("\n", Enumerable.Range(0, 6).Select(i => "· 超长名字的普通战利品测试条目 " + i + " ×999（破碎都市 -9999, 9999）· 23 小时 59 分（游戏时间）后被敌方回收"));
            string probe = UiToolkitLayoutProbe.Probe("Assets/GameRes/Raw/UI/Expedition/ExpeditionReturnPanel.uxml", "ExpeditionReturnPanelRoot", stressFill: true, prepare: root =>
            {
                Label g = root.Q<Label>("GroundLabel");
                Label sum = root.Q<Label>("SummaryLabel");
                if (g != null)
                {
                    g.text = longList;
                }
                if (sum != null)
                {
                    sum.text = summary;
                }
            });
            Expect(probe.StartsWith("PASS", StringComparison.Ordinal), $"G0 撤离 / 全灭面板布局探针（留在原地清单 7 行 + 全灭说明，四种分辨率）：{string.Join(" / ", probe.Split('\n').Take(3))}");
            ExpeditionReturnService.ReturnResult abandon = ExpeditionReturnService.TryConfirmAbandon();
            s = CampaignSession.Current;
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            var siteLines = new List<AwayLine>();
            if (r != null)
            {
                AwayReportView.Build(s, r, siteLines);
            }
            bool settled = abandon.Success && r != null && r.HasSettlement && r.Settlement.Via == ExpeditionSettlementService.ViaWiped
                           && r.Settlement.Left.Any(e => e.ItemId == key.SalvageInstanceId && e.RegionId == FracturedCityLayout.RegionId) && key.State == RegionQuestItemState.OnGround
                           && siteLines.Any(l => l.Section == AwayReportView.SectionSettle && l.Action == AwayLineAction.Notifications && !l.HasPos && l.Text.Contains(BlackBoxService.PlaceName(FracturedCityLayout.RegionId)))
                           && siteLines.Where(l => l.Section == AwayReportView.SectionSettle).All(l => l.IsEntry || l.Cls == "ar-row-title" || l.Cls == "ar-row-dim");
            Expect(wiped && keyDown && listed && settled,
                $"G1 远征地点里全灭（FG-GAP-133）：携带的关键物掉在破碎都市地上、不判遗失（{keyDown}）；全灭面板列出留在原地的东西与倒计时（“{list.Replace("\n", " / ")}”）并写明处理（{summary.Length} 字）；放弃远征后结算列出它（{settled}）");

            // G2 再派队进去，原地还在、能装载、撤离走回家园带回。
            int c = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, c);
            ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            bool again = key.State == RegionQuestItemState.OnGround && FracturedCityRegion.TryCollectQuestItem(s, key.SalvageInstanceId, c).Success;
            ExpeditionReturnService.TryConfirmEvacuation();
            bool walkedHome = ExpeditionMarchService.ReturnNowForTests(CampaignSession.Current);
            Expect(again && walkedHome && key.State == RegionQuestItemState.Recovered,
                $"G2 下一次远征再取：关键物还在原地、能装载（{again}），撤离走回家园后带回（{key.State}）");
        }

        // ── H 离开前清单（行军条二次确认）───────────────────────────────────────────

        private static void CheckLeaveWarning()
        {
            CampaignState s = NewWorld(8509);
            CoverEverywhere();
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            s = Depart(s, a);
            MoveTo(a, FieldPoint(s, 80f, 1));
            WorldSimulation.StepMany(GameClock.StepHz);
            Vector2 at = Pos(a);
            GroundItemRecord loot = Drop(s, at + new Vector2(18f, 0f), "settle:h-loot", 6);
            RegionQuestItemRecord key = AddQuest(s, "settle:h-key", FracturedCityLayout.ProtocolDataboxContentId, HomeValleyLayout.RegionId, RegionQuestItemState.OnGround, 0, at + new Vector2(0f, 18f));
            // 修复第 2 轮（审查 P1）：货舱掉落的关键材料（D-13 不盖回收章）也要列出；对照：同样没章的家园物流地面物不列。
            ItemDef keyMat = ItemCatalog.Items.First(i => i.Tier == KeyItemRetention.KeyTier);
            GroundItemRecord keyGround = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at + new Vector2(-18f, 0f), keyMat.ResourceType, 1, CargoService.HeldPrefix + "settle-h-key");
            GroundLootExpiry.Stamp(s, keyGround, keyMat.Id);
            GroundItemRecord logistics = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at + new Vector2(0f, -18f), ItemCatalog.ResourceTypeOf("alloy"), 3, "settle:h-logistics");
            var warnList = new List<SettleLeftRecord>();
            bool warn = ExpeditionSettlementService.LeaveWarning(s, out string text, warnList);
            bool keyMatListed = keyGround != null && keyGround.ExpireTick == 0 && warnList.Any(e => e.ItemId == keyMat.Id && e.Kind == ExpeditionSettlementService.KindKey && e.ExpireTick == 0)
                                && logistics != null && !warnList.Any(e => Mathf.Approximately(e.X, logistics.Position.x) && Mathf.Approximately(e.Y, logistics.Position.y));
            bool content = warn && keyMatListed && text.Contains("被敌方回收") && text.Contains(GameText.Get("settle.left.never")) && text.Contains("再点一次");
            GameObject go = null;
            bool firstHeld = false, reason = false, secondGoes = false, negative = false;
            try
            {
                VisualElement root = F.MountUxml(UiKitFolder + "WorldBar.uxml", out go);
                WorldBarHudUIToolkit hud = go.AddComponent<WorldBarHudUIToolkit>();
                hud.BindView(root);
                hud.Refresh();
                hud.IssueMarch(ExpeditionMarchService.Command.Return);
                firstHeld = ExpeditionMarchService.Current(s).Phase == ExpeditionMarchService.PhaseOutbound && hud.ReturnConfirmText == text;
                reason = hud.MarchReasonText == text;
                hud.IssueMarch(ExpeditionMarchService.Command.Return);
                secondGoes = ExpeditionMarchService.Current(s).Phase == ExpeditionMarchService.PhaseReturning && hud.ReturnConfirmText == null;
                // 负向：附近地上没东西 → 一次就走（不多问）。
                s.GroundItems = s.GroundItems.Where(g => g != loot && g != keyGround).ToArray();
                key.State = RegionQuestItemState.Recovered;
                ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Advance, out _);
                WorldSimulation.StepMany(2);
                hud.IssueMarch(ExpeditionMarchService.Command.Return);
                negative = ExpeditionMarchService.Current(s).Phase == ExpeditionMarchService.PhaseReturning && hud.ReturnConfirmText == null
                           && !ExpeditionSettlementService.LeaveWarning(s, out _);
            }
            finally
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            string barProbe = UiToolkitLayoutProbe.Probe(UiKitFolder + "WorldBar.uxml", null, stressFill: true, prepare: root =>
            {
                Label l = root.Q<Label>("WorldMarchReason");
                if (l != null)
                {
                    l.text = text;
                    l.RemoveFromClassList("uk-hidden");
                }
            });
            Expect(barProbe.StartsWith("PASS", StringComparison.Ordinal), $"H0 世界栏行军条原因行（离开前清单，三条 + 确认提示）布局探针：{string.Join(" / ", barProbe.Split('\n').Take(3))}");
            Expect(content && firstHeld && reason && secondGoes && negative,
                $"H FGR-EXP-032 离开一片区域前列出地上还没捡的东西：行军条第一次点“返回家园”只列清单（“{text}”，{firstHeld}；货舱掉落的关键材料 {keyMat.Id} 在列、家园物流地面物不在列 {keyMatListed}），8 秒内再点一次才返回（{secondGoes}）；负向：附近没东西时一次就走（{negative}）");
        }

        // ── I 室内场地里掉落的遗产 / 黑匣子 ─────────────────────────────────────────

        private static void CheckInterior()
        {
            CampaignState s = NewWorld(8510);
            EnsureWarehouse(s);
            GameConfig.fg.Surface interior = WorldGenContent.Surfaces.FirstOrDefault(WorldGenContent.IsInterior);
            var her = HeritageService.All.FirstOrDefault();
            if (interior == null || her == null)
            {
                Fail($"I 夹具：没有室内表面（{interior?.Id}）或遗产表为空（{her?.Id}）");
                return;
            }
            int h = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(2);
            MachineRecord rec = Rec(h);
            Vector2 p = Pos(h);
            rec.RegionId = interior.Id; // 测试捷径：把这台记在室内（真实进出由 FgTerritorySelfCheck 覆盖），只验证室内地上关键物的拾取
            RegionQuestItemRecord legacy = AddQuest(s, HeritageService.InstanceIdOf(her.Id), HeritageService.ContentId, interior.Id, RegionQuestItemState.OnGround, 0, p + new Vector2(1f, 0f));
            int picked0 = CargoService.InteriorPicked;
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool picked = legacy.State == RegionQuestItemState.Carried && legacy.CarrierLogicId == h && CargoService.InteriorPicked == picked0 + 1;
            rec.RegionId = HomeValleyLayout.RegionId; // 走出室内（测试捷径）
            MoveTo(h, CoreNear(s));
            WorldSimulation.StepMany(GameClock.StepHz * 2);
            bool home = legacy.State == RegionQuestItemState.Recovered && HeritageService.IsObtained(s, her.Id);
            Expect(picked && home,
                $"I 室内场地里掉落的遗产（DEBT-FG8EXP04-07）：在室内的己方机器走近即捡起（{picked}），带回家园范围即入核心保管库（{home}）");
        }

        // ── J 远征卸货送到解析台 ───────────────────────────────────────────────────

        private static void CheckUnloadToAnalysis()
        {
            CampaignState s = NewWorld(8511);
            EnsureWarehouse(s);
            StandingRuleRecord rule = StandingRuleService.ActiveUnloadRule(s);
            bool set = rule != null && StandingRuleService.TrySetUnloadAnalysis(s, rule.Serial, true, out string msg) && rule.SendToAnalysis;
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            WorldSimulation.StepMany(2);
            string module = LootPickCatalog.Rows.First(r => r.Kind == LootPickCatalog.KindModule).Target;
            LootPacketState st = LootPickService.StateOf(s);
            // 负向 1：开关关着 → 只进仓库（不排队）。
            StandingRuleService.TrySetUnloadAnalysis(s, rule.Serial, false, out _);
            AddHeld(st, a, "settle:j0", LootPickCatalog.KindModule, AnalysisCatalog.UnparsedModuleId, module, 1);
            int activeOff = HomeValleyAnalysis.ActiveCount(s);
            int stockOff = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);
            StandingRuleService.OnExpeditionReturned(s, new List<int> { a });
            WorldSimulation.StepMany(3);
            bool offStays = HomeValleyAnalysis.ActiveCount(s) == activeOff && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) == stockOff + 1;
            StandingRuleService.TrySetUnloadAnalysis(s, rule.Serial, true, out _);
            // 负向 2：没有解析台（测试捷径：把开局的解析台记录拿掉）→ 留在仓库、规则日志写明原因。
            BuildingRecord startBench = HomeValleyAnalysis.FindBench(s);
            if (startBench != null)
            {
                s.BuildingRecords = s.BuildingRecords.Where(x => x != startBench).ToArray();
            }
            AddHeld(st, a, "settle:j1", LootPickCatalog.KindModule, AnalysisCatalog.UnparsedModuleId, module, 1);
            int fail0 = StandingRuleService.UnloadAnalyzeFailed;
            int stock0 = HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId);
            // 修复第 2 轮（审查 P1）：走正式的“结算 → 卸货”顺序，断言结算条目按真实结果回写去处。
            AwayReportRecord r1 = SettleThenUnload(s, a);
            SettleItemRecord it1 = r1?.Settlement?.Items?.SingleOrDefault(i => i.Kind == ExpeditionSettlementService.KindCargo);
            var lines1 = new List<AwayLine>();
            if (r1 != null)
            {
                AwayReportView.Build(s, r1, lines1);
            }
            bool failDest = it1 != null && it1.Confirmed && it1.Dest == ExpeditionSettlementService.DestAnalysisWait && !string.IsNullOrEmpty(it1.Reason)
                            && lines1.Any(l => l.Section == AwayReportView.SectionSettle && l.Text.Contains("没能排进解析台")) && !lines1.Any(l => GameText.ContainsMarker(l.Text));
            bool noBench = offStays && StandingRuleService.UnloadAnalyzeFailed == fail0 + 1 && HomeInventory.Stock(s, AnalysisCatalog.UnparsedModuleId) == stock0 + 1
                           && (s.StandingRules?.Log ?? Array.Empty<RuleLogRecord>()).Any(l => l.Key == "rules.log.unload_analysis_fail") && failDest;
            if (startBench != null)
            {
                s.BuildingRecords = s.BuildingRecords.Concat(new[] { startBench }).ToArray();
            }
            // 有解析台 → 排进队列。
            F.PowerUp(s);
            GridCell? cell = F.FindFree(s, HomeValleyLayout.BuildingTypeAnalysisBench, 6f, 30f);
            if (cell.HasValue && HomeValleyAnalysis.FindBench(s) == null)
            {
                F.Built(s, HomeValleyLayout.BuildingTypeAnalysisBench, "settle_bench", cell.Value);
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
            WorldSimulation.StepMany(3);
            AddHeld(st, a, "settle:j2", LootPickCatalog.KindModule, AnalysisCatalog.UnparsedModuleId, module, 1);
            int ok0 = StandingRuleService.UnloadAnalyzed;
            int active0 = HomeValleyAnalysis.ActiveCount(s);
            AwayReportRecord r2 = SettleThenUnload(s, a);
            SettleItemRecord it2 = r2?.Settlement?.Items?.SingleOrDefault(i => i.Kind == ExpeditionSettlementService.KindCargo);
            bool okDest = it2 != null && it2.Confirmed && it2.Dest == ExpeditionSettlementService.DestAnalysis && string.IsNullOrEmpty(it2.Reason)
                          && it1 != null && it1.Dest == ExpeditionSettlementService.DestAnalysisWait; // 后一次卸货不改前一份结算
            bool queued = StandingRuleService.UnloadAnalyzed == ok0 + 1 && HomeValleyAnalysis.ActiveCount(s) == active0 + 1 && okDest;
            string lastLog = (s.StandingRules?.Log ?? Array.Empty<RuleLogRecord>()).LastOrDefault()?.Key;
            Expect(set && noBench && queued,
                $"J 远征卸货送到解析台（DEBT-FG4ECO06-04）：规则开关打开（{set}）；开关关着只进仓库（{offStays}）；没有解析台时留在仓库并写明原因，远征结算的去处回写为“仓库（没能排进解析台：原因）”（{noBench}；{it1?.Dest}/{it1?.Reason}）；有解析台时卸下的未解析模块直接排进解析台队列、结算去处确认为“解析台队列”（{queued}；{it2?.Dest}，{HomeValleyAnalysis.ActiveCount(s)} 项；解析台 {HomeValleyAnalysis.FindBench(s)?.ConstructionState}，最后日志“{lastLog}”）");
        }

        /// <summary>J 段：按正式到家的顺序（ReturnHome：结算 → 离家报告结束 → 远征卸货排到下一步）走一遍，返回这份结算。</summary>
        private static AwayReportRecord SettleThenUnload(CampaignState s, int machine)
        {
            AwayReportService.Begin(s, FracturedCityLayout.RegionId, new[] { machine });
            AwayReportRecord r = ExpeditionSettlementService.Settle(s, ExpeditionSettlementService.ViaFoot, new List<int> { machine });
            AwayReportService.End(s, AwayReportService.OutcomeEvacuated, notify: false);
            StandingRuleService.OnExpeditionReturned(s, new List<int> { machine });
            WorldSimulation.StepMany(3);
            return r;
        }

        // ── K 从远征地点入口呼叫投送舱接回 ──────────────────────────────────────────

        private static void CheckPodFromSiteEntrance()
        {
            CampaignState s = FgDropPodSelfCheck.NewWorld(8512, out BuildingRecord station);
            if (station == null)
            {
                Fail("K 夹具：没能放下投送站");
                return;
            }
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            CoverEverywhere();
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int b = Spawn(s, CoreNear(s) + new Vector2(2f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            for (int i = 0; i < 20 && !ExpeditionSiteAnchors.IsResolved(s, FracturedCityLayout.RegionId); i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            s = Depart(s, a, b);
            ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz);
            RegionQuestItemRecord key = AddQuest(s, "settle:k-key", FracturedCityLayout.ProtocolDataboxContentId, FracturedCityLayout.RegionId, RegionQuestItemState.OnGround, 0, Pos(a));
            FracturedCityRegion.TryCollectQuestItem(s, key.SalvageInstanceId, a);
            ExpeditionReturnService.TryConfirmEvacuation();
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            // 玩家先让队伍原地待命再呼叫回收（返程中的队伍会一直往家走，舱到时不在回收点；不替玩家停下，FGR-BASE-020）。
            bool held = ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Hold, out string holdWhy);
            WorldSimulation.StepMany(GameClock.StepHz / 2);
            Vector2 center = (Pos(a) + Pos(b)) * 0.5f;
            DropPreview p = default;
            Vector2 point = center;
            foreach (Vector2 off in new[] { Vector2.zero, new Vector2(3f, 0f), new Vector2(-3f, 0f), new Vector2(0f, 3f), new Vector2(0f, -3f) })
            {
                p = DropPodService.PreviewRecall(s, station.BuildingId, center + off);
                if (p.Ok)
                {
                    point = center + off;
                    break;
                }
            }
            bool called = held && p.Ok && DropPodService.TryRecall(s, station.BuildingId, point, out _);
            List<DropPodRecord> outbound = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateOutbound).ToList();
            if (outbound.Count > 0)
            {
                FgDropPodSelfCheck.StepTo(outbound.Max(x => x.ArriveTick) + 2);
            }
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            string why = r == null ? $"没有报告（报告开着 {AwayReportService.IsOpen(s)}，阶段 {ExpeditionMarchService.Current(s)?.Phase}，舱 {outbound.Count}，" +
                $"a 在 {Rec(a)?.RegionId}，b 在 {Rec(b)?.RegionId}，接回 {ExpeditionMarchService.PodReturnCount}）" : $"{r.Settlement?.Via}/{string.Join(",", r.Settlement?.Items.Select(i => i.Kind + ":" + i.Dest) ?? Array.Empty<string>())}/阶段 {ExpeditionMarchService.Current(s)?.Phase}/舱 {outbound.Count}";
            bool boarded = r != null && r.HasSettlement && r.Settlement.Via == ExpeditionSettlementService.ViaPod
                           && r.Settlement.Items.Any(i => i.TargetId == key.SalvageInstanceId && i.Dest == ExpeditionSettlementService.DestPod)
                           && ExpeditionMarchService.Current(s).Phase == ExpeditionMarchService.PhaseReturned;
            // 修复第 2 轮（审查 P1）：不再把携带者挪到核心旁——舱落回投送站这一刻就要带回（投送站是正式回收落点），与投送站离核心 / 仓库多远无关。
            int landed0 = DropPodService.QuestItemsLanded;
            bool returning = DropPodService.Pods(s).Any(x => x.State == DropPodService.StateReturning);
            if (returning)
            {
                // 只推进到最晚那只舱落地的那一步（不多等：CargoService 的“走进仓库范围即带回”每游戏秒才查一次，靠它带回的不算通过）。
                FgDropPodSelfCheck.StepTo(DropPodService.Pods(s).Where(x => x.State == DropPodService.StateReturning).Max(x => x.ArriveTick) + 1);
            }
            float depotGap = float.MaxValue;
            foreach (BuildingRecord bd in s.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (bd != null && (bd.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse || bd.BuildingTypeId == HomeValleyLayout.BuildingTypeCore))
                {
                    depotGap = Math.Min(depotGap, Vector2.Distance(bd.Position, station.Position));
                }
            }
            bool home = returning && key.State == RegionQuestItemState.Recovered && DropPodService.QuestItemsLanded == landed0 + 1;
            Expect(called && boarded && home,
                $"K 从远征地点入口呼叫投送舱接回（DEBT-FG8DROP01-01）：撤离后在地点入口呼叫回收（{(p.Ok ? "预览通过" : "预览被拒：" + p.Code)}，{called}）；最后一台登舱按“投送舱接回”结算（携带的关键物写“随投送舱返回” {boarded}：{why}）；" +
                $"舱落回投送站那一步即带回（不挪携带者，{key.State}，落地带回 {DropPodService.QuestItemsLanded - landed0} 件；投送站离最近的核心 / 仓库 {depotGap:0.0} 格，仓库范围 {LootPickCatalog.DepotRadius:0} 格）");
        }

        // ── K2～K4 分批接回：只接回携带关键物的那台 ──────────────────────────────
        // 修复第 3 轮（审查 P1）：先登舱的成员离开行军名单，它随舱落地带回的关键物 / 货舱货仍要记进这次远征的结算，且只记一次。
        // 修复第 4 轮（审查 P1）：反过来的先后顺序——舱还在返程途中远征就结束了（其余成员全灭 / 走回了家），舱里那台的关键物、货舱货与“生还归来”同样要进结算；
        // 有成员随舱生还时，其余成员阵亡不算全灭（按“随投送舱返回”结算）。

        private sealed class PartialRecall
        {
            public CampaignState S;
            public int A;
            public int B;
            public RegionQuestItemRecord Key;
            public bool Called;
            public bool Partial;
            public string Preview;
            public DropPodRecord Pod;
        }

        /// <summary>真实出发 → 到达 → a 捡起关键物 → 撤离 → 待命；b 挪到回收范围外；只呼叫回收 a，推进到 a 登舱（舱刚开始返程）。</summary>
        private static PartialRecall SetupPartialRecall(int seed, string tag)
        {
            var c = new PartialRecall();
            CampaignState s = FgDropPodSelfCheck.NewWorld(seed, out BuildingRecord station);
            if (station == null)
            {
                return null;
            }
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            CoverEverywhere();
            int a = Spawn(s, CoreNear(s), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            int b = Spawn(s, CoreNear(s) + new Vector2(2f, 0f), HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id);
            for (int i = 0; i < 20 && !ExpeditionSiteAnchors.IsResolved(s, FracturedCityLayout.RegionId); i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            s = Depart(s, a, b);
            ExpeditionMarchService.ArriveNowForTests(s);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(GameClock.StepHz);
            RegionQuestItemRecord key = AddQuest(s, "settle:" + tag + "-key", FracturedCityLayout.ProtocolDataboxContentId, FracturedCityLayout.RegionId, RegionQuestItemState.OnGround, 0, Pos(a));
            FracturedCityRegion.TryCollectQuestItem(s, key.SalvageInstanceId, a);
            ExpeditionReturnService.TryConfirmEvacuation();
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            AddHeld(LootPickService.StateOf(s), a, "settle:" + tag + "-alloy", LootPickCatalog.KindItem, "alloy", null, 5);
            bool held = ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Hold, out _);
            WorldSimulation.StepMany(GameClock.StepHz / 2);
            // b 站到回收范围外（只接回 a）。
            MoveTo(b, Pos(a) + new Vector2(DropPodService.RecallRadius * 3f, 0f));
            WorldSimulation.StepMany(2);
            DropPreview p = default;
            Vector2 point = Pos(a);
            foreach (Vector2 off in new[] { Vector2.zero, new Vector2(-2f, 0f), new Vector2(0f, 2f), new Vector2(0f, -2f) })
            {
                p = DropPodService.PreviewRecall(s, station.BuildingId, Pos(a) + off);
                if (p.Ok)
                {
                    point = Pos(a) + off;
                    break;
                }
            }
            c.Called = held && p.Ok && DropPodService.TryRecall(s, station.BuildingId, point, out _);
            c.Preview = p.Ok ? "预览通过" : "预览被拒：" + p.Code;
            List<DropPodRecord> outbound = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateOutbound).ToList();
            if (outbound.Count > 0)
            {
                FgDropPodSelfCheck.StepTo(outbound.Max(x => x.ArriveTick) + 2);
            }
            c.Pod = DropPodService.PodCarrying(s, a);
            c.Partial = AwayReportService.IsOpen(s) && !ExpeditionMarchService.IsTeamMember(s, a) && ExpeditionMarchService.IsTeamMember(s, b)
                        && c.Pod != null && c.Pod.State == DropPodService.StateReturning;
            c.S = s;
            c.A = a;
            c.B = b;
            c.Key = key;
            return c;
        }

        private static int KeyRows(ExpeditionSettlementRecord st, RegionQuestItemRecord key) => st?.Items.Count(i => i.TargetId == key.SalvageInstanceId) ?? -1;

        private static int AlloyRows(ExpeditionSettlementRecord st, string dest) =>
            st?.Items.Count(i => i.Kind == ExpeditionSettlementService.KindCargo && i.ItemId == "alloy" && (dest == null || i.Dest == dest)) ?? -1;

        private static bool CameBack(ExpeditionSettlementRecord st, int id) => st?.Machines.Any(x => x.LogicId == id && x.Alive && x.Expeditions >= 1) == true;

        private static string Rows(ExpeditionSettlementRecord st) => st == null ? "没有结算" : string.Join(",", st.Items.Select(i => i.Kind + ":" + i.ItemId + ":" + i.Dest));

        // K2：先接回携带者，它的舱落地后其余成员阵亡。
        private static void CheckPodPartialRecall()
        {
            PartialRecall c = SetupPartialRecall(8514, "k2");
            if (c == null)
            {
                Fail("K2 夹具：没能放下投送站");
                return;
            }
            CampaignState s = c.S;
            int a = c.A, b = c.B;
            RegionQuestItemRecord key = c.Key;
            int landed0 = DropPodService.QuestItemsLanded;
            List<DropPodRecord> back = DropPodService.Pods(s).Where(x => x.State == DropPodService.StateReturning).ToList();
            if (back.Count > 0)
            {
                FgDropPodSelfCheck.StepTo(back.Max(x => x.ArriveTick) + 1);
            }
            AwayReportRecord open = AwayReportService.Current(s);
            bool noted = open != null && key.State == RegionQuestItemState.Recovered && DropPodService.QuestItemsLanded == landed0 + 1
                         && open.Settlement.Items.Any(i => i.TargetId == key.SalvageInstanceId && i.Dest == ExpeditionSettlementService.DestRecovered)
                         && AlloyRows(open.Settlement, null) == 1
                         && (open.Settlement.Landed ?? Array.Empty<int>()).Contains(a);
            WorldSimulation.StepMany(3); // 远征卸货在下一模拟步按规则卸 a 的货
            // 其余成员阵亡 → 有成员已随舱生还，不算全灭：按“随投送舱返回”结算；先落地那件关键物正好一条（已带回），a 的货只记一次，a 记“生还归来”。
            int wiped0 = ExpeditionMarchService.WipedCount;
            MachineRegistry.ApplyDamage(b, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz);
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            ExpeditionSettlementRecord st = r?.Settlement;
            int keyRows = KeyRows(st, key);
            int alloyRows = AlloyRows(st, null);
            bool aBack = CameBack(st, a);
            bool notWipe = !AwayReportService.IsOpen(s) && ExpeditionMarchService.WipedCount == wiped0 && r != null && r.HasSettlement
                           && st.Via == ExpeditionSettlementService.ViaPod && r.Outcome == AwayReportService.OutcomeEvacuated;
            bool ok = notWipe && keyRows == 1 && st.Items.First(i => i.TargetId == key.SalvageInstanceId).Dest == ExpeditionSettlementService.DestRecovered && alloyRows == 1 && aBack;
            Expect(c.Called && c.Partial && noted && ok,
                $"K2 分批接回（审查 P1）：只接回携带关键物的 {Name(a)}（{c.Preview}，{c.Called}；登舱后离开行军名单、报告仍开着 {c.Partial}）；" +
                $"它的舱落地那一刻关键物带回并记进进行中的结算、货舱货记一条、记进 Landed（{noted}）；其余成员阵亡后不算全灭、按“随投送舱返回”结算（{notWipe}，{st?.Via}）：" +
                $"关键物正好 {keyRows} 条且写“已带回”、合金 {alloyRows} 条、{Name(a)} 记生还归来 {aBack}（{Rows(st)}）");
        }

        // K3：舱还在返程途中，其余成员全部阵亡。
        private static void CheckPodRecallInFlightWipe()
        {
            PartialRecall c = SetupPartialRecall(8515, "k3");
            if (c == null)
            {
                Fail("K3 夹具：没能放下投送站");
                return;
            }
            CampaignState s = c.S;
            int a = c.A, b = c.B;
            RegionQuestItemRecord key = c.Key;
            long arrive = c.Pod?.ArriveTick ?? 0;
            int wiped0 = ExpeditionMarchService.WipedCount;
            MachineRegistry.ApplyDamage(b, 1e6f);
            WorldSimulation.StepMany(GameClock.StepHz);
            bool inFlight = c.Pod != null && c.Pod.State == DropPodService.StateReturning && c.Pod.MachineId == a && GameClock.Ticks < arrive;
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            ExpeditionSettlementRecord st = r?.Settlement;
            bool settled = !AwayReportService.IsOpen(s) && r != null && r.HasSettlement && st.Via == ExpeditionSettlementService.ViaPod
                           && r.Outcome == AwayReportService.OutcomeEvacuated && ExpeditionMarchService.WipedCount == wiped0;
            bool listed = KeyRows(st, key) == 1 && st.Items.First(i => i.TargetId == key.SalvageInstanceId).Dest == ExpeditionSettlementService.DestPod
                          && AlloyRows(st, ExpeditionSettlementService.DestPod) == 1 && CameBack(st, a) && !CameBack(st, b);
            string before = Rows(st);
            // 舱落地：关键物真实带回，结算不重复记、也不新开报告。
            FgDropPodSelfCheck.StepTo(arrive + 1);
            WorldSimulation.StepMany(3);
            AwayReportRecord r2 = AwayReportService.Recent(s).FirstOrDefault();
            bool landed = key.State == RegionQuestItemState.Recovered && ReferenceEquals(r2, r) && !AwayReportService.IsOpen(s)
                          && KeyRows(r2?.Settlement, key) == 1 && AlloyRows(r2?.Settlement, null) == 1;
            Expect(c.Called && c.Partial && inFlight && settled && listed && landed,
                $"K3 分批接回、舱在返程途中其余成员全灭（审查 P1）：只接回 {Name(a)}（{c.Preview}，登舱 {c.Partial}），舱飞行途中 {Name(b)} 阵亡（舱仍在途中 {inFlight}）；" +
                $"不算全灭、按“随投送舱返回”结算、报告写“返回”（{settled}，{st?.Via}）；关键物与货舱货各一条且写“随投送舱返回”、{Name(a)} 记生还归来（{listed}：{before}）；" +
                $"舱落地后关键物带回（{key.State}），结算不重复记（{landed}：{Rows(r2?.Settlement)}）");
        }

        // K4：舱还在返程途中，其余成员先走回了家。
        private static void CheckPodRecallInFlightWalkHome()
        {
            PartialRecall c = SetupPartialRecall(8516, "k4");
            if (c == null)
            {
                Fail("K4 夹具：没能放下投送站");
                return;
            }
            CampaignState s = c.S;
            int a = c.A, b = c.B;
            RegionQuestItemRecord key = c.Key;
            long arrive = c.Pod?.ArriveTick ?? 0;
            bool cmd = ExpeditionMarchService.IssueCommand(s, ExpeditionMarchService.Command.Return, out string why);
            bool walked = cmd && ExpeditionMarchService.ReturnNowForTests(s);
            bool inFlight = c.Pod != null && c.Pod.State == DropPodService.StateReturning && c.Pod.MachineId == a && GameClock.Ticks < arrive;
            AwayReportRecord r = AwayReportService.Recent(s).FirstOrDefault();
            ExpeditionSettlementRecord st = r?.Settlement;
            bool settled = !AwayReportService.IsOpen(s) && r != null && r.HasSettlement && st.Via == ExpeditionSettlementService.ViaFoot;
            bool listed = KeyRows(st, key) == 1 && st.Items.First(i => i.TargetId == key.SalvageInstanceId).Dest == ExpeditionSettlementService.DestPod
                          && AlloyRows(st, ExpeditionSettlementService.DestPod) == 1 && CameBack(st, a) && CameBack(st, b);
            string before = Rows(st);
            FgDropPodSelfCheck.StepTo(arrive + 1);
            WorldSimulation.StepMany(3);
            AwayReportRecord r2 = AwayReportService.Recent(s).FirstOrDefault();
            bool landed = key.State == RegionQuestItemState.Recovered && ReferenceEquals(r2, r) && KeyRows(r2?.Settlement, key) == 1 && AlloyRows(r2?.Settlement, null) == 1;
            Expect(c.Called && c.Partial && walked && inFlight && settled && listed && landed,
                $"K4 分批接回、舱在返程途中其余成员走回家（审查 P1）：只接回 {Name(a)}（{c.Preview}，登舱 {c.Partial}），{Name(b)} 走回家园（命令 {cmd}{why}，到家 {walked}，舱仍在途中 {inFlight}）；" +
                $"按走回结算（{settled}，{st?.Via}）；关键物与货舱货各一条且写“随投送舱返回”、两台都记生还归来（{listed}：{before}）；" +
                $"舱落地后关键物带回（{key.State}），结算不重复记（{landed}：{Rows(r2?.Settlement)}）");
        }

        // ── M 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerf()
        {
            CampaignState s = NewWorld(8513);
            Vector2 field = FieldPoint(s, 120f, 2);
            for (int i = 0; i < 500; i++)
            {
                Drop(s, field + new Vector2(i % 25 * 2f, i / 25 * 2f), "settle:perf" + i, 1);
            }
            var sw = Stopwatch.StartNew();
            const int Calls = 20000;
            for (int i = 0; i < Calls; i++)
            {
                GroundLootExpiry.WorldStep(s, GameClock.Ticks);
            }
            sw.Stop();
            double perCall = sw.Elapsed.TotalMilliseconds * 1000.0 / Calls;
            GroundLootExpiry.ResetScanStats();
            GroundLootExpiry.Process(s, GameClock.Ticks + GroundLootExpiry.LifeTicks + 1);
            double scan = GroundLootExpiry.MaxScanMs;
            bool gone = s.GroundItems.All(g => g == null || !g.SalvageInstanceId.StartsWith("settle:perf", StringComparison.Ordinal));
            PerfLines.Add($"M 性能（Editor batchmode / 影子工程，非真机）：500 堆地上战利品时每步到期判定平均 {perCall:0.000} 微秒（平时 O(1)）；到期那一步整体扫描 + 回收 500 堆 {scan:0.00} 毫秒");
            Expect(perCall < 5.0 && scan < 50.0 && gone,
                $"M 性能：每步到期判定 {perCall:0.000} 微秒（< 5 微秒，平时 O(1)）；到期那一步回收 500 堆 {scan:0.00} 毫秒（< 50 毫秒，只发生在到期步）；回收完 {gone}");
        }

        // ── 夹具 ──────────────────────────────────────────────────────────────

        private static void AddHeld(LootPacketState st, int machine, string id, string kind, string itemId, string target, int amount)
        {
            st.Held = st.Held.Concat(new[]
            {
                new LootHeldRecord { HeldId = id, Machine = machine, Kind = kind, ItemId = itemId, TargetId = target ?? string.Empty, Amount = amount, Space = LootPickCatalog.SpaceOf(itemId, amount) },
            }).ToArray();
        }

        private static RegionQuestItemRecord AddQuest(CampaignState s, string sid, string content, string region, RegionQuestItemState state, int carrier, Vector2 at)
        {
            var q = new RegionQuestItemRecord { SalvageInstanceId = sid, RegionId = region, ContentId = content, State = state, CarrierLogicId = carrier, Position = at };
            s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Concat(new[] { q }).ToArray();
            BlackBoxService.Touch();
            return q;
        }

        /// <summary>地上一堆普通战利品（与野外掉落 / 阵亡掉落同一个盖章入口）。</summary>
        private static GroundItemRecord Drop(CampaignState s, Vector2 at, string sid, int amount)
        {
            GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, at, ItemCatalog.ResourceTypeOf("alloy"), amount, sid);
            GroundLootExpiry.Stamp(s, g, "alloy");
            return g;
        }

        private static void CoverEverywhere()
        {
            SignalCoverageService.OverrideForTests = (site, pos) => new SignalCoverageSample(true, true, 50f, pos, 80f, SignalCoverageSourceKind.RelayTower);
            SignalCoverageService.Invalidate();
        }

        private static CampaignState Depart(CampaignState s, params int[] ids)
        {
            WorldSimulation.StepMany(3);
            for (int i = 0; i < 20 && !ExpeditionSiteAnchors.IsResolved(s, FracturedCityLayout.RegionId); i++)
            {
                WorldSimulation.StepMany(GameClock.StepHz);
            }
            ExpeditionDepartureService.DepartureResult dep = ExpeditionDepartureService.TryDepart(ids, true, ExpeditionDepartureService.ExpeditionTarget.SilentRuins);
            s = CampaignSession.Current;
            WorldSimulation.StepMany(3);
            if (!dep.Success || !ids.All(id => ExpeditionMarchService.IsTeamMember(s, id)))
            {
                Fail($"夹具：出发失败（{dep.Outcome}：{string.Join("，", dep.Reasons ?? Array.Empty<string>())}）");
            }
            return s;
        }

        private static IEnumerable<string> NotifyDetails(string type) =>
            NotificationCenter.History.Where(e => e?.Type != null && e.Type.Id == type).SelectMany(e => e.Members).Select(m => m.Detail ?? string.Empty);

        private static void MoveTo(int logicId, Vector2 at)
        {
            CombatSite site = WorldSimulation.Home?.Combat;
            if (site != null && site.TryGetMachineUnit(logicId, out int unit))
            {
                site.Kernel.SetPosition(unit, new Unity.Mathematics.double2(at.x, at.y));
            }
            if (MachineRegistry.TryGetRecord(logicId, out MachineRecord m))
            {
                m.WorldPosition = at;
            }
        }

        private static Vector2 Pos(int logicId) =>
            MachineRegistry.TryGetLivePosition(logicId, out Vector2 p) ? p : MachineRegistry.TryGetRecord(logicId, out MachineRecord m) ? m.WorldPosition : Vector2.zero;

        private static MachineRecord Rec(int id) => MachineRegistry.TryGetRecord(id, out MachineRecord m) ? m : null;

        private static string Name(int id) => Rec(id) != null ? MachineNaming.Short(Rec(id)) : "#" + id;

        private static Vector2 FieldPoint(CampaignState s, float dist, int dir)
        {
            GridCell c = HomeGridService.CorePivot(s);
            float ang = dir * Mathf.PI / 4f;
            var raw = new GridCell(c.X + Mathf.RoundToInt(Mathf.Cos(ang) * dist), c.Y + Mathf.RoundToInt(Mathf.Sin(ang) * dist));
            GridCell ok = SiegeService.NearestPassable(raw, 12);
            return new Vector2(ok.X, ok.Y);
        }

        /// <summary>归还核心旁的一格（以核心建筑的位置为准，在家园范围 loot.pick.depot_radius 内）。</summary>
        private static Vector2 CoreNear(CampaignState s)
        {
            Vector2 core = HomeValleyLayout.Core.Position;
            GridCell ok = SiegeService.NearestPassable(new GridCell(Mathf.RoundToInt(core.x) + 5, Mathf.RoundToInt(core.y) - 5), 8);
            return new Vector2(ok.X, ok.Y);
        }

        private static void EnsureWarehouse(CampaignState s)
        {
            BuildingRecord wh = s.BuildingRecords.FirstOrDefault(b => b != null && BuildingOps.IsWarehouse(b) && !HomeGridService.IsRelocationGhost(b));
            if (wh != null)
            {
                wh.ConstructionState = BuildingConstructionState.Operational;
                wh.Health = BuildingOps.MaxDurability(wh.BuildingTypeId);
            }
            else
            {
                GridCell? c = F.FindFree(s, HomeValleyLayout.BuildingTypeWarehouse, 6f, 30f);
                if (c.HasValue)
                {
                    F.Built(s, HomeValleyLayout.BuildingTypeWarehouse, "settle_wh", c.Value);
                }
            }
            HomeValleyPowerGrid.Recompute(s);
            F.Resync(s);
        }

        private static CampaignState NewWorld(int seed, bool observe = true, int version = 3)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            WorldGenService.Invalidate();
            HomeValleyWorkOrders.ResetSessionState();
            GroundLootExpiry.ResetSessionState();
            SignalCoverageService.OverrideForTests = null;
            SignalCoverageService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgsettle-" + seed, "Standard", seed);
            WorldGenService.ApplyNewGameWorld(s, seed, WorldSettings.Resolve(version, WorldGenContent.DefaultPresetId));
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            HomeValleyController home = WorldSimulation.LoadHome(resume: false);
            if (observe)
            {
                WorldView.Observe(home.SiteId);
            }
            WorldSimulation.StepMany(2);
            RegionRecord ruins = FracturedCityRegion.Find(s);
            if (ruins != null && ruins.State == RegionState.Locked)
            {
                ruins.State = RegionState.Available;
            }
            return s;
        }

        private static CampaignState Reload(int fromSlot, int toSlot = RunSlot)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            GroundLootExpiry.ResetSessionState();
            File.Copy(CampaignSaveService.SlotPath(fromSlot), CampaignSaveService.SlotPath(toSlot), true);
            string bak = CampaignSaveService.SlotPath(toSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(toSlot);
            if (!r.Success)
            {
                Fail("读档失败：" + r.Message);
                return null;
            }
            CampaignSession.Set(toSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            WorldView.Observe(HomeValleyLayout.RegionId);
            WorldSimulation.StepMany(1);
            return r.State;
        }

        private static int Spawn(CampaignState s, Vector2 at, string chassis, string blueprint)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(chassis, blueprint, HomeValleyLayout.RegionId, at, 120f, 120f);
            if (r.Success && MachineRegistry.TryGetRecord(r.LogicId, out MachineRecord rec))
            {
                MachineLoadoutRegistry.Register(s, rec.LogicId, rec.BlueprintId, rec.BlueprintVersion);
            }
            WorldSimulation.StepMany(2);
            return r.Success ? r.LogicId : 0;
        }

        private static void Step(Action check)
        {
            try
            {
                Debug.Log($"[FgExpeditionSettlementSelfCheck] {DateTime.Now:HH:mm:ss} {check.Method.Name}");
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
            finally
            {
                SignalCoverageService.OverrideForTests = null;
                SignalCoverageService.Invalidate();
                LootPickCatalog.ResetForTests();
                GameClock.SetPaused(false);
                GameClock.SetSpeed(1f);
                GameSettings.SetLanguage(GameLanguage.ZhCn);
            }
        }

        /// <summary>单段入口（bash tools/unity-validate.sh GameLogic.EditorTools.FgExpeditionSettlementSelfCheck.RunSegment）。</summary>
        public static void RunSegment() => RunFromMenu();

        private static void Expect(bool ok, string message)
        {
            if (ok)
            {
                _pass++;
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
            _report?.AppendLine(text);
        }
    }
}
