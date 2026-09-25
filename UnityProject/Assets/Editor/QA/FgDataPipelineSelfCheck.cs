using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Settings;
using Luban;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG0-DATA-01 的自动验收：建筑、敌人、文本三类数据从 Luban 表走到运行时（FGR-ARC-005/006）。
    ///
    /// 全部断言都起真实系统跑：真实 ConfigSystem 读真实 bytes；生产入口（电网仲裁 <see cref="HomeValleyPowerGrid.Recompute"/>、
    /// 敌人播种 / 击破掉落、工单与字幕的建筑名、出征情报的敌人名）在真表与"注入改过数值的表"两种情况下各跑一遍——
    /// 只有生产代码真的经表取值，注入后的结果才会跟着变（排除"常量碰巧等于表值"的假阳性）。
    /// 负向矩阵：缺键、缺某语言、占位符不匹配、表没加载上、字节缺字段、主键重复、语言切换与持久化、未知语言代码；
    /// 工具链侧真跑 python check_luban.py（规则自测 + 真实数据）与 fgdata.py 快照比对（源数据 == 运行时表）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgDataPipelineSelfCheck
    {
        private static StringBuilder _report;
        private static int _fail;

        [MenuItem("BinGames/自检：FG 数据驱动与文本键")]
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

        [MenuItem("BinGames/语言/简体中文")]
        public static void UseChinese() => GameSettings.SetLanguage(GameLanguage.ZhCn);

        [MenuItem("BinGames/语言/English")]
        public static void UseEnglish() => GameSettings.SetLanguage(GameLanguage.En);

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            Line("\n[FG数据] 数据驱动与文本键管线（FG0-DATA-01）");

            GameLanguage originalLanguage = GameSettings.Language;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                FgContentTables.Reload();

                CheckRealTablesLoaded();
                CheckTextEndToEnd();
                CheckMissingKeyMarkers();
                CheckBuildingEndToEnd();
                CheckEnemyEndToEnd();
                CheckCatalogConsistency();
                CheckRuntimeNegativeMatrix();
                CheckLanguagePersistence();
                CheckPerformance();
                CheckToolchain();
            }
            catch (Exception e)
            {
                Fail($"FG 数据自检抛异常：{e}");
            }
            finally
            {
                GameText.ResetForTests();
                FgContentTables.ResetForTests();
                GameSettings.SetLanguage(originalLanguage);
            }
            return _fail;
        }

        // ── 真表加载 ─────────────────────────────────────────────────────────

        private static void CheckRealTablesLoaded()
        {
            GameConfig.Tables t = ConfigSystem.Instance.Tables;
            Expect(GameText.LoadError == null, $"fg.TbLocText 经 ConfigSystem 读取成功（错误：{GameText.LoadError ?? "无"}）");
            Expect(FgContentTables.LoadError == null, $"fg.TbBuilding / fg.TbMechEnemy 读取成功（错误：{FgContentTables.LoadError ?? "无"}）");
            Expect(t.TbLocText.DataList.Count >= 14 && GameText.Count == t.TbLocText.DataList.Count,
                $"文本表 {t.TbLocText.DataList.Count} 条，GameText 可查 {GameText.Count} 条");
            Expect(FgContentTables.Buildings.Count == 9, $"建筑表 9 行（Demo 8 种建筑 + 第二座发电机），实际 {FgContentTables.Buildings.Count}");
            Expect(FgContentTables.Enemies.Count == 6, $"机械敌人表 6 行（Demo 6 类敌人），实际 {FgContentTables.Enemies.Count}");
        }

        // ── 文本端到端 ───────────────────────────────────────────────────────

        private static void CheckTextEndToEnd()
        {
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            Expect(GameText.Get("building.generator.name") == "发电机", $"中文：building.generator.name = “{GameText.Get("building.generator.name")}”");
            string zhLabel = MechanicalContentFacade.ResolveWorkOrderTargetLabel("home_valley:generator_2");
            Expect(zhLabel == "发电机", $"生产入口（工单目标名）：home_valley:generator_2 → “{zhLabel}”");
            string zhBeacon = FeedbackCues.BuildingLabel("home_valley:beacon");
            Expect(zhBeacon == "导航信标", $"生产入口（字幕建筑名）：导航信标此前查不到名字，现在 → “{zhBeacon}”");

            GameSettings.SetLanguage(GameLanguage.En);
            Expect(GameText.Get("building.generator.name") == "Generator", $"切到英文后同一键 → “{GameText.Get("building.generator.name")}”");
            string enLabel = MechanicalContentFacade.ResolveWorkOrderTargetLabel("home_valley:generator_2");
            Expect(enLabel == "Generator", $"切换语言不重载表，工单目标名下一次刷新即为英文：“{enLabel}”");
            Expect(FeedbackCues.BuildingLabel("home_valley:signal_tower") == "Signal Tower", "字幕建筑名同步变为英文（Signal Tower）");

            CampaignState state = CampaignState.CreateNew("fg0-data-01-intel", "Standard", 5);
            FracturedCityRegion.EnsureRegionRecordSeeded(state);
            FracturedCityRegion.Find(state).State = RegionState.Available; // 与出征门禁解锁后的状态一致
            string enIntel = ExpeditionDepartureService.BuildPrepSnapshot(state).EnemyIntelText;
            Expect(enIntel != null && enIntel.Contains("Silent Scout x2") && enIntel.Contains("Silent Jammer x1"),
                $"生产入口（出征情报）敌人名走文本键：{enIntel}");
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string zhIntel = ExpeditionDepartureService.BuildPrepSnapshot(state).EnemyIntelText;
            Expect(zhIntel != null && zhIntel.Contains("静默侦察机 x2（HP70") && !zhIntel.Contains("Silent"),
                $"切回中文，出征情报同步：{zhIntel}");

            // 对照：非建筑目标仍按原约定返回原始 id（调用方据此给通用称呼），不会被误标成缺失。
            Expect(MechanicalContentFacade.ResolveWorkOrderTargetLabel("home_valley:wreckage_x") == "home_valley:wreckage_x",
                "对照：非建筑目标仍返回原始 id（工单面板显示通用称呼），不产生 ⟦⟧");
        }

        private static void CheckMissingKeyMarkers()
        {
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            const string missing = "fg0.data01.no_such_key";
            string shown = GameText.Get(missing);
            Expect(shown == "⟦fg0.data01.no_such_key⟧", $"缺失键显示可见标记：{shown}");
            Expect(GameText.MissingKeys.Contains(missing), "缺失键进入 GameText.MissingKeys（调试可查）");
            Expect(GameText.Get(null) == "⟦null⟧", "null 键也显示标记，不返回空串");
            Expect(GameText.Format(missing, 1) == "⟦fg0.data01.no_such_key⟧", "Format 缺失键同样显示标记");
            Expect(GameText.ContainsMarker(shown) && !GameText.ContainsMarker(GameText.Get("building.core.name")),
                "ContainsMarker 能区分缺失标记与正常文本（冒烟扫界面用）");
        }

        // ── 建筑端到端 ───────────────────────────────────────────────────────

        private static void CheckBuildingEndToEnd()
        {
            Expect(HomeValleyLayout.BuildProfile.TryGetValue("generator_2", out var g2) && g2.ScrapCost == 60 && Mathf.Approximately(g2.Seconds, 40f),
                $"BuildProfile[generator_2] 来自表 = ({g2.ScrapCost}, {g2.Seconds})");
            string[] consumers = HomeValleyLayout.PowerProfile.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Expect(string.Join(",", consumers) == "analysis_bench,assembly_station,beacon,core,repair_bay,signal_tower,warehouse",
                $"PowerProfile 用电建筑集合 = {string.Join(",", consumers)}");
            Expect(HomeValleyLayout.PowerProfile["beacon"] == (30f, 2) && HomeValleyLayout.PowerProfile["repair_bay"] == (15f, 3),
                "PowerProfile 数值与 Demo 一致（信标 30/优先级 2，维修台 15/优先级 3）");
            Expect(HomeValleyLayout.PowerSupplyProfile.Count == 2 && Mathf.Approximately(HomeValleyLayout.PowerSupplyProfile["generator"], 80f),
                "PowerSupplyProfile = 两座发电机各 80");
            Expect(HomeValleyLayout.RepairProfile.Count == 3 && HomeValleyLayout.RepairProfile["signal_tower"] == (40, 15f),
                "RepairProfile = 发电机/仓库/信号塔，信号塔 (40, 15)");

            CampaignState state = PowerState();
            HomeValleyPowerGrid.GridSummary real = HomeValleyPowerGrid.Recompute(state);
            Expect(Mathf.Approximately(real.TotalSupply, 100f) && Mathf.Approximately(real.TotalDemand, 40f),
                $"生产入口（电网仲裁）真表：供给 {real.TotalSupply}（核心20+发电机80），需求 {real.TotalDemand}（核心10+信标30）");

            // 注入改过数值的表：生产代码若仍写死常量，下面三条都会失败。
            FgContentTables.OverrideForTests(BuildingTable(row => row.TypeId switch
            {
                "generator" => With(row, powerSupply: 50f),
                "beacon" => With(row, powerDemand: 45f),
                "generator_2" => With(row, buildScrap: 77, buildSeconds: 33f),
                _ => row,
            }), EnemyTable(r => r));
            HomeValleyPowerGrid.GridSummary injected = HomeValleyPowerGrid.Recompute(PowerState());
            Expect(Mathf.Approximately(injected.TotalSupply, 70f) && Mathf.Approximately(injected.TotalDemand, 55f),
                $"改表后电网仲裁跟着变：供给 {injected.TotalSupply}（期望 70），需求 {injected.TotalDemand}（期望 55）");
            Expect(HomeValleyLayout.BuildProfile["generator_2"] == (77, 33f), "改表后 BuildProfile[generator_2] = (77, 33)（派生缓存按 Revision 重建）");
            FgContentTables.ResetForTests();
            Expect(HomeValleyLayout.BuildProfile["generator_2"] == (60, 40f), "还原后回到真表数值 (60, 40)");
        }

        private static CampaignState PowerState()
        {
            CampaignState state = CampaignState.CreateNew("fg0-data-01-power", "Standard", 7);
            state.BuildingRecords = new[]
            {
                PowerBuilding("core", 1),
                PowerBuilding("generator", 1),
                PowerBuilding("beacon", 2),
            };
            return state;
        }

        private static BuildingRecord PowerBuilding(string typeId, int priority) => new BuildingRecord
        {
            BuildingId = HomeValleyLayout.RegionId + ":" + typeId,
            BuildingTypeId = typeId,
            RegionId = HomeValleyLayout.RegionId,
            Health = 100f,
            ConstructionState = BuildingConstructionState.Operational,
            PowerPriority = priority,
            PowerState = BuildingPowerState.NotApplicable,
            Inventory = Array.Empty<CargoEntry>(),
            QueueIds = Array.Empty<string>(),
        };

        // ── 敌人端到端 ───────────────────────────────────────────────────────

        private static void CheckEnemyEndToEnd()
        {
            Expect(Mathf.Approximately(FoundryOutpostLayout.ArmorBotMaxHealth, 160f) && Mathf.Approximately(FoundryOutpostLayout.MainCoreMaxHealth, 600f)
                && Mathf.Approximately(FracturedCityLayout.ScoutMaxHealth, 70f), "敌人生命读表：护甲机 160、主核心 600、侦察机 70");
            Expect(Mathf.Approximately(EnemyCatalog.ComputeFrontalArmorReducedDamage(100f, true), 60f)
                && Mathf.Approximately(EnemyCatalog.ComputeFrontalArmorReducedDamage(100f, false), 100f), "护甲机正面减伤读表 0.4：100 → 60，侧后不减");

            RegionEnemyRecord scout = SeedAndKillScout(out int realLoot);
            Expect(scout != null && Mathf.Approximately(scout.MaxHealth, 70f), $"生产入口（破碎都市播种）侦察机 MaxHealth = {scout?.MaxHealth}");
            Expect(realLoot == 15, $"生产入口（击破掉落）侦察机掉落废料 {realLoot}（表值 15）");
            CampaignState foundry = CampaignState.CreateNew("fg0-data-01-foundry", "Standard", 9);
            FoundryOutpostRegion.EnsureEnemiesSeeded(foundry);
            RegionEnemyRecord armor = foundry.RegionEnemies.FirstOrDefault(e => e.EnemyTypeId == EnemyCatalog.ArmorBotId);
            Expect(armor != null && Mathf.Approximately(armor.MaxHealth, 160f), $"生产入口（铸造前哨播种）护甲机 MaxHealth = {armor?.MaxHealth}");

            FgContentTables.OverrideForTests(BuildingTable(r => r), EnemyTable(row => row.Id switch
            {
                "enemy_scout" => new EnemyRow(row.Id, row.NameKey, 123f, row.Frontal, 17),
                "enemy_armorbot" => new EnemyRow(row.Id, row.NameKey, row.MaxHp, 0.25f, row.Loot),
                _ => row,
            }));
            RegionEnemyRecord injectedScout = SeedAndKillScout(out int injectedLoot);
            Expect(injectedScout != null && Mathf.Approximately(injectedScout.MaxHealth, 123f) && injectedLoot == 17,
                $"改表后播种生命 {injectedScout?.MaxHealth}（期望 123）、掉落 {injectedLoot}（期望 17）");
            Expect(Mathf.Approximately(EnemyCatalog.ComputeFrontalArmorReducedDamage(100f, true), 75f), "改表后护甲机正面减伤 0.25：100 → 75");
            FgContentTables.ResetForTests();
        }

        private static RegionEnemyRecord SeedAndKillScout(out int loot)
        {
            CampaignState state = CampaignState.CreateNew("fg0-data-01-ruins", "Standard", 8);
            FracturedCityRegion.EnsureEnemiesSeeded(state);
            RegionEnemyRecord scout = state.RegionEnemies.FirstOrDefault(e => e.EnemyTypeId == EnemyCatalog.ScoutId);
            loot = -1;
            if (scout == null)
            {
                return null;
            }
            float maxHealth = scout.MaxHealth;
            FracturedCityRegion.TryDamageEnemy(state, scout.EnemyInstanceId, maxHealth + 1f);
            GroundItemRecord drop = state.GroundItems?.FirstOrDefault(g => g.SalvageInstanceId == scout.EnemyInstanceId + ":scrap");
            loot = drop?.Amount ?? -1;
            scout.MaxHealth = maxHealth;
            return scout;
        }

        // ── 与 Demo 目录一致（防两处数据漂移）─────────────────────────────────

        private static void CheckCatalogConsistency()
        {
            int textRows = 0;
            int textOk = 0;
            foreach (LocText row in ConfigSystem.Instance.Tables.TbLocText.DataList)
            {
                textRows++;
                if (GameText.TryGet(row.Key, GameLanguage.ZhCn, out _) && GameText.TryGet(row.Key, GameLanguage.En, out _))
                {
                    textOk++;
                }
            }
            Expect(textRows > 0 && textOk == textRows, $"每条文本中英两列都非空（{textOk}/{textRows}）");

            var problems = new List<string>();
            foreach (Building row in FgContentTables.Buildings)
            {
                string contentId = BuildingCatalog.ResolveByBuildingTypeId(row.TypeId);
                string zh = GameText.Get(row.NameKey, GameLanguage.ZhCn);
                if (contentId != null && BuildingCatalog.TryGet(contentId, out MechanicalContentDef def) && def.DisplayName != zh)
                {
                    problems.Add($"{row.TypeId}: 目录“{def.DisplayName}” ≠ 表“{zh}”");
                }
                if (!GameText.Has(row.NameKey))
                {
                    problems.Add($"{row.TypeId}: 文本键 {row.NameKey} 不存在");
                }
            }
            foreach (KeyValuePair<string, MechanicalContentDef> kv in EnemyCatalog.All)
            {
                if (!FgContentTables.TryGetEnemy(kv.Key, out MechEnemy row))
                {
                    problems.Add($"{kv.Key}: 表里没有这个敌人");
                    continue;
                }
                string zh = GameText.Get(row.NameKey, GameLanguage.ZhCn);
                if (kv.Value.DisplayName != zh)
                {
                    problems.Add($"{kv.Key}: 目录“{kv.Value.DisplayName}” ≠ 表“{zh}”");
                }
                string hp = "HP" + row.MaxHp.ToString("0", CultureInfo.InvariantCulture);
                if (!kv.Value.ValuesSummary.Contains(hp) && !kv.Value.ValuesSummary.Contains("主核心" + hp))
                {
                    problems.Add($"{kv.Key}: 数值摘要没有 {hp}");
                }
            }
            Expect(problems.Count == 0, "建筑/敌人目录显示名与表中文逐字一致，敌人数值摘要与表生命一致" +
                (problems.Count == 0 ? string.Empty : "：" + string.Join("；", problems)));
        }

        // ── 运行时负向矩阵 ───────────────────────────────────────────────────

        private static void CheckRuntimeNegativeMatrix()
        {
            // 某语言缺文本 / 占位符
            GameText.OverrideForTests(new TbLocText(TextBuf(new[]
            {
                ("t.only_zh", "只有中文", ""),
                ("t.fmt", "{0} x{1}", "{0} x{1}"),
                ("t.bad_fmt", "{0} 与 {1}", "{0} and {1}"),
            })));
            GameSettings.SetLanguage(GameLanguage.En);
            Expect(GameText.Get("t.only_zh") == "⟦t.only_zh⟧", "英文列为空 → 显示 ⟦t.only_zh⟧，不静默显示空白");
            Expect(GameText.Get("t.only_zh", GameLanguage.ZhCn) == "只有中文", "对照：同一键中文列正常");
            Expect(GameText.Format("t.fmt", "Scout", 2) == "Scout x2", "Format 正常替换占位符");
            string bad;
            try
            {
                bad = GameText.Format("t.bad_fmt", "A");
            }
            catch (Exception e)
            {
                bad = "抛异常：" + e.GetType().Name;
            }
            Expect(bad == "⟦t.bad_fmt⟧", $"占位符比参数多 → 显示标记、不抛异常（{bad}）");
            GameSettings.SetLanguage(GameLanguage.ZhCn);

            // 文本表没加载上
            GameText.OverrideForTests(null, "测试：bytes 缺失");
            Expect(GameText.Get("building.core.name") == "⟦building.core.name⟧" && GameText.LoadError != null,
                "文本表没加载上 → 所有文本显示 ⟦key⟧ 并给出 LoadError");
            GameText.ResetForTests();
            Expect(GameText.Get("building.core.name") == "归还核心", "对照：还原真表后恢复正常");

            // 主键重复：Luban 运行时同样拒绝（python R2 之外的第二道防线）
            Expect(Throws(() => new TbLocText(TextBuf(new[] { ("dup.key", "甲", "A"), ("dup.key", "乙", "B") }))),
                "字节里文本键重复 → Luban 构造表时抛异常（不会悄悄用后一条覆盖前一条）");
            Expect(Throws(() => new TbBuilding(BuildingBuf(new[] { RealBuilding("core"), RealBuilding("core") }))),
                "字节里建筑主键重复 → 抛异常");

            // 表里缺字段：新代码读旧 bytes（少一列）
            Expect(Throws(() => new TbMechEnemy(TruncatedEnemyBuf())), "字节少一个字段（表结构与代码不一致）→ 读表抛异常，不会读成 0");

            // 内容表没加载上：生产入口要大声失败，不能用 0 血 / 0 成本继续
            FgContentTables.OverrideForTests(null, null, "测试：bytes 缺失");
            string msg = CaptureMessage(() => { float _ = FoundryOutpostLayout.ArmorBotMaxHealth; });
            Expect(msg != null && msg.Contains("正式版内容表不可用"), $"内容表不可用时读敌人生命 → 抛出可读错误（{msg}）");
            msg = CaptureMessage(() => { var _ = HomeValleyLayout.PowerProfile; });
            Expect(msg != null && msg.Contains("正式版内容表不可用"), "内容表不可用时读电力档案 → 抛出可读错误");
            FgContentTables.ResetForTests();
            msg = CaptureMessage(() => FgContentTables.Enemy("enemy_not_exist"));
            Expect(msg != null && msg.Contains("fg.TbMechEnemy") && msg.Contains("enemy_not_exist"), $"查不存在的敌人 → 错误里带表名与 ID（{msg}）");
        }

        private static void CheckLanguagePersistence()
        {
            GameSettings.SetLanguage(GameLanguage.En);
            GameSettings.Load(); // 从 PlayerPrefs 重新读盘
            Expect(GameSettings.Language == GameLanguage.En && GameText.Get("enemy.scout.name") == "Silent Scout",
                "语言设置落盘：重新读盘后仍为英文");
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            GameSettings.Load();
            Expect(GameSettings.Language == GameLanguage.ZhCn, "切回中文并重新读盘 → 中文");
            GameSettingsData legacy = JsonUtility.FromJson<GameSettingsData>("{\"UiScale\":1.2}");
            Expect(legacy.Language == GameLanguageCodes.ZhCn, "旧设置 JSON 没有 Language 字段 → 默认简体中文");
            Expect(GameLanguageCodes.Parse("fr") == GameLanguage.ZhCn && GameLanguageCodes.Parse("EN") == GameLanguage.En && GameLanguageCodes.Parse(null) == GameLanguage.ZhCn,
                "未知 / 空语言代码回落简体中文，代码大小写不敏感");
        }

        // ── 性能（Editor batchmode 实测；真机另测，见证据）────────────────────

        private static void CheckPerformance()
        {
            const int n = 1000000;
            string[] keys = ConfigSystem.Instance.Tables.TbLocText.DataList.Select(r => r.Key).ToArray();
            for (int i = 0; i < 1000; i++)
            {
                GameText.Get(keys[i % keys.Length]);
            }
            // Unity 的 Mono 不实现 GC.GetAllocatedBytesForCurrentThread（恒为 0，第一次跑时被下面的对照抓到），
            // 改用托管堆已用字节。它是按堆块计的粗粒度读数（编辑器其它线程也会贡献几百 KB 噪声），所以按"平均每次"判：
            // 100 万次命中若每次分配哪怕一个最小对象（≥16 B），堆增量至少 16 MB；容差 2 MB = 平均每次 < 2 B，只有零分配才过得去。
            GC.Collect();
            long before = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            var sw = Stopwatch.StartNew();
            int len = 0;
            for (int i = 0; i < n; i++)
            {
                len += GameText.Get(keys[i % keys.Length]).Length;
            }
            sw.Stop();
            long hitAlloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - before;
            double nsPerGet = sw.Elapsed.TotalMilliseconds * 1e6 / n;

            // 对照：同样的计量方式下，留住 20 万个新拼的串（约 8 MB）必须超过同一容差——证明计量能看见这个量级。
            GC.Collect();
            long ctlBefore = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            var keep = new string[200000];
            for (int i = 0; i < keep.Length; i++)
            {
                keep[i] = keys[i % keys.Length] + i;
            }
            long ctlAlloc = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - ctlBefore;
            len += keep[keep.Length - 1].Length;

            var profile = Stopwatch.StartNew();
            int sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += HomeValleyLayout.PowerProfile.Count;
            }
            profile.Stop();

            var load = Stopwatch.StartNew();
            ConfigSystem.Instance.Load();
            load.Stop();
            GameText.Reload();
            FgContentTables.Reload();

            Line($"  · 实测（Editor batchmode）：GameText.Get 命中 {nsPerGet:F0} ns/次、{n} 次堆增量 {hitAlloc} B（对照 20 万个串 +{ctlAlloc} B）；" +
                 $"PowerProfile 读取 {profile.Elapsed.TotalMilliseconds * 1e6 / n:F0} ns/次；全部 Luban 表重载 {load.Elapsed.TotalMilliseconds:F1} ms（校验和 {len + sum}）");
            Expect(ctlAlloc > 2 * 1024 * 1024 && hitAlloc < 2 * 1024 * 1024, $"GameText.Get 命中路径不分配（{n} 次堆增量 {hitAlloc} B，容差 2 MB 即平均每次 < 2 B；对照留住 20 万个串 +{ctlAlloc} B 证明计量有效）");
            Expect(nsPerGet < 2000, $"GameText.Get 命中 {nsPerGet:F0} ns/次 < 2 µs（每帧上百个标签也可忽略）");
        }

        // ── 工具链：python 检查与源数据快照 ──────────────────────────────────

        private static void CheckToolchain()
        {
            string root = LocateRepo();
            if (root == null)
            {
                Fail("找不到仓库根（需要 tools/cell_tables），无法跑 python 检查");
                return;
            }

            (int code, string output) = RunPython(root, "tools/cell_tables/check_luban.py --selftest");
            Match m = Regex.Match(output, @"自测 (\d+)/(\d+) 通过");
            bool selftestOk = code == 0 && m.Success && m.Groups[1].Value == m.Groups[2].Value && int.Parse(m.Groups[2].Value) >= 16;
            Expect(selftestOk, $"check_luban.py 规则自测（跨模块同名表 / 主键重复 / 缺字段 / 缺中英 / 键格式 / 键悬空 / 建筑与敌人语义）：{(m.Success ? m.Value : Tail(output))}");

            (code, output) = RunPython(root, "tools/cell_tables/check_luban.py");
            Expect(code == 0 && output.Contains("Luban 已知坑检查：通过"), $"真实 Datas 过 Luban 已知坑检查：{Tail(output)}");

            (code, output) = RunPython(root, "tools/cell_tables/fgdata.py --dump");
            if (code != 0)
            {
                Fail($"fgdata.py --dump 失败：{Tail(output)}");
                return;
            }
            var diffs = new List<string>();
            int rows = 0;
            foreach (string raw in output.Replace("\r", string.Empty).Split('\n'))
            {
                if (raw.Length == 0)
                {
                    continue;
                }
                rows++;
                string[] f = raw.Split('\t');
                switch (f[0])
                {
                    case "B":
                        if (!FgContentTables.TryGetBuilding(f[1], out Building b))
                        {
                            diffs.Add($"建筑 {f[1]} 不在运行时表");
                        }
                        else if (b.NameKey != f[2] || !Eq(b.PowerDemand, f[3]) || b.PowerPriority != int.Parse(f[4]) || !Eq(b.PowerSupply, f[5])
                                 || b.RepairScrap != int.Parse(f[6]) || !Eq(b.RepairSeconds, f[7]) || b.BuildScrap != int.Parse(f[8]) || !Eq(b.BuildSeconds, f[9]))
                        {
                            diffs.Add($"建筑 {f[1]} 数值与源数据不一致");
                        }
                        break;
                    case "E":
                        if (!FgContentTables.TryGetEnemy(f[1], out MechEnemy e))
                        {
                            diffs.Add($"敌人 {f[1]} 不在运行时表");
                        }
                        else if (e.NameKey != f[2] || !Eq(e.MaxHp, f[3]) || !Eq(e.FrontalDamageReduction, f[4]) || e.ScrapLoot != int.Parse(f[5]))
                        {
                            diffs.Add($"敌人 {f[1]} 数值与源数据不一致");
                        }
                        break;
                    case "T":
                        if (!GameText.TryGet(f[1], GameLanguage.ZhCn, out string zh) || zh != f[2]
                            || !GameText.TryGet(f[1], GameLanguage.En, out string en) || en != f[3])
                        {
                            diffs.Add($"文本 {f[1]} 与源数据不一致");
                        }
                        break;
                }
            }
            int runtimeRows = FgContentTables.Buildings.Count + FgContentTables.Enemies.Count + GameText.Count;
            Expect(diffs.Count == 0 && rows == runtimeRows,
                $"源数据 fgdata.py（{rows} 行）与运行时表（{runtimeRows} 行）逐字段一致——改了源数据却没重新生成会在这里失败" +
                (diffs.Count == 0 ? string.Empty : "：" + string.Join("；", diffs.Take(6))));
        }

        // ── 构造测试用字节（与 Luban 生成代码的读取顺序一致）────────────────

        private readonly struct EnemyRow
        {
            public readonly string Id;
            public readonly string NameKey;
            public readonly float MaxHp;
            public readonly float Frontal;
            public readonly int Loot;

            public EnemyRow(string id, string nameKey, float maxHp, float frontal, int loot)
            {
                Id = id;
                NameKey = nameKey;
                MaxHp = maxHp;
                Frontal = frontal;
                Loot = loot;
            }
        }

        private sealed class BuildingRow
        {
            public string TypeId;
            public string NameKey;
            public float PowerDemand;
            public int PowerPriority;
            public float PowerSupply;
            public int RepairScrap;
            public float RepairSeconds;
            public int BuildScrap;
            public float BuildSeconds;
        }

        private static BuildingRow RealBuilding(string typeId)
        {
            Building b = FgContentTables.Building(typeId);
            return new BuildingRow
            {
                TypeId = b.TypeId, NameKey = b.NameKey, PowerDemand = b.PowerDemand, PowerPriority = b.PowerPriority,
                PowerSupply = b.PowerSupply, RepairScrap = b.RepairScrap, RepairSeconds = b.RepairSeconds,
                BuildScrap = b.BuildScrap, BuildSeconds = b.BuildSeconds,
            };
        }

        private static BuildingRow With(BuildingRow row, float? powerDemand = null, float? powerSupply = null, int? buildScrap = null, float? buildSeconds = null)
        {
            return new BuildingRow
            {
                TypeId = row.TypeId, NameKey = row.NameKey,
                PowerDemand = powerDemand ?? row.PowerDemand, PowerPriority = row.PowerPriority,
                PowerSupply = powerSupply ?? row.PowerSupply, RepairScrap = row.RepairScrap, RepairSeconds = row.RepairSeconds,
                BuildScrap = buildScrap ?? row.BuildScrap, BuildSeconds = buildSeconds ?? row.BuildSeconds,
            };
        }

        private static TbBuilding BuildingTable(Func<BuildingRow, BuildingRow> edit)
        {
            ConfigSystem.Instance.Load();
            BuildingRow[] rows = ConfigSystem.Instance.Tables.TbBuilding.DataList.Select(b => edit(new BuildingRow
            {
                TypeId = b.TypeId, NameKey = b.NameKey, PowerDemand = b.PowerDemand, PowerPriority = b.PowerPriority,
                PowerSupply = b.PowerSupply, RepairScrap = b.RepairScrap, RepairSeconds = b.RepairSeconds,
                BuildScrap = b.BuildScrap, BuildSeconds = b.BuildSeconds,
            })).ToArray();
            return new TbBuilding(BuildingBuf(rows));
        }

        private static TbMechEnemy EnemyTable(Func<EnemyRow, EnemyRow> edit)
        {
            EnemyRow[] rows = ConfigSystem.Instance.Tables.TbMechEnemy.DataList
                .Select(e => edit(new EnemyRow(e.Id, e.NameKey, e.MaxHp, e.FrontalDamageReduction, e.ScrapLoot))).ToArray();
            var buf = new ByteBuf();
            buf.WriteSize(rows.Length);
            foreach (EnemyRow r in rows)
            {
                buf.WriteString(r.Id);
                buf.WriteString(r.NameKey);
                buf.WriteFloat(r.MaxHp);
                buf.WriteFloat(r.Frontal);
                buf.WriteInt(r.Loot);
            }
            return new TbMechEnemy(buf);
        }

        private static ByteBuf BuildingBuf(IReadOnlyList<BuildingRow> rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Count);
            foreach (BuildingRow r in rows)
            {
                buf.WriteString(r.TypeId);
                buf.WriteString(r.NameKey);
                buf.WriteFloat(r.PowerDemand);
                buf.WriteInt(r.PowerPriority);
                buf.WriteFloat(r.PowerSupply);
                buf.WriteInt(r.RepairScrap);
                buf.WriteFloat(r.RepairSeconds);
                buf.WriteInt(r.BuildScrap);
                buf.WriteFloat(r.BuildSeconds);
            }
            return buf;
        }

        private static ByteBuf TextBuf(IReadOnlyList<(string key, string zh, string en)> rows)
        {
            var buf = new ByteBuf();
            buf.WriteSize(rows.Count);
            foreach ((string key, string zh, string en) in rows)
            {
                buf.WriteString(key);
                buf.WriteString(zh);
                buf.WriteString(en);
            }
            return buf;
        }

        /// <summary>一行敌人只写到 maxHp，少了 frontalDamageReduction 与 scrapLoot——模拟旧 bytes 缺字段。</summary>
        private static ByteBuf TruncatedEnemyBuf()
        {
            var buf = new ByteBuf();
            buf.WriteSize(1);
            buf.WriteString("enemy_scout");
            buf.WriteString("enemy.scout.name");
            buf.WriteFloat(70f);
            return buf;
        }

        // ── 小工具 ───────────────────────────────────────────────────────────

        private static bool Eq(float value, string text) =>
            Mathf.Approximately(value, float.Parse(text, CultureInfo.InvariantCulture));

        private static bool Throws(Action action) => CaptureMessage(action) != null;

        private static string CaptureMessage(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                return (e is TypeInitializationException && e.InnerException != null ? e.InnerException : e).Message;
            }
        }

        private static string LocateRepo()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tools", "cell_tables", "check_luban.py")))
                {
                    return dir.FullName;
                }
            }
            return null;
        }

        private static (int code, string output) RunPython(string root, string args)
        {
            var psi = new ProcessStartInfo("python", args)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (Process proc = Process.Start(psi))
                {
                    var stdout = proc.StandardOutput.ReadToEndAsync();
                    var stderr = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch (InvalidOperationException) { }
                        return (-1, $"python {args} 超过 120 秒没有结束");
                    }
                    return (proc.ExitCode, stdout.Result + stderr.Result);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                return (-1, $"无法启动 python：{e.Message}");
            }
        }

        private static string Tail(string output) =>
            string.Join(" / ", output.Replace("\r", string.Empty).Split('\n').Where(l => l.Length > 0).Reverse().Take(3).Reverse());

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
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

        private static void Line(string text)
        {
            _report.AppendLine(text);
        }
    }
}
