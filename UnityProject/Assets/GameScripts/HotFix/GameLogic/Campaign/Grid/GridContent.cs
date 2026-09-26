using System;
using System.Collections.Generic;
using GameConfig.fg;
using TEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>
    /// FG0-ARCH-04（FGR-ARC-001 / FGR-LOG-001、003）：格网建造原型的表入口——建筑格网属性 fg.TbBuildingGrid、
    /// 端口 fg.TbBuildingPort、开局布局 fg.TbStartLayout、地形 fg.TbGridTerrain、家园与格网调参 fg.TbHomeTuning。
    /// 数据源 tools/cell_tables/fgdata_grid.py。
    ///
    /// 失败策略与 <see cref="Content.FgContentTables"/> 相同（IC-REQ-013）：表没加载上或查不到行时抛出带表名与 ID 的异常，
    /// 不用 0 尺寸、0 成本悄悄跑下去。<see cref="Revision"/> 在重载 / 测试注入时递增，派生缓存据此重建。
    /// </summary>
    public static class GridContent
    {
        private static TbBuildingGrid _grid;
        private static TbBuildingPort _ports;
        private static TbStartLayout _layout;
        private static TbGridTerrain _terrain;
        private static TbHomeTuning _tuning;
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;

        private static Dictionary<string, List<BuildingPort>> _portsByType;
        private static BuildingTerrainRow[] _terrainByCode;
        private static Dictionary<string, byte> _terrainCodeById;
        private static int _derivedRevision;

        public static int Revision { get; private set; } = 1;

        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<BuildingGrid> Buildings
        {
            get
            {
                RequireLoaded();
                return _grid.DataList;
            }
        }

        public static IReadOnlyList<StartLayout> StartLayout
        {
            get
            {
                RequireLoaded();
                return _layout.DataList;
            }
        }

        public static IReadOnlyList<GridTerrain> Terrains
        {
            get
            {
                RequireLoaded();
                return _terrain.DataList;
            }
        }

        public static bool TryGetBuilding(string typeId, out BuildingGrid row)
        {
            EnsureLoaded();
            row = null;
            return typeId != null && _grid != null && _grid.DataMap.TryGetValue(typeId, out row) && row != null;
        }

        /// <summary>建筑的格网属性；查不到抛异常（带表名与 ID）。</summary>
        public static BuildingGrid Building(string typeId)
        {
            RequireLoaded();
            if (!TryGetBuilding(typeId, out BuildingGrid row))
            {
                throw new KeyNotFoundException($"格网表 fg.TbBuildingGrid 缺少建筑 {typeId ?? "null"}（改 tools/cell_tables/fgdata_grid.py 后重新生成）");
            }
            return row;
        }

        /// <summary>某建筑类型的端口（旋转 0 时的局部格与朝向）。没有端口返回空列表。</summary>
        public static IReadOnlyList<BuildingPort> PortsOf(string typeId)
        {
            EnsureDerived();
            return typeId != null && _portsByType.TryGetValue(typeId, out List<BuildingPort> list)
                ? list
                : (IReadOnlyList<BuildingPort>)Array.Empty<BuildingPort>();
        }

        public static bool TryGetLayout(string anchorId, out StartLayout row)
        {
            EnsureLoaded();
            row = null;
            return anchorId != null && _layout != null && _layout.DataMap.TryGetValue(anchorId, out row) && row != null;
        }

        /// <summary>开局布局里的一个锚点；查不到抛异常。</summary>
        public static StartLayout Layout(string anchorId)
        {
            RequireLoaded();
            if (!TryGetLayout(anchorId, out StartLayout row))
            {
                throw new KeyNotFoundException($"开局布局表 fg.TbStartLayout 缺少锚点 {anchorId ?? "null"}（改 tools/cell_tables/fgdata_grid.py 后重新生成）");
            }
            return row;
        }

        /// <summary>地形字节值 → 地形行（表里没有的字节值返回 null）。</summary>
        public static GridTerrain TerrainByCode(byte code)
        {
            EnsureDerived();
            BuildingTerrainRow r = _terrainByCode[code];
            return r?.Row;
        }

        /// <summary>地形 ID → 字节值；查不到抛异常。</summary>
        public static byte TerrainCode(string terrainId)
        {
            EnsureDerived();
            if (terrainId == null || !_terrainCodeById.TryGetValue(terrainId, out byte code))
            {
                throw new KeyNotFoundException($"地形表 fg.TbGridTerrain 缺少地形 {terrainId ?? "null"}");
            }
            return code;
        }

        public static bool TryTerrainCode(string terrainId, out byte code)
        {
            EnsureDerived();
            code = 0;
            return terrainId != null && _terrainCodeById.TryGetValue(terrainId, out code);
        }

        public static bool TryGetTuning(string id, out float value)
        {
            EnsureLoaded();
            value = 0f;
            if (id == null || _tuning == null || !_tuning.DataMap.TryGetValue(id, out HomeTuning row) || row == null)
            {
                return false;
            }
            value = row.Value;
            return true;
        }

        /// <summary>家园与格网调参；查不到抛异常（配置坏了不能用 0 悄悄跑）。</summary>
        public static float Tuning(string id)
        {
            RequireLoaded();
            if (!TryGetTuning(id, out float value))
            {
                throw new KeyNotFoundException($"调参表 fg.TbHomeTuning 缺少参数 {id ?? "null"}（改 tools/cell_tables/fgdata_grid.py 后重新生成）");
            }
            return value;
        }

        public static int TuningInt(string id) => (int)Math.Round(Tuning(id));

        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            _grid = null;
            _ports = null;
            _layout = null;
            _terrain = null;
            _tuning = null;
            _loadError = null;
            Revision++;
            EnsureLoaded();
        }

        /// <summary>测试注入：用构造出来的表替换真实表（改表 → 行为跟着变）。传 null 的表沿用真实表。
        /// 用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(TbBuildingGrid grid = null, TbBuildingPort ports = null, TbStartLayout layout = null,
            TbGridTerrain terrain = null, TbHomeTuning tuning = null)
        {
            Reload();
            _overridden = true;
            _grid = grid ?? _grid;
            _ports = ports ?? _ports;
            _layout = layout ?? _layout;
            _terrain = terrain ?? _terrain;
            _tuning = tuning ?? _tuning;
            Revision++;
        }

        public static void ResetForTests()
        {
            Reload();
        }

        private static void RequireLoaded()
        {
            EnsureLoaded();
            if (_loadError != null || _grid == null || _ports == null || _layout == null || _terrain == null || _tuning == null)
            {
                throw new InvalidOperationException($"格网建造表不可用：{_loadError ?? "未知原因"}");
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
                _grid = tables?.TbBuildingGrid;
                _ports = tables?.TbBuildingPort;
                _layout = tables?.TbStartLayout;
                _terrain = tables?.TbGridTerrain;
                _tuning = tables?.TbHomeTuning;
                if (_grid == null || _ports == null || _layout == null || _terrain == null || _tuning == null)
                {
                    _loadError = "配置表 fg.TbBuildingGrid / TbBuildingPort / TbStartLayout / TbGridTerrain / TbHomeTuning 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[GridContent] {_loadError}");
            }
        }

        private sealed class BuildingTerrainRow
        {
            public GridTerrain Row;
        }

        private static void EnsureDerived()
        {
            RequireLoaded();
            if (_portsByType != null && _derivedRevision == Revision)
            {
                return;
            }
            var ports = new Dictionary<string, List<BuildingPort>>(StringComparer.Ordinal);
            foreach (BuildingPort p in _ports.DataList)
            {
                if (!ports.TryGetValue(p.TypeId, out List<BuildingPort> list))
                {
                    ports[p.TypeId] = list = new List<BuildingPort>(2);
                }
                list.Add(p);
            }
            var byCode = new BuildingTerrainRow[256];
            var codeById = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (GridTerrain t in _terrain.DataList)
            {
                if (t.Code >= 0 && t.Code <= 255)
                {
                    byCode[t.Code] = new BuildingTerrainRow { Row = t };
                    codeById[t.Id] = (byte)t.Code;
                }
            }
            _portsByType = ports;
            _terrainByCode = byCode;
            _terrainCodeById = codeById;
            _derivedRevision = Revision;
        }
    }
}
