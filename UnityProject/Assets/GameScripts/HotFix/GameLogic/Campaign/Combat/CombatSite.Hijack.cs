using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// EMG4-HIJ-01（FG19 FGR-EMG-162 / 163，ADR-EMG-001 ⑰）：信号劫持的热更层一侧——只订阅内核玩法事件（O(1) / 事件），不另做拦截，阵亡仍只有内核一个统一入口。
    /// - Hijacked：敌人记录不算阵亡（不翻 IsAlive、不结算掉落 / 击杀）；攻城 / 突袭队伍归属已由内核清掉（SiegeService 按内核计数对账，剩余数减一）；发通知与引导钩子 emg_first_hijack。
    /// - HijackEnded：紧接着的 Killed（击杀者 = 劫持者）照常结算掉落与击杀——只结算一次。
    /// - HijackRefused：上限满 / 入侵抗性（精英、首领默认）写原因通知，单位照常阵亡。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>自检读数：本进程收到的劫持开始 / 结束 / 被拒次数（按原因）。</summary>
        public static int HijackStarted, HijackEndedCount, HijackRefusedCap, HijackRefusedResist;

        /// <summary>已发过“入侵抗性”拒绝通知的单位（内核每次 hij.hack 命中精英 / 首领都发一次 HijackRefused，这里每台只通知一次；O(1) / 事件）。</summary>
        private readonly System.Collections.Generic.HashSet<int> _hackResistNoted = new System.Collections.Generic.HashSet<int>();

        private void HandleHijackEvent(CampaignState state, in CombatEvent e)
        {
            RegionEnemyRecord rec = FindEnemyRecord(state, e.Unit);
            string name = rec != null ? EnemyDefs.NameOf(rec.EnemyTypeId) : GameText.Get("emg.hijack.unit_unknown");
            var at = new Vector3((float)e.Pos.x, 0f, (float)e.Pos.y);
            switch (e.Kind)
            {
                case CombatEventKind.Hijacked:
                    HijackStarted++;
                    if (rec != null)
                    {
                        rec.Position = new Vector2((float)e.Pos.x, (float)e.Pos.y); // 记录照旧存活（被劫持不算阵亡）
                        rec.Health = e.Value2; // 致命一击的 Damaged 已把记录写成 0：同步成重启后的耐久
                        Progression.MechanicCodex.OnEnemyEncountered(rec.EnemyTypeId);
                    }
                    GuidanceHooks.Raise(GuidanceHooks.EmgFirstHijack);
                    Notifications.NotificationCenter.Post("emg_hijacked",
                        GameText.Format("emg.hijack.notify.started", name, Mathf.RoundToInt(e.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)), at);
                    return;
                case CombatEventKind.HijackEnded:
                    HijackEndedCount++;
                    return;
                case CombatEventKind.HijackRefused:
                    if (e.Code == EmgHijackConst.RefuseCap)
                    {
                        HijackRefusedCap++;
                        Notifications.NotificationCenter.Post("emg_hijack_full", GameText.Format("emg.hijack.notify.full", name,
                            Mathf.RoundToInt(e.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Mathf.RoundToInt(e.Value2).ToString(System.Globalization.CultureInfo.InvariantCulture)), at);
                    }
                    else if (_hackResistNoted.Add(e.Unit))
                    {
                        HijackRefusedResist++;
                        Notifications.NotificationCenter.Post("emg_hijack_resist", GameText.Format("emg.hijack.notify.resist", name), at);
                    }
                    return;
            }
        }

        /// <summary>悬停：被劫持单位的状态行（“信号劫持中：剩余 N 秒，由教义触发：跟随劫持者、攻击最近敌人；不接受编队命令、不能接入”）。不是被劫持单位返回 null。</summary>
        public string HijackHoverLine(int unitId)
        {
            if (IsDisposed || !Kernel.TryGetHijack(unitId, out _, out float remaining, out int held))
            {
                return null;
            }
            return GameText.Format("emg.hijack.hover", Mathf.CeilToInt(remaining).ToString(System.Globalization.CultureInfo.InvariantCulture),
                held.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\n" + GameText.Get("emg.hijack.hover.doctrine");
        }

        /// <summary>悬停：敌方单位的程序（FGR-EMG-162“击杀悬停显示敌方单位的程序（读写端口）”）。不是敌方单位 / 没有声明返回 null。</summary>
        public string EnemyProgramLine(CampaignState state, int unitId)
        {
            if (IsDisposed || !TryGetEnemyOfUnit(unitId, out _))
            {
                return null;
            }
            RegionEnemyRecord rec = FindEnemyRecord(state, unitId);
            return rec != null && EnemyDefs.TryGet(rec.EnemyTypeId, out GameConfig.fg.MechEnemy row) ? EnemyProgramText(row) : null;
        }

        /// <summary>
        /// 敌方单位的程序文本（悬停与图鉴同一份）：组装武器里每枚固件一行“固件名：端口：读 …；写 …”（与玩家固件详情同一套端口行），
        /// 敌方单位行自己的声明（ownerKind = enemy，如澄净排放）另起一行。按端口声明，不按单位 ID（FGR-EMG-013）。都没有返回 null。
        /// </summary>
        public static string EnemyProgramText(GameConfig.fg.MechEnemy row)
        {
            if (row == null)
            {
                return null;
            }
            var sb = new System.Text.StringBuilder();
            string sep = GameText.Get("emg.enemy.program.sep");
            if (EnemyDefs.HasTag(row, EnemyDefs.TagAssembled) && EnemySpawnBuilder.TryAssembly(row, out _, out System.Collections.Generic.List<string> firmware))
            {
                foreach (string fw in firmware)
                {
                    string ports = Emergence.EmgProgramTreeText.PortsLine(fw);
                    if (ports != null)
                    {
                        sb.Append(sb.Length > 0 ? "\n" : string.Empty).Append(Emergence.EmgProgramTreeText.FirmwareName(fw)).Append(sep).Append(ports);
                    }
                }
            }
            string own = Emergence.EmgProgramTreeText.PortsLine(row.Id);
            if (own != null)
            {
                sb.Append(sb.Length > 0 ? "\n" : string.Empty).Append(GameText.Get("emg.enemy.program.own")).Append(sep).Append(own);
            }
            return sb.Length == 0 ? null : GameText.Get("emg.enemy.program.title") + "\n" + sb;
        }
    }
}
