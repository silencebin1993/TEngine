using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Campaign.WorldSim;
using UnityEngine;

namespace GameLogic.UI.SilentMind
{
    /// <summary>
    /// FG11-BOSS-01（FGR-FAC-006“血条上标出阶段分界”“阶段转换有明确的文字提示”；卡片“被封锁槽位在 HUD 上的显示”“倒计时的显示”）：余烬主机的首领 HUD 内容。
    /// 界面复用首领 HUD 同一套结构（SilentMindHud.uxml），由 <see cref="SilentMindHudUIToolkit"/> 在寂听主脑 / 洗礼塔都不显示时显示这一份；
    /// 本类只读状态机 <see cref="EmberHostBoss"/> 与槽位封锁 <see cref="SignalSeizeService"/>，不做业务判断（纯函数，自检直接调）。只在镜头看着数据中心、首领战进行中时显示。
    /// </summary>
    public static class EmberHostHud
    {
        public static bool Compose(CampaignState s, out string title, out float hpPct, out float markPct, out string hp, out string lines)
        {
            title = hp = lines = string.Empty;
            hpPct = 0f;
            markPct = 0f;
            if (s == null || !WorldView.IsObserved(EmberHostBoss.SiteId) || !EmberHostBoss.IsEngaged(s))
            {
                return false;
            }
            EmberBossRecord r = EmberHostBoss.Record(s);
            EmberPhase p = EmberHostBoss.PhaseOf(s);
            title = GameText.Get("boss.ember.hud.title") + " · " + EmberHostBoss.PhaseName(p);
            markPct = p == EmberPhase.Upclock ? EmberHostBoss.Phase2HpFraction : 0f;
            RegionEnemyRecord core = EmberHostBoss.Core(s);
            if (core != null && core.MaxHealth > 0f)
            {
                hpPct = Mathf.Clamp01(core.Health / core.MaxHealth);
                hp = GameText.Format("boss.ember.hud.hp", Mathf.CeilToInt(core.Health).ToString(), Mathf.CeilToInt(core.MaxHealth).ToString(),
                    Mathf.RoundToInt(EmberHostBoss.Phase2HpFraction * 100f).ToString());
            }
            long now = GameClock.Ticks;
            var sb = new StringBuilder();
            if (p == EmberPhase.Meltdown)
            {
                float left = Mathf.Max(0f, (r.MeltdownEndTick - now) / (float)Mathf.Max(1, GameClock.StepHz));
                sb.AppendLine(GameText.Format("boss.ember.hud.countdown", Mathf.CeilToInt(left).ToString()));
            }
            if (p == EmberPhase.Lockout)
            {
                sb.AppendLine(GameText.Format("boss.ember.hud.spikes", EmberHostBoss.SpikesAlive(s).ToString(), EmberHostBoss.Spikes.Count.ToString()));
                sb.AppendLine(GameText.Get("boss.ember.invulnerable.lockout"));
            }
            if (r.BlockedSlot >= 0 && SignalSeizeService.IsBlocked(s, r.BlockedSlot))
            {
                // 被封锁的槽（来源标注，FGR-BASE-020）：与信号核面板 / 状态行同一条原因文本。
                sb.AppendLine(GameText.Format("emg.seize.hud.slot", (r.BlockedSlot + 1).ToString(), GameText.Get("emg.seize.source.ember")));
                sb.AppendLine(SignalSeizeService.BlockedReason(s, r.BlockedSlot));
            }
            int vent = EmberHostBoss.VentStateAt(r, now);
            string ventLeft = EmberHostBoss.VentSecondsLeft(r, now).ToString("F0");
            if (p != EmberPhase.Lockout)
            {
                sb.AppendLine(vent == 2 ? GameText.Format("boss.ember.hud.vent_open", ventLeft, EmberHostBoss.VentDamageMult.ToString("0.##"))
                    : vent == 1 ? GameText.Format("boss.ember.hud.vent_warn", ventLeft)
                    : GameText.Format("boss.ember.hud.vent_closed", ventLeft, EmberHostBoss.SealedDamageMult.ToString("0.##")));
            }
            float cap = EmberHostBoss.HeatCapMult(r);
            if (cap < 1f)
            {
                sb.AppendLine(GameText.Format("boss.ember.hud.heat", Mathf.RoundToInt(cap * 100f).ToString()));
            }
            if (p == EmberPhase.Burnout)
            {
                sb.AppendLine(GameText.Get("boss.ember.hud.burnout"));
            }
            if (r.LastReachedPhase > 0)
            {
                sb.AppendLine(GameText.Format("boss.ember.hud.last_phase", EmberHostBoss.PhaseName((EmberPhase)r.LastReachedPhase)));
            }
            lines = sb.ToString().TrimEnd();
            return true;
        }
    }
}
