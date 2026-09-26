using System;
using System.Collections.Generic;
using GameConfig.fg;
using TEngine;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>生成器版本号（FGR-GEN-061）。</summary>
    public static class WorldGenVersions
    {
        /// <summary>新战役使用的生成器版本。**任何会改变生成结果的改动都必须新增 fg.TbWorldGenVersion 的一行并把它加 1**；
        /// 旧行不许改（FgWorldGenSelfCheck 的回归哈希守护）。</summary>
        public const int Current = 1;

        /// <summary>0 = 尚未启用程序生成：FG0-ARCH-05 之前的存档，地形来自 FG0-ARCH-04 的原型来源（GridTerrainPrototype，保留为旧版本路径）。</summary>
        public const int LegacyPrototype = 0;

        /// <summary>本版本游戏能生成的版本：0（原型）与 1..Current。</summary>
        public static bool IsSupported(int version) => version >= LegacyPrototype && version <= Current;
    }

    /// <summary>
    /// FG0-ARCH-05：世界生成四张表的入口——生成器版本 fg.TbWorldGenVersion、世界设置预设 fg.TbWorldPreset、
    /// 表面 fg.TbSurface、区域规划层的领地 fg.TbTerritory（数据源 tools/cell_tables/fgdata_world.py）；
    /// 世界调参（world.*）在 fg.TbHomeTuning，经 <see cref="Grid.GridContent.Tuning"/> 读取。
    /// 生成输入随版本走（FGR-GEN-061，ADR-ARC-013 第 4 节）：规划层参数在版本行；领地与世界设置按版本行引用的集合
    /// （territorySet / presetSet）取，旧存档永远读自己那个版本的集合；全部生成输入的清单见 <see cref="WorldGenInputs"/>。
    /// 失败策略同 GridContent：表没加载上或查不到行时抛出带表名与 ID 的异常，不悄悄用 0 跑下去（IC-REQ-013）。
    /// </summary>
    public static class WorldGenContent
    {
        public const string EarthSurfaceId = "earth";
        public const string DefaultPresetId = "default";

        private static TbWorldGenVersion _versions;
        private static TbWorldPreset _presets;
        private static TbSurface _surfaces;
        private static TbTerritory _territories;
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;

        /// <summary>重载 / 测试注入时递增；派生缓存（格网、规划层）据此重建。</summary>
        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<WorldGenVersion> Versions
        {
            get
            {
                RequireLoaded();
                return _versions.DataList;
            }
        }

        public static IReadOnlyList<WorldPreset> Presets
        {
            get
            {
                RequireLoaded();
                return _presets.DataList;
            }
        }

        public static IReadOnlyList<Surface> Surfaces
        {
            get
            {
                RequireLoaded();
                return _surfaces.DataList;
            }
        }

        public static IReadOnlyList<Territory> Territories
        {
            get
            {
                RequireLoaded();
                return _territories.DataList;
            }
        }

        public static bool TryGetVersion(int version, out WorldGenVersion row)
        {
            EnsureLoaded();
            row = null;
            return _versions != null && _versions.DataMap.TryGetValue(version, out row) && row != null;
        }

        public static WorldGenVersion Version(int version)
        {
            RequireLoaded();
            if (!TryGetVersion(version, out WorldGenVersion row))
            {
                throw new KeyNotFoundException($"生成器版本表 fg.TbWorldGenVersion 缺少 v{version}（改 tools/cell_tables/fgdata_world.py 后重新生成）");
            }
            return row;
        }

        /// <summary>生成器版本 <paramref name="version"/> 的世界设置集合里的预设（主键 = 版本行 presetSet + "." + ID）。
        /// 预设数值是生成输入，随版本走（FGR-GEN-061）：存档只记预设 ID，数值永远按存档记录的生成器版本取，调数值要新集合 + 新版本。
        /// <paramref name="version"/> &lt; 1（原型地形旧存档）按当前版本的集合解释（只用于显示名称）。</summary>
        public static bool TryGetPreset(int version, string id, out WorldPreset row)
        {
            row = null;
            if (id == null || !TryGetVersion(version < 1 ? WorldGenVersions.Current : version, out WorldGenVersion v))
            {
                return false;
            }
            return _presets != null && _presets.DataMap.TryGetValue(string.Concat(v.PresetSet, ".", id), out row) && row != null;
        }

        public static WorldPreset Preset(int version, string id)
        {
            RequireLoaded();
            if (!TryGetPreset(version, id, out WorldPreset row))
            {
                throw new KeyNotFoundException($"世界设置表 fg.TbWorldPreset 缺少生成器 v{version} 的预设 {id ?? "null"}");
            }
            return row;
        }

        /// <summary>生成器版本 <paramref name="version"/> 可选的世界设置（表顺序；新游戏界面列当前版本的）。</summary>
        public static List<WorldPreset> PresetsFor(int version)
        {
            RequireLoaded();
            var list = new List<WorldPreset>();
            if (!TryGetVersion(version, out WorldGenVersion v))
            {
                return list;
            }
            foreach (WorldPreset p in _presets.DataList)
            {
                if (p.Set == v.PresetSet)
                {
                    list.Add(p);
                }
            }
            return list;
        }

        /// <summary>版本行引用的领地集合（表顺序；集合内序号参与规划层子种子，所以行序也是生成输入）。</summary>
        public static List<Territory> TerritoriesFor(WorldGenVersion v)
        {
            RequireLoaded();
            var list = new List<Territory>();
            if (v == null)
            {
                return list;
            }
            foreach (Territory t in _territories.DataList)
            {
                if (t.Set == v.TerritorySet)
                {
                    list.Add(t);
                }
            }
            return list;
        }

        public static bool TryGetSurface(string id, out Surface row)
        {
            EnsureLoaded();
            row = null;
            return id != null && _surfaces != null && _surfaces.DataMap.TryGetValue(id, out row) && row != null;
        }

        public static Surface Surface(string id)
        {
            RequireLoaded();
            if (!TryGetSurface(id, out Surface row))
            {
                throw new KeyNotFoundException($"表面表 fg.TbSurface 缺少表面 {id ?? "null"}");
            }
            return row;
        }

        public static bool IsInterior(Surface s) => s != null && s.Kind == "interior";

        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            _versions = null;
            _presets = null;
            _surfaces = null;
            _territories = null;
            _loadError = null;
            Revision++;
            EnsureLoaded();
        }

        /// <summary>测试注入：用构造出来的表替换真实表（传 null 的沿用真实表）。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(TbWorldGenVersion versions = null, TbWorldPreset presets = null, TbSurface surfaces = null,
            TbTerritory territories = null)
        {
            Reload();
            _overridden = true;
            _versions = versions ?? _versions;
            _presets = presets ?? _presets;
            _surfaces = surfaces ?? _surfaces;
            _territories = territories ?? _territories;
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void RequireLoaded()
        {
            EnsureLoaded();
            if (_loadError != null || _versions == null || _presets == null || _surfaces == null || _territories == null)
            {
                throw new InvalidOperationException($"世界生成表不可用：{_loadError ?? "未知原因"}");
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded || _overridden)
            {
                return;
            }
            _loaded = true;
            try
            {
                GameConfig.Tables tables = ConfigSystem.Instance.Tables;
                _versions = tables?.TbWorldGenVersion;
                _presets = tables?.TbWorldPreset;
                _surfaces = tables?.TbSurface;
                _territories = tables?.TbTerritory;
                if (_versions == null || _presets == null || _surfaces == null || _territories == null)
                {
                    _loadError = "配置表 fg.TbWorldGenVersion / TbWorldPreset / TbSurface / TbTerritory 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[WorldGenContent] {_loadError}");
            }
        }
    }
}
