using System;
using System.Collections.Generic;
using GameLogic.Campaign.Grid;
using TEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>生产建筑的工作方式（fg.TbProducer.mode）。</summary>
    public enum ProducerMode : byte
    {
        /// <summary>回收站：先分解输入口送来的固体，没有时拆脚下的废墟。</summary>
        Recycler = 0,
        /// <summary>提取钻：脚下矿脉无限。</summary>
        Drill = 1,
        /// <summary>按配方生产（精炼炉 / 精炼塔 / 调配站）。</summary>
        Recipe = 2,
        /// <summary>废液池：缓慢销毁收到的流体。</summary>
        Waste = 3,
        /// <summary>流体泵（FG04 第 3.3 节，2×2 采集建筑）：抽脚下流体源（水源 / 油井）的流体推进出口的管线。</summary>
        Pump = 4,
    }

    /// <summary>一个流体端口（fg.TbBuildingFluidPort 一行的运行时视图）。</summary>
    public sealed class FluidPortDef
    {
        public string Id;
        public int LocalX;
        public int LocalY;
        public GridDir Dir;
        public bool IsOutput;
        /// <summary>只收 / 只出的流体（物品表行）；null = 收任何流体（废液池），或出脚下流体源的流体（<see cref="FromSource"/>）。</summary>
        public ItemDef Fluid;
        /// <summary>表里写 source：出脚下流体源的流体（只用于流体泵的输出口；实际流体由 ProductionService 按地形定）。</summary>
        public bool FromSource;
        public int FluidId => Fluid?.FluidId ?? 0;
    }

    /// <summary>一座生产建筑的参数（fg.TbProducer 一行 + 它的流体端口）。</summary>
    public sealed class ProducerDef
    {
        public string TypeId;
        public ProducerMode Mode;
        public readonly List<RecipeDef> Recipes = new List<RecipeDef>(2);
        public int InBatches;
        public int OutBatches;
        public float CycleSeconds;
        public int CycleAmount;
        public float ItemSeconds;
        public float FluidLpm;
        public float VibrationPerMinute;
        public string PlaceHintKey;
        public int SortOrder;
        public readonly List<FluidPortDef> FluidPorts = new List<FluidPortDef>(3);

        /// <summary>只有一条配方时，那就是这座建筑的固定功能（不用选，FGR-BASE-020 不算替玩家做选择）；多条时由玩家选。</summary>
        public RecipeDef FixedRecipe => Mode == ProducerMode.Recipe && Recipes.Count == 1 ? Recipes[0] : null;

        public bool AllowsRecipe(RecipeDef r)
        {
            foreach (RecipeDef x in Recipes)
            {
                if (ReferenceEquals(x, r))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// FG4-ECO-02：生产建筑表的运行时入口。数据源 tools/cell_tables/fgdata_production.py → fg.TbProducer / fg.TbBuildingFluidPort（配方在 <see cref="ItemCatalog"/>）。
    /// 载入时做与生成端 validate() 相同的检查：配方必须存在且属于这座建筑、流体端口的流体必须是物品表里的流体、参数为正……出问题的建筑整行拒绝（不在运行时“少一个口”地跑），
    /// 原因记进 <see cref="Problems"/> 与日志。只在载入 / 重载时 O(行数)，查询 O(1)。
    /// </summary>
    public static class ProducerCatalog
    {
        public struct ProducerRow
        {
            public string TypeId;
            public string Mode;
            public string Recipes;
            public int InBatches;
            public int OutBatches;
            public float CycleSeconds;
            public int CycleAmount;
            public float ItemSeconds;
            public float FluidLpm;
            public float VibrationPerMinute;
            public string PlaceHintKey;
            public int SortOrder;
        }

        public struct FluidPortRow
        {
            public string Id;
            public string TypeId;
            public int LocalX;
            public int LocalY;
            public string Dir;
            public string Kind;
            public string Fluid;
        }

        public sealed class Data
        {
            public readonly List<ProducerDef> All = new List<ProducerDef>();
            public readonly Dictionary<string, ProducerDef> ByType = new Dictionary<string, ProducerDef>(StringComparer.Ordinal);
            public readonly List<string> Problems = new List<string>();
        }

        private static Data _data;
        private static bool _overridden;
        private static string _loadError;

        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<ProducerDef> All
        {
            get
            {
                Ensure();
                return _data.All;
            }
        }

        public static IReadOnlyList<string> Problems
        {
            get
            {
                Ensure();
                return _data.Problems;
            }
        }

        public static string LoadError
        {
            get
            {
                Ensure();
                return _loadError;
            }
        }

        public static bool TryGet(string typeId, out ProducerDef def)
        {
            Ensure();
            def = null;
            return typeId != null && _data.ByType.TryGetValue(typeId, out def);
        }

        public static bool IsProducer(string typeId) => TryGet(typeId, out _);

        public static void Reload()
        {
            _overridden = false;
            _data = null;
            _loadError = null;
            Revision++;
        }

        public static void OverrideForTests(Data data)
        {
            _overridden = true;
            _data = data ?? new Data();
            _loadError = data == null ? "测试注入：生产建筑表为空" : null;
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void Ensure()
        {
            if (_data != null || _overridden)
            {
                return;
            }
            var rows = new List<ProducerRow>();
            var ports = new List<FluidPortRow>();
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                if (t?.TbProducer == null || t.TbBuildingFluidPort == null)
                {
                    _loadError = "配置表 fg.TbProducer / fg.TbBuildingFluidPort 不存在";
                }
                else
                {
                    foreach (GameConfig.fg.Producer r in t.TbProducer.DataList)
                    {
                        rows.Add(new ProducerRow
                        {
                            TypeId = r.TypeId, Mode = r.Mode, Recipes = r.Recipes, InBatches = r.InBatches, OutBatches = r.OutBatches, CycleSeconds = r.CycleSeconds,
                            CycleAmount = r.CycleAmount, ItemSeconds = r.ItemSeconds, FluidLpm = r.FluidLpm, VibrationPerMinute = r.VibrationPerMinute,
                            PlaceHintKey = r.PlaceHintKey, SortOrder = r.SortOrder,
                        });
                    }
                    foreach (GameConfig.fg.BuildingFluidPort r in t.TbBuildingFluidPort.DataList)
                    {
                        ports.Add(new FluidPortRow { Id = r.Id, TypeId = r.TypeId, LocalX = r.LocalX, LocalY = r.LocalY, Dir = r.Dir, Kind = r.Kind, Fluid = r.Fluid });
                    }
                }
            }
            catch (Exception ex)
            {
                _loadError = "配置表读取失败：" + ex.Message;
            }
            _data = Build(rows, ports);
            if (_loadError != null)
            {
                Log.Error($"[ProducerCatalog] {_loadError}（改 tools/cell_tables/fgdata_production.py 后重新生成）");
            }
            else if (_data.Problems.Count > 0)
            {
                Log.Error($"[ProducerCatalog] 生产建筑表有 {_data.Problems.Count} 个问题：{string.Join("；", _data.Problems)}");
            }
            Revision++;
        }

        public static bool TryParseMode(string text, out ProducerMode mode)
        {
            switch (text)
            {
                case "recycler": mode = ProducerMode.Recycler; return true;
                case "drill": mode = ProducerMode.Drill; return true;
                case "recipe": mode = ProducerMode.Recipe; return true;
                case "waste": mode = ProducerMode.Waste; return true;
                case "pump": mode = ProducerMode.Pump; return true;
                default: mode = ProducerMode.Recipe; return false;
            }
        }

        /// <summary>由纯结构构建并检查（与 fgdata_production.validate() 同一套规则）。出问题的建筑整行拒绝。</summary>
        public static Data Build(IReadOnlyList<ProducerRow> rows, IReadOnlyList<FluidPortRow> ports)
        {
            var d = new Data();
            var portsByType = new Dictionary<string, List<FluidPortRow>>(StringComparer.Ordinal);
            foreach (FluidPortRow p in ports ?? Array.Empty<FluidPortRow>())
            {
                if (string.IsNullOrEmpty(p.TypeId))
                {
                    d.Problems.Add($"流体口 {p.Id} 缺建筑");
                    continue;
                }
                if (!portsByType.TryGetValue(p.TypeId, out List<FluidPortRow> l))
                {
                    portsByType[p.TypeId] = l = new List<FluidPortRow>(3);
                }
                l.Add(p);
            }
            foreach (ProducerRow r in rows ?? Array.Empty<ProducerRow>())
            {
                if (string.IsNullOrEmpty(r.TypeId) || d.ByType.ContainsKey(r.TypeId))
                {
                    d.Problems.Add($"生产建筑 {r.TypeId} 缺 ID 或重复");
                    continue;
                }
                string why = Check(r, out ProducerDef def);
                if (why == null && portsByType.TryGetValue(r.TypeId, out List<FluidPortRow> fps))
                {
                    foreach (FluidPortRow p in fps)
                    {
                        why = AddPort(def, p);
                        if (why != null)
                        {
                            break;
                        }
                    }
                }
                if (why == null)
                {
                    why = CheckPorts(def);
                }
                if (why != null)
                {
                    d.Problems.Add($"生产建筑 {r.TypeId}：{why}");
                    continue;
                }
                d.All.Add(def);
                d.ByType[def.TypeId] = def;
            }
            d.All.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            return d;
        }

        private static string Check(ProducerRow r, out ProducerDef def)
        {
            def = null;
            if (!TryParseMode(r.Mode, out ProducerMode mode))
            {
                return $"工作方式 {r.Mode} 不认识";
            }
            if (!GridContent.TryGetBuilding(r.TypeId, out _))
            {
                return "不在建筑格网表里";
            }
            def = new ProducerDef
            {
                TypeId = r.TypeId,
                Mode = mode,
                InBatches = r.InBatches,
                OutBatches = r.OutBatches,
                CycleSeconds = r.CycleSeconds,
                CycleAmount = r.CycleAmount,
                ItemSeconds = r.ItemSeconds,
                FluidLpm = r.FluidLpm,
                VibrationPerMinute = r.VibrationPerMinute,
                PlaceHintKey = string.IsNullOrEmpty(r.PlaceHintKey) ? "prod.place.any" : r.PlaceHintKey,
                SortOrder = r.SortOrder,
            };
            if (!string.IsNullOrEmpty(r.Recipes) && r.Recipes != "none")
            {
                foreach (string id in r.Recipes.Split(','))
                {
                    string rid = id.Trim();
                    if (!ItemCatalog.TryGetRecipe(rid, out RecipeDef rec))
                    {
                        return $"配方 {rid} 不在配方表（或被配方表拒绝）";
                    }
                    if (rec.Building != r.TypeId)
                    {
                        return $"配方 {rid} 属于 {rec.Building}，不是它";
                    }
                    def.Recipes.Add(rec);
                }
            }
            switch (mode)
            {
                case ProducerMode.Recipe:
                    if (def.Recipes.Count == 0 || r.InBatches < 1 || r.OutBatches < 1)
                    {
                        return "配方建筑至少一条配方、输入 / 输出缓存至少 1 批";
                    }
                    break;
                case ProducerMode.Drill:
                    if (def.Recipes.Count > 0 || r.CycleSeconds <= 0f || r.CycleAmount < 1 || r.OutBatches < 1)
                    {
                        return "提取钻要有周期、产量与输出缓存，不用配方";
                    }
                    break;
                case ProducerMode.Recycler:
                    if (def.Recipes.Count > 0 || r.CycleSeconds <= 0f || r.CycleAmount < 1 || r.ItemSeconds <= 0f || r.InBatches < 1 || r.OutBatches < 1)
                    {
                        return "回收站要有拆废墟周期 / 产量、分解时间与缓存，不用配方";
                    }
                    break;
                case ProducerMode.Waste:
                    if (def.Recipes.Count > 0 || r.FluidLpm <= 0f)
                    {
                        return "废液池要有销毁速率，不用配方";
                    }
                    break;
                case ProducerMode.Pump:
                    if (def.Recipes.Count > 0 || r.FluidLpm <= 0f || r.OutBatches < 1)
                    {
                        return "流体泵要有抽取速率与出口存量（秒），不用配方";
                    }
                    break;
            }
            if (r.VibrationPerMinute < 0f)
            {
                return "震动不能为负";
            }
            return null;
        }

        private static string AddPort(ProducerDef def, FluidPortRow p)
        {
            if (!GridMath.TryParseDir(p.Dir, out GridDir dir))
            {
                return $"流体口 {p.Id} 方向 {p.Dir} 不认识";
            }
            bool output = p.Kind == "out";
            if (!output && p.Kind != "in")
            {
                return $"流体口 {p.Id} kind {p.Kind} 不认识";
            }
            ItemDef fluid = null;
            bool fromSource = false;
            if (p.Fluid == "any")
            {
                if (output)
                {
                    return $"流体口 {p.Id}：any 只能用于输入";
                }
            }
            else if (p.Fluid == "source")
            {
                if (!output || def.Mode != ProducerMode.Pump)
                {
                    return $"流体口 {p.Id}：source 只能用于流体泵的输出口";
                }
                fromSource = true;
            }
            else if (!ItemCatalog.TryGet(p.Fluid, out fluid) || fluid.Form != ItemForm.Fluid || fluid.FluidId <= 0)
            {
                return $"流体口 {p.Id}：{p.Fluid} 不是物品表里的流体";
            }
            def.FluidPorts.Add(new FluidPortDef { Id = p.Id, LocalX = p.LocalX, LocalY = p.LocalY, Dir = dir, IsOutput = output, Fluid = fluid, FromSource = fromSource });
            return null;
        }

        /// <summary>流体泵有且只有一个 source 输出口（没有就整行拒绝：一座抽不出东西的泵不该出现在建造菜单里）。</summary>
        private static string CheckPorts(ProducerDef def)
        {
            if (def.Mode == ProducerMode.Pump && (def.FluidPorts.Count != 1 || !def.FluidPorts[0].FromSource))
            {
                return "流体泵要有且只有一个 fluid = source 的输出口";
            }
            return null;
        }
    }
}
