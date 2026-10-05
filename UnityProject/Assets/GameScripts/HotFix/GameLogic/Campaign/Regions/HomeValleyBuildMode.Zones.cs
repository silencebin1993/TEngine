using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG6-DEF-03（FG06 FGR-DEF-015“开启自动重建规则的区域”；卡片“自动重建按区域开关”）：建造模式里的重建区域模式。
    /// 常驻规则面板编辑一条自动重建规则时点“在地图上圈一块”进入：按住左键拖框 = 给这条规则圈一个重建区域（至少 rules.zone.min_cells 格），
    /// 点区域里面的一格（不拖）= 打开 / 关掉这个区域；右键或 Esc 退出。区域边框由 <see cref="GameLogic.View.RepairDroneViews"/> 画（开着的粗绿线、关着的细灰线）。
    /// 所有改动都走 <see cref="StandingRuleService"/> 的同一入口（与面板、自检同一路径），改完下一个模拟步补排区域里已经被摧毁的东西。
    /// </summary>
    public sealed partial class HomeValleyBuildMode
    {
        /// <summary>正在给哪条自动重建规则圈区域（0 = 不在重建区域模式）。</summary>
        public int ZoneRuleSerial { get; private set; }

        public bool ZoneMode => ZoneRuleSerial > 0;

        /// <summary>拖框时的框（没在拖时无意义）。</summary>
        public GridCell ZoneBoxMin { get; private set; }
        public GridCell ZoneBoxMax { get; private set; }

        /// <summary>进入（<paramref name="ruleSerial"/> &gt; 0）/ 退出（0）重建区域模式。进入时打开建造模式，退出放置 / 拆除 / 搬迁 / 规划模式。</summary>
        public void SetZoneMode(int ruleSerial)
        {
            if (ruleSerial > 0 && !IsOpen)
            {
                Open();
            }
            if (ruleSerial > 0)
            {
                ExitPlanModes();
                SelectedTypeId = null;
                SelectedToolId = null;
                DemolishMode = false;
                RelocateMode = false;
                PrioritizeMode = false;
                ClearMode = false;
                CarryBuildingId = null;
                Preview = null;
            }
            ZoneRuleSerial = ruleSerial > 0 ? ruleSerial : 0;
            CancelDrag();
            _previewKey = int.MinValue;
            SetStatus(ZoneMode ? GameText.Format("ui.build.zone_mode", GameText.Format("rules.label", ruleSerial)) : string.Empty, false);
        }

        /// <summary>拖框松开：起点 = 终点且落在这条规则的某个区域里 = 开 / 关它；否则圈一个新区域。规则已不在时退出模式并写明。</summary>
        private void CommitZone(CampaignState state, GridCell a, GridCell b)
        {
            StandingRuleRecord r = StandingRuleService.Find(state, ZoneRuleSerial);
            if (r == null || r.Kind != StandingRuleService.KindRebuild)
            {
                ZoneRuleSerial = 0;
                SetStatus(GameText.Get("ui.build.zone_gone"), true);
                return;
            }
            bool ok;
            string message;
            if (a == b && StandingRuleService.ZoneAt(r, a) is RebuildZoneRecord hit)
            {
                ok = StandingRuleService.TryToggleZone(state, r.Serial, hit.Serial, out message);
            }
            else
            {
                ok = StandingRuleService.TryAddZone(state, r.Serial, a, b, out _, out message);
            }
            SetStatus(message, !ok);
            Feedback.FeedbackCues.Raise(ok ? Feedback.FeedbackCueId.CommandAck : Feedback.FeedbackCueId.Denied, message);
        }

        /// <summary>每帧（<see cref="Tick"/> 之外由 HUD 读状态前调也安全）：规则被删 / 换了类型时退出重建区域模式。</summary>
        public void CheckZoneRule(CampaignState state)
        {
            if (!ZoneMode || state == null)
            {
                return;
            }
            StandingRuleRecord r = StandingRuleService.Find(state, ZoneRuleSerial);
            if (r == null || r.Kind != StandingRuleService.KindRebuild)
            {
                ZoneRuleSerial = 0;
                CancelDrag();
                SetStatus(GameText.Get("ui.build.zone_gone"), true);
            }
        }
    }
}
