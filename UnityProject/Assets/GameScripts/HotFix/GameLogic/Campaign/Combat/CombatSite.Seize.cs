using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>镜像复制的结果（FG19 FGR-EMG-164）：成功时 <see cref="Weapon"/> 是镜像体换上的武器；被拒时 <see cref="RefuseKey"/> 是原因文本键。</summary>
    public sealed class MirrorCopyResult
    {
        public bool Ok;
        public string RefuseKey;
        /// <summary>复制品实际持有的指令条数（= 占镜像体自己的刷写带宽）与因带宽不够从末尾截掉的条数。</summary>
        public int Held;
        public int Dropped;
        public int Weapon = -1;
        public Emergence.EmgProgramResult Program;
    }

    /// <summary>
    /// EMG4-HIJ-02（FG19 FGR-EMG-164 / 165；FG10 FGR-EVT-021～022；FG11 FGR-END-022 阶段三）：夺控与镜像复制的热更层一侧。
    /// - 夺控（敌方 → 己方机器）与信号劫持同一张内核表、同一个行为种类：本类只订阅内核玩法事件（Seized / SeizeDowned / SeizeEnded，O(1) / 事件）发通知、同步记录。
    /// - 外部血量机器的致死伤害请求在这里拦成瘫痪（<see cref="TryDownSeized"/>），不走 MachineRegistry.MarkDeadByLogicId（DEBT-EMG1FW02-03）。
    /// - 镜像复制（<see cref="MirrorCopy"/>）：1 代程序、容量 C(1)、占镜像体自己的刷写带宽、复制品不能再被复制；具体单位由 FG11-FAC-01 复用。
    /// </summary>
    public sealed partial class CombatSite
    {
        /// <summary>自检读数：本进程收到的夺控开始 / 瘫痪 / 归还次数，镜像复制成功 / 被拒次数。</summary>
        public static int SeizeStarted, SeizeDownedCount, SeizeEndedCount, MirrorCopied, MirrorRefused;

        /// <summary>自检读数：最近一条夺控结束通知的正文（同类通知会聚合成“机器已归还（N 次）”，自检按它核对写的是哪一种结束）。</summary>
        public static string LastSeizeEndText;

        /// <summary>镜像体第一次复制前的武器（换复制品时从它出发，旧复制品不叠加）。按单位 ID，地点生命周期内有效。</summary>
        private readonly Dictionary<int, CombatWeapon> _mirrorBase = new Dictionary<int, CombatWeapon>();

        private void HandleSeizeEvent(in CombatEvent e)
        {
            if (!_unitMachine.TryGetValue(e.Unit, out int logicId))
            {
                return;
            }
            MachineRegistry.TryGetRecord(logicId, out MachineRecord rec);
            string label = SignalPresence.MachineLabel(logicId);
            var at = new Vector3((float)e.Pos.x, 0f, (float)e.Pos.y);
            SignalSeizeService.Touch();
            switch (e.Kind)
            {
                case CombatEventKind.Seized:
                    SeizeStarted++;
                    GuidanceHooks.Raise(GuidanceHooks.EmgFirstSeize);
                    string secs = e.Value > 0f ? GameText.Format("emg.seize.until_seconds", Mathf.CeilToInt(e.Value).ToString(CultureInfo.InvariantCulture)) : GameText.Get("emg.seize.until_release");
                    Notifications.NotificationCenter.Post("emg_seized", GameText.Format("emg.seize.notify.started", label, SignalSeizeService.SourceName(e.Code), secs), at);
                    return;
                case CombatEventKind.SeizeDowned:
                    SeizeDownedCount++;
                    if (rec != null)
                    {
                        rec.Health = 0f; // 瘫痪：不阵亡（IsAlive 不翻），夺控结束时恢复
                    }
                    Notifications.NotificationCenter.Post("emg_seize_downed", GameText.Format("emg.seize.notify.downed", label), at);
                    return;
                case CombatEventKind.SeizeEnded:
                    SeizeEndedCount++;
                    if (rec != null)
                    {
                        rec.Health = Mathf.Min(rec.MaxHealth > 0f ? rec.MaxHealth : e.Value2, e.Value2);
                    }
                    string key = e.Code == EmgHijackConst.EndReclaimed ? "emg.seize.notify.reclaimed" : e.Code2 != 0 ? "emg.seize.notify.recovered" : "emg.seize.notify.ended";
                    LastSeizeEndText = GameText.Format(key, label);
                    Notifications.NotificationCenter.Post("emg_seize_ended", LastSeizeEndText, at);
                    return;
            }
        }

        /// <summary>夺控中的外部血量机器受到致死伤害：记录血量写 0、内核瘫痪（幂等）。不是夺控中的机器返回 false（调用方照常结算伤害 / 阵亡）。</summary>
        private bool TryDownSeized(MachineRecord rec, int unit, int attacker)
        {
            if (rec == null || !Kernel.TryGetSeize(unit, out _, out _, out _, out _))
            {
                return false;
            }
            rec.Health = 0f;
            Kernel.DownSeized(unit, attacker);
            return true;
        }

        // ── 夺控入口（SignalSeizeService 是唯一调用方）──

        /// <summary><paramref name="seizerEnemyId"/>：夺控者（例如根权限压制的中继）的敌人实例 ID——它被击毁即立即归还；给了却不在本地点时拒绝（NoUnit），不静默变成无来源。</summary>
        public EmgSeizeResult SeizeMachine(int logicId, float seconds, int source, string seizerEnemyId = null)
        {
            int seizer = 0;
            if (IsDisposed || !_machineUnit.TryGetValue(logicId, out int unit) || (seizerEnemyId != null && !_enemyUnit.TryGetValue(seizerEnemyId, out seizer)))
            {
                return EmgSeizeResult.NoUnit;
            }
            return Kernel.Seize(unit, seconds, seizer, (ushort)Math.Max(0, source));
        }

        public bool TryGetSeizedMachine(int logicId, out float remaining, out bool downed, out float reclaimLeft, out int source)
        {
            remaining = reclaimLeft = 0f;
            downed = false;
            source = 0;
            if (IsDisposed || !_machineUnit.TryGetValue(logicId, out int unit) || !Kernel.TryGetSeize(unit, out remaining, out downed, out reclaimLeft, out ushort src))
            {
                return false;
            }
            source = src;
            return true;
        }

        /// <summary>
        /// 右键攻击 / 直控瞄准认被夺控的机器（FGR-EVT-022 应对“把它击倒”）：它在内核里是敌对阵营但不在敌人表里，敌人 ID 写成这个前缀 + 机器 LogicId
        /// （同家园的 unit: 前缀做法，只在本次命令里用，不进存档）。<see cref="TryGetEnemyUnit"/> 只在它仍被夺控、没有瘫痪时解析得出。
        /// </summary>
        public const string SeizedHostilePrefix = "seized:";

        public static bool IsSeizedHostileId(string id) => id != null && id.StartsWith(SeizedHostilePrefix, StringComparison.Ordinal);

        private bool TrySeizedHostileId(int unit, out string id)
        {
            id = _unitMachine.TryGetValue(unit, out int logicId) && Kernel.TryGetSeize(unit, out _, out bool downed, out _, out _) && !downed
                ? SeizedHostilePrefix + logicId.ToString(CultureInfo.InvariantCulture) : null;
            return id != null;
        }

        private bool TryGetSeizedHostileUnit(string id, out int unit)
        {
            unit = 0;
            if (IsDisposed || !IsSeizedHostileId(id) || !int.TryParse(id.Substring(SeizedHostilePrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int logicId)
                || !_machineUnit.TryGetValue(logicId, out int u) || !Kernel.TryGetSeize(u, out _, out bool downed, out _, out _) || downed)
            {
                return false;
            }
            unit = u;
            return true;
        }

        public bool BeginReclaimMachine(int logicId, float seconds) => !IsDisposed && _machineUnit.TryGetValue(logicId, out int unit) && Kernel.BeginReclaim(unit, seconds);

        public bool ReleaseSeizedMachine(int logicId) => !IsDisposed && _machineUnit.TryGetValue(logicId, out int unit) && Kernel.ReleaseSeize(unit);

        /// <summary>悬停：被夺控机器的状态行（剩余 / 瘫痪 / 反向接入进度 + “由敌方信号驱动：攻击你的单位；不接受编队命令；可反向接入夺回”）。不是被夺控单位返回 null。</summary>
        public string SeizeHoverLine(int unitId)
        {
            if (IsDisposed || !Kernel.TryGetSeize(unitId, out float remaining, out bool downed, out float reclaim, out ushort source))
            {
                return null;
            }
            return StatusText(remaining, downed, reclaim, source) + "\n" + GameText.Get(downed ? "emg.seize.hover.downed" : "emg.seize.hover.doctrine");
        }

        /// <summary>HUD（SignalSeizeService.HudLine）：本地点每台被夺控机器追加一段“机器：状态”。桥接层之外不碰内核（FG14 §5 硬约束 3）。O(本地点夺控记录数)。</summary>
        public void AppendSeizeHud(StringBuilder sb, string sep)
        {
            for (int i = 0; !IsDisposed && i < Kernel.SeizeCount; i++)
            {
                int unit = Kernel.SeizedUnitAt(i);
                if (_unitMachine.TryGetValue(unit, out int logicId) && Kernel.TryGetSeize(unit, out float remaining, out bool downed, out float reclaim, out ushort source))
                {
                    sb.Append(sb.Length > 0 ? sep : string.Empty).Append(GameText.Format("emg.seize.hud.machine", SignalPresence.MachineLabel(logicId), StatusText(remaining, downed, reclaim, source)));
                }
            }
        }

        /// <summary>HUD 变化键（SignalSeizeService.HudKey）：把本地点每条夺控记录的整秒读数并进 <paramref name="key"/>。O(本地点夺控记录数)。</summary>
        public int SeizeHudKey(int key)
        {
            for (int i = 0; !IsDisposed && i < Kernel.SeizeCount; i++)
            {
                if (Kernel.TryGetSeize(Kernel.SeizedUnitAt(i), out float remaining, out bool downed, out float reclaim, out _))
                {
                    key = HashCode.Combine(key, Mathf.CeilToInt(remaining), downed, Mathf.CeilToInt(reclaim));
                }
            }
            return key;
        }

        /// <summary>“被夺控（信号幽灵）：剩余 32 秒”/“……：瘫痪，夺控结束后恢复”/“……：反向接入中，2 秒后夺回”。</summary>
        public static string StatusText(float remaining, bool downed, float reclaim, int source)
        {
            string state = downed ? GameText.Get("emg.seize.state.downed")
                : reclaim > 0f ? GameText.Format("emg.seize.state.reclaiming", Mathf.CeilToInt(reclaim).ToString(CultureInfo.InvariantCulture))
                : remaining >= 0f ? GameText.Format("emg.seize.until_seconds", Mathf.CeilToInt(remaining).ToString(CultureInfo.InvariantCulture))
                : GameText.Get("emg.seize.until_release");
            return GameText.Format("emg.seize.hover", SignalSeizeService.SourceName(source), state);
        }

        // ── 镜像复制（FGR-EMG-164）──

        /// <summary>
        /// 镜像体 <paramref name="mirrorUnitId"/> 复制 <paramref name="sourceFirmware"/>（玩家在它附近最近一次发动的核心固件，FG09 镜像体）——按“本地版本”编成 1 代程序（容量 C(1)，混合固件展开后受容量截断，
        /// 触发器的子载荷按容量截断），占镜像体自己的刷写带宽（剩余带宽不够从末尾截断，截到 0 条 = 只有基础攻击），换上带复制品的武器。
        /// <paramref name="sourceUnitId"/> 自己持有的就是复制品时拒绝（复制品不能再被复制）。O(程序长度)，只在复制发生时调用。
        /// </summary>
        public MirrorCopyResult MirrorCopy(int mirrorUnitId, string component, IReadOnlyList<string> sourceFirmware, int sourceUnitId = 0)
        {
            var res = new MirrorCopyResult();
            if (IsDisposed || !Kernel.TryGetUnitWeapon(mirrorUnitId, out CombatWeapon current))
            {
                res.RefuseKey = "emg.mirror.refuse_no_unit";
                MirrorRefused++;
                return res;
            }
            BlueprintCircuitPreview p = EnemyPreview(component, sourceFirmware);
            Emergence.EmgProgramResult program = Emergence.EmgProgramCompiler.CompileCopy(p, sourceUnitId != 0 && UnitHoldsCopy(sourceUnitId), out string refuse);
            if (program == null)
            {
                res.RefuseKey = refuse;
                MirrorRefused++;
                return res;
            }
            if (!_mirrorBase.TryGetValue(mirrorUnitId, out CombatWeapon baseWeapon))
            {
                baseWeapon = current;
                _mirrorBase[mirrorUnitId] = current;
            }
            int baseIndex = WeaponIndex(baseWeapon);
            Kernel.SetUnitWeapon(mirrorUnitId, baseIndex); // 旧复制品先让出带宽，再按剩余带宽截新的
            int full = Emergence.EmgProgramCompiler.FlashSteps(program, false).Count;
            int free = Math.Max(0, Kernel.FlashBandwidth - Kernel.FlashUsed(mirrorUnitId));
            int held = Math.Min(full, free);
            if (held < full)
            {
                program = held > 0 ? Emergence.EmgProgramCompiler.TruncateHeld(program, held) : null;
            }
            res.Held = held;
            res.Dropped = full - held;
            res.Ok = true;
            MirrorCopied++;
            if (program == null || program.IsEmpty)
            {
                res.Weapon = baseIndex;
                return res;
            }
            CombatWeapon w = EnemyProgramWeapon(p, baseWeapon, program, component, "mirror:" + mirrorUnitId.ToString(CultureInfo.InvariantCulture));
            res.Weapon = WeaponIndex(w);
            res.Program = program;
            Kernel.SetUnitWeapon(mirrorUnitId, res.Weapon);
            return res;
        }

        /// <summary>单位此刻用的武器是不是镜像复制品（程序带 IsCopy）。</summary>
        public bool UnitHoldsCopy(int unitId) =>
            !IsDisposed && Kernel.TryGetUnitWeapon(unitId, out CombatWeapon w) && w.Program >= 0 && Kernel.TryGetProgram(w.Program, out EmgProgram prog) && (prog.Flags & EmgProgramFlags.IsCopy) != 0;
    }
}
