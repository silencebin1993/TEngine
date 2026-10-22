using System.Collections.Generic;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Campaign.WorldSim;
using UnityEngine;

namespace GameLogic.UI.SilentMind
{
    /// <summary>
    /// FG10-BOSS-01（FGR-FAC-006“血条上标出阶段分界”“阶段转换有明确的文字提示”、FG09 第 9 节“首领血条显示名称和阶段”“重试前显示上次止步”）：
    /// 洗礼塔的首领 HUD 内容。界面复用首领 HUD 同一套结构（SilentMindHud.uxml：标题 + 阶段血条 + 说明行），由 <see cref="SilentMindHudUIToolkit"/> 在
    /// 寂听主脑不显示时改显示这一份；本类只读首领状态机 <see cref="BaptismTowerBoss"/>，不做业务判断（纯函数，自检直接调）。
    /// 只在镜头看着星球表面、首领战进行中时显示。血条上的分界线标下一个阶段门槛（灌注 → 阶段二，反应 → 阶段三）。
    /// </summary>
    public static class BaptismTowerHud
    {
        public static bool Compose(CampaignState s, out string title, out float hpPct, out float markPct, out string hp, out string lines)
        {
            title = hp = lines = string.Empty;
            hpPct = 0f;
            markPct = 0f;
            if (s == null || !WorldView.IsObserved(HomeValleyLayout.RegionId) || !BaptismTowerBoss.IsEngaged(s))
            {
                return false;
            }
            ClarityBossRecord r = BaptismTowerBoss.Record(s);
            BaptismPhase p = BaptismTowerBoss.PhaseOf(s);
            title = GameText.Get("boss.clarity.hud.title") + " · " + BaptismTowerBoss.PhaseName(p);
            markPct = p == BaptismPhase.Fill ? BaptismTowerBoss.Phase2HpFraction : p == BaptismPhase.React ? BaptismTowerBoss.Phase3HpFraction : 0f;
            RegionEnemyRecord tower = BaptismTowerBoss.Tower(s);
            if (tower != null && tower.MaxHealth > 0f)
            {
                hpPct = Mathf.Clamp01(tower.Health / tower.MaxHealth);
                hp = GameText.Format("boss.clarity.hud.hp", Mathf.CeilToInt(tower.Health).ToString(), Mathf.CeilToInt(tower.MaxHealth).ToString(),
                    Mathf.RoundToInt(BaptismTowerBoss.Phase2HpFraction * 100f).ToString(), Mathf.RoundToInt(BaptismTowerBoss.Phase3HpFraction * 100f).ToString());
            }
            var sb = new StringBuilder();
            var fluids = new List<string>(BaptismTowerBoss.PumpCount);
            for (int i = 0; i < BaptismTowerBoss.PumpCount; i++)
            {
                if (BaptismTowerBoss.Pump(s, i)?.IsAlive == true)
                {
                    string tag = BaptismTowerBoss.FluidOf(s, i);
                    fluids.Add(StatusTagCatalog.NameOf(tag) ?? tag);
                }
            }
            sb.AppendLine(GameText.Format("boss.clarity.hud.pumps", fluids.Count.ToString(), BaptismTowerBoss.PumpCount.ToString(), string.Join("、", fluids)));
            long now = GameClock.Ticks;
            float hz = Mathf.Max(1, GameClock.StepHz);
            sb.AppendLine(GameText.Format("boss.clarity.hud.next_fill", Mathf.Max(0f, (r.NextFillTick - now) / hz).ToString("F0"), r.Active.Length.ToString()));
            if (p == BaptismPhase.React || p == BaptismPhase.Vent)
            {
                if (r.ReactFill >= 0)
                {
                    sb.AppendLine(GameText.Format("boss.clarity.hud.react_warn", Mathf.Max(0f, (r.ReactAtTick - now) / hz).ToString("F1"),
                        BaptismTowerBoss.PendingReactVerb(s)));
                }
                else
                {
                    sb.AppendLine(GameText.Format("boss.clarity.hud.next_react", Mathf.Max(0f, (r.NextReactTick - now) / hz).ToString("F0")));
                }
            }
            if (p == BaptismPhase.Vent)
            {
                bool venting = BaptismTowerBoss.VentStateAt(r, now) == 2;
                string left = BaptismTowerBoss.VentSecondsLeft(r, now).ToString("F0");
                sb.AppendLine(venting
                    ? GameText.Format("boss.clarity.hud.vent", left, BaptismTowerBoss.VentDamageMult.ToString("0.##"))
                    : GameText.Format("boss.clarity.hud.sealed", left, BaptismTowerBoss.SealedDamageMult.ToString("0.##")));
            }
            float leave = BaptismTowerBoss.ArenaLeaveSecondsLeft(s);
            if (leave >= 0f)
            {
                sb.AppendLine(GameText.Format("boss.clarity.hud.arena_leave", leave.ToString("F0")));
            }
            if (r.LastReachedPhase > 0)
            {
                sb.AppendLine(GameText.Format("boss.clarity.hud.last_phase", BaptismTowerBoss.PhaseName((BaptismPhase)r.LastReachedPhase)));
            }
            lines = sb.ToString().TrimEnd();
            return true;
        }
    }
}
