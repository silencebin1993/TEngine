using System;
using System.Collections.Generic;
using GameConfig.fg;
using TEngine;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// FG0-DATA-01（FGR-ARC-005）：正式版内容表（Luban fg.*）的运行时入口——建筑 fg.TbBuilding、机械敌人
    /// fg.TbMechEnemy。数据源头是 tools/cell_tables/fgdata.py，经 gen_all.py → run_luban.sh 生成。
    ///
    /// 生产代码只经这里取内容数值：<see cref="Regions.HomeValleyLayout"/> 的电力 / 修复 / 新建档案、
    /// 区域布局类的敌人生命 / 掉落 / 正面减伤都是本类的派生视图，不再各自写死常量。
    ///
    /// 失败策略（IC-REQ-013）：表没加载上或查不到行时抛出带表名与 ID 的异常并记 Error——内容表坏了
    /// 是安装 / 热更包损坏，不能用 0 血、0 成本悄悄跑下去；自检和冒烟会把这类错误当失败。
    /// <see cref="Revision"/> 在重载 / 测试注入时递增，派生缓存据此重建。
    /// </summary>
    public static class FgContentTables
    {
        private static TbBuilding _buildings;
        private static TbMechEnemy _enemies;
        private static bool _loaded;
        private static bool _overridden;
        private static string _loadError;

        /// <summary>每次重载 / 测试注入 +1。派生视图比较版本号决定是否重建（O(1)）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>加载失败的原因；null 表示正常。</summary>
        public static string LoadError
        {
            get
            {
                EnsureLoaded();
                return _loadError;
            }
        }

        public static IReadOnlyList<Building> Buildings
        {
            get
            {
                RequireLoaded();
                return _buildings.DataList;
            }
        }

        public static IReadOnlyList<MechEnemy> Enemies
        {
            get
            {
                RequireLoaded();
                return _enemies.DataList;
            }
        }

        public static bool TryGetBuilding(string typeId, out Building row)
        {
            EnsureLoaded();
            row = null;
            return typeId != null && _buildings != null && _buildings.DataMap.TryGetValue(typeId, out row) && row != null;
        }

        /// <summary>按 <c>BuildingRecord.BuildingTypeId</c> 取建筑行；查不到抛异常（带表名与 ID）。</summary>
        public static Building Building(string typeId)
        {
            RequireLoaded();
            if (!TryGetBuilding(typeId, out Building row))
            {
                throw new KeyNotFoundException($"内容表 fg.TbBuilding 缺少建筑 {typeId ?? "null"}（改 tools/cell_tables/fgdata.py 后重新生成）");
            }
            return row;
        }

        public static bool TryGetEnemy(string enemyTypeId, out MechEnemy row)
        {
            EnsureLoaded();
            row = null;
            return enemyTypeId != null && _enemies != null && _enemies.DataMap.TryGetValue(enemyTypeId, out row) && row != null;
        }

        /// <summary>按 <c>RegionEnemyRecord.EnemyTypeId</c> 取敌人行；查不到抛异常（带表名与 ID）。</summary>
        public static MechEnemy Enemy(string enemyTypeId)
        {
            RequireLoaded();
            if (!TryGetEnemy(enemyTypeId, out MechEnemy row))
            {
                throw new KeyNotFoundException($"内容表 fg.TbMechEnemy 缺少敌人 {enemyTypeId ?? "null"}（改 tools/cell_tables/fgdata.py 后重新生成）");
            }
            return row;
        }

        /// <summary>重新从 <see cref="ConfigSystem"/> 取表（热更新了配置包之后调用）。</summary>
        public static void Reload()
        {
            _overridden = false;
            _loaded = false;
            _buildings = null;
            _enemies = null;
            _loadError = null;
            Revision++;
            EnsureLoaded();
        }

        /// <summary>测试注入：用构造出来的表替换真实表，证明生产代码确实经表取值（改表 → 行为跟着变）。
        /// 任一参数为 null 表示模拟"该表没加载上"。用完必须 <see cref="ResetForTests"/>。</summary>
        public static void OverrideForTests(TbBuilding buildings, TbMechEnemy enemies, string loadError = null)
        {
            _overridden = true;
            _loaded = true;
            _buildings = buildings;
            _enemies = enemies;
            _loadError = loadError ?? (buildings == null || enemies == null ? "测试注入：内容表为空" : null);
            Revision++;
        }

        public static void ResetForTests()
        {
            Reload();
        }

        private static void RequireLoaded()
        {
            EnsureLoaded();
            if (_loadError != null || _buildings == null || _enemies == null)
            {
                throw new InvalidOperationException($"正式版内容表不可用：{_loadError ?? "未知原因"}");
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
                _buildings = tables?.TbBuilding;
                _enemies = tables?.TbMechEnemy;
                if (_buildings == null || _enemies == null)
                {
                    _loadError = "配置表 fg.TbBuilding / fg.TbMechEnemy 不存在";
                }
            }
            catch (Exception ex)
            {
                _loadError = $"配置表读取失败：{ex.Message}";
            }
            if (_loadError != null)
            {
                Log.Error($"[FgContentTables] {_loadError}");
            }
        }
    }
}
