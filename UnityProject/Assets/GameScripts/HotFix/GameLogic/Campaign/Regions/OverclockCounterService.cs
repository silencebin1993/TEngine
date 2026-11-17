using System;
using System.Collections.Generic;
using GameConfig.fg;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Emergence;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Signal;
using GameLogic.Localization;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// FG11-FAC-01（FG09 FGR-FAC-004 / 第 7 章反制表；FGT-FAC-005）：超频阵营的反制——只在超频自己的技术树内选对策，每个游戏日更新一次并在当天锁定。
    ///
    /// | 玩家常用 | 超频的对策 | 落地 |
    /// |---|---|---|
    /// | 限制器与击发逻辑（在用固件写语法族 grm.*（grm.trig 除外）的比例 ≥ overclock.counter.limiter_share） | 镜像压制：多派镜像体 | 据点展开追加镜像体；突袭反制行 overclock.limiter（标签 mirror） |
    /// | 流体改道（写物质族 mat.* 的比例 ≥ overclock.counter.fluid_share） | 高热蒸发：多派高热单位 | 据点展开追加熔断者；突袭反制行 overclock.fluid（标签 hot） |
    /// | 引信与弹芯（写 en.kinetic / grm.trig 的比例 ≥ overclock.counter.fuse_share） | 猎犬群：多派超频猎犬 | 据点展开追加超频猎犬；突袭反制行 overclock.fuse（标签 hound） |
    ///
    /// 与 <see cref="ClarityCounterService"/> 同一套口径（ADR ⑭ #12、FGR-EMG-160）：只看声明的写端口，不看固件 ID、标签与类别；一枚固件只归一族，按 流体 → 引信 → 限制器 取第一个命中。
    /// 优先级：限制器 &gt; 流体 &gt; 引信（同时满足取前者，确定）。锁定在日切那一步（<see cref="WorldStep"/>）或当天第一次查询时做；据点展开、突袭排定读 <see cref="Locked"/>，情报只读 <see cref="Preview"/>。
    /// </summary>
    public static class OverclockCounterService
    {
        public const string CounterNone = "";
        public const string CounterLimiter = "overclock.limiter";
        public const string CounterFluid = "overclock.fluid";
        public const string CounterFuse = "overclock.fuse";

        public static int LockCount { get; private set; }

        /// <summary>自检读数：超频驻军按反制换上对策单位的次数。</summary>
        public static int Applied { get; private set; }

        public static void ResetForTests()
        {
            LockCount = 0;
            Applied = 0;
        }

        internal static void NoteApplied() => Applied++;

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
            if (f == null || f.OverclockCounterDay == Today(s) || f.OverclockCounterDay == 0)
            {
                return;
            }
            Lock(s, f);
        }

        private static void Lock(CampaignState s, FactionState f)
        {
            string before = f.OverclockCounterId ?? CounterNone;
            bool first = f.OverclockCounterDay == 0;
            f.OverclockCounterId = Decide(s);
            f.OverclockCounterDay = Today(s);
            LockCount++;
            if (!first && f.OverclockCounterId != before && f.OverclockCounterId != CounterNone && Visible(s))
            {
                Notifications.NotificationCenter.Post("overclock_counter", GameText.Format("overclock.counter.notify", CounterName(f.OverclockCounterId)));
            }
        }

        /// <summary>超频领地在地图上显示（第二幕起，或玩家已经揭开过它的危害）：情报行与换对策通知只在这之后出现（第一幕不剧透）。</summary>
        public static bool Visible(CampaignState s)
        {
            WorldGen.PlannedTerritory t = WorldGen.WorldGenService.PlanFor(s)?.Find(EnemyDefs.FactionOverclock);
            return t != null && Env.HomeExplorationService.TerritoryVisible(s, t);
        }

        /// <summary>当天锁定的对策（overclock.limiter / fluid / fuse / 空串）。当天还没锁定时当场锁定一次。</summary>
        public static string Locked(CampaignState s)
        {
            FactionState f = F(s);
            if (f == null)
            {
                return CounterNone;
            }
            if (f.OverclockCounterDay != Today(s))
            {
                Lock(s, f);
            }
            return f.OverclockCounterId ?? CounterNone;
        }

        /// <summary>下一次日切的预测：按当前构筑算，不改状态。</summary>
        public static string Preview(CampaignState s) => Decide(s);

        /// <summary>按当前在用固件决定对策（纯函数）。</summary>
        public static string Decide(CampaignState s)
        {
            if (s == null)
            {
                return CounterNone;
            }
            Shares(s, out float limiter, out float fluid, out float fuse);
            if (limiter > 0f && limiter >= Tuning("overclock.counter.limiter_share", 0.4f))
            {
                return CounterLimiter;
            }
            if (fluid > 0f && fluid >= Tuning("overclock.counter.fluid_share", 0.3f))
            {
                return CounterFluid;
            }
            if (fuse > 0f && fuse >= Tuning("overclock.counter.fuse_share", 0.3f))
            {
                return CounterFuse;
            }
            return CounterNone;
        }

        /// <summary>在用固件里 限制器 / 流体 / 引信 三个端口族各占多少（分母 = 在用固件总枚数）。同 <see cref="ClarityCounterService.Shares"/> 的“在用固件”口径。</summary>
        public static void Shares(CampaignState s, out float limiter, out float fluid, out float fuse)
        {
            int total = 0, nLim = 0, nFluid = 0, nFuse = 0;
            foreach (MachineRecord m in MachineRegistry.AllRecords)
            {
                if (m == null || !m.IsAlive || string.IsNullOrEmpty(m.BlueprintId))
                {
                    continue;
                }
                foreach (string id in FirmwareOf(s, m.BlueprintId, m.BlueprintVersion))
                {
                    Count(id, ref total, ref nLim, ref nFluid, ref nFuse);
                }
            }
            foreach (TurretRecord r in TurretService.All(s))
            {
                if (r == null || string.IsNullOrEmpty(r.BlueprintId))
                {
                    continue;
                }
                foreach (string id in FirmwareOf(s, r.BlueprintId, r.BlueprintVersion))
                {
                    Count(id, ref total, ref nLim, ref nFluid, ref nFuse);
                }
            }
            foreach (DefenseRecord dr in DefenseService.All(s))
            {
                if (dr != null)
                {
                    Count(dr.TrapFirmware, ref total, ref nLim, ref nFluid, ref nFuse);
                }
            }
            limiter = total > 0 ? nLim / (float)total : 0f;
            fluid = total > 0 ? nFluid / (float)total : 0f;
            fuse = total > 0 ? nFuse / (float)total : 0f;
        }

        private static void Count(string id, ref int total, ref int lim, ref int fluid, ref int fuse)
        {
            if (string.IsNullOrEmpty(id) || !FirmwareKinds.IsFirmware(id))
            {
                return;
            }
            total++;
            switch (PortGroupOf(id))
            {
                case PortGroup.Limiter:
                    lim++;
                    break;
                case PortGroup.Fluid:
                    fluid++;
                    break;
                case PortGroup.Fuse:
                    fuse++;
                    break;
            }
        }

        private enum PortGroup : byte
        {
            None, Limiter, Fluid, Fuse,
        }

        private static readonly Dictionary<string, PortGroup> GroupCache = new Dictionary<string, PortGroup>(StringComparer.Ordinal);
        private static int _groupRevision = -1;

        /// <summary>这枚固件（混合固件取两父的声明）归哪个端口族。按 EmgCatalog.Revision 缓存，只看写端口（FGR-EMG-013）。</summary>
        private static PortGroup PortGroupOf(string id)
        {
            if (_groupRevision != EmgCatalog.Revision)
            {
                GroupCache.Clear();
                _groupRevision = EmgCatalog.Revision;
            }
            if (GroupCache.TryGetValue(id, out PortGroup cached))
            {
                return cached;
            }
            EmgCompiled c = EmgCatalog.Current;
            var owners = new HashSet<int>();
            foreach (string o in FirmwareKinds.ExpandMixed(new[] { id }))
            {
                if (c.OwnerIndex.TryGetValue(o, out int oi))
                {
                    owners.Add(oi);
                }
            }
            bool fluid = false, fuse = false, limiter = false;
            foreach (EmgDeclRow d in c.Decls)
            {
                if (d.Dir != EmgDirs.Write || !owners.Contains(d.Owner) || !c.PortIdByCode.TryGetValue(d.Port, out string port))
                {
                    continue;
                }
                EmgFamily family = EmgCatalog.FamilyOf(port);
                fluid |= port.StartsWith("mat.", StringComparison.Ordinal);
                fuse |= port == "en.kinetic" || port == "grm.trig";
                limiter |= family == EmgFamily.Grammar && port != "grm.trig";
            }
            PortGroup group = fluid ? PortGroup.Fluid : fuse ? PortGroup.Fuse : limiter ? PortGroup.Limiter : PortGroup.None;
            GroupCache[id] = group;
            return group;
        }

        private static IEnumerable<string> FirmwareOf(CampaignState s, string blueprintId, int version)
        {
            BlueprintRecord rec = BlueprintEditorService.Find(s, blueprintId);
            foreach (BlueprintVersionRecord v in rec?.Versions ?? Array.Empty<BlueprintVersionRecord>())
            {
                if (v != null && v.Version == version)
                {
                    return v.OrderedFirmwareIds ?? Array.Empty<string>();
                }
            }
            return Array.Empty<string>();
        }

        // ─────────────────────────────── 落地 ───────────────────────────────

        /// <summary>对策追加的单位（fg.TbRaidUnit.id）：按编成表的反制标签找（mirror / hot / hound），不认敌人 ID。没有对策 → null。</summary>
        public static RaidUnitDef ExtraUnit(string counterId)
        {
            string tag = counterId switch
            {
                CounterLimiter => "mirror",
                CounterFluid => "hot",
                CounterFuse => "hound",
                _ => null,
            };
            if (tag == null)
            {
                return null;
            }
            foreach (RaidUnitDef u in RaidCatalog.Units)
            {
                if (u != null && u.Faction == EnemyDefs.FactionOverclock && u.HasTag(tag))
                {
                    return u;
                }
            }
            return null;
        }

        /// <summary>每次展开追加几台对策单位（overclock.counter.extra_units）。</summary>
        public static int ExtraUnitCount => Math.Max(1, (int)Math.Round(Tuning("overclock.counter.extra_units", 2f)));

        /// <summary>突袭反制行（fg.TbRaidCounter.id 与对策 ID 同名）；没有对策 → null。</summary>
        public static RaidCounter RaidCounterFor(string counterId) => string.IsNullOrEmpty(counterId) ? null : RaidCatalog.CounterById(counterId);

        public static string CounterName(string counterId) => GameText.Get(counterId switch
        {
            CounterLimiter => "raid.counter.overclock.limiter.name",
            CounterFluid => "raid.counter.overclock.fluid.name",
            CounterFuse => "raid.counter.overclock.fuse.name",
            _ => "overclock.counter.none",
        });

        /// <summary>FGR-FAC-004：情报“敌方反制预览”里超频的一行（今天锁定 + 下一次日切预测）。</summary>
        public static string IntelLine(string locked, string next) =>
            GameText.Format("intel.counter.overclock", GameText.Get("world.faction.overclock"), CounterName(locked), CounterName(next));

        private static float Tuning(string id, float fallback) => GridContent.TryGetTuning(id, out float v) && v > 0f ? v : fallback;
    }
}
