using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG10-FAC-01（FG09 FGR-FAC-004 / 第 6 章反制表）：澄净阵营的反制——只在澄净自己的技术树内选对策，每个游戏日更新一次并在当天锁定。
    ///
    /// | 玩家常用 | 澄净的对策 | 落地 |
    /// |---|---|---|
    /// | 燃烧类（在用固件里带燃烧标签的比例 ≥ clarity.counter.fire_share） | 液氮压制：多派液氮泵车 | 据点展开追加液氮泵车；突袭反制行 clarity.fluid（标签 cryo） |
    /// | 引信与弹芯（引信类比例 ≥ clarity.counter.fuse_share） | 蜂群饱和：多派腐蚀蜂群 | 据点展开追加腐蚀蜂群；突袭反制行 clarity.fuse（标签 swarm） |
    /// | 电磁场控（电磁类比例 ≥ clarity.counter.em_share） | 绝缘涂层：带 adapt_insulate 的单位当天挂不上电击类标签 | 生成时写进免疫位；突袭反制行 clarity.em（标签 insulated） |
    ///
    /// “在用固件” = 存活机器当前蓝图版本的电路槽 + 炮塔蓝图的电路槽 + 陷阱发射器的固件（与突袭导演 <see cref="RaidDirectorService.TopDefenseCategory"/> 同一套计数，外加机器）。
    /// 优先级：燃烧 &gt; 引信 &gt; 电磁（同时满足取前者，确定）。锁定在日切那一步（<see cref="WorldStep"/>，只看游戏日，与观察无关）或当天第一次查询时做；
    /// 据点展开（TerritoryControlService）、突袭排定（RaidDirectorService.ChooseCounter）与攻城单位生成（SiegeService.SpawnOne）都读 <see cref="Locked"/>，
    /// 情报只读 <see cref="PeekLocked"/> / <see cref="Preview"/>（FGR-FAC-004“突袭在出发时按当天的状态生成；情报面板可以看到当前的反制和下一次更新的预测”）。
    /// </summary>
    public static class ClarityCounterService
    {
        public const string CounterNone = "";

        public static int LockCount { get; private set; }

        public static void ResetForTests()
        {
            LockCount = 0;
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

        /// <summary>每个世界模拟步：日切那一步重算并锁定当天的对策（O(1)；日切当步 O(机器 + 炮塔 + 防御建筑)）。第 1 天不在模拟步里锁（当天第一次查询时锁）。</summary>
        public static void WorldStep(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null || f.ClarityCounterDay == Today(s) || f.ClarityCounterDay == 0)
            {
                return;
            }
            Lock(s, f);
        }

        private static void Lock(CampaignState s, FactionState f)
        {
            string before = f.ClarityCounterId ?? CounterNone;
            bool first = f.ClarityCounterDay == 0;
            f.ClarityCounterId = Decide(s);
            f.ClarityCounterDay = Today(s);
            LockCount++;
            // 换对策时提示一次（第一次锁定、没有对策、对策没变都不提示；只在世界里有澄净领地时，避免第一幕前的噪音）。
            if (!first && f.ClarityCounterId != before && f.ClarityCounterId != CounterNone && HasClarity(s))
            {
                Notifications.NotificationCenter.Post("clarity_counter", GameText.Format("clarity.counter.notify", CounterName(f.ClarityCounterId)));
            }
        }

        private static bool HasClarity(CampaignState s) => WorldGen.WorldGenService.PlanFor(s)?.Find(EnemyDefs.FactionClarity) != null;

        /// <summary>当天锁定的对策（counter_cryo / counter_swarm / counter_insulate / 空串）。当天还没锁定时当场锁定一次（同一天之后不再变）。</summary>
        public static string Locked(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null)
            {
                return CounterNone;
            }
            if (f.ClarityCounterDay != Today(s))
            {
                Lock(s, f);
            }
            return f.ClarityCounterId ?? CounterNone;
        }

        /// <summary>界面用：当天锁定的对策，只读——今天已锁定就读锁定值，否则按同一规则预先算出将要锁定的值，不写状态。</summary>
        public static string PeekLocked(CampaignState s)
        {
            FactionState f = s?.Factions;
            if (f != null && f.ClarityCounterDay == Today(s))
            {
                return f.ClarityCounterId ?? CounterNone;
            }
            return Decide(s);
        }

        /// <summary>下一次日切的预测（情报“下一次更新的预测”）：按当前构筑算，不改状态。</summary>
        public static string Preview(CampaignState s) => Decide(s);

        /// <summary>今天澄净单位是否挂绝缘涂层（带 adapt_insulate 的单位挂不上电击类标签）。</summary>
        public static bool InsulateToday(CampaignState s) => s != null && Locked(s) == EnemyDefs.ConditionCounterInsulate;

        /// <summary>按当前在用固件决定对策（纯函数）。</summary>
        public static string Decide(CampaignState s)
        {
            if (s == null)
            {
                return CounterNone;
            }
            Shares(s, out float fire, out float fuse, out float em);
            if (fire > 0f && fire >= Tuning("clarity.counter.fire_share", 0.3f))
            {
                return EnemyDefs.ConditionCounterCryo;
            }
            if (fuse > 0f && fuse >= Tuning("clarity.counter.fuse_share", 0.4f))
            {
                return EnemyDefs.ConditionCounterSwarm;
            }
            if (em > 0f && em >= Tuning("clarity.counter.em_share", 0.3f))
            {
                return EnemyDefs.ConditionCounterInsulate;
            }
            return CounterNone;
        }

        private static readonly int[] CategoryScratch = new int[4];

        /// <summary>
        /// 在用固件里 燃烧标签 / 引信类 / 电磁类 各占多少（分母 = 在用固件总枚数；没有固件全为 0）。
        /// 机器：存活机器当前蓝图版本的电路槽；防线：炮塔蓝图 + 陷阱（与突袭导演同一套计数）。O(机器 × 9 + 炮塔 × 9 + 防御建筑)，只在锁定与情报刷新时算。
        /// </summary>
        public static void Shares(CampaignState s, out float fire, out float fuse, out float em)
        {
            int total = 0, nFire = 0, nFuse = 0, nEm = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                foreach (string id in SlotsOf(s, m.BlueprintId, m.BlueprintVersion))
                {
                    Count(id, ref total, ref nFire, ref nFuse, ref nEm);
                }
            }
            // 防线：突袭导演的类别计数（炮塔 + 陷阱）；燃烧标签另数一遍。
            RaidDirectorService.TopDefenseCategory(s, CategoryScratch);
            int defense = CategoryScratch[0] + CategoryScratch[1] + CategoryScratch[2] + CategoryScratch[3];
            total += defense;
            nFuse += CategoryScratch[0];
            nEm += CategoryScratch[3];
            nFire += DefenseFireCount(s);
            fire = total > 0 ? nFire / (float)total : 0f;
            fuse = total > 0 ? nFuse / (float)total : 0f;
            em = total > 0 ? nEm / (float)total : 0f;
        }

        private static void Count(string id, ref int total, ref int fire, ref int fuse, ref int em)
        {
            if (string.IsNullOrEmpty(id) || !FirmwareKinds.IsFirmware(id))
            {
                return;
            }
            total++;
            if (HasFireTag(id))
            {
                fire++;
            }
            switch (FirmwareKinds.CategoryOf(id))
            {
                case FirmwareCategory.Fuse:
                    fuse++;
                    break;
                case FirmwareCategory.Electromagnetic:
                    em++;
                    break;
            }
        }

        private static bool HasFireTag(string id)
        {
            string[] tags = FirmwareKinds.TagsOf(id);
            return tags != null && Array.IndexOf(tags, "Fire") >= 0;
        }

        private static int DefenseFireCount(CampaignState s)
        {
            int n = 0;
            foreach (TurretRecord r in TurretService.All(s))
            {
                if (r == null || string.IsNullOrEmpty(r.BlueprintId))
                {
                    continue;
                }
                foreach (string id in SlotsOf(s, r.BlueprintId, r.BlueprintVersion))
                {
                    if (!string.IsNullOrEmpty(id) && FirmwareKinds.IsFirmware(id) && HasFireTag(id))
                    {
                        n++;
                    }
                }
            }
            foreach (DefenseRecord dr in DefenseService.All(s))
            {
                if (dr != null && !string.IsNullOrEmpty(dr.TrapFirmware) && FirmwareKinds.IsFirmware(dr.TrapFirmware) && HasFireTag(dr.TrapFirmware))
                {
                    n++;
                }
            }
            return n;
        }

        private static IEnumerable<string> SlotsOf(CampaignState s, string blueprintId, int version)
        {
            BlueprintRecord rec = BlueprintEditorService.Find(s, blueprintId);
            foreach (BlueprintVersionRecord v in rec?.Versions ?? Array.Empty<BlueprintVersionRecord>())
            {
                if (v != null && v.Version == version)
                {
                    return v.CircuitSlotContentIds ?? Array.Empty<string>();
                }
            }
            return Array.Empty<string>();
        }

        // ─────────────────────────────── 落地 ───────────────────────────────

        /// <summary>对策追加的单位（fg.TbRaidUnit.id）：液氮压制 → 液氮泵车，蜂群饱和 → 腐蚀蜂群；绝缘涂层与没有对策 → null（不加单位）。按编成表的反制标签找，不认识敌人 ID。</summary>
        public static RaidUnitDef ExtraUnit(string counterId)
        {
            string tag = counterId switch
            {
                EnemyDefs.ConditionCounterCryo => "cryo",
                EnemyDefs.ConditionCounterSwarm => "swarm",
                _ => null,
            };
            if (tag == null)
            {
                return null;
            }
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                if (u != null && u.Faction == EnemyDefs.FactionClarity && u.HasTag(tag))
                {
                    return u;
                }
            }
            return null;
        }

        /// <summary>每次展开追加几台对策单位（clarity.counter.extra_units）。</summary>
        public static int ExtraUnitCount => Math.Max(1, (int)Math.Round(Tuning("clarity.counter.extra_units", 2f)));

        /// <summary>突袭反制行（fg.TbRaidCounter.id）：当天锁定的对策映射到 clarity.fluid / clarity.fuse / clarity.em；没有对策 → null。</summary>
        public static RaidCounter RaidCounterFor(string counterId) => RaidCatalog.CounterById(counterId switch
        {
            EnemyDefs.ConditionCounterCryo => "clarity.fluid",
            EnemyDefs.ConditionCounterSwarm => "clarity.fuse",
            EnemyDefs.ConditionCounterInsulate => "clarity.em",
            _ => null,
        });

        public static string CounterName(string counterId) => GameText.Get(counterId switch
        {
            EnemyDefs.ConditionCounterCryo => "clarity.counter.cryo",
            EnemyDefs.ConditionCounterSwarm => "clarity.counter.swarm",
            EnemyDefs.ConditionCounterInsulate => "clarity.counter.insulate",
            _ => "clarity.counter.none",
        });

        /// <summary>FGR-FAC-004：情报“敌方反制预览”里澄净的一行（今天锁定 + 下一次日切预测）。</summary>
        public static string IntelLine(string locked, string next) =>
            GameText.Format("intel.counter.clarity", GameText.Get("world.faction.clarity"), CounterName(locked), CounterName(next));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
