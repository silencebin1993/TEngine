using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Campaign.Defense;
using GameLogic.Campaign.Economy;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Endgame
{
    /// <summary>一个巨构阶段（fg.TbMegaStage 一行的运行时视图）。</summary>
    public sealed class MegaStageDef
    {
        public string Id;
        public int Order;
        public string NameKey;
        public string DescKey;
        public ItemDef Solid;
        public int SolidAmount;
        public ItemDef Fluid;
        public long FluidMl;
        public float PowerHours;
        public int RaidLevel;
        public string Name => GameText.Get(NameKey);
    }

    /// <summary>
    /// FG13-END-01 返航工程（FG11 FGR-END-001 / 002；验收 FGT-END-002）：五个巨构阶段的进度、完工结算与工地面板文字。
    /// <b>唯一写入口</b> <see cref="CampaignProgressState.Mega"/>。
    ///
    /// - 推进：返航工地是生产建筑（模式 megastructure），<see cref="ProductionService"/> 每个生产步把工地缓存里的固体、流体口里的流体按本阶段还差的量取走，
    ///   连同这一步有没有电交给 <see cref="Accrue"/>；三项各自累计（世界步数，与观察 / 倍速无关；暂停不走）。O(1)。
    /// - 完工（三项都够）：记完工、进入下一阶段（进度清零）；突袭导演排一次更高等级的突袭（<see cref="RaidDirectorService.OnMegastructureStageCompleted"/>）；
    ///   舰队发来阶段消息（资料库触发 mega:&lt;阶段ID&gt;）；通知；<see cref="StageCompletedEvent"/>（最后一个阶段另发 <see cref="AllCompletedEvent"/>，信标点火 → 揭示 FG13-END-02）。
    /// - 受损暂停（FGR-END-002）：工地耐久低于上限或被摧毁时三项都不推进（<see cref="OnPaused"/>），已完成的不倒退；进度在战役状态里，修好 / 重建后接着做。
    /// - 预计完成时间：每 mega.rate_window_seconds 游戏秒按窗口里实际送入的量重算供给速度；某项一整个窗口没有送入 = 供给中断（通知一次，预计时间写“要等供给恢复”）。
    /// </summary>
    public static class MegastructureService
    {
        public const string TypeId = "megastructure_site";
        public const string BlueprintId = "megastructure_blueprint";
        /// <summary>资料库触发前缀：巨构阶段完工（fg.TbNarrativeEntry trigger = mega:&lt;阶段ID&gt;）。</summary>
        public const string NarrativeTriggerPrefix = "mega:";
        /// <summary>资料库触发前缀：拿到蓝图（blueprint:&lt;蓝图ID&gt;；巨构蓝图 = 舰队发来蓝图，第三幕开始）。</summary>
        public const string BlueprintTriggerPrefix = "blueprint:";
        /// <summary>GameEvent：一个巨构阶段完工（参数 = 阶段 ID）。</summary>
        public const string StageCompletedEvent = "Megastructure.StageCompleted";
        /// <summary>GameEvent：五个阶段全部完工（信标点火；揭示在 FG13-END-02）。</summary>
        public const string AllCompletedEvent = "Megastructure.AllCompleted";

        private static readonly List<MegaStageDef> StageList = new List<MegaStageDef>(5);
        private static readonly List<string> ProblemList = new List<string>();
        private static bool _loaded;

        /// <summary>自检读：本进程完工过几个阶段、发过几次暂停 / 中断通知。</summary>
        public static int StagesCompletedCount { get; private set; }
        public static int PauseNotifyCount { get; private set; }
        public static int StarveNotifyCount { get; private set; }

        public static IReadOnlyList<string> Problems
        {
            get
            {
                EnsureLoaded();
                return ProblemList;
            }
        }

        public static IReadOnlyList<MegaStageDef> Stages
        {
            get
            {
                EnsureLoaded();
                return StageList;
            }
        }

        public static int StageCount => Stages.Count;

        public static void Reload()
        {
            _loaded = false;
            EnsureLoaded();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            StageList.Clear();
            ProblemList.Clear();
            TbMegaStage t = null;
            try
            {
                t = ConfigSystem.Instance.Tables?.TbMegaStage;
            }
            catch (Exception e)
            {
                ProblemList.Add("读取 fg.TbMegaStage 失败：" + e.Message); // loc-ignore: 开发者自检 / 诊断文本
            }
            if (t == null)
            {
                ProblemList.Add("fg.TbMegaStage 不存在（改 tools/cell_tables/fgdata_megastructure.py 后重新生成）"); // loc-ignore: 开发者自检 / 诊断文本
                return;
            }
            var rows = new List<MegaStage>(t.DataList);
            rows.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (MegaStage r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.Id) || !ItemCatalog.TryGet(r.SolidItem, out ItemDef solid) || solid.Form == ItemForm.Fluid
                    || !ItemCatalog.TryGet(r.FluidItem, out ItemDef fluid) || fluid.Form != ItemForm.Fluid
                    || r.SolidAmount < 1 || r.FluidLiters < 1 || r.PowerHours <= 0f)
                {
                    ProblemList.Add($"fg.TbMegaStage {r?.Id}：物品 / 数量 / 供电时长不合法，已跳过"); // loc-ignore: 开发者自检 / 诊断文本
                    continue;
                }
                if (StageList.Count > 0 && !ReferenceEquals(StageList[0].Solid, solid))
                {
                    ProblemList.Add($"fg.TbMegaStage {r.Id}：五个阶段的固体必须是同一种（物品口登记时就定下收什么），已跳过"); // loc-ignore: 开发者自检 / 诊断文本
                    continue;
                }
                StageList.Add(new MegaStageDef
                {
                    Id = r.Id, Order = r.Order, NameKey = r.NameKey, DescKey = r.DescKey, Solid = solid, SolidAmount = r.SolidAmount,
                    Fluid = fluid, FluidMl = r.FluidLiters * 1000L, PowerHours = r.PowerHours, RaidLevel = Math.Max(1, Math.Min(4, r.RaidLevel)),
                });
            }
            if (StageList.Count == 0)
            {
                ProblemList.Add("fg.TbMegaStage 没有可用的阶段"); // loc-ignore: 开发者自检 / 诊断文本
            }
        }

        // ── 读 ───────────────────────────────────────────────────────────

        public static bool IsSite(string typeId) => typeId == TypeId;

        public static MegastructureState StateOf(CampaignState s)
        {
            if (s?.Progress == null)
            {
                return null;
            }
            s.Progress.Mega ??= new MegastructureState();
            s.Progress.Mega.Done ??= Array.Empty<MegaStageDoneRecord>();
            return s.Progress.Mega;
        }

        /// <summary>当前阶段（全部完工 / 没有表 = null）。</summary>
        public static MegaStageDef Current(CampaignState s)
        {
            MegastructureState m = StateOf(s);
            return m != null && m.StageIndex >= 0 && m.StageIndex < StageCount ? Stages[m.StageIndex] : null;
        }

        public static bool AllDone(CampaignState s)
        {
            MegastructureState m = StateOf(s);
            return m != null && StageCount > 0 && m.StageIndex >= StageCount;
        }

        /// <summary>工地要收的固体（物品口登记用；五个阶段同一种）。没有表 = null。</summary>
        public static ItemDef SolidItem => StageCount > 0 ? Stages[0].Solid : null;

        public static long PowerTicksFor(MegaStageDef st) => st == null ? 0 : Math.Max(1L, GameClock.TicksFor(st.PowerHours * GameClock.HourSeconds));

        /// <summary>拿到巨构蓝图了吗（事件账本 quest_blueprint:megastructure_blueprint；FG8-QST-01 的消费方，DEBT-FG8QST01-03）。</summary>
        public static bool HasBlueprint(CampaignState s) => CampaignObjectiveTracker.HasKeyMaterial(s, QuestCatalog.KeyMaterialOf(BlueprintId));

        /// <summary>工地是否受损（耐久低于上限）——受损时暂停推进（FGR-END-002；口径：整座工地，一座建筑一个耐久）。</summary>
        public static bool IsWounded(CampaignState s, BuildingRecord b, out float hp, out float max)
        {
            max = BuildingOps.MaxDurability(b?.BuildingTypeId);
            hp = b == null ? 0f : RepairDroneService.DurabilityOf(s, b);
            return b != null && hp < max - 0.01f;
        }

        // ── 推进（ProductionService 每个生产步调用一次）──────────────────────────────

        /// <summary>
        /// 记一步：本步计入的固体 <paramref name="solid"/> 件、流体 <paramref name="fluidMl"/> 毫升、有电的步数 <paramref name="poweredTicks"/>（调用方已按“还差多少”取，不会多送）；
        /// <paramref name="ticks"/> = 这一步经过的世界步数（统计窗口用）。三项都够 → 完工。返回这一步是否完工了一个阶段。
        /// </summary>
        public static bool Accrue(CampaignState s, BuildingRecord site, long solid, long fluidMl, long poweredTicks, int ticks)
        {
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null || st == null)
            {
                return false;
            }
            long powerNeed = PowerTicksFor(st);
            m.SolidDelivered = Math.Min(st.SolidAmount, m.SolidDelivered + Math.Max(0, solid));
            m.FluidDeliveredMl = Math.Min(st.FluidMl, m.FluidDeliveredMl + Math.Max(0, fluidMl));
            m.PoweredTicks = Math.Min(powerNeed, m.PoweredTicks + Math.Max(0, poweredTicks));
            m.PausedNotified = false;
            Window(s, m, st, site, solid, fluidMl, poweredTicks, ticks);
            if (m.SolidDelivered < st.SolidAmount || m.FluidDeliveredMl < st.FluidMl || m.PoweredTicks < powerNeed)
            {
                return false;
            }
            Complete(s, m, st, site);
            return true;
        }

        private static long WindowTicks => Math.Max(1L, GameClock.TicksFor(Math.Max(10.0, GridContent.Tuning("mega.rate_window_seconds"))));

        private static void Window(CampaignState s, MegastructureState m, MegaStageDef st, BuildingRecord site, long solid, long fluidMl, long powered, int ticks)
        {
            long now = GameClock.Ticks;
            if (m.WindowStartTick < 0)
            {
                m.WindowStartTick = now - Math.Max(0, ticks);
            }
            m.WindowSolid += Math.Max(0, solid);
            m.WindowFluidMl += Math.Max(0, fluidMl);
            m.WindowPoweredTicks += Math.Max(0, powered);
            long elapsed = now - m.WindowStartTick;
            if (elapsed < WindowTicks)
            {
                return;
            }
            double minutes = elapsed / (double)Math.Max(1, GameClock.StepHz) / 60.0;
            long prevSolid = m.SolidMilliPerMinute, prevFluid = m.FluidMlPerMinute;
            // 复审 P2：某项已经送够后传送带 / 管线停送是“够了”不是“断了”：保留上一次测到的速度（进入下一阶段时面板仍按它估时，不误报“供给中断”）；还没测到过才用这一窗口的数。
            if (m.SolidDelivered < st.SolidAmount || prevSolid <= 0)
            {
                m.SolidMilliPerMinute = (long)Math.Round(m.WindowSolid * 1000.0 / minutes);
            }
            if (m.FluidDeliveredMl < st.FluidMl || prevFluid <= 0)
            {
                m.FluidMlPerMinute = (long)Math.Round(m.WindowFluidMl / minutes);
            }
            m.PoweredPermille = Math.Min(1000L, (long)Math.Round(m.WindowPoweredTicks * 1000.0 / elapsed));
            m.RateWindows++;
            // 卡片负向“物资供给中断”：还没送够的物资，上一个窗口在送、这一个窗口一件没送 → 通知一次（恢复送入后清掉，下次中断再通知）。
            bool solidCut = m.SolidDelivered < st.SolidAmount && m.WindowSolid == 0;
            bool fluidCut = m.FluidDeliveredMl < st.FluidMl && m.WindowFluidMl == 0;
            if (m.WindowSolid > 0)
            {
                m.SolidStarvedNotified = false;
            }
            if (m.WindowFluidMl > 0)
            {
                m.FluidStarvedNotified = false;
            }
            if (solidCut && prevSolid > 0 && !m.SolidStarvedNotified)
            {
                m.SolidStarvedNotified = true;
                NotifyStarved(site, st.Solid);
            }
            if (fluidCut && prevFluid > 0 && !m.FluidStarvedNotified)
            {
                m.FluidStarvedNotified = true;
                NotifyStarved(site, st.Fluid);
            }
            m.WindowStartTick = now;
            m.WindowSolid = 0;
            m.WindowFluidMl = 0;
            m.WindowPoweredTicks = 0;
        }

        private static void NotifyStarved(BuildingRecord site, ItemDef item)
        {
            StarveNotifyCount++;
            NotificationCenter.Post("mega_alert", GameText.Format("mega.notify.starved", Label(site), item?.Name ?? string.Empty), At(site));
        }

        /// <summary>工地受损 / 被摧毁的这一步（不推进）：这一段通知一次；统计窗口重开（暂停的时间不算进供给速度）。</summary>
        public static void OnPaused(CampaignState s, BuildingRecord site)
        {
            MegastructureState m = StateOf(s);
            if (m == null || AllDone(s))
            {
                return;
            }
            m.WindowStartTick = -1;
            m.WindowSolid = 0;
            m.WindowFluidMl = 0;
            m.WindowPoweredTicks = 0;
            if (!m.PausedNotified)
            {
                m.PausedNotified = true;
                PauseNotifyCount++;
                NotificationCenter.Post("mega_alert", GameText.Format("mega.notify.paused", Label(site)), At(site));
            }
        }

        private static void Complete(CampaignState s, MegastructureState m, MegaStageDef st, BuildingRecord site)
        {
            var done = new MegaStageDoneRecord[m.Done.Length + 1];
            Array.Copy(m.Done, done, m.Done.Length);
            done[m.Done.Length] = new MegaStageDoneRecord { Id = st.Id, Tick = GameClock.Ticks, Day = s.Clock != null && s.Clock.Day > 0 ? s.Clock.Day : 1 };
            m.Done = done;
            m.StageIndex++;
            m.SolidDelivered = 0;
            m.FluidDeliveredMl = 0;
            m.PoweredTicks = 0;
            m.SolidStarvedNotified = false;
            m.FluidStarvedNotified = false;
            StagesCompletedCount++;
            Log.Info($"[Megastructure] 巨构阶段 {st.Id} 完工（第 {st.Order}/{StageCount} 阶段）");
            // FGR-END-002：每完成一个阶段，触发一次更高等级的突袭（FGR-DEF-020“巨构阶段完工”）。
            RaidDirectorService.OnMegastructureStageCompleted(s, st.RaidLevel);
            // FG11 第 3.1 节“阶段消息”：舰队的消息（语气按阶段顺序变得程式化，内容在 fg.TbNarrativeEntry）。
            Narrative.NarrativeService.Trigger(s, NarrativeTriggerPrefix + st.Id);
            NotificationCenter.Post("mega_stage_done", GameText.Format("mega.notify.stage_done", st.Name, st.Order, StageCount), At(site));
            GuidanceHooks.Raise(GuidanceHooks.MegaFirstStageDone);
            GameEvent.Send(StageCompletedEvent, st.Id);
            if (AllDone(s))
            {
                GameEvent.Send(AllCompletedEvent);
            }
        }

        private static string Label(BuildingRecord b) => b != null ? Feedback.FeedbackCues.BuildingLabel(b.BuildingId) : GameText.Get("building.megastructure_site.name");

        private static Vector3? At(BuildingRecord b) => b != null ? new Vector3(b.Position.x, 0f, b.Position.y) : (Vector3?)null;

        // ── 预计完成时间与面板 ───────────────────────────────────────────

        /// <summary>某项还要多少游戏秒（已够 = 0；还在统计 / 供给中断 = -1）。</summary>
        public static double SolidSecondsLeft(CampaignState s)
        {
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null || st == null || m.SolidDelivered >= st.SolidAmount)
            {
                return 0;
            }
            return m.RateWindows == 0 || m.SolidMilliPerMinute <= 0 ? -1 : (st.SolidAmount - m.SolidDelivered) * 60000.0 / m.SolidMilliPerMinute;
        }

        public static double FluidSecondsLeft(CampaignState s)
        {
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null || st == null || m.FluidDeliveredMl >= st.FluidMl)
            {
                return 0;
            }
            return m.RateWindows == 0 || m.FluidMlPerMinute <= 0 ? -1 : (st.FluidMl - m.FluidDeliveredMl) * 60.0 / m.FluidMlPerMinute;
        }

        /// <summary>持续供电还要多少游戏秒：现在有电 = 剩余时长（供电计时与实际时间一比一）；现在没电 = -1。</summary>
        public static double PowerSecondsLeft(CampaignState s, bool poweredNow)
        {
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null || st == null)
            {
                return 0;
            }
            long left = PowerTicksFor(st) - m.PoweredTicks;
            if (left <= 0)
            {
                return 0;
            }
            return poweredNow ? left / (double)Math.Max(1, GameClock.StepHz) : -1;
        }

        /// <summary>本阶段预计还要多少游戏秒（三项里最慢的那项；任何一项无法估计 = -1）。</summary>
        public static double StageSecondsLeft(CampaignState s, bool poweredNow)
        {
            double a = SolidSecondsLeft(s), b = FluidSecondsLeft(s), c = PowerSecondsLeft(s, poweredNow);
            return a < 0 || b < 0 || c < 0 ? -1 : Math.Max(a, Math.Max(b, c));
        }

        /// <summary>本阶段整体进度（三项各自完成比例的平均，0～1）。</summary>
        public static float StageFraction(CampaignState s)
        {
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null || st == null)
            {
                return AllDone(s) ? 1f : 0f;
            }
            double f = m.SolidDelivered / (double)st.SolidAmount + m.FluidDeliveredMl / (double)st.FluidMl + m.PoweredTicks / (double)PowerTicksFor(st);
            return Mathf.Clamp01((float)(f / 3.0));
        }

        private static string Eta(double seconds, string rateText, bool met)
        {
            if (met)
            {
                return GameText.Get("mega.panel.met");
            }
            if (seconds < 0)
            {
                return rateText ?? GameText.Get("mega.panel.stalled");
            }
            return rateText;
        }

        /// <summary>工地面板正文（生产面板的工地段、悬停与自检共用）：阶段、三项进度与各自的预计时间、本阶段预计完成时间、暂停原因、已完工列表。</summary>
        public static string PanelText(CampaignState s, BuildingRecord site, bool poweredNow)
        {
            var sb = new StringBuilder(256);
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null)
            {
                return string.Empty;
            }
            if (st == null)
            {
                sb.Append(GameText.Get("mega.panel.all_done"));
            }
            else
            {
                CultureInfo ci = CultureInfo.InvariantCulture;
                sb.Append(GameText.Format("mega.panel.header", st.Order, StageCount, st.Name)).Append('\n').Append(GameText.Get(st.DescKey));
                bool measuring = m.RateWindows == 0;
                double perGameHour = GameClock.HourSeconds / 60.0; // 复审 P2：速率按游戏小时显示，与预计时间（CalendarService.ShortDuration，游戏钟）同一套时钟
                double sl = SolidSecondsLeft(s);
                string sRate = measuring ? GameText.Get("mega.panel.measuring")
                    : sl < 0 ? GameText.Get("mega.panel.stalled")
                    : GameText.Format("mega.panel.rate", (m.SolidMilliPerMinute / 1000.0 * perGameHour).ToString("0.#", ci), Env.CalendarService.ShortDuration(sl));
                sb.Append('\n').Append(GameText.Format("mega.panel.solid", st.Solid.Name, m.SolidDelivered.ToString(ci), st.SolidAmount.ToString(ci),
                    Eta(sl, sRate, m.SolidDelivered >= st.SolidAmount)));
                double fl = FluidSecondsLeft(s);
                string fRate = measuring ? GameText.Get("mega.panel.measuring")
                    : fl < 0 ? GameText.Get("mega.panel.stalled")
                    : GameText.Format("mega.panel.rate", (m.FluidMlPerMinute / 1000.0 * perGameHour).ToString("0.#", ci), Env.CalendarService.ShortDuration(fl));
                sb.Append('\n').Append(GameText.Format("mega.panel.fluid", st.Fluid.Name, (m.FluidDeliveredMl / 1000).ToString(ci), (st.FluidMl / 1000).ToString(ci),
                    Eta(fl, fRate, m.FluidDeliveredMl >= st.FluidMl)));
                double pl = PowerSecondsLeft(s, poweredNow);
                long need = PowerTicksFor(st);
                double hz = Math.Max(1, GameClock.StepHz);
                string pRate = pl < 0 ? GameText.Get("mega.panel.no_power") : GameText.Format("mega.panel.rate_power", Env.CalendarService.ShortDuration(pl));
                sb.Append('\n').Append(GameText.Format("mega.panel.power", HomeValleyPowerDemand(site),
                    Env.CalendarService.ShortDuration(m.PoweredTicks / hz), Env.CalendarService.ShortDuration(need / hz), Eta(pl, pRate, m.PoweredTicks >= need)));
                if (site != null && site.ConstructionState == BuildingConstructionState.Damaged)
                {
                    sb.Append('\n').Append(GameText.Get("mega.panel.destroyed"));
                }
                else if (site != null && IsWounded(s, site, out float hp, out float max))
                {
                    sb.Append('\n').Append(GameText.Format("mega.panel.paused", Mathf.RoundToInt(hp), Mathf.RoundToInt(max)));
                }
                else
                {
                    double all = StageSecondsLeft(s, poweredNow);
                    sb.Append('\n').Append(all < 0 ? GameText.Get("mega.panel.eta_unknown") : GameText.Format("mega.panel.eta", Env.CalendarService.ShortDuration(all)));
                }
                sb.Append('\n').Append(GameText.Format("mega.panel.next_raid", st.RaidLevel));
            }
            if (m.Done.Length > 0)
            {
                var names = new List<string>(m.Done.Length);
                foreach (MegaStageDoneRecord d in m.Done)
                {
                    MegaStageDef def = Find(d.Id);
                    names.Add(def != null ? def.Name : d.Id);
                }
                sb.Append('\n').Append(GameText.Format("mega.panel.done_list", string.Join(GameText.Get("pace.gate.join"), names)));
            }
            return sb.ToString();
        }

        private static string HomeValleyPowerDemand(BuildingRecord site)
        {
            float d = site != null && Regions.HomeValleyLayout.PowerProfile.TryGetValue(site.BuildingTypeId, out (float PowerDemand, int PowerPriority) prof) ? prof.PowerDemand : 0f;
            return Regions.HomeValleyPowerGrid.Num(d);
        }

        public static MegaStageDef Find(string id)
        {
            foreach (MegaStageDef d in Stages)
            {
                if (d.Id == id)
                {
                    return d;
                }
            }
            return null;
        }

        /// <summary>状态原因文字（生产状态行 / 悬停）。</summary>
        public static string ReasonText(CampaignState s, ProductionService.Producer p)
        {
            MegaStageDef st = Current(s);
            switch (p.Reason)
            {
                case ProdReason.MegaWounded:
                    return GameText.Format("mega.reason.wounded", p.ReasonHave, p.ReasonNeed);
                case ProdReason.MegaDone:
                    return GameText.Get("mega.reason.done");
                case ProdReason.MegaStarved:
                    return GameText.Format("mega.reason.starved", st?.Order ?? 0, MissingList(s, p));
                default:
                    return GameText.Format("mega.reason.working", st?.Order ?? 0, st?.Name ?? string.Empty);
            }
        }

        private static string MissingList(CampaignState s, ProductionService.Producer p)
        {
            MegastructureState m = StateOf(s);
            MegaStageDef st = Current(s);
            if (m == null || st == null)
            {
                return string.Empty;
            }
            var parts = new List<string>(3);
            if (m.SolidDelivered < st.SolidAmount)
            {
                parts.Add(st.Solid.Name);
            }
            if (m.FluidDeliveredMl < st.FluidMl)
            {
                parts.Add(st.Fluid.Name);
            }
            if (m.PoweredTicks < PowerTicksFor(st) && p.Building.PowerState != BuildingPowerState.Powered)
            {
                parts.Add(GameText.Get("mega.panel.no_power"));
            }
            return string.Join(GameText.Get("pace.gate.join"), parts);
        }
    }
}
