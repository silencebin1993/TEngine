using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.WorldGen;
using GameConfig.fg;
using GameLogic.Campaign.Grid;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG0-ARCH-05（FGR-GEN-001～003、010、011、020、050～052、060～062；FGR-ARC-013、014）：世界生成的服务入口。
    ///
    /// - 新战役：<see cref="InitializeNewWorld"/> 写入生成器版本与世界设置（种子在 CampaignState.CreateNew 时已定）。
    /// - 地形来源：<see cref="CreateSource"/> 按（种子, 版本, 世界设置, 表面, 核心落点）组装内核参数；旧存档（GridState.TerrainSourceId
    ///   = prototype-v1）仍用 FG0-ARCH-04 的原型来源（生成器版本 0 的旧路径，FGR-GEN-061）。
    /// - 规划层：<see cref="PlanFor"/> 按种子与世界设置计算，读档时重算，不存档（FGR-GEN-020）。
    /// - 表面：地球（星球）由 <see cref="HomeGridService"/> 持有；室内表面由这里按需建图（FGR-GEN-010 / FGR-ARC-014）。
    /// - 存档：<see cref="CaptureDiffs"/> 在写盘前把各表面被修改的区块写进 WorldGenState.ChunkDiffs（只存修改，FGR-GEN-060）；
    ///   不认识的表面（例如以后新增的星球）的差异原样保留（FGR-GEN-011）。<see cref="ValidateForLoad"/> 在读档时拒绝本版本无法生成的
    ///   存档（生成器版本更新）与损坏的差异编码。
    /// 玩法随机流完全不参与世界生成：生成只读 WorldGenState 与表数据（FGR-GEN-003 / FGR-ARC-010）。
    /// </summary>
    public static class WorldGenService
    {
        private static CampaignState _planState;
        private static WorldPlan _plan;
        private static string _planKey;

        private static CampaignState _interiorState;
        private static readonly Dictionary<string, HomeGridMap> InteriorMaps = new Dictionary<string, HomeGridMap>(StringComparer.Ordinal);
        private static readonly Dictionary<string, WorldChunkStreamer> InteriorStreamers = new Dictionary<string, WorldChunkStreamer>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> InteriorKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        // ── 新战役 ─────────────────────────────────────────────────────────────

        /// <summary>新战役的世界域：当前生成器版本 + 默认世界设置。种子由 CampaignState.CreateNew 写入。</summary>
        public static void InitializeNewWorld(WorldGenState world)
        {
            if (world == null)
            {
                return;
            }
            world.GeneratorVersion = WorldGenVersions.Current;
            if (string.IsNullOrEmpty(world.WorldSettingsId))
            {
                world.WorldSettingsId = WorldGenContent.DefaultPresetId;
            }
        }

        /// <summary>这份战役的格网是否用世界生成器（而不是旧版本原型地形）。</summary>
        public static bool UsesWorldGen(CampaignState state) =>
            state?.Grid != null && state.Grid.TerrainSourceId == WorldTerrainSource.Id;

        /// <summary>取战役的世界设置预设；查不到时（只可能发生在从没生成过地形的战役上）回退 default 并写回。</summary>
        public static WorldPreset PresetFor(CampaignState state)
        {
            WorldGenState w = state.World;
            // 预设数值随生成器版本走：按存档记录的版本的集合取（FGR-GEN-061），不是按当前表的同名行。
            if (WorldGenContent.TryGetPreset(w.GeneratorVersion, w.WorldSettingsId, out WorldPreset p))
            {
                return p;
            }
            Log.Warning($"[WorldGenService] 生成器 v{w.GeneratorVersion} 没有世界设置 {w.WorldSettingsId ?? "null"}，改用 default（这份战役还没有生成过地形）。");
            w.WorldSettingsId = WorldGenContent.DefaultPresetId;
            return WorldGenContent.Preset(w.GeneratorVersion, WorldGenContent.DefaultPresetId);
        }

        // ── 规划层 ─────────────────────────────────────────────────────────────

        /// <summary>战役的区域规划层（按种子、存档记录的生成器版本与世界设置计算、缓存；旧版本原型地形没有规划层，返回 null）。</summary>
        public static WorldPlan PlanFor(CampaignState state)
        {
            if (state?.World == null || state.World.GeneratorVersion < 1)
            {
                return null;
            }
            GridCell core = HomeGridService.CorePivot(state);
            string key = string.Concat(state.World.WorldSeed.ToString(CultureInfo.InvariantCulture), "|", state.World.GeneratorVersion.ToString(CultureInfo.InvariantCulture),
                "|", state.World.WorldSettingsId, "|", core.ToString(), "|", WorldGenContent.Revision.ToString(CultureInfo.InvariantCulture),
                "|", GridContent.Revision.ToString(CultureInfo.InvariantCulture));
            if (!ReferenceEquals(state, _planState) || _plan == null || key != _planKey)
            {
                _planState = state;
                _planKey = key;
                _plan = WorldPlan.Compute(state.World.WorldSeed, WorldGenContent.Version(state.World.GeneratorVersion), PresetFor(state), core.X, core.Y);
                foreach (string line in _plan.Log)
                {
                    Log.Info($"[WorldPlan] 种子 {state.World.WorldSeed}：{line}");
                }
                foreach (string line in _plan.Failures)
                {
                    Log.Error($"[WorldPlan] 种子 {state.World.WorldSeed}：{line}");
                }
            }
            return _plan;
        }

        // ── 地形来源与表面 ─────────────────────────────────────────────────────────

        /// <summary>按战役与表面组装世界生成器来源。</summary>
        public static WorldTerrainSource CreateSource(CampaignState state, string surfaceId)
        {
            Surface surface = WorldGenContent.Surface(surfaceId);
            bool interior = WorldGenContent.IsInterior(surface);
            GridCell core = interior ? new GridCell(0, 0) : HomeGridService.CorePivot(state);
            return new WorldTerrainSource(state.World.WorldSeed, state.World.GeneratorVersion, PresetFor(state), surface, core,
                GridContent.TuningInt("grid.chunk_size"), interior ? null : PlanFor(state));
        }

        /// <summary>任一表面的格网：地球 = 家园格网（HomeGridService）；室内表面在这里按需建图（独立的区块存储，FGR-GEN-010）。</summary>
        public static HomeGridMap MapFor(CampaignState state, string surfaceId)
        {
            if (surfaceId == WorldGenContent.EarthSurfaceId)
            {
                return HomeGridService.MapFor(state);
            }
            if (!ReferenceEquals(state, _interiorState))
            {
                ShutdownInteriors();
                InteriorMaps.Clear();
                InteriorKeys.Clear();
                _interiorState = state;
            }
            string key = string.Concat(state.World.WorldSeed.ToString(CultureInfo.InvariantCulture), "|", state.World.GeneratorVersion.ToString(CultureInfo.InvariantCulture),
                "|", state.World.WorldSettingsId, "|", WorldGenContent.Revision.ToString(CultureInfo.InvariantCulture), "|", GridContent.Revision.ToString(CultureInfo.InvariantCulture));
            if (!InteriorMaps.TryGetValue(surfaceId, out HomeGridMap map) || !InteriorKeys.TryGetValue(surfaceId, out string old) || old != key)
            {
                if (InteriorStreamers.TryGetValue(surfaceId, out WorldChunkStreamer s))
                {
                    s.Dispose();
                    InteriorStreamers.Remove(surfaceId);
                }
                if (state.World.GeneratorVersion < 1)
                {
                    throw new InvalidOperationException("旧版本（原型地形）的战役没有室内表面：室内场地从生成器 v1 起才存在");
                }
                map = new HomeGridMap(GridContent.TuningInt("grid.chunk_size"), CreateSource(state, surfaceId), surfaceId);
                map.SetSavedDiffs(state.World.ChunkDiffs);
                InteriorMaps[surfaceId] = map;
                InteriorKeys[surfaceId] = key;
            }
            return map;
        }

        /// <summary>任一表面的流式加载器（地球的由 HomeGridService 持有）。</summary>
        public static WorldChunkStreamer StreamerFor(CampaignState state, string surfaceId)
        {
            if (surfaceId == WorldGenContent.EarthSurfaceId)
            {
                return HomeGridService.Streamer(state);
            }
            HomeGridMap map = MapFor(state, surfaceId);
            if (!InteriorStreamers.TryGetValue(surfaceId, out WorldChunkStreamer s) || s.IsDisposed || !ReferenceEquals(s.Map, map))
            {
                s?.Dispose();
                s = new WorldChunkStreamer(map);
                InteriorStreamers[surfaceId] = s;
            }
            return s;
        }

        /// <summary>释放室内表面的流式加载器（区域卸载时）。</summary>
        public static void ShutdownInteriors()
        {
            foreach (WorldChunkStreamer s in InteriorStreamers.Values)
            {
                s.Dispose();
            }
            InteriorStreamers.Clear();
        }

        /// <summary>丢掉全部缓存（换战役、测试注入表后）。</summary>
        public static void Invalidate()
        {
            ShutdownInteriors();
            InteriorMaps.Clear();
            InteriorKeys.Clear();
            _interiorState = null;
            _plan = null;
            _planState = null;
            _planKey = null;
        }

        // ── 存档（FGR-GEN-060～062）─────────────────────────────────────────────────

        /// <summary>写盘前调用：把与本战役绑定的各表面格网里被修改的区块写进 WorldGenState.ChunkDiffs。
        /// 没绑定的表面（本次会话没加载过）与不认识的表面，原有记录原样保留。</summary>
        public static void CaptureDiffs(CampaignState state)
        {
            if (state?.World == null)
            {
                return;
            }
            var captured = new HashSet<string>(StringComparer.Ordinal);
            var fresh = new List<ChunkDiffRecord>();
            HomeGridMap earth = HomeGridService.BoundMap(state);
            if (earth != null)
            {
                earth.CollectDiffs(fresh);
                captured.Add(earth.SurfaceId);
            }
            if (ReferenceEquals(state, _interiorState))
            {
                foreach (HomeGridMap m in InteriorMaps.Values)
                {
                    m.CollectDiffs(fresh);
                    captured.Add(m.SurfaceId);
                }
            }
            if (captured.Count == 0)
            {
                return;
            }
            var merged = new List<ChunkDiffRecord>(fresh.Count + (state.World.ChunkDiffs?.Length ?? 0));
            foreach (ChunkDiffRecord r in state.World.ChunkDiffs ?? Array.Empty<ChunkDiffRecord>())
            {
                if (r != null && !captured.Contains(r.SurfaceId ?? string.Empty))
                {
                    merged.Add(r);
                }
            }
            merged.AddRange(fresh);
            // 稳定排序（表面, 区块 X, 区块 Y）：同一逻辑状态写出同样的字节，与修改先后、字典插入顺序无关（ER1-SAVE-01 / ERD-SAV-004）。
            merged.Sort(CompareDiffs);
            state.World.ChunkDiffs = merged.ToArray();
        }

        private static int CompareDiffs(ChunkDiffRecord a, ChunkDiffRecord b)
        {
            int c = string.CompareOrdinal(a.SurfaceId ?? string.Empty, b.SurfaceId ?? string.Empty);
            if (c != 0)
            {
                return c;
            }
            c = a.ChunkX.CompareTo(b.ChunkX);
            return c != 0 ? c : a.ChunkY.CompareTo(b.ChunkY);
        }

        /// <summary>读档校验：生成器版本本版本游戏能否生成（更新的存档 → Newer）；已知表面的差异编码是否完整（坏 → Payload）。
        /// 不认识的表面（以后新增的星球）不校验、原样保留。</summary>
        public static bool ValidateForLoad(CampaignState state, out SaveFailureReason reason, out string message)
        {
            reason = SaveFailureReason.None;
            message = null;
            WorldGenState w = state?.World;
            if (w == null)
            {
                return true;
            }
            if (w.GeneratorVersion > WorldGenVersions.Current)
            {
                reason = SaveFailureReason.Newer;
                message = $"存档的世界生成器是 v{w.GeneratorVersion}，本版本游戏只能生成到 v{WorldGenVersions.Current}，请更新游戏。";
                return false;
            }
            if (w.GeneratorVersion < 0)
            {
                reason = SaveFailureReason.Payload;
                message = $"存档的世界生成器版本 {w.GeneratorVersion} 非法。";
                return false;
            }
            if (w.GeneratorVersion >= 1 && !WorldGenContent.TryGetVersion(w.GeneratorVersion, out _))
            {
                reason = SaveFailureReason.Payload;
                message = $"生成器版本表缺少 v{w.GeneratorVersion}，无法重建这份存档的地形。";
                return false;
            }
            if (UsesWorldGen(state) && !WorldGenContent.TryGetPreset(w.GeneratorVersion, w.WorldSettingsId, out _))
            {
                reason = SaveFailureReason.Payload;
                message = $"存档的世界设置 {w.WorldSettingsId ?? "null"} 在生成器 v{w.GeneratorVersion} 的世界设置集合里不存在，无法重建地形。";
                return false;
            }
            int size = GridContent.TuningInt("grid.chunk_size");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChunkDiffRecord r in w.ChunkDiffs ?? Array.Empty<ChunkDiffRecord>())
            {
                if (r == null || !WorldGenContent.TryGetSurface(r.SurfaceId, out _))
                {
                    continue;
                }
                if (!WorldDiffCodec.TryDecode(r.DiffPayload, size, null, out string error))
                {
                    reason = SaveFailureReason.Payload;
                    message = $"表面 {r.SurfaceId} 区块 ({r.ChunkX},{r.ChunkY}) 的差异损坏：{error}";
                    return false;
                }
                if (!seen.Add(string.Concat(r.SurfaceId, "|", r.ChunkX.ToString(CultureInfo.InvariantCulture), "|", r.ChunkY.ToString(CultureInfo.InvariantCulture))))
                {
                    reason = SaveFailureReason.Payload;
                    message = $"表面 {r.SurfaceId} 区块 ({r.ChunkX},{r.ChunkY}) 的差异重复。";
                    return false;
                }
            }
            return true;
        }

        // ── 显示（暂停菜单、存档卡）──────────────────────────────────────────────────

        /// <summary>“世界设置：标准 · 生成器 v1 · 地球”（当前语言）；旧存档显示“原型地形（旧存档）”。</summary>
        public static string DescribeSettings(CampaignState state)
        {
            if (state?.World == null)
            {
                return string.Empty;
            }
            string preset = PresetName(state.World.GeneratorVersion, state.World.WorldSettingsId);
            string surface = WorldGenContent.TryGetSurface(WorldGenContent.EarthSurfaceId, out Surface s) ? GameText.Get(s.NameKey) : WorldGenContent.EarthSurfaceId;
            if (state.World.GeneratorVersion < 1 || (state.Grid != null && state.Grid.TerrainSourceId == GridTerrainPrototype.Id))
            {
                preset = GameText.Get("world.legacy_terrain");
            }
            return GameText.Format("ui.pause.world_settings", preset, state.World.GeneratorVersion.ToString(CultureInfo.InvariantCulture), surface);
        }

        /// <summary>世界设置的显示名（当前语言）；生成器 v0（原型地形旧存档）显示“原型地形（旧存档）”；查不到时显示 ID 原文。
        /// 存档卡与暂停菜单共用（FG17 第 4 节“存档卡片显示种子和世界设置摘要”）。</summary>
        public static string PresetName(int generatorVersion, string presetId)
        {
            if (generatorVersion < 1)
            {
                return GameText.Get("world.legacy_terrain");
            }
            return WorldGenContent.TryGetPreset(generatorVersion, presetId, out WorldPreset p) ? GameText.Get(p.NameKey) : presetId ?? string.Empty;
        }
    }
}
