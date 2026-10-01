using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-03（FG04 配方表“机器 | 装配站 | 按蓝图（底盘、组件、模块、电子件）”；卡片“装配站改为从产线取料生产机器”；承接 DEBT-FG4ECO01-01 的组件 / 蓝图材料构成、
    /// DEBT-FG3LOG03-01 的装配站输入口、DEBT-FG2FW05-01 的“取料路线被堵就等待物料”）：装配站的材料规则。
    ///
    /// - 材料表（fg.TbAssemblyMaterial）：机器 = 底盘（chassis.&lt;原型&gt;）+ 每台都要的控制电路（base）+ 装了主组件 / 功能组件各一个作战组件（slot.primary / slot.utility）
    ///   + 装了结构模块一个结构模块（slot.structure）。<see cref="For"/> 按蓝图版本算出清单，<see cref="RetrofitDiff"/> 算改造要补的差额。
    /// - 材料缓存（<see cref="EconomyState.AssemblyBuffer"/>）：装配站西侧输入口（role = prod，内核收法 <see cref="BeltConst.AcceptSet"/>）只收机器材料，
    ///   每种最多 eco.assembly.buffer_per_item；放满了带停下（下游已满），送来别的物品带停下并写明“只收机器材料”。
    /// - 取料（<see cref="TryTake"/>，<see cref="HomeValleyFactory"/> 在队首开工时调用）：先从材料缓存、再从仓库取，<b>全有或全无</b>；仓库到装配站的路线被堵
    ///   （<see cref="Signal.FirmwareLibrary.EvaluateRoute"/>，与固件库同一个判定）时不从仓库取。还缺的材料：开着“缺材料时用废料代付”就按废料当量折算用废料补
    ///   （<see cref="SubstituteScrap"/>：全部代付 = 蓝图的废料价），关着就等。取走的材料记在队列项上（取消 / 被毁 / 出厂失败退回仓库，完工时消耗）。
    /// 不做玩家没要求的事（FGR-BASE-020）：只为玩家排的队列项取料；废料代付是玩家在面板上能看到、能关的设置。O(材料种类)，只在队首尝试开工时调用（不按帧扫描）。
    /// </summary>
    public static class AssemblyMaterials
    {
        public const string PartBase = "base";
        public const string PartPrimary = "slot.primary";
        public const string PartUtility = "slot.utility";
        public const string PartStructure = "slot.structure";
        public const string ChassisPrefix = "chassis.";

        public struct MaterialRow
        {
            public string Id;
            public string Part;
            public string Item;
            public int Amount;
        }

        public sealed class Data
        {
            public readonly Dictionary<string, List<(ItemDef Item, int Amount)>> ByPart = new Dictionary<string, List<(ItemDef, int)>>(StringComparer.Ordinal);
            /// <summary>出现在材料表里的全部物品（按物品表顺序）。</summary>
            public readonly List<ItemDef> Items = new List<ItemDef>();
            public readonly List<string> Problems = new List<string>();
            public ulong BeltMask;
        }

        private static Data _data;
        private static bool _overridden;
        private static Dictionary<string, double> _equivalents;
        private static int _equivalentsRevision = -1;

        public static int Revision { get; private set; } = 1;

        public static IReadOnlyList<ItemDef> MaterialItems
        {
            get
            {
                Ensure();
                return _data.Items;
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

        /// <summary>装配站输入口的收货集合（传送带内核配置 <see cref="BeltConfig.AcceptSetMask"/>）。</summary>
        public static ulong BeltMask
        {
            get
            {
                Ensure();
                return _data.BeltMask;
            }
        }

        public static bool IsMaterial(ItemDef item)
        {
            Ensure();
            return item != null && _data.Items.Contains(item);
        }

        public static void Reload()
        {
            _data = null;
            _overridden = false;
            _equivalents = null;
            PortJamItem = null;
            Revision++;
        }

        public static void OverrideForTests(Data data)
        {
            _overridden = true;
            _data = data ?? new Data();
            Revision++;
        }

        public static void ResetForTests() => Reload();

        private static void Ensure()
        {
            if (_data != null || _overridden)
            {
                return;
            }
            var rows = new List<MaterialRow>();
            string loadError = null;
            try
            {
                GameConfig.Tables t = ConfigSystem.Instance.Tables;
                if (t?.TbAssemblyMaterial == null)
                {
                    loadError = "配置表 fg.TbAssemblyMaterial 不存在";
                }
                else
                {
                    foreach (GameConfig.fg.AssemblyMaterial r in t.TbAssemblyMaterial.DataList)
                    {
                        rows.Add(new MaterialRow { Id = r.Id, Part = r.Part, Item = r.Item, Amount = r.Amount });
                    }
                }
            }
            catch (Exception ex)
            {
                loadError = "配置表读取失败：" + ex.Message;
            }
            _data = Build(rows);
            if (loadError != null)
            {
                _data.Problems.Add(loadError);
            }
            if (_data.Problems.Count > 0)
            {
                Log.Error($"[AssemblyMaterials] 机器材料表有 {_data.Problems.Count} 个问题：{string.Join("；", _data.Problems)}（改 tools/cell_tables/fgdata_manufacturing.py 后重新生成）");
            }
            Revision++;
        }

        /// <summary>由纯结构构建并检查（与 fgdata_manufacturing.validate() 同一套规则）：部位不认识、物品不是能上传送带的固体、数量 &lt; 1 的行拒绝并记原因。</summary>
        public static Data Build(IReadOnlyList<MaterialRow> rows)
        {
            var d = new Data();
            foreach (MaterialRow r in rows ?? Array.Empty<MaterialRow>())
            {
                if (string.IsNullOrEmpty(r.Part) || !(r.Part == PartBase || r.Part == PartPrimary || r.Part == PartUtility || r.Part == PartStructure
                                                     || r.Part.StartsWith(ChassisPrefix, StringComparison.Ordinal)))
                {
                    d.Problems.Add($"机器材料 {r.Id}：部位 {r.Part} 不认识");
                    continue;
                }
                if (!ItemCatalog.TryGet(r.Item, out ItemDef item) || item.Form != ItemForm.Solid || item.BeltId == 0)
                {
                    d.Problems.Add($"机器材料 {r.Id}：{r.Item} 不是能上传送带的固体");
                    continue;
                }
                if (r.Amount < 1)
                {
                    d.Problems.Add($"机器材料 {r.Id}：数量至少 1");
                    continue;
                }
                if (!d.ByPart.TryGetValue(r.Part, out List<(ItemDef, int)> list))
                {
                    d.ByPart[r.Part] = list = new List<(ItemDef, int)>(3);
                }
                list.Add((item, r.Amount));
                if (!d.Items.Contains(item))
                {
                    d.Items.Add(item);
                }
                if (item.BeltId > 0 && item.BeltId < 64)
                {
                    d.BeltMask |= 1UL << item.BeltId;
                }
                else
                {
                    d.Problems.Add($"机器材料 {r.Id}：{r.Item} 的传送带编号 {item.BeltId} 超出收货集合（1～63）");
                }
            }
            d.Items.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            return d;
        }

        // ── 蓝图 → 材料清单 ──────────────────────────────────────────────────────

        /// <summary>底盘 ID → 材料表部位（chassis_track → chassis.track）。不认识的底盘没有底盘材料（记一次问题）。</summary>
        public static string ChassisPart(string chassisId)
        {
            string archetype = ChassisCatalog.ResolveArchetype(chassisId) ?? chassisId ?? string.Empty;
            return archetype.StartsWith("chassis_", StringComparison.Ordinal) ? ChassisPrefix + archetype.Substring(8) : ChassisPrefix + archetype;
        }

        /// <summary>一个蓝图版本的材料清单（按物品表顺序合并同类）。版本为空 = 空清单。</summary>
        public static ItemStackRecord[] For(BlueprintVersionRecord v)
        {
            if (v == null)
            {
                return Array.Empty<ItemStackRecord>();
            }
            Ensure();
            var acc = new List<ItemStackRecord>(5);
            AddPart(acc, ChassisPart(v.ChassisId));
            AddPart(acc, PartBase);
            if (!string.IsNullOrEmpty(v.PrimaryId))
            {
                AddPart(acc, PartPrimary);
            }
            if (!string.IsNullOrEmpty(v.UtilityId))
            {
                AddPart(acc, PartUtility);
            }
            if (!string.IsNullOrEmpty(v.StructureId))
            {
                AddPart(acc, PartStructure);
            }
            return Sorted(acc);
        }

        /// <summary>改造要补的材料：新蓝图比旧蓝图多出来的部分（逐种取正差；少的不退）。</summary>
        public static ItemStackRecord[] RetrofitDiff(BlueprintVersionRecord from, BlueprintVersionRecord to)
        {
            ItemStackRecord[] a = For(from);
            ItemStackRecord[] b = For(to);
            var acc = new List<ItemStackRecord>(b.Length);
            foreach (ItemStackRecord s in b)
            {
                int extra = s.Amount - ProductionService.Count(a, s.ItemId);
                if (extra > 0)
                {
                    acc.Add(new ItemStackRecord { ItemId = s.ItemId, Amount = extra });
                }
            }
            return Sorted(acc);
        }

        private static void AddPart(List<ItemStackRecord> acc, string part)
        {
            if (!_data.ByPart.TryGetValue(part, out List<(ItemDef Item, int Amount)> rows))
            {
                return;
            }
            foreach ((ItemDef item, int amount) in rows)
            {
                ItemStackRecord found = null;
                foreach (ItemStackRecord s in acc)
                {
                    if (s.ItemId == item.Id)
                    {
                        found = s;
                        break;
                    }
                }
                if (found != null)
                {
                    found.Amount += amount;
                }
                else
                {
                    acc.Add(new ItemStackRecord { ItemId = item.Id, Amount = amount });
                }
            }
        }

        private static ItemStackRecord[] Sorted(List<ItemStackRecord> acc)
        {
            acc.Sort((x, y) => Order(x.ItemId).CompareTo(Order(y.ItemId)));
            return acc.ToArray();
        }

        private static int Order(string itemId) => ItemCatalog.TryGet(itemId, out ItemDef d) ? d.SortOrder : int.MaxValue;

        // ── 废料当量与代付 ──────────────────────────────────────────────────────

        /// <summary>
        /// 废料当量（与 fgdata_eco 回收产出同一算法）：废料 1、金属矿 2、稀土矿 1、流体 0；其余固体 = 产出它的配方里输入当量之和 ÷ 产量，取最便宜的那条（迭代到不再变化）。
        /// 只在物品表变化后重算一次，O(配方数 × 迭代轮数)。
        /// </summary>
        public static double ScrapEquivalent(ItemDef item)
        {
            if (item == null)
            {
                return 0;
            }
            if (_equivalents == null || _equivalentsRevision != ItemCatalog.Revision)
            {
                _equivalents = ComputeEquivalents();
                _equivalentsRevision = ItemCatalog.Revision;
            }
            return _equivalents.TryGetValue(item.Id, out double v) ? v : 1.0;
        }

        private static Dictionary<string, double> ComputeEquivalents()
        {
            var eq = new Dictionary<string, double>(StringComparer.Ordinal) { [ItemCatalog.ScrapId] = 1.0, ["metal_ore"] = 2.0, ["rare_earth_ore"] = 1.0 };
            foreach (ItemDef i in ItemCatalog.Items)
            {
                if (i.Form == ItemForm.Fluid)
                {
                    eq[i.Id] = 0.0;
                }
            }
            bool changed = true;
            for (int round = 0; changed && round < 32; round++)
            {
                changed = false;
                foreach (RecipeDef r in ItemCatalog.Recipes)
                {
                    double cost = 0;
                    bool known = true;
                    ItemDef main = null;
                    int mainAmount = 0;
                    foreach (RecipeLine l in r.Lines)
                    {
                        if (l.Role == RecipeRole.In)
                        {
                            if (!eq.TryGetValue(l.Item.Id, out double c))
                            {
                                known = false;
                                break;
                            }
                            cost += c * l.Amount;
                        }
                        else if (l.Role == RecipeRole.Out && main == null)
                        {
                            main = l.Item;
                            mainAmount = l.Amount;
                        }
                    }
                    if (!known || main == null || mainAmount <= 0 || main.Form != ItemForm.Solid || main.Id == ItemCatalog.ScrapId
                        || main.Id == "metal_ore" || main.Id == "rare_earth_ore")
                    {
                        continue;
                    }
                    double per = cost / mainAmount;
                    if (!eq.TryGetValue(main.Id, out double old) || per < old - 1e-9)
                    {
                        eq[main.Id] = per;
                        changed = true;
                    }
                }
            }
            return eq;
        }

        /// <summary>
        /// 缺料时用废料代付多少：按“缺的那部分材料的废料当量 ÷ 全部材料的废料当量”折算 <paramref name="scrapPrice"/>（全部代付 = 废料价），向上取整。
        /// 材料清单为空（例如改造前后材料一样）= 0。
        /// </summary>
        public static int SubstituteScrap(ItemStackRecord[] materials, ItemStackRecord[] missing, int scrapPrice)
        {
            if (scrapPrice <= 0 || missing == null || missing.Length == 0)
            {
                return 0;
            }
            double total = 0, miss = 0;
            foreach (ItemStackRecord s in materials ?? Array.Empty<ItemStackRecord>())
            {
                total += s.Amount * ScrapEquivalent(ItemCatalog.Find(s.ItemId));
            }
            foreach (ItemStackRecord s in missing)
            {
                miss += s.Amount * ScrapEquivalent(ItemCatalog.Find(s.ItemId));
            }
            if (total <= 0 || miss <= 0)
            {
                return 0;
            }
            return Math.Min(scrapPrice, (int)Math.Ceiling(scrapPrice * miss / total - 1e-9));
        }

        /// <summary>
        /// 一份材料（<paramref name="part"/>）在整台机器材料（<paramref name="all"/>，废料价 <paramref name="allPrice"/>）里值多少废料：按废料当量占比折算，向下取整，
        /// 不超过 <paramref name="cap"/>。<paramref name="part"/> 为空 = 0；全部材料的废料当量为 0（数据异常）时按 <paramref name="cap"/> 算。回厂改造定价用（<see cref="HomeValleyFactory.PriceRetrofit"/>）。
        /// </summary>
        public static int PriceShare(ItemStackRecord[] all, ItemStackRecord[] part, int allPrice, int cap)
        {
            if (part == null || part.Length == 0 || cap <= 0 || allPrice <= 0)
            {
                return 0;
            }
            double total = 0, share = 0;
            foreach (ItemStackRecord s in all ?? Array.Empty<ItemStackRecord>())
            {
                total += s.Amount * ScrapEquivalent(ItemCatalog.Find(s.ItemId));
            }
            foreach (ItemStackRecord s in part)
            {
                share += s.Amount * ScrapEquivalent(ItemCatalog.Find(s.ItemId));
            }
            if (total <= 0)
            {
                return cap;
            }
            return Math.Max(0, Math.Min(cap, (int)Math.Floor(allPrice * share / total + 1e-9)));
        }

        // ── 装配站状态（材料缓存、废料代付开关）──────────────────────────────────────

        public static int BufferCap => Math.Max(1, GridContent.TuningInt("eco.assembly.buffer_per_item"));

        public static ItemStackRecord[] Buffer(CampaignState state)
        {
            if (state == null)
            {
                return Array.Empty<ItemStackRecord>();
            }
            if (state.Economy?.AssemblyBuffer == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            return state.Economy.AssemblyBuffer;
        }

        public static int InBuffer(CampaignState state, string itemId) => ProductionService.Count(Buffer(state), itemId);

        /// <summary>“缺材料时用废料代付”现在开着吗（玩家没设置过时按 eco.assembly.scrap_substitute_default）。</summary>
        public static bool ScrapSubstitute(CampaignState state)
        {
            int v = state?.Economy?.AssemblyScrapSubstitute ?? 0;
            return v == 0 ? GridContent.Tuning("eco.assembly.scrap_substitute_default") > 0.5f : v == 1;
        }

        public static void SetScrapSubstitute(CampaignState state, bool on)
        {
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            state.Economy.AssemblyScrapSubstitute = on ? 1 : 2;
            Revision++;
        }

        public static BuildingRecord Station(CampaignState state)
        {
            foreach (BuildingRecord b in state?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation && string.IsNullOrEmpty(b.RelocateFromId))
                {
                    return b;
                }
            }
            return null;
        }

        public static bool IsStation(BuildingRecord b) => b != null && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation;

        // ── 输入口（BeltPortService.Pump → ProductionService.PumpPort → 这里；每个传送带内核步一次，O(1)）────────────────────

        /// <summary>
        /// 装配站输入口：缓存里的机器材料收进材料缓存（每种最多 <see cref="BufferCap"/>，放不下的留在端口缓存，带停下）。
        /// 内核收法是 <see cref="BeltConst.AcceptSet"/>，不是机器材料的物品不会进端口；表里没有的编号（内容被删）放到装配站旁边的地上（不消失、不堵带）。
        /// </summary>
        public static void PumpPort(CampaignState state, BeltPortService.Binding bind, BeltKernel k, int buffered, ushort kind)
        {
            if (bind.IsOutput)
            {
                return;
            }
            if (buffered <= 0)
            {
                PortJamItem = null;
                return;
            }
            ItemDef item = BeltItems.Def(kind);
            BuildingRecord b = HomeGridService.FindBuilding(state, bind.BuildingId);
            Vector2 at = b?.Position ?? HomeValleyLayout.CameraFocusStart;
            if (item == null || !IsMaterial(item))
            {
                int n0 = k.TakeFromSink(bind.PortId, buffered);
                if (n0 > 0)
                {
                    HomeValleyConstruction.ReturnMaterials(state, at, item != null ? item.ResourceType : BeltItems.ResourceOf(kind), n0,
                        "asm-port:" + bind.PortId.ToString(CultureInfo.InvariantCulture) + ":" + GameClock.Ticks.ToString(CultureInfo.InvariantCulture));
                }
                return;
            }
            ItemStackRecord[] buf = Buffer(state);
            int room = BufferCap - ProductionService.Count(buf, item.Id);
            int n = Math.Min(buffered, room);
            // 这种材料在缓存里已放满、端口里还压着它：同一条带后面的其它材料进不来（端口一次只放一种）。只用于原因文字（DescribeShortfall），不改变行为。
            PortJamItem = room <= 0 ? item.Id : null;
            if (n > 0)
            {
                ProductionService.Add(ref state.Economy.AssemblyBuffer, item.Id, k.TakeFromSink(bind.PortId, n));
                HomeInventory.Touch();
            }
        }

        /// <summary>
        /// 装配站输入口现在被哪种“缓存已放满”的材料占着（null = 没有）。每个传送带内核步由 <see cref="PumpPort"/> 刷新；不进存档（读档后第一步就重算）。
        /// 原因文字据此提示“输入口被 X 堵着”（审查 P2：缓存满的材料停在端口，同带后面缺的材料进不来，原因却只写“缺材料”会误导）。
        /// </summary>
        public static string PortJamItem { get; private set; }

        public static void ResetSessionState() => PortJamItem = null;

        /// <summary>端口面板：装配站输入口收什么。</summary>
        public static string PortAcceptLine(bool output)
        {
            if (output)
            {
                return GameText.Get("asm.port.out");
            }
            return GameText.Format("asm.port.accept", NamesOf(MaterialItems), BufferCap);
        }

        public static string NamesOf(IReadOnlyList<ItemDef> items)
        {
            var parts = new List<string>(items.Count);
            foreach (ItemDef i in items)
            {
                parts.Add(i.Name);
            }
            return string.Join(Sep, parts);
        }

        public static string Sep => GameText.Language == GameLanguage.En ? ", " : "、";

        // ── 取料（HomeValleyFactory 队首开工时调用）───────────────────────────────────

        /// <summary>取料的结果。</summary>
        public enum TakeResult : byte
        {
            Taken = 0,
            /// <summary>材料不齐，代付关着（或代付的废料也不够）。</summary>
            Missing,
            /// <summary>要从仓库取，但仓库到装配站的路线被堵。</summary>
            RouteBlocked,
        }

        /// <summary>
        /// 队首开工取料（全有或全无）：先从装配站材料缓存、再从仓库（路线通畅时）取；还缺的按设置用废料代付（消费型事务）。
        /// 成功时把取走的材料与代付记在 <paramref name="item"/> 上；失败时什么都不动，写下每种还差多少（<see cref="FactoryQueueItemRecord.Shortfall"/>）。
        /// </summary>
        public static TakeResult TryTake(CampaignState state, FactoryQueueItemRecord item, out string routeText)
        {
            routeText = null;
            if (state.Economy?.AssemblyBuffer == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            ItemStackRecord[] need = item.Materials ?? Array.Empty<ItemStackRecord>();
            ItemStackRecord[] buf = state.Economy.AssemblyBuffer;
            var fromBuffer = new int[need.Length];
            var fromStore = new int[need.Length];
            var missing = new List<ItemStackRecord>(need.Length);
            bool needStore = false;
            for (int i = 0; i < need.Length; i++)
            {
                ItemStackRecord s = need[i];
                int inBuf = ProductionService.Count(buf, s.ItemId);
                fromBuffer[i] = Math.Min(inBuf, s.Amount);
                int rest = s.Amount - fromBuffer[i];
                if (rest > 0)
                {
                    int stock = HomeInventory.Stock(state, s.ItemId);
                    fromStore[i] = Math.Min(stock, rest);
                    needStore |= fromStore[i] > 0;
                    if (rest - fromStore[i] > 0)
                    {
                        missing.Add(new ItemStackRecord { ItemId = s.ItemId, Amount = rest - fromStore[i] });
                    }
                }
            }
            if (needStore)
            {
                // DEBT-FG2FW05-01：仓库到装配站的路线被建筑 / 地形围死时，仓库里的材料送不过去（与固件库详情页同一个判定）。
                // 模拟路径：缓存只按格网 / 地形 / 施工版本失效，不看真实时钟（倍速下结论一致，FGR-BASE-021）。
                Signal.FirmwareRouteInfo route = Signal.FirmwareLibrary.EvaluateRoute(state, sim: true);
                if (route.NoStation || (route.Reach != NavService.BuildingReach.Connected && route.Reach != NavService.BuildingReach.Unknown))
                {
                    routeText = Signal.FirmwareLibrary.RouteText(route);
                    item.Shortfall = need;
                    item.ScrapShortNeed = 0;
                    item.ScrapShortHave = 0;
                    return TakeResult.RouteBlocked;
                }
            }
            int sub = 0;
            if (missing.Count > 0)
            {
                ItemStackRecord[] miss = missing.ToArray();
                if (!ScrapSubstitute(state))
                {
                    item.Shortfall = miss;
                    item.ScrapShortNeed = 0;
                    item.ScrapShortHave = 0;
                    return TakeResult.Missing;
                }
                sub = SubstituteScrap(need, miss, item.ScrapPrice);
                if (sub > state.Scrap)
                {
                    item.Shortfall = miss;
                    item.ScrapShortNeed = sub;
                    item.ScrapShortHave = state.Scrap;
                    return TakeResult.Missing;
                }
            }
            // 全部够了：先扣废料（事务失败就整体不动），再取材料。
            if (sub > 0)
            {
                string tx = item.QueueItemId + ":sub";
                CampaignEconomyLedger.ProposeConsume(state, tx, item.QueueItemId, CampaignEconomyLedger.ResourceScrap, sub);
                CampaignEconomyLedger.LedgerResult r = CampaignEconomyLedger.Reserve(state, tx);
                if (!r.Success)
                {
                    CampaignEconomyLedger.Fail(state, tx, r.FailureReason);
                    item.Shortfall = missing.ToArray();
                    item.ScrapShortNeed = sub;
                    item.ScrapShortHave = state.Scrap;
                    return TakeResult.Missing;
                }
                CampaignEconomyLedger.MarkRunning(state, tx);
                item.SubstituteTxId = tx;
            }
            var taken = new List<ItemStackRecord>(need.Length);
            for (int i = 0; i < need.Length; i++)
            {
                string id = need[i].ItemId;
                int got = 0;
                if (fromBuffer[i] > 0)
                {
                    ProductionService.Add(ref state.Economy.AssemblyBuffer, id, -fromBuffer[i]);
                    got += fromBuffer[i];
                }
                if (fromStore[i] > 0 && ItemCatalog.TryGet(id, out ItemDef def))
                {
                    got += HomeInventory.RemoveUpTo(state, def, fromStore[i]);
                }
                if (got > 0)
                {
                    taken.Add(new ItemStackRecord { ItemId = id, Amount = got });
                }
            }
            HomeInventory.Touch();
            item.Taken = taken.ToArray();
            item.MaterialsTaken = true;
            item.SubstituteScrap = sub;
            item.Shortfall = null;
            item.ScrapShortNeed = 0;
            item.ScrapShortHave = 0;
            return TakeResult.Taken;
        }

        /// <summary>退回取走的材料与代付的废料（取消 / 装配站被毁 / 出厂失败）：材料进仓库（放不下落地 + 搬运单），废料事务取消退款。返回退回的件数。</summary>
        public static int Refund(CampaignState state, FactoryQueueItemRecord item, string why)
        {
            int n = 0;
            BuildingRecord st = Station(state);
            Vector2 at = st?.Position ?? HomeValleyLayout.CameraFocusStart;
            int k = 0;
            foreach (ItemStackRecord s in item.Taken ?? Array.Empty<ItemStackRecord>())
            {
                if (s == null || s.Amount <= 0)
                {
                    continue;
                }
                HomeValleyConstruction.ReturnMaterials(state, at, ItemCatalog.ResourceTypeOf(s.ItemId), s.Amount,
                    item.QueueItemId + ":" + why + ":" + (k++).ToString(CultureInfo.InvariantCulture));
                n += s.Amount;
            }
            item.Taken = Array.Empty<ItemStackRecord>();
            item.MaterialsTaken = false;
            if (!string.IsNullOrEmpty(item.SubstituteTxId))
            {
                CampaignEconomyLedger.Cancel(state, item.SubstituteTxId);
            }
            item.SubstituteScrap = 0;
            HomeInventory.Touch();
            return n;
        }

        /// <summary>完工：取走的材料就此消耗（只清记录），代付事务确认。</summary>
        public static void Consume(CampaignState state, FactoryQueueItemRecord item)
        {
            if (!string.IsNullOrEmpty(item.SubstituteTxId))
            {
                CampaignEconomyLedger.Commit(state, item.SubstituteTxId);
            }
            if (item.SubstituteScrap <= 0)
            {
                state.Economy.MachinesFromLine++;
            }
        }

        // ── 文字 ─────────────────────────────────────────────────────────────────

        /// <summary>材料清单的一行：“结构材 ×4（装配站 1、仓库 2）、零件 ×4（…）”。</summary>
        public static string DescribeMaterials(CampaignState state, ItemStackRecord[] materials)
        {
            if (materials == null || materials.Length == 0)
            {
                return GameText.Get("asm.panel.none");
            }
            var parts = new List<string>(materials.Length);
            foreach (ItemStackRecord s in materials)
            {
                parts.Add(GameText.Format("asm.panel.material", ItemCatalog.NameOf(s.ItemId), s.Amount, InBuffer(state, s.ItemId), HomeInventory.Stock(state, s.ItemId)));
            }
            return string.Join(Sep, parts);
        }

        public static string DescribeStacks(ItemStackRecord[] stacks)
        {
            if (stacks == null || stacks.Length == 0)
            {
                return GameText.Get("asm.panel.none");
            }
            var parts = new List<string>(stacks.Length);
            foreach (ItemStackRecord s in stacks)
            {
                if (s != null && s.Amount > 0)
                {
                    parts.Add(GameText.Format("asm.panel.stack", ItemCatalog.NameOf(s.ItemId), s.Amount));
                }
            }
            return parts.Count == 0 ? GameText.Get("asm.panel.none") : string.Join(Sep, parts);
        }

        /// <summary>等待材料的原因（写明缺什么、差多少、怎么办）。</summary>
        public static string DescribeShortfall(FactoryQueueItemRecord item)
        {
            var parts = new List<string>(3);
            foreach (ItemStackRecord s in item?.Shortfall ?? Array.Empty<ItemStackRecord>())
            {
                if (s != null && s.Amount > 0)
                {
                    parts.Add(GameText.Format("asm.reason.short", ItemCatalog.NameOf(s.ItemId), s.Amount));
                }
            }
            string list = parts.Count == 0 ? GameText.Get("asm.panel.none") : string.Join(Sep, parts);
            string text = item != null && item.ScrapShortNeed > 0
                ? GameText.Format("asm.reason.materials_scrap", list, item.ScrapShortNeed, item.ScrapShortHave)
                : GameText.Format("asm.reason.materials", list);
            // 输入口被缓存已满的材料占着、而它不是缺的那种：写明“同一条带后面的材料进不来”与解法（不然玩家照原因送料也没用）。
            string jam = PortJamItem;
            if (!string.IsNullOrEmpty(jam) && ProductionService.Count(item?.Shortfall, jam) <= 0)
            {
                text += GameText.Format("asm.reason.port_jam", ItemCatalog.NameOf(jam), BufferCap);
            }
            return text;
        }
    }
}
