using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG11-FAC-01（FG09 镜像体；FG19 FGR-EMG-164；DEBT-EMG4HIJ02-02）：镜像体侦测“玩家在它附近最近一次发动的核心固件”并复制成本地版本。
    /// - 触发点 = 核心固件发动（与核心冷却 / 暴露同一时刻：玩法事件 ReactionFired 的门控发动，或声明轨 <see cref="OnDeclCoreReaction"/>），不逐帧扫描。
    /// - 镜像体按读法 <see cref="EnemyDefs.BehaviorMirror"/> 识别（不认敌人 ID）；名单在敌人进出 / 读档时标脏、下一次发动时重建（O(敌人)，只在名单变化后的第一次发动），
    ///   没有镜像体 O(1) 早退出；每次发动 O(镜像体数)。距离 = 敌人行 effectRange；同一枚不重复复制，两次复制间隔 overclock.mirror.copy_cooldown 游戏秒。
    /// - 复制本身走 <see cref="MirrorCopy"/>（1 代程序、C(1)、占镜像体自己的刷写带宽、复制品不能再被复制），复制到的固件 / 条数 / 时刻写进敌人记录（随存档）。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>自检读数：镜像体按侦测真正复制成功的次数。</summary>
        public static int MirrorDetectCopies;

        private readonly List<int> _mirrorUnits = new List<int>();
        private bool _mirrorDirty = true;

        private void MarkMirrorsDirty() => _mirrorDirty = true;

        private void RebuildMirrors(CampaignState state)
        {
            _mirrorUnits.Clear();
            foreach (KeyValuePair<int, string> kv in _unitEnemy)
            {
                RegionEnemyRecord rec = FindEnemyRecord(state, kv.Key);
                if (rec != null && EnemyDefs.TryGet(rec.EnemyTypeId, out GameConfig.fg.MechEnemy row) && row.Behavior == EnemyDefs.BehaviorMirror)
                {
                    _mirrorUnits.Add(kv.Key);
                }
            }
            _mirrorUnits.Sort(); // 确定顺序（字典遍历顺序不作依据）
            _mirrorDirty = false;
        }

        /// <summary>玩家单位 <paramref name="sourceUnit"/> 发动了一次核心固件（反应 <paramref name="reactionId"/> 由它触发）：范围内的镜像体复制它。</summary>
        private void MirrorOnCoreFired(CampaignState state, int sourceUnit, string reactionId)
        {
            if (IsDisposed || state == null)
            {
                return;
            }
            if (_mirrorDirty)
            {
                RebuildMirrors(state);
            }
            if (_mirrorUnits.Count == 0)
            {
                return;
            }
            string fw = MechanicalReactionCatalog.TriggerFirmwareOf(reactionId);
            if (fw != null)
            {
                MirrorDetect(state, sourceUnit, fw, new[] { fw });
            }
        }

        /// <summary>
        /// 玩家单位 <paramref name="sourceUnit"/> 发动了核心固件 <paramref name="fw"/>：范围内的镜像体把 <paramref name="copyFirmware"/> 编成复制品（正式路径 = 那一枚；
        /// 自检用带触发器 / 混合固件的夹具走同一条侦测、通知、悬停路径）。
        /// </summary>
        public void MirrorDetect(CampaignState state, int sourceUnit, string fw, IReadOnlyList<string> copyFirmware)
        {
            if (IsDisposed || state == null)
            {
                return;
            }
            if (_mirrorDirty)
            {
                RebuildMirrors(state);
            }
            if (_mirrorUnits.Count == 0)
            {
                return;
            }
            // 审查修复：只复制“玩家”发动的核心固件（FGR-EMG-164）——镜像体自己用复制品打出的核心反应不触发别的镜像体反复尝试
            if (!IsPlayerUnit(sourceUnit) || !Kernel.TryGetPosition(sourceUnit, out double2 src))
            {
                return;
            }
            float cooldown = GridContent.TryGetTuning("overclock.mirror.copy_cooldown", out float cd) && cd > 0f ? cd : 4f;
            float now = (float)Kernel.Time;
            string fwName = FirmwareKinds.DisplayName(fw) ?? fw;
            foreach (int m in _mirrorUnits)
            {
                if (!Kernel.IsAlive(m) || !Kernel.TryGetPosition(m, out double2 mp))
                {
                    continue;
                }
                RegionEnemyRecord rec = FindEnemyRecord(state, m);
                if (rec == null || !EnemyDefs.TryGet(rec.EnemyTypeId, out GameConfig.fg.MechEnemy row))
                {
                    continue;
                }
                float range = row.EffectRange > 0f ? row.EffectRange : 14f;
                bool copied = !string.IsNullOrEmpty(rec.MirrorCopyFirmware);
                // “同一枚不重复”只在手上真有那份复制品时成立（从记录重新生成、没拿着复制品的镜像体可以再复制）
                if (math.distance(src, mp) > range || (rec.MirrorCopyFirmware == fw && UnitHoldsCopy(m))
                    || (copied && now >= rec.MirrorCopyAt && now - rec.MirrorCopyAt < cooldown))
                {
                    continue;
                }
                string component = EnemySpawnBuilder.TryAssembly(row, out string comp, out _) && !string.IsNullOrEmpty(comp) ? comp : "comp_gun";
                MirrorCopyResult res = MirrorCopy(m, component, copyFirmware, sourceUnit, state);
                var at = new Vector3((float)mp.x, 0f, (float)mp.y);
                if (!res.Ok)
                {
                    Notifications.NotificationCenter.Post("overclock_mirror", GameText.Get(res.RefuseKey ?? "emg.mirror.refuse_no_unit"), at);
                    continue;
                }
                rec.MirrorCopyFirmware = fw;
                rec.MirrorCopyHeld = res.Held;
                rec.MirrorCopyAt = now;
                rec.MirrorCopyDropped = res.Dropped;
                rec.MirrorCopyOverflow = res.Overflowed;
                rec.MirrorCopyGenFull = res.GenFull;
                MirrorDetectCopies++;
                GuidanceHooks.Raise("overclock.first_mirror");
                Notifications.NotificationCenter.Post("overclock_mirror",
                    GameText.Format("overclock.mirror.copied", fwName, res.Held.ToString(CultureInfo.InvariantCulture)), at);
                string cut = MirrorCutText(res.Dropped, res.Overflowed, res.GenFull);
                if (cut != null)
                {
                    Notifications.NotificationCenter.Post("overclock_mirror", cut, at); // 卡片负向：复制品少了一截时原因可见（带宽 / 容量 C(1) / 代际已满）
                }
            }
        }

        /// <summary>复制品被截断的原因（带宽截断 / 容量 C(1) 溢出 / 触发器子载荷代际已满），没有截断返回 null。通知与悬停共用。</summary>
        public static string MirrorCutText(int dropped, int overflowed, int genFull)
        {
            var parts = new List<string>(3);
            if (overflowed > 0)
            {
                parts.Add(GameText.Format("overclock.mirror.capacity", overflowed.ToString(CultureInfo.InvariantCulture)));
            }
            if (genFull > 0)
            {
                parts.Add(GameText.Format("overclock.mirror.gen_full", genFull.ToString(CultureInfo.InvariantCulture)));
            }
            if (dropped > 0)
            {
                parts.Add(GameText.Format("overclock.mirror.truncated", dropped.ToString(CultureInfo.InvariantCulture)));
            }
            return parts.Count > 0 ? string.Join(GameText.Get("logistics.load.reason.separator"), parts) : null;
        }

        /// <summary>悬停（FGR-EMG-162 同一面板）：镜像体手上的复制品“镜像复制品：X（N 条）”/“还没有复制到你的核心固件”。不是镜像体返回 null。O(1)。</summary>
        public string MirrorHoverLine(CampaignState state, int unitId)
        {
            if (IsDisposed || state == null || !_unitEnemy.ContainsKey(unitId))
            {
                return null;
            }
            RegionEnemyRecord rec = FindEnemyRecord(state, unitId);
            if (rec == null || !EnemyDefs.TryGet(rec.EnemyTypeId, out GameConfig.fg.MechEnemy row) || row.Behavior != EnemyDefs.BehaviorMirror)
            {
                return null;
            }
            if (string.IsNullOrEmpty(rec.MirrorCopyFirmware) || !UnitHoldsCopy(unitId))
            {
                return GameText.Get("overclock.mirror.hover_none");
            }
            string line = GameText.Format("overclock.mirror.hover", FirmwareKinds.DisplayName(rec.MirrorCopyFirmware) ?? rec.MirrorCopyFirmware,
                rec.MirrorCopyHeld.ToString(CultureInfo.InvariantCulture));
            string cut = MirrorCutText(rec.MirrorCopyDropped, rec.MirrorCopyOverflow, rec.MirrorCopyGenFull);
            return cut != null ? line + GameText.Get("logistics.load.reason.separator") + cut : line;
        }
    }
}
