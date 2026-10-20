using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using BinGames.Sim.Combat;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>寂听主脑的阶段（存档稳定码：只追加，不重排）。</summary>
    public enum SilentMindPhase
    {
        Dormant = 0,
        /// <summary>阶段一·藏身：本体藏在 6 个中继节点之一，其余是诱饵；周期性中继跳转。</summary>
        Hiding = 1,
        /// <summary>阶段二·磁暴：本体现身，周期性展开全场偏折场（1.5 秒预警）。</summary>
        Magnetic = 2,
        /// <summary>阶段三·全频静默的情报预警倒计时。</summary>
        SilenceWarn = 3,
        /// <summary>阶段三·全频静默：20 秒全场断链。</summary>
        Silence = 4,
        /// <summary>撑过静默：核心暴露（受伤倍率）。</summary>
        Exposed = 5,
        Defeated = 6,
    }

    /// <summary>
    /// FG9-FAC-01（FG09 第 4 章“首领：寂听主脑”，FGR-FAC-006；FGT-FAC-003 / 004 / 006）：寂听主脑首领战的状态机——<see cref="TryTransition"/> 是阶段的唯一写入口
    /// （非法跳转一律拒绝并记开发诊断）；复位（全灭 / 全员撤出）走 <see cref="ResetToPreFight"/>，不经过阶段转换表。
    ///
    /// - 部件全是 <see cref="RegionEnemyRecord"/>（编成表 relay_hall 的 boss 行：6 个中继节点 + 主脑本体），按表的职能 / 层级识别（职能 structure = 节点、boss = 本体），
    ///   不按敌人 ID 写特例（IC-REQ-012）；血量真相在这里（external_health 标签），内核只发伤害请求（<see cref="ApplyDamage"/>）。
    /// - 阶段一：真节点由（世界种子, 第几次挑战）派生；每 boss.silent.relay_jump_seconds 秒跳到另一个存活节点，目标节点提前 boss.silent.relay_warn_seconds 秒亮起；
    ///   亮起期间目标被摧毁 → 跳转取消、主脑留在出发节点并现形（FG09 第 10 节负向）。打中真节点 → 阶段二；打掉诱饵节点只是拆掉它。
    ///   标记器标记的节点显示波形（真节点不规则）；监听站的首领弱点情报直接指出真节点（HUD 读 <see cref="RealNodeKnown"/>）。
    /// - 阶段二：本体可伤、开火、全场偏折场（内核偏折场参数来自敌人表；只在本阶段打开 DeflectArmed）。耐久降到 boss.silent.phase2_hp_fraction → 静默预警。
    /// - 阶段三：预警 boss.silent.silence_warn_seconds 秒（字幕 + 警报 + HUD 倒计时 + 通知）→ 全频静默 boss.silent.silence_seconds 秒：地点里全部机器断链进入安全模式（只跑本地常规固件），
    ///   接入一律被拒（<see cref="SignalLinkService.SiteSilencedProvider"/>），召来一波伏击（编成 boss_wave）；本体不可伤。到时 → 核心暴露（伤害 × boss.silent.exposed_damage_mult）。
    /// - 击败：固定奖励（敌人表 components 第一个 = 核心固件，第二个 = 此前没拿到才给）、一个三选一数据包、静默领地平定（MarkLeaderDefeated）→ 主线“监听阵列核”。
    /// 全部计时是统一时钟的整数步：暂停不走、倍速按游戏时间、与是否被观察无关；状态在 <see cref="FactionState.SilentBoss"/>（进存档）。
    /// </summary>
    public static class SilentMindBoss
    {
        public const string SiteId = RelayHallLayout.SurfaceId;

        /// <summary>静默反制的三种编成条件（开战时按当天锁定的那一种追加；复位时三种都撤）。</summary>
        private static readonly string[] CounterConditions = { EnemyDefs.ConditionCounterDeflect, EnemyDefs.ConditionCounterListen, EnemyDefs.ConditionCounterJam };

        private static readonly Dictionary<SilentMindPhase, SilentMindPhase> LegalNext = new Dictionary<SilentMindPhase, SilentMindPhase>
        {
            [SilentMindPhase.Dormant] = SilentMindPhase.Hiding,
            [SilentMindPhase.Hiding] = SilentMindPhase.Magnetic,
            [SilentMindPhase.Magnetic] = SilentMindPhase.SilenceWarn,
            [SilentMindPhase.SilenceWarn] = SilentMindPhase.Silence,
            [SilentMindPhase.Silence] = SilentMindPhase.Exposed,
            [SilentMindPhase.Exposed] = SilentMindPhase.Defeated,
        };

        // ── 自检读数 ──
        public static int TransitionCount { get; private set; }
        public static int RejectedTransitions { get; private set; }
        public static int Resets { get; private set; }
        public static int JumpsStarted { get; private set; }
        public static int JumpsCancelled { get; private set; }
        public static int Revision { get; private set; } = 1;

        public static void ResetForTests()
        {
            TransitionCount = RejectedTransitions = Resets = JumpsStarted = JumpsCancelled = 0;
            _nodes = null;
            _core = null;
            _wave = null;
            Revision++;
        }

        // ── 调参（fg.TbHomeTuning boss.silent.*）──
        public static float JumpSeconds => Tuning("boss.silent.relay_jump_seconds", 14f);
        public static float JumpWarnSeconds => Tuning("boss.silent.relay_warn_seconds", 1.5f);
        public static float RevealSeconds => Tuning("boss.silent.reveal_seconds", 6f);
        public static float Phase2HpFraction => Mathf.Clamp(Tuning("boss.silent.phase2_hp_fraction", 0.55f), 0.05f, 0.95f);
        public static float SilenceWarnSeconds => Tuning("boss.silent.silence_warn_seconds", 5f);
        public static float SilenceSeconds => Tuning("boss.silent.silence_seconds", 20f);
        public static float ExposedDamageMult => Tuning("boss.silent.exposed_damage_mult", 1.5f);

        // ── 状态 ──

        public static SilentBossRecord Record(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Factions?.SilentBoss == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            return s.Factions.SilentBoss;
        }

        public static SilentMindPhase PhaseOf(CampaignState s) => (SilentMindPhase)(Record(s)?.Phase ?? 0);

        public static bool IsEngaged(CampaignState s)
        {
            SilentMindPhase p = PhaseOf(s);
            return p != SilentMindPhase.Dormant && p != SilentMindPhase.Defeated;
        }

        public static bool IsDefeated(CampaignState s) => PhaseOf(s) == SilentMindPhase.Defeated;

        /// <summary>遭遇过（首领战开始过至少一次，或已击败）：监听站首领弱点情报的前提（FGR-FAC-006“需要先遭遇过一次”）。</summary>
        public static bool WasEncountered(CampaignState s) => (Record(s)?.Attempt ?? 0) > 0 || IsDefeated(s);

        /// <summary>全频静默中的地点（<see cref="SignalLinkService.SiteSilencedProvider"/>）。</summary>
        public static bool IsSilencing(CampaignState s, string regionId) => regionId == SiteId && PhaseOf(s) == SilentMindPhase.Silence;

        public static string PhaseName(SilentMindPhase p) => GameText.Get(p switch
        {
            SilentMindPhase.Hiding => "boss.silent.phase.hiding",
            SilentMindPhase.Magnetic => "boss.silent.phase.magnetic",
            SilentMindPhase.SilenceWarn => "boss.silent.phase.silence_warn",
            SilentMindPhase.Silence => "boss.silent.phase.silence",
            SilentMindPhase.Exposed => "boss.silent.phase.exposed",
            SilentMindPhase.Defeated => "boss.silent.phase.defeated",
            _ => "boss.silent.phase.dormant",
        });

        // ── 部件（编成表 relay_hall 的 boss 行，按职能识别）──

        private static List<EnemyEncounter> _nodes;
        private static EnemyEncounter _core;
        private static List<EnemyEncounter> _wave;
        private static int _partsRevision = int.MinValue;

        private static void EnsureParts()
        {
            int rev = FgContentTables.Revision;
            if (_nodes != null && _partsRevision == rev)
            {
                return;
            }
            _partsRevision = rev;
            _nodes = new List<EnemyEncounter>(6);
            _core = null;
            foreach (EnemyEncounter enc in EnemyDefs.Encounters(SiteId, EnemyDefs.ConditionBoss))
            {
                if (!EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy row))
                {
                    continue;
                }
                if (EnemyDefs.IsStructure(row))
                {
                    _nodes.Add(enc);
                }
                else if (row.Role == EnemyDefs.RoleBoss && _core == null)
                {
                    _core = enc;
                }
            }
            _wave = EnemyDefs.Encounters(SiteId, EnemyDefs.ConditionBossWave);
        }

        /// <summary>中继节点的实例 ID（编成表顺序 = 节点序号）。</summary>
        public static IReadOnlyList<EnemyEncounter> Nodes
        {
            get
            {
                EnsureParts();
                return _nodes;
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

        public static int NodeIndexOf(string instanceId)
        {
            EnsureParts();
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].Id == instanceId)
                {
                    return i;
                }
            }
            return -1;
        }

        public static bool IsBossPart(string instanceId) => instanceId != null && (instanceId == CoreInstanceId || NodeIndexOf(instanceId) >= 0);

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

        public static RegionEnemyRecord Node(CampaignState s, int index) => index >= 0 && index < Nodes.Count ? FindPart(s, Nodes[index].Id) : null;

        public static string NodeLabel(int index) => GameText.Format("boss.silent.node", (index + 1).ToString());

        /// <summary>真节点的名字（情报文本用；还没开战时写“开战后揭示”）。</summary>
        public static string RealNodeLabel(CampaignState s)
        {
            SilentBossRecord r = Record(s);
            return r != null && r.RealNode >= 0 && IsEngaged(s) ? NodeLabel(r.RealNode) : GameText.Get("boss.silent.intel.node_pending");
        }

        /// <summary>监听站的首领弱点情报在有效期内：HUD 直接指出真节点。</summary>
        public static bool RealNodeKnown(CampaignState s) =>
            s != null && Economy.IntelService.HasValid(s, Economy.IntelCatalog.KindBoss, Economy.IntelCatalog.BossIdSilentMind, GameClock.Ticks, out _);

        // ── 唯一阶段写入口 ──

        public static bool TryTransition(CampaignState s, SilentMindPhase to)
        {
            SilentBossRecord r = Record(s);
            if (r == null)
            {
                return false;
            }
            SilentMindPhase from = (SilentMindPhase)r.Phase;
            if (!LegalNext.TryGetValue(from, out SilentMindPhase legal) || legal != to)
            {
                RejectedTransitions++;
                Log.Error($"[SilentMindBoss] 非法阶段转换 {from} → {to}，已拒绝（开发诊断：调用方逻辑有误）。");
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
                case SilentMindPhase.Magnetic:
                    r.JumpTarget = -1;
                    r.PhaseEndTick = 0;
                    Announce(s, to, at);
                    break;
                case SilentMindPhase.SilenceWarn:
                    r.PhaseEndTick = now + GameClock.TicksFor(SilenceWarnSeconds);
                    string warn = GameText.Format("boss.silent.notify.silence_warn", Mathf.CeilToInt(SilenceWarnSeconds).ToString());
                    FeedbackCues.RaiseLocated(FeedbackCueId.SilentSilenceWarn, at, warn);
                    NotificationCenter.Post("silent_boss", warn, new Vector3(at.x, 0f, at.y));
                    GuidanceHooks.Raise("silent.first_silence");
                    break;
                case SilentMindPhase.Silence:
                    r.PhaseEndTick = now + GameClock.TicksFor(SilenceSeconds);
                    FeedbackCues.RaiseLocated(FeedbackCueId.SilentSilence, at);
                    ApplySilence(s);
                    RegionEnemySites.SeedEncounters(s, SiteId, EnemyDefs.ConditionBossWave);
                    break;
                case SilentMindPhase.Exposed:
                    r.PhaseEndTick = 0;
                    Announce(s, to, at);
                    break;
                case SilentMindPhase.Defeated:
                    r.PhaseEndTick = 0;
                    r.DefeatedTick = now;
                    FeedbackCues.RaiseLocated(FeedbackCueId.BossDestroyed, at);
                    GrantRewardsOnce(s, at);
                    break;
                default:
                    Announce(s, to, at);
                    break;
            }
            Log.Info($"[SilentMindBoss] 阶段 {from} → {to}（第 {r.Attempt} 次挑战）。");
            return true;
        }

        private static void Announce(CampaignState s, SilentMindPhase to, Vector2 at)
        {
            string text = GameText.Format("boss.silent.notify.phase", PhaseName(to));
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, text);
            NotificationCenter.Post("silent_boss", text, new Vector3(at.x, 0f, at.y));
        }

        // ── 开战 / 复位 ──

        /// <summary>
        /// 机器进入大厅（新进入，不是读档恢复）时调用：首领还在休眠就开始一次新的挑战——播种 6 个节点 + 本体（满血），按（种子, 第几次挑战）定真节点，阶段 → 藏身。
        /// 已在战斗中 / 已击败时什么都不做。返回是否这一次开的战。
        /// </summary>
        public static bool Engage(CampaignState s)
        {
            SilentBossRecord r = Record(s);
            if (r == null || (SilentMindPhase)r.Phase != SilentMindPhase.Dormant)
            {
                return false;
            }
            EnsureParts();
            if (_core == null || _nodes.Count == 0)
            {
                EnemyDefs.ReportEncounterProblem(SiteId, "boss");
                return false;
            }
            RegionEnemySites.SeedEncounters(s, SiteId, EnemyDefs.ConditionBoss);
            for (int i = 0; i < _nodes.Count; i++)
            {
                RestorePart(s, _nodes[i].Id);
            }
            RestorePart(s, _core.Id);
            r.Attempt++;
            r.RealNode = (int)(Hash((uint)(s.World?.WorldSeed ?? s.RandomSeed), (uint)r.Attempt, 0x4E4F4445u) % (uint)_nodes.Count);
            r.Jumps = 0;
            r.JumpTarget = -1;
            r.JumpAtTick = 0;
            r.RevealUntilTick = 0;
            r.PhaseEndTick = 0;
            r.EngagedTick = GameClock.Ticks;
            r.NextJumpTick = GameClock.Ticks + GameClock.TicksFor(JumpSeconds);
            r.CounterId = SilentCounterService.Locked(s);
            if (!TryTransition(s, SilentMindPhase.Hiding))
            {
                return false;
            }
            Vector2 at = PartPosition(s, _core.Id);
            string engaged = GameText.Get("boss.silent.notify.engaged");
            if (!string.IsNullOrEmpty(r.CounterId))
            {
                // FGR-FAC-004 / B05：反制追加的编成要让玩家知道原因（按最近的打法加派了什么）——写进同一条开战通知（同类通知会聚合，分开发会被并成“×2”）。
                engaged += "\n" + GameText.Format("boss.silent.notify.counter", SilentCounterService.CounterName(r.CounterId));
            }
            NotificationCenter.Post("silent_boss", engaged, new Vector3(at.x, 0f, at.y));
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == SiteId)
                {
                    MachineRegistry.TryMarkExperience(m.LogicId, MachineExperienceFlags.BossParticipation);
                }
            }
            GuidanceHooks.Raise("silent.first_boss");
            return true;
        }

        /// <summary>
        /// ER7-FAIL-01 同一语义（FGR-FAC-006“全灭后从首领战前的自动存档重试”）：大厅全灭或全员撤出、首领还没被击败 → 首领回到开战前（节点与本体满血、伏击波撤走、阶段 = 休眠），
        /// 记下“上次止步于第几阶段”。不碰机队 / 仓储 / 已经结算的战利品——等价于把首领字段恢复成“首领战前”自动存档里的样子（那个档在每次进门前写）。
        /// </summary>
        public static bool ResetToPreFight(CampaignState s, bool wiped)
        {
            SilentBossRecord r = Record(s);
            SilentMindPhase p = (SilentMindPhase)(r?.Phase ?? 0);
            if (r == null || p == SilentMindPhase.Dormant || p == SilentMindPhase.Defeated)
            {
                return false;
            }
            r.LastReachedPhase = (int)p;
            EnsureParts();
            var wave = new HashSet<string>(StringComparer.Ordinal);
            foreach (EnemyEncounter enc in _wave)
            {
                wave.Add(enc.Id);
            }
            // 反制追加的编成也是开战时才播种的：一并撤走，下次开战按那天锁定的反制重新播种（不同天锁定的反制不会在大厅里累积）。
            foreach (string counter in CounterConditions)
            {
                foreach (EnemyEncounter enc in EnemyDefs.Encounters(SiteId, counter))
                {
                    wave.Add(enc.Id);
                }
            }
            if (s.RegionEnemies != null && wave.Count > 0)
            {
                var keep = new List<RegionEnemyRecord>(s.RegionEnemies.Length);
                foreach (RegionEnemyRecord e in s.RegionEnemies)
                {
                    if (e != null && !wave.Contains(e.EnemyInstanceId))
                    {
                        keep.Add(e);
                    }
                }
                s.RegionEnemies = keep.ToArray();
            }
            for (int i = 0; i < _nodes.Count; i++)
            {
                RestorePart(s, _nodes[i].Id);
            }
            if (_core != null)
            {
                RestorePart(s, _core.Id);
            }
            r.Phase = (int)SilentMindPhase.Dormant;
            r.PhaseEndTick = 0;
            r.JumpTarget = -1;
            r.JumpAtTick = 0;
            r.RevealUntilTick = 0;
            Resets++;
            Revision++;
            string text = GameText.Format("boss.silent.notify.retry", PhaseName(p));
            NotificationCenter.Post("silent_boss", text);
            Log.Info($"[SilentMindBoss] 首领战{(wiped ? "全灭" : "全员撤出")}：止步于 {p}，首领复位到开战前。");
            return true;
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

        public static (bool ok, string reason) ApplyDamage(CampaignState s, RegionEnemyRecord target, float damage)
        {
            SilentBossRecord r = Record(s);
            if (r == null || target == null || !target.IsAlive)
            {
                return (false, "no-target");
            }
            SilentMindPhase p = (SilentMindPhase)r.Phase;
            int node = NodeIndexOf(target.EnemyInstanceId);
            if (node >= 0)
            {
                if (p != SilentMindPhase.Hiding)
                {
                    return (false, "node-inert");
                }
                if (node == r.RealNode)
                {
                    // 打中真节点：主脑被逼现身（节点本身不掉血）。
                    TryTransition(s, SilentMindPhase.Magnetic);
                    return (true, null);
                }
                target.Health = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage));
                if (target.Health <= 0f)
                {
                    target.IsAlive = false;
                    Revision++;
                    if (node == r.JumpTarget)
                    {
                        CancelJump(s, r);
                    }
                }
                return (true, null);
            }
            if (target.EnemyInstanceId != CoreInstanceId)
            {
                return (false, "not-a-boss-part");
            }
            if (p == SilentMindPhase.Magnetic)
            {
                target.Health = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage));
                if (target.Health <= target.MaxHealth * Phase2HpFraction)
                {
                    TryTransition(s, SilentMindPhase.SilenceWarn);
                }
                return (true, null);
            }
            if (p == SilentMindPhase.Exposed)
            {
                target.Health = Mathf.Max(0f, target.Health - Mathf.Max(0f, damage) * ExposedDamageMult);
                if (target.Health <= 0f)
                {
                    target.IsAlive = false;
                    TryTransition(s, SilentMindPhase.Defeated);
                }
                return (true, null);
            }
            return (false, "core-invulnerable:" + p);
        }

        /// <summary>伤害没生效时的原因文本（开火结果“无法被击伤”的详情）。</summary>
        public static string InvulnerableDetail(CampaignState s, RegionEnemyRecord e)
        {
            SilentMindPhase p = PhaseOf(s);
            if (e != null && NodeIndexOf(e.EnemyInstanceId) >= 0)
            {
                return GameText.Get("boss.silent.invulnerable.node");
            }
            return GameText.Get(p == SilentMindPhase.Hiding ? "boss.silent.invulnerable.hiding" : "boss.silent.invulnerable.silence");
        }

        // ── 每个模拟步（大厅载入时由室内地点调用；与观察无关）──

        public static void Tick(CampaignState s)
        {
            SilentBossRecord r = Record(s);
            if (r == null)
            {
                return;
            }
            long now = GameClock.Ticks;
            switch ((SilentMindPhase)r.Phase)
            {
                case SilentMindPhase.Hiding:
                    TickJumps(s, r, now);
                    break;
                case SilentMindPhase.SilenceWarn:
                    if (now >= r.PhaseEndTick)
                    {
                        TryTransition(s, SilentMindPhase.Silence);
                    }
                    break;
                case SilentMindPhase.Silence:
                    if (now >= r.PhaseEndTick)
                    {
                        TryTransition(s, SilentMindPhase.Exposed);
                    }
                    break;
            }
        }

        private static void TickJumps(CampaignState s, SilentBossRecord r, long now)
        {
            if (r.JumpTarget >= 0)
            {
                if (now < r.JumpAtTick)
                {
                    return;
                }
                RegionEnemyRecord target = Node(s, r.JumpTarget);
                if (target != null && target.IsAlive)
                {
                    r.RealNode = r.JumpTarget;
                }
                r.Jumps++;
                r.JumpTarget = -1;
                r.JumpAtTick = 0;
                r.NextJumpTick = now + GameClock.TicksFor(JumpSeconds);
                Revision++;
                return;
            }
            if (now < r.NextJumpTick)
            {
                return;
            }
            var alive = new List<int>(6);
            for (int i = 0; i < Nodes.Count; i++)
            {
                RegionEnemyRecord n = Node(s, i);
                if (i != r.RealNode && n != null && n.IsAlive)
                {
                    alive.Add(i);
                }
            }
            if (alive.Count == 0)
            {
                r.NextJumpTick = now + GameClock.TicksFor(JumpSeconds); // 只剩真节点：没有跳点了（打它）
                return;
            }
            uint h = Hash((uint)(s.World?.WorldSeed ?? s.RandomSeed), (uint)r.Attempt, 0x4A554D50u ^ (uint)r.Jumps);
            r.JumpTarget = alive[(int)(h % (uint)alive.Count)];
            r.JumpAtTick = now + GameClock.TicksFor(JumpWarnSeconds);
            JumpsStarted++;
            Revision++;
            Vector2 at = PartPosition(s, Nodes[r.JumpTarget].Id);
            FeedbackCues.RaiseLocated(FeedbackCueId.SilentRelayJump, at, NodeLabel(r.JumpTarget));
        }

        /// <summary>跳转途中目标节点被摧毁：跳转取消，主脑留在出发节点并现形 boss.silent.reveal_seconds 秒（FG09 第 10 节负向）。</summary>
        private static void CancelJump(CampaignState s, SilentBossRecord r)
        {
            long now = GameClock.Ticks;
            r.JumpTarget = -1;
            r.JumpAtTick = 0;
            r.Jumps++;
            r.RevealUntilTick = now + GameClock.TicksFor(RevealSeconds);
            r.NextJumpTick = now + GameClock.TicksFor(JumpSeconds);
            JumpsCancelled++;
            Revision++;
            Vector2 at = PartPosition(s, Nodes[r.RealNode].Id);
            string text = GameText.Format("boss.silent.hud.revealed", NodeLabel(r.RealNode), Mathf.CeilToInt(RevealSeconds).ToString());
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, at, text);
        }

        public static bool IsRevealed(CampaignState s) => (Record(s)?.RevealUntilTick ?? 0) > GameClock.Ticks;

        // ── 内核标志（阶段变了 / 读档 / 部件重建后同步；O(节点数)）──

        public static void SyncFlags(CombatSite site, CampaignState s)
        {
            if (site == null || s == null)
            {
                return;
            }
            SilentMindPhase p = PhaseOf(s);
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (site.TryGetEnemyUnit(Nodes[i].Id, out int unit))
                {
                    site.SetUnitFlag(unit, CombatUnitFlags.Invulnerable, p != SilentMindPhase.Hiding);
                }
            }
            if (CoreInstanceId != null && site.TryGetEnemyUnit(CoreInstanceId, out int core))
            {
                bool visible = p == SilentMindPhase.Magnetic || p == SilentMindPhase.SilenceWarn || p == SilentMindPhase.Silence || p == SilentMindPhase.Exposed;
                bool alive = Core(s)?.IsAlive ?? false;
                site.SetUnitFlag(core, CombatUnitFlags.Targetable, visible && alive);
                site.SetUnitFlag(core, CombatUnitFlags.Invulnerable, !(p == SilentMindPhase.Magnetic || p == SilentMindPhase.Exposed));
                site.SetUnitFlag(core, CombatUnitFlags.WeaponEnabled, (p == SilentMindPhase.Magnetic || p == SilentMindPhase.Exposed) && alive);
                site.SetUnitFlag(core, CombatUnitFlags.DeflectArmed, p == SilentMindPhase.Magnetic);
            }
        }

        // ── 全频静默 ──

        /// <summary>全频静默开始：信号若在大厅里的机器上就强制断链；大厅里的全部机器进入安全模式（原因“全频静默”）。之后的接入由 <see cref="IsSilencing"/> 拒绝。</summary>
        private static void ApplySilence(CampaignState s)
        {
            Signal.SignalUplinkService.CancelPendingForLink(GameText.Get("signal.link.reason_name.boss_silence"));
            int up = Signal.SignalUplinkService.CurrentMachine(s);
            if (up != 0 && MachineRegistry.TryGetRecord(up, out MachineRecord rec) && rec.RegionId == SiteId)
            {
                RegionControlSystem.ForRegion(SiteId)?.ForceBreak(RegionControlChangeReason.SignalLost);
                GameClock.SetDirectLocked(false);
            }
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m != null && m.IsAlive && m.RegionId == SiteId)
                {
                    Signal.SignalLinkService.EnterSafeModeFor(s, m.LogicId, Signal.SignalLinkBreakReason.BossSilence);
                }
            }
        }

        // ── 奖励 ──

        public static int RewardGrants { get; private set; }

        private static void GrantRewardsOnce(CampaignState s, Vector2 at)
        {
            SilentBossRecord r = Record(s);
            if (r == null || r.RewardsGranted)
            {
                return;
            }
            r.RewardsGranted = true;
            RewardGrants++;
            RegionEnemyRecord core = Core(s);
            if (core != null && EnemyDefs.TryGet(core.EnemyTypeId, out MechEnemy row) && !string.IsNullOrEmpty(row.Components) && row.Components != EnemyDefs.None)
            {
                string[] fw = row.Components.Split(',');
                // components 第一个 = 固定的核心固件（集群协议）；第二个 = 此前还没拿到才给（标记跳转）。
                if (fw.Length > 0)
                {
                    Primitive.PrimitiveInventory.GrantBossFirmware(s, "boss:" + SiteId + ":0", fw[0].Trim());
                }
                if (fw.Length > 1 && !Signal.FirmwareLibrary.IsObtained(s, fw[1].Trim()))
                {
                    Primitive.PrimitiveInventory.GrantBossFirmware(s, "boss:" + SiteId + ":1", fw[1].Trim());
                    NotificationCenter.Post("silent_boss", GameText.Get("boss.silent.notify.reward_mark"));
                }
                Economy.TestRangeService.NoteEnemyDefeated(s, row.Id);
            }
            int holder = Economy.LootPickService.NearestMachine(SiteId, at, 200f, 0);
            Economy.LootPickService.Grant(s, Economy.LootPickService.SourceBoss, EnemyDefs.FactionSilent, holder, SiteId, at);
            // 静默领地平定 → 主线“击败静默的首领”完成 → 关键材料“监听阵列核”进核心保管库（FG8-QST-01 的任务奖励）；剧情节点 leader:silent。
            WorldSim.TerritoryControlService.MarkLeaderDefeated(s, EnemyDefs.FactionSilent);
            NotificationCenter.Post("silent_boss", GameText.Get("boss.silent.notify.defeated"), new Vector3(at.x, 0f, at.y));
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
