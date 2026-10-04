using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Notifications;
using GameLogic.Stage;
using UnityEngine;
using M = GameLogic.EditorTools.JourneyBots.FgjM5Common;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FGJ-M5 远征段（里程碑第 3 步“从远征回放拿到线索”、第 1 步“远征带回的加密固件”）：冷却液机 + 电弧机 + 一台机器出征破碎都市，
    /// 选中两台右键敌人一起打（RTS：右键敌人 = 攻击），远征场次里“短路”（浸湿 + 电击同时出现）触发到线索门槛；接入冷却液机、开到撤离点按住 E 撤离。
    /// 回家时远征场次关闭，仿真实验室一次性分析这场的战斗记录，给出指向熔合配方的线索（FGR-RND-044）。
    /// 协议数据盒用进度夹具装进冷却液机的货舱（DEBT-FG5E2E01-03：读协议终端要先摧毁守在旁边的监听节点，那是 Demo 关卡流程；敌方物品的远征掉落在 FG8-LOOT-01），
    /// 撤离结算照正式流程把它变成一枚未破解的标记跳转芯片（FG1-SIG-06 / FG5-RND-02）。
    /// </summary>
    public static partial class FgjM5Journey
    {
        private static Vector2 EvacPoint => FracturedCityLayout.EntryEvac.Position;

        private static IEnumerable<JourneyStep> ExpeditionSteps() => new[]
        {
            S("prep_open", "左键点信号塔打开远征准备面板", 15, c => FgjM1Journey.ClickBuildingIf(HomeValleyLayout.BuildingTypeSignalTower, !GameRoot.HomeValley.IsExpeditionPrepPanelOpen),
                FgjM1Journey.TickPrepOpen, retries: 2),
            S("prep_pick", "勾选出征名单（冷却液机 + 电弧机 + 再一台满足人数）", 20, null, FgjM2Common.TickRosterPicked),
            S("prep_depart", "点“出发”（有在办工作时确认中断）", 30, c => FgjM1Journey.ClickUi(c, "[HomeValleyExpeditionPrepHost]", "DepartButton"), FgjM1Journey.TickDeparted, retries: 1),
            S("fc_sel_a", "左键点冷却液机", 20, c => FgjM2Common.ClickFc(c, c.GetInt("wetM"), false), c => FgjM2Common.TickFcSelection(c, c.GetInt("wetM")), retries: 4),
            S("fc_sel_b", "按住 Shift 左键点电弧机（加选）", 20, c => FgjM2Common.ClickFc(c, c.GetInt("shockM"), true),
                c => FgjM2Common.TickFcSelection(c, c.GetInt("wetM"), c.GetInt("shockM")), retries: 4),
            S("fc_attack", "右键点最近的敌人（右键敌人 = 攻击）：两台一起打，远征场次里“短路”触发到线索门槛（部分线索 3 次）", 240, FgjM2Common.RightClickFcEnemy, TickFcClueReactions, retries: 1),
            S("uplink", "机器列表点冷却液机：接入", 25, c => { c.SetInt("other", c.GetInt("wetM")); FgjM1Journey.ClickMachineList(c, c.GetInt("wetM")); }, TickUplinked, retries: 3),
            S("databox", "进度夹具：协议数据盒装进冷却液机的货舱（读协议终端要先摧毁监听节点，Demo 关卡流程；敌方物品掉落在 FG8-LOOT-01；DEBT-FG5E2E01-03）", 10,
                ApplyDataboxFixture, TickDataboxFixture),
            S("drive_evac", "开着冷却液机（WASD）回撤离点", 120, null, TickDriveToEvac),
            S("evac_hold", "在撤离点按住交互键（默认 E）打开撤离清单", 40, c => JourneyInput.HoldAction(GameActionId.Interact, 1.6), FgjM1Journey.TickEvacPanel, retries: 3),
            S("evac_confirm", "点“确认撤离”：远征队返回家园（远征场次关闭）", 30, c => FgjM1Journey.ClickUi(c, "[FracturedCityExpeditionReturnHost]", "ConfirmButton"),
                FgjM1Journey.TickReturnedHome, retries: 1),
            S("clue", "从远征回放拿到线索：仿真实验室分析这场的“短路”次数，给出指向熔合配方的线索（通知“新的熔合线索”）；带回的协议数据盒变成一枚未破解的标记跳转芯片", 60, null, TickClue),
        };

        /// <summary>两台一起打：远征场次里“短路”的次数到部分线索门槛（FusionCatalog.CluePartialMin）；目标死了就像玩家一样右键下一个最近的敌人。</summary>
        private static StepOutcome TickFcClueReactions(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            FgjM2Common.SampleFrame();
            if (c.Get("enemy").Length == 0)
            {
                return StepOutcome.Fail("破碎都市里找不到干扰场外活着的敌人");
            }
            if (FgjM2Common.PanPending(c, FgjM2Common.RightClickFcEnemy))
            {
                return StepOutcome.Wait;
            }
            ReactionSessionRecord sess = ReactionAttribution.Current(St, FracturedCityLayout.RegionId, create: false);
            ReactionShareRecord row = sess?.Reactions?.FirstOrDefault(r => r.ReactionId == FgjM2Common.Conduct);
            int need = FusionCatalog.CluePartialMin;
            if (row != null && row.Count >= need)
            {
                c.SetInt("conduct", row.Count);
                return StepOutcome.Done($"远征场次“{ReactionAttribution.Title(sess)}”记下“{FgjM2Common.ConductName}” {row.Count} 次（部分线索门槛 {need}、完整线索门槛 {FusionCatalog.ClueFullMin}）");
            }
            CombatSite site = GameRoot.FracturedCity.Combat;
            int a = c.GetInt("wetM");
            int b = c.GetInt("shockM");
            if (c.StepElapsed > 1.5 && !FgjM2Common.IsAttacking(site, a) && !FgjM2Common.IsAttacking(site, b))
            {
                int n = c.GetInt("reissue");
                if (n >= 8)
                {
                    return StepOutcome.Fail($"右键了 {n + 1} 个敌人，“{FgjM2Common.ConductName}”只有 {row?.Count ?? 0} 次（门槛 {need}）");
                }
                c.SetInt("reissue", n + 1);
                c.Log($"目标 {c.Get("enemy")} 已不在（攻击命令结束、没有自己找别的敌人）；“{FgjM2Common.ConductName}”已 {row?.Count ?? 0} 次，右键下一个最近的敌人");
                FgjM2Common.RightClickFcEnemy(c);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickUplinked(JourneyContext c)
        {
            int b = c.GetInt("other");
            if (c.StepElapsed < 0.6)
            {
                return StepOutcome.Wait;
            }
            if (FgjM1Journey.UiFail(c).Length > 0)
            {
                return StepOutcome.Retry("点机器列表失败：" + FgjM1Journey.UiFail(c));
            }
            if (SignalPresence.CurrentMachineLogicId != b || GameRoot.FracturedCity?.PossessedMachineLogicId != b)
            {
                return c.StepElapsed < 8 ? StepOutcome.Wait : StepOutcome.Retry($"没有接入 {FgjM1Journey.Label(b)}");
            }
            return c.StepElapsed < 2.0 ? StepOutcome.Wait : StepOutcome.Done($"接入 {FgjM1Journey.Label(b)}（镜头直控）");
        }

        /// <summary>
        /// 进度夹具（DEBT-FG5E2E01-03）：协议数据盒装进冷却液机的货舱——等同“摧毁监听节点 → 读协议终端 → 按住 E 装载”（Demo 关卡流程，FGJ-M1 系列与 Demo 回归覆盖）。
        /// 只写区域关键物这一条记录（Carried）；撤离结算（<c>FracturedCityRegion.ResolveExtraction</c>）照正式流程把它标成 Recovered、发一枚未破解固件芯片。
        /// </summary>
        private static void ApplyDataboxFixture(JourneyContext c)
        {
            CampaignState s = St;
            int carrier = c.GetInt("wetM");
            RawFirmwareService.TryEncryptedFirmwareOf(FracturedCityLayout.ProtocolDataboxContentId, out string fw);
            c.Set("rawFw", fw ?? string.Empty);
            c.SetInt("raw0", s.PrimitiveChips?.Count(x => x != null && x.CardDefId == fw) ?? 0);
            var rec = new RegionQuestItemRecord
            {
                SalvageInstanceId = "fgjm5-databox",
                RegionId = FracturedCityLayout.RegionId,
                ContentId = FracturedCityLayout.ProtocolDataboxContentId,
                State = RegionQuestItemState.Carried,
                CarrierLogicId = carrier,
                Position = default,
            };
            s.RegionQuestItems = (s.RegionQuestItems ?? Array.Empty<RegionQuestItemRecord>()).Append(rec).ToArray();
        }

        private static StepOutcome TickDataboxFixture(JourneyContext c)
        {
            RegionQuestItemRecord q = St.RegionQuestItems?.FirstOrDefault(x => x != null && x.SalvageInstanceId == "fgjm5-databox");
            return q != null && q.State == RegionQuestItemState.Carried && q.CarrierLogicId == c.GetInt("wetM") && c.Get("rawFw").Length > 0
                ? StepOutcome.Done($"进度夹具：协议数据盒在 {FgjM1Journey.Label(c.GetInt("wetM"))} 的货舱里（解析后对应敌方加密固件“{M.FwName(c.Get("rawFw"))}”）")
                : StepOutcome.Fail("数据盒夹具没有生效");
        }

        private static StepOutcome TickDriveToEvac(JourneyContext c)
        {
            FgjM2Common.SampleFrame();
            JourneyCommon.ResumeIfAutoPaused(c);
            int b = c.GetInt("other");
            if (!FgjM1Journey.FcPos(b, out Vector2 at))
            {
                return StepOutcome.Fail("找不到接入的机器");
            }
            float d = Vector2.Distance(at, EvacPoint);
            if (d <= FracturedCityController.InteractRange - 0.8f)
            {
                JourneyInput.ReleaseKeys();
                return StepOutcome.Done($"{FgjM1Journey.Label(b)} 开到撤离点（离撤离点 {d:F1} 格）");
            }
            if (!JourneyInput.Holding)
            {
                int stuck = c.GetInt(FgjM3Common.SK(c, "stuck"));
                float last = float.TryParse(c.Get(FgjM3Common.SK(c, "last")), NumberStyles.Float, CultureInfo.InvariantCulture, out float l) ? l : float.MaxValue;
                if (d > last - 0.2f)
                {
                    c.SetInt(FgjM3Common.SK(c, "stuck"), stuck + 1);
                }
                c.Set(FgjM3Common.SK(c, "last"), d.ToString("R", CultureInfo.InvariantCulture));
                FgjM1Journey.DriveToward(at, EvacPoint, 0.4, stuck % 4 == 3 ? 1 : 0);
            }
            return StepOutcome.Wait;
        }

        private static StepOutcome TickClue(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            CampaignState s = St;
            FusionState f = FusionService.StateOf(s);
            FusionClueRecord clue = f?.Clues?.LastOrDefault(x => x != null && x.SessionKind == ReactionAttribution.KindExpedition && x.ReactionId == FgjM2Common.Conduct);
            string fw = c.Get("rawFw");
            PrimitiveChipRecord raw = s.PrimitiveChips?.LastOrDefault(x => x != null && x.CardDefId == fw);
            if (clue == null || raw == null)
            {
                return c.StepElapsed < 40 ? StepOutcome.Wait
                    : StepOutcome.Fail($"回家后：线索 {(clue == null ? "没有" : "有")}（线索 {f?.Clues?.Length ?? 0} 条、待分析 {f?.Pending?.Length ?? 0} 场、实验室 {ResearchService.BuiltLabCount(s)} 座）；" +
                                       $"未破解芯片 {(raw == null ? "没有" : "有")}");
            }
            if (!FusionCatalog.TryGet(clue.RecipeId, out FusionRecipeDef def))
            {
                return StepOutcome.Fail("线索指向的配方不在配方表里：" + clue.RecipeId);
            }
            bool notified = NotificationCenter.History.Any(e => e.Type?.Id == "fusion_clue");
            bool isRaw = FirmwareKinds.IsRaw(s, fw);
            if (!notified || !isRaw)
            {
                return StepOutcome.Fail($"线索通知 {notified}；带回的固件仍未破解 {isRaw}");
            }
            c.Set("clue.recipe", def.Id);
            c.Set("clue.known", clue.KnownParent ?? def.ParentA);
            c.Set("clue.other", (clue.KnownParent ?? def.ParentA) == def.ParentA ? def.ParentB : def.ParentA);
            c.Set("mixId", def.MixId);
            c.Set("rawPart", raw.PartId);
            return StepOutcome.Done($"线索“{FusionService.ClueText(s, clue)}”（{(clue.Full ? "完整" : "部分")}线索，来自这次远征，“{FgjM2Common.ConductName}” {clue.Count} 次）指向配方“{GameLogic.Localization.GameText.Get(def.NameKey)}”；" +
                                    $"通知“新的熔合线索”；带回的协议数据盒变成一枚未破解的“{M.FwName(fw)}”芯片（{raw.PartId}，▲未破解）");
        }
    }
}
