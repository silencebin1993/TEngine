using System;
using System.Collections.Generic;
using BinGames.Sim.Logistics;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Logistics;
using GameLogic.Campaign.Regions;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Notifications;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Campaign.Economy
{
    /// <summary>
    /// FG4-ECO-09（FG04 FGR-ECO-060 离家报告；FG04 第 6 节“离家报告（最近 3 份）”；FG13 FGU-30；FGT-ECO-007）：离家报告的记账与存档。<b>唯一写入口</b>（<see cref="StatsState.AwayReports"/>）。
    ///
    /// - <b>开 / 关</b>：远征出发事务成功（<see cref="ExpeditionDepartureService.TryDepart"/>）时 <see cref="Begin"/>；撤离 / 全灭放弃结算（<see cref="ExpeditionReturnService"/>）时 <see cref="End"/>。
    ///   一次只有一支远征队（出发前已拦“远征进行中”），所以同一时刻至多一份进行中的报告。结算后进“最近几份”（away.reports_keep = 3），发一条“离家报告”通知，并发事件
    ///   <see cref="ReportReadyEvent"/>（界面按设置决定是否自动打开）。
    /// - <b>记账</b>：全部在事情发生的那一刻记（不每帧遍历、不靠镜头）——产出 / 消耗跟统计面板同一记账点（<see cref="ProductionStats"/>）；缺料随生产步记在建筑自己身上，结算时汇总；
    ///   停电在电网结算写回时按“缺电停机的建筑数”开 / 关一段；发电 / 需要 / 储能每游戏秒累加；机器受伤 / 阵亡在机器登记表唯一写入口记；突袭、天气、事件、研究、建筑受损、电力事件按通知类型的
    ///   awaySection 列逐条记；常驻规则的动作在规则日志写入时记。所以后台运行与观察时结果完全一致（FGR-BASE-021），倍速只是一帧多走几步，暂停不走步就不记。
    /// - <b>开销</b>：每个记账点 O(1)（或 O(该报告已有的物品种类 / 条目)，有上限）；结算 O(生产建筑 + 机器)，只发生一次；界面只在打开时组装文字（<see cref="AwayReportView"/>）。
    /// </summary>
    public static class AwayReportService
    {
        /// <summary>报告结算后发出（参数 = 报告序号）。界面订阅它决定是否自动打开（跨模块走 GameEvent）。</summary>
        public const string ReportReadyEvent = "AwayReport.Ready";

        public const int OutcomeOpen = 0;
        public const int OutcomeEvacuated = 1;
        public const int OutcomeWiped = 2;
        public const int OutcomeOther = 3;

        // ── 调参 ─────────────────────────────────────────────────────────────

        public static int ReportsKeep => Math.Max(1, GridContent.TuningInt("away.reports_keep"));
        public static int BottleneckTop => Math.Max(1, GridContent.TuningInt("away.bottleneck_top"));
        public static int ItemsMax => Math.Max(1, GridContent.TuningInt("away.items_max"));
        public static int EntriesMax => Math.Max(1, GridContent.TuningInt("away.entries_max"));
        public static int RulesMax => Math.Max(1, GridContent.TuningInt("away.rules_max"));
        public static int OutagesMax => Math.Max(1, GridContent.TuningInt("away.outages_max"));
        public static int MachinesMax => Math.Max(1, GridContent.TuningInt("away.machines_max"));

        // ── 自检读点 ─────────────────────────────────────────────────────────

        /// <summary>任何报告数据变了 +1（界面据此决定是否重建；记账热路径里只在条目增删时自增）。</summary>
        public static int Revision { get; private set; } = 1;
        /// <summary>记账钩子被调用的次数（进行中的报告才计；性能自检读）。</summary>
        public static long HookCalls { get; private set; }
        /// <summary>结算时逐建筑 / 逐机器扫描的累计次数（自检据此证明“只在结算时扫描”）。</summary>
        public static long CloseScans { get; private set; }
        /// <summary>记账累计耗时（毫秒）。</summary>
        public static double HookMs { get; private set; }

        public static void ResetSessionState()
        {
            HookCalls = 0;
            CloseScans = 0;
            HookMs = 0;
            Revision++;
        }

        // ── 读 ───────────────────────────────────────────────────────────────

        public static AwayReportState Domain(CampaignState state)
        {
            if (state == null)
            {
                return null;
            }
            if (state.Stats?.AwayReports == null)
            {
                CampaignFgStateDomains.EnsureAll(state);
            }
            return state.Stats.AwayReports;
        }

        public static bool IsOpen(CampaignState state) => state?.Stats?.AwayReports != null && state.Stats.AwayReports.HasOpen;

        /// <summary>正在记的报告（没有远征在外 = null）。</summary>
        public static AwayReportRecord Current(CampaignState state) => IsOpen(state) ? state.Stats.AwayReports.Open : null;

        /// <summary>已结算的报告，最新的在前（界面的“最近 3 份”）。</summary>
        public static List<AwayReportRecord> Recent(CampaignState state)
        {
            var list = new List<AwayReportRecord>();
            AwayReportRecord[] all = state?.Stats?.AwayReports?.Reports;
            if (all == null)
            {
                return list;
            }
            for (int i = all.Length - 1; i >= 0; i--)
            {
                if (all[i] != null)
                {
                    list.Add(all[i]);
                }
            }
            return list;
        }

        public static AwayReportRecord Find(CampaignState state, int serial)
        {
            AwayReportState d = state?.Stats?.AwayReports;
            if (d == null)
            {
                return null;
            }
            if (d.HasOpen && d.Open != null && d.Open.Serial == serial)
            {
                return d.Open;
            }
            foreach (AwayReportRecord r in d.Reports ?? Array.Empty<AwayReportRecord>())
            {
                if (r != null && r.Serial == serial)
                {
                    return r;
                }
            }
            return null;
        }

        // ── 开 / 关 ──────────────────────────────────────────────────────────

        /// <summary>远征出发事务成功：开一份报告（记下出发时的累计读数作为基准）。上一份异常地没结算（不该发生）时先按“其它方式结束”结算，不丢记录。</summary>
        public static AwayReportRecord Begin(CampaignState state, string regionId, IReadOnlyList<int> members)
        {
            AwayReportState d = Domain(state);
            if (d == null)
            {
                return null;
            }
            if (d.HasOpen && d.Open != null && d.Open.RegionId == (regionId ?? string.Empty) && d.Open.StartTick == GameClock.Ticks)
            {
                // 同一步里已经由 Reconcile 开过（远征地点先载入、出发事务随后提交）：只补上正式名单。
                d.Open.Members = ToArray(members);
                return d.Open;
            }
            if (d.HasOpen)
            {
                End(state, OutcomeOther, notify: false);
            }
            var r = new AwayReportRecord
            {
                Serial = d.NextSerial++,
                RegionId = regionId ?? string.Empty,
                StartTick = GameClock.Ticks,
                EndTick = -1,
                Outcome = OutcomeOpen,
            };
            r.Members = ToArray(members);
            ReadTotals(state, out r.BaseFuelMl, out r.BaseFuelOuts, out r.BasePumpedMl, out r.BaseDeliveredMl, out r.BaseFlushedMl, out r.BaseRemovedMl,
                out r.BaseCleared, out r.BaseDiscarded, out r.BaseSplit, out r.BaseBeltIn, out r.BaseBeltOut);
            d.Open = r;
            d.HasOpen = true;
            // 出发那一刻已经在停电：从现在开一段（不伪造出发前的时长）。
            if (HomeValleyPowerGrid.TryGetCachedSummary(state, out HomeValleyPowerGrid.GridSummary summary))
            {
                OnPowerApplied(state, summary.BrownoutBuildingIds);
            }
            Revision++;
            return r;
        }

        /// <summary>撤离 / 放弃结算：补上差额类读数与瓶颈汇总，进“最近几份”，发通知与事件。没有进行中的报告时返回 null。</summary>
        public static AwayReportRecord End(CampaignState state, int outcome, bool notify = true)
        {
            AwayReportState d = Domain(state);
            if (d == null || !d.HasOpen || d.Open == null)
            {
                return null;
            }
            AwayReportRecord r = d.Open;
            long now = GameClock.Ticks;
            r.EndTick = now;
            r.Outcome = outcome;
            if (r.Outages.Length > 0 && r.Outages[r.Outages.Length - 1].EndTick < 0)
            {
                r.Outages[r.Outages.Length - 1].EndTick = now; // 结算时还在停电：显示“到结算时还没恢复”（见 AwayReportView）。
                r.OutageOngoingAtEnd = true;
            }
            FillDeltas(state, r);
            SettleTeam(r);
            r.Starve = CollectStarve(state, r, clear: true).ToArray();
            r.ReactionSessionId = FindReactionSession(state, r)?.SessionId ?? string.Empty;
            d.HasOpen = false;
            d.Open = new AwayReportRecord();
            var kept = new List<AwayReportRecord>(d.Reports ?? Array.Empty<AwayReportRecord>()) { r };
            int keep = ReportsKeep;
            if (kept.Count > keep)
            {
                kept.RemoveRange(0, kept.Count - keep);
            }
            d.Reports = kept.ToArray();
            Revision++;
            if (notify)
            {
                // 修复轮（审查 P2）：正文写明去处（暂停菜单按钮与当前绑定的快捷键）；点击这条通知直接打开离家报告（AwayReportPanelUIToolkit 登记的去处）。
                NotificationCenter.Post("away_report", ReadyText(r));
                TEngine.GameEvent.Send(ReportReadyEvent, r.Serial);
            }
            return r;
        }

        /// <summary>
        /// 每个世界步（家园载入时）对齐一次，O(1)：报告开着但远征已经不在（读了一个远征已结束的档等异常路径）→ 按“其它方式结束”结算、不弹出；
        /// 远征在外却没有报告（本 Story 之前的旧档读档时正在远征）→ 从此刻开一份（名单 = 此刻在远征地点的机器，只在这一次扫描）。
        /// 正式路径（出发事务 / 撤离事务）在同一个调用里先开 / 关，不会走到这里。
        /// </summary>
        public static void Reconcile(CampaignState state, string activeSiteId)
        {
            AwayReportState d = state?.Stats?.AwayReports;
            if (d == null)
            {
                return;
            }
            bool away = !string.IsNullOrEmpty(activeSiteId);
            if (d.HasOpen && (!away || (d.Open != null && d.Open.RegionId != activeSiteId)))
            {
                // 远征已不在，或在外的已经换成另一个地点（测试捷径同一帧卸载 / 派遣）：这一份按“其它方式结束”结算，下一步再为新地点开一份。
                End(state, OutcomeOther, notify: false);
            }
            else if (!d.HasOpen && away)
            {
                var members = new List<int>();
                foreach (MachineRecord m in MachineRegistry.AllRecords)
                {
                    // 修复轮（审查 P2）：只算活着的（以前死在同一地点的机器 RegionId 不变，不能算进这次的远征队；与 GameRoot.ResumeFracturedCity 的 alreadyThere 同口径）。
                    if (m != null && m.IsAlive && m.RegionId == activeSiteId)
                    {
                        members.Add(m.LogicId);
                    }
                }
                Begin(state, activeSiteId, members);
            }
        }

        /// <summary>
        /// FG4-ECO-09 修复轮（审查 P1）：结算时把远征队结局（名单里阵亡几台）与受伤机器此刻的耐久固化进记录。
        /// 已结算的报告是历史记录：之后同一批机器再出征阵亡、被修好或再受伤，都不改写这一份（FGT-ECO-007“报告与实际发生的一致”）。
        /// O(名单 + 机器条目)，只在结算这一次调用。
        /// </summary>
        public static void SettleTeam(AwayReportRecord r)
        {
            if (r == null)
            {
                return;
            }
            r.MembersLost = CountLostNow(r);
            r.TeamSettled = true;
            foreach (AwayMachineRecord a in r.Machines)
            {
                if (a == null || a.Died)
                {
                    continue;
                }
                if (MachineRegistry.TryGetRecord(a.LogicId, out MachineRecord m))
                {
                    a.EndHealth = m.Health;
                    a.EndMaxHealth = m.MaxHealth > 0f ? m.MaxHealth : a.MaxHealth;
                }
                else
                {
                    a.EndHealth = a.MinHealth;
                    a.EndMaxHealth = a.MaxHealth;
                }
                a.HasEndHealth = true;
            }
        }

        /// <summary>远征队里阵亡几台：已结算并固化的报告读固化值；进行中（或固化前的旧档）按此刻的机器登记现算。</summary>
        public static int MembersLost(AwayReportRecord r)
        {
            if (r == null)
            {
                return 0;
            }
            return r.EndTick >= 0 && r.TeamSettled ? r.MembersLost : CountLostNow(r);
        }

        private static int CountLostNow(AwayReportRecord r)
        {
            int lost = 0;
            foreach (int id in r.Members)
            {
                if (MachineRegistry.TryGetRecord(id, out MachineRecord m) && !m.IsAlive)
                {
                    lost++;
                }
            }
            return lost;
        }

        private static int[] ToArray(IReadOnlyList<int> members)
        {
            if (members == null)
            {
                return Array.Empty<int>();
            }
            var ids = new int[members.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = members[i];
            }
            return ids;
        }

        /// <summary>“报告已生成”通知的正文：“破碎都市 · 23 分钟（暂停菜单“离家报告”或 Alt+H）”。</summary>
        public static string ReadyText(AwayReportRecord r) =>
            GameText.Format("away.notify.ready", ShortTitle(r), InputDisplay.ForAction(GameActionId.OpenAwayReport));

        /// <summary>报告标题（下拉框 / 通知）：“破碎都市 · 离家 23 分钟”。</summary>
        public static string ShortTitle(AwayReportRecord r)
        {
            if (r == null)
            {
                return string.Empty;
            }
            long end = r.EndTick >= 0 ? r.EndTick : GameClock.Ticks;
            return SiteName(r.RegionId) + " · " + Duration(end - r.StartTick);
        }

        public static string SiteName(string regionId) => string.IsNullOrEmpty(regionId) ? GameText.Get("away.where.expedition") : CombatSites.SiteName(regionId);

        /// <summary>世界步数 → “N 分钟” / “H 小时 M 分钟”（游戏时间）。</summary>
        public static string Duration(long ticks)
        {
            double sec = Math.Max(0, ticks) / (double)Math.Max(1, GameClock.StepHz);
            if (sec < 60)
            {
                return GameText.Format("away.duration.seconds", ((int)Math.Round(sec)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            int minutes = (int)Math.Round(sec / 60.0);
            if (minutes < 60)
            {
                return GameText.Format("away.duration.minutes", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return GameText.Format("away.duration.hours", minutes / 60, minutes % 60);
        }

        // ── 记账钩子（只在有进行中的报告时做事）──────────────────────────────

        /// <summary>生产统计记账点（<see cref="ProductionStats"/>）：离家期间的产出 / 消耗。</summary>
        public static void OnRecord(CampaignState state, string itemId, long amount, bool produced)
        {
            AwayReportRecord r = Current(state);
            if (r == null || string.IsNullOrEmpty(itemId) || amount <= 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (produced)
            {
                ProductionStats.Add(ref r.Produced, itemId, amount);
            }
            else
            {
                ProductionStats.Add(ref r.Consumed, itemId, amount);
            }
            Tick(t0);
        }

        /// <summary>生产步的缺料记账（<see cref="ProductionService"/>）：记在建筑自己身上（O(1)，不分配），结算时汇总成瓶颈。</summary>
        public static void OnStarve(CampaignState state, ProducerRecord rec, string itemId, int ticks)
        {
            AwayReportRecord r = Current(state);
            if (r == null || rec == null || string.IsNullOrEmpty(itemId) || ticks <= 0)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (rec.AwaySerial != r.Serial)
            {
                rec.AwaySerial = r.Serial;
                rec.AwayStarve = Array.Empty<ItemStackRecord>();
            }
            ProductionService.Add(ref rec.AwayStarve, itemId, ticks);
            Tick(t0);
        }

        /// <summary>电网结算写回（<see cref="HomeValleyPowerGrid"/>）：按“此刻缺电停机的建筑”开 / 关一段停电。</summary>
        public static void OnPowerApplied(CampaignState state, string[] brownoutIds)
        {
            AwayReportRecord r = Current(state);
            if (r == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = brownoutIds?.Length ?? 0;
            AwayOutageRecord last = r.Outages.Length > 0 ? r.Outages[r.Outages.Length - 1] : null;
            bool ongoing = r.OutageOverflowOpen || (last != null && last.EndTick < 0);
            long now = GameClock.Ticks;
            if (n > 0)
            {
                if (!ongoing)
                {
                    if (r.Outages.Length >= OutagesMax)
                    {
                        r.OutagesDropped++;
                        r.OutageOverflowOpen = true;
                    }
                    else
                    {
                        BuildingRecord b = HomeGridService.FindBuilding(state, brownoutIds[0]);
                        var o = new AwayOutageRecord
                        {
                            StartTick = now,
                            EndTick = -1,
                            Peak = n,
                            BuildingId = brownoutIds[0] ?? string.Empty,
                            X = b?.Position.x ?? 0f,
                            Y = b?.Position.y ?? 0f,
                        };
                        Append(ref r.Outages, o);
                        Revision++;
                    }
                }
                else if (!r.OutageOverflowOpen && last != null && n > last.Peak)
                {
                    last.Peak = n;
                }
            }
            else if (ongoing)
            {
                if (r.OutageOverflowOpen)
                {
                    r.OutageOverflowOpen = false;
                }
                else if (last != null)
                {
                    last.EndTick = now;
                }
                Revision++;
            }
            Tick(t0);
        }

        /// <summary>电网每游戏秒（<see cref="HomeValleyPowerGrid.WorldStep"/>）：累加发电（按类别）/ 需要 / 实际供上 / 储能充放，统计缺电秒数。O(电网数 × 发电类别)。</summary>
        public static void OnPowerSecond(CampaignState state, PowerKernel kernel)
        {
            AwayReportRecord r = Current(state);
            if (r == null || kernel == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int classes = Math.Min(HomeValleyPowerGrid.SourceClassCount, PowerKernel.MaxSourceClasses);
            if (r.ClassSum.Length < classes)
            {
                var grown = new double[classes];
                Array.Copy(r.ClassSum, grown, r.ClassSum.Length);
                r.ClassSum = grown;
            }
            bool shortNow = false;
            r.PowerSeconds++;
            for (int s = 0; s < kernel.SubnetCount; s++)
            {
                PowerSubnetInfo info = kernel.Subnet(s);
                r.SupplySum += info.Supply;
                r.DemandSum += info.Demand;
                r.DeliveredSum += info.Delivered;
                if (info.StorageFlow > 0f)
                {
                    r.ChargedSum += info.StorageFlow;
                }
                else
                {
                    r.DischargedSum += -info.StorageFlow;
                }
                if (info.Delivered + 0.5f < info.Demand)
                {
                    shortNow = true;
                }
                for (int c = 0; c < classes; c++)
                {
                    r.ClassSum[c] += kernel.ClassSupply(s, c);
                }
            }
            if (shortNow)
            {
                r.ShortSeconds++;
            }
            Tick(t0);
        }

        /// <summary>机器受到伤害（<see cref="MachineRegistry.ApplyDamage"/>，唯一的外部伤害写入口）。</summary>
        public static void OnMachineDamaged(CampaignState state, MachineRecord m)
        {
            AwayReportRecord r = Current(state);
            if (r == null || m == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            AwayMachineRecord a = MachineEntry(r, m, create: true);
            if (a != null)
            {
                a.MinHealth = Mathf.Min(a.MinHealth, m.Health);
                a.MaxHealth = m.MaxHealth;
                Vector2 pos = MachineRegistry.TryGetLivePosition(m.LogicId, out Vector2 live) ? live : m.WorldPosition;
                a.X = pos.x;
                a.Y = pos.y;
                a.RegionId = m.RegionId ?? string.Empty;
            }
            Tick(t0);
        }

        /// <summary>机器阵亡（<see cref="MachineRegistry.MarkDeadByLogicId"/>，存活 → 阵亡的唯一翻转点）。</summary>
        public static void OnMachineDied(CampaignState state, MachineRecord m, Vector2 at)
        {
            AwayReportRecord r = Current(state);
            if (r == null || m == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            AwayMachineRecord a = MachineEntry(r, m, create: true);
            if (a == null)
            {
                r.MachinesDropped++; // 阵亡对每台机器只会发生一次，所以超出上限的计数不会重复。
            }
            else
            {
                a.Died = true;
                a.DiedTick = GameClock.Ticks;
                a.MinHealth = 0f;
                a.MaxHealth = m.MaxHealth;
                a.X = at.x;
                a.Y = at.y;
                a.RegionId = m.RegionId ?? string.Empty;
            }
            Revision++;
            Tick(t0);
        }

        /// <summary>通知中心（<see cref="NotificationCenter"/>）：登记了离家报告分段的通知逐条记进报告（不受通知聚合影响）。</summary>
        public static void OnNotification(CampaignState state, NotifyTypeDef def, NotificationMember member)
        {
            if (def == null || member == null || string.IsNullOrEmpty(def.AwaySection) || def.AwaySection == "none")
            {
                return;
            }
            AwayReportRecord r = Current(state);
            if (r == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (r.Entries.Length >= EntriesMax)
            {
                r.EntriesDropped++;
            }
            else
            {
                Append(ref r.Entries, new AwayEntryRecord
                {
                    Section = def.AwaySection,
                    TypeId = def.Id,
                    Detail = member.Detail ?? string.Empty,
                    Tick = GameClock.Ticks,
                    HasLocation = member.HasLocation,
                    RegionId = member.RegionId ?? string.Empty,
                    X = member.Location.x,
                    Y = member.Location.y,
                    Z = member.Location.z,
                });
            }
            Revision++;
            Tick(t0);
        }

        /// <summary>常驻规则写触发日志（<see cref="StandingRuleService"/>）：离家期间的规则动作（FG-GAP-098）。</summary>
        public static void OnRuleLog(CampaignState state, RuleLogRecord e)
        {
            AwayReportRecord r = Current(state);
            if (r == null || e == null)
            {
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            r.RulesTotal++;
            if (r.Rules.Length < RulesMax)
            {
                Append(ref r.Rules, e);
            }
            Revision++;
            Tick(t0);
        }

        // ── 结算 / 显示用的汇总 ──────────────────────────────────────────────

        /// <summary>出发 / 此刻的累计读数：燃油累计、燃油耗尽次数、管线四项累计、清带送回 / 丢弃、分流器累计分出。O(生产建筑 + 分流器)，只在开 / 关 / 显示进行中的报告时调用。</summary>
        public static void ReadTotals(CampaignState state, out long fuelMl, out long fuelOuts, out long pumped, out long delivered, out long flushed, out long removed,
            out long cleared, out long discarded, out long split, out long beltIn, out long beltOut)
        {
            beltIn = 0;
            beltOut = 0;
            fuelMl = 0;
            foreach (ProducerRecord p in state?.Economy?.Producers ?? Array.Empty<ProducerRecord>())
            {
                if (p != null && p.FuelBurnedMl > 0)
                {
                    fuelMl += p.FuelBurnedMl;
                }
            }
            fuelOuts = state?.Stats?.Production?.FuelOuts ?? 0;
            PipeKernel pk = PipeNetworkService.IsRunning && ReferenceEquals(PipeNetworkService.BoundState, state) ? PipeNetworkService.Kernel : null;
            pumped = pk?.TotalPumpedMl ?? state?.Pipes?.TotalPumpedMl ?? 0;
            delivered = pk?.TotalDeliveredMl ?? state?.Pipes?.TotalDeliveredMl ?? 0;
            flushed = pk?.TotalFlushedMl ?? state?.Pipes?.TotalFlushedMl ?? 0;
            removed = pk?.TotalRemovedMl ?? state?.Pipes?.TotalRemovedMl ?? 0;
            cleared = state?.Belts?.ClearedToStorage ?? 0;
            discarded = state?.Belts?.Discarded ?? 0;
            split = 0;
            BeltKernel bk = BeltNetworkService.IsRunning && ReferenceEquals(BeltNetworkService.BoundState, state) ? BeltNetworkService.Kernel : null;
            if (bk != null)
            {
                // 传送带：各端口累计收下（送达建筑）/ 推出（推上传送带）之和（内核按端口累计，进存档；网络本身只有窗口速率）。
                PortScratch.Clear();
                bk.CollectPortIds(PortScratch);
                foreach (int id in PortScratch)
                {
                    if (bk.TryGetPortInfo(id, out BeltPortInfo pi))
                    {
                        if (pi.Kind == BeltPortKind.Sink)
                        {
                            beltIn += pi.Total;
                        }
                        else if (pi.Kind == BeltPortKind.Source)
                        {
                            beltOut += pi.Total;
                        }
                    }
                }
                SplitterScratch.Clear();
                bk.CollectSplitters(SplitterScratch);
                foreach (int2 c in SplitterScratch)
                {
                    if (bk.TryGetNodeInfo(c.x, c.y, out BeltNodeInfo info))
                    {
                        split += info.SentL + info.SentR;
                    }
                }
            }
        }

        private static readonly List<int2> SplitterScratch = new List<int2>(8);
        private static readonly List<int> PortScratch = new List<int>(16);

        /// <summary>差额类读数：结算时写进记录；进行中的报告显示时现算（不写记录）。</summary>
        public static void FillDeltas(CampaignState state, AwayReportRecord r)
        {
            ReadTotals(state, out long fuel, out long outs, out long pumped, out long delivered, out long flushed, out long removed, out long cleared, out long discarded, out long split,
                out long beltIn, out long beltOut);
            r.FuelMl = Math.Max(0, fuel - r.BaseFuelMl);
            r.FuelOuts = Math.Max(0, outs - r.BaseFuelOuts);
            r.PumpedMl = Math.Max(0, pumped - r.BasePumpedMl);
            r.DeliveredMl = Math.Max(0, delivered - r.BaseDeliveredMl);
            r.FlushedMl = Math.Max(0, flushed - r.BaseFlushedMl);
            r.RemovedMl = Math.Max(0, removed - r.BaseRemovedMl);
            r.Cleared = Math.Max(0, cleared - r.BaseCleared);
            r.Discarded = Math.Max(0, discarded - r.BaseDiscarded);
            r.Split = Math.Max(0, split - r.BaseSplit);
            // 离家期间拆掉的建筑端口带走了它的累计：差额按 0 下限（不会出现负数）。
            r.BeltIn = Math.Max(0, beltIn - r.BaseBeltIn);
            r.BeltOut = Math.Max(0, beltOut - r.BaseBeltOut);
        }

        /// <summary>瓶颈原始数据：各生产建筑在这份报告期间缺每种物品的步数（缺得最久的在前）。<paramref name="clear"/> = 结算（清掉建筑上的计数）。O(生产建筑)。</summary>
        public static List<AwayStarveRecord> CollectStarve(CampaignState state, AwayReportRecord r, bool clear)
        {
            var list = new List<AwayStarveRecord>();
            if (r == null)
            {
                return list;
            }
            if (r.EndTick >= 0 && !clear)
            {
                list.AddRange(r.Starve);
                return list;
            }
            foreach (ProducerRecord p in state?.Economy?.Producers ?? Array.Empty<ProducerRecord>())
            {
                CloseScans++;
                if (p == null || p.AwaySerial != r.Serial || p.AwayStarve == null)
                {
                    continue;
                }
                BuildingRecord b = HomeGridService.FindBuilding(state, p.BuildingId);
                foreach (ItemStackRecord s in p.AwayStarve)
                {
                    if (s != null && s.Amount > 0)
                    {
                        list.Add(new AwayStarveRecord { ItemId = s.ItemId, BuildingId = p.BuildingId, Ticks = s.Amount, X = b?.Position.x ?? 0f, Y = b?.Position.y ?? 0f });
                    }
                }
                if (clear)
                {
                    p.AwayStarve = Array.Empty<ItemStackRecord>();
                    p.AwaySerial = 0;
                }
            }
            list.Sort((a, b) => a.Ticks != b.Ticks ? b.Ticks.CompareTo(a.Ticks)
                : string.CompareOrdinal(a.ItemId, b.ItemId) != 0 ? string.CompareOrdinal(a.ItemId, b.ItemId) : string.CompareOrdinal(a.BuildingId, b.BuildingId));
            return list;
        }

        /// <summary>这次远征的反应伤害归因场次：同一地点、出发之后开的那一场（FG2-FW-04；GAP-055 远征段）。</summary>
        public static ReactionSessionRecord FindReactionSession(CampaignState state, AwayReportRecord r)
        {
            if (r == null || string.IsNullOrEmpty(r.RegionId))
            {
                return null;
            }
            ReactionSessionRecord best = null;
            foreach (ReactionSessionRecord s in ReactionAttribution.Sessions(state))
            {
                if (s != null && s.Kind == "expedition" && s.SiteId == r.RegionId && s.StartTick >= r.StartTick && (r.EndTick < 0 || s.StartTick <= r.EndTick))
                {
                    best = s;
                }
            }
            if (best == null && !string.IsNullOrEmpty(r.ReactionSessionId))
            {
                foreach (ReactionSessionRecord s in ReactionAttribution.Sessions(state))
                {
                    if (s != null && s.SessionId == r.ReactionSessionId)
                    {
                        best = s;
                    }
                }
            }
            return best;
        }

        // ── 内部 ─────────────────────────────────────────────────────────────

        private static AwayMachineRecord MachineEntry(AwayReportRecord r, MachineRecord m, bool create)
        {
            foreach (AwayMachineRecord a in r.Machines)
            {
                if (a != null && a.LogicId == m.LogicId)
                {
                    return a;
                }
            }
            if (!create || r.Machines.Length >= MachinesMax)
            {
                return null;
            }
            var made = new AwayMachineRecord
            {
                LogicId = m.LogicId,
                Expedition = Array.IndexOf(r.Members, m.LogicId) >= 0,
                MinHealth = m.Health,
                MaxHealth = m.MaxHealth,
                RegionId = m.RegionId ?? string.Empty,
            };
            Append(ref r.Machines, made);
            Revision++;
            return made;
        }

        private static void Append<T>(ref T[] arr, T item)
        {
            arr ??= Array.Empty<T>();
            var next = new T[arr.Length + 1];
            Array.Copy(arr, next, arr.Length);
            next[arr.Length] = item;
            arr = next;
        }

        private static void Tick(long t0)
        {
            HookCalls++;
            HookMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }
}
