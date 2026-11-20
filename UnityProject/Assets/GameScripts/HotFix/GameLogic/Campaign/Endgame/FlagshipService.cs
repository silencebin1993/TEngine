using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Env;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Endgame
{
    /// <summary>
    /// FG13-END-05 先遣旗舰“归乡号”（最终首领；FG11 FGR-END-022、FG19 FGR-EMG-161 / 165；验收 FGT-END-006 / 007）。唯一写入口（<see cref="FlagshipState"/> 与 fs_ 敌人记录）。
    /// 脚本化首领机制：只在这场战斗出现，不属于常态 AI。
    ///
    /// - 开战：切断投送（<see cref="BeachheadService.ClearedEventId"/>）后的第一个世界步。位置只由规划层决定（B25）：fs_ 点位在滩头模板 white_tide_beachhead 里，
    ///   经 <see cref="BeachheadService.TryPoint"/> 落到星球（同一锚点原点与朝向、同一就近挪格）；编成 = fg.TbEnemyEncounter 区域 <see cref="RegionId"/>。
    /// - 阶段一·轨道轰炸：编成 base（4 座校准信标）。旗舰悬在高空、不在场上（无法攻击）。每 flagship.bomb_interval_seconds 用内核轨道打击（<see cref="CombatSite.ScheduleOrbitalStrike"/>，
    ///   倒计时内地上画落点）轰炸竞技场里离降落区最近的机器，落点散布 = base + 已摧毁信标数 × per_beacon（越毁越不准；方向与距离按序号确定）。信标全毁 → 阶段二。
    /// - 阶段二·登陆要塞：编成 boss（旗舰要塞、2 座炮位、四类护盾节点）。节点 = 内核端口闸门（表列 flGate，FGR-EMG-161 四行表）：一切伤害不扣耐久，只有该族端口削减闸门，
    ///   到 0 当场击毁——判定全在内核（Main/Sim），这里只读剩余值给 HUD。要塞在节点 / 中继还在时不可伤（内核无敌位，每游戏秒按阶段重设）。4 个节点全破 → 阶段三。
    /// - 阶段三·根权限压制：编成 boss_wave（3 座压制中继）。每 flagship.seize_interval_seconds 夺取一台离存活中继最近的机器，走共用夺控状态机
    ///   <see cref="SignalSeizeService.Seize"/>（来源 = 根权限，直到阶段结束；玩家正接入 / 接入过渡中的那台永不夺取）；没有可夺目标 = 这一次空转。反向接入、击倒瘫痪、
    ///   反夺控广播都是共用状态机的既有行为。3 座中继全毁 → 被夺取的机器统一归还（瘫痪的恢复耐久）、核心暴露（阶段四·爆发窗口）。
    /// - 阶段四：要塞可伤；被摧毁 = 击退旗舰（事件账本 <see cref="DefeatedEventId"/> + <see cref="DefeatedEvent"/>，FG13-END-06 结局从这里开始）。
    /// - 每个阶段开始时自动存档（<see cref="SaveReason.FlagshipPhase"/>）。全灭（本阶段竞技场里出现过可用的机器，之后一台都没了）→ 从这一阶段开始重试：本阶段目标复原、
    ///   夺取的机器归还，再自动存档一次。核心被摧毁走家园失败页（写明读档后从第几阶段开始）。
    /// 只看统一时钟步数（暂停不走、倍速同比），与观察无关（星球内核一直在跑）。每步 O(1)；每游戏秒一次 O(旗舰记录 + 机器)；阶段切换一次 O(编成)。
    /// </summary>
    public static class FlagshipService
    {
        public const string RegionId = "white_tide_flagship";
        public const string Prefix = "fs_";
        public const string DefeatedEventId = "endgame_flagship_defeated";
        public const string PhaseEvent = "Endgame.FlagshipPhase";
        public const string DefeatedEvent = "Endgame.FlagshipDefeated";
        public const string NotifyType = RevealService.OrbitalNotifyType;
        public const int PhaseNone = 0, PhaseOrbital = 1, PhaseFortress = 2, PhaseRoot = 3, PhaseBurst = 4, PhaseDone = 5;

        /// <summary>阶段开始时的自动存档（自检可替换）。</summary>
        public static Func<SaveResult> PhaseSaveHandler = () => CampaignAutoSaveService.SaveAuto(SaveReason.FlagshipPhase, toast: false);

        public static int Revision { get; private set; }
        public static int PhaseSaves { get; private set; }
        public static int BombsScheduled { get; private set; }
        public static int Seizes { get; private set; }
        public static int Wipes { get; private set; }
        public static float LastScatter { get; private set; }
        public static Vector2 LastBombAt { get; private set; }

        public static void ResetCountersForTests()
        {
            PhaseSaves = BombsScheduled = Seizes = Wipes = 0;
            LastScatter = 0f;
            LastBombAt = default;
        }

        // ── 调参 ─────────────────────────────────────────────────────────

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) ? v : fallback;

        public static float BombFirst => Mathf.Max(0f, Tuning("flagship.bomb_first_seconds", 8f));
        public static float BombInterval => Mathf.Max(1f, Tuning("flagship.bomb_interval_seconds", 10f));
        public static float BombWarn => Mathf.Max(0.8f, Tuning("flagship.bomb_warn_seconds", 6f));
        public static float BombRadius => Mathf.Max(0.5f, Tuning("flagship.bomb_radius", 6f));
        public static float BombDamage => Mathf.Max(0f, Tuning("flagship.bomb_damage", 90f));
        public static float BombBuilding => Mathf.Clamp01(Tuning("flagship.bomb_building", 0.3f));
        public static float ScatterBase => Mathf.Max(0f, Tuning("flagship.bomb_scatter_base", 1.5f));
        public static float ScatterPerBeacon => Mathf.Max(0f, Tuning("flagship.bomb_scatter_per_beacon", 4f));
        public static float ArenaRadius => Mathf.Max(10f, Tuning("flagship.arena_radius", 70f));
        public static float SeizeFirst => Mathf.Max(0f, Tuning("flagship.seize_first_seconds", 20f));
        public static float SeizeInterval => Mathf.Max(1f, Tuning("flagship.seize_interval_seconds", 20f));

        // ── 读 ───────────────────────────────────────────────────────────

        public static FlagshipState StateOf(CampaignState s) => s?.Progress?.Flagship;
        public static int PhaseOf(CampaignState s) => StateOf(s)?.Phase ?? PhaseNone;
        public static bool Started(CampaignState s) => PhaseOf(s) != PhaseNone;
        public static bool Done(CampaignState s) => s != null && CampaignEventLedger.Contains(s, DefeatedEventId);
        public static bool Active(CampaignState s) => Started(s) && PhaseOf(s) != PhaseDone;

        public static bool IsFlagshipInstance(string id) => id != null && id.StartsWith(Prefix, StringComparison.Ordinal);

        public static List<EnemyEncounter> Encounters(string condition) => EnemyDefs.Encounters(RegionId, condition);

        /// <summary>全部编成行（三个条件按阶段顺序）。</summary>
        public static IEnumerable<EnemyEncounter> AllEncounters()
        {
            foreach (string c in new[] { EnemyDefs.ConditionBase, EnemyDefs.ConditionBoss, EnemyDefs.ConditionBossWave })
            {
                foreach (EnemyEncounter e in Encounters(c))
                {
                    yield return e;
                }
            }
        }

        public static string PhaseName(int phase) => GameText.Get("flagship.phase." + Mathf.Clamp(phase, 1, 4).ToString(CultureInfo.InvariantCulture));

        public static bool IsCoreRow(EnemyEncounter enc) => enc != null && EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy r) && r.Role == EnemyDefs.RoleBoss;

        public static bool IsNodeRow(EnemyEncounter enc) => enc != null && EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy r) && EnemyFleetParams.GateFamily(r) != CombatFleetConst.GateNone;

        /// <summary>这一阶段要清掉的目标行：阶段一 = 信标（base 的结构），二 = 护盾节点，三 = 中继（boss_wave），四 = 核心。</summary>
        public static bool IsGoalRow(int phase, EnemyEncounter enc) => phase switch
        {
            PhaseOrbital => enc.Condition == EnemyDefs.ConditionBase,
            PhaseFortress => enc.Condition == EnemyDefs.ConditionBoss && IsNodeRow(enc),
            PhaseRoot => enc.Condition == EnemyDefs.ConditionBossWave,
            PhaseBurst => IsCoreRow(enc),
            _ => false,
        };

        public static RegionEnemyRecord Find(CampaignState s, string id) => BeachheadService.Find(s, id);

        /// <summary>当前阶段目标的存活数与总数（O(编成行)）。</summary>
        public static int GoalsAlive(CampaignState s, int phase, out int total)
        {
            total = 0;
            int alive = 0;
            foreach (EnemyEncounter enc in AllEncounters())
            {
                if (!IsGoalRow(phase, enc))
                {
                    continue;
                }
                total++;
                RegionEnemyRecord r = Find(s, enc.Id);
                if (r != null && r.IsAlive)
                {
                    alive++;
                }
            }
            return alive;
        }

        /// <summary>阶段一的落点散布（米）：base + 已摧毁信标数 × per_beacon。</summary>
        public static float Scatter(CampaignState s)
        {
            int alive = GoalsAlive(s, PhaseOrbital, out int total);
            return ScatterBase + Math.Max(0, total - alive) * ScatterPerBeacon;
        }

        public static bool TryZone(CampaignState s, out Vector2 zone) => BeachheadService.TryPoint(s, BeachheadService.ZonePoint, out zone);

        private static CombatSite Site() => CombatSites.Get(HomeValleyLayout.RegionId);

        /// <summary>护盾节点的族别、剩余与总值（读内核端口闸门；不在场 / 已破 = false）。</summary>
        public static bool TryNode(CampaignState s, string id, out byte family, out float gate, out float max)
        {
            family = CombatFleetConst.GateNone;
            gate = max = 0f;
            CombatSite site = Site();
            return site != null && !site.IsDisposed && site.TryGetEnemyUnit(id, out int unit) && site.TryGetGate(unit, out family, out gate, out max);
        }

        public static string WeaknessText(byte family) => GameText.Get(family switch
        {
            CombatFleetConst.GateEm => "flagship.weak.em",
            CombatFleetConst.GateFluid => "flagship.weak.fluid",
            CombatFleetConst.GateFuse => "flagship.weak.fuse",
            _ => "flagship.weak.limiter",
        });

        private static string NameOf(EnemyEncounter enc) => EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy r) ? GameText.Get(r.NameKey) : enc.Id;

        /// <summary>首领 HUD 一行（阶段名 + 本阶段目标；阶段二逐个列出节点的护盾百分比与弱点；阶段一 / 三带下次轰炸 / 夺取倒计时）。</summary>
        public static string HudText(CampaignState s, long tick)
        {
            FlagshipState st = StateOf(s);
            if (st == null || st.Phase == PhaseNone)
            {
                return string.Empty;
            }
            if (st.Phase == PhaseDone)
            {
                return GameText.Get("flagship.hud.done");
            }
            string phase = PhaseName(st.Phase);
            int alive = GoalsAlive(s, st.Phase, out int total);
            switch (st.Phase)
            {
                case PhaseOrbital:
                    return GameText.Format("flagship.hud.1", phase, alive, total, Scatter(s).ToString("0.#", CultureInfo.InvariantCulture), Secs(st.NextBombTick, tick));
                case PhaseFortress:
                {
                    var parts = new List<string>(4);
                    foreach (EnemyEncounter enc in Encounters(EnemyDefs.ConditionBoss))
                    {
                        if (!IsNodeRow(enc))
                        {
                            continue;
                        }
                        RegionEnemyRecord r = Find(s, enc.Id);
                        if (r != null && r.IsAlive && TryNode(s, enc.Id, out byte fam, out float g, out float max) && max > 0f)
                        {
                            parts.Add(GameText.Format("flagship.hud.node", NameOf(enc), Mathf.CeilToInt(100f * g / max), WeaknessText(fam)));
                        }
                        else
                        {
                            parts.Add(GameText.Format("flagship.hud.node_down", NameOf(enc)));
                        }
                    }
                    return GameText.Format("flagship.hud.2", phase, alive, string.Join(" · ", parts));
                }
                case PhaseRoot:
                    return GameText.Format("flagship.hud.3", phase, alive, total, SeizedNow(st), Secs(st.NextSeizeTick, tick));
                default:
                    return GameText.Format("flagship.hud.4", phase, CorePercent(s));
            }
        }

        private static string Secs(long at, long tick) => at < 0 ? "—" : Mathf.CeilToInt(Math.Max(0, at - tick) / (float)GameClock.StepHz).ToString(CultureInfo.InvariantCulture);

        private static int SeizedNow(FlagshipState st)
        {
            int n = 0;
            foreach (int id in st.Seized)
            {
                if (SignalSeizeService.TryGetSeize(id, out _, out int src) && src == SignalSeizeService.SourceRoot)
                {
                    n++;
                }
            }
            return n;
        }

        private static int CorePercent(CampaignState s)
        {
            CombatSite site = Site();
            foreach (EnemyEncounter enc in Encounters(EnemyDefs.ConditionBoss))
            {
                if (IsCoreRow(enc) && site != null && site.TryGetEnemyUnit(enc.Id, out int unit) && site.TryGetUnitView(unit, out CombatUnitView v) && v.MaxHealth > 0f)
                {
                    return Mathf.CeilToInt(100f * v.Health / v.MaxHealth);
                }
            }
            return 0;
        }

        public static string QuestProgressText(CampaignState s)
        {
            FlagshipState st = StateOf(s);
            if (st == null || !Active(s))
            {
                return null;
            }
            int alive = GoalsAlive(s, st.Phase, out int total);
            string goal = st.Phase == PhaseBurst ? GameText.Format("flagship.hud.4", PhaseName(st.Phase), CorePercent(s)) : (total - alive).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
            return GameText.Format("quest.prog.flagship", PhaseName(st.Phase), goal);
        }

        /// <summary>失败页一行（先遣旗舰战进行中：读档后从当前阶段开始）；不在旗舰战 = 空。</summary>
        public static string FailureRetryText(CampaignState s) => Active(s) ? GameText.Format("failure.flagship_retry", PhaseName(PhaseOf(s))) : string.Empty;

        /// <summary>要塞 / 节点的悬停原因（为什么打不动）。</summary>
        public static string InvulnerableDetail(CampaignState s, RegionEnemyRecord e)
        {
            if (s == null || e == null)
            {
                return string.Empty;
            }
            if (TryNode(s, e.EnemyInstanceId, out byte fam, out _, out _))
            {
                return GameText.Format("flagship.invuln.gate", WeaknessText(fam));
            }
            if (!EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row) || row.Role != EnemyDefs.RoleBoss)
            {
                return string.Empty;
            }
            int phase = PhaseOf(s);
            return phase == PhaseFortress ? GameText.Format("flagship.invuln.nodes", GoalsAlive(s, PhaseFortress, out _))
                : phase == PhaseRoot ? GameText.Format("flagship.invuln.relays", GoalsAlive(s, PhaseRoot, out _)) : string.Empty;
        }

        // ── 写 ───────────────────────────────────────────────────────────

        /// <summary>世界模拟的一个固定步（WorldSimulation 在白潮滩头之后调用）。</summary>
        public static void WorldStep(CampaignState s, long tick)
        {
            FlagshipState st = StateOf(s);
            if (st == null || st.Phase == PhaseDone || HomeValleySoftlockGuard.IsCoreDestroyed(s))
            {
                return; // 核心被毁：失败页接管；读档后从存档里的阶段接着打
            }
            CombatSite site = Site();
            if (site == null || site.IsDisposed)
            {
                return;
            }
            if (st.Phase == PhaseNone)
            {
                if (BeachheadService.Done(s) && TryZone(s, out _))
                {
                    st.StartTick = tick;
                    BeginPhase(s, st, site, PhaseOrbital, tick, retry: false);
                }
                return;
            }
            if (tick % Math.Max(1, GameClock.StepHz) == 0)
            {
                EnsureSpawned(s, site, st.Phase);
                ApplyCoreGuard(s, site, st.Phase);
                if (CheckWipe(s, st, site, tick))
                {
                    return;
                }
                if (GoalsAlive(s, st.Phase, out int total) == 0 && total > 0)
                {
                    if (st.Phase == PhaseBurst)
                    {
                        Finish(s, st, tick);
                    }
                    else
                    {
                        BeginPhase(s, st, site, st.Phase + 1, tick, retry: false);
                    }
                    return;
                }
            }
            if (st.Phase == PhaseOrbital && st.NextBombTick >= 0 && tick >= st.NextBombTick)
            {
                Bomb(s, st, site, tick);
            }
            if (st.Phase == PhaseRoot && st.NextSeizeTick >= 0 && tick >= st.NextSeizeTick)
            {
                st.NextSeizeTick = tick + GameClock.TicksFor(SeizeInterval);
                TrySeize(s, st);
            }
        }

        /// <summary>进入阶段 <paramref name="phase"/>（或 <paramref name="retry"/> = 从这一阶段开始重试）：本阶段目标落位（重试时复原到满耐久 / 满闸门）、计时归零、
        /// 夺取的机器归还；阶段转换提示（通知 + 首领阶段提示音 + 事件）；步末自动存档。</summary>
        private static void BeginPhase(CampaignState s, FlagshipState st, CombatSite site, int phase, long tick, bool retry)
        {
            ReleaseSeized(st);
            st.Phase = phase;
            st.PhaseTick = tick;
            st.ArenaSeen = false;
            st.NextBombTick = phase == PhaseOrbital ? tick + GameClock.TicksFor(BombFirst) : -1;
            st.NextSeizeTick = phase == PhaseRoot ? tick + GameClock.TicksFor(SeizeFirst) : -1;
            string cond = phase switch
            {
                PhaseOrbital => EnemyDefs.ConditionBase,
                PhaseFortress => EnemyDefs.ConditionBoss,
                PhaseRoot => EnemyDefs.ConditionBossWave,
                _ => null,
            };
            if (cond != null)
            {
                SeedRows(s, site, cond, reset: retry);
            }
            else if (retry)
            {
                foreach (EnemyEncounter enc in Encounters(EnemyDefs.ConditionBoss))
                {
                    if (IsCoreRow(enc))
                    {
                        Reset(s, site, enc);
                    }
                }
            }
            ApplyCoreGuard(s, site, phase);
            Revision++;
            Vector2 c = TryZone(s, out Vector2 z) ? z : Vector2.zero;
            string text = retry ? GameText.Format("flagship.notify.wipe", PhaseName(phase)) : GameText.Get("flagship.notify.phase." + phase.ToString(CultureInfo.InvariantCulture));
            Log.Info($"[FlagshipService] {(retry ? "全灭重试" : "进入")}{PhaseName(phase)}");
            NotificationCenter.Post(NotifyType, text, new Vector3(c.x, 0f, c.y));
            FeedbackCues.RaiseLocated(FeedbackCueId.BossPhase, c, PhaseName(phase));
            CampaignObjectiveTracker.Recompute(s);
            GameEvent.Send(PhaseEvent);
            WorldSim.WorldSimulation.RunAfterStep(() =>
            {
                if (CampaignSession.Current == s && Active(s) && st.Phase == phase)
                {
                    PhaseSaves++;
                    PhaseSaveHandler?.Invoke();
                }
            });
        }

        /// <summary>按编成条件播种（已有记录且活着的不动；<paramref name="reset"/> = 全部复原到满耐久）。</summary>
        private static void SeedRows(CampaignState s, CombatSite site, string condition, bool reset)
        {
            foreach (EnemyEncounter enc in Encounters(condition))
            {
                RegionEnemyRecord r = Find(s, enc.Id);
                if (r == null || reset)
                {
                    Reset(s, site, enc);
                }
            }
        }

        /// <summary>一行编成复原：没有记录就新建，有就满耐久复活、回到点位；内核里旧单位撤掉重生（闸门回满、无敌位重设）。</summary>
        private static void Reset(CampaignState s, CombatSite site, EnemyEncounter enc)
        {
            if (!EnemyDefs.TryGet(enc.EnemyTypeId, out MechEnemy row) || !BeachheadService.TryPoint(s, enc.AnchorId, out Vector2 at))
            {
                EnemyDefs.ReportEncounterProblem(RegionId, enc.Id + " @ " + enc.AnchorId);
                return;
            }
            float hp = EnemyDefs.SeedHealth(s, row);
            RegionEnemyRecord rec = Find(s, enc.Id);
            if (rec == null)
            {
                rec = new RegionEnemyRecord { EnemyInstanceId = enc.Id, RegionId = HomeValleyLayout.RegionId, EnemyTypeId = row.Id };
                var list = new List<RegionEnemyRecord>(s.RegionEnemies ?? Array.Empty<RegionEnemyRecord>()) { rec };
                s.RegionEnemies = list.ToArray();
            }
            rec.Position = at;
            rec.Health = hp;
            rec.MaxHealth = hp;
            rec.IsAlive = true;
            rec.CycleCooldownRemaining = enc.InitialCycle;
            site.RemoveEnemy(enc.Id);
            Spawn(s, site, rec);
        }

        private static void Spawn(CampaignState s, CombatSite site, RegionEnemyRecord rec)
        {
            if (EnemySpawnBuilder.TryBuild(site, s, rec, null, out CombatSpawn spawn))
            {
                site.SpawnEnemy(rec, spawn);
            }
        }

        /// <summary>fs_ 记录还活着却不在内核里（没有快照的新内核等）→ 补回。只补到当前阶段为止出现过的条件。O(旗舰记录数)。</summary>
        private static void EnsureSpawned(CampaignState s, CombatSite site, int phase)
        {
            if (s.RegionEnemies == null)
            {
                return;
            }
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e == null || !e.IsAlive || e.RegionId != HomeValleyLayout.RegionId || !IsFlagshipInstance(e.EnemyInstanceId)
                    || (site.TryGetEnemyUnit(e.EnemyInstanceId, out int unit) && site.UnitExists(unit)))
                {
                    continue;
                }
                Spawn(s, site, e);
            }
        }

        /// <summary>要塞（职能 boss）只在阶段四可伤（节点 / 中继还在时不可伤）；炮位、节点、中继照常（节点另由内核端口闸门挡住其余伤害）。</summary>
        private static void ApplyCoreGuard(CampaignState s, CombatSite site, int phase)
        {
            foreach (EnemyEncounter enc in Encounters(EnemyDefs.ConditionBoss))
            {
                if (IsCoreRow(enc) && site.TryGetEnemyUnit(enc.Id, out int unit) && site.UnitExists(unit))
                {
                    site.SetUnitFlag(unit, CombatUnitFlags.Invulnerable, phase != PhaseBurst);
                }
            }
        }

        // ── 阶段一：延时轰炸 ──────────────────────────────────────────────

        private static void Bomb(CampaignState s, FlagshipState st, CombatSite site, long tick)
        {
            st.NextBombTick = tick + GameClock.TicksFor(BombInterval);
            if (!TryZone(s, out Vector2 zone) || !TryArenaTarget(s, site, zone, out Vector2 target))
            {
                return; // 竞技场里没有机器：这一次不轰炸
            }
            float scatter = Scatter(s);
            // 确定的散布：序号的黄金角方向 + 序号哈希的半径（读档后同一序号同一落点）
            uint h = (uint)(st.BombCount + 1) * 2654435761u;
            float angle = st.BombCount * 137.5f * Mathf.Deg2Rad;
            float dist = scatter * Mathf.Sqrt((h >> 8) / 16777216f);
            Vector2 at = target + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dist;
            st.BombCount++;
            if (site.ScheduleOrbitalStrike(at, BombRadius, BombDamage, BombWarn, BombBuilding) == 0)
            {
                return;
            }
            BombsScheduled++;
            LastScatter = scatter;
            LastBombAt = at;
            Revision++;
            NotificationCenter.Post(NotifyType, GameText.Format("flagship.notify.bomb", Mathf.CeilToInt(BombWarn), scatter.ToString("0.#", CultureInfo.InvariantCulture)),
                new Vector3(at.x, 0f, at.y));
        }

        /// <summary>竞技场里离降落区最近的可用机器的实时位置（并列按 LogicId，确定）。</summary>
        private static bool TryArenaTarget(CampaignState s, CombatSite site, Vector2 zone, out Vector2 at)
        {
            at = default;
            float best = float.MaxValue;
            int bestId = int.MaxValue;
            float r2 = ArenaRadius * ArenaRadius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (!Usable(m, site, out Vector2 p))
                {
                    continue;
                }
                float d = (p - zone).sqrMagnitude;
                if (d <= r2 && (d < best || (Mathf.Approximately(d, best) && m.LogicId < bestId)))
                {
                    best = d;
                    bestId = m.LogicId;
                    at = p;
                }
            }
            return bestId != int.MaxValue;
        }

        private static bool Usable(MachineRecord m, CombatSite site, out Vector2 p)
        {
            p = default;
            if (m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId || !site.TryGetMachineUnit(m.LogicId, out int unit) || !site.UnitExists(unit)
                || SignalSeizeService.IsSeized(m.LogicId))
            {
                return false;
            }
            p = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
            return true;
        }

        /// <summary>竞技场里可用（活着、在星球内核、没被夺取）的机器数。O(机器数)，每游戏秒一次。</summary>
        public static int ArenaMachines(CampaignState s)
        {
            CombatSite site = Site();
            if (site == null || !TryZone(s, out Vector2 zone))
            {
                return 0;
            }
            int n = 0;
            float r2 = ArenaRadius * ArenaRadius;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (Usable(m, site, out Vector2 p) && (p - zone).sqrMagnitude <= r2)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>家园里可用的机器数（不限竞技场）。O(机器数)，只在夺取那一刻。</summary>
        private static int HomeUsable(CombatSite site)
        {
            int n = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (Usable(m, site, out _))
                {
                    n++;
                }
            }
            return n;
        }

        private static bool CheckWipe(CampaignState s, FlagshipState st, CombatSite site, long tick)
        {
            int n = ArenaMachines(s);
            if (n > 0)
            {
                st.ArenaSeen = true;
                return false;
            }
            if (!st.ArenaSeen)
            {
                return false;
            }
            Wipes++;
            st.Retries++;
            BeginPhase(s, st, site, st.Phase, tick, retry: true);
            return true;
        }

        // ── 阶段三：根权限压制 ────────────────────────────────────────────

        private static readonly List<int> Cand = new List<int>(16);

        /// <summary>夺取一台离存活压制中继最近的机器（共用夺控状态机；玩家正接入 / 接入过渡中的永不夺取，由候选与 Seize 双重保证）。返回夺取的 LogicId（0 = 空转）。</summary>
        public static int TrySeize(CampaignState s, FlagshipState st)
        {
            st.SeizeCount++;
            Revision++;
            var relays = new List<Vector2>(3);
            CombatSite site = Site();
            foreach (EnemyEncounter enc in Encounters(EnemyDefs.ConditionBossWave))
            {
                RegionEnemyRecord r = Find(s, enc.Id);
                if (r != null && r.IsAlive)
                {
                    relays.Add(site != null && site.TryGetEnemyPosition(enc.Id, out Vector2 live) ? live : r.Position);
                }
            }
            SignalGhostService.Candidates(s, Cand);
            int pick = 0;
            float best = float.MaxValue;
            // 待用户复核（FG13-END-05）：不夺取玩家最后一台能战斗的机器——家园里可用（活着、在内核、没被夺取）的只剩 1 台时这一次空转，
            // 否则“只有 1 台机器”时夺取会直接变成全灭重试的死循环（负向：玩家只有 1 台机器）。
            if (site == null || HomeUsable(site) <= 1)
            {
                Cand.Clear();
            }
            foreach (int id in Cand)
            {
                if (!MachineRegistry.TryGetRecord(id, out MachineRecord m) || m.RegionId != HomeValleyLayout.RegionId || relays.Count == 0)
                {
                    continue;
                }
                Vector2 p = MachineRegistry.TryGetLivePosition(id, out Vector2 live) ? live : m.WorldPosition;
                foreach (Vector2 rp in relays)
                {
                    float d = (p - rp).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        pick = id;
                    }
                }
            }
            if (pick == 0 || SignalSeizeService.Seize(s, pick, SignalSeizeService.SourceRoot, 0f) != EmgSeizeResult.Ok)
            {
                if (st.SeizeIdle++ == 0)
                {
                    NotificationCenter.Post(NotifyType, GameText.Get("flagship.notify.no_target"));
                }
                return 0;
            }
            st.SeizeIdle = 0;
            Seizes++;
            var list = new List<int>(st.Seized) { pick };
            st.Seized = list.ToArray();
            return pick;
        }

        /// <summary>阶段结束 / 重试：根权限夺取的机器统一归还（瘫痪的恢复耐久，共用状态机 Release）。</summary>
        private static void ReleaseSeized(FlagshipState st)
        {
            foreach (int id in st.Seized)
            {
                if (SignalSeizeService.TryGetSeize(id, out _, out int src) && src == SignalSeizeService.SourceRoot)
                {
                    SignalSeizeService.Release(id);
                }
            }
            st.Seized = Array.Empty<int>();
            st.SeizeIdle = 0;
        }

        // ── 阵亡 / 结束 ───────────────────────────────────────────────────

        /// <summary>fs_ 单位被击毁（家园战斗规则的阵亡回调）：废料掉在原地（开搬运单）、本阶段目标写一条可定位的通知与击毁字幕。</summary>
        public static void OnPartKilled(CampaignState s, RegionEnemyRecord e)
        {
            if (s == null || e == null || !IsFlagshipInstance(e.EnemyInstanceId) || !EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row))
            {
                return;
            }
            if (row.ScrapLoot > 0)
            {
                GroundItemRecord g = HomeValleyCargo.SpawnGroundItem(s, HomeValleyLayout.RegionId, e.Position, CampaignEconomyLedger.ResourceScrap, row.ScrapLoot, e.EnemyInstanceId + ":scrap");
                if (g != null)
                {
                    HomeValleyWorkOrders.TryCreateHaulPool(s, g.GroundItemId);
                }
            }
            Revision++;
            int phase = PhaseOf(s);
            int alive = GoalsAlive(s, phase, out int total);
            string text = GameText.Format("flagship.notify.part_down", GameText.Get(row.NameKey), GameText.Format("quest.prog.flagship", PhaseName(phase),
                (total - alive).ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture)));
            FeedbackCues.RaiseLocated(FeedbackCueId.EnemyDestroyed, e.Position, text);
            NotificationCenter.Post(NotifyType, text, new Vector3(e.Position.x, 0f, e.Position.y));
        }

        private static void Finish(CampaignState s, FlagshipState st, long tick)
        {
            ReleaseSeized(st);
            st.Phase = PhaseDone;
            st.DoneTick = tick;
            st.NextBombTick = st.NextSeizeTick = -1;
            Revision++;
            if (!CampaignEventLedger.TryGrant(s, DefeatedEventId, "Endgame", s.PlaySeconds, "flagship"))
            {
                return;
            }
            Log.Info("[FlagshipService] 先遣旗舰的核心被摧毁：先遣舰队被击退");
            NotificationCenter.Post(NotifyType, GameText.Get("flagship.notify.defeated"));
            FeedbackCues.RaiseLocated(FeedbackCueId.BossDestroyed, TryZone(s, out Vector2 z) ? z : Vector2.zero, GameText.Get("flagship.notify.defeated"));
            CampaignObjectiveTracker.Recompute(s);
            GameEvent.Send(DefeatedEvent);
        }
    }
}
