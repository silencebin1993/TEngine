using System;
using System.Collections.Generic;
using System.Globalization;
using GameLogic.Campaign;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.UI.Common;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.UI.SignalCore
{
    /// <summary>
    /// FG1-HUD-01（FG01 FGR-SIG-080；FG13 FGU-33；FG-GAP-045）：接入 HUD（与信号核同一个 UIDocument，SignalCorePanel.uxml 的 UplinkHud 块，
    /// 停靠左下角，避开顶部信号操作条与底部指挥栏）。只在信号在某台机器里时显示。
    /// - 标题：机体编号与名字（“ERC-003 #5 · 重炮机”）；机身状态与来源固件（“机身：形变态（过载）”）；“?”打开图鉴“信号接入”。
    /// - 信号核各槽：固件名 + 状态（生效 / 冷却 N 秒 / 未插入：原因 / 不生效：原因 / 空槽；裸跑再标“裸跑”），悬停写明全部。
    /// - 机体：热量（过热时写恢复线）、电池、耐久与伤势；链路强度（离覆盖边缘越近越弱、边缘 / 中断）；信号暴露。
    /// 数据全部来自 <see cref="UplinkHudModel"/>；刷新：槽位只在 <see cref="UplinkHudModel.SlotsKey"/> 变化时重建（含 1 次装配解析），
    /// 机体每帧 O(1) 取值、按量化键变化才改写文字 / 条宽（B18：与机器总数无关）。
    /// </summary>
    public sealed class UplinkHudView
    {
        private VisualElement _root;
        private Label _title;
        private Label _morph;
        private Button _help;
        private Label _note;
        private VisualElement _slots;
        private Label _heat;
        private VisualElement _heatFill;
        private Label _battery;
        private VisualElement _batteryFill;
        private Label _health;
        private VisualElement _healthFill;
        private Label _link;
        private VisualElement _linkFill;
        private Label _exposure;
        private Label _injury;
        private Label _experience;
        private Label _keys;
        private readonly List<Label> _slotLabels = new List<Label>(5);
        private readonly UplinkHudSnapshot _snap = new UplinkHudSnapshot();
        private int _slotsKey;
        private int _vitalsKey;
        private int _logicId;

        public bool IsBound => _root != null;
        public bool Visible => _root != null && !_root.ClassListContains("uk-hidden");
        public UplinkHudSnapshot Snapshot => _snap;
        public int SlotRebuilds { get; private set; }
        public int VitalRewrites { get; private set; }

        // ── 自检读点 ──
        public string TitleText => _title?.text ?? string.Empty;
        public string MorphText => _morph?.text ?? string.Empty;
        public string NoteText => _note != null && !_note.ClassListContains("uk-hidden") ? _note.text ?? string.Empty : string.Empty;
        public int SlotCount => _slotLabels.FindAll(l => !l.ClassListContains("uk-hidden")).Count;
        public string SlotText(int i) => i >= 0 && i < _slotLabels.Count && !_slotLabels[i].ClassListContains("uk-hidden") ? _slotLabels[i].text : string.Empty;
        public Label SlotLabel(int i) => i >= 0 && i < _slotLabels.Count ? _slotLabels[i] : null;
        public string HeatText => _heat?.text ?? string.Empty;
        public string BatteryText => _battery?.text ?? string.Empty;
        public string HealthText => _health?.text ?? string.Empty;
        public string InjuryText => _injury?.text ?? string.Empty;
        public string LinkText => _link?.text ?? string.Empty;
        public string ExposureText => _exposure?.text ?? string.Empty;
        public string ExperienceText => _experience?.text ?? string.Empty;
        public string KeysText => _keys?.text ?? string.Empty;
        public bool HeatWarn => _heat != null && _heat.ClassListContains("uh-warn");
        public bool LinkWarn => _link != null && _link.ClassListContains("uh-warn");
        public float LinkFillPercent => _linkFill != null ? _linkFill.style.width.value.value : -1f;
        public float HeatFillPercent => _heatFill != null ? _heatFill.style.width.value.value : -1f;
        public Button HelpButton => _help;
        public VisualElement LinkElement => _link;
        public VisualElement HeatElement => _heat;

        public void Bind(VisualElement root)
        {
            _root = root.Q<VisualElement>("UplinkHud");
            if (_root == null)
            {
                return;
            }
            _title = root.Q<Label>("UplinkHudTitle");
            _morph = root.Q<Label>("UplinkHudMorph");
            _help = root.Q<Button>("UplinkHudHelp");
            _note = root.Q<Label>("UplinkHudNote");
            _slots = root.Q<VisualElement>("UplinkHudSlots");
            _heat = root.Q<Label>("UplinkHudHeatText");
            _heatFill = root.Q<VisualElement>("UplinkHudHeatFill");
            _battery = root.Q<Label>("UplinkHudBatteryText");
            _batteryFill = root.Q<VisualElement>("UplinkHudBatteryFill");
            _health = root.Q<Label>("UplinkHudHealthText");
            _healthFill = root.Q<VisualElement>("UplinkHudHealthFill");
            _link = root.Q<Label>("UplinkHudLinkText");
            _linkFill = root.Q<VisualElement>("UplinkHudLinkFill");
            _exposure = root.Q<Label>("UplinkHudExposure");
            _injury = root.Q<Label>("UplinkHudInjury");
            _experience = root.Q<Label>("UplinkHudExperience");
            _keys = root.Q<Label>("UplinkHudKeys");

            _help.text = GameText.Get("uplink.hud.help_button");
            _help.clicked += () => MechanicCodex.Open("codex.signal.uplink");
            UiTooltip.Attach(_help, () => new TooltipContent
            {
                Title = GameText.Get("uplink.hud.help_tip_title"),
                Body = GameText.Get("uplink.hud.help_tip") + "\n" + GameText.Format("codex.help.tip", GameText.Get("codex.signal.uplink.title")),
            });
            UiTooltip.Attach(_morph, () => new TooltipContent
            {
                Title = GameText.Get("codex.firmware.morph.title"),
                Body = GameText.Get("codex.firmware.morph.body"),
            });
            UiTooltip.Attach(_heat, () => new TooltipContent
            {
                Title = _heat.text,
                Body = GameText.Format("uplink.hud.heat_tip", F0(_snap.HeatMax), F0(_snap.HeatRecover)),
            });
            UiTooltip.Attach(_battery, () => new TooltipContent
            {
                Title = _battery.text,
                Body = GameText.Format("uplink.hud.battery_tip", F0(_snap.Battery), F0(_snap.BatteryMax)),
            });
            UiTooltip.Attach(_health, () => new TooltipContent
            {
                Title = _health.text,
                Body = GameText.Get("uplink.hud.health_tip"),
            });
            UiTooltip.Attach(_link, () => new TooltipContent
            {
                Title = _link.text,
                Body = GameText.Format("uplink.hud.link_tip", F0(UplinkHudModel.FullStrengthCells), F0(SignalCoverageService.EdgeWarnCells),
                    SignalLinkService.GraceSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
            });
            UiTooltip.Attach(_exposure, () => new TooltipContent
            {
                Title = _exposure.text,
                Body = GameText.Format("uplink.hud.exposure_tip", F0(_snap.Exposure), F0(CampaignExposureLedger.MaxExposure)),
            });

            _slots.Clear();
            _slotLabels.Clear();
            for (int i = 0; i < SignalCoreService.MaxSlots; i++)
            {
                int index = i;
                var l = new Label { name = "UplinkHudSlot" + i };
                l.AddToClassList("uh-slot");
                l.AddToClassList("uk-hidden");
                // 审查 P1：能悬停看说明，但不吞接入视角的世界点击（见 UiWindowFocus.PointerPassthroughClass）。
                l.AddToClassList(UiWindowFocus.PointerPassthroughClass);
                UiTooltip.Attach(l, () => SlotTooltip(index));
                _slots.Add(l);
                _slotLabels.Add(l);
            }
            _slotsKey = 0;
            _vitalsKey = 0;
            _logicId = 0;
        }

        /// <summary>每帧（由 <see cref="SignalCoreHudUIToolkit.Refresh"/> 调用）。</summary>
        public void Refresh(CampaignState s, bool inWorld)
        {
            if (_root == null)
            {
                return;
            }
            int id = s != null && inWorld ? SignalUplinkService.CurrentMachine(s) : 0;
            bool show = id != 0 && MachineRegistry.TryGetRecord(id, out MachineRecord rec) && rec != null && rec.IsAlive;
            _root.EnableInClassList("uk-hidden", !show);
            if (!show)
            {
                _slotsKey = 0;
                _vitalsKey = 0;
                _logicId = 0;
                return;
            }
            int slotsKey = UplinkHudModel.SlotsKey(s, id);
            if (slotsKey != _slotsKey || id != _logicId)
            {
                _slotsKey = slotsKey;
                _logicId = id;
                _vitalsKey = 0;
                UplinkHudModel.BuildSlots(s, id, _snap);
                RebuildSlots(s);
            }
            UplinkHudModel.BuildVitals(s, id, _snap);
            int vitalsKey = HashCode.Combine(UplinkHudModel.VitalsKey(_snap), SignalUplinkService.CurrentMachine(s), GameSettings.Revision,
                MachineRegistry.TryGetRecord(id, out MachineRecord r2) ? r2.SignalUplinkCount : 0, (int)(MachineSignalExperience.TotalSeconds(s, r2)));
            if (vitalsKey != _vitalsKey)
            {
                _vitalsKey = vitalsKey;
                RewriteVitals(s, r2);
            }
        }

        private void RebuildSlots(CampaignState s)
        {
            SlotRebuilds++;
            _title.text = GameText.Format("uplink.hud.title", _snap.Label, _snap.ChassisName);
            _morph.text = GameText.Format("uplink.hud.morph", UplinkHudModel.Compose(_snap.Morph, _snap.MorphSources));
            string note = !_snap.HasPort
                ? GameText.Get("uplink.hud.no_port")
                : _snap.CoreEmpty ? GameText.Format("uplink.hud.core_empty", InputDisplay.ForAction(GameActionId.OpenSignalCore)) : string.Empty;
            _note.text = note;
            _note.EnableInClassList("uk-hidden", note.Length == 0);
            for (int i = 0; i < _slotLabels.Count; i++)
            {
                Label l = _slotLabels[i];
                bool shown = i < _snap.Slots.Count;
                l.EnableInClassList("uk-hidden", !shown);
                if (!shown)
                {
                    continue;
                }
                UplinkHudSlot slot = _snap.Slots[i];
                string name = slot.FirmwareId.Length > 0 ? (FirmwareKinds.KindLabel(FirmwareKinds.KindOf(slot.FirmwareId)) + " " + (FirmwareKinds.DisplayName(slot.FirmwareId) ?? slot.FirmwareId)) : string.Empty;
                string head = (slot.Index + 1).ToString(CultureInfo.InvariantCulture);
                l.text = name.Length > 0
                    ? GameText.Format("uplink.hud.slot", head, name) + " · " + UplinkHudModel.SlotStateText(slot)
                    : GameText.Format("uplink.hud.slot", head, UplinkHudModel.SlotStateText(slot));
                l.EnableInClassList("uh-slot-active", slot.State == UplinkSlotState.Active);
                l.EnableInClassList("uh-slot-cooling", slot.State == UplinkSlotState.Cooling);
                l.EnableInClassList("uh-slot-off", slot.State == UplinkSlotState.NotInserted || slot.State == UplinkSlotState.Ineffective || slot.State == UplinkSlotState.Offline);
                l.EnableInClassList("uh-slot-empty", slot.State == UplinkSlotState.Empty);
                l.EnableInClassList("uh-slot-raw", slot.Raw);
            }
        }

        private void RewriteVitals(CampaignState s, MachineRecord rec)
        {
            VitalRewrites++;
            _heat.text = _snap.Overheated
                ? GameText.Format("uplink.hud.heat", F0(_snap.Heat), F0(_snap.HeatMax)) + " · " + GameText.Format("uplink.hud.overheated", F0(_snap.HeatRecover))
                : GameText.Format("uplink.hud.heat", F0(_snap.Heat), F0(_snap.HeatMax));
            _heat.EnableInClassList("uh-warn", _snap.Overheated);
            SetFill(_heatFill, _snap.HeatMax > 0f ? _snap.Heat / _snap.HeatMax : 0f);
            float battery01 = _snap.BatteryMax > 0f ? _snap.Battery / _snap.BatteryMax : 0f;
            _battery.text = GameText.Format("uplink.hud.battery", UplinkHudModel.Pct(battery01));
            SetFill(_batteryFill, battery01);
            _health.text = GameText.Format("uplink.hud.health", F0(_snap.Health), F0(_snap.HealthMax));
            SetFill(_healthFill, _snap.HealthMax > 0f ? _snap.Health / _snap.HealthMax : 0f);
            _injury.text = _snap.Injuries.Length == 0
                ? GameText.Get("uplink.hud.injury_none")
                : GameText.Format("uplink.hud.injury", MachineInjury.DescribeAll(_snap.Injuries));
            _link.text = UplinkHudModel.LinkText(_snap);
            bool linkWarn = _snap.LinkLevel == UplinkLinkLevel.Edge || _snap.LinkLevel == UplinkLinkLevel.Lost;
            _link.EnableInClassList("uh-warn", linkWarn);
            _linkFill?.EnableInClassList("uh-fill-warn", linkWarn);
            SetFill(_linkFill, _snap.LinkStrength01);
            _exposure.text = GameText.Format("uplink.hud.exposure", _snap.Exposure.ToString("0.#", CultureInfo.InvariantCulture));
            _experience.text = rec != null ? MachineSignalExperience.Describe(s, rec, "uplink.hud.experience") : string.Empty;
            if (_keys != null)
            {
                // 按键提示（B02：随重绑与语言变化；GameSettings.Revision 在变化键里）。
                _keys.text = GameText.Format("uplink.hud.keys", InputDisplay.ForAction(GameActionId.ToggleCameraView), InputDisplay.ForAction(GameActionId.CycleControlTarget),
                    InputDisplay.ForAction(GameActionId.JumpHome), InputDisplay.ForAction(GameActionId.OpenSignalCore));
            }
        }

        private TooltipContent SlotTooltip(int index)
        {
            if (index < 0 || index >= _snap.Slots.Count)
            {
                return null;
            }
            UplinkHudSlot slot = _snap.Slots[index];
            string name = slot.FirmwareId.Length > 0 ? FirmwareKinds.DisplayName(slot.FirmwareId) ?? slot.FirmwareId : GameText.Get("uplink.hud.state.empty");
            string detail = slot.FirmwareId.Length > 0
                ? FirmwareKinds.KindTip(FirmwareKinds.KindOf(slot.FirmwareId)) + SignalCoreHudUIToolkit.RawTip(CampaignSession.Current, slot.FirmwareId)
                : GameText.Get("signal.core.hint");
            return new TooltipContent
            {
                Title = GameText.Format("uplink.hud.slot_tip", (slot.Index + 1).ToString(CultureInfo.InvariantCulture), name),
                Body = UplinkHudModel.SlotStateText(slot) + "\n" + detail,
                CodexEntryId = GameLogic.Progression.MechanicCodex.FirmwareEntryId(slot.FirmwareId), // FG2-FW-05：悬停按图鉴键跳到固件条目
            };
        }

        private static void SetFill(VisualElement fill, float v01)
        {
            if (fill != null)
            {
                fill.style.width = Length.Percent(Mathf.Clamp01(v01) * 100f); // 数据驱动的条宽（UI Toolkit 红线 2 允许）。
            }
        }

        private static string F0(float v) => v.ToString("0", CultureInfo.InvariantCulture);
    }
}
