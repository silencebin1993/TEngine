using System;
using System.Collections.Generic;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>余烬主机的阶段（存档稳定码：只追加，不重排）。</summary>
    public enum EmberPhase
    {
        Dormant = 0,
        /// <summary>阶段一·升频：场地每隔一段时间升温一级，数据中心里全部单位的武器热量上限下降；散热口开合（张开 = 重创窗口）。</summary>
        Upclock = 1,
        /// <summary>阶段二·槽位封锁：封锁信号核的一个槽（FGR-EMG-165），场上 3 根数据尖刺；尖刺全毁 → 槽位解除、进入阶段三。本体在本阶段不可伤。</summary>
        Lockout = 2,
        /// <summary>阶段三·熔毁倒计时（60 秒）：结束前击毁它；到时熔毁重创全场（包括它自己）。</summary>
        Meltdown = 3,
        /// <summary>熔毁之后：战斗继续（FG09 第 7 章“战斗仍然可以继续”）。</summary>
        Burnout = 4,
        Defeated = 5,
    }

    /// <summary>
    /// FG11-BOSS-01（FG09 第 7 章“首领：余烬主机”，FGR-FAC-006；FG19 FGR-EMG-031 / 165；验收 FGT-FAC-006、FGT-EMG-061）：余烬主机首领战的状态机。
    /// <see cref="TryTransition"/> 是阶段的唯一写入口（非法跳转拒绝并记开发诊断）；复位（全灭 / 全员撤出）走 <see cref="ResetToPreFight"/>。
    ///
    /// - 部件是 <see cref="RegionEnemyRecord"/>（编成表 data_center：boss 行 = 本体，按职能 boss 识别；boss_wave 行 = 数据尖刺，按职能 structure 识别），不认敌人 ID。
    ///   本体血量真相在这里（external_health），内核只发伤害请求（<see cref="ApplyDamage"/>）；尖刺是内核耐久的结构。
    /// - 阶段一·升频：每 boss.ember.heat_step_seconds 升温一级，热量上限倍率 = max(heat_cap_min, 1 − 级数 × heat_step)，写进本地点内核的高温辐射倍率
    ///   （<see cref="CombatSite.SetHeatRadiation"/>，派生量，内核打开 / 读档时由 <see cref="SyncSite"/> 重写；敌我同一规则）。耐久降到 phase2_hp_fraction → 阶段二。
    /// - 散热口：每 vent_cycle_seconds 一个周期，最后 vent_open_seconds 秒张开（受伤 × vent_damage_mult），张开前 vent_warn_seconds 预警；其余时间闭合（× sealed_damage_mult）。
    /// - 阶段二·槽位封锁：按（种子, 第几次挑战）从装着固件的生效槽里挑一个调 <see cref="SignalSeizeService.BlockSlot"/>（来源 SourceEmber；固件留在槽里、按空槽重编译、
    ///   冷却不重置，事件 evt.seize 由共用入口注入），同时播种 3 根数据尖刺；尖刺全毁 → <see cref="SignalSeizeService.UnblockSlot"/>（evt.release、立即恢复）→ 阶段三。
    /// - 阶段三·熔毁倒计时 meltdown_seconds：开始、剩 30 秒、最后 countdown_beep_seconds 秒每秒响警报（倒计时音效）；到时熔毁——本地点每台己方机器失去最大耐久的
    ///   meltdown_machine_fraction，其它敌人 meltdown_enemy_fraction，本体自己 meltdown_self_fraction——战斗继续（熔毁之后）。
    /// - 击败：固定奖励（敌人表 components = 核心固件“处决”）、一个三选一数据包、超频领地平定（MarkLeaderDefeated → 主线“让数据城降温” + 关键材料“超算残核” + 剧情节点“破译”），
    ///   解除本来源全部封锁、场地降温。
    /// 全部计时是统一时钟的整数步：暂停不走、倍速按游戏时间、与是否被观察无关；状态在 <see cref="FactionState.EmberBoss"/>（进存档）。热更层每步 O(1)（尖刺 3 根）；熔毁一次 O(本地点单位数)。
    /// </summary>
    public static class EmberHostBoss
    {
        public const string SiteId = "data_center";

        private static readonly HashSet<(EmberPhase, EmberPhase)> Legal = new HashSet<(EmberPhase, EmberPhase)>
        {
            (EmberPhase.Dormant, EmberPhase.Upclock),
            (EmberPhase.Upclock, EmberPhase.Lockout),
            (EmberPhase.Lockout, EmberPhase.Meltdown),
            (EmberPhase.Meltdown, EmberPhase.Burnout),
            (EmberPhase.Meltdown, EmberPhase.Defeated),
            (EmberPhase.Burnout, EmberPhase.Defeated),
        };

        // ── 自检读数 ──
        public static int TransitionCount { get; private set; }
        public static int RejectedTransitions { get; private set; }
        public static int Resets { get; private set; }
        public static int Blasts { get; private set; }
        public static int CountdownBeeps { get; private set; }
        /// <summary>散热口预警 / 张开提示发了几次（反馈信号同类有真实秒限流，自检看状态机发了几次）。</summary>
        public static int VentWarns { get; private set; }
        public static int VentOpens { get; private set; }
        public static int RewardGrants { get; private set; }
        public static int Revision { get; private set; } = 1;

        public static void ResetForTests()
        {
            TransitionCount = RejectedTransitions = Resets = Blasts = CountdownBeeps = RewardGrants = VentWarns = VentOpens = 0;
            _core = null;
            _spikes = null;
            Revision++;
        }

        // ── 调参（fg.TbHomeTuning boss.ember.*）──
        public static float HeatStepSeconds => Tuning("boss.ember.heat_step_seconds", 10f);
        public static float HeatStep => Mathf.Clamp(Tuning("boss.ember.heat_step", 0.05f), 0.001f, 1f);
        public static float HeatCapMin => Mathf.Clamp(Tuning("boss.ember.heat_cap_min", 0.6f), 0.05f, 1f);
        public static float Phase2HpFraction => Mathf.Clamp(Tuning("boss.ember.phase2_hp_fraction", 0.6f), 0.05f, 0.95f);
        public static float VentCycleSeconds => Mathf.Max(1f, Tuning("boss.ember.vent_cycle_seconds", 12f));
        public static float VentOpenSeconds => Mathf.Clamp(Tuning("boss.ember.vent_open_seconds", 4f), 0.1f, VentCycleSeconds - 0.1f);
        public static float VentWarnSeconds => Mathf.Clamp(Tuning("boss.ember.vent_warn_seconds", 1f), 0.8f, VentCycleSeconds - VentOpenSeconds);
        public static float VentDamageMult => Tuning("boss.ember.vent_damage_mult", 2f);
        public static float SealedDamageMult => Tuning("boss.ember.sealed_damage_mult", 0.35f);
        public static float MeltdownSeconds => Tuning("boss.ember.meltdown_seconds", 60f);
        public static float MeltdownMachineFraction => Mathf.Clamp01(Tuning("boss.ember.meltdown_machine_fraction", 0.7f));
        public static float MeltdownEnemyFraction => Mathf.Clamp01(Tuning("boss.ember.meltdown_enemy_fraction", 0.7f));
        public static float MeltdownSelfFraction => Mathf.Clamp01(Tuning("boss.ember.meltdown_self_fraction", 0.3f));
        public static int CountdownBeepSeconds => Mathf.Max(1, Mathf.RoundToInt(Tuning("boss.ember.countdown_beep_seconds", 10f)));

        // ── 状态 ──

        public static EmberBossRecord Record(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Factions?.EmberBoss == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            return s.Factions.EmberBoss;
        }

        public static EmberPhase PhaseOf(CampaignState s) => (EmberPhase)(Record(s)?.Phase ?? 0);

        public static bool IsEngaged(CampaignState s)
        {
            EmberPhase p = PhaseOf(s);
            return p != EmberPhase.Dormant && p != EmberPhase.Defeated;
        }

        public static bool IsDefeated(CampaignState s) => PhaseOf(s) == EmberPhase.Defeated;

        /// <summary>遭遇过（首领战开始过至少一次，或已击败）：监听站首领弱点情报的前提。</summary>
        public static bool WasEncountered(CampaignState s) => (Record(s)?.Attempt ?? 0) > 0 || IsDefeated(s);

        public static string PhaseName(EmberPhase p) => GameText.Get(p switch
        {
            EmberPhase.Upclock => "boss.ember.phase.upclock",
            EmberPhase.Lockout => "boss.ember.phase.lockout",
            EmberPhase.Meltdown => "boss.ember.phase.meltdown",
            EmberPhase.Burnout => "boss.ember.phase.burnout",
            EmberPhase.Defeated => "boss.ember.phase.defeated",
            _ => "boss.ember.phase.dormant",
        });

        /// <summary>场地升温后的热量上限倍率（1 = 没升温）。</summary>
        public static float HeatCapMult(EmberBossRecord r) => r == null || r.HeatLevel <= 0 ? 1f : Mathf.Max(HeatCapMin, 1f - r.HeatLevel * HeatStep);

        // ── 部件（编成表 data_center，按职能识别）──

        private static EnemyEncounter _core;
        private static List<EnemyEncounter> _spikes;
        private static int _partsRevision = int.MinValue;

        private static void EnsureParts()
        {
            int rev = FgContentTables.Revision;
            if (_spikes != null && _partsRevision == rev)
            {
                return;
            }
            _partsRevision = rev;
            _core = null;
            foreach (EnemyEncounter enc in EnemyDefs.Encounters(SiteId, EnemyDefs.ConditionBoss))
            {
                if (EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy row) && row.Role == EnemyDefs.RoleBoss)
                {
                    _core = enc;
                    break;
                }
            }
            _spikes = new List<EnemyEncounter>(3);
            foreach (EnemyEncounter enc in EnemyDefs.Encounters(SiteId, EnemyDefs.ConditionBossWave))
            {
                if (EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy row) && EnemyDefs.IsStructure(row))
                {
                    _spikes.Add(enc);
                }
            }
        }

        public static string CoreInstanceId
        {
            get
            {
                EnsureParts();
                return _core?.Id;
            }
        }

        public static IReadOnlyList<EnemyEncounter> Spikes
        {
            get
            {
                EnsureParts();
                return _spikes;
            }
        }

        public static bool IsSpike(string instanceId)
        {
            EnsureParts();
            foreach (EnemyEncounter e in _spikes)
            {
                if (e.Id == instanceId)
                {
                    return true;
                }
            }
            return false;
        }

        public static RegionEnemyRecord FindPart(CampaignState s, string instanceId)
        {
            if (s?.RegionEnemies == null || instanceId == null)
            {
                return null;
            }
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e != null && e.EnemyInstanceId == instanceId)
                {
                    return e;
                }
            }
            return null;
        }

        public static RegionEnemyRecord Core(CampaignState s) => FindPart(s, CoreInstanceId);

        /// <summary>还立着的数据尖刺数（O(尖刺数) = 3）。</summary>
        public static int SpikesAlive(CampaignState s)
        {
            int n = 0;
            foreach (EnemyEncounter e in Spikes)
            {
                if (FindPart(s, e.Id)?.IsAlive == true)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>监听站的首领弱点情报在有效期内。</summary>
        public static bool IntelKnown(CampaignState s) =>
            s != null && Economy.IntelService.HasValid(s, Economy.IntelCatalog.KindBoss, Economy.IntelCatalog.BossIdEmberHost, GameClock.Ticks, out _);

        // ── 唯一阶段写入口 ──

        public static bool TryTransition(CampaignState s, EmberPhase to)
        {
            EmberBossRecord r = Record(s);
            if (r == null)
            {
                return false;
            }
            EmberPhase from = (EmberPhase)r.Phase;
            if (!Legal.Contains((from, to)))
            {
                RejectedTransitions++;
                Log.Error($"[EmberHostBoss] 非法阶段转换 {from} → {to}，已拒绝（开发诊断：调用方逻辑有误）。");
                return false;
            }
            r.Phase = (int)to;
            r.BestPhase = Math.Max(r.BestPhase, (int)to);
            TransitionCount++;
            Revision++;
            long now = GameClock.Ticks;
            Vector2 at = PartPosition(s, CoreInstanceId);
            switch (to)
            {
                case EmberPhase.Lockout:
                    BeginLockout(s, r, at);
                    break;
                case EmberPhase.Meltdown:
                    r.MeltdownEndTick = now + GameClock.TicksFor(MeltdownSeconds);
                    r.LastBeepSecond = -1;
                    string text = GameText.Format("boss.ember.notify.meltdown", Mathf.RoundToInt(MeltdownSeconds).ToString());
                    NotificationCenter.Post("ember_boss", text, new Vector3(at.x, 0f, at.y));
                    Beep(r, Mathf.CeilToInt(MeltdownSeconds), at);
                    GuidanceHooks.Raise("ember.first_meltdown");
                    break;
                case EmberPhase.Burnout:
                    r.MeltdownEndTick = 0;
                    break;
                case EmberPhase.Defeated:
                    r.MeltdownEndTick = 0;
                    r.DefeatedTick = now;
                    FeedbackCues.RaiseLocated(FeedbackCueId.BossDestroyed, at);
                    EndFightEffects(s, r);
                    GrantRewardsOnce(s, at);
                    break;
                default:
                    Announce(to, at);
                    break;
            }
            Log.Info($"[EmberHostBoss] 阶段 {from} → {to}（第 {r.Attempt} 次挑战）。");
            return true;
        }

        private static void Announce(EmberPhase to, Vector2 at)
        {
            string text = GameText.Format("boss.ember.notify.phase", PhaseName(to));
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, text);
            NotificationCenter.Post("ember_boss", text, new Vector3(at.x, 0f, at.y));
        }

        /// <summary>阶段二开始：封锁一个槽（共用入口，来源 SourceEmber）、播种 3 根数据尖刺（满血）。</summary>
        private static void BeginLockout(CampaignState s, EmberBossRecord r, Vector2 at)
        {
            r.BlockedSlot = PickSlot(s, r);
            if (r.BlockedSlot >= 0 && !SignalSeizeService.BlockSlot(s, r.BlockedSlot, SignalSeizeService.SourceEmber))
            {
                r.BlockedSlot = -1;
            }
            RegionEnemySites.SeedEncounters(s, SiteId, EnemyDefs.ConditionBossWave);
            foreach (EnemyEncounter e in Spikes)
            {
                RestorePart(s, e.Id);
            }
            r.SpikesLeft = SpikesAlive(s);
            string slot = r.BlockedSlot >= 0 ? (r.BlockedSlot + 1).ToString() : "-";
            string text = GameText.Format("boss.ember.notify.lockout", slot);
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, text);
            NotificationCenter.Post("ember_boss", text, new Vector3(at.x, 0f, at.y));
        }

        /// <summary>
        /// 被封锁的槽：生效槽里装着固件、还没被封的那些里，按（种子, 第几次挑战）挑一个（读档一致）；一个都没装 → 在已解锁、没被封的槽里挑。都没有 → -1。
        /// </summary>
        public static int PickSlot(CampaignState s, EmberBossRecord r)
        {
            var filled = new List<int>(SignalCoreService.MaxSlots);
            var open = new List<int>(SignalCoreService.MaxSlots);
            int unlocked = SignalCoreService.UnlockedSlots(s);
            for (int i = 0; i < unlocked; i++)
            {
                if (SignalSeizeService.IsBlocked(s, i))
                {
                    continue;
                }
                open.Add(i);
                if (SignalCoreService.IsSlotActive(s, i) && SignalCoreService.SlotContentId(s, i).Length > 0)
                {
                    filled.Add(i);
                }
            }
            List<int> pool = filled.Count > 0 ? filled : open;
            if (pool.Count == 0)
            {
                return -1;
            }
            uint h = Hash((uint)(s.World?.WorldSeed ?? s.RandomSeed), (uint)(r?.Attempt ?? 0), 0x534C4F54u);
            return pool[(int)(h % (uint)pool.Count)];
        }

        // ── 开战 / 复位 ──

        /// <summary>机器进入数据中心（新进入，不是读档恢复）时调用：首领还在休眠就开始一次新的挑战。返回是否这一次开的战。</summary>
        public static bool Engage(CampaignState s)
        {
            EmberBossRecord r = Record(s);
            if (r == null || (EmberPhase)r.Phase != EmberPhase.Dormant)
            {
                return false;
            }
            EnsureParts();
            if (_core == null || _spikes.Count == 0)
            {
                EnemyDefs.ReportEncounterProblem(SiteId, "ember_host");
                return false;
            }
            RegionEnemySites.SeedEncounters(s, SiteId, EnemyDefs.ConditionBoss);
            RestorePart(s, _core.Id);
            long now = GameClock.Ticks;
            r.Attempt++;
            r.EngagedTick = now;
            r.HeatLevel = 0;
            r.NextHeatTick = now + GameClock.TicksFor(HeatStepSeconds);
            r.VentCycleTick = now;
            r.VentState = 0;
            r.BlockedSlot = -1;
            r.SpikesLeft = 0;
            r.MeltdownEndTick = 0;
            r.LastBeepSecond = -1;
            r.MeltedDown = false;
            if (!TryTransition(s, EmberPhase.Upclock))
            {
                return false;
            }
            Vector2 at = PartPosition(s, _core.Id);
            NotificationCenter.Post("ember_boss", GameText.Get("boss.ember.notify.engaged"), new Vector3(at.x, 0f, at.y));
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == SiteId)
                {
                    MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.BossParticipation);
                }
            }
            GuidanceHooks.Raise("ember.first_boss");
            return true;
        }

        /// <summary>
        /// 全灭或全员撤出、首领还没被击败 → 首领回到开战前（本体满血、尖刺撤走、阶段 = 休眠、封锁解除、场地降温），记下“上次止步于第几阶段”（FGR-FAC-006 / ER7-FAIL-01 同一语义）。
        /// </summary>
        public static bool ResetToPreFight(CampaignState s, bool wiped)
        {
            EmberBossRecord r = Record(s);
            EmberPhase p = (EmberPhase)(r?.Phase ?? 0);
            if (r == null || p == EmberPhase.Dormant || p == EmberPhase.Defeated)
            {
                return false;
            }
            r.LastReachedPhase = (int)p;
            EnsureParts();
            var spikes = new HashSet<string>(StringComparer.Ordinal);
            foreach (EnemyEncounter e in _spikes)
            {
                spikes.Add(e.Id);
            }
            if (s.RegionEnemies != null)
            {
                var keep = new List<RegionEnemyRecord>(s.RegionEnemies.Length);
                foreach (RegionEnemyRecord e in s.RegionEnemies)
                {
                    if (e != null && !spikes.Contains(e.EnemyInstanceId))
                    {
                        keep.Add(e);
                    }
                }
                s.RegionEnemies = keep.ToArray();
            }
            if (_core != null)
            {
                RestorePart(s, _core.Id);
            }
            EndFightEffects(s, r);
            r.Phase = (int)EmberPhase.Dormant;
            r.MeltdownEndTick = 0;
            r.SpikesLeft = 0;
            Resets++;
            Revision++;
            NotificationCenter.Post("ember_boss", GameText.Format("boss.ember.notify.retry", PhaseName(p)));
            Log.Info($"[EmberHostBoss] 首领战{(wiped ? "全灭" : "全员撤出")}：止步于 {p}，首领复位到开战前。");
            return true;
        }

        /// <summary>战斗结束（击败 / 复位）：解除本来源的全部封锁（evt.release，冷却不重置），场地降温。</summary>
        private static void EndFightEffects(CampaignState s, EmberBossRecord r)
        {
            SignalSeizeService.UnblockAll(s, SignalSeizeService.SourceEmber);
            r.BlockedSlot = -1;
            r.HeatLevel = 0;
            r.VentState = 0;
            ApplyArenaHeat(CombatSites.Get(SiteId), r);
        }

        private static void RestorePart(CampaignState s, string instanceId)
        {
            RegionEnemyRecord rec = FindPart(s, instanceId);
            if (rec == null || !EnemyDefs.TryGet(rec.EnemyTypeId, out MechEnemy row))
            {
                return;
            }
            float hp = EnemyDefs.SeedHealth(s, row);
            rec.Health = hp;
            rec.MaxHealth = hp;
            rec.IsAlive = true;
            if (RegionEnemySites.TrySpawnPosition(SiteId, EnemyDefs.TryEncounter(instanceId, out EnemyEncounter enc) ? enc.AnchorId : null, out Vector2 at))
            {
                rec.Position = at;
            }
        }

        // ── 伤害（内核伤害请求 → 这里结算）──

        /// <summary>散热口此刻所处的段：0 闭合 / 1 预警 / 2 张开。周期从开战那一步算起，只看步序号。</summary>
        public static int VentStateAt(EmberBossRecord r, long now)
        {
            if (r == null)
            {
                return 0;
            }
            long cycle = Math.Max(1, GameClock.TicksFor(VentCycleSeconds));
            long open = GameClock.TicksFor(VentOpenSeconds);
            long warn = GameClock.TicksFor(VentWarnSeconds);
            long t = ((now - r.VentCycleTick) % cycle + cycle) % cycle;
            return t >= cycle - open ? 2 : t >= cycle - open - warn ? 1 : 0;
        }

        /// <summary>离散热口下一次切换（张开 / 闭合）还有几游戏秒。</summary>
        public static float VentSecondsLeft(EmberBossRecord r, long now)
        {
            if (r == null)
            {
                return 0f;
            }
            long cycle = Math.Max(1, GameClock.TicksFor(VentCycleSeconds));
            long open = GameClock.TicksFor(VentOpenSeconds);
            long t = ((now - r.VentCycleTick) % cycle + cycle) % cycle;
            long left = t >= cycle - open ? cycle - t : cycle - open - t;
            return left / (float)Math.Max(1, GameClock.StepHz);
        }

        public static bool VentOpen(CampaignState s) => VentStateAt(Record(s), GameClock.Ticks) == 2;

        public static (bool ok, string reason) ApplyDamage(CampaignState s, RegionEnemyRecord target, float damage)
        {
            EmberBossRecord r = Record(s);
            if (r == null || target == null || !target.IsAlive)
            {
                return (false, "no-target");
            }
            if (target.EnemyInstanceId != CoreInstanceId)
            {
                return (false, "not-a-boss-part");
            }
            EmberPhase p = (EmberPhase)r.Phase;
            if (p != EmberPhase.Upclock && p != EmberPhase.Meltdown && p != EmberPhase.Burnout)
            {
                return (false, "core-invulnerable:" + p);
            }
            float mult = VentStateAt(r, GameClock.Ticks) == 2 ? VentDamageMult : SealedDamageMult;
            float hp = target.Health - Mathf.Max(0f, damage) * mult;
            if (p == EmberPhase.Upclock)
            {
                // 阶段一的门槛不能被一发越过：耐久停在阶段二门槛上，进入槽位封锁。
                float gate = target.MaxHealth * Phase2HpFraction;
                target.Health = Mathf.Max(gate, hp);
                if (target.Health <= gate)
                {
                    TryTransition(s, EmberPhase.Lockout);
                }
                return (true, null);
            }
            target.Health = Mathf.Max(0f, hp);
            if (target.Health <= 0f)
            {
                target.IsAlive = false;
                TryTransition(s, EmberPhase.Defeated);
            }
            return (true, null);
        }

        /// <summary>伤害没生效时的原因文本（开火结果“无法被击伤”的详情）。</summary>
        public static string InvulnerableDetail(CampaignState s, RegionEnemyRecord e) => GameText.Get("boss.ember.invulnerable.lockout");

        // ── 每个模拟步（数据中心载入时由室内地点调用；与观察无关）──

        public static void Tick(CampaignState s)
        {
            EmberBossRecord r = Record(s);
            if (r == null || !IsEngaged(s))
            {
                return;
            }
            long now = GameClock.Ticks;
            Vector2 at = PartPosition(s, CoreInstanceId);
            TickVents(r, now, at, (EmberPhase)r.Phase != EmberPhase.Lockout); // 阶段二本体不可伤：散热口照转，但不发“重创窗口”提示
            switch ((EmberPhase)r.Phase)
            {
                case EmberPhase.Upclock:
                    if (now >= r.NextHeatTick && HeatCapMult(r) > HeatCapMin + 1e-5f)
                    {
                        r.HeatLevel++;
                        r.NextHeatTick = now + GameClock.TicksFor(HeatStepSeconds);
                        Revision++;
                        ApplyArenaHeat(CombatSites.Get(SiteId), r);
                    }
                    break;
                case EmberPhase.Lockout:
                    int alive = SpikesAlive(s);
                    if (alive != r.SpikesLeft)
                    {
                        r.SpikesLeft = alive;
                        Revision++;
                        if (alive > 0)
                        {
                            NotificationCenter.Post("ember_boss", GameText.Format("boss.ember.notify.spike_down", alive.ToString()), new Vector3(at.x, 0f, at.y));
                        }
                    }
                    if (alive == 0)
                    {
                        // 尖刺全毁：夺回槽位（立即恢复，冷却不重置；evt.release 由共用入口注入）→ 阶段三。
                        if (r.BlockedSlot >= 0)
                        {
                            SignalSeizeService.UnblockSlot(s, r.BlockedSlot);
                            r.BlockedSlot = -1;
                        }
                        TryTransition(s, EmberPhase.Meltdown);
                    }
                    break;
                case EmberPhase.Meltdown:
                    long left = r.MeltdownEndTick - now;
                    if (left <= 0)
                    {
                        Blast(s, r, at);
                        break;
                    }
                    int sec = (int)Math.Ceiling(left / (double)Math.Max(1, GameClock.StepHz));
                    if (sec != r.LastBeepSecond && (sec <= CountdownBeepSeconds || sec == 30))
                    {
                        Beep(r, sec, at);
                    }
                    break;
            }
        }

        private static void TickVents(EmberBossRecord r, long now, Vector2 at, bool cue)
        {
            int v = VentStateAt(r, now);
            if (v == r.VentState)
            {
                return;
            }
            r.VentState = v;
            Revision++;
            if (!cue)
            {
                return;
            }
            if (v == 1)
            {
                VentWarns++;
                FeedbackCues.RaiseLocated(FeedbackCueId.EmberVentWarn, at);
            }
            else if (v == 2)
            {
                VentOpens++;
                FeedbackCues.RaiseLocated(FeedbackCueId.EmberVent, at);
            }
        }

        private static void Beep(EmberBossRecord r, int second, Vector2 at)
        {
            r.LastBeepSecond = second;
            CountdownBeeps++;
            FeedbackCues.RaiseLocated(FeedbackCueId.EmberCountdown, at, GameText.Format("boss.ember.hud.countdown", second.ToString()));
        }

        /// <summary>熔毁：本地点每台己方机器、其它敌人按最大耐久比例受到重创，本体自己也受伤；战斗继续（熔毁之后）。本体被自己的熔毁打死 → 击败。</summary>
        private static void Blast(CampaignState s, EmberBossRecord r, Vector2 at)
        {
            Blasts++;
            r.MeltedDown = true;
            CombatSite site = CombatSites.Get(SiteId);
            if (site != null && !site.IsDisposed)
            {
                var machines = new List<int>(site.MachineLogicIds);
                foreach (int id in machines)
                {
                    if (site.TryGetMachineUnit(id, out int unit) && site.Kernel.TryGetUnit(unit, out CombatUnitView v) && v.Health > 0f)
                    {
                        site.Kernel.Damage(unit, v.MaxHealth * MeltdownMachineFraction, 0);
                    }
                }
                if (s.RegionEnemies != null)
                {
                    string core = CoreInstanceId;
                    var others = new List<RegionEnemyRecord>();
                    foreach (RegionEnemyRecord e in s.RegionEnemies)
                    {
                        if (e != null && e.IsAlive && e.RegionId == SiteId && e.EnemyInstanceId != core)
                        {
                            others.Add(e);
                        }
                    }
                    foreach (RegionEnemyRecord e in others)
                    {
                        site.TryDamageEnemy(e.EnemyInstanceId, e.MaxHealth * MeltdownEnemyFraction);
                    }
                }
                site.ProcessEvents();
            }
            FeedbackCues.RaiseLocated(FeedbackCueId.EmberBlast, at);
            NotificationCenter.Post("ember_boss", GameText.Get("boss.ember.notify.blast"), new Vector3(at.x, 0f, at.y));
            RegionEnemyRecord self = Core(s);
            if (self != null && self.IsAlive)
            {
                self.Health = Mathf.Max(0f, self.Health - self.MaxHealth * MeltdownSelfFraction);
                if (self.Health <= 0f)
                {
                    self.IsAlive = false;
                    TryTransition(s, EmberPhase.Defeated);
                    return;
                }
            }
            TryTransition(s, EmberPhase.Burnout);
        }

        // ── 内核同步（阶段变了 / 读档 / 部件重建后；O(1)）──

        /// <summary>本体的可选中 / 无敌 / 开火标志与场地升温倍率写进本地点内核（读档、部件重建、阶段变化后调用）。</summary>
        public static void SyncSite(CombatSite site, CampaignState s)
        {
            if (site == null || site.IsDisposed || s == null)
            {
                return;
            }
            EmberPhase p = PhaseOf(s);
            if (CoreInstanceId != null && site.TryGetEnemyUnit(CoreInstanceId, out int core))
            {
                bool alive = Core(s)?.IsAlive ?? false;
                bool engaged = IsEngaged(s);
                site.SetUnitFlag(core, CombatUnitFlags.Targetable, alive);
                site.SetUnitFlag(core, CombatUnitFlags.Invulnerable, !(p == EmberPhase.Upclock || p == EmberPhase.Meltdown || p == EmberPhase.Burnout));
                site.SetUnitFlag(core, CombatUnitFlags.WeaponEnabled, engaged && alive);
            }
            ApplyArenaHeat(site, Record(s));
        }

        private static readonly List<float4> HeatShape = new List<float4>(1);

        /// <summary>场地升温 → 本地点内核的高温辐射（整个场地一块，热量上限 × <see cref="HeatCapMult"/>，散热不变）。没升温时清掉。</summary>
        private static void ApplyArenaHeat(CombatSite site, EmberBossRecord r)
        {
            if (site == null || site.IsDisposed)
            {
                return;
            }
            float mult = HeatCapMult(r);
            HeatShape.Clear();
            if (mult < 1f)
            {
                HeatShape.Add(new float4(0f, 0f, 0f, 100000f)); // 室内全场（室内坐标在 0～宽 / 高之内）
            }
            site.SetHeatRadiation(HeatShape, mult, 1f);
        }

        // ── 奖励 ──

        private static void GrantRewardsOnce(CampaignState s, Vector2 at)
        {
            EmberBossRecord r = Record(s);
            if (r == null || r.RewardsGranted)
            {
                return;
            }
            r.RewardsGranted = true;
            RewardGrants++;
            RegionEnemyRecord core = Core(s);
            if (core != null && EnemyDefs.TryGet(core.EnemyTypeId, out MechEnemy row))
            {
                if (!string.IsNullOrEmpty(row.Components) && row.Components != EnemyDefs.None)
                {
                    string[] fw = row.Components.Split(',');
                    // components 第一个 = 固定的核心固件（处决）；后面的 = 此前还没拿到才给。
                    Primitive.PrimitiveInventory.GrantBossFirmware(s, "boss:" + SiteId + ":0", fw[0].Trim());
                    for (int i = 1; i < fw.Length; i++)
                    {
                        if (!FirmwareLibrary.IsObtained(s, fw[i].Trim()))
                        {
                            Primitive.PrimitiveInventory.GrantBossFirmware(s, "boss:" + SiteId + ":" + i, fw[i].Trim());
                        }
                    }
                }
                Economy.TestRangeService.NoteEnemyDefeated(s, row.Id);
            }
            int holder = Economy.LootPickService.NearestMachine(SiteId, at, 200f, 0);
            Economy.LootPickService.Grant(s, Economy.LootPickService.SourceBoss, EnemyDefs.FactionOverclock, holder, SiteId, at);
            // 超频领地平定 → 主线“让数据城降温”完成 → 关键材料“超算残核”进核心保管库；剧情节点 leader:overclock = “破译”（舰队信号破译片段）。
            WorldSim.TerritoryControlService.MarkLeaderDefeated(s, EnemyDefs.FactionOverclock);
            NotificationCenter.Post("ember_boss", GameText.Get("boss.ember.notify.defeated"), new Vector3(at.x, 0f, at.y));
        }

        // ── 工具 ──

        public static Vector2 PartPosition(CampaignState s, string instanceId)
        {
            CombatSite site = CombatSites.Get(SiteId);
            if (site != null && instanceId != null && site.TryGetEnemyPosition(instanceId, out Vector2 live))
            {
                return live;
            }
            RegionEnemyRecord rec = FindPart(s, instanceId);
            return rec != null ? rec.Position : Vector2.zero;
        }

        private static uint Hash(uint a, uint b, uint c)
        {
            unchecked
            {
                uint h = 2166136261u;
                h = (h ^ a) * 16777619u;
                h = (h ^ b) * 16777619u;
                h = (h ^ c) * 16777619u;
                h ^= h >> 13;
                h *= 0x5bd1e995u;
                h ^= h >> 15;
                return h;
            }
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
