using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG9-FAC-01（FG09 FGR-FAC-004 / 第 4 章“反制”）：静默阵营的反制——只在静默自己的技术树内选对策，每个游戏日更新一次并在当天锁定。
    ///
    /// | 玩家常用 | 静默的对策 | 编成条件 |
    /// |---|---|---|
    /// | 引信与弹芯（存活机器装这一类固件的比例 ≥ silent.counter.fuse_share） | 增加磁暴棱柱（偏折射弹） | counter_deflect |
    /// | 核心固件频繁发动（前一个游戏日发动次数 ≥ silent.counter.core_fire_per_day） | 增加监听节点和寂听者 | counter_listen |
    /// | 电磁场控（比例 ≥ silent.counter.em_share） | 增加干扰机 | counter_jam |
    ///
    /// 优先级：核心固件 &gt; 引信 &gt; 电磁（同时满足时取前者，确定）。锁定在日切那一步做（<see cref="WorldStep"/>，只看游戏日，与观察无关）；
    /// 界面与情报只读 <see cref="Locked"/> / <see cref="Preview"/>，不改状态。突袭的阵营反制走突袭导演自己的 fg.TbRaidCounter（静默：引信 → 磁暴棱柱、电磁 → 干扰机）。
    /// 另外承担监听节点的“暴露翻倍”（<see cref="OnCoreFired"/>）。
    /// </summary>
    public static class SilentCounterService
    {
        public const string CounterNone = "";

        public static int LockCount { get; private set; }
        public static int AmplifiedFires { get; private set; }

        public static void ResetForTests()
        {
            LockCount = 0;
            AmplifiedFires = 0;
        }

        private static FactionState F(CampaignState s)
        {
            if (s == null)
            {
                return null;
            }
            if (s.Factions == null)
            {
                CampaignFgStateDomains.EnsureAll(s);
            }
            return s.Factions;
        }

        private static int Today(CampaignState s) => Math.Max(1, s?.Clock?.Day ?? 1);

        /// <summary>日切：把“当天”的核心固件计数滚到“前一天”。</summary>
        private static void Roll(CampaignState s, FactionState f)
        {
            int day = Today(s);
            if (f.CoreFireDay == day)
            {
                return;
            }
            f.CoreFiresYesterday = f.CoreFireDay == day - 1 ? f.CoreFiresToday : 0;
            f.CoreFiresToday = 0;
            f.CoreFireDay = day;
        }

        /// <summary>每个世界模拟步：日切那一步重算并锁定当天的对策（O(1)；日切当步 O(存活机器)）。</summary>
        public static void WorldStep(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null || f.SilentCounterDay == Today(s))
            {
                return;
            }
            Roll(s, f);
            f.SilentCounterId = Compute(s);
            f.SilentCounterDay = Today(s);
            LockCount++;
        }

        /// <summary>当天锁定的对策（编成条件；没有对策为空串）。日切之前还没锁定时当场锁定一次（同一天之后不再变）。</summary>
        public static string Locked(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null)
            {
                return CounterNone;
            }
            if (f.SilentCounterDay != Today(s))
            {
                WorldStep(s);
            }
            return f.SilentCounterId ?? CounterNone;
        }

        /// <summary>下一次日切的预测（情报“下一次更新的预测”）：按当前构筑与今天的核心固件次数算，不改状态。</summary>
        public static string Preview(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null)
            {
                return CounterNone;
            }
            int coreFires = f.CoreFireDay == Today(s) ? f.CoreFiresToday : 0;
            return Decide(s, coreFires);
        }

        /// <summary>按前一个游戏日的核心固件次数与当前构筑决定对策（纯函数）。</summary>
        public static string Compute(CampaignState s)
        {
            FactionState f = F(s);
            return f == null ? CounterNone : Decide(s, f.CoreFiresYesterday);
        }

        private static string Decide(CampaignState s, int coreFires)
        {
            if (coreFires >= Tuning("silent.counter.core_fire_per_day", 6f))
            {
                return EnemyDefs.ConditionCounterListen;
            }
            BuildShares(s, out float fuse, out float em);
            if (fuse >= Tuning("silent.counter.fuse_share", 0.4f))
            {
                return EnemyDefs.ConditionCounterDeflect;
            }
            if (em >= Tuning("silent.counter.em_share", 0.4f))
            {
                return EnemyDefs.ConditionCounterJam;
            }
            return CounterNone;
        }

        /// <summary>存活机器里装着引信与弹芯 / 电磁场控类固件的比例（按 AI 配置的装配解析；O(存活机器)，只在日切与情报刷新时）。</summary>
        public static void BuildShares(CampaignState s, out float fuseShare, out float emShare)
        {
            int total = 0, fuse = 0, em = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                MachineCombatResolution res = MachineLoadoutRegistry.ResolveForAi(s, m.LogicId, s.RandomSeed);
                if (!res.Success || res.Preview == null)
                {
                    continue;
                }
                total++;
                bool hasFuse = false, hasEm = false;
                foreach (string fw in CombatSite.ReadingFirmware(res.Preview))
                {
                    FirmwareCategory c = FirmwareKinds.CategoryOf(fw);
                    hasFuse |= c == FirmwareCategory.Fuse;
                    hasEm |= c == FirmwareCategory.Electromagnetic;
                }
                fuse += hasFuse ? 1 : 0;
                em += hasEm ? 1 : 0;
            }
            fuseShare = total > 0 ? fuse / (float)total : 0f;
            emShare = total > 0 ? em / (float)total : 0f;
        }

        /// <summary>界面用（入口面板 / 悬停说明）：当天锁定的对策，只读——今天已锁定就读锁定值，日切那一步之前（还没锁）按同一规则预先算出将要锁定的值，不写状态。</summary>
        public static string PeekLocked(CampaignState s)
        {
            FactionState f = s?.Factions;
            if (f == null)
            {
                return CounterNone;
            }
            if (f.SilentCounterDay == Today(s))
            {
                return f.SilentCounterId ?? CounterNone;
            }
            int yesterday = f.CoreFireDay == Today(s) ? f.CoreFiresYesterday : f.CoreFireDay == Today(s) - 1 ? f.CoreFiresToday : 0;
            return Decide(s, yesterday);
        }

        /// <summary>FGR-FAC-004：情报“敌方反制预览”里地下中继大厅的一行（产出时的当天锁定 + 下一次日切预测）。</summary>
        public static string IntelLine(string locked, string next) =>
            GameText.Format("intel.counter.silent", InteriorSiteService.SurfaceName(RelayHallLayout.SurfaceId), CounterName(locked), CounterName(next));

        public static string CounterName(string counterId) => GameText.Get(counterId switch
        {
            EnemyDefs.ConditionCounterDeflect => "silent.counter.deflect",
            EnemyDefs.ConditionCounterListen => "silent.counter.listen",
            EnemyDefs.ConditionCounterJam => "silent.counter.jam",
            _ => "silent.counter.none",
        });

        // ── 监听节点（FG09 第 4 章：范围内你的核心固件发动时，暴露翻倍）──

        /// <summary>
        /// 核心固件发动一次（信号接入服务在开始冷却、计过一次暴露之后调用）：当天计数 +1；发动的机器在某个存活监听节点（标签 listen_amp）的范围里时，
        /// 暴露再按（倍率 − 1）计——倍率 2 = 翻倍。O(该地点的敌人记录)，只在发动那一刻。
        /// </summary>
        public static void OnCoreFired(CampaignState s, string firmwareId, bool raw, int logicId)
        {
            FactionState f = F(s);
            if (f == null)
            {
                return;
            }
            Roll(s, f);
            f.CoreFiresToday++;
            float amp = ListenAmpAt(s, logicId);
            int extra = Mathf.RoundToInt(amp) - 1;
            for (int i = 0; i < extra; i++)
            {
                CampaignExposureLedger.GrantCoreFire(s, firmwareId, raw);
                AmplifiedFires++;
            }
        }

        /// <summary>这台机器此刻所在位置的监听倍率（不在任何存活监听节点范围里 = 1）。</summary>
        public static float ListenAmpAt(CampaignState s, int logicId)
        {
            if (s?.RegionEnemies == null || logicId <= 0 || !MachineRegistry.TryGetRecord(logicId, out MachineRecord m) || m == null)
            {
                return 1f;
            }
            Vector2 pos = MachineRegistry.TryGetLivePosition(logicId, out Vector2 live) ? live : m.WorldPosition;
            CombatSite site = CombatSites.Get(m.RegionId);
            float best = 1f;
            foreach (RegionEnemyRecord e in s.RegionEnemies)
            {
                if (e == null || !e.IsAlive || e.RegionId != m.RegionId || !EnemyDefs.TryGet(e.EnemyTypeId, out MechEnemy row)
                    || !EnemyDefs.HasTag(row, EnemyDefs.TagListenAmp) || row.EffectRange <= 0f)
                {
                    continue;
                }
                Vector2 at = site != null && site.TryGetEnemyPosition(e.EnemyInstanceId, out Vector2 p) ? p : e.Position;
                if ((at - pos).sqrMagnitude <= row.EffectRange * row.EffectRange)
                {
                    best = Mathf.Max(best, Mathf.Max(1f, row.EffectAmount));
                }
            }
            return best;
        }

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
