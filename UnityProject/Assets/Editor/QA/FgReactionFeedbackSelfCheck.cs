using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using GameLogic.UI.Expedition;
using GameLogic.UI.Kit;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-FW-04 反应反馈与伤害归因的自动验收（FG02 FGR-FW-043；FGT-FW-005；卡片负向“同一帧触发 200 次反应”；
    /// 承接 DEBT-FG2FW03-01（弹字 / 聚合 / 上限 / 慢放 / 图鉴）、DEBT-FG2FW03-02（逐标签到期）、DEBT-FG2FW02-02（读法弹字与音效））。
    /// 起真实系统跑、断言行为：
    /// A 内核：逐标签到期（先挂的先到期、持续伤害跟着停）、快照格式 5 往返 / 格式 4 兼容 / 坏值拒绝、反馈进给（最后位置 / 出手者 / 目标、敌方反应伤害、敌方总伤害、纪元）；
    /// B FGT-FW-005：真实地点（CombatSite.Step）里第一次打出短路 → 首次触发记录（图鉴解锁）、慢放只一次、镜头推动；关掉设置后不慢放不推镜头但仍解锁；
    ///   不观察的地点只记录不慢放；未开放命名的反应不算首次；首次记录真实存读档、旧档没有字段；
    /// C 慢放 × 暂停 × 0.5x～3x：步数按倍速 × 慢放倍率成比例、暂停时冻结不流逝、慢放不改变模拟结果（与不慢放的同一段哈希一致）；
    /// D 弹字：同一帧 200 次同一反应 → 一条“短路 ×200”、次数不丢；多种反应同一帧 → 1 秒内新开不超过上限、其余攒着稍后放完；关掉设置不弹；首次触发单独一条；读法弹字；
    ///   真实内核同一帧 200 次反应（提示事件被丢也不影响）：一条日志 ×200、归因 200 次、音效只出一次；
    /// E 伤害归因：破碎都市正式流程（编队攻击打出短路）→ 远征场次、占比 = 反应伤害 / 敌方总伤害（与内核对账）、撤离后关闭且不再开同次场；家园突袭场次；
    ///   克制类只记次数不记伤害；场次上限；真实存读档；撤离报告显示占比；观察与不观察结果一致（FGR-BASE-021）；
    /// F 反应日志：触发者 / 参与的固件 / 目标；远征 / 突袭筛选；容量 50；换战役清空；
    /// G 界面：暂停菜单三个开关与恢复默认、反应记录面板三页签与筛选、弹字层、布局探针（中英、缩放极值、四种分辨率）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgReactionFeedbackSelfCheck
    {
        private const int Slot = 0;
        private const int RunSlot = 1;
        private const string BpWet = "bp_fgfw04_wet";
        private const string BpShock = "bp_fgfw04_shock";
        private const string SiteId = "fgfw04-site";
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static float _fakeNow;

        [MenuItem("BinGames/Validate/FG2-FW-04 反应反馈与伤害归因")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(fail == 0 ? 0 : 1);
            }
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            Line("\n[反应反馈] 反应反馈与伤害归因（FG2-FW-04）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            Func<string> originalObserved = FeedbackCues.ObservedSiteProvider;
            bool popups = GameSettings.ReactionPopupsEnabled;
            bool slowmo = GameSettings.ReactionSlowMotionEnabled;
            bool nudge = GameSettings.ReactionCameraNudgeEnabled;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfw04-selfcheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                CarrierReadings.Reload();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                UiTuningValues.Reload();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                GameSettings.ResetReactionFeedback();
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                Directory.CreateDirectory(_dir);
                ReactionFeedback.RealTime = () => _fakeNow;
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}；弹字 / 慢放 / 镜头推动用真实秒（自检用可控的假时钟），模拟步用统一时钟整数步");

                Step(CheckKernelPerBitExpiry);
                Step(CheckKernelSnapshotFormat5);
                Step(CheckKernelFeed);
                Step(CheckFirstTrigger);
                Step(CheckFirstTriggerSaveLoad);
                Step(CheckSlowMotionClock);
                Step(CheckPopupThrottle);
                Step(CheckSameFrame200);
                Step(CheckWorldAttribution);
                Step(CheckRaidAndCounterReactions);
                Step(CheckLog);
                Step(CheckUi);
            }
            catch (Exception e)
            {
                Fail($"反应反馈自检抛异常：{e}");
            }
            finally
            {
                WorldSimulation.UnloadAll();
                FeedbackCues.ObservedSiteProvider = originalObserved;
                ReactionFeedback.ResetForTests();
                ReactionPopups.ResetForTests();
                ReactionLog.Clear();
                CameraNudge.ResetForTests();
                ReactionLogPanelUIToolkit.InWorldOverrideForTests = false;
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = false;
                FirmwareKinds.ResetForTests();
                CarrierReadings.ResetForTests();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
                GameSettings.SetReactionPopupsEnabled(popups);
                GameSettings.SetReactionSlowMotionEnabled(slowmo);
                GameSettings.SetReactionCameraNudgeEnabled(nudge);
                CampaignSession.Set(originalSlot, originalSession);
                try
                {
                    if (Directory.Exists(_dir))
                    {
                        Directory.Delete(_dir, true);
                    }
                }
                catch (IOException)
                {
                }
            }
            Line($"  [反应反馈] 断言 {_pass} 过 / {_fail} 败");
            return _fail;
        }

        // ── A. 内核：逐标签到期（DEBT-FG2FW03-02）───────────────────────────────

        private static void CheckKernelPerBitExpiry()
        {
            Line("  · A1. 逐标签到期：先挂的标签先到期，只清那一位并按剩下的位重算效果（燃烧到期后持续伤害停下，后挂的标签照常留着）；悬停读数逐标签剩余时间");
            NewState(9401);
            string other = TagWithoutReaction("Fire");
            uint fire = NamedReactionCatalog.BitOf("Fire");
            uint ob = NamedReactionCatalog.BitOf(other);
            float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
            using (var a = new Arena(true))
            {
                int e = a.Enemy();
                a.K.ApplyStatus(e, fire, 1.0f, fireDps, 0f, 0f, 0);
                a.Run(0.5f);
                a.K.ApplyStatus(e, ob, 3.0f, 0f, 0f, 0f, 0);
                a.Run(0.25f); // t ≈ 0.75
                bool bothAt075 = a.Has(e, "Fire") && a.Has(e, other);
                float fireLeft = (float)a.K.StatusBitSecondsLeft(e, Bit("Fire"));
                float otherLeft = (float)a.K.StatusBitSecondsLeft(e, Bit(other));
                List<(string Glyph, string Name, float Seconds, int Stacks)> hover = StatusTagHover.Describe(a.K, e);
                bool hoverPerTag = hover != null && hover.Count == 2 && Math.Abs(hover.Max(h => h.Seconds) - hover.Min(h => h.Seconds)) > 1.5f;
                a.Run(0.5f); // t ≈ 1.25：燃烧已到期
                a.K.TryGetUnit(e, out CombatUnitView v1);
                bool fireGone = !a.Has(e, "Fire") && a.Has(e, other) && a.K.StatusStacksOf(e, Bit("Fire")) == 0;
                a.K.TryGetStatus(e, out _, out _, out float dpsAfter, out _, out _);
                a.Run(1.5f); // t ≈ 2.75：别的标签还在，燃烧不再掉血
                a.K.TryGetUnit(e, out CombatUnitView v2);
                bool otherStill = a.Has(e, other);
                a.Run(1.0f); // t ≈ 3.75：别的标签也到期
                bool allGone = !a.Has(e, other) && !a.Has(e, "Fire");
                Expect(bothAt075 && Math.Abs(fireLeft - 0.25f) < 0.05f && Math.Abs(otherLeft - 2.75f) < 0.05f && hoverPerTag,
                    $"t=0.75：两个标签都在，燃烧剩 {fireLeft:0.00} 秒、{other} 剩 {otherLeft:0.00} 秒（各自的到期，不是整组取晚）；悬停读数逐标签显示剩余时间");
                Expect(fireGone && dpsAfter == 0f && Math.Abs(v1.Health - v2.Health) < 1e-3f && otherStill && allGone && fireDps > 0f,
                    $"燃烧 1 秒到期：只清燃烧位（叠层清零），持续伤害重算为 {dpsAfter}，之后 1.5 秒不再掉血（{v1.Health:0.0} → {v2.Health:0.0}）；{other} 3 秒后照常到期（此前整组共用到期会让燃烧烧到 3.5 秒）");
            }
            using (var a = new Arena(true))
            {
                int e = a.Enemy();
                a.K.ApplyStatus(e, ob, 2.0f, 0f, 0f, 0f, 0);
                a.Run(1.0f);
                a.K.ApplyStatus(e, fire, 2.0f, fireDps, 0f, 0f, 0);
                a.K.ApplyStatus(e, ob, 0.5f, 0f, 0f, 0f, 0); // 再挂一次更短的：已有的位取晚（仍到 2.0）
                a.Run(1.2f); // t = 2.2
                bool otherExpired = !a.Has(e, other) && a.Has(e, "Fire");
                float left = (float)a.K.StatusBitSecondsLeft(e, Bit("Fire"));
                Expect(otherExpired && Math.Abs(left - 0.8f) < 0.05f,
                    $"先挂的 {other}（2 秒，中途再挂更短的只取晚不缩短）在 t=2.2 已到期，后挂的燃烧还剩 {left:0.00} 秒（续挂只续自己那一位）");
            }
            using (var a = new Arena(false))
            {
                // 状态位效果表没配置的旧地点 / 测试内核：不知道哪份效果属于哪一位，沿用整组到期（对照，行为不变）。
                int e = a.Enemy();
                a.K.ApplyStatus(e, fire, 1.0f, 1f, 0f, 0f, 0);
                a.Run(0.5f);
                a.K.ApplyStatus(e, ob, 3.0f, 0f, 0f, 0f, 0);
                a.Run(0.75f);
                Expect(a.Has(e, "Fire") && a.Has(e, other), "对照：没配置状态位效果表的内核沿用整组到期（燃烧跟着续到 3.5 秒），正式地点都配置了效果表");
            }
            using (var a = new Arena(true))
            {
                // 审查修复：区域减速位（连网 / 纯减速区域）与减速标签叠加时，减速标签先到期后减速降回区域自己的值，不残留标签的数值。
                int e = a.Enemy();
                float tagSlow = StatusTagCatalog.TryGet("Slow", out StatusTag stt) ? stt.Amount : 0f;
                const float zoneSlow = 0.2f;
                a.K.ApplyStatus(e, CombatConst.StatusBitZoneSlow, 3.0f, 0f, zoneSlow, 0f, 0);
                a.K.ApplyStatus(e, NamedReactionCatalog.BitOf("Slow"), 1.0f, 0f, tagSlow, 0f, 0);
                a.K.TryGetStatus(e, out _, out _, out _, out float s0, out _);
                a.Run(1.25f);
                a.K.TryGetStatus(e, out uint m1, out _, out _, out float s1, out _);
                a.Run(2.0f);
                a.K.TryGetStatus(e, out uint m2, out _, out _, out float s2, out _);
                Expect(tagSlow > zoneSlow && Math.Abs(s0 - tagSlow) < 1e-4f && (m1 & CombatConst.StatusBitZoneSlow) != 0u && !a.Has(e, "Slow") && Math.Abs(s1 - zoneSlow) < 1e-4f
                       && m2 == 0u && s2 == 0f,
                    $"区域减速 {zoneSlow} + 减速标签 {tagSlow}（1 秒）：开始取大 {s0:0.00}；减速标签到期后降回区域值 {s1:0.00}（不残留 {tagSlow}）；区域减速位 3 秒到期后归零 {s2:0.00}");
            }
        }

        private static void CheckKernelSnapshotFormat5()
        {
            Line("  · A2. 内核快照格式 6：逐位到期与区域减速值往返一致、续跑一致；格式 5 / 4 旧快照照样读；某位晚于整组到期的坏值拒绝且内核不变");
            string other = TagWithoutReaction("Fire");
            uint fire = NamedReactionCatalog.BitOf("Fire");
            uint ob = NamedReactionCatalog.BitOf(other);
            using (var a = new Arena(true))
            using (var b = new Arena(true))
            using (var c = new Arena(true))
            {
                int e = a.Enemy();
                a.K.ApplyStatus(e, fire, 1.0f, 2f, 0f, 0f, 0);
                a.Run(0.5f);
                a.K.ApplyStatus(e, ob, 3.0f, 0f, 0f, 0f, 0);
                byte[] snap = a.K.Serialize();
                CombatLoadResult r = b.K.Load(snap);
                bool same = a.K.StateHash() == b.K.StateHash() && CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion == 6;
                a.Run(1.0f);
                b.Run(1.0f);
                bool cont = a.K.StateHash() == b.K.StateHash() && !b.Has(e, "Fire") && b.Has(e, other);
                Expect(r == CombatLoadResult.Ok && same && cont, $"格式 {CombatConst.FormatVersion} 往返：哈希一致、续跑 1 游戏秒后两边都是“燃烧已到期、{other} 还在”（{r}）");

                byte[] v4 = a.K.SerializeFormatForTests(4);
                using (var d = new Arena(true))
                {
                    // 在 a 续跑前的状态上取格式 4：重新造一次同样的状态。
                    int e2 = d.Enemy();
                    d.K.ApplyStatus(e2, fire, 1.0f, 2f, 0f, 0f, 0);
                    d.Run(0.5f);
                    d.K.ApplyStatus(e2, ob, 3.0f, 0f, 0f, 0f, 0);
                    byte[] old = d.K.SerializeFormatForTests(4);
                    CombatLoadResult r4 = c.K.Load(old);
                    c.Run(1.0f);
                    Expect(r4 == CombatLoadResult.Ok && c.Has(e2, "Fire") && c.Has(e2, other),
                        $"格式 4 旧快照照样读（{r4}）：没有逐位到期，各位 = 整组到期，燃烧跟着留到整组到期（与旧版一致）");
                }
                _ = v4;

                // 格式 6：区域减速值往返；格式 5 旧快照按旧算法（有区域减速位时取整组减速值）照样读。
                using (var z = new Arena(true))
                using (var z6 = new Arena(true))
                using (var z5 = new Arena(true))
                {
                    int ez = z.Enemy();
                    z.K.ApplyStatus(ez, CombatConst.StatusBitZoneSlow, 3.0f, 0f, 0.2f, 0f, 0);
                    z.K.ApplyStatus(ez, NamedReactionCatalog.BitOf("Slow"), 1.0f, 0f, 0.4f, 0f, 0);
                    CombatLoadResult l6 = z6.K.Load(z.K.Serialize());
                    CombatLoadResult l5 = z5.K.Load(z.K.SerializeFormatForTests(5));
                    bool h6 = z6.K.StateHash() == z.K.StateHash();
                    z.Run(1.25f);
                    z6.Run(1.25f);
                    z5.Run(1.25f);
                    z6.K.TryGetStatus(ez, out _, out _, out _, out float s6, out _);
                    z5.K.TryGetStatus(ez, out uint m5, out _, out _, out float s5, out _);
                    Expect(l6 == CombatLoadResult.Ok && h6 && z6.K.StateHash() == z.K.StateHash() && Math.Abs(s6 - 0.2f) < 1e-4f
                           && l5 == CombatLoadResult.Ok && (m5 & CombatConst.StatusBitZoneSlow) != 0u && s5 >= 0.2f - 1e-4f,
                        $"格式 6 区域减速值往返：哈希一致、续跑后减速标签到期降回 {s6:0.00}；格式 5 旧快照照样读（{l5}，区域减速值取整组减速 {s5:0.00}）");
                }

                byte[] bad = CorruptBitUntil(b.K);
                ulong before = c.K.StateHash();
                CombatLoadResult rb = c.K.Load(bad);
                Expect(rb == CombatLoadResult.InvalidValue && c.K.StateHash() == before, $"某一位的到期晚于整组到期的快照被拒绝（{rb}），内核保持原样");
            }
        }

        /// <summary>造一份坏快照：单位的一个状态位到期写成比整组到期还晚（用格式 5 写出后改字节不可行——位置不定；这里直接挂一个标签后把整组到期缩短再序列化）。</summary>
        private static byte[] CorruptBitUntil(CombatKernel k)
        {
            using (var a = new Arena(true))
            {
                int e = a.Enemy();
                a.K.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 2.0f, 1f, 0f, 0f, 0);
                byte[] good = a.K.Serialize();
                // 整组到期（double）紧跟在状态掩码（uint）之后；位到期在叠层（ulong）之后。找到“掩码 + 整组到期”并把整组到期改成 0.5。
                uint mask = NamedReactionCatalog.BitOf("Fire");
                double until = a.K.Time + 2.0;
                byte[] pattern = BitConverter.GetBytes(mask).Concat(BitConverter.GetBytes(until)).ToArray();
                int at = IndexOf(good, pattern);
                if (at < 0)
                {
                    Fail("测试准备：快照里没找到状态掩码 + 整组到期");
                    return good;
                }
                Buffer.BlockCopy(BitConverter.GetBytes(0.5), 0, good, at + 4, 8);
                int body = good.Length - 4;
                uint h = 2166136261u;
                for (int i = 0; i < body; i++)
                {
                    h ^= good[i];
                    h *= 16777619u;
                }
                good[body] = (byte)h;
                good[body + 1] = (byte)(h >> 8);
                good[body + 2] = (byte)(h >> 16);
                good[body + 3] = (byte)(h >> 24);
                _ = k;
                return good;
            }
        }

        private static void CheckKernelFeed()
        {
            Line("  · A3. 反馈进给：每条反应最后一次的位置 / 出手者 / 目标；敌方身上的反应额外伤害 = 反应伤害；敌方受到的全部伤害精确累计；读档 / 重新登记规则时纪元变化");
            NewState(9402);
            CombatWeapon wet = WeaponFor("fw_coolant");
            CombatWeapon shock = WeaponFor("fw_arcchain");
            using (var a = new Arena(true))
            {
                int mw = a.Machine(wet);
                int ms = a.Machine(shock);
                // 血量用 1000（1e6 的 float 精度只有 0.06，比对不了精确伤害）。
                CombatSpawn sp = Arena.HostileSpawn(new double2(3.0, 0.5));
                sp.Health = 1000f;
                sp.MaxHealth = 1000f;
                int e = a.K.Spawn(sp);
                a.K.TryGetUnit(e, out CombatUnitView v0);
                CombatFireResult f1 = a.Fire(mw, e);
                CombatFireResult f2 = a.Fire(ms, e);
                a.K.TryGetUnit(e, out CombatUnitView v1);
                int ci = RuleIndex("reaction_conduct");
                double2 pos = a.K.ReactionLastPosOf(ci);
                bool who = a.K.ReactionLastSourceOf(ci) == ms && a.K.ReactionLastTargetOf(ci) == e && math.distance(pos, new double2(3.0, 0.5)) < 1e-6;
                // 敌方身上的反应伤害按实际落地算（易伤照样乘），所以 ≥ 反应的原始额外伤害。
                double hostile = a.K.ReactionHostileDamageOf(ci);
                float raw = a.K.ReactionDamageOf(ci);
                double dealt = a.K.DamageDealtToHostile;
                bool dmg = a.K.ReactionCountOf(ci) == 1 && hostile > 0f && hostile >= raw - 1e-3f;
                bool total = Math.Abs(dealt - (v0.Health - v1.Health)) < 1e-2;
                int epoch0 = a.K.FeedEpoch;
                a.K.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
                int epoch1 = a.K.FeedEpoch;
                a.K.Load(a.K.Serialize());
                int epoch2 = a.K.FeedEpoch;
                Expect(who && dmg && total && epoch1 == epoch0 + 1 && epoch2 == epoch1 + 1,
                    $"短路：最后一次触发者 = 电弧链机器、目标 = 敌人、位置 = 敌人位置；敌方反应伤害 {hostile:0.00}（原始额外伤害 {raw:0.00}，易伤照乘）；敌方受到全部伤害 {dealt:0.00} = 实际掉血 {v0.Health - v1.Health:0.00}；纪元 {epoch0}→{epoch1}→{epoch2}（开火 {f1} / {f2}）");
            }
        }

        // ── B. FGT-FW-005：首次触发 ───────────────────────────────────────────

        private static void CheckFirstTrigger()
        {
            Line("  · B. FGT-FW-005：第一次触发解锁图鉴、慢放只发生一次、镜头推动；关掉设置后不慢放不推镜头（仍解锁）；不观察的地点不慢放；未开放命名的反应不算首次");
            CampaignState s = NewState(9403, unlockAll: true);
            ResetFeedbackCounters();
            FeedbackCues.ObservedSiteProvider = () => SiteId;
            int slow0 = GameClock.SlowMotionStarts;
            int nudge0 = CameraNudge.Starts;
            using (var site = NewSite())
            {
                (int mw, int ms) = SiteMachines(site);
                int e1 = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                int e2 = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 2)));
                bool lockedBefore = !ReactionFeedback.IsFirstTriggered(s, "reaction_conduct");
                FirePair(site, mw, ms, e1);
                StepSite(site, 1);
                bool unlocked = ReactionFeedback.IsFirstTriggered(s, "reaction_conduct");
                ReactionFirstTriggerRecord rec = ReactionFeedback.FirstRecordOf(s, "reaction_conduct");
                bool slowOnce = GameClock.SlowMotionStarts == slow0 + 1 && Math.Abs(GameClock.SlowMotionSecondsLeft - ReactionFeedback.SlowMotionSeconds) < 1e-4f
                                && Mathf.Approximately(GameClock.SlowMotionFactor, ReactionFeedback.SlowMotionFactor);
                bool nudged = CameraNudge.Starts == nudge0 + 1 && CameraNudge.Active;
                bool firstPopup = ReactionPopups.Active.Any(p => p.First && p.Key == "reaction:reaction_conduct" && p.Text.Contains("短路"));
                GameClock.CancelSlowMotion();
                StepSite(site, 90); // 等武器冷却
                FirePair(site, mw, ms, e2);
                StepSite(site, 1);
                bool noSecond = GameClock.SlowMotionStarts == slow0 + 1 && GameClock.SlowMotionSecondsLeft == 0f && CameraNudge.Starts == nudge0 + 1
                                && s.ReactionFirstTriggers.Count(r => r.ReactionId == "reaction_conduct") == 1;
                bool secondPopup = ReactionPopups.Active.Any(p => !p.First && p.Key == "reaction:reaction_conduct");
                Expect(lockedBefore && unlocked && rec != null && rec.SiteId == SiteId && rec.Tick == GameClock.Ticks && slowOnce && nudged && firstPopup,
                    $"第一次短路：首次触发记录写入（图鉴解锁，地点 {rec?.SiteId}），慢放 {ReactionFeedback.SlowMotionSeconds} 秒 × {ReactionFeedback.SlowMotionFactor} 开始、镜头轻推开始，弹字“{ReactionPopups.Active.FirstOrDefault(p => p.First)?.Text}”");
                Expect(noSecond && secondPopup, "第二次短路：不再慢放、不再推镜头、首次记录仍只有一条；弹字照常（非首次样式）");
            }

            // 关掉慢放与镜头推动：首次触发仍解锁图鉴，但不慢放、不推镜头。
            s = NewState(9404, unlockAll: true);
            GameSettings.SetReactionSlowMotionEnabled(false);
            GameSettings.SetReactionCameraNudgeEnabled(false);
            slow0 = GameClock.SlowMotionStarts;
            nudge0 = CameraNudge.Starts;
            using (var site = NewSite())
            {
                (int mw, int ms) = SiteMachines(site);
                int e = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                FirePair(site, mw, ms, e);
                StepSite(site, 1);
                Expect(ReactionFeedback.IsFirstTriggered(s, "reaction_conduct") && GameClock.SlowMotionStarts == slow0 && GameClock.SlowMotionSecondsLeft == 0f && CameraNudge.Starts == nudge0,
                    "设置关闭“首次反应慢放 / 镜头推动”：第一次短路照样写首次记录（图鉴解锁），不慢放、不推镜头");
            }
            GameSettings.ResetReactionFeedback();

            // 慢放进行中把设置关掉：立即恢复原速。
            GameClock.BeginSlowMotion(0.3f, 0.25f);
            GameSettings.SetReactionSlowMotionEnabled(false);
            bool cancelled = GameClock.SlowMotionSecondsLeft == 0f && GameClock.SlowMotionFactor == 1f;
            GameSettings.SetReactionSlowMotionEnabled(true);
            Expect(cancelled, "慢放途中关掉设置：慢放立即结束（倍率回到 1）");

            // 不观察的地点：只记录，不慢放、不弹字。
            s = NewState(9405, unlockAll: true);
            FeedbackCues.ObservedSiteProvider = () => "somewhere-else";
            ReactionPopups.ResetForTests();
            slow0 = GameClock.SlowMotionStarts;
            using (var site = NewSite())
            {
                (int mw, int ms) = SiteMachines(site);
                int e = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                FirePair(site, mw, ms, e);
                StepSite(site, 1);
                Expect(ReactionFeedback.IsFirstTriggered(s, "reaction_conduct") && GameClock.SlowMotionStarts == slow0 && ReactionPopups.Active.Count == 0 && ReactionPopups.PushedCount == 0,
                    "镜头不在这个地点：首次触发照样记录（后台结果与观察一致），不慢放、不弹字（弹字只给“同一屏幕”）");
            }

            // 未开放命名的反应（化工批次的爆燃）：照样触发、记日志与归因，但不算首次、不弹字；开放后再触发才算首次。
            s = NewState(9406, unlockAll: true);
            FeedbackCues.ObservedSiteProvider = () => SiteId;
            ReactionPopups.ResetForTests();
            using (var site = NewSite())
            {
                int e = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                StepSite(site, 1);
                float fireDps = StatusTagCatalog.TryGet("Fire", out StatusTag ft) ? ft.Amount : 0f;
                site.Kernel.ApplyStatus(e, NamedReactionCatalog.BitOf("Oil"), 5f, 0f, 0f, 0f, 0);
                site.Kernel.ApplyStatus(e, NamedReactionCatalog.BitOf("Fire"), 3f, fireDps, 0f, 0f, 0);
                StepSite(site, 1);
                bool notFirst = !ReactionFeedback.IsFirstTriggered(s, "reaction_deflagrate") && ReactionPopups.Active.Count == 0;
                ReactionLogEntry entry = ReactionLog.Filtered(ReactionLogFilter.All, s).FirstOrDefault(x => x.ReactionId == "reaction_deflagrate");
                bool logged = entry != null && !entry.Named && ReactionLog.Describe(s, entry).Contains(GameText.Get("reaction.unnamed")) && !ReactionLog.Describe(s, entry).Contains("爆燃");
                NamedReactionCatalog.OpenBatch(s, "clarity");
                int e2 = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 3)));
                StepSite(site, 1);
                site.Kernel.ApplyStatus(e2, NamedReactionCatalog.BitOf("Oil"), 5f, 0f, 0f, 0f, 0);
                site.Kernel.ApplyStatus(e2, NamedReactionCatalog.BitOf("Fire"), 3f, fireDps, 0f, 0f, 0);
                StepSite(site, 1);
                Expect(notFirst && logged && ReactionFeedback.IsFirstTriggered(s, "reaction_deflagrate"),
                    "化工未开放时的爆燃：照样触发并进日志（显示“未知反应”，不漏名字），不算首次、不弹字；开放化工后再触发 → 首次记录写入");
            }

            // 装配反应（标记跳转）：内核发动事件经地点交给反馈——同样写首次记录、进日志。
            s = NewState(9407, unlockAll: true);
            using (var site = NewSite())
            {
                (int mw, _) = SiteMachines(site);
                int e = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                ReactionFeedback.OnAssemblyReaction(site, s, MechanicalReactionCatalog.ReactionMarkJumpId, mw, e, new Vector2(3, 0));
                Expect(ReactionFeedback.IsFirstTriggered(s, MechanicalReactionCatalog.ReactionMarkJumpId)
                       && ReactionLog.Filtered(ReactionLogFilter.All, s).Any(x => x.ReactionId == MechanicalReactionCatalog.ReactionMarkJumpId && x.First),
                    "装配反应（标记跳转）发动：写首次记录、进日志（标“首次”）");
            }
        }

        private static void CheckFirstTriggerSaveLoad()
        {
            Line("  · B2. 首次触发记录真实存读档：读档后同一反应不再慢放；旧档没有这个字段 = 全部未解锁");
            CampaignState s = NewState(9408, unlockAll: true);
            FeedbackCues.ObservedSiteProvider = () => SiteId;
            using (var site = NewSite())
            {
                (int mw, int ms) = SiteMachines(site);
                int e = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                FirePair(site, mw, ms, e);
                StepSite(site, 1);
            }
            GameClock.CancelSlowMotion();
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            CampaignState l = loaded.State;
            bool kept = saved.Success && loaded.Success && ReactionFeedback.IsFirstTriggered(l, "reaction_conduct")
                        && ReactionFeedback.FirstRecordOf(l, "reaction_conduct").Tick == ReactionFeedback.FirstRecordOf(s, "reaction_conduct").Tick;
            CampaignSession.Set(Slot, l);
            int slow0 = GameClock.SlowMotionStarts;
            using (var site = NewSite())
            {
                (int mw, int ms) = SiteMachines(site);
                int e = site.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                FirePair(site, mw, ms, e);
                StepSite(site, 1);
            }
            string json = JsonUtility.ToJson(s);
            CampaignState legacy = JsonUtility.FromJson<CampaignState>(SaveMigrationJson.RemoveField(json, "ReactionFirstTriggers"));
            CampaignFgStateDomains.EnsureAll(legacy);
            Expect(kept && GameClock.SlowMotionStarts == slow0 && legacy != null && legacy.ReactionFirstTriggers != null && legacy.ReactionFirstTriggers.Length == 0
                   && !ReactionFeedback.IsFirstTriggered(legacy, "reaction_conduct"),
                "首次记录真实存档 → 读档：短路仍解锁（第几步一致），读档后再打出短路不再慢放；旧档没有字段：全部未解锁");
        }

        // ── C. 慢放 × 暂停 × 倍速 ─────────────────────────────────────────────

        private static void CheckSlowMotionClock()
        {
            Line("  · C. 慢放 × 暂停 × 0.5x～3x：慢放期间每帧步数 = 倍速 × 慢放倍率 × 真实秒 × 步频；暂停时慢放不流逝；慢放只改节奏不改结果（哈希一致）");
            var bad = new List<string>();
            foreach (float speed in GameClock.Speeds)
            {
                GameClock.ResetSession();
                GameClock.SetSpeed(speed);
                GameClock.BeginSlowMotion(0.3f, 0.25f);
                int steps = 0;
                for (int i = 0; i < 30; i++)
                {
                    steps += GameClock.Advance(0.01f); // 0.3 真实秒，全程慢放
                }
                int slowSteps = steps;
                for (int i = 0; i < 30; i++)
                {
                    steps += GameClock.Advance(0.01f); // 再 0.3 真实秒，已恢复
                }
                int expectSlow = (int)Math.Floor(speed * 0.25 * 0.3 * GameClock.StepHz + 1e-6);
                int expectTotal = (int)Math.Floor(speed * (0.25 * 0.3 + 0.3) * GameClock.StepHz + 1e-6);
                if (Math.Abs(slowSteps - expectSlow) > 1 || Math.Abs(steps - expectTotal) > 1 || GameClock.SlowMotionSecondsLeft != 0f)
                {
                    bad.Add($"{speed}x：慢放段 {slowSteps} 步（应 {expectSlow}）、合计 {steps}（应 {expectTotal}）");
                }
            }
            GameClock.ResetSession();
            GameClock.BeginSlowMotion(0.3f, 0.25f);
            GameClock.SetPaused(true);
            int pausedSteps = 0;
            for (int i = 0; i < 100; i++)
            {
                pausedSteps += GameClock.Advance(0.02f);
            }
            float leftWhilePaused = GameClock.SlowMotionSecondsLeft;
            GameClock.SetPaused(false);
            GameClock.Advance(0.1f);
            float leftAfter = GameClock.SlowMotionSecondsLeft;
            GameClock.SetDirectLocked(true);
            GameClock.SetSpeed(3f);
            int lockedSteps = 0;
            for (int i = 0; i < 10; i++)
            {
                lockedSteps += GameClock.Advance(0.01f);
            }
            GameClock.SetDirectLocked(false);
            GameClock.ResetSession();
            Expect(bad.Count == 0, $"0.5x / 1x / 2x / 3x：慢放段与恢复段的步数都按倍速 × 慢放倍率成比例{Detail(bad)}");
            Expect(pausedSteps == 0 && Math.Abs(leftWhilePaused - 0.3f) < 1e-5f && Math.Abs(leftAfter - 0.2f) < 1e-4f && lockedSteps <= 2,
                $"暂停 2 真实秒：0 步、慢放剩余不变（{leftWhilePaused:0.000}）；继续后接着慢完（剩 {leftAfter:0.000}）；接入锁 1x 时慢放照样生效（0.1 秒 {lockedSteps} 步）");

            // 慢放不改变结果：同一存档，一边在中途慢放、一边不慢放，推进到同一步，地点哈希一致。
            BuildCityWorld(9409, observeCity: true);
            RestoreWorld(observeCity: true);
            long target = GameClock.Ticks + 240;
            RunTo(target, slowmoAt: -1);
            ulong plain = SiteHash();
            RestoreWorld(observeCity: true);
            RunTo(target, slowmoAt: GameClock.Ticks + 30);
            ulong slowed = SiteHash();
            Expect(plain != 0 && plain == slowed, $"慢放只改节奏：推进到同一步，中途慢放与不慢放的破碎都市内核哈希一致（{plain:X16}）");
        }

        // ── D. 弹字：聚合与每秒上限 ───────────────────────────────────────────

        private static void CheckPopupThrottle()
        {
            Line("  · D1. 弹字：同一帧同一反应 200 次 → 一条“×200”；多种反应同一帧 → 1 秒内新开不超过上限，其余攒着稍后放完、次数不丢；关掉设置不弹；首次触发单独一条");
            ReactionPopups.ResetForTests();
            _fakeNow = 100f;
            for (int i = 0; i < 200; i++)
            {
                ReactionPopups.Push("reaction:reaction_conduct", "短路", 1, Vector3.zero, false, false, _fakeNow);
            }
            bool one = ReactionPopups.Active.Count == 1 && ReactionPopups.Active[0].Count == 200 && ReactionPopups.Active[0].Text == "短路 ×200";
            Expect(one && ReactionPopups.SpawnedCount == 1, $"同一帧 200 次短路：一条弹字“{ReactionPopups.Active.FirstOrDefault()?.Text}”（聚合），只新开 1 条");

            ReactionPopups.ResetForTests();
            int cap = ReactionPopups.PerSecondCap;
            for (int k = 0; k < 10; k++)
            {
                for (int i = 0; i < 20; i++)
                {
                    ReactionPopups.Push("reaction:r" + k, "反应" + k, 1, Vector3.zero, false, false, _fakeNow);
                }
            }
            bool capped = ReactionPopups.Active.Count == cap && ReactionPopups.PendingCount == 10 - cap && ReactionPopups.VisibleAndPendingCount == 200;
            var shownKeys = new HashSet<string>(ReactionPopups.Active.Select(p => p.Key));
            for (int f = 1; f <= 60; f++)
            {
                _fakeNow = 100f + f * 0.05f;
                ReactionPopups.Tick(_fakeNow);
                foreach (ReactionPopups.Popup p in ReactionPopups.Active)
                {
                    shownKeys.Add(p.Key);
                }
            }
            Expect(cap == 6 && capped && shownKeys.Count == 10 && ReactionPopups.PendingCount == 0 && ReactionPopups.PeakPerSecond <= cap,
                $"同一帧 10 种反应各 20 次：当帧新开 {cap} 条（上限 reaction.popup_per_second = {cap}），其余 {10 - cap} 种攒着、200 次一次不丢；3 秒内全部放出，任意 1 秒新开最多 {ReactionPopups.PeakPerSecond} 条");

            ReactionPopups.ResetForTests();
            GameSettings.SetReactionPopupsEnabled(false);
            bool refused = !ReactionPopups.Push("reaction:x", "x", 5, Vector3.zero, false, false, _fakeNow) && ReactionPopups.Active.Count == 0 && ReactionPopups.DroppedBySetting == 5;
            GameSettings.SetReactionPopupsEnabled(true);
            ReactionPopups.Push("reaction:reaction_conduct", "短路", 1, Vector3.zero, false, false, _fakeNow);
            ReactionPopups.Push("reaction:reaction_conduct", "短路", 1, Vector3.zero, true, false, _fakeNow);
            ReactionPopups.Push("reading:0", GameText.Get("reading.popup.zone"), 3, Vector3.zero, false, true, _fakeNow);
            bool separate = ReactionPopups.Active.Count == 3 && ReactionPopups.Active.Count(p => p.First) == 1 && ReactionPopups.Active.First(p => p.First).Text == "新反应：短路！"
                            && ReactionPopups.Active.Any(p => p.Reading && p.Text == "区域展开 ×3");
            _fakeNow += 10f;
            ReactionPopups.Tick(_fakeNow);
            Expect(refused && separate && ReactionPopups.Active.Count == 0,
                "设置关闭“反应弹字”：不进队列（记为被设置丢弃）；首次触发单独一条“新反应：短路！”，读法弹字“区域展开 ×3”；过了停留时间全部消失");
        }

        private static void CheckSameFrame200()
        {
            Line("  · D2. 负向：真实内核同一帧触发 200 次反应（提示事件超每步上限被丢）——计数可靠：一条弹字“短路 ×200”、一条日志 ×200、归因 200 次、声音只出一次、热更层这一步只处理有上限的工作");
            CampaignState s = NewState(9410, unlockAll: true);
            FeedbackCues.ObservedSiteProvider = () => SiteId;
            ReactionPopups.ResetForTests();
            ReactionFeedback.RecordFirst(s, "reaction_conduct", SiteId); // 不是第一次：只看聚合
            s.RegionRecords = new[] { new RegionRecord { RegionId = SiteId, ExpeditionCount = 1 } };
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using (var site = new CombatSite(SiteId, cfg))
            {
                CombatKernel k = site.Kernel;
                int iw = k.AddWeapon(WeaponFor("fw_coolant"));
                int isk = k.AddWeapon(WeaponFor("fw_arcchain"));
                var enemies = new List<int>();
                var shooters = new List<int>();
                for (int i = 0; i < 200; i++)
                {
                    // 每对相隔 12 米（电弧链的连锁跳不到别的对），机器贴着自己的目标。
                    var at = new double2((i % 20) * 12.0, (i / 20) * 12.0);
                    enemies.Add(k.Spawn(Arena.HostileSpawn(at)));
                    shooters.Add(k.Spawn(Arena.MachineSpawn(at + new double2(0, -2), isk)));
                }
                int wetter = k.Spawn(Arena.MachineSpawn(new double2(0, -30), iw));
                foreach (int e in enemies)
                {
                    k.ApplyStatus(e, NamedReactionCatalog.BitOf("Wet"), 10f, 0f, 0f, 0f, wetter);
                }
                StepSite(site, 1); // 基线
                long drop0 = k.Counters.CuesDropped;
                int raise0 = FeedbackCues.CountOf(FeedbackCueId.TagReaction);
                for (int i = 0; i < 200; i++)
                {
                    k.FireAt(shooters[i], enemies[i], k.Time);
                }
                long seen0 = ReactionFeedback.ReactionsSeen;
                int ci = RuleIndex("reaction_conduct");
                int kernelFired = k.ReactionCountOf(ci);
                site.Step(1f / 60f, k.Time + 1f / 60f);
                ReactionLogEntry entry = ReactionLog.Filtered(ReactionLogFilter.All, s).FirstOrDefault(x => x.ReactionId == "reaction_conduct");
                ReactionSessionRecord sess = ReactionAttribution.Current(s, SiteId, create: false);
                ReactionShareRecord row = sess?.Reactions.FirstOrDefault(r => r.ReactionId == "reaction_conduct");
                int n = k.ReactionCountOf(ci);
                bool popup = ReactionPopups.Active.Count == 1 && ReactionPopups.Active[0].Count == n && ReactionPopups.Active[0].Text == "短路 ×" + n;
                Expect(kernelFired >= 200 && n == kernelFired && ReactionFeedback.ReactionsSeen - seen0 == n && popup && entry != null && entry.Count == n
                       && row != null && row.Count == n && Math.Abs(row.Damage - k.ReactionHostileDamageOf(ci)) < 1e-2 && FeedbackCues.CountOf(FeedbackCueId.TagReaction) - raise0 == 1,
                    $"同一帧 200 次短路（这一帧提示事件丢弃 {k.Counters.CuesDropped - drop0} 条）：内核计数 {k.ReactionCountOf(ci)}、弹字“{ReactionPopups.Active.FirstOrDefault()?.Text}”、日志一条 ×{entry?.Count}、" +
                    $"归因 {row?.Count} 次 / 伤害 {row?.Damage:0.0}（= 内核）、反应声音 / 字幕这一步只发 {FeedbackCues.CountOf(FeedbackCueId.TagReaction) - raise0} 次（逐次发会是 {n} 次）");
            }
        }

        // ── E. 伤害归因（正式流程）─────────────────────────────────────────────

        private static void CheckWorldAttribution()
        {
            Line("  · E1. 伤害归因（正式流程）：破碎都市两台机器编队攻击打出短路 → 远征场次；占比 = 反应伤害 / 敌方全部伤害（与内核对账）；撤离报告显示占比；撤离后关闭；观察与不观察一致；真实存读档");
            BuildCityWorld(9411, observeCity: false, warmSteps: 1); // 存档时还没打出反应：本段的反应都发生在读档之后（日志只记本次会话）
            RestoreWorld(observeCity: false);
            CampaignState s = CampaignSession.Current;
            CombatSite city = CombatSites.Get(FracturedCityLayout.RegionId);
            int ci = RuleIndex("reaction_conduct");
            double hostile0 = city.Kernel.ReactionHostileDamageOf(ci);
            double total0 = city.Kernel.DamageDealtToHostile;
            ReactionSessionRecord before = ReactionAttribution.Current(s, FracturedCityLayout.RegionId, create: false);
            double sessTotal0 = before?.TotalDamage ?? 0;
            double sessConduct0 = before?.Reactions.FirstOrDefault(r => r.ReactionId == "reaction_conduct")?.Damage ?? 0;
            int c0 = city.Kernel.ReactionCountOf(ci);
            WorldSimulation.StepMany(360);
            int dCount = city.Kernel.ReactionCountOf(ci) - c0;
            ReactionSessionRecord sess = ReactionAttribution.Current(s, FracturedCityLayout.RegionId, create: false);
            ReactionShareRecord row = sess?.Reactions.FirstOrDefault(r => r.ReactionId == "reaction_conduct");
            double dConduct = (row?.Damage ?? 0) - sessConduct0;
            double dTotal = (sess?.TotalDamage ?? 0) - sessTotal0;
            bool reconcile = Math.Abs(dConduct - (city.Kernel.ReactionHostileDamageOf(ci) - hostile0)) < 0.05 && Math.Abs(dTotal - (city.Kernel.DamageDealtToHostile - total0)) < 0.05;
            List<ReactionAttribution.Share> shares = ReactionAttribution.SharesOf(sess);
            ReactionAttribution.Share share = shares.FirstOrDefault(x => x.ReactionId == "reaction_conduct");
            string text = ExpeditionReturnPanelUIToolkit.ReactionShareText(s, FracturedCityLayout.RegionId);
            Expect(sess != null && sess.Kind == ReactionAttribution.KindExpedition && sess.Ordinal == 1 && sess.EndTick < 0 && row != null && dCount >= 1 && row.Count >= dCount && row.Damage > 0 && dConduct > 0
                   && reconcile && share.Fraction > 0 && share.Fraction < 1 && Math.Abs(share.Fraction - row.Damage / sess.TotalDamage) < 1e-9 && text.Contains("短路") && text.Contains("%"),
                $"远征场次“{ReactionAttribution.Title(sess)}”：短路 {row?.Count} 次、伤害 {row?.Damage:0.0} / 敌方全部 {sess?.TotalDamage:0.0}（占比 {ReactionAttribution.Percent(share.Fraction)}%，" +
                $"本段增量与内核对账一致）；撤离报告：“{text}”");

            // 日志：触发者是真实机器（#编号）、参与的固件、目标是侦察机。
            ReactionLogEntry entry = ReactionLog.Filtered(ReactionLogFilter.Expedition, s).FirstOrDefault(x => x.ReactionId == "reaction_conduct");
            string line = entry != null ? ReactionLog.Describe(s, entry) : string.Empty;
            string fwName = FirmwareKinds.TryGetRow("fw_arcchain", out GameConfig.fg.FirmwareKind fk) ? GameText.Get(fk.NameKey) : "?";
            string fwName2 = FirmwareKinds.TryGetRow("fw_coolant", out GameConfig.fg.FirmwareKind fk2) ? GameText.Get(fk2.NameKey) : "?";
            Expect(entry != null && entry.Source.Kind == CombatUnitKind.Machine && entry.Source.LogicId > 0 && entry.Target.Kind == CombatUnitKind.Enemy
                   && entry.FirmwareIds.Length >= 1 && line.Contains("#") && (line.Contains(fwName) || line.Contains(fwName2)) && entry.SessionKind == ReactionAttribution.KindExpedition,
                $"反应日志（远征筛选）：“{line}”——触发者是机器 #编号、参与的固件、目标");

            // 撤离：场次关闭，之后同一次出击的伤害不再开新场。
            ReactionAttribution.CloseExpedition(s, FracturedCityLayout.RegionId);
            long end = sess?.EndTick ?? -99;
            WorldSimulation.StepMany(60);
            int sessions = s.Stats.ReactionSessions.Count(x => x.SiteId == FracturedCityLayout.RegionId);
            Expect(end >= 0 && sessions == 1 && ReactionAttribution.Current(s, FracturedCityLayout.RegionId, create: false) == null,
                $"撤离结算：远征场次关闭（第 {end} 步），区域卸载前继续的战斗不再为同一次出击开新场（{sessions} 场）");

            // 真实存读档：归因进存档。
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            string a = JsonUtility.ToJson(s.Stats);
            string b = loaded.Success ? JsonUtility.ToJson(loaded.State.Stats) : "";
            Expect(saved.Success && loaded.Success && a == b && loaded.State.Stats.ReactionSessions.Length == s.Stats.ReactionSessions.Length,
                $"伤害归因真实存档 → 读档逐字段一致（{s.Stats.ReactionSessions.Length} 场）");

            // 观察与不观察一致（FGR-BASE-021）：同一存档分别观察破碎都市 / 观察家园，推进到同一步，首次记录与归因逐字段一致、内核哈希一致。
            RestoreWorld(observeCity: true);
            long target = GameClock.Ticks + 180;
            RunTo(target, slowmoAt: -1);
            string obsStats = JsonUtility.ToJson(CampaignSession.Current.Stats) + JsonUtility.ToJson(new FirstWrap { Items = CampaignSession.Current.ReactionFirstTriggers });
            ulong obsHash = SiteHash();
            RestoreWorld(observeCity: false);
            WorldSimulation.StepMany((int)(target - GameClock.Ticks));
            string bgStats = JsonUtility.ToJson(CampaignSession.Current.Stats) + JsonUtility.ToJson(new FirstWrap { Items = CampaignSession.Current.ReactionFirstTriggers });
            Expect(obsStats == bgStats && obsHash == SiteHash() && CampaignSession.Current.ReactionFirstTriggers.Any(r => r.ReactionId == "reaction_conduct"),
                "观察破碎都市（有慢放 / 弹字）与不观察（无头推进）推进到同一步：首次记录与伤害归因逐字段一致、内核哈希一致");
        }

        [Serializable]
        private sealed class FirstWrap
        {
            public ReactionFirstTriggerRecord[] Items;
        }

        private static void CheckRaidAndCounterReactions()
        {
            Line("  · E2. 突袭场次与筛选、克制类只记次数不记伤害、场次上限");
            CampaignState s = NewState(9412, unlockAll: true);
            FeedbackCues.ObservedSiteProvider = () => HomeValleyLayout.RegionId;
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            using (var home = new CombatSite(HomeValleyLayout.RegionId, cfg))
            {
                (int mw, int ms) = SiteMachines(home);
                int e1 = home.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 0)));
                FirePair(home, mw, ms, e1);
                StepSite(home, 1);
                bool noSessionOutsideRaid = s.Stats.ReactionSessions.Length == 0;
                ReactionSessionRecord raid = ReactionAttribution.BeginRaid(s, "transit-7");
                ReactionSessionRecord again = ReactionAttribution.BeginRaid(s, "transit-8");
                int e2 = home.Kernel.Spawn(Arena.HostileSpawn(new double2(3, 2)));
                StepSite(home, 90); // 等武器冷却
                FirePair(home, mw, ms, e2);
                StepSite(home, 1);
                bool raidRow = raid != null && ReferenceEquals(raid, again) && raid.Kind == ReactionAttribution.KindRaid && raid.Reactions.Any(r => r.ReactionId == "reaction_conduct" && r.Count >= 1)
                               && raid.TotalDamage > 0;
                List<ReactionLogEntry> raids = ReactionLog.Filtered(ReactionLogFilter.Raid, s);
                List<ReactionLogEntry> exps = ReactionLog.Filtered(ReactionLogFilter.Expedition, s);
                List<ReactionLogEntry> all = ReactionLog.Filtered(ReactionLogFilter.All, s);
                bool filters = raids.Count >= 1 && raids.All(x => x.SessionKind == ReactionAttribution.KindRaid) && exps.Count == 0 && all.Count > raids.Count
                               && all.Any(x => x.SessionKind == null);
                bool ended = ReactionAttribution.EndRaid(s) && !ReactionAttribution.EndRaid(s) && raid.EndTick >= 0;
                Expect(noSessionOutsideRaid && raidRow && filters && ended,
                    $"家园平时（训练 / 测试）不算场次；突袭开始后同一地点的反应记到突袭场次（重复开始不重开）；日志按“突袭”筛出 {raids.Count} 条、“远征”0 条、“全部”{all.Count} 条；突袭结束后关闭");

                // 克制类（绝缘：电击 + 油污 ×0.6）：让目标少掉血，归因只记次数、伤害 0。
                ReactionAttribution.BeginRaid(s, "transit-9");
                // 远离前面的战斗（冷却液的驻留区域会给附近的单位挂浸湿，那样先结算的是短路），旁边另放一台电弧链机器。
                int e3 = home.Kernel.Spawn(Arena.HostileSpawn(new double2(40, 0)));
                int ms3 = home.Kernel.Spawn(Arena.MachineSpawn(new double2(40, -2), home.Kernel.AddWeapon(WeaponFor("fw_arcchain"))));
                StepSite(home, 1);
                home.Kernel.ApplyStatus(e3, NamedReactionCatalog.BitOf("Oil"), 5f, 0f, 0f, 0f, 0);
                bool oilBefore = home.Kernel.TryGetStatus(e3, out uint m0, out _, out _, out _, out _) && (m0 & NamedReactionCatalog.BitOf("Oil")) != 0u;
                int insK0 = home.Kernel.ReactionCountOf(RuleIndex("reaction_insulate"));
                home.Kernel.TryGetUnit(e3, out CombatUnitView hp0);
                CombatFireResult fr = home.Kernel.FireAt(ms3, e3, home.Kernel.Time);
                home.Kernel.TryGetUnit(e3, out CombatUnitView hp1);
                home.Kernel.TryGetStatus(e3, out uint m1, out _, out _, out _, out _);
                int insK1 = home.Kernel.ReactionCountOf(RuleIndex("reaction_insulate"));
                home.ProcessEvents();
                StepSite(home, 1);
                ReactionSessionRecord r2 = ReactionAttribution.Current(s, HomeValleyLayout.RegionId, create: false);
                ReactionShareRecord ins = r2?.Reactions.FirstOrDefault(r => r.ReactionId == "reaction_insulate");
                Expect(home.Kernel.ReactionCountOf(RuleIndex("reaction_insulate")) >= 1 && ins != null && ins.Count >= 1 && ins.Damage == 0,
                    $"克制类绝缘触发 {ins?.Count} 次（开火 {fr}，开火前有油污 {oilBefore}，掉血 {hp0.Health - hp1.Health:0.0}，标签 {m0:X}→{m1:X}，内核计数 {insK0}→{insK1}）：归因记次数、伤害 {ins?.Damage}（少掉的伤害不算反应伤害），占比 0");
            }

            // 场次上限：超出时丢最旧的已结束场次，进行中的不丢。
            s = NewState(9413);
            int cap = ReactionAttribution.SessionCapacity;
            ReactionAttribution.BeginRaid(s, "keep-open", "site-open");
            for (int i = 0; i < cap + 5; i++)
            {
                ReactionAttribution.BeginRaid(s, "r" + i, "site-" + i);
                ReactionAttribution.EndRaid(s, "site-" + i);
            }
            Expect(cap == 20 && s.Stats.ReactionSessions.Length == cap && s.Stats.ReactionSessions.Any(x => x.SiteId == "site-open" && x.EndTick < 0)
                   && s.Stats.ReactionSessions.All(x => x.SiteId != "site-0"),
                $"场次上限 {cap}（reaction.attribution_sessions）：超出时丢最旧的已结束场次，进行中的一场保留");
        }

        // ── F. 反应日志 ────────────────────────────────────────────────────────

        private static void CheckLog()
        {
            Line("  · F. 反应日志：容量 50（最旧的先丢）、最新在前；换战役（新建 / 读档）清空；只保留本次会话（不存档）");
            CampaignState s = NewState(9414);
            ReactionLog.Clear();
            for (int i = 0; i < 60; i++)
            {
                ReactionLog.Add(s, new ReactionLogEntry { Tick = i, ReactionId = "reaction_conduct", Named = true, Count = 1, SiteId = SiteId });
            }
            List<ReactionLogEntry> all = ReactionLog.Filtered(ReactionLogFilter.All, s);
            bool cap = ReactionLog.Capacity == 50 && all.Count == 50 && all[0].Tick == 59 && all[49].Tick == 10;
            CampaignState other = NewState(9415);
            bool hiddenFromOther = ReactionLog.Filtered(ReactionLogFilter.All, other).Count == 0;
            ReactionLog.Add(other, new ReactionLogEntry { Tick = 1, ReactionId = "reaction_conduct", Named = true, Count = 1, SiteId = SiteId });
            bool cleared = ReactionLog.All.Count == 1;
            bool notSaved = !JsonUtility.ToJson(other).Contains("ReactionLog");
            Expect(cap && hiddenFromOther && cleared && notSaved, "日志保留最近 50 条（第 10～59 条，最新在前）；换了战役看不到旧战役的日志、写入新条目时清空；日志不进存档");
        }

        // ── G. 界面 ────────────────────────────────────────────────────────────

        private static void CheckUi()
        {
            Line("  · G. 界面：暂停菜单三个开关（改了立即生效、恢复默认）、反应记录面板三页签与远征 / 突袭筛选、反应图鉴剪影、弹字层画出弹字；布局探针");
            CampaignState s = NewState(9416, unlockAll: true);
            ReactionFeedback.RecordFirst(s, "reaction_conduct", FracturedCityLayout.RegionId);
            s.RegionRecords = new[] { new RegionRecord { RegionId = FracturedCityLayout.RegionId, ExpeditionCount = 2 } };
            ReactionAttribution.AddDamage(s, FracturedCityLayout.RegionId, 100);
            ReactionAttribution.AddReaction(s, FracturedCityLayout.RegionId, "reaction_conduct", 3, 25);
            ReactionAttribution.BeginRaid(s, "transit-1");
            ReactionAttribution.AddDamage(s, HomeValleyLayout.RegionId, 50);
            ReactionAttribution.AddReaction(s, HomeValleyLayout.RegionId, "reaction_thermalshock", 1, 10);
            ReactionLog.Clear();
            ReactionLog.Add(s, new ReactionLogEntry { Tick = 60, ReactionId = "reaction_conduct", Named = true, First = true, Count = 3, SiteId = FracturedCityLayout.RegionId, SessionKind = ReactionAttribution.KindExpedition });
            ReactionLog.Add(s, new ReactionLogEntry { Tick = 120, ReactionId = "reaction_thermalshock", Named = true, Count = 1, SiteId = HomeValleyLayout.RegionId, SessionKind = ReactionAttribution.KindRaid });

            // 暂停菜单：三个开关。
            VisualElement pauseRoot = MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject pgo);
            try
            {
                PauseMenuUIToolkit pause = pgo.AddComponent<PauseMenuUIToolkit>();
                pause.BindView(pauseRoot);
                bool labels = pause.ReactionPopupsToggle?.label == "反应弹字" && pause.ReactionSlowMotionToggle?.label == "首次反应慢放" && pause.ReactionNudgeToggle?.label == "首次反应镜头推动";
                pause.ReactionPopupsToggle.value = false;
                pause.ReactionSlowMotionToggle.value = false;
                pause.ReactionNudgeToggle.value = false;
                bool off = !GameSettings.ReactionPopupsEnabled && !GameSettings.ReactionSlowMotionEnabled && !GameSettings.ReactionCameraNudgeEnabled;
                Click(pause.ReactionResetButton);
                bool reset = GameSettings.ReactionPopupsEnabled && GameSettings.ReactionSlowMotionEnabled && GameSettings.ReactionCameraNudgeEnabled
                             && pause.ReactionPopupsToggle.value && pause.ReactionSlowMotionToggle.value && pause.ReactionNudgeToggle.value;
                Expect(labels && off && reset && pause.ReactionLogButton?.text == "反应记录" && pause.StatsButton?.text == "统计",
                    "暂停菜单“战斗反馈”：反应弹字 / 首次反应慢放 / 首次反应镜头推动三个开关，取消勾选立即写进设置；“恢复默认”全部打开并同步勾选；“反应记录”与“统计”入口");
            }
            finally
            {
                GameSettings.ResetReactionFeedback();
                Object.DestroyImmediate(pgo);
            }

            // 反应记录面板。
            VisualElement root = MountUxml(UiKitFolder + "ReactionLogPanel.uxml", out GameObject go);
            ReactionLogPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ReactionLogPanelUIToolkit panel = go.AddComponent<ReactionLogPanelUIToolkit>();
                panel.BindView(root);
                panel.SetOpen(true);
                bool opened = panel.PanelVisible && panel.CurrentTab == ReactionLogPanelUIToolkit.Tab.Log && panel.VisibleRowCount == 2 && panel.RowText(0).Contains("热震")
                              && panel.RowText(1).Contains("短路 ×3") && panel.RowText(1).Contains("首次") && panel.FilterBarVisible;
                Click(panel.FilterButton(ReactionLogFilter.Raid));
                bool raidOnly = panel.VisibleRowCount == 1 && panel.RowText(0).Contains("热震");
                Click(panel.FilterButton(ReactionLogFilter.Expedition));
                bool expOnly = panel.VisibleRowCount == 1 && panel.RowText(0).Contains("短路");
                Click(panel.TabButton(ReactionLogPanelUIToolkit.Tab.Attribution));
                string attrAll = string.Join("\n", Enumerable.Range(0, panel.VisibleRowCount).Select(panel.RowText));
                bool attr = attrAll.Contains("远征 · ") && attrAll.Contains("第 2 次") && attrAll.Contains("短路 25%（3 次）") && !attrAll.Contains("突袭 · ");
                Click(panel.FilterButton(ReactionLogFilter.Raid));
                string attrRaid = string.Join("\n", Enumerable.Range(0, panel.VisibleRowCount).Select(panel.RowText));
                bool attr2 = attrRaid.Contains("突袭 · ") && attrRaid.Contains("热震 20%（1 次）") && attrRaid.Contains("进行中");
                Click(panel.TabButton(ReactionLogPanelUIToolkit.Tab.Codex));
                string codex = string.Join("\n", Enumerable.Range(0, panel.VisibleRowCount).Select(panel.RowText));
                bool codexOk = !panel.FilterBarVisible && codex.Contains("短路") && codex.Contains("首次触发") && codex.Contains("？？？") && !codex.Contains("热震\n")
                               && codex.Contains("尚未开放命名") && codex.Contains("无法触发") && panel.FooterText.Contains("1 / 18");
                ReactionLog.Clear();
                panel.SelectTab(ReactionLogPanelUIToolkit.Tab.Log);
                panel.SelectFilter(ReactionLogFilter.All);
                bool empty = panel.VisibleRowCount == 0 && panel.EmptyText.Contains("还没有反应记录");
                Click(panel.CloseButton);
                bool closed = !panel.PanelVisible && !ReactionLogPanelUIToolkit.IsOpen;
                Expect(opened && closed && raidOnly && expOnly, $"反应日志页：最新在前两条（“{panel.RowText(0)}”），“突袭”只剩热震、“远征”只剩短路");
                Expect(attr && attr2, $"伤害归因页：远征“{attrAll.Replace("\n", " / ")}”；突袭筛选“{attrRaid.Replace("\n", " / ")}”");
                Expect(codexOk && empty, $"反应图鉴页：打出过的短路显示名字、说明与首次触发；其余“？？？”并写明原因（未打出 / 未开放命名 / 无法触发），脚注“{panel.FooterText}”；日志空时有空状态说明");
            }
            finally
            {
                ReactionLogPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }

            // 统计面板（卡片“伤害归因进入统计面板”）：累计 + 每场明细，按远征 / 突袭筛选，空状态。
            VisualElement statsRoot = MountUxml(UiKitFolder + "StatsPanel.uxml", out GameObject sgo);
            StatsPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                StatsPanelUIToolkit sp = sgo.AddComponent<StatsPanelUIToolkit>();
                sp.BindView(statsRoot);
                StatsPanelUIToolkit.Open(); // 暂停菜单“统计”按钮调的同一个入口
                string all = Rows(sp.VisibleRowCount, sp.RowText);
                // 累计：敌方共 100 + 50 = 150，反应 25 + 10 = 35（23.3%）；短路 25 / 150 = 16.7%，热震 10 / 150 = 6.7%；明细最新在前（突袭先于远征）。
                bool totals = StatsPanelUIToolkit.IsOpen && sp.PanelVisible && sp.SectionText == "战斗 · 反应伤害归因" && sp.RowText(0) == "累计（2 场）"
                              && sp.RowText(1).Contains("敌方共受伤害 150") && sp.RowText(1).Contains("23.3%") && sp.RowText(2).Contains("短路 16.7%（3 次）")
                              && sp.RowText(3).Contains("热震 6.7%（1 次）") && sp.RowText(4) == "每场明细" && sp.RowText(5).StartsWith("突袭 · ", StringComparison.Ordinal)
                              && all.Contains("远征 · ") && all.Contains("第 2 次") && all.Contains("短路 25%（3 次）") && sp.CountText == "2 场" && sp.FooterText.Contains("20")
                              && !GameText.ContainsMarker(all + sp.FooterText);
                Click(sp.FilterButton(ReactionLogFilter.Expedition));
                string exp = Rows(sp.VisibleRowCount, sp.RowText);
                bool expOnly = sp.RowText(0) == "累计（1 场）" && exp.Contains("短路 25%（3 次）") && !exp.Contains("热震") && !exp.Contains("突袭 · ");
                Click(sp.FilterButton(ReactionLogFilter.Raid));
                string raid = Rows(sp.VisibleRowCount, sp.RowText);
                bool raidOnly = sp.RowText(0) == "累计（1 场）" && raid.Contains("热震 20%（1 次）") && !raid.Contains("短路") && raid.Contains("进行中");
                CampaignState keep = CampaignSession.Current;
                CampaignState blank = NewState(9417);
                sp.SelectFilter(ReactionLogFilter.All);
                bool empty = sp.VisibleRowCount == 0 && sp.EmptyText.Contains("还没有远征或突袭的战斗统计") && sp.CountText == "0 场";
                CampaignSession.Set(CampaignSession.ActiveSlotIndex, keep);
                sp.Refresh();
                Click(sp.CloseButton);
                bool closed = !sp.PanelVisible && !StatsPanelUIToolkit.IsOpen;
                Expect(totals && closed, $"统计面板“{sp.SectionText}”：累计（两场按反应合计，占比按两场敌方全部伤害）与每场明细（最新在前）——“{all.Replace("\n", " / ")}”；点关闭收起");
                Expect(expOnly && raidOnly && empty && blank != null,
                    $"统计面板筛选：“远征”只剩破碎都市一场（“{exp.Replace("\n", " / ")}”），“突袭”只剩进行中的突袭（热震 20%）；没有场次的存档显示空状态说明");
            }
            finally
            {
                StatsPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(sgo);
            }

            // 弹字层：画出队列里的弹字。
            VisualElement hudRoot = MountUxml(UiKitFolder + "ReactionPopupHud.uxml", out GameObject hgo);
            ReactionPopupHudUIToolkit.InWorldOverrideForTests = true;
            try
            {
                ReactionPopups.ResetForTests();
                ReactionPopupHudUIToolkit hud = hgo.AddComponent<ReactionPopupHudUIToolkit>();
                hud.BindView(hudRoot);
                hud.Tick(null, _fakeNow); // 同步“当前观察地点”
                ReactionPopups.Push("reaction:reaction_conduct", "短路", 200, Vector3.zero, false, false, _fakeNow);
                ReactionPopups.Push("reaction:reaction_conduct", "短路", 1, Vector3.zero, true, false, _fakeNow);
                hud.Tick(null, _fakeNow);
                bool drawn = hud.VisibleCount == 2 && (hud.VisibleText(0) == "短路 ×200" || hud.VisibleText(1) == "短路 ×200") && hudRoot.Q<VisualElement>("ReactionPopupLayer").pickingMode == PickingMode.Ignore;
                _fakeNow += 10f;
                hud.Tick(null, _fakeNow);
                Expect(drawn && hud.VisibleCount == 0, "弹字层按队列画出“短路 ×200”与首次触发各一条，整层不拦截点击；过了停留时间隐藏");
            }
            finally
            {
                ReactionPopupHudUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(hgo);
            }

            // 布局探针：反应记录面板（中英、缩放极值）。暂停菜单的新增行由 FgUiKitSelfCheck 的暂停菜单探针覆盖。
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                {
                    string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "ReactionLogPanel.uxml", "ReactionLogWindow", stressFill: true, prepare: r =>
                    {
                        var probeGo = new GameObject("__probe_reactionlog") { hideFlags = HideFlags.HideAndDontSave };
                        ReactionLogPanelUIToolkit.InWorldOverrideForTests = true;
                        ReactionLogPanelUIToolkit p = probeGo.AddComponent<ReactionLogPanelUIToolkit>();
                        p.BindView(r.panel.visualTree);
                        p.SelectTab(ReactionLogPanelUIToolkit.Tab.Attribution);
                        Object.DestroyImmediate(probeGo);
                        ReactionLogPanelUIToolkit.InWorldOverrideForTests = false;
                        r.panel.visualTree.Q<VisualElement>("ReactionLogRoot")?.RemoveFromClassList("uk-hidden");
                    }, uiScale: scale);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 ReactionLogPanel.uxml#ReactionLogWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                }
            }
            // 布局探针：统计面板（中英、缩放极值；有累计与明细的数据态）。
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                {
                    string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "StatsPanel.uxml", "StatsPanelWindow", stressFill: true, prepare: r =>
                    {
                        var probeGo = new GameObject("__probe_stats") { hideFlags = HideFlags.HideAndDontSave };
                        StatsPanelUIToolkit.InWorldOverrideForTests = true;
                        StatsPanelUIToolkit p = probeGo.AddComponent<StatsPanelUIToolkit>();
                        p.BindView(r.panel.visualTree);
                        p.Refresh();
                        Object.DestroyImmediate(probeGo);
                        StatsPanelUIToolkit.InWorldOverrideForTests = false;
                        r.panel.visualTree.Q<VisualElement>("StatsPanelRoot")?.RemoveFromClassList("uk-hidden");
                    }, uiScale: scale);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 StatsPanel.uxml#StatsPanelWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
        }

        private static string Rows(int count, Func<int, string> row) => string.Join("\n", Enumerable.Range(0, count).Select(row));

        /// <summary>真 UIDocument + 面板（事件能派发：勾选开关、点按钮走 UI Toolkit 的真实回调）。</summary>
        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgReactionFeedbackSelfCheck") { hideFlags = HideFlags.HideAndDontSave };
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            doc.visualTreeAsset = vta;
            UiToolkitLayoutProbe.ForceLayout(doc.rootVisualElement);
            return doc.rootVisualElement;
        }

        private static void Click(Button b)
        {
            if (b?.clickable == null)
            {
                Fail($"按钮 {b?.name ?? "（空）"} 没有 Clickable");
                return;
            }
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod("Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { typeof(EventBase) }, null);
            using (ClickEvent evt = ClickEvent.GetPooled())
            {
                evt.target = b;
                invoke?.Invoke(b.clickable, new object[] { evt });
            }
        }

        // ─────────────────────────────── 世界 ───────────────────────────────

        private static void BuildCityWorld(int seed, bool observeCity, int warmSteps = 90)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            ReactionLog.Clear();
            CampaignState s = CampaignState.CreateNew("fgfw04-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            AddBlueprint(s, BpWet, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_coolant" }));
            AddBlueprint(s, BpShock, BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, new[] { "fw_arcchain" }));
            WorldSimulation.LoadHome(resume: false);
            int a = SpawnMachine(FracturedCityLayout.RegionId, BpWet, FracturedCityLayout.Scout2Spawn.Position + new Vector2(-3f, -3f));
            int b = SpawnMachine(FracturedCityLayout.RegionId, BpShock, FracturedCityLayout.Scout2Spawn.Position + new Vector2(2f, -3f));
            FracturedCityRegion.EnsureRegionRecordSeeded(s);
            RegionRecord region = FracturedCityRegion.Find(s);
            region.State = RegionState.Available;
            region.ExpeditionCount = 1; // 正式出发（ExpeditionDepartureService.TryDepart）会记第 1 次出击；这里直接载入地点，照同样的值写。
            FracturedCityController city = WorldSimulation.LoadFracturedCity(new[] { a, b }, resume: false);
            WorldView.Observe(observeCity ? FracturedCityLayout.RegionId : HomeValleyLayout.RegionId);
            city.SquadCommands.DebugSelectMany(new[] { a, b });
            city.SquadCommands.IssueAttack(FracturedCityLayout.Scout2SpawnId, paused: false);
            city.SquadCommands.ClearSelection();
            WorldSimulation.StepMany(warmSteps);
            WorldSimulation.SyncAllForSave();
            SaveResult r = CampaignAutoSaveService.SaveWithExport(Slot, SaveReason.Manual);
            if (!r.Success)
            {
                Fail("反应战斗存档写入失败：" + r.Message);
            }
        }

        private static void RestoreWorld(bool observeCity)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            InputRouter.Reset();
            ReactionPopups.ResetForTests();
            File.Copy(CampaignSaveService.SlotPath(Slot), CampaignSaveService.SlotPath(RunSlot), true);
            string bak = CampaignSaveService.SlotPath(RunSlot) + ".bak";
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }
            RestoreResult r = CampaignRestoreOrchestrator.Restore(RunSlot);
            if (!r.Success)
            {
                Fail("读反应战斗存档失败：" + r.Message);
                return;
            }
            CampaignSession.Set(RunSlot, r.State);
            WorldSimulation.LoadHome(resume: true);
            int[] ids = r.State.MachineRecords.Where(m => m.RegionId == FracturedCityLayout.RegionId && m.IsAlive).Select(m => m.LogicId).ToArray();
            WorldSimulation.LoadFracturedCity(ids, resume: true);
            WorldView.Observe(observeCity ? FracturedCityLayout.RegionId : HomeValleyLayout.RegionId);
            FeedbackCues.ObservedSiteProvider = () => WorldSimulation.AnyLoaded ? WorldView.ObservedSiteId : null;
        }

        /// <summary>按真实帧（1/60 秒）推进到第 <paramref name="target"/> 步；<paramref name="slowmoAt"/> ≥ 0 时到那一步开始一段慢放。</summary>
        private static void RunTo(long target, long slowmoAt)
        {
            int guard = 0;
            bool started = false;
            while (GameClock.Ticks < target && guard++ < 20000)
            {
                if (!started && slowmoAt >= 0 && GameClock.Ticks >= slowmoAt)
                {
                    GameClock.BeginSlowMotion(0.3f, 0.25f);
                    started = true;
                }
                WorldSimulation.Frame(1f / 60f, target);
            }
            GameClock.CancelSlowMotion();
        }

        private static ulong SiteHash() => CombatSites.Get(FracturedCityLayout.RegionId)?.Kernel.StateHash() ?? 0;

        private static int SpawnMachine(string region, string bp, Vector2 at)
        {
            MachineOpResult r = MachineRegistry.SpawnMachine(HomeValleyLayout.Erc003ChassisId, bp, region, at, 200f, 200f, "Player", 1);
            if (!r.Success)
            {
                Fail($"登记测试机器失败：{r.Message}");
            }
            return r.LogicId;
        }

        private static void AddBlueprint(CampaignState s, string id, BlueprintCircuitBoard board)
        {
            BlueprintVersionRecord version = board.ToVersion(1, 0f);
            s.BlueprintRecords = (s.BlueprintRecords ?? Array.Empty<BlueprintRecord>()).Where(r => r.BlueprintId != id)
                .Append(new BlueprintRecord { BlueprintId = id, DisplayName = id, ActiveVersion = 1, Versions = new[] { version } }).ToArray();
        }

        // ─────────────────────────────── 场地 ───────────────────────────────

        private static CombatSite NewSite()
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            return new CombatSite(SiteId, cfg); // 建地点时反馈基线已取好（CombatSite 构造里 Prime），第一步的反应不会被当成基线吞掉。
        }

        private static (int Wet, int Shock) SiteMachines(CombatSite site)
        {
            int iw = site.Kernel.AddWeapon(WeaponFor("fw_coolant"));
            int isk = site.Kernel.AddWeapon(WeaponFor("fw_arcchain"));
            return (site.Kernel.Spawn(Arena.MachineSpawn(new double2(0, 0), iw)), site.Kernel.Spawn(Arena.MachineSpawn(new double2(0, 1), isk)));
        }

        private static void FirePair(CombatSite site, int wet, int shock, int target)
        {
            site.Kernel.FireAt(wet, target, site.Kernel.Time);
            site.ProcessEvents();
            site.Kernel.FireAt(shock, target, site.Kernel.Time);
            site.ProcessEvents();
        }

        private static void StepSite(CombatSite site, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                site.Step(1f / 60f, site.Kernel.Time + 1f / 60f);
            }
        }

        private sealed class Arena : IDisposable
        {
            public readonly CombatKernel K;
            private int _machines;

            public Arena(bool configured)
            {
                CombatConfig cfg = CombatSite.ConfigFromTuning();
                cfg.NavEnabled = 0;
                K = new CombatKernel(cfg, 32);
                if (configured)
                {
                    K.SetReactionRules(NamedReactionCatalog.BuildKernelRules());
                    K.SetStatusFx(NamedReactionCatalog.BuildStatusFx());
                }
            }

            public int Machine(CombatWeapon w)
            {
                int wi = K.AddWeapon(w);
                return K.Spawn(MachineSpawn(new double2(0, _machines++ * 0.3), wi));
            }

            public int Enemy() => Enemy(new double2(3.0, 0.2));

            public int Enemy(double2 at) => K.Spawn(HostileSpawn(at));

            public CombatFireResult Fire(int machine, int target)
            {
                CombatFireResult r = K.FireAt(machine, target, K.Time);
                K.ClearCues();
                return r;
            }

            public void Run(float seconds)
            {
                int steps = Mathf.RoundToInt(seconds * 60f);
                for (int i = 0; i < steps; i++)
                {
                    K.Step(1f / 60f, K.Time + 1f / 60f);
                    K.ClearCues();
                    K.DrainGameplay(4096, out _);
                }
            }

            public bool Has(int id, string tag) =>
                K.TryGetStatus(id, out uint m, out _, out _, out _, out _) && (m & NamedReactionCatalog.BitOf(tag)) != 0u;

            public static CombatSpawn MachineSpawn(double2 at, int weapon) => new CombatSpawn
            {
                Kind = CombatUnitKind.Machine,
                Faction = CombatFaction.Player,
                Behavior = CombatBehavior.Commanded,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
                Position = at,
                Home = at,
                Radius = 0.4f,
                Speed = 4f,
                Health = 1e6f,
                MaxHealth = 1e6f,
                Weapon = weapon,
                BehaviorProfile = -1,
            };

            public static CombatSpawn HostileSpawn(double2 at) => new CombatSpawn
            {
                Kind = CombatUnitKind.Enemy,
                Faction = CombatFaction.Hostile,
                Behavior = CombatBehavior.HoldFire,
                Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable,
                Position = at,
                Home = at,
                Radius = 0.4f,
                Speed = 0f,
                Health = 1e6f,
                MaxHealth = 1e6f,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
            };

            public void Dispose() => K?.Dispose();
        }

        // ─────────────────────────────── 工具 ───────────────────────────────

        private static CampaignState NewState(int seed, bool unlockAll = false)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgfw04-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            CampaignFgStateDomains.EnsureAll(s);
            if (unlockAll)
            {
                s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            }
            return s;
        }

        private static void ResetFeedbackCounters()
        {
            ReactionPopups.ResetForTests();
            CameraNudge.ResetForTests();
            ReactionLog.Clear();
            GameClock.CancelSlowMotion();
        }

        private static CombatWeapon WeaponFor(params string[] firmware)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, ComponentCatalog.CompGunId, null, null, Array.Empty<string>());
            CircuitOpResult up = board.TrySetUplink(2);
            if (!up.Success)
            {
                Fail($"测试准备：蓝图 2 号格标接入口失败：{up.Message}");
            }
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, firmware ?? Array.Empty<string>());
            if (firmware != null && firmware.Length > 0 && !firmware.All(f => p.FirmwareIds.Contains(f)))
            {
                Fail($"测试准备：接入 {string.Join("/", firmware)} 没全部生效（生效 {string.Join("/", p.FirmwareIds)}）");
            }
            return CombatSite.MachineWeaponFrom(p);
        }

        /// <summary>与 <paramref name="tag"/> 不构成任何反应配对、且在内核里有状态位的另一个主标签（逐位到期测试用，避免触发反应把标签消耗掉）。</summary>
        private static string TagWithoutReaction(string tag)
        {
            uint a = NamedReactionCatalog.BitOf(tag);
            CombatReactionRule[] rules = NamedReactionCatalog.BuildKernelRules();
            foreach (StatusTag row in StatusTagCatalog.Rows)
            {
                // 不是持续伤害类（否则“燃烧到期后不再掉血”的断言会被它自己的持续伤害干扰）。
                if (row == null || row.Kind != "status" || row.Bit < 0 || row.Bit > 30 || (row.AliasOf != null && row.AliasOf != "none") || row.Id == tag
                    || NamedReactionCatalog.EffectCode(row.Effect) == CombatStatusFx.Dot)
                {
                    continue;
                }
                uint b = NamedReactionCatalog.BitOf(row.Id);
                if (b == 0u || b == a || rules.Any(r => r.Pair == (a | b)))
                {
                    continue;
                }
                return row.Id;
            }
            Fail($"测试准备：找不到与 {tag} 不反应的标签");
            return tag;
        }

        private static int RuleIndex(string reactionId)
        {
            for (int i = 0; i < NamedReactionCatalog.TagRules.Count; i++)
            {
                if (NamedReactionCatalog.TagRules[i].Id == reactionId)
                {
                    return i;
                }
            }
            return -1;
        }

        private static int Bit(string tag)
        {
            uint b = NamedReactionCatalog.BitOf(tag);
            return b == 0u ? -1 : math.tzcnt(b);
        }

        private static int IndexOf(byte[] data, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= data.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Length && ok; j++)
                {
                    ok = data[i + j] == pattern[j];
                }
                if (ok)
                {
                    return i;
                }
            }
            return -1;
        }

        private static string Detail(List<string> items) =>
            items.Count == 0 ? string.Empty : "：" + string.Join("；", items.Take(8)) + (items.Count > 8 ? $"……共 {items.Count} 项" : string.Empty);

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
