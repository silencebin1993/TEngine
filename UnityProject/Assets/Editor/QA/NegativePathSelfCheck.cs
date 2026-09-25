using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.UI.Common;
using GameLogic.UI.WorkOrder;
using UnityEditor;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// ER8-NEG-01（逻辑层部分，不依赖旅程机器人）：负向路径矩阵——缺料、断电、仓满、厂口堵塞、主档损坏、关键模块丢失、
    /// Boss 撤离重置、核心门、信标误触发、重复事件、反复读档。每条都走真实服务入口，核对：稳定失败码、玩家可见文字
    /// （中文、不含原因码）、失败时已提交的进度不变、恢复动作之后能继续。
    /// “从哪一屏发现问题”的真机部分留给旅程机器人。会清空机器登记表并改写存档目录（改到临时目录），Play 模式下跳过。
    /// 并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class NegativePathSelfCheck
    {
        private static readonly Regex RawCode = new Regex(@"[a-z]+[-_][a-z0-9_:=-]+", RegexOptions.IgnoreCase);

        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：负向路径")]
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
            Line("\n[负向] 负向路径矩阵（ER8-NEG-01 逻辑层）");
            if (Application.isPlaying)
            {
                Line("  - Play 模式下跳过（会清空机器登记表、改写存档目录）");
                return 0;
            }
            string tempSaves = Path.Combine(Path.GetTempPath(), "bingames-neg-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                CheckRepairShortfall();
                CheckProductionShortfallPowerAndExit();
                CheckQueueOrderWhilePaused();
                CheckStorageFull();
                CheckCorruptSaveAndReloads(tempSaves);
                CheckLostKeyModuleReissue();
                CheckBossRetreatReset();
                CheckCoreGate();
                CheckBeaconMisfire();
                CheckDuplicateEvent();
            }
            catch (Exception e)
            {
                Fail($"负向自检抛异常：{e}");
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                try
                {
                    if (Directory.Exists(tempSaves))
                    {
                        Directory.Delete(tempSaves, true);
                    }
                }
                catch (Exception)
                {
                    // 临时目录清不掉不影响结论。
                }
                MachineRegistry.ResetForNewCampaign();
                FeedbackCues.ResetForTests();
            }
            return _fail;
        }

        // ── 缺料 ────────────────────────────────────────────────────

        private static void CheckRepairShortfall()
        {
            MachineRegistry.ResetForNewCampaign();
            CampaignState s = CampaignState.CreateNew("neg-repair", "Standard", 31);
            s.CurrentRegionId = HomeValleyLayout.RegionId;
            s.BuildingRecords = new[] { Building(HomeValleyLayout.BuildingTypeGenerator, BuildingConstructionState.Damaged, BuildingPowerState.NotApplicable) };
            int worker = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc001ChassisId, HomeValleyLayout.BlueprintErc001Id,
                HomeValleyLayout.RegionId, Vector2.zero, 100f, 100f).LogicId;
            s.Scrap = 10;

            HomeValleyWorkOrders.WorkOrderOpResult fail = HomeValleyWorkOrders.TryCreateRepair(s, HomeValleyLayout.BuildingTypeGenerator, worker);
            string text = HomeValleyWorkOrders.DescribeCommandFailure(fail.FailureReason);
            Expect(!fail.Success && fail.FailureReason.StartsWith("insufficient-scrap", StringComparison.Ordinal)
                   && s.Scrap == 10 && (s.WorkOrders?.Length ?? 0) == 0
                   && s.BuildingRecords[0].ConstructionState == BuildingConstructionState.Damaged,
                "缺料·修复：稳定失败码 insufficient-scrap，废料、工单、建筑状态都不变");
            Expect(text.StartsWith("废料不足：需要 30，现有 10", StringComparison.Ordinal) && Readable(text),
                $"缺料·修复：玩家看到“{text}”（写明缺多少与怎么补，不含原因码）");

            HomeValleyWorkOrders.WorkOrderOpResult busy = HomeValleyWorkOrders.TryCreateRepair(s,
                HomeValleyLayout.BuildingTypeGenerator, MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId,
                    HomeValleyLayout.BlueprintErc003Id, HomeValleyLayout.RegionId, Vector2.zero, 120f, 120f).LogicId);
            string cannot = HomeValleyWorkOrders.DescribeCommandFailure(busy.FailureReason);
            Expect(!busy.Success && cannot.Contains("不能修复") && Readable(cannot), $"战斗履带机不能修复：玩家看到“{cannot}”");

            s.Scrap = 100;
            HomeValleyWorkOrders.WorkOrderOpResult ok = HomeValleyWorkOrders.TryCreateRepair(s, HomeValleyLayout.BuildingTypeGenerator, worker);
            Expect(ok.Success && !string.IsNullOrEmpty(ok.WorkOrderId) && s.WorkOrders.Length == 1 && s.Scrap < 100,
                $"恢复：补足废料后同一指令成功，并当场预留修复费用（剩余 {s.Scrap}）");
        }

        private static void CheckProductionShortfallPowerAndExit()
        {
            MachineRegistry.ResetForNewCampaign();
            CampaignState s = CampaignState.CreateNew("neg-factory", "Standard", 32);
            s.CurrentRegionId = HomeValleyLayout.RegionId;
            BuildingRecord station = Building(HomeValleyLayout.BuildingTypeAssemblyStation, BuildingConstructionState.Operational, BuildingPowerState.Powered);
            s.BuildingRecords = new[] { station };
            s.Scrap = 0;

            HomeValleyFactory.FactoryOpResult first = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            HomeValleyFactory.Tick(s, 1f);
            FactoryQueueItemRecord head = s.FactoryQueues.First(q => q.QueueItemId == first.QueueItemId);
            string waitText = QueueText.Reason(head.BlockedReason);
            Expect(first.Success && head.State == FactoryQueueState.WaitingResources && s.Scrap == 0 && head.Progress == 0f,
                $"缺料·生产：排队成功但停在“缺材料”，不扣废料、不前进（状态 {head.State}）");
            Expect(waitText.StartsWith("废料不足：需要 60，现有 0", StringComparison.Ordinal) && Readable(waitText),
                $"缺料·生产：队列写明“{waitText}”");

            s.Scrap = 200;
            HomeValleyFactory.Tick(s, 1f);
            Expect(head.State == FactoryQueueState.Running && s.Scrap == 140, $"恢复：补足废料后同一项自动开工并扣 60（状态 {head.State}，剩 {s.Scrap}）");

            HomeValleyFactory.Tick(s, 5f);
            float before = head.Progress;
            station.PowerState = BuildingPowerState.Unpowered;
            HomeValleyFactory.Tick(s, 5f);
            HomeValleyFactory.Tick(s, 5f);
            string powerText = QueueText.Reason(head.BlockedReason);
            Expect(head.State == FactoryQueueState.WaitingPower && Mathf.Approximately(head.Progress, before) && powerText.Contains("进度保留"),
                $"断电：停在“缺电力”，进度冻结不回退（{before:F1}），玩家看到“{powerText}”");
            station.PowerState = BuildingPowerState.Powered;
            HomeValleyFactory.Tick(s, 5f); // 复电第一帧只切回 Running（事务幂等重预留，不重复扣费）。
            bool resumed = head.State == FactoryQueueState.Running && s.Scrap == 140;
            HomeValleyFactory.Tick(s, 5f);
            Expect(resumed && head.Progress > before, $"恢复：复电后切回进行中、不重复扣费，从保留的进度继续（{before:F1}→{head.Progress:F1}）");

            // 第二台排队：第一台出厂后停在出口，第二台完工时出口被占用。
            HomeValleyFactory.FactoryOpResult second = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id);
            for (int i = 0; i < 80; i++)
            {
                HomeValleyFactory.Tick(s, 1f);
            }
            FactoryQueueItemRecord next = s.FactoryQueues.First(q => q.QueueItemId == second.QueueItemId);
            int producedBefore = MachineRegistry.AllRecords.Count(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId);
            Expect(head.State == FactoryQueueState.Completed && next.State == FactoryQueueState.OutputBlocked && producedBefore == 1
                   && QueueText.Reason(next.BlockedReason).Contains("出口被占用"),
                $"厂口堵塞：第一台停在出口时第二台完工也不出厂（第二台 {next.State}，已出厂 {producedBefore} 台）");

            MachineRecord blocker = MachineRegistry.AllRecords.First(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId);
            HomeValleyFactory.ReleaseFromFactory(blocker.LogicId);
            HomeValleyFactory.Tick(s, 1f);
            Expect(next.State == FactoryQueueState.Completed && MachineRegistry.AllRecords.Count(m => m.ChassisId == HomeValleyLayout.Erc003ChassisId) == 2,
                "恢复：第一台驶离出口后第二台自动出厂");

            HomeValleyFactory.FactoryOpResult locked = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintHoverId);
            string lockedText = HomeValleyFactory.DescribeFailure(locked.FailureReason);
            Expect(!locked.Success && lockedText.Contains("解析台通电") && Readable(lockedText), $"未解锁蓝图：面板写“{lockedText}”而不是原因码");
        }

        /// <summary>暂停中连续排队（战役时间不走）：必须按点击先后开工。此前同一时刻的几项靠随机 ID 定先后。</summary>
        private static void CheckQueueOrderWhilePaused()
        {
            bool ordered = true;
            for (int round = 0; round < 8; round++)
            {
                MachineRegistry.ResetForNewCampaign();
                CampaignState s = CampaignState.CreateNew("neg-fifo-" + round, "Standard", 40 + round);
                s.CurrentRegionId = HomeValleyLayout.RegionId;
                s.BuildingRecords = new[] { Building(HomeValleyLayout.BuildingTypeAssemblyStation, BuildingConstructionState.Operational, BuildingPowerState.Powered) };
                s.Scrap = 500;
                string a = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id).QueueItemId;
                string b = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintHaulerId).QueueItemId;
                string c = HomeValleyFactory.TryEnqueueProduce(s, HomeValleyLayout.BlueprintErc003Id).QueueItemId;
                HomeValleyFactory.Tick(s, 1f);
                FactoryQueueState sa = s.FactoryQueues.First(q => q.QueueItemId == a).State;
                FactoryQueueState sb = s.FactoryQueues.First(q => q.QueueItemId == b).State;
                FactoryQueueState sc = s.FactoryQueues.First(q => q.QueueItemId == c).State;
                ordered &= sa == FactoryQueueState.Running && sb == FactoryQueueState.Queued && sc == FactoryQueueState.Queued;
            }
            Expect(ordered, "暂停中连续排 3 台（战役时间不走）：8 轮都按点击先后开工，后排的不会插队");
        }

        // ── 仓满 ────────────────────────────────────────────────────

        private static void CheckStorageFull()
        {
            MachineRegistry.ResetForNewCampaign();
            CampaignState s = CampaignState.CreateNew("neg-storage", "Standard", 33);
            s.CurrentRegionId = HomeValleyLayout.RegionId;
            int capacity = HomeValleyCargo.GetStorageCapacity(s, "Scrap");
            s.Scrap = capacity;
            GroundItemRecord pile = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, new Vector2(4f, 4f), "Scrap", 20, "neg:pile");
            HomeValleyCargo.StoreResult full = HomeValleyCargo.CommitHaul(s, HomeValleyCargo.TryReserveHaul(s, pile.GroundItemId));
            GroundItemRecord back = s.GroundItems?.FirstOrDefault(g => g.RegionId == HomeValleyLayout.RegionId && g.ResourceType == "Scrap");
            string text = WorkOrderPanelUIToolkit.ReasonText(full.FailureReason);
            Expect(!full.Success && full.FailureReason.StartsWith("storage-full", StringComparison.Ordinal) && s.Scrap == capacity
                   && back != null && back.Amount == 20,
                $"仓满：稳定失败码 storage-full，废料不超上限，20 废料退回地面不丢（上限 {capacity}）");
            Expect(text.Contains("仓库已满") && Readable(text), $"仓满：玩家看到“{text}”");

            s.Scrap = 100;
            HomeValleyCargo.StoreResult ok = HomeValleyCargo.CommitHaul(s, HomeValleyCargo.TryReserveHaul(s, back.GroundItemId));
            Expect(ok.Success && s.Scrap == 120 && !(s.GroundItems ?? Array.Empty<GroundItemRecord>()).Any(g => g.GroundItemId == back.GroundItemId),
                $"恢复：腾出仓位后同一堆废料入库（{s.Scrap}），地面不再残留");
        }

        // ── 主档损坏 / 反复读档 ─────────────────────────────────────────

        private static void CheckCorruptSaveAndReloads(string dir)
        {
            CampaignSaveService.SaveDirectoryOverrideForTests = dir;
            MachineRegistry.ResetForNewCampaign();
            CampaignState s = CampaignState.CreateNew("neg-save", "Standard", 34);
            s.CurrentRegionId = HomeValleyLayout.RegionId;
            s.Scrap = 77;
            const int slot = 0;
            CampaignSaveResult(CampaignSaveService.Save(slot, s, SaveReason.Manual), "第一次保存");
            string firstFingerprint = CampaignFingerprint.Compute(CampaignSaveService.Load(slot).State);
            s.Scrap = 88;
            CampaignSaveResult(CampaignSaveService.Save(slot, s, SaveReason.Manual), "第二次保存（生成备份）");

            string main = CampaignSaveService.SlotPath(slot);
            string text = File.ReadAllText(main);
            File.WriteAllText(main, text.Substring(0, text.Length / 2));
            LoadResult corrupt = CampaignSaveService.Load(slot);
            CampaignSlotMetadata meta = CampaignSaveService.GetSlotMetadata(slot);
            Expect(corrupt.Outcome == LoadOutcome.Corrupt && corrupt.State == null && meta.State == CampaignSlotState.Corrupt && meta.HasBackup,
                $"主档损坏：读档判定损坏（{corrupt.Outcome}）、不产生半个战役，槽位显示损坏且有备份可恢复");

            bool restored = CampaignSaveService.RestoreFromBak(slot);
            LoadResult back = CampaignSaveService.Load(slot);
            Expect(restored && back.Outcome == LoadOutcome.Success && back.State.Scrap == 77
                   && CampaignFingerprint.Compute(back.State) == firstFingerprint,
                "恢复：从备份恢复后读到上一次完好的存档（指纹与当时一致）");

            string fingerprint = CampaignFingerprint.Compute(back.State);
            bool stable = true;
            int records = -1;
            for (int i = 0; i < 20; i++)
            {
                LoadResult again = CampaignSaveService.Load(slot);
                stable &= again.Outcome == LoadOutcome.Success && CampaignFingerprint.Compute(again.State) == fingerprint;
                MachineRegistry.LoadFromCampaignState(again.State);
                records = records < 0 ? MachineRegistry.RecordCount : records;
                stable &= MachineRegistry.RecordCount == records;
            }
            Expect(stable, $"连续读档 20 次：指纹逐次一致，机器登记数不增长（{records}）");
            CampaignSaveService.SaveDirectoryOverrideForTests = null;
        }

        private static void CampaignSaveResult(SaveResult r, string step)
        {
            if (r.Outcome != SaveOutcome.Success)
            {
                Fail($"{step}失败：{r.Outcome} {r.Message}");
            }
        }

        // ── 关键模块丢失 ───────────────────────────────────────────────

        private static void CheckLostKeyModuleReissue()
        {
            CampaignState s = CampaignState.CreateNew("neg-lost", "Standard", 35);
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            string marker = FracturedCityLayout.MarkerModuleContentId;
            s.RegionQuestItems = new[]
            {
                new RegionQuestItemRecord { SalvageInstanceId = "neg:marker-1", RegionId = FracturedCityLayout.RegionId, ContentId = marker, State = RegionQuestItemState.Lost },
            };
            FracturedCityRegion.RecoveryLockerCheck(s);
            FracturedCityRegion.RecoveryLockerCheck(s);
            int onGround = s.RegionQuestItems.Count(q => q.ContentId == marker && q.State == RegionQuestItemState.OnGround);
            RegionQuestItemRecord reissued = s.RegionQuestItems.FirstOrDefault(q => q.ContentId == marker && q.State == RegionQuestItemState.OnGround);
            Expect(onGround == 1 && reissued != null && (reissued.Position - FracturedCityLayout.RecoveryLocker.Position).sqrMagnitude < 0.01f
                   && CampaignObjectiveCatalog.QuestStatusText(s, marker) == "在地面，还没装车",
                $"关键模块丢失：下次进入由恢复柜补发一件（恢复柜位置、重复进入不多发，共 {onGround} 件）");

            reissued.State = RegionQuestItemState.Recovered;
            s.RegionQuestItems = s.RegionQuestItems.Append(new RegionQuestItemRecord
            {
                SalvageInstanceId = "neg:marker-2", RegionId = FracturedCityLayout.RegionId, ContentId = marker, State = RegionQuestItemState.Lost,
            }).ToArray();
            FracturedCityRegion.RecoveryLockerCheck(s);
            Expect(!s.RegionQuestItems.Any(q => q.ContentId == marker && q.State == RegionQuestItemState.OnGround),
                "已带回过的关键模块不再补发（不刷第二份）");
        }

        // ── Boss 撤离重置 ──────────────────────────────────────────────

        private static void CheckBossRetreatReset()
        {
            CampaignState s = CampaignState.CreateNew("neg-boss", "Standard", 36);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            RegionRecord foundry = FoundryOutpostRegion.Find(s);
            FoundryOutpostCoreBoss.EnsureInitialized(s, foundry);
            RegionEnemyRecord core = FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.MainCoreId);
            RegionEnemyRecord node1 = FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.CoreNode1Id);
            RegionEnemyRecord node2 = FoundryOutpostRegion.FindEnemy(s, FoundryOutpostLayout.CoreNode2Id);

            (bool shieldedOk, string shieldedReason) = FoundryOutpostCoreBoss.ApplyDamage(s, core, 50f);
            Expect(!shieldedOk && shieldedReason.StartsWith("core-invulnerable-in-state:Shielded", StringComparison.Ordinal) && Mathf.Approximately(core.Health, core.MaxHealth),
                "护盾期打主核心：稳定失败码 core-invulnerable-in-state，主核心不掉血");

            FoundryOutpostCoreBoss.ApplyDamage(s, node1, node1.MaxHealth + 1f);
            FoundryOutpostCoreBoss.ApplyDamage(s, node2, node2.MaxHealth + 1f);
            (bool hitOk, _) = FoundryOutpostCoreBoss.ApplyDamage(s, core, 50f);
            Expect(FoundryOutpostCoreBoss.GetState(foundry) >= CoreBossState.Phase1 && hitOk && core.Health < core.MaxHealth,
                $"两个供能节点毁掉后主核心可被击伤（{FoundryOutpostCoreBoss.GetState(foundry)}）");

            FoundryOutpostCoreBoss.ResetToPreBossState(s, foundry);
            Expect(!FoundryOutpostCoreBoss.IsInitialized(foundry) && node1.IsAlive && node2.IsAlive
                   && Mathf.Approximately(node1.Health, node1.MaxHealth) && Mathf.Approximately(core.Health, core.MaxHealth),
                "Boss 没打完就撤离：节点与主核心恢复满血，下次重新开始");

            FoundryOutpostCoreBoss.EnsureInitialized(s, foundry);
            foundry.CoreState = CoreBossState.Destroyed.ToString();
            FoundryOutpostCoreBoss.ResetToPreBossState(s, foundry);
            Expect(FoundryOutpostCoreBoss.GetState(foundry) == CoreBossState.Destroyed, "主核心已摧毁后撤离：不重置（已提交的进度不回滚）");
        }

        // ── 核心门 ────────────────────────────────────────────────────

        private static void CheckCoreGate()
        {
            MachineRegistry.ResetForNewCampaign();
            CampaignState s = CampaignState.CreateNew("neg-gate", "Standard", 37);
            FoundryOutpostRegion.EnsureRegionRecordSeeded(s);
            FoundryOutpostRegion.ActionResult gate = FoundryOutpostRegion.CanEnterCoreZone(s);
            Expect(!gate.Success && gate.FailureReason.Contains("重炮解析") && gate.FailureReason.Contains("熔穿过载蓝图保存") && gate.FailureReason.Contains("现役机实装"),
                $"核心门三灯未亮：拒绝进入并逐项写明缺什么（“{gate.FailureReason}”）");
            Expect(FoundryOutpostRegion.IsBeyondCoreGateLine(new Vector2(0f, FoundryOutpostLayout.CoreGateBlockLineY + 0.5f))
                   && FoundryOutpostRegion.IsBeyondCoreGateLine(new Vector2(40f, FoundryOutpostLayout.CoreGateBlockLineY + 0.5f)),
                "封锁线与横坐标无关：从侧面绕过去同样算越线（绕路无效）");
        }

        // ── 信标误触发 ─────────────────────────────────────────────────

        private static void CheckBeaconMisfire()
        {
            CampaignState s = CampaignState.CreateNew("neg-beacon", "Standard", 38);
            s.PlaySeconds = 1000f;
            HomeValleyBeacon.ActionResult notUnlocked = HomeValleyBeacon.TryStartLaunch(s);
            s.ObjectiveRecords = new[] { new ObjectiveRecord { ObjectiveId = CampaignObjectiveTracker.Obj09, State = ObjectiveState.Completed } };
            BuildingRecord beacon = Building(HomeValleyLayout.BuildingTypeBeacon, BuildingConstructionState.Operational, BuildingPowerState.Unpowered);
            s.BuildingRecords = new[] { beacon };
            HomeValleyBeacon.ActionResult unpowered = HomeValleyBeacon.TryStartLaunch(s);
            Expect(!notUnlocked.Success && notUnlocked.FailureReason == "not-unlocked" && !unpowered.Success
                   && unpowered.FailureReason == "not-operational-or-powered" && !HomeValleyBeacon.IsLaunched(s),
                "信标：未解锁、未通电时都拒绝启动（稳定失败码），不会误触发胜利");

            beacon.PowerState = BuildingPowerState.Powered;
            HomeValleyBeacon.ActionResult start = HomeValleyBeacon.TryStartLaunch(s);
            HomeValleyBeacon.ActionResult again = HomeValleyBeacon.TryStartLaunch(s);
            s.PlaySeconds += HomeValleyBeacon.LaunchCutsceneSeconds;
            HomeValleyBeacon.Tick(s, HomeValleyBeacon.LaunchCutsceneSeconds);
            HomeValleyBeacon.Tick(s, 1f);
            HomeValleyBeacon.ActionResult after = HomeValleyBeacon.TryStartLaunch(s);
            int events = s.EventLedger.Count(e => e.EventId == CampaignObjectiveTracker.BeaconLaunchEventId);
            Expect(start.Success && again.FailureReason == "already-launching" && after.FailureReason == "already-launched"
                   && HomeValleyBeacon.IsLaunched(s) && events == 1,
                $"信标：演出中再按不重来，演出结束只授予一次胜利事件（{events} 条），之后再按不重复");
        }

        // ── 重复事件 ──────────────────────────────────────────────────

        private static void CheckDuplicateEvent()
        {
            CampaignState s = CampaignState.CreateNew("neg-event", "Standard", 39);
            bool first = CampaignEventLedger.TryGrant(s, "neg:grant", "Test", 1f);
            bool repeated = true;
            for (int i = 0; i < 10; i++)
            {
                repeated &= !CampaignEventLedger.TryGrant(s, "neg:grant", "Test", 2f + i);
            }
            Expect(first && repeated && s.EventLedger.Count(e => e.EventId == "neg:grant") == 1, "同一事件重复 10 次只记一次（奖励不重复发）");
        }

        // ── 工具 ────────────────────────────────────────────────────

        private static BuildingRecord Building(string typeId, BuildingConstructionState construction, BuildingPowerState power) => new BuildingRecord
        {
            BuildingId = HomeValleyLayout.RegionId + ":" + typeId,
            BuildingTypeId = typeId,
            RegionId = HomeValleyLayout.RegionId,
            ConstructionState = construction,
            PowerState = power,
        };

        /// <summary>玩家文字：有中文、不含原因码形态（snake/kebab 带冒号参数）。</summary>
        private static bool Readable(string text) =>
            !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"[一-鿿]") && !RawCode.IsMatch(text);

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
