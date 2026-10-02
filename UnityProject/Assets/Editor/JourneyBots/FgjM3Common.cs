using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Logistics;
using GameLogic.Campaign;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Settings;
using GameLogic.Stage;
using GameLogic.UI.Kit;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameLogic.EditorTools.JourneyBots
{
    /// <summary>
    /// FG3-E2E-01：M3 两条旅程（FGJ-M3 / FGJ-M3R）共用的部分——镜头与点地面（先确认没被界面挡住、在画面里）、建造模式与选工具、
    /// 按格网规则现找传送带路线与抽水线位置（B25：不写死坐标）、产线读数（端口累计收货、泵累计抽取、储罐存量、传送带等级）、存读档摘要、出征名单。
    ///
    /// 全部玩家动作都经 <see cref="JourneyInput"/> 的正式输入通道（键鼠后端 / UI Toolkit 指针事件 / uGUI EventSystem）；
    /// 这里的“找位置”“读数”只读游戏状态，不调放置、施工、撤销、复制、布局库、诊断等业务方法（[M3 出口] A 段扫描源码守护）。
    /// </summary>
    internal static class FgjM3Common
    {
        internal const string BuildHost = "[BuildModeHudHost]";
        internal const string DiagHost = "[DiagnosisPanelHost]";
        internal const string LibHost = "[LayoutLibraryHost]";
        internal const string ToolBeltT1 = "belt_t1";
        internal const string ToolPipeT1 = "pipe_t1";
        internal const string ToolPump = "pump";
        internal const string ToolTank = "tank";

        internal static CampaignState St => CampaignSession.Current;

        internal static HomeValleyBuildMode Mode => HomeValleyBuildMode.Current;

        internal static bool BuildOpen => Mode != null && Mode.IsOpen;

        internal static long NowMs() => FgjM2Common.NowMs();

        internal static string Label(int id) => FgjM1Journey.Label(id);

        internal static string F(double v, string fmt = "0.##") => v.ToString(fmt, CultureInfo.InvariantCulture);

        // ── 帧耗时（120 帧口径的参照，Editor batchmode 无图形设备）──────────────────────────

        private static readonly List<float> FrameMs = new List<float>(8192);
        private static int _lastFrame = -1;

        internal static void ResetSampling()
        {
            FrameMs.Clear();
            _lastFrame = -1;
        }

        /// <summary>产线运行 / 等施工 / 离家这些“世界在跑、玩家在看”的步骤里每帧采一次（同一帧只采一次）。</summary>
        internal static void SampleFrame()
        {
            if (Time.frameCount == _lastFrame)
            {
                return;
            }
            _lastFrame = Time.frameCount;
            FrameMs.Add(Time.unscaledDeltaTime * 1000f);
        }

        internal static string FrameReport(string what)
        {
            if (FrameMs.Count < 10)
            {
                return "帧耗时采样不足";
            }
            List<float> sorted = FrameMs.OrderBy(x => x).ToList();
            return $"{what}帧耗时（Editor batchmode 无图形设备，帧率上限 120，{sorted.Count} 帧）p50 {sorted[sorted.Count / 2]:F2} ms、p95 {sorted[(int)(sorted.Count * 0.95)]:F2} ms、" +
                   $"最大 {sorted[sorted.Count - 1]:F1} ms（120 帧预算 8.33 ms 仅作参照）";
        }

        /// <summary>格子落在某座家园建筑的占地（外扩 <paramref name="margin"/> 格）里。</summary>
        internal static bool NearBuilding(GridCell p, int margin)
        {
            foreach (BuildingRecord b in St?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId || !GridContent.TryGetBuilding(b.BuildingTypeId, out GameConfig.fg.BuildingGrid g))
                {
                    continue;
                }
                GridMath.FootprintBounds(new GridCell(b.GridX, b.GridY), g.FootprintW, g.FootprintH, (int)b.Rotation, out GridCell min, out GridCell max);
                if (p.X >= min.X - margin && p.X <= max.X + margin && p.Y >= min.Y - margin && p.Y <= max.Y + margin)
                {
                    return true;
                }
            }
            return false;
        }

        // ── 一步里的一次性动作 ────────────────────────────────────────────────────────

        /// <summary>本步（含第几次尝试）的键：同一步重试时从头来，不同步之间不串。</summary>
        internal static string SK(JourneyContext c, string k) => "s" + c.StepIndex.ToString(CultureInfo.InvariantCulture) + "." + c.Attempt.ToString(CultureInfo.InvariantCulture) + "." + k;

        /// <summary>
        /// 一次性输入动作：<paramref name="act"/> 返回 true = 输入已发出（记下时刻）；返回 false = 这一帧先平移了镜头（目标不在画面里 / 被界面挡住），
        /// 等方向键松开、镜头停稳 0.35 真实秒后再试。返回 true 表示动作已经发出。
        /// </summary>
        internal static bool Once(JourneyContext c, string k, Func<bool> act)
        {
            string key = SK(c, k);
            if (c.GetInt(key) == 1)
            {
                return true;
            }
            if (JourneyInput.Holding)
            {
                c.SetLong(key + ".ps", 0);
                return false;
            }
            long settle = c.GetLong(key + ".ps");
            if (settle == -1)
            {
                c.SetLong(key + ".ps", Math.Max(1, NowMs()));
                return false;
            }
            if (settle > 0 && NowMs() - settle < 350)
            {
                return false;
            }
            if (act())
            {
                c.SetInt(key, 1);
                c.SetLong(key + ".at", NowMs());
                return true;
            }
            c.SetLong(key + ".ps", -1);
            c.SetInt(key + ".pans", c.GetInt(key + ".pans") + 1);
            return false;
        }

        /// <summary><see cref="Once"/> 发出动作之后过了多少真实毫秒（没发出时返回 -1）。</summary>
        internal static long SinceMs(JourneyContext c, string k)
        {
            string key = SK(c, k);
            return c.GetInt(key) == 1 ? NowMs() - c.GetLong(key + ".at") : -1;
        }

        internal static bool Done(JourneyContext c, string k) => c.GetInt(SK(c, k)) == 1;

        internal static void Mark(JourneyContext c, string k) => c.SetInt(SK(c, k), 1);

        // ── 镜头与点地面 ──────────────────────────────────────────────────────────────

        internal static Vector2 Ground(GridCell cell) => new Vector2(cell.X, cell.Y);

        /// <summary>地面点在画面里（离边缘至少 12%）且没有被界面挡住。</summary>
        internal static bool Clear(Vector2 ground, out string why)
        {
            if (!JourneyInput.OnScreen(ground, 0.12f))
            {
                why = "不在画面里";
                return false;
            }
            why = JourneyInput.UiCoverAt(JourneyInput.ScreenOf(ground));
            if (why != null)
            {
                why = "被界面挡住（" + why + "）";
                return false;
            }
            return true;
        }

        /// <summary>按住朝目标的方向键 0.3 真实秒（玩家平移镜头的做法）：目标不在画面里、或在画面里但被界面挡住时都平移，把它带到画面中间。</summary>
        internal static void PanTo(Vector2 target)
        {
            if (JourneyInput.Holding)
            {
                return;
            }
            Unity.Mathematics.float2 f = WorldView.Director.StrategyFocus;
            Vector2 d = target - new Vector2(f.x, f.y);
            var keys = new List<KeyCode>(2);
            if (Mathf.Abs(d.x) > 1.5f)
            {
                keys.Add(GameSettings.KeyBindings.GetKey(d.x > 0 ? GameActionId.StrategyPanRight : GameActionId.StrategyPanLeft));
            }
            if (Mathf.Abs(d.y) > 1.5f)
            {
                keys.Add(GameSettings.KeyBindings.GetKey(d.y > 0 ? GameActionId.StrategyPanUp : GameActionId.StrategyPanDown));
            }
            if (keys.Count == 0)
            {
                // 已经在镜头中心附近还被挡住：往下平移一点（面板多在左侧 / 底部），让目标离开面板。
                keys.Add(GameSettings.KeyBindings.GetKey(GameActionId.StrategyPanDown));
            }
            JourneyCommon.PanPresses++;
            JourneyInput.HoldKeys(keys, 0.3);
        }

        internal static bool TryClick(Vector2 ground, int button = 0)
        {
            if (!Clear(ground, out _))
            {
                PanTo(ground);
                return false;
            }
            JourneyInput.Click(ground, button);
            return true;
        }

        internal static bool TryHover(Vector2 ground)
        {
            if (!Clear(ground, out _))
            {
                PanTo(ground);
                return false;
            }
            JourneyInput.Hover(ground);
            return true;
        }

        /// <summary>按住左键从 a 拖到 b（两端都要在画面里、没被界面挡住，否则先平移到两端中点）。</summary>
        internal static bool TryDrag(Vector2 a, Vector2 b)
        {
            if (!Clear(a, out _) || !Clear(b, out _))
            {
                PanTo((a + b) * 0.5f);
                return false;
            }
            JourneyInput.Drag(a, b);
            return true;
        }

        /// <summary>光标停在格子上、建造模式确认指着它之后，再按一次动作键（光标留在原处）。两阶段：先悬停，下一次调用再按。返回 true = 已按下。</summary>
        internal static bool HoverThenPress(JourneyContext c, string k, GridCell cell, GameActionId action)
        {
            if (!Once(c, k + ".hover", () => TryHover(Ground(cell))))
            {
                return false;
            }
            if (Mode == null || !Mode.HasHover || Mode.HoverCell != cell)
            {
                if (SinceMs(c, k + ".hover") > 1500)
                {
                    c.SetInt(SK(c, k + ".hover"), 0); // 悬停没落到这一格（镜头还在动）：重新悬停
                }
                return false;
            }
            return Once(c, k + ".press", () =>
            {
                JourneyInput.PressAction(action, JourneyInput.ScreenOf(Ground(cell)));
                return true;
            });
        }

        // ── 建造模式 ──────────────────────────────────────────────────────────────────

        internal static void PressBuild(bool want) => JourneyInput.PressToggleTo(GameActionId.OpenBuildMenu, () => BuildOpen, want);

        internal static StepOutcome TickBuild(JourneyContext c, bool want)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            if (BuildOpen != want)
            {
                return StepOutcome.Retry($"按建造键后建造模式{(want ? "没有打开" : "没有关闭")}");
            }
            if (want)
            {
                BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
                return hud != null && hud.PanelVisible && InputRouter.ActiveContext == InputContext.Build
                    ? StepOutcome.Done($"按建造键（默认 {InputDisplay.ForAction(GameActionId.OpenBuildMenu)}）打开建造模式：建造栏 {hud.CategoryCount} 个分类、半透明地格参考线叠在地形上")
                    : StepOutcome.Retry("建造模式打开了但建造栏没有显示");
            }
            return StepOutcome.Done("再按建造键关闭建造模式（回到普通战略视角，没有地格表现）");
        }

        /// <summary>
        /// 在建造栏里选一个条目：先点它所在的分类页签，再在条目列表里按条目 ID 找到序号（不写死序号）；条目在列表可见区外时像玩家一样在列表上滚一下滚轮
        /// （返回 false、<paramref name="scrolling"/> = true，下一帧布局更新后再点）。
        /// </summary>
        internal static bool PickEntry(string entryId, out string why, out bool scrolling)
        {
            BuildModeHudUIToolkit hud = BuildModeHudUIToolkit.Instance;
            why = null;
            scrolling = false;
            if (hud == null || !BuildOpen)
            {
                why = "建造模式没有打开";
                return false;
            }
            if (!BuildCatalog.TryGet(entryId, out BuildEntry e))
            {
                why = "建造目录里没有 " + entryId;
                return false;
            }
            if (hud.SelectedCategoryId != e.CategoryId)
            {
                int cat = GridContent.Categories.ToList().FindIndex(x => x.Id == e.CategoryId);
                if (cat < 0 || !JourneyInput.ClickUitk(BuildHost, "BuildCat" + cat.ToString(CultureInfo.InvariantCulture)))
                {
                    why = $"点不到分类页签 {e.CategoryId}：{JourneyInput.LastUiFailure}";
                    return false;
                }
                scrolling = true; // 换了分类：条目列表下一帧才排好版
                return false;
            }
            for (int i = 0; i < hud.ItemCount; i++)
            {
                if (hud.ItemId(i) != entryId)
                {
                    continue;
                }
                string name = "BuildItem" + i.ToString(CultureInfo.InvariantCulture);
                VisualElement item = JourneyInput.FindUitk<VisualElement>(BuildHost, name);
                if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>(BuildHost, "BuildList"), item))
                {
                    scrolling = true;
                    return false;
                }
                if (!JourneyInput.ClickUitk(BuildHost, name))
                {
                    why = $"点不到条目 {entryId}：{JourneyInput.LastUiFailure}";
                    return false;
                }
                return true;
            }
            why = $"分类 {e.CategoryId} 的条目里没有 {entryId}";
            return false;
        }

        internal static bool EntrySelected(string entryId) => Mode != null && (Mode.SelectedToolId == entryId || Mode.SelectedTypeId == entryId);

        internal static StepOutcome TickPick(JourneyContext c, string entryId)
        {
            if (!Done(c, "pick"))
            {
                if (!PickEntry(entryId, out string why, out bool scrolling))
                {
                    if (scrolling && c.GetInt(SK(c, "scrolls")) < 40)
                    {
                        c.SetInt(SK(c, "scrolls"), c.GetInt(SK(c, "scrolls")) + 1);
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Retry("选不中 " + entryId + "：" + (why ?? "滚了 40 次仍不在可见区"));
                }
                Mark(c, "pick");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 0.4)
            {
                return StepOutcome.Wait;
            }
            return EntrySelected(entryId)
                ? StepOutcome.Done($"点“{BuildModeHudUIToolkit.Instance?.CategoryText(Math.Max(0, GridContent.Categories.ToList().FindIndex(x => x.Id == BuildModeHudUIToolkit.Instance?.SelectedCategoryId)))}”分类再点条目：选中 {entryId}（建造栏提示“{BuildModeHudUIToolkit.Instance?.HintLabelText}”）")
                : StepOutcome.Retry($"点了条目后选中的是 {Mode?.SelectedEntryId}");
        }

        // ── 方向与格子 ────────────────────────────────────────────────────────────────

        private static readonly int[] DX = { 0, 1, 0, -1 };
        private static readonly int[] DY = { 1, 0, -1, 0 };

        internal static GridCell Step(GridCell c, int d, int n = 1) => new GridCell(c.X + DX[d & 3] * n, c.Y + DY[d & 3] * n);

        internal static int DirOf(GridCell a, GridCell b) => b.X > a.X ? 1 : b.X < a.X ? 3 : b.Y > a.Y ? 0 : 2;

        internal static long Key(GridCell c) => ((long)c.X << 32) ^ (uint)c.Y;

        internal static string DirName(int d) => GameLogic.Localization.GameText.Get(GridMath.DirTextKey((GridDir)(d & 3)));

        internal static string Cell(GridCell c) => $"({c.X}, {c.Y})";

        internal static string EncodeCells(IEnumerable<GridCell> cells) =>
            string.Join(";", cells.Select(x => x.X.ToString(CultureInfo.InvariantCulture) + "," + x.Y.ToString(CultureInfo.InvariantCulture)));

        internal static List<GridCell> DecodeCells(string s) =>
            (s ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(','))
                .Select(p => new GridCell(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)))
                .ToList();

        internal static void SetCell(JourneyContext c, string key, GridCell cell)
        {
            c.SetInt(key + ".x", cell.X);
            c.SetInt(key + ".y", cell.Y);
        }

        internal static GridCell GetCell(JourneyContext c, string key) => new GridCell(c.GetInt(key + ".x"), c.GetInt(key + ".y"));

        // ── 建筑端口 ──────────────────────────────────────────────────────────────────

        internal static BuildingRecord Home(string typeId) =>
            St?.BuildingRecords?.FirstOrDefault(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == typeId);

        /// <summary>建筑端口外侧那一格（铺传送带的格）与端口朝外的方向。找不到返回 false。</summary>
        internal static bool PortBeltCell(string typeId, bool output, out GridCell beltCell, out int outward, out string portKey)
        {
            beltCell = default;
            outward = 0;
            portKey = null;
            BuildingRecord b = Home(typeId);
            if (b == null)
            {
                return false;
            }
            var ports = new List<PortPlacement>(4);
            HomeGridService.PortsFor(b.BuildingTypeId, new GridCell(b.GridX, b.GridY), (int)b.Rotation, ports);
            foreach (PortPlacement p in ports)
            {
                if (p.IsOutput != output)
                {
                    continue;
                }
                outward = (int)p.Dir;
                beltCell = Step(p.Cell, outward);
                portKey = p.PortId;
                return true;
            }
            return false;
        }

        /// <summary>家园全部建筑端口外侧的格（路线要绕开：别的建筑的输入口前面不能正好朝它铺带）。</summary>
        internal static HashSet<long> AllPortCells(CampaignState s)
        {
            var set = new HashSet<long>();
            var ports = new List<PortPlacement>(4);
            foreach (BuildingRecord b in s?.BuildingRecords ?? Array.Empty<BuildingRecord>())
            {
                if (b == null || b.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                HomeGridService.PortsFor(b.BuildingTypeId, new GridCell(b.GridX, b.GridY), (int)b.Rotation, ports);
                foreach (PortPlacement p in ports)
                {
                    set.Add(Key(Step(p.Cell, (int)p.Dir)));
                }
            }
            return set;
        }

        // ── 路线（B25：按格网规则现找）────────────────────────────────────────────────

        /// <summary>
        /// 传送带路线：从 <paramref name="start"/>（第一格）到 <paramref name="goal"/>（最后一格，它自己的方向 = <paramref name="goalDir"/>，朝着建筑），
        /// 每格 <see cref="HomeGridService.ValidateBeltCell"/> 合法、不在 <paramref name="avoid"/> 里；第一格不能朝 <paramref name="forbidFirst"/>（输出口不往指回建筑的带上推）。
        /// 代价 = 格数 + 每个转弯 4（少转弯 = 少拖几段）。在两端外扩 <paramref name="margin"/> 格的框里找（Dijkstra，格 × 进入方向）。找不到返回 null。
        /// </summary>
        internal static List<GridCell> PlanRoute(CampaignState s, GridCell start, GridCell goal, int goalDir, int forbidFirst, ISet<long> avoid, int margin)
        {
            int minX = Math.Min(start.X, goal.X) - margin;
            int maxX = Math.Max(start.X, goal.X) + margin;
            int minY = Math.Min(start.Y, goal.Y) - margin;
            int maxY = Math.Max(start.Y, goal.Y) + margin;
            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            const int turn = 4;
            var okCache = new Dictionary<long, bool>();
            bool Ok(GridCell c)
            {
                if (c.X < minX || c.X > maxX || c.Y < minY || c.Y > maxY)
                {
                    return false;
                }
                long k = Key(c);
                if (okCache.TryGetValue(k, out bool v))
                {
                    return v;
                }
                v = (c == start || c == goal || avoid == null || !avoid.Contains(k)) && HomeGridService.ValidateBeltCell(s, c).Ok;
                okCache[k] = v;
                return v;
            }
            if (!Ok(start) || !Ok(goal))
            {
                return null;
            }
            int Idx(GridCell c) => (c.Y - minY) * w + (c.X - minX);
            int n = w * h * 4;
            var dist = new int[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = int.MaxValue;
                prev[i] = int.MinValue;
            }
            var pq = new SortedSet<(int cost, int state)>();
            for (int d = 0; d < 4; d++)
            {
                if (d == forbidFirst)
                {
                    continue;
                }
                GridCell nb = Step(start, d);
                if (!Ok(nb))
                {
                    continue;
                }
                int st = Idx(nb) * 4 + d;
                dist[st] = 1;
                prev[st] = -1; // 上一格是起点
                pq.Add((1, st));
            }
            int best = int.MaxValue;
            int bestState = -1;
            while (pq.Count > 0)
            {
                (int cost, int st) = pq.Min;
                pq.Remove(pq.Min);
                if (cost > dist[st] || cost >= best)
                {
                    continue;
                }
                int cellIdx = st / 4;
                int din = st % 4;
                var cell = new GridCell(minX + cellIdx % w, minY + cellIdx / w);
                if (cell == goal)
                {
                    int total = cost + (din != goalDir ? turn : 0);
                    if (total < best)
                    {
                        best = total;
                        bestState = st;
                    }
                    continue;
                }
                for (int d = 0; d < 4; d++)
                {
                    if (d == ((din + 2) & 3))
                    {
                        continue;
                    }
                    GridCell nb = Step(cell, d);
                    if (nb == start || !Ok(nb))
                    {
                        continue;
                    }
                    int ns = Idx(nb) * 4 + d;
                    int nc = cost + 1 + (d != din ? turn : 0);
                    if (nc < dist[ns])
                    {
                        dist[ns] = nc;
                        prev[ns] = st;
                        pq.Add((nc, ns));
                    }
                }
            }
            if (bestState < 0)
            {
                return null;
            }
            var path = new List<GridCell>();
            int cur = bestState;
            while (cur >= 0)
            {
                int ci = cur / 4;
                path.Add(new GridCell(minX + ci % w, minY + ci / w));
                cur = prev[cur];
            }
            path.Add(start);
            path.Reverse();
            return path;
        }

        /// <summary>路线上每一格的方向：指向下一格，最后一格 = <paramref name="goalDir"/>。</summary>
        internal static List<int> RouteDirs(List<GridCell> path, int goalDir)
        {
            var dirs = new List<int>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                dirs.Add(i + 1 < path.Count ? DirOf(path[i], path[i + 1]) : goalDir);
            }
            return dirs;
        }

        /// <summary>路线切成“同一方向的连续格”一段一段（每段按住左键从第一格拖到最后一格；只有一格的段先按旋转键转到方向再单击）。</summary>
        internal static List<(int a, int b, int dir)> Runs(List<GridCell> path, int goalDir)
        {
            List<int> dirs = RouteDirs(path, goalDir);
            var runs = new List<(int, int, int)>();
            int start = 0;
            for (int i = 1; i <= path.Count; i++)
            {
                if (i == path.Count || dirs[i] != dirs[start])
                {
                    runs.Add((start, i - 1, dirs[start]));
                    start = i;
                }
            }
            return runs;
        }

        // ── 铺路线（多段拖拽的状态机）──────────────────────────────────────────────────

        /// <summary>
        /// 把 <paramref name="pathKey"/> 记下的路线一段一段铺出来（选中的工具必须已经是传送带）：每段按住左键拖（单格段先按旋转键转向再单击），
        /// 松开后核对这一段每格都成了规划中的虚影、方向对。全部铺完返回 Done。
        /// </summary>
        internal static StepOutcome TickLayRoute(JourneyContext c, string pathKey, int goalDir, string toolId)
        {
            List<GridCell> path = DecodeCells(c.Get(pathKey));
            List<(int a, int b, int dir)> runs = Runs(path, goalDir);
            List<int> dirs = RouteDirs(path, goalDir);
            string ri = SK(c, "run");
            int k = c.GetInt(ri);
            if (k >= runs.Count)
            {
                int planned = path.Count(p => HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _));
                return planned == path.Count
                    ? StepOutcome.Done($"{runs.Count} 段拖拽（其中单格段 {runs.Count(r => r.a == r.b)} 段先按旋转键转向）铺出 {path.Count} 格传送带虚影，方向逐格正确；" +
                                       $"拖的时候建造栏写“{c.Get("dragInfo").Replace("\n", " ")}”；放下虚影不扣料（废料 {St.Scrap}）")
                    : StepOutcome.Fail($"铺完后规划中的格子只有 {planned}/{path.Count}");
            }
            if (!EntrySelected(toolId))
            {
                return StepOutcome.Fail($"铺第 {k + 1} 段时选中的不是 {toolId}（{Mode?.SelectedEntryId}）");
            }
            (int a, int b, int dir) run = runs[k];
            string rk = "r" + k.ToString(CultureInfo.InvariantCulture);
            GridCell ca = path[run.a];
            GridCell cb = path[run.b];
            if (run.a == run.b)
            {
                int want = run.dir * 90;
                if (GridMath.NormalizeRotation(Mode.GhostRotation) != want)
                {
                    if (JourneyInput.Holding || NowMs() - c.GetLong(SK(c, rk + ".rot")) < 250)
                    {
                        return StepOutcome.Wait;
                    }
                    c.SetLong(SK(c, rk + ".rot"), NowMs());
                    JourneyInput.PressAction(GameActionId.Rotate);
                    return StepOutcome.Wait;
                }
                if (!Once(c, rk, () => TryClick(Ground(ca))))
                {
                    return StepOutcome.Wait;
                }
            }
            else if (!Once(c, rk, () => TryDrag(Ground(ca), Ground(cb))))
            {
                return StepOutcome.Wait;
            }
            string info = BuildModeHudUIToolkit.Instance?.DragInfoText ?? string.Empty;
            if (info.Length > 0 && run.a != run.b)
            {
                c.Set("dragInfo", info);
            }
            if (SinceMs(c, rk) < 500)
            {
                return StepOutcome.Wait;
            }
            for (int i = run.a; i <= run.b; i++)
            {
                if (!HomeValleyConstruction.TryFindPlannedCell(St, path[i], out PlannedBeltRecord p, out int idx) || p.Dirs == null || idx < 0 || idx >= p.Dirs.Length
                    || p.Dirs[idx] != dirs[i])
                {
                    if (SinceMs(c, rk) < 1500)
                    {
                        return StepOutcome.Wait;
                    }
                    return StepOutcome.Retry($"第 {k + 1} 段 {Cell(ca)}→{Cell(cb)} 松开后 {Cell(path[i])} 不是朝{DirName(dirs[i])}的虚影（状态行“{Mode?.StatusText}”）");
                }
            }
            c.SetInt(ri, k + 1);
            return StepOutcome.Wait;
        }

        // ── 抽水线（水源 → 泵 → 3 格管线 → 储罐）────────────────────────────────────────

        internal const int PipeCells = 3;

        /// <summary>抽水线的格子：泵、管线 1～3、储罐。</summary>
        internal static GridCell[] WaterLine(GridCell pump, int dir) =>
            Enumerable.Range(0, PipeCells + 2).Select(i => Step(pump, dir, i)).ToArray();

        /// <summary>
        /// 按种子地形找一处能放抽水线的水源（B25）：泵格是水源且能放泵；朝某个方向 3 格管线 + 1 格储罐都能放、都不是水源，
        /// 两侧与储罐外侧一格没有别的管线件（不和别的网络连在一起），也不在 <paramref name="avoid"/> 里。按离 <paramref name="near"/> 由近到远找。
        /// </summary>
        internal static bool FindWaterLine(CampaignState s, GridCell near, int minR, int maxR, ISet<long> avoid, out GridCell pump, out int dir)
        {
            pump = default;
            dir = 0;
            int water = PipeNetworkService.FluidId("water");
            for (int r = minR; r <= maxR; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                        {
                            continue;
                        }
                        var w = new GridCell(near.X + dx, near.Y + dy);
                        if (avoid != null && avoid.Contains(Key(w)))
                        {
                            continue;
                        }
                        if (PipeNetworkService.SourceFluidAt(s, w) != water || !HomeGridService.ValidatePipeCell(s, w, PipePieceKind.Pump).Ok)
                        {
                            continue;
                        }
                        for (int d = 0; d < 4; d++)
                        {
                            if (WaterLineFits(s, w, d, avoid))
                            {
                                pump = w;
                                dir = d;
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        internal static bool WaterLineFits(CampaignState s, GridCell pump, int d, ISet<long> avoid)
        {
            GridCell[] line = WaterLine(pump, d);
            for (int i = 1; i < line.Length; i++)
            {
                GridCell p = line[i];
                PipePieceKind kind = i == line.Length - 1 ? PipePieceKind.Tank : PipePieceKind.Pipe;
                if ((avoid != null && avoid.Contains(Key(p))) || PipeNetworkService.SourceFluidAt(s, p) != 0 || !HomeGridService.ValidatePipeCell(s, p, kind).Ok)
                {
                    return false;
                }
                foreach (GridCell side in new[] { Step(p, d + 1), Step(p, d + 3) })
                {
                    if (PipeNetworkService.TryGetPiece(side, out _, out _) || HomeValleyConstruction.TryFindPlannedCell(s, side, out _, out _))
                    {
                        return false;
                    }
                }
            }
            GridCell beyond = Step(line[line.Length - 1], d);
            return !PipeNetworkService.TryGetPiece(beyond, out _, out _) && !HomeValleyConstruction.TryFindPlannedCell(s, beyond, out _, out _);
        }

        // ── 读数（只读）────────────────────────────────────────────────────────────────

        internal static bool BeltInfo(GridCell cell, out BeltCellInfo info)
        {
            info = default;
            return BeltNetworkService.IsRunning && BeltNetworkService.Kernel.TryGetCellInfo(cell.X, cell.Y, out info);
        }

        internal static bool PipeInfo(GridCell cell, out PipeCellInfo info)
        {
            info = default;
            return PipeNetworkService.Kernel != null && PipeNetworkService.Kernel.TryGetCellInfo(cell.X, cell.Y, out info);
        }

        internal static long PumpTotalMl(GridCell pump) => PipeInfo(pump, out PipeCellInfo i) && i.Kind == PipePieceKind.Pump ? i.PumpTotalMl : -1;

        internal static long TankMl(GridCell tank) => PipeInfo(tank, out PipeCellInfo i) && i.Kind == PipePieceKind.Tank ? i.TankStockMl : -1;

        internal static long TankCapMl(GridCell tank) => PipeInfo(tank, out PipeCellInfo i) && i.Kind == PipePieceKind.Tank ? i.TankCapacityMl : -1;

        /// <summary>建筑某个端口在传送带内核里的累计件数（输入口 = 收货、输出口 = 推上）。没绑定返回 -1。</summary>
        internal static long PortTotal(string typeId, string portKey, out bool connected)
        {
            connected = false;
            BuildingRecord b = Home(typeId);
            BeltPortService.Binding bind = b != null ? BeltPortService.Find(b.BuildingId, portKey) : null;
            if (bind == null || bind.PortId < 0 || !BeltNetworkService.IsRunning || !BeltNetworkService.Kernel.TryGetPortInfo(bind.PortId, out BeltPortInfo info))
            {
                return -1;
            }
            connected = info.Connected;
            return info.Total;
        }

        /// <summary>路线上每一格的传送带等级（0 = T1）；有格不存在时返回 -1。</summary>
        internal static int MinTier(IEnumerable<GridCell> cells, out int maxTier, out int missing)
        {
            int min = int.MaxValue;
            maxTier = -1;
            missing = 0;
            foreach (GridCell p in cells)
            {
                if (!BeltInfo(p, out BeltCellInfo i))
                {
                    missing++;
                    continue;
                }
                min = Math.Min(min, i.Tier);
                maxTier = Math.Max(maxTier, i.Tier);
            }
            return min == int.MaxValue ? -1 : min;
        }

        internal static bool AllBuilt(IEnumerable<GridCell> beltCells, IEnumerable<GridCell> pipeCells, out int unbuilt)
        {
            unbuilt = 0;
            foreach (GridCell p in beltCells ?? Enumerable.Empty<GridCell>())
            {
                if (!BeltInfo(p, out _) || HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _))
                {
                    unbuilt++;
                }
            }
            foreach (GridCell p in pipeCells ?? Enumerable.Empty<GridCell>())
            {
                if (!PipeInfo(p, out _) || HomeValleyConstruction.TryFindPlannedCell(St, p, out _, out _))
                {
                    unbuilt++;
                }
            }
            return unbuilt == 0;
        }

        // ── 存读档摘要 ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 存档层面的摘要（存档文件与存档那一刻对照）：世界步、废料、家园建筑（类型 @ 位置 : 状态）、规划中的物流件、撤销 / 重做栈深度、
        /// 管线存档里的泵 / 储罐读数。<paramref name="s"/> 可以是读盘得到的状态（没有内核）。
        /// </summary>
        internal static string StateDigest(CampaignState s)
        {
            if (s == null)
            {
                return "（没有战役）";
            }
            var sb = new StringBuilder();
            sb.Append("步=").Append(s.Clock?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "?");
            sb.Append("｜废料=").Append(s.Scrap.ToString(CultureInfo.InvariantCulture));
            IEnumerable<BuildingRecord> home = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(b => b.BuildingId, StringComparer.Ordinal);
            sb.Append("｜建筑=").Append(string.Join(",", home.Select(b => $"{b.BuildingTypeId}@{b.GridX},{b.GridY}:{b.ConstructionState}")));
            PlannedBeltRecord[] planned = s.Grid?.PlannedBelts ?? Array.Empty<PlannedBeltRecord>();
            sb.Append("｜规划=").Append(planned.Length.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(planned.Sum(p => HomeValleyConstruction.UnbuiltCells(p)).ToString(CultureInfo.InvariantCulture));
            sb.Append("｜撤销=").Append(PlanHistory.UndoSteps(s).ToString(CultureInfo.InvariantCulture))
                .Append("｜重做=").Append(PlanHistory.RedoSteps(s).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>存档层面的静态摘要（读档后世界一开跑就会变的世界步、库存、施工进度不算）：家园建筑（类型 @ 位置 : 状态）、撤销 / 重做栈深度。</summary>
        internal static string StableDigest(CampaignState s)
        {
            IEnumerable<BuildingRecord> home = (s?.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(b => b != null && b.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(b => b.BuildingId, StringComparer.Ordinal);
            return "建筑=" + string.Join(",", home.Select(b => $"{b.BuildingTypeId}@{b.GridX},{b.GridY}:{b.ConstructionState}"))
                   + "｜撤销=" + PlanHistory.UndoSteps(s).ToString(CultureInfo.InvariantCulture) + "｜重做=" + PlanHistory.RedoSteps(s).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>家园物流守恒量（运行中）：库存 + 各建筑端口待推 / 缓存 + 传送带上的物品 + 地上的废料 + 家园机器货舱。物品只在这些地方之间移动，施工不在进行时总数不变。</summary>
        internal static long Conserved()
        {
            CampaignState s = St;
            long ports = 0;
            foreach (BeltPortService.Binding b in BeltPortService.All)
            {
                if (BeltNetworkService.Kernel.TryGetPortCounts(b.PortId, out int pending, out int buffered))
                {
                    ports += Math.Max(0, pending) + Math.Max(0, buffered);
                }
            }
            int ground = s?.GroundItems?.Where(g => g != null && g.RegionId == HomeValleyLayout.RegionId && g.ResourceType == CampaignEconomyLedger.ResourceScrap).Sum(g => g.Amount) ?? 0;
            int cargo = MachineRegistry.AllRecords.Where(m => m != null && m.IsAlive && m.RegionId == HomeValleyLayout.RegionId).Sum(HomeValleyConstruction.CargoScrap);
            return s.Scrap + ports + BeltNetworkService.Kernel.CountItemsSlow() + ground + cargo;
        }

        /// <summary>泵每个世界步抽多少毫升（表里的升 / 分钟换算）。</summary>
        internal static double PumpMlPerStep => PipeNetworkService.Kernel.Config.PumpLitersPerMinute * 1000.0 / 60.0 / GameClock.StepHz;

        /// <summary>运行中的内核摘要（读档后与存档前对照）：传送带内核状态哈希、指定格的传送带等级、泵累计与储罐存量。</summary>
        internal static string KernelDigest(IEnumerable<GridCell> belts, IEnumerable<GridCell> pumps, IEnumerable<GridCell> tanks)
        {
            var sb = new StringBuilder();
            sb.Append("带哈希=").Append(BeltNetworkService.IsRunning ? BeltNetworkService.Kernel.ComputeStateHash().ToString("X16", CultureInfo.InvariantCulture) : "-");
            sb.Append("｜等级=").Append(string.Join(string.Empty, belts.Select(p => BeltInfo(p, out BeltCellInfo i) ? i.Tier.ToString(CultureInfo.InvariantCulture) : "x")));
            sb.Append("｜泵=").Append(string.Join(",", pumps.Select(p => PumpTotalMl(p).ToString(CultureInfo.InvariantCulture))));
            sb.Append("｜罐=").Append(string.Join(",", tanks.Select(p => TankMl(p).ToString(CultureInfo.InvariantCulture))));
            return sb.ToString();
        }

        // ── 出征名单（任意三台，按面板同一校验挑）─────────────────────────────────────────

        internal static StepOutcome TickRosterAny(JourneyContext c)
        {
            CampaignState s = St;
            if (!Done(c, "cursor"))
            {
                // 玩家去点名单时光标在面板上：先把脚本光标从上一步右键的地面挪开，世界悬停提示随之收起，别让它盖住名单行。
                JourneyInput.ReleaseKeys();
                Mark(c, "cursor");
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < 1.0)
            {
                return StepOutcome.Wait;
            }
            if (c.Get("roster", string.Empty).Length == 0)
            {
                ExpeditionDepartureService.PrepSnapshot snap = ExpeditionDepartureService.BuildPrepSnapshot(s);
                List<int> eligible = snap.Machines.Where(m => m.Eligible).OrderBy(m => m.BusyKind.HasValue).ThenBy(m => m.LogicId).Select(m => m.LogicId).ToList();
                List<int> chosen = null;
                foreach (IEnumerable<int> combo in FgjM1Journey.Combos(eligible, ExpeditionDepartureService.MinRosterSize))
                {
                    List<int> ids = combo.ToList();
                    if (ExpeditionDepartureService.ValidateRoster(s, ids).Success)
                    {
                        chosen = ids;
                        break;
                    }
                }
                if (chosen == null)
                {
                    return StepOutcome.Fail($"找不到能出发的三人名单（可出征 {eligible.Count} 台）");
                }
                c.Set("roster", string.Join(",", chosen.Select(x => x.ToString(CultureInfo.InvariantCulture))));
                c.SetInt("rosterIdx", 0);
            }
            List<int> roster = FgjM1Journey.Roster(c);
            int idx = c.GetInt("rosterIdx");
            if (idx < roster.Count)
            {
                if (c.StepElapsed < (idx + 1) * 0.6)
                {
                    return StepOutcome.Wait;
                }
                Toggle t = FgjM1Journey.RowToggle(roster[idx]);
                if (t == null)
                {
                    return StepOutcome.Fail($"名单表里找不到 {Label(roster[idx])} 这一行");
                }
                if (!JourneyInput.ScrollIntoView(JourneyInput.FindUitk<ScrollView>("[HomeValleyExpeditionPrepHost]", "MachineList"), t))
                {
                    c.SetInt("wheel", c.GetInt("wheel") + 1);
                    return c.GetInt("wheel") > 40 ? StepOutcome.Fail($"{Label(roster[idx])} 这一行滚不到可见区") : StepOutcome.Wait;
                }
                if (!t.value && !JourneyInput.ClickElement(t))
                {
                    return StepOutcome.Fail($"勾选 {Label(roster[idx])} 失败：{JourneyInput.LastUiFailure}");
                }
                c.SetInt("rosterIdx", idx + 1);
                return StepOutcome.Wait;
            }
            if (c.StepElapsed < (roster.Count + 1) * 0.6)
            {
                return StepOutcome.Wait;
            }
            string summary = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "SummaryLabel")?.text ?? string.Empty;
            string reasons = JourneyInput.FindUitk<Label>("[HomeValleyExpeditionPrepHost]", "ReasonsLabel")?.text ?? string.Empty;
            bool allOn = roster.All(id => FgjM1Journey.RowToggle(id)?.value == true);
            return allOn && reasons.Length == 0
                ? StepOutcome.Done($"勾选 {string.Join("、", roster.Select(Label))}：“{summary}”")
                : StepOutcome.Fail($"勾选后名单不对：全部勾上 {allOn}；“{summary}”“{reasons}”");
        }

        // ── 家园机器 ──────────────────────────────────────────────────────────────────

        /// <summary>记下开局家园里的两台工程机（只读名册，不改任何状态）。</summary>
        internal static StepOutcome TickWorkers(JourneyContext c)
        {
            if (c.StepElapsed < 0.5)
            {
                return StepOutcome.Wait;
            }
            List<MachineRecord> home = MachineRegistry.AllRecords
                .Where(m => m != null && m.IsAlive && !m.IsInFactory && m.RegionId == HomeValleyLayout.RegionId)
                .OrderBy(m => m.LogicId).ToList();
            List<MachineRecord> workers = home.Where(m => m.ChassisId == HomeValleyLayout.Erc001ChassisId).ToList();
            if (workers.Count < 2)
            {
                workers = home;
            }
            if (workers.Count < 2)
            {
                return StepOutcome.Fail($"家园里找不到两台可以派工的机器（{home.Count} 台）");
            }
            c.SetInt("workerA", workers[0].LogicId);
            c.SetInt("workerB", workers[1].LogicId);
            c.SetInt("scrap0", St.Scrap);
            return StepOutcome.Done($"开局家园机器 {home.Count} 台：{string.Join("，", home.Select(m => $"#{m.DisplayNumber}({m.ChassisId})"))}；废料 {St.Scrap}（不加进度夹具）");
        }

        // ── 端口面板（建造模式里点一下建筑）──────────────────────────────────────────────

        internal const string PortHost = "[BeltPortHost]";
        /// <summary>FG4-ECO-05：建筑通用面板的宿主。</summary>
        internal const string BuildingPanelHost = "[ProductionPanelHost]";

        /// <summary>建造模式里左键点一下开局建筑（不拖，拖是搬迁）：打开它的端口面板。</summary>
        internal static StepOutcome TickPortOpen(JourneyContext c, string typeId)
        {
            Transform t = FgjM1Journey.FindNamed("Building_" + typeId);
            BuildingRecord b = Home(typeId);
            if (t == null || b == null)
            {
                return StepOutcome.Fail("场景里找不到 " + typeId);
            }
            if (!Once(c, "click", () => TryClick(new Vector2(t.position.x, t.position.z))))
            {
                return StepOutcome.Wait;
            }
            if (SinceMs(c, "click") < 800)
            {
                return StepOutcome.Wait;
            }
            // FG4-ECO-05（FGU-09）：点建筑先打开它的通用面板，再点面板里的“端口…”进端口面板（玩家的真实路径）。
            // FG4-E2E-01（M4 出口回归）：FG4-ECO-07 / 08 / 11 往通用面板加了名字、效率、槽位等几行后，“端口…”按钮落到面板滚动区的可见区下面——
            // 像玩家一样先在面板主体上滚滚轮把它滚进可见区再点（不滚就点不到，原来这里会一直等到超时）。
            if (!BeltPortPanelUIToolkit.IsOpen && ProductionPanelUIToolkit.IsOpen && ProductionPanelUIToolkit.BuildingId == b.BuildingId
                && !Once(c, "ports", () => ClickPanelBodyButton(BuildingPanelHost, "ProductionPanelBody", "PrPorts")))
            {
                return StepOutcome.Wait;
            }
            if (Done(c, "ports") && SinceMs(c, "ports") < 500)
            {
                return StepOutcome.Wait;
            }
            BeltPortPanelUIToolkit panel = BeltPortPanelUIToolkit.Instance;
            return BeltPortPanelUIToolkit.IsOpen && panel != null && panel.PanelVisible && BeltPortPanelUIToolkit.BuildingId == b.BuildingId && panel.VisibleRowCount >= 1
                ? StepOutcome.Done($"左键点{HomeGridService.DisplayName(typeId)}：端口面板打开（“{panel.TitleText}”，{panel.VisibleRowCount} 个端口；“{panel.StoreText}”）")
                : StepOutcome.Retry($"点{HomeGridService.DisplayName(typeId)}后端口面板没打开（状态行“{Mode?.StatusText}”）");
        }

        /// <summary>本会话里为了点到面板滚动区里的按钮而先滚滚轮的次数（报告里计数）。</summary>
        internal static int PanelBodyScrolls { get; set; }

        /// <summary>
        /// 点面板滚动区里的一个按钮：按钮不在滚动区可见区里时像玩家一样在滚动区上滚一下滚轮（这次不点，返回 false，下一次再试）；
        /// 在可见区里就向面板派发真实指针事件（<see cref="JourneyInput.ClickElement"/>）。
        /// </summary>
        internal static bool ClickPanelBodyButton(string host, string scrollName, string buttonName)
        {
            VisualElement btn = JourneyInput.FindUitk<VisualElement>(host, buttonName);
            ScrollView body = JourneyInput.FindUitk<ScrollView>(host, scrollName);
            if (btn != null && body != null && body.Contains(btn) && !JourneyInput.ScrollIntoView(body, btn))
            {
                PanelBodyScrolls++;
                return false;
            }
            return JourneyInput.ClickElement(btn);
        }

        /// <summary>端口面板里某个输出口那一行的过滤下拉框选 <paramref name="filter"/>（下拉框确认能点、没被挡住后设值——与在弹出菜单里点那一项同一个值变化回调）。</summary>
        internal static bool PickPortFilter(JourneyContext c, string portKey, int filter, out string why)
        {
            why = null;
            BeltPortPanelUIToolkit panel = BeltPortPanelUIToolkit.Instance;
            if (panel == null || !BeltPortPanelUIToolkit.IsOpen)
            {
                why = "端口面板没开";
                return false;
            }
            for (int i = 0; i < panel.VisibleRowCount; i++)
            {
                if (panel.RowPortKey(i) != portKey)
                {
                    continue;
                }
                DropdownField d = panel.RowFilter(i);
                if (d == null || !panel.RowFilterVisible(i) || !JourneyInput.IsClickable(d))
                {
                    why = "这一行的过滤下拉框不能点";
                    return false;
                }
                string want = BeltPortService.FilterName(filter);
                if (!d.choices.Contains(want))
                {
                    why = $"下拉里没有“{want}”（{string.Join("、", d.choices)}）";
                    return false;
                }
                c.Set(SK(c, "portRowText"), panel.RowText(i, "BpState") + "｜" + panel.RowText(i, "BpStats"));
                d.value = want;
                return true;
            }
            why = "端口面板里没有 " + portKey;
            return false;
        }

        // ── 拆第二处开局残骸（右键残骸 = 拆解）──────────────────────────────────────────

        internal static WorkOrderRecord SalvageOrder(string nodeId) =>
            St?.WorkOrders?.LastOrDefault(x => x != null && x.Kind == WorkOrderKind.Salvage && x.TargetId == nodeId);

        internal static void RightClickWreck2(JourneyContext c)
        {
            c.SetInt("scrapBeforeWreck2", St.Scrap);
            FgjM2Common.ClickNamed(c, "Wreckage_" + HomeValleyLayout.Wreckage2NodeId, 1);
        }

        internal static StepOutcome TickWreck2Ordered(JourneyContext c)
        {
            if (c.StepElapsed < 0.8 || FgjM2Common.PanPending(c, RightClickWreck2) || FgjM2Common.JustClicked(c))
            {
                return StepOutcome.Wait;
            }
            WorkOrderRecord o = SalvageOrder(HomeValleyLayout.Wreckage2NodeId);
            return o != null
                ? StepOutcome.Done($"右键点第二处残骸（情境命令：拆解），工单 {o.State}")
                : StepOutcome.Retry($"右键后没有拆解工单（{FgjM2Common.PanNote(c)}；{GameRoot.HomeValley.SquadCommands.RecentEvents.LastOrDefault()}）");
        }

        /// <summary>拆解工单完成（家园库存此刻也随物品线的循环上下浮动几件，所以按工单完成判定，并写出库存变化）。</summary>
        internal static StepOutcome TickWreck2Done(JourneyContext c)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            WorkOrderRecord o = SalvageOrder(HomeValleyLayout.Wreckage2NodeId);
            if (o == null || o.State == WorkOrderState.Failed || o.State == WorkOrderState.Cancelled)
            {
                return StepOutcome.Fail($"第二处残骸的拆解工单 {o?.State}");
            }
            if (o.State != WorkOrderState.Completed)
            {
                return StepOutcome.Wait;
            }
            return St.Scrap - c.GetInt("scrapBeforeWreck2") >= HomeValleyLayout.WreckageScrapYield - 10
                ? StepOutcome.Done($"第二处残骸拆完：家园废料 {c.GetInt("scrapBeforeWreck2")} → {St.Scrap}")
                : StepOutcome.Fail($"拆完后家园废料只从 {c.GetInt("scrapBeforeWreck2")} 变成 {St.Scrap}（应多约 {HomeValleyLayout.WreckageScrapYield}）");
        }

        internal static StepOutcome TickRepaired(JourneyContext c, params string[] types)
        {
            JourneyCommon.ResumeIfAutoPaused(c);
            var missing = new List<string>();
            foreach (string t in types)
            {
                BuildingRecord b = Home(t);
                if (b == null || b.ConstructionState != BuildingConstructionState.Operational)
                {
                    missing.Add(t);
                    if (b != null && FgjM1Journey.RepairOrder(t) == null && c.StepElapsed > 5)
                    {
                        return StepOutcome.Fail($"{t} 还没修好，修复工单却没了（废料 {St.Scrap}）");
                    }
                }
            }
            return missing.Count == 0
                ? StepOutcome.Done($"{string.Join("、", types.Select(HomeGridService.DisplayName))} 修好（废料 {St.Scrap}）")
                : StepOutcome.Wait;
        }
    }
}
