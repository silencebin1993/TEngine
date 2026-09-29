using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.EditorTools.JourneyBots;
using UnityEngine;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG1-E2E-01 自检 [M1 出口]：里程碑出口的自动化部分里，能在编辑模式下逐语义断言的那些。
    ///
    /// A. 存档计时整数步（DEBT-FG1SIG07-05）：核心固件冷却、裸跑计次、安全模式进入 / 条件消失、高功率窗口经真实存档写盘再读回逐位相同；
    ///    随机 5,000 组整数步经 JsonUtility 往返逐位相同（对照：同样的值按游戏秒存 double 时的往返差异只报告）。
    /// B. 旧档迁移：旧档里按游戏秒存的五种字段读档时换成整数步（四舍五入）、旧字段清空；再读一次什么也不做（幂等）。
    /// C. 整数步的行为：冷却 / 裸跑剩余时间按步数精确换算；高功率窗口恰好在第 N 步结算一次（第 N−1 步不结算）。
    /// D. 旅程框架（DEBT-FG0QA01-07）：切换类按键先读状态再决定按不按——首按已生效、本步因别的检查重试时不会再按一次把状态切回去
    ///    （对照：不读状态的写法会切回去、旅程失败）；首按被吞时重试会再按、最终生效。
    /// E. 旅程登记：FGJ-M1 覆盖里程碑出口旅程的 9 步 + 接入中存读档；FGJ-M1R 覆盖 IC-REQ-022 的六类反向场景；两条用不同的固定种子；FGJ-M0 不再用镜头捷径跟编队。
    /// F. 缺口清零（里程碑出口第 5 条）：FG-GAP-REGISTER 里最迟里程碑是 M1 的缺口全部 Closed；门禁是“FG-M1（出口）”的延后项全部 Closed / 部分关闭 / 写明顺延。
    /// G. 试玩包：FGR-BAL-060 / 061 要的四份材料都在，标准原文与 5 人汇总表、第 9 节问题都在里面。
    /// H. FGJ-M1 旅程发现的表现交接 bug：出征时远征地点先挂新表现、家园随后摘旧表现——晚到的摘除不能清掉远征地点的形变 / 安全模式图标登记
    ///    （对照：摘掉的就是当前登记的那份时照常清掉）。
    /// </summary>
    public static class FgMilestoneM1SelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private const int Slot = 7;

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[M1 出口] FG1-E2E-01：存档整数步、旧档迁移、旅程框架、旅程登记、缺口清零、试玩包");
            string saveDir = Path.Combine(Path.GetTempPath(), "bingames-m1exit-" + Guid.NewGuid().ToString("N"));
            string oldDir = CampaignSaveService.SaveDirectoryOverrideForTests;
            try
            {
                Directory.CreateDirectory(saveDir);
                CampaignSaveService.SaveDirectoryOverrideForTests = saveDir;
                Step(CheckTickRoundTrip);
                Step(CheckLegacyMigration);
                Step(CheckTickBehaviour);
                Step(CheckToggleRetry);
                Step(CheckJourneyCatalog);
                Step(CheckGapGate);
                Step(CheckPlaytestPackage);
                Step(CheckViewHandover);
            }
            finally
            {
                CampaignSaveService.SaveDirectoryOverrideForTests = oldDir;
                CampaignSession.Clear();
                try
                {
                    Directory.Delete(saveDir, true);
                }
                catch (Exception)
                {
                    // 临时目录删不掉不影响结论。
                }
            }
            Line($"  · [M1 出口] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        private static void Step(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                Fail($"{a.Method.Name} 抛异常：{e}");
            }
        }

        // ── A. 存档整数步 ──────────────────────────────────────────────────────────

        private static long NextLong(System.Random r) => ((long)r.Next(1, int.MaxValue) << 20) ^ r.Next();

        [Serializable]
        private sealed class DoubleBox
        {
            public double D;
        }

        [Serializable]
        private sealed class LongBox
        {
            public long L;
        }

        private static CampaignState NewState(int seed)
        {
            CampaignState s = CampaignState.CreateNew("fg1e2e01-" + seed, "Standard", seed);
            CampaignFgStateDomains.EnsureAll(s);
            return s;
        }

        private static CampaignState SaveLoad(CampaignState s, out string failure)
        {
            failure = null;
            SaveResult w = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            if (!w.Success)
            {
                failure = "写盘失败：" + w.Message;
                return null;
            }
            LoadResult r = CampaignSaveService.Load(Slot);
            if (!r.Success)
            {
                failure = $"读档失败：{r.Outcome}/{r.Reason}";
                return null;
            }
            return r.State;
        }

        private static void CheckTickRoundTrip()
        {
            Line("  · A. DEBT-FG1SIG07-05：信号计时字段改存整数步，经真实存档往返逐位相同");
            var rng = new System.Random(10101);
            CampaignState s = NewState(10101);
            var cds = new List<SignalCoreCooldownRecord>();
            var modes = new List<SignalSafeModeRecord>();
            for (int i = 0; i < 40; i++)
            {
                cds.Add(new SignalCoreCooldownRecord { ContentId = "fw_rt_" + i, ReadyTick = NextLong(rng) });
                modes.Add(new SignalSafeModeRecord { LogicId = 1000 + i, Reason = 1 + i % 3, SinceTick = NextLong(rng), ClearSinceTick = i % 4 == 0 ? -1 : NextLong(rng) });
            }
            s.SignalCore.CoreCooldowns = cds.ToArray();
            s.SignalCore.SafeModes = modes.ToArray();
            s.SignalCore.RawChargeReadyTick = NextLong(rng);
            s.SignalCore.JumpCooldownReadyTick = NextLong(rng);
            s.HighPowerElapsedTicks = NextLong(rng);
            string before = Digest(s);
            CampaignState l = SaveLoad(s, out string failure);
            if (l == null)
            {
                Fail(failure);
                return;
            }
            string after = Digest(l);
            Expect(before == after && l.SignalCore.CoreCooldowns.Length == 40 && l.SignalCore.SafeModes.Length == 40,
                $"40 条冷却到期步、40 条安全模式（进入步 / 条件消失步）、裸跑计次步、跳转冷却步、高功率窗口步写盘再读回逐位相同（{before.Length} 字符摘要）");

            int longBad = 0;
            int doubleBad = 0;
            for (int i = 0; i < 5000; i++)
            {
                long v = NextLong(rng) % 50_000_000L;
                LongBox lb = JsonUtility.FromJson<LongBox>(JsonUtility.ToJson(new LongBox { L = v }));
                longBad += lb.L == v ? 0 : 1;
                // 对照：同一个时刻按游戏秒存 double（冷却到期 = 游戏秒 + 8；高功率窗口 = float 步长逐步累加）
                double d = v / 60.0 + 8.0;
                DoubleBox db = JsonUtility.FromJson<DoubleBox>(JsonUtility.ToJson(new DoubleBox { D = d }));
                doubleBad += BitConverter.DoubleToInt64Bits(db.D) == BitConverter.DoubleToInt64Bits(d) ? 0 : 1;
            }
            Expect(longBad == 0, $"随机 5,000 个整数步经 JsonUtility 往返逐位相同（对照：同样的时刻存成游戏秒 double，往返后不逐位相同 {doubleBad} 个——只报告，不断言）");
        }

        private static string Digest(CampaignState s)
        {
            var sb = new StringBuilder();
            foreach (SignalCoreCooldownRecord c in s.SignalCore.CoreCooldowns)
            {
                sb.Append(c.ContentId).Append('@').Append(c.ReadyTick).Append(';');
            }
            foreach (SignalSafeModeRecord m in s.SignalCore.SafeModes)
            {
                sb.Append(m.LogicId).Append(':').Append(m.Reason).Append(':').Append(m.SinceTick).Append(':').Append(m.ClearSinceTick).Append(';');
            }
            sb.Append('|').Append(s.SignalCore.RawChargeReadyTick).Append('|').Append(s.SignalCore.JumpCooldownReadyTick).Append('|').Append(s.HighPowerElapsedTicks);
            return sb.ToString();
        }

        // ── B. 旧档迁移 ────────────────────────────────────────────────────────────

        private static void CheckLegacyMigration()
        {
            Line("  · B. 旧档（按游戏秒存）读档时换成整数步，旧字段清空，幂等");
            CampaignState s = NewState(10102);
            s.Clock ??= new GameClockState();
            s.Clock.StepHz = 60;
            s.SignalCore.CoreCooldowns = new[] { new SignalCoreCooldownRecord { ContentId = FirmwareCatalog.FwOverloadId, ReadyAtGameSeconds = 26.416675867512823 } };
            s.SignalCore.RawChargeReadyAtGameSeconds = 20.25;
            s.HighPowerElapsedSeconds = 12.5;
            s.SignalCore.SafeModes = new[]
            {
                new SignalSafeModeRecord { LogicId = 7, Reason = 1, SinceGameSeconds = 3.5, ClearSinceGameSeconds = 4.0 },
                new SignalSafeModeRecord { LogicId = 8, Reason = 2, SinceGameSeconds = 5.0, ClearSinceGameSeconds = -1 },
            };
            int migrated0 = SignalTimeMigration.MigratedFields;
            CampaignState l = SaveLoad(s, out string failure);
            if (l == null)
            {
                Fail(failure);
                return;
            }
            SignalCoreCooldownRecord cd = l.SignalCore.CoreCooldowns.Single();
            SignalSafeModeRecord m7 = l.SignalCore.SafeModes.Single(m => m.LogicId == 7);
            SignalSafeModeRecord m8 = l.SignalCore.SafeModes.Single(m => m.LogicId == 8);
            bool values = cd.ReadyTick == 1585 && l.SignalCore.RawChargeReadyTick == 1215 && l.HighPowerElapsedTicks == 750
                          && m7.SinceTick == 210 && m7.ClearSinceTick == 240 && m8.SinceTick == 300 && m8.ClearSinceTick == -1;
            bool cleared = cd.ReadyAtGameSeconds == 0 && l.SignalCore.RawChargeReadyAtGameSeconds == 0 && l.HighPowerElapsedSeconds == 0
                           && m7.SinceGameSeconds == 0 && m7.ClearSinceGameSeconds < 0 && m8.SinceGameSeconds == 0 && m8.ClearSinceGameSeconds < 0;
            Expect(values && cleared && SignalTimeMigration.MigratedFields - migrated0 == 6,
                $"读档迁移：冷却 26.4167 秒 → 第 {cd.ReadyTick} 步、裸跑计次 20.25 秒 → {l.SignalCore.RawChargeReadyTick}、高功率窗口 12.5 秒 → {l.HighPowerElapsedTicks}、" +
                $"安全模式 3.5 / 4.0 秒 → {m7.SinceTick} / {m7.ClearSinceTick}（条件仍在的保持 -1）；旧字段全部清空；迁移 {SignalTimeMigration.MigratedFields - migrated0} 个字段");
            string d1 = Digest(l);
            int again = SignalTimeMigration.Migrate(l);
            CampaignState l2 = SaveLoad(l, out failure);
            Expect(again == 0 && l2 != null && Digest(l2) == d1, "再迁移一次什么也不做（0 个字段）；再存再读逐位相同（幂等）");
        }

        // ── C. 整数步的行为 ──────────────────────────────────────────────────────

        private static void CheckTickBehaviour()
        {
            Line("  · C. 整数步的行为：剩余时间精确换算；高功率窗口恰好在第 N 步结算");
            CampaignState s = NewState(10103);
            long now = GameClock.Ticks;
            s.SignalCore.CoreCooldowns = new[] { new SignalCoreCooldownRecord { ContentId = FirmwareCatalog.FwOverloadId, ReadyTick = now + GameClock.TicksFor(8.0) } };
            s.SignalCore.RawChargeReadyTick = now + GameClock.TicksFor(2.5);
            double cd = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
            double raw = RawFirmwareService.ChargeRemaining(s);
            s.SignalCore.CoreCooldowns[0].ReadyTick = now;
            double expired = SignalUplinkService.CooldownRemaining(s, FirmwareCatalog.FwOverloadId);
            Expect(cd == 8.0 && raw == 2.5 && expired == 0.0 && GameClock.TicksFor(8.0) == 8 * GameClock.StepHz,
                $"冷却到期步 = 现在 + {GameClock.TicksFor(8.0)} 步 → 剩 {cd} 秒（精确）；裸跑计次剩 {raw} 秒；到期步 = 现在 → 剩 0");

            // 高功率窗口：用电需求高于阈值，逐步推进；恰好第 N 步记一笔，第 N−1 步不记。
            CampaignState h = NewState(10104);
            h.PowerDemand = 200f;
            long window = GameClock.TicksFor(CampaignExposureLedger.HighPowerSettleSeconds);
            int Events() => (h.SignalExposureEvents ?? Array.Empty<SignalExposureEventRecord>()).Count(e => e.Kind == ExposureSourceKind.HighPower);
            for (long i = 0; i < window - 1; i++)
            {
                CampaignExposureLedger.SimStepHighPower(h, GameClock.StepSeconds);
            }
            int beforeLast = Events();
            long ticksBefore = h.HighPowerElapsedTicks;
            CampaignExposureLedger.SimStepHighPower(h, GameClock.StepSeconds);
            int afterLast = Events();
            Expect(beforeLast == 0 && ticksBefore == window - 1 && afterLast == 1 && h.HighPowerElapsedTicks == 0,
                $"高功率窗口 {window} 步：第 {window - 1} 步累计 {ticksBefore} 步、不结算；第 {window} 步结算一笔（{afterLast} 条）、窗口归零");
        }

        // ── D. 旅程框架：切换键先读状态 ───────────────────────────────────────────────

        private sealed class Clock : IJourneyClock
        {
            public double T;
            public double Now => T;
        }

        private static string RunToggleJourney(Func<JourneyContext, bool> tickFirstAttemptRetries, Action<JourneyContext> enter, Func<bool> isOn, out int ticks)
        {
            var clock = new Clock();
            var def = new JourneyDef
            {
                Id = "TEST-TOGGLE",
                Title = "切换键重试",
                Seed = 1,
                TotalTimeoutSeconds = 100,
                Steps = new List<JourneyStep>
                {
                    new JourneyStep
                    {
                        Id = "toggle",
                        Title = "toggle",
                        TimeoutSeconds = 3,
                        MaxRetries = 1,
                        OnEnter = enter,
                        Tick = c => tickFirstAttemptRetries(c) ? StepOutcome.Retry("别的检查还没过") : isOn() ? StepOutcome.Done() : StepOutcome.Wait,
                    },
                },
            };
            var r = new JourneyRunner(def, new MemoryJourneyStore(), clock);
            r.Start();
            ticks = 0;
            while (r.Tick() && ticks < 200)
            {
                clock.T += 1;
                ticks++;
            }
            return r.Result;
        }

        private static void CheckToggleRetry()
        {
            Line("  · D. DEBT-FG0QA01-07：切换键先读状态再决定按不按（首按已生效时重试不再按）");
            // ① 首按立即生效，但本步第一次尝试因别的检查重试：先读状态的写法不再按，状态保持 = 通过。
            bool on = false;
            int presses = 0;
            string guarded = RunToggleJourney(c => c.Attempt == 0, c => JourneyInput.EnsureToggle(() => on, true, () => { presses++; on = !on; }), () => on, out _);
            bool guardedOk = guarded == JourneyRunner.ResultPass && presses == 1 && on;
            // 对照：不读状态、进入就按——重试时第二次按把状态切回去，本步永远等不到“开”，超时失败。
            bool on2 = false;
            int presses2 = 0;
            string naive = RunToggleJourney(c => c.Attempt == 0, c => { presses2++; on2 = !on2; }, () => on2, out _);
            Expect(guardedOk && naive == JourneyRunner.ResultFail && presses2 >= 2,
                $"首按已生效 + 重试：先读状态只按 {presses} 次、状态保持开、通过；对照（不读状态）按了 {presses2} 次、状态被切回、旅程 {naive}");
            // ② 首按被吞（没生效）：超时重试时状态仍是关 → 再按一次 → 生效通过。
            bool on3 = false;
            int presses3 = 0;
            string swallowed = RunToggleJourney(c => false, c => JourneyInput.EnsureToggle(() => on3, true, () => { presses3++; on3 = presses3 >= 2; }), () => on3, out int ticks3);
            Expect(swallowed == JourneyRunner.ResultPass && presses3 == 2 && on3,
                $"首按被吞：超时重试时读到仍是关，再按一次后生效（按 {presses3} 次，{ticks3} 次驱动），通过");
            Expect(!JourneyInput.ClickElement(null) && JourneyInput.LastUiFailure.Length > 0
                   && !JourneyInput.ClickElement(new UnityEngine.UIElements.Button()) && JourneyInput.LastUiFailure.Contains("不在面板上"),
                $"UI 点击失败时给原因（不存在：点不了；不在面板上：“{JourneyInput.LastUiFailure}”）");
        }

        // ── E. 旅程登记 ──────────────────────────────────────────────────────────

        private static void CheckJourneyCatalog()
        {
            Line("  · E. 旅程登记：FGJ-M1 覆盖出口旅程 9 步 + 接入中存读档；FGJ-M1R 覆盖 IC-REQ-022 六类；FGJ-M0 改用跟随键");
            JourneyDef m1 = JourneyCatalog.Create(FgjM1Journey.Id);
            JourneyDef m1r = JourneyCatalog.Create(FgjM1ReverseJourney.Id);
            bool Wellformed(JourneyDef d) => d != null && d.Steps.Count > 0 && d.Steps.Select(x => x.Id).Distinct().Count() == d.Steps.Count
                                               && d.Steps.All(x => x.TimeoutSeconds > 0 && x.Tick != null) && d.TotalTimeoutSeconds > 0;
            // 里程碑文档 FG-M1 出口旅程逐步 → 旅程步骤。
            var exit = new (string Step, string Id)[]
            {
                ("在家园编辑电路标出接入口", "cb_mark"), ("看双态预览", "cb_recheck"), ("给信号核装上过载", "sc_equip"), ("远征", "prep_depart"),
                ("接入重炮机、看到形变", "up_cannon"), ("打出熔穿过载", "drive_fire"), ("切到另一台机器、冷却没有重置", "switch"),
                ("被干扰机断链、进入安全模式", "drive_jam"), ("跳回家园", "jump_home"), ("再跳回远征队", "jump_back"), ("远征队返回家园", "evac_confirm"),
                ("接入中存档 → 读取（DEBT-FG1SIG03-06）", "loaded"),
            };
            List<string> missing = exit.Where(e => m1 == null || m1.Steps.All(x => x.Id != e.Id)).Select(e => e.Step).ToList();
            Expect(Wellformed(m1) && missing.Count == 0 && m1.Seed == FgjM1Journey.TestSeed,
                $"FGJ-M1：{m1?.Steps.Count} 步、步骤 ID 不重复、每步有超时与检查、固定种子 {m1?.Seed}；出口旅程各步都有对应步骤{(missing.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missing))}");
            var reverse = new (string Kind, string[] Ids)[]
            {
                ("资源不足", new[] { "r1_print_denied", "r1_print_ok" }), ("路径失败", new[] { "r4_reject", "r2_click_bad", "r2_click_ok" }),
                ("暂停与倍速", new[] { "r3_uplink_paused", "r3_speed_locked", "r3_rate05", "r3_rate2", "r3_rate3" }), ("断链", new[] { "r4_drive", "r6_wait" }),
                ("存读档", new[] { "r5_menu", "r5_loaded" }), ("目标死亡", new[] { "r7_kill", "r7_after" }),
            };
            List<string> missingR = reverse.Where(k => m1r == null || k.Ids.Any(id => m1r.Steps.All(x => x.Id != id))).Select(k => k.Kind).ToList();
            Expect(Wellformed(m1r) && missingR.Count == 0 && m1r.Seed != m1.Seed,
                $"FGJ-M1R：{m1r?.Steps.Count} 步；六类反向场景（每类都有“出错 → 恢复”两端）齐全{(missingR.Count == 0 ? string.Empty : "，缺：" + string.Join("、", missingR))}；种子 {m1r?.Seed} ≠ FGJ-M1 的 {m1?.Seed}（B25 种子无关）");
            string src = ReadRepo("TEngine/UnityProject/Assets/Editor/JourneyBots/FgjM0Journey.cs");
            JourneyDef m0 = JourneyCatalog.Create(FgjM0Journey.Id);
            Expect(src != null && m0 != null && m0.Steps.Any(x => x.Id == "fly_back") && src.Contains("PressFollow") && !src.Contains("FlyTo(GameRoot.HomeValley.SiteId, Centroid"),
                "FGJ-M0 行进段改用“跟随选中对象”键（DEBT-FG1HUD01-06），源码里不再有飞到编队中心的镜头捷径");
        }

        // ── F. 缺口清零 ──────────────────────────────────────────────────────────

        private static readonly Regex M1Token = new Regex(@"(?<![0-9])M1(?![0-9])");

        private static void CheckGapGate()
        {
            Line("  · F. 里程碑出口第 5 条：属于 FG-M1 的缺口 / 延后项全部关闭或写明顺延");
            string text = ReadRepo("production/design/full-game/FG-GAP-REGISTER.md");
            if (text == null)
            {
                Fail("找不到 production/design/full-game/FG-GAP-REGISTER.md（仓库根定位失败）");
                return;
            }
            var openGaps = new List<string>();
            var openDebts = new List<string>();
            int gapRows = 0;
            int debtRows = 0;
            int m1Rows = 0;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (!raw.StartsWith("| FG-GAP-", StringComparison.Ordinal) && !raw.StartsWith("| DEBT-", StringComparison.Ordinal))
                {
                    continue;
                }
                string[] cols = raw.Trim().Trim('|').Split('|').Select(x => x.Trim()).ToArray();
                string status = cols[cols.Length - 1];
                if (raw.StartsWith("| FG-GAP-", StringComparison.Ordinal))
                {
                    gapRows++;
                    if (cols.Length >= 7 && M1Token.IsMatch(cols[5]) && !cols[5].Contains("M1" + "0") && !status.StartsWith("Closed", StringComparison.Ordinal))
                    {
                        m1Rows++;
                        openGaps.Add(cols[0]);
                    }
                    else if (cols.Length >= 7 && M1Token.IsMatch(cols[5]))
                    {
                        m1Rows++;
                    }
                    continue;
                }
                debtRows++;
                if (cols.Length < 9 || !M1Token.IsMatch(cols[6]))
                {
                    continue;
                }
                m1Rows++;
                bool ok = status.StartsWith("Closed", StringComparison.Ordinal) || status.StartsWith("部分关闭", StringComparison.Ordinal) || status.Contains("顺延");
                if (!ok)
                {
                    openDebts.Add(cols[0]);
                }
            }
            Expect(gapRows > 40 && debtRows > 100 && m1Rows > 10, $"读到缺口 {gapRows} 行、延后项 {debtRows} 行，其中属于 FG-M1 的 {m1Rows} 行（解析没有漏行）");
            Expect(openGaps.Count == 0 && openDebts.Count == 0,
                openGaps.Count + openDebts.Count == 0
                    ? "属于 FG-M1 的缺口全部 Closed，延后项全部 Closed / 部分关闭（剩余部分另有门禁）/ 写明顺延"
                    : $"FG-M1 出口前还开着：缺口 [{string.Join("、", openGaps)}]，延后项 [{string.Join("、", openDebts)}]");
        }

        // ── G. 试玩包 ────────────────────────────────────────────────────────────

        private static void CheckPlaytestPackage()
        {
            Line("  · G. FGR-BAL-060 / 061 试玩包（真人试玩由用户完成，DEBT-FG1E2E01-01）");
            string dir = "production/design/full-game/playtest/";
            string script = ReadRepo(dir + "FG-M1-试玩脚本.md");
            string table = ReadRepo(dir + "FG-M1-通过标准对照表.md");
            string form = ReadRepo(dir + "FG-M1-问卷与记录模板.md");
            string issues = ReadRepo(dir + "FG-M1-问题清单模板.md");
            string readme = ReadRepo(dir + "README.md");
            bool present = script != null && table != null && form != null && issues != null && readme != null;
            bool standard = table != null && table.Contains("5 人中至少 4 人，在前 30 分钟内主动接入过至少 3 台不同的机器")
                            && new[] { "| P1 |", "| P2 |", "| P3 |", "| P4 |", "| P5 |" }.All(table.Contains) && table.Contains("主动接入");
            bool questions = form != null && form.Contains("FG01 §9 Q1") && form.Contains("FG01 §9 Q2") && form.Contains("FG01 §9 Q3")
                             && form.Contains("FG02 §9 Q1") && form.Contains("FG02 §9 Q2") && form.Contains("FG02 §9 Q3");
            bool rules = script != null && script.Contains("不能说的") && script.Contains("开场白") && issues != null && issues.Contains("P0") && readme != null && readme.Contains("DEBT-FG1E2E01-01");
            Expect(present && standard && questions && rules,
                "试玩包四份材料齐全：脚本（开场白原文、可说 / 不可说、介入规则）、通过标准对照表（FGR-BAL-061 原文、主动接入口径、5 人汇总）、问卷与记录（FG01 / FG02 第 9 节六个问题）、问题清单（P0～P3）");
        }

        // ── H. 表现交接 ──────────────────────────────────────────────────────────

        private static void CheckViewHandover()
        {
            Line("  · H. 出征时同一台机器的两份表现交接：远征地点先挂、家园后摘（再摘一次已经没有表现的旧标记），形变与安全模式图标登记留在远征地点那份上");
            CampaignState s = NewState(10105);
            CampaignSession.Set(Slot, s);
            const int id = 424242;
            s.SignalCore.SafeModes = new[] { new SignalSafeModeRecord { LogicId = id, Reason = (int)SignalLinkBreakReason.Jammed, SinceTick = 1, ClearSinceTick = -1 } };
            GameObject goA = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            GameObject goB = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            try
            {
                var viewA = goA.AddComponent<GameLogic.Campaign.Regions.MachineView>();
                viewA.Initialize(goA.GetComponent<Renderer>(), Color.white);
                var viewB = goB.AddComponent<GameLogic.Campaign.Regions.MachineView>();
                viewB.Initialize(goB.GetComponent<Renderer>(), Color.white);
                var home = new GameLogic.Campaign.Regions.HomeValleyMachineMarker(id, GameLogic.Campaign.Regions.HomeValleyLayout.Erc003ChassisId, null, 0);
                var away = new GameLogic.Campaign.Regions.HomeValleyMachineMarker(id, GameLogic.Campaign.Regions.HomeValleyLayout.Erc003ChassisId, null, 0);
                home.AttachView(viewA);
                bool atHome = GameLogic.View.MachineMorphView.IsRegistered(id) && GameLogic.View.SignalLinkView.BadgeWanted(id);
                away.AttachView(viewB);   // 远征地点先挂上新表现
                home.DetachView();        // 家园随后才摘掉旧表现
                home.DetachView();        // 家园模拟步移走出征机器的标记时再摘一次（这时它已经没有表现了）
                GameLogic.View.WorldBadge badge = GameLogic.View.SignalLinkView.BadgeFor(id);
                bool kept = GameLogic.View.MachineMorphView.IsRegistered(id) && GameLogic.View.SignalLinkView.BadgeWanted(id)
                            && badge != null && badge.transform.IsChildOf(viewB.transform);
                away.DetachView();        // 对照：摘的就是当前登记的那份 → 照常清掉
                bool cleared = !GameLogic.View.MachineMorphView.IsRegistered(id) && GameLogic.View.SignalLinkView.BadgeFor(id) == null;
                Expect(atHome && kept && cleared,
                    $"家园挂上：登记在家园那份（{atHome}）；远征地点挂上后家园才摘：形变登记与安全模式图标仍在远征地点那份上（{kept}）；摘掉远征地点那份：照常清掉（{cleared}）");
            }
            finally
            {
                GameLogic.View.MachineMorphView.OnViewDetached(id);
                GameLogic.View.SignalLinkView.OnViewDetached(id);
                UnityEngine.Object.DestroyImmediate(goA);
                UnityEngine.Object.DestroyImmediate(goB);
                CampaignSession.Clear();
            }
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        /// <summary>真工程（仓库根/TEngine/UnityProject）与影子工程（仓库根/.unity-validate-clone）都能向上找到仓库根。</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "production", "design", "full-game")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "TEngine", "UnityProject", "DesignDocs")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        private static string ReadRepo(string relative)
        {
            string root = RepoRoot();
            string path = root == null ? null : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            return path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("  ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("  ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
