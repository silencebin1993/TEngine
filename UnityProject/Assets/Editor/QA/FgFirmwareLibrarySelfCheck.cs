using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BinGames.EditorTools;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Nav;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Campaign.WorldSim;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Progression;
using GameLogic.Settings;
using GameLogic.UI.Kit;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-FW-05 固件库与图鉴的自动验收（FG02 FGR-FW-060、061；FG13 FGU-20、FGU-38、FGR-UX-051；FGT-FW-007；
    /// 卡片“未获得的条目显示剪影和获取途径；悬停按键跳到图鉴；锁定的固件在批量分解时跳过；批量分解需要确认；装配时固件所在的仓库路线被堵”；
    /// 承接 DEBT-FG2FW02-05、FG-GAP-026、FG-GAP-057、DEBT-FG1HUD01-02）。起真实系统跑、断言行为：
    /// A 数据：44 条固件、全部具名反应都有图鉴条目；标题 / 获取途径 / 图标贴图齐全；固件 ↔ 反应互链双向一致；界面文本键与调参齐全（中英）。
    /// B FGT-FW-007 筛选：6 个筛选维度逐个与“按表独立计算的期望集合”对账；搜索（名称 / 说明、大小写）；6 种排序的顺序。
    /// C 锁定与批量分解：锁定的、装进信号核的、被合成台预留的跳过，非固件芯片忽略；确认时按此刻状态重算；废料经资源账本返还；面板上“分解所选”先弹确认框，取消零变化、确认才执行。
    /// D 固件芯片是物品（FGR-FW-060）：容量 = 基础格 + 运转中的仓库 × 追加格；满了刻印拒绝、解析入账进待领取；仓库停用容量回落但不丢芯片；领取。
    /// E 图鉴：未获得 = 剪影 + 获取途径（图标同一张着黑）；拿到芯片即解锁并写图鉴文件、重新载入仍在（跨存档）；内容解锁补同步；反应首次打出解锁；页签计数；搜索不剧透；相关条目跨页签跳转。
    /// F 悬停跳图鉴：固件库行的提示带图鉴链接，悬停时按图鉴键 → 广播 GameEvent 并打开到该条目；没悬停时图鉴键开关图鉴；拼错的条目不当链接；确认框开着时不抢键；固件库键开关。
    /// G 存读档：锁定 / 来源 / 获得时刻真实存档往返；旧档没有字段 = 未锁定、来源未记录。
    /// H 取用路线（负向“装配时固件所在的仓库路线被堵”）：真实家园里仓储 → 装配站通畅；装配站四周被悬崖围住 → 被堵并写明被什么堵住；隔断 → 没有通路。
    /// I 性能：1000 枚芯片的查询 / 分解计划耗时；面板键不变时刷新 O(1)。
    /// J 界面：固件库面板（数量 / 容量、筛选下拉、空状态、详情、比较、锁定按钮、领取、暂停菜单入口）与布局探针（中英、缩放极值、四种分辨率）。
    /// 已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgFirmwareLibrarySelfCheck
    {
        private const int Slot = 0;
        private const string UiKitFolder = "Assets/GameRes/Raw/UI/UiKit/";
        private const string IconFolder = "Assets/GameRes/Raw/UI/Icons/";

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static string _dir;
        private static readonly Reader Keys = new Reader();

        [MenuItem("BinGames/Validate/FG2-FW-05 固件库与图鉴")]
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
            Line("\n[固件库] 固件库与图鉴（FG2-FW-05）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            _dir = Path.Combine(Path.GetTempPath(), "bingames-fgfw05-selfcheck-" + Guid.NewGuid().ToString("N"));
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
                Directory.CreateDirectory(_dir);
                CampaignSaveService.SaveDirectoryOverrideForTests = _dir;
                MechanicCodex.ResetForTests();
                MechanicCodex.FilePathOverrideForTests = Path.Combine(_dir, "codex_mechanics.json");
                FirmwareLibrary.ResetForTests();
                FirmwareLibrary.Install(); // 入账 → 图鉴解锁的订阅（正式流程在进入战役时装上）
                CodexHoverLink.ResetForTests();
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}；图鉴文件与存档写到临时目录；按键用脚本读取器驱动 UiKitInputPump");

                Step(CheckData);
                Step(CheckFilters);
                Step(CheckSearchAndSort);
                Step(CheckLockAndDisassemble);
                Step(CheckCapacity);
                Step(CheckCodexUnlock);
                Step(CheckCodexPanel);
                Step(CheckHoverJumpAndKeys);
                Step(CheckSaveLoad);
                Step(CheckRoute);
                Step(CheckPerf);
                Step(CheckLibraryPanel);
                Step(CheckLayout);
            }
            catch (Exception e)
            {
                Fail($"固件库自检抛异常：{e}");
            }
            finally
            {
                WorldSimulation.UnloadAll();
                UiConfirmDialog.ResetForTests();
                UiTooltip.ResetForTests();
                UiEscapeStack.Clear();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                FirmwareLibraryPanelUIToolkit.Clock = () => Time.realtimeSinceStartupAsDouble;
                CodexHoverLink.ResetForTests();
                FirmwareLibrary.ResetForTests();
                MechanicCodex.ResetForTests();
                FirmwareKinds.ResetForTests();
                CarrierReadings.ResetForTests();
                NamedReactionCatalog.ResetForTests();
                FirmwareCatalog.Invalidate();
                GameClock.SetSpeed(1f);
                GameClock.SetPaused(false);
                GameClock.ResetSession();
                CampaignSaveService.SaveDirectoryOverrideForTests = null;
                GameSettings.SetLanguage(originalLanguage);
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
            Line($"  [固件库] 断言 {_pass} 过 / {_fail} 败");
            return _fail;
        }

        // ── A. 数据 ─────────────────────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 数据：44 条固件与全部具名反应都有图鉴条目；标题 / 获取途径 / 图标贴图齐全；固件 ↔ 反应互链双向一致；文本键与调参齐全");
            var fw = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _)).Select(r => r.Id).ToList();
            var fwEntries = MechanicCodex.Entries.Where(e => e.Kind == MechanicCodexKind.Firmware).ToList();
            var rxEntries = MechanicCodex.Entries.Where(e => e.Kind == MechanicCodexKind.Reaction).ToList();
            int reactions = NamedReactionCatalog.Rows.Count(r => r != null);
            var missing = fw.Where(id => MechanicCodex.Find(MechanicCodex.FirmwareEntryId(id)) == null).ToList();
            Expect(fw.Count == 44 && fwEntries.Count == 44 && missing.Count == 0 && MechanicCodex.CountIn(MechanicCodex.TabFirmware) == 44,
                $"固件目录 {fw.Count} 条，图鉴固件页签 {fwEntries.Count} 条，一一对应{(missing.Count > 0 ? "；缺：" + string.Join("、", missing) : "")}");
            Expect(rxEntries.Count == reactions && reactions >= 18 && MechanicCodex.CountIn(MechanicCodex.TabReaction) == reactions,
                $"具名反应 {reactions} 条，图鉴反应页签 {rxEntries.Count} 条");
            var bad = new List<string>();
            foreach (MechanicCodexEntry e in fwEntries)
            {
                string title = MechanicCodex.Title(e);
                string hint = MechanicCodex.Hint(e, null);
                string acquire = FirmwareKinds.AcquireText(e.ContentId);
                string icon = MechanicCodex.IconOf(e);
                if (string.IsNullOrEmpty(title) || title == e.ContentId || GameText.ContainsMarker(title))
                {
                    bad.Add(e.Id + " 标题");
                }
                if (string.IsNullOrEmpty(acquire) || !hint.Contains(acquire) || GameText.ContainsMarker(hint))
                {
                    bad.Add(e.Id + " 获取途径");
                }
                if (string.IsNullOrEmpty(icon) || AssetDatabase.LoadAssetAtPath<Texture2D>(IconFolder + icon + ".png") == null)
                {
                    bad.Add(e.Id + " 图标 " + icon);
                }
            }
            foreach (MechanicCodexEntry e in rxEntries)
            {
                if (string.IsNullOrEmpty(MechanicCodex.Title(e)) || string.IsNullOrEmpty(MechanicCodex.Hint(e, null)))
                {
                    bad.Add(e.Id + " 文字");
                }
            }
            Expect(bad.Count == 0, $"44 条固件条目都有标题、获取途径（剪影下那一行）与存在的图标贴图（剪影 = 同一张着黑）；反应条目都有名字与未解锁说明{(bad.Count > 0 ? "；问题：" + string.Join("、", bad.Take(6)) : "")}");

            // 互链双向：固件条目链接到反应 ⇔ 反应条目链接回该固件；链接的目标都存在。
            var oneWay = new List<string>();
            foreach (MechanicCodexEntry e in fwEntries)
            {
                foreach (string link in e.Links)
                {
                    MechanicCodexEntry r = MechanicCodex.Find(link);
                    if (r == null || !r.Links.Contains(e.Id))
                    {
                        oneWay.Add(e.Id + "→" + link);
                    }
                }
            }
            foreach (MechanicCodexEntry r in rxEntries)
            {
                foreach (string link in r.Links)
                {
                    MechanicCodexEntry f = MechanicCodex.Find(link);
                    if (f == null || !f.Links.Contains(r.Id))
                    {
                        oneWay.Add(r.Id + "→" + link);
                    }
                }
            }
            int linked = fwEntries.Count(e => e.Links.Length > 0);
            // 对账：固件 fw 参与反应 r ⇔ r 是装配反应且触发固件是 fw，或 fw 产生的标签是 r 的配料之一（独立按表重算）。
            var oracleBad = new List<string>();
            foreach (GameConfig.fg.Reaction r in NamedReactionCatalog.Rows.Where(x => x != null))
            {
                foreach (string id in fw)
                {
                    var tags = new HashSet<string>(FirmwareKinds.TagsOf(id).Select(StatusTagCatalog.Canonical));
                    bool expect = r.Kind == NamedReactionCatalog.KindAssembly
                        ? MechanicalReactionCatalog.TriggerFirmwareOf(r.Id) == id
                        : tags.Contains(StatusTagCatalog.Canonical(r.TagA)) || tags.Contains(StatusTagCatalog.Canonical(r.TagB));
                    bool has = MechanicCodex.Find(MechanicCodex.FirmwareEntryId(id)).Links.Contains(MechanicCodex.ReactionEntryId(r.Id));
                    if (expect != has)
                    {
                        oracleBad.Add(id + "/" + r.Id);
                    }
                }
            }
            Expect(oneWay.Count == 0 && oracleBad.Count == 0 && linked >= 10,
                $"固件 ↔ 反应互链双向一致，且与“产生的标签是反应配料 / 装配反应的触发固件”按表独立重算的结果一致（{linked} 条固件至少参与一条反应）" +
                $"{(oneWay.Count + oracleBad.Count > 0 ? "；问题：" + string.Join("、", oneWay.Concat(oracleBad).Take(5)) : "")}");

            var keys = new[]
            {
                "fwlib.title", "fwlib.count", "fwlib.disassemble", "fwlib.confirm.title", "fwlib.confirm.lose", "fwlib.confirm.skip_locked", "fwlib.result.done",
                "fwlib.detail.route_blocked", "fwlib.route.station_enclosed", "fwlib.footer", "codex.tab.firmware", "codex.tab.reaction", "codex.firmware.locked_body",
                "ui.common.codex_jump", "pause.firmware", "fwlib.origin.unknown", "fwlib.empty", "fwlib.empty_filtered",
            };
            var missingKeys = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                missingKeys.AddRange(keys.Where(k => !GameText.Has(k) || GameText.ContainsMarker(GameText.Get(k))).Select(k => lang + ":" + k));
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            string[] tuning = { "firmware.storage.base_slots", "firmware.storage.per_warehouse", "firmware.library.disassemble_scrap", "firmware.library.refresh_seconds", "firmware.library.route_cache_seconds" };
            var missingTuning = tuning.Where(t => !GridContent.TryGetTuning(t, out float v) || !(v > 0f)).ToList();
            Expect(missingKeys.Count == 0 && missingTuning.Count == 0,
                $"固件库 / 图鉴文本键中英齐全，调参 5 项都在 fg.TbHomeTuning 且为正数{(missingKeys.Count + missingTuning.Count > 0 ? "；缺：" + string.Join("、", missingKeys.Concat(missingTuning)) : "")}");
            bool actions = InputActionCatalog.TryGet(GameActionId.OpenFirmware, out InputActionDef f1) && f1.Status == InputActionStatus.Wired
                           && InputActionCatalog.TryGet(GameActionId.OpenCodex, out InputActionDef f2) && f2.Status == InputActionStatus.Wired;
            Expect(actions, "固件库键（I）与图鉴键（C）在 fg.TbInputAction 里标为已接入（不再弹“后续版本开放”）");
        }

        // ── B. 筛选 ─────────────────────────────────────────────────────────────

        /// <summary>每条固件各一枚芯片（全部解锁），其中一条敌方加密的保持未破解。</summary>
        private static CampaignState LibraryState(int seed, out string rawId)
        {
            CampaignState s = NewState(seed, unlockAll: true);
            // 全解锁 = 全破解；留一条敌方加密固件不破解（破解按内容记在 UnlockedContentIds），让“未破解”筛选有样本。
            rawId = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _) && FirmwareKinds.IsEnemyProtocol(r.Id)).Select(r => r.Id).FirstOrDefault();
            string keepRaw = rawId;
            s.UnlockedContentIds = s.UnlockedContentIds.Where(id => id != keepRaw).ToArray();
            rawId = rawId != null && FirmwareKinds.IsRaw(s, rawId) ? rawId : null;
            int t = 0;
            foreach (GameConfig.fg.FirmwareKind row in FirmwareKinds.Rows)
            {
                if (row == null || !FirmwareCatalog.TryGet(row.Id, out _))
                {
                    continue;
                }
                s.PlaySeconds = ++t; // 获得时刻逐个递增（“最新获得”排序用）
                PrimitiveInventory.GrantCrafted(s, row.Id);
            }
            return s;
        }

        private static void CheckFilters()
        {
            Line("  · B1. FGT-FW-007 筛选：类别 / 种类 / 协议 / 破解 / 兼容载体 / 稀有度逐个与按表独立算出的期望集合对账；非固件芯片（聚焦镜）不进固件库");
            CampaignState s = LibraryState(9501, out string rawId);
            var rows = new List<FirmwareLibraryRow>();
            int total = FirmwareLibrary.Query(s, new FirmwareLibraryFilter(), rows);
            int nonFirmware = s.PrimitiveChips.Count(p => !FirmwareKinds.IsFirmware(p.CardDefId));
            Expect(total == 44 && rows.Count == 44 && nonFirmware >= 1 && rows.All(r => FirmwareKinds.IsFirmware(r.FirmwareId)),
                $"不筛选：44 枚固件芯片全部列出（另有 {nonFirmware} 枚基元芯片不属于固件库）");

            var all = s.PrimitiveChips.Where(p => FirmwareKinds.IsFirmware(p.CardDefId)).ToList();
            var failures = new List<string>();
            void Verify(string name, FirmwareLibraryFilter f, Func<string, bool> expect)
            {
                FirmwareLibrary.Query(s, f, rows);
                var got = new HashSet<string>(rows.Select(r => r.Chip.PartId));
                var want = new HashSet<string>(all.Where(p => expect(p.CardDefId)).Select(p => p.PartId));
                if (!got.SetEquals(want) || want.Count == 0 || want.Count == all.Count)
                {
                    failures.Add($"{name}：得到 {got.Count}、应为 {want.Count}");
                }
            }
            GameConfig.fg.FirmwareKind Row(string id) => FirmwareKinds.Rows.First(r => r != null && r.Id == id);
            foreach (FirmwareCategory c in new[] { FirmwareCategory.Fuse, FirmwareCategory.Limiter, FirmwareCategory.Fluid, FirmwareCategory.Electromagnetic })
            {
                string key = FirmwareLibraryPanelUIToolkit.CategoryKey(c);
                Verify("类别 " + key, new FirmwareLibraryFilter { Category = c }, id => Row(id).Category == key);
            }
            Verify("种类 常规", new FirmwareLibraryFilter { Kind = FirmwareKind.Regular }, id => !FirmwareKinds.CoreRosterIds.Contains(id));
            Verify("种类 核心", new FirmwareLibraryFilter { Kind = FirmwareKind.Core }, id => FirmwareKinds.CoreRosterIds.Contains(id));
            Verify("协议 敌方", new FirmwareLibraryFilter { EnemyProtocol = true }, id => Row(id).Protocol == "enemy");
            Verify("协议 己方 / 中立", new FirmwareLibraryFilter { EnemyProtocol = false }, id => Row(id).Protocol != "enemy");
            Verify("未破解", new FirmwareLibraryFilter { Cracked = false }, id => FirmwareKinds.IsRaw(s, id));
            Verify("已破解", new FirmwareLibraryFilter { Cracked = true }, id => !FirmwareKinds.IsRaw(s, id));
            foreach (FirmwareCarrier c in CarrierReadings.AllCarriers)
            {
                Verify("兼容 " + c, new FirmwareLibraryFilter { Carrier = c },
                    id => !FirmwareKinds.CoreRosterIds.Contains(id) && !string.IsNullOrEmpty(FirmwareKinds.Reading(id, c)));
            }
            foreach (string r in new[] { "common", "rare", "epic" })
            {
                if (FirmwareKinds.Rows.Any(x => x != null && x.Rarity == r))
                {
                    Verify("稀有度 " + r, new FirmwareLibraryFilter { Rarity = r }, id => Row(id).Rarity == r);
                }
            }
            // 组合：常规 + 射弹兼容 + 普通
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Kind = FirmwareKind.Regular, Carrier = FirmwareCarrier.Projectile, Rarity = "common" }, rows);
            bool combo = rows.Count > 0 && rows.All(r => r.Kind == FirmwareKind.Regular && r.Rarity == "common" && FirmwareLibrary.IsCompatible(r.FirmwareId, FirmwareCarrier.Projectile));
            Expect(failures.Count == 0 && combo && rawId != null,
                $"6 个筛选维度逐个对账通过（核心固件对任何载体都不兼容；未破解 = 敌方加密且未破解，例如 {FirmwareKinds.DisplayName(rawId)}）；组合筛选取交集（{rows.Count} 枚）" +
                $"{(failures.Count > 0 ? "；不一致：" + string.Join("、", failures) : "")}");
        }

        private static void CheckSearchAndSort()
        {
            Line("  · B2. 搜索（名称 / 说明、首尾空格、英文大小写）与 6 种排序");
            CampaignState s = LibraryState(9502, out _);
            var rows = new List<FirmwareLibraryRow>();
            string id = RegularFirmware();
            string name = FirmwareKinds.DisplayName(id);
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Search = "  " + name + " " }, rows);
            bool byName = rows.Count >= 1 && rows.Any(r => r.FirmwareId == id) && rows.All(r => FirmwareKinds.DisplayName(r.FirmwareId).Contains(name)
                                                                                            || (FirmwareCatalog.TryGet(r.FirmwareId, out MechanicalContentDef d) && d.Description.Contains(name)));
            FirmwareCatalog.TryGet(id, out MechanicalContentDef def);
            string descWord = def?.Description?.Length >= 6 ? def.Description.Substring(2, 4) : null;
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Search = descWord }, rows);
            bool byDesc = descWord != null && rows.Any(r => r.FirmwareId == id);
            GameSettings.SetLanguage(GameLanguage.En);
            FirmwareCatalog.Invalidate();
            string en = FirmwareKinds.DisplayName(id);
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Search = en.ToUpperInvariant() }, rows);
            bool caseless = rows.Any(r => r.FirmwareId == id);
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            FirmwareCatalog.Invalidate();
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Search = "不存在的固件名zz" }, rows);
            Expect(byName && byDesc && caseless && rows.Count == 0,
                $"搜索“{name}”命中（首尾空格忽略）；按说明片段“{descWord}”命中；英文“{en.ToUpperInvariant()}”不分大小写命中；搜不到时为空");

            var bad = new List<string>();
            int Rank(string r) => r == "epic" ? 3 : r == "rare" ? 2 : r == "common" ? 1 : 0;
            void Sorted(FirmwareLibrarySort sort, Func<FirmwareLibraryRow, FirmwareLibraryRow, bool> inOrder)
            {
                FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Sort = sort }, rows);
                for (int i = 1; i < rows.Count; i++)
                {
                    if (!inOrder(rows[i - 1], rows[i]))
                    {
                        bad.Add($"{sort} 第 {i} 行：{rows[i - 1].Name} / {rows[i].Name}");
                        return;
                    }
                }
            }
            Sorted(FirmwareLibrarySort.Name, (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture) <= 0);
            Sorted(FirmwareLibrarySort.Category, (a, b) => a.Category <= b.Category);
            Sorted(FirmwareLibrarySort.Rarity, (a, b) => Rank(a.Rarity) >= Rank(b.Rarity));
            Sorted(FirmwareLibrarySort.Load, (a, b) => a.Load >= b.Load);
            Sorted(FirmwareLibrarySort.Newest, (a, b) => a.Chip.AcquiredTick >= b.Chip.AcquiredTick);
            // 位置：把一枚移进信号核、一枚锁住（锁定不影响位置），信号核排在仓储之后。
            PrimitiveChipRecord first = s.PrimitiveChips.First(p => FirmwareKinds.IsFirmware(p.CardDefId) && !FirmwareKinds.IsCore(p.CardDefId));
            PrimitiveInventory.TryMoveToSignalCore(s, first.PartId);
            Sorted(FirmwareLibrarySort.Location, (a, b) => a.Location <= b.Location);
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Sort = FirmwareLibrarySort.Newest }, rows);
            bool newestFirst = rows.Count > 0 && rows[0].Chip.AcquiredTick == s.PrimitiveChips.Max(p => p.AcquiredTick);
            FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Sort = FirmwareLibrarySort.Location }, rows);
            bool coreLast = rows.Count > 0 && rows[rows.Count - 1].Chip.PartId == first.PartId && rows[rows.Count - 1].Location == FirmwareLocationKind.SignalCore;
            Expect(bad.Count == 0 && newestFirst && coreLast,
                $"排序：名称升序、类别、稀有度（高→低）、负载（高→低）、最新获得（按获得时刻）、所在位置（仓储 → 信号核）逐行有序{(bad.Count > 0 ? "；乱序：" + string.Join("、", bad) : "")}");
        }

        // ── C. 锁定与批量分解 ─────────────────────────────────────────────────

        private static void CheckLockAndDisassemble()
        {
            Line("  · C. 锁定与批量分解：锁定 / 在信号核 / 合成台预留的跳过，非固件芯片忽略；确认时按此刻状态重算；废料经资源账本返还");
            CampaignState s = NewState(9503, unlockAll: true);
            string fw = FirmwareCatalog.FwOverloadId;
            string a = PrimitiveInventory.GrantCrafted(s, fw); // 锁定
            string b = PrimitiveInventory.GrantCrafted(s, fw); // 在仓，可分解
            string c = PrimitiveInventory.GrantCrafted(s, fw); // 装进信号核
            string d = PrimitiveInventory.GrantCrafted(s, fw); // 合成台预留
            string e = PrimitiveInventory.GrantCrafted(s, fw); // 待领取，可分解
            PrimitiveInventory.Find(s, e).State = PrimitiveChipState.Pending;
            string seed = s.PrimitiveChips.First(p => !FirmwareKinds.IsFirmware(p.CardDefId)).PartId; // 基元芯片“聚焦镜”
            bool locked = FirmwareLibrary.TrySetLocked(s, a, true) && PrimitiveInventory.Find(s, a).Locked;
            bool movedC = PrimitiveInventory.TryMoveToSignalCore(s, c).Success;
            bool reservedD = PrimitiveInventory.TryReserveForCraft(s, d, "fgfw05-tx").Success;
            bool lockSeed = FirmwareLibrary.TrySetLocked(s, seed, true);
            var ids = new[] { a, b, c, d, e, seed, b, "no-such-chip" };
            FirmwareDisassemblePlan plan = FirmwareLibrary.PlanDisassemble(s, ids);
            bool planned = plan.Eligible.Count == 2 && plan.Eligible.Contains(b) && plan.Eligible.Contains(e) && plan.SkippedLocked == 1 && plan.SkippedBusy == 2
                           && plan.Scrap == 2 * FirmwareLibrary.DisassembleScrap && plan.Unprintable == 0;
            Expect(locked && movedC && reservedD && !lockSeed && planned,
                $"计划：可分解 {plan.Eligible.Count}（在仓 + 待领取）、跳过锁定 {plan.SkippedLocked}、跳过在用 {plan.SkippedBusy}（信号核 + 合成台预留）；重复 ID、找不到的、基元芯片都不算；固件库不锁非固件芯片");
            float scrap0 = s.Scrap;
            int ledger0 = s.ResourceTransactions?.Length ?? 0;
            FirmwareDisassemblePlan done = FirmwareLibrary.Disassemble(s, ids);
            bool removed = PrimitiveInventory.Find(s, b) == null && PrimitiveInventory.Find(s, e) == null;
            bool kept = PrimitiveInventory.Find(s, a) != null && PrimitiveInventory.Find(s, c)?.State == PrimitiveChipState.SignalCore
                        && PrimitiveInventory.Find(s, d)?.ReservedByTransactionId == "fgfw05-tx" && PrimitiveInventory.Find(s, seed) != null;
            Expect(removed && kept && Math.Abs(s.Scrap - scrap0 - 2 * FirmwareLibrary.DisassembleScrap) < 1e-3f && done.Scrap == 2 * FirmwareLibrary.DisassembleScrap
                   && (s.ResourceTransactions?.Length ?? 0) > ledger0 && s.EventLedger.Any(x => x.Category == "FirmwareChipDisassemble" && x.EventId.EndsWith(b)),
                $"执行：只移除在仓与待领取的两枚；锁定的、信号核里的、被预留的、基元芯片都还在；废料 {scrap0:0} → {s.Scrap:0}（+{done.Scrap}，经资源账本一笔生产型事务），事件账本记下每枚的分解");

            // 确认框开着期间状态变了：确认时按此刻状态重算。
            string f = PrimitiveInventory.GrantCrafted(s, fw);
            FirmwareDisassemblePlan before = FirmwareLibrary.PlanDisassemble(s, new[] { f });
            FirmwareLibrary.TrySetLocked(s, f, true);
            FirmwareDisassemblePlan after = FirmwareLibrary.Disassemble(s, new[] { f });
            Expect(before.Eligible.Count == 1 && after.Eligible.Count == 0 && after.SkippedLocked == 1 && PrimitiveInventory.Find(s, f) != null,
                "计划时可分解、确认前被锁定 → 执行时照样跳过（不信任计划时刻的状态）");
            // 第二道防线：直接调移除入口也不会移除锁定 / 在用的。
            List<PrimitiveChipRecord> direct = PrimitiveInventory.RemoveForDisassembly(s, new[] { a, c, d, f });
            Expect(direct.Count == 0 && PrimitiveInventory.Find(s, a) != null && PrimitiveInventory.Find(s, c) != null,
                "绕过计划直接调 PrimitiveInventory.RemoveForDisassembly：锁定 / 信号核 / 预留的一枚都不移除");
            // 解锁后可以分解；无法再刻印的件数写进确认框。
            FirmwareLibrary.TrySetLocked(s, a, false);
            string rawOnly = FirmwareKinds.Rows.Where(r => r != null && FirmwareKinds.IsEnemyProtocol(r.Id)).Select(r => r.Id).FirstOrDefault();
            CampaignState s2 = NewState(9504, unlockAll: false);
            string g = rawOnly != null ? PrimitiveInventory.GrantCrafted(s2, rawOnly) : null;
            FirmwareDisassemblePlan p2 = FirmwareLibrary.PlanDisassemble(s2, new[] { g });
            Expect(FirmwareLibrary.PlanDisassemble(s, new[] { a }).Eligible.Count == 1 && g != null && p2.Unprintable == 1,
                $"解除锁定后可分解；未解锁 / 未破解的固件（{FirmwareKinds.DisplayName(rawOnly)}）分解后无法再刻印，计划里单列（确认框写明）");
        }

        // ── D. 容量 ─────────────────────────────────────────────────────────────

        private static void CheckCapacity()
        {
            Line("  · D. 固件芯片是物品（FGR-FW-060）：容量 = 基础格 + 运转中的仓库 × 追加格；满了刻印拒绝、入账进待领取；仓库停用容量回落但芯片不丢");
            CampaignState s = NewState(9505, unlockAll: true);
            s.BuildingRecords = (s.BuildingRecords ?? Array.Empty<BuildingRecord>()).Where(x => x == null || x.BuildingTypeId != HomeValleyLayout.BuildingTypeWarehouse).ToArray();
            GridContent.TryGetTuning("firmware.storage.base_slots", out float baseSlots);
            GridContent.TryGetTuning("firmware.storage.per_warehouse", out float per);
            int cap0 = PrimitiveInventory.CapacityOf(s);
            var wh = new BuildingRecord
            {
                BuildingId = "fgfw05-wh", BuildingTypeId = HomeValleyLayout.BuildingTypeWarehouse, RegionId = HomeValleyLayout.RegionId,
                ConstructionState = BuildingConstructionState.Operational,
            };
            s.BuildingRecords = s.BuildingRecords.Append(wh).ToArray();
            int cap1 = PrimitiveInventory.CapacityOf(s);
            Expect(cap0 == (int)baseSlots && cap1 == (int)baseSlots + (int)per && per > 0f,
                $"没有运转中的仓库：{cap0} 格（Demo 8 格沿用为基础格）；修好一座仓库：{cap1} 格（+{per}）——Demo 的 8 格硬上限取消");
            string fw = FirmwareCatalog.FwOverloadId;
            while (PrimitiveInventory.BagCount(s) < cap1)
            {
                PrimitiveInventory.GrantCrafted(s, fw);
            }
            CircuitOpResult print = PrimitiveInventory.TryPrintFirmwareChip(s, fw, true, true, 0, out _);
            string over = PrimitiveInventory.GrantCrafted(s, fw);
            bool pending = PrimitiveInventory.Find(s, over).State == PrimitiveChipState.Pending;
            wh.ConstructionState = BuildingConstructionState.Damaged;
            int cap2 = PrimitiveInventory.CapacityOf(s);
            int bagAfter = PrimitiveInventory.BagCount(s);
            Expect(!print.Success && print.Code == "bag-full" && pending && cap2 == cap0 && bagAfter == cap1,
                $"放满 {cap1} 格：刻印拒绝（{print.Code}），合成 / 解析入账的新芯片进待领取；仓库停用后容量回落到 {cap2}，已经放着的 {bagAfter} 枚一枚不丢");
            wh.ConstructionState = BuildingConstructionState.Operational;
            FirmwareDisassemblePlan made = FirmwareLibrary.Disassemble(s, new[] { s.PrimitiveChips.First(p => p.State == PrimitiveChipState.Bag && p.CardDefId == fw).PartId });
            bool claimed = PrimitiveInventory.TryClaimPending(s, over).Success && PrimitiveInventory.Find(s, over).State == PrimitiveChipState.Bag;
            Expect(made.Eligible.Count == 1 && claimed, "分解一枚腾出一格后，待领取的那枚可以领取进仓储");
        }

        // ── E. 图鉴 ─────────────────────────────────────────────────────────────

        private static void CheckCodexUnlock()
        {
            Line("  · E1. 图鉴解锁：未获得 = 剪影 + 获取途径；拿到芯片即解锁并写图鉴文件；重新载入 / 换存档仍在（机制条目跨存档）；内容解锁补同步；反应首次打出解锁");
            string file = MechanicCodex.FilePathOverrideForTests;
            if (File.Exists(file))
            {
                File.Delete(file);
            }
            GameSettings.ResetAllToDefault();
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            MechanicCodex.Reload();
            int unlockedBefore = MechanicCodex.UnlockedCountIn(MechanicCodex.TabFirmware);
            CampaignState s = NewState(9506, unlockAll: false);
            string target = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _) && !FirmwareLibrary.IsObtained(s, r.Id)).Select(r => r.Id).FirstOrDefault();
            string entry = MechanicCodex.FirmwareEntryId(target);
            bool lockedAtStart = target != null && !MechanicCodex.IsUnlocked(entry) && unlockedBefore == 0;
            int saves = MechanicCodex.SaveCount;
            PrimitiveInventory.GrantCrafted(s, target);
            bool unlocked = MechanicCodex.IsUnlocked(entry) && MechanicCodex.SaveCount == saves + 1 && File.Exists(file);
            PrimitiveInventory.GrantCrafted(s, target);
            bool noRewrite = MechanicCodex.SaveCount == saves + 1;
            MechanicCodex.Reload();
            CampaignState other = NewState(9507, unlockAll: false);
            bool persisted = MechanicCodex.IsUnlocked(entry) && other != null && !FirmwareLibrary.IsObtained(other, target);
            Expect(lockedAtStart && unlocked && noRewrite && persisted,
                $"新玩家：固件页签全是剪影；第一次拿到“{FirmwareKinds.DisplayName(target)}”芯片 → 条目解锁并写图鉴文件，再拿不重复写；重新载入、换一个没有它的存档仍解锁（跨存档）");

            // 内容解锁（开局蓝图库 / 解析台破解）但没有芯片：打开图鉴时补同步。
            string second = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _) && !MechanicCodex.IsUnlocked(MechanicCodex.FirmwareEntryId(r.Id)))
                .Select(r => r.Id).FirstOrDefault();
            other.UnlockedContentIds = (other.UnlockedContentIds ?? Array.Empty<string>()).Append(second).ToArray();
            int synced = FirmwareLibrary.SyncCodex(other);
            int again = FirmwareLibrary.SyncCodex(other);
            Expect(second != null && synced >= 1 && again == 0 && MechanicCodex.IsUnlocked(MechanicCodex.FirmwareEntryId(second)),
                $"“{FirmwareKinds.DisplayName(second)}”内容已解锁但没有芯片：同步补解锁 {synced} 条，再同步 0 条（幂等）");

            // 反应：第一次打出（首次触发记录）即解锁统一图鉴的反应条目。
            string rx = "reaction_conduct";
            string rxEntry = MechanicCodex.ReactionEntryId(rx);
            bool rxLocked = !MechanicCodex.IsUnlocked(rxEntry);
            CampaignState s3 = NewState(9516, unlockAll: true);
            bool recorded = ReactionFeedback.RecordFirst(s3, rx, "fgfw05-site");
            MechanicCodex.Reload();
            bool rxUnlocked = MechanicCodex.IsUnlocked(rxEntry);
            string body = MechanicCodex.Body(MechanicCodex.Find(rxEntry), s3);
            NamedReactionCatalog.TryGet(rx, out GameConfig.fg.Reaction rxRow);
            bool bodyOk = rxRow != null && body.Contains(GameText.Get(rxRow.DescKey)) && !body.Contains(GameText.Get("codex.reaction.elsewhere"));
            string bodyElsewhere = MechanicCodex.Body(MechanicCodex.Find(rxEntry), other);
            Expect(rxLocked && recorded && rxUnlocked && bodyOk && bodyElsewhere.Contains(GameText.Get("codex.reaction.elsewhere")),
                $"第一次打出短路 → 反应条目解锁（写图鉴文件，重新载入仍在）；正文含说明、配料、能提供配料的固件与本存档首次触发：“{Short(body)}”；在别的存档里看写“本存档还没有打出过”");

            // 旧存档 / 换机器（图鉴文件没有这些条目，但存档里已经持有芯片、打出过反应）：进入战役时就补解锁，
            // 悬停提示里的图鉴链接从一开始就显示名字，而不是等玩家打开一次固件库或图鉴。
            File.Delete(file);
            MechanicCodex.Reload();
            bool wiped = !MechanicCodex.IsUnlocked(entry) && !MechanicCodex.IsUnlocked(rxEntry);
            string linkBefore = CodexHoverLink.EntryTitle(entry);
            int entered = FirmwareLibrary.OnCampaignEntered(s);
            string linkAfter = CodexHoverLink.EntryTitle(entry);
            // 反应记录放到一个没有“全部解锁”的存档上补同步（s3 是全解锁档，拿它同步会把 44 条固件都解锁，后面 E2 就没有剪影可测了）。
            other.ReactionFirstTriggers = s3.ReactionFirstTriggers;
            int enteredRx = FirmwareLibrary.OnCampaignEntered(other);
            Expect(wiped && linkBefore == GameText.Get("codex.panel.locked_title") && entered >= 1 && MechanicCodex.IsUnlocked(entry)
                   && linkAfter == FirmwareKinds.DisplayName(target) && enteredRx >= 1 && MechanicCodex.IsUnlocked(rxEntry),
                $"旧存档（图鉴文件里没有、存档里有芯片 / 反应记录）：进入战役（FirmwareLibrary.OnCampaignEntered，新建 / 读档 / 远征回滚共用）即补解锁 {entered} + {enteredRx} 条，悬停链接从“{linkBefore}”变成“{linkAfter}”");
        }

        private static void CheckCodexPanel()
        {
            Line("  · E2. 图鉴面板：固件 / 反应页签与计数；未获得显示“？？？”+ 获取途径 + 剪影图标；搜索不剧透；相关条目跨页签跳转；空搜索提示");
            CampaignState s = NewState(9508, unlockAll: false);
            string owned = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _) && FirmwareKinds.KindOf(r.Id) == FirmwareKind.Regular
                                                         && MechanicCodex.Find(MechanicCodex.FirmwareEntryId(r.Id)).Links.Length > 0).Select(r => r.Id).First();
            PrimitiveInventory.GrantCrafted(s, owned);
            string locked = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _) && !MechanicCodex.IsUnlocked(MechanicCodex.FirmwareEntryId(r.Id))
                                                          && !FirmwareLibrary.IsObtained(s, r.Id)).Select(r => r.Id).FirstOrDefault();
            VisualElement root = MountUxml(UiKitFolder + "MechanicCodexPanel.uxml", out GameObject go);
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<MechanicCodexPanelUIToolkit>();
                panel.BindView(root);
                MechanicCodex.Open(MechanicCodex.FirmwareEntryId(locked), unlock: false);
                string lockedName = FirmwareKinds.DisplayName(locked);
                bool lockedShown = MechanicCodexPanelUIToolkit.IsOpen && panel.CurrentTab == MechanicCodex.TabFirmware && panel.SelectedId == MechanicCodex.FirmwareEntryId(locked)
                                   && panel.EntryTitleText == GameText.Get("codex.panel.locked_title") && panel.EntryBodyText.Contains(FirmwareKinds.AcquireText(locked))
                                   && !panel.EntryBodyText.Contains(lockedName) && panel.EntryIconSilhouette && !panel.EntryIconHidden
                                   && panel.EntryIconId == MechanicCodex.IconOf(MechanicCodex.Find(MechanicCodex.FirmwareEntryId(locked))) && panel.RelatedCount == 0;
                int lockedItems = Enumerable.Range(0, panel.ItemCount).Count(i => panel.ItemText(i) == GameText.Get("codex.panel.locked_title"));
                bool counts = panel.ItemCount == 44 && lockedItems == 44 - MechanicCodex.UnlockedCountIn(MechanicCodex.TabFirmware)
                              && panel.CountText == GameText.Format("codex.panel.list_title", MechanicCodex.UnlockedCountIn(MechanicCodex.TabFirmware), 44)
                              && panel.TabText(1).Contains("/44");
                Expect(lockedShown && counts,
                    $"未获得的“{lockedName}”：标题“？？？”、正文只有获取途径（不剧透名字）、图标是同一张着黑的剪影、不列相关条目；固件页签 {panel.ItemCount} 条、剪影 {lockedItems} 条、计数“{panel.CountText}”、页签“{panel.TabText(1)}”");

                panel.Select(MechanicCodex.FirmwareEntryId(owned));
                bool ownedShown = panel.EntryTitleText == FirmwareKinds.DisplayName(owned) && !panel.EntryIconSilhouette
                                  && panel.EntryBodyText.Contains(GameText.Get("fwlib.readings_title")) && panel.EntryBodyText.Contains(GameText.Format("fwlib.detail.acquire", FirmwareKinds.AcquireText(owned)))
                                  && CarrierReadings.AllCarriers.All(c => panel.EntryBodyText.Contains(CarrierReadings.CarrierName(c)));
                Expect(ownedShown, $"已获得的“{panel.EntryTitleText}”：彩色图标，正文 = 完整详情页（5 种载体各一句读法、标签、参与的反应、获取途径、持有数量，DEBT-FG2FW02-05）");

                // 搜索：未获得的名字搜不到（不剧透），按获取途径能搜到；已获得的按名字能搜到。
                panel.SetSearch(lockedName);
                bool noSpoiler = Enumerable.Range(0, panel.ItemCount).All(i => panel.ItemId(i) != MechanicCodex.FirmwareEntryId(locked));
                panel.SetSearch(FirmwareKinds.DisplayName(owned));
                bool foundOwned = Enumerable.Range(0, panel.ItemCount).Any(i => panel.ItemId(i) == MechanicCodex.FirmwareEntryId(owned));
                panel.SetSearch("不存在的条目zz");
                bool noMatch = panel.ItemCount == 0 && panel.NoMatchVisible;
                panel.SetSearch(string.Empty);
                Expect(noSpoiler && foundOwned && noMatch, "搜索：未获得条目的名字搜不到（只按获取途径匹配，不剧透）；已获得的按名字搜到；没有结果时显示“没有符合搜索的条目”");

                // 相关条目：固件 → 反应，跨页签跳转。
                MechanicCodexEntry ownedEntry = MechanicCodex.Find(MechanicCodex.FirmwareEntryId(owned));
                panel.Select(ownedEntry.Id);
                bool jumped = false;
                string jumpedTo = null;
                if (panel.RelatedCount > 0)
                {
                    Click(panel.RelatedButton(0));
                    jumpedTo = panel.SelectedId;
                    jumped = panel.CurrentTab == MechanicCodex.TabReaction && jumpedTo == ownedEntry.Links[0];
                }
                Click(panel.TabButton(0));
                bool systemTab = panel.CurrentTab == MechanicCodex.TabSystem && panel.ItemCount == MechanicCodex.CountIn(MechanicCodex.TabSystem);
                Expect(ownedEntry.Links.Length > 0 && jumped && systemTab,
                    $"点“相关条目”从固件页签跳到反应页签的 {jumpedTo}；点“系统说明”页签回到 {panel.ItemCount} 条系统说明");
                UiEscapeStack.CloseTop();
                Expect(!MechanicCodexPanelUIToolkit.IsOpen, "Esc 关闭图鉴");
            }
            finally
            {
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── F. 悬停跳图鉴 / 按键 ──────────────────────────────────────────────

        private static void CheckHoverJumpAndKeys()
        {
            Line("  · F. 悬停跳图鉴：固件库行的提示带图鉴链接，悬停按图鉴键 → 广播 GameEvent 并打开到该条目；没悬停时图鉴键开关图鉴；拼错的条目不当链接；确认框开着不抢键；固件库键开关");
            CampaignState s = NewState(9509, unlockAll: true);
            string fw = RegularFirmware();
            PrimitiveInventory.GrantCrafted(s, fw);
            VisualElement codexRoot = MountUxml(UiKitFolder + "MechanicCodexPanel.uxml", out GameObject cgo);
            VisualElement libRoot = MountUxml(UiKitFolder + "FirmwareLibraryPanel.uxml", out GameObject lgo);
            MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
            FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            string heard = null;
            Action<string> listener = id => heard = id;
            TEngine.GameEvent.AddEventListener<string>(CodexHoverLink.JumpEvent, listener);
            try
            {
                var codex = cgo.AddComponent<MechanicCodexPanelUIToolkit>();
                codex.BindView(codexRoot);
                var lib = lgo.AddComponent<FirmwareLibraryPanelUIToolkit>();
                lib.BindView(libRoot);
                InputRouter.Reset();
                InputRouter.SetScope(InputScope.Strategy);
                InputRouter.DebugSetReader(Keys);
                bool strategy = InputRouter.ActiveContext == InputContext.Strategy;

                // 固件库键：关着 → 打开（模态，输入上下文 = 界面）。
                const string libraryEntry = "codex.firmware.library";
                bool hookUnseen = !GameSettings.HasSeenGuidanceHook(GuidanceHooks.FirmwareLibraryFirstOpen);
                bool entryLocked = MechanicCodex.Find(libraryEntry) != null && !MechanicCodex.IsUnlocked(libraryEntry);
                int raised = GuidanceHooks.RaisedCount;
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
                bool libOpen = FirmwareLibraryPanelUIToolkit.IsOpen && lib.PanelVisible && InputRouter.IsModalOwner(lib) && InputRouter.ActiveContext == InputContext.Interface;
                Expect(strategy && libOpen && lib.RowCount == 1, $"按固件库键（{InputDisplay.ForAction(GameActionId.OpenFirmware)}）打开固件库：模态、输入上下文 = 界面；1 行芯片");
                MechanicCodexEntry libEntry = MechanicCodex.Find(libraryEntry);
                Expect(hookUnseen && entryLocked && GuidanceHooks.Known.Contains(GuidanceHooks.FirmwareLibraryFirstOpen)
                       && GuidanceHooks.RaisedCount == raised + 1 && GuidanceHooks.LastRaised == GuidanceHooks.FirmwareLibraryFirstOpen
                       && GameSettings.HasSeenGuidanceHook(GuidanceHooks.FirmwareLibraryFirstOpen)
                       && libEntry != null && libEntry.Tab == MechanicCodex.TabSystem && MechanicCodex.IsUnlocked(libraryEntry)
                       && MechanicCodex.Title(libEntry) == GameText.Get("codex.firmware.library.title"),
                    $"FG00 B14：第一次打开固件库发出引导钩子 {GuidanceHooks.FirmwareLibraryFirstOpen}（已登记进 GuidanceHooks.Known），图鉴系统说明“{MechanicCodex.Title(libEntry)}”随之从剪影变为解锁");

                // 固件库的列表行：真实建出的行元素挂着提示，提示带图鉴链接。
                UiToolkitLayoutProbe.ForceLayout(libRoot);
                VisualElement row = null;
                lib.ListView.Query<VisualElement>(className: "fl-row").ForEach(r =>
                {
                    if (row == null && r.userData is int i && i == 0)
                    {
                        row = r;
                    }
                });
                string hoverText = HoverTip(row);
                string expectLink = GameText.Format("ui.common.codex_jump", InputDisplay.ForAction(GameActionId.OpenCodex), FirmwareKinds.DisplayName(fw));
                Expect(row != null && hoverText.Contains(FirmwareKinds.DisplayName(fw)) && UiTooltipCodexLine(row) == expectLink,
                    $"悬停固件库的行：提示“{Short(hoverText)}”，图鉴链接行“{UiTooltipCodexLine(row)}”（按键跟着当前绑定）");

                // 悬停时按图鉴键：跳到该固件的图鉴条目（广播 GameEvent），固件库留在下面。
                UiTooltip.NotifyEnter(row);
                int jumps = CodexHoverLink.JumpCount;
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                string entry = MechanicCodex.FirmwareEntryId(fw);
                Expect(CodexHoverLink.JumpCount == jumps + 1 && heard == entry && MechanicCodexPanelUIToolkit.IsOpen && codex.SelectedId == entry
                       && codex.CurrentTab == MechanicCodex.TabFirmware && FirmwareLibraryPanelUIToolkit.IsOpen,
                    $"悬停时按图鉴键（{InputDisplay.ForAction(GameActionId.OpenCodex)}）→ 广播 {CodexHoverLink.JumpEvent}（{heard}），图鉴打开到固件页签的该条目；固件库仍开着");
                // 跳转后提示收起（图鉴盖在上面时不再挂着旧提示）；指针离开后的宽限期里提示还显示着，也不再算悬停——否则再按图鉴键会再“跳”一次而不是关图鉴。
                bool hiddenAfterJump = !UiTooltip.IsVisible && CodexHoverLink.CurrentEntryId == null;
                string lingerTip = HoverTipKeepVisible(row);
                UiTooltip.NotifyLeave(row);
                bool lingering = UiTooltip.IsVisible && CodexHoverLink.CurrentEntryId == null;
                UiTooltip.Hide();
                Expect(hiddenAfterJump && lingerTip.Length > 0 && lingering,
                    "跳转后提示收起；鼠标离开行、提示还在离开宽限期里显示时，不再算悬停（图鉴键不会重复跳转）");
                // 图鉴开着：固件库键不关下面的固件库；图鉴键（没悬停）关图鉴。
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
                bool libStays = FirmwareLibraryPanelUIToolkit.IsOpen;
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                Expect(libStays && !MechanicCodexPanelUIToolkit.IsOpen && FirmwareLibraryPanelUIToolkit.IsOpen && CodexHoverLink.JumpCount == jumps + 1,
                    "图鉴盖在固件库上时按固件库键不关下面的固件库；不悬停时按图鉴键 = 关闭图鉴（不当成跳转）");

                // 拼错的条目 ID 不当链接：按图鉴键退回普通开关。
                var fake = new VisualElement();
                UiTooltip.Attach(fake, () => new TooltipContent { Title = "x", CodexEntryId = "fw:no_such_firmware" });
                UiTooltip.NotifyEnter(fake);
                bool noLink = CodexHoverLink.CurrentEntryId == null;
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                bool plainOpen = MechanicCodexPanelUIToolkit.IsOpen && CodexHoverLink.JumpCount == jumps + 1;
                UiTooltip.NotifyLeave(fake);
                UiTooltip.Detach(fake);
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                Expect(noLink && plainOpen && !MechanicCodexPanelUIToolkit.IsOpen, "提示里写错的条目 ID 不算图鉴链接：按图鉴键退回普通的开 / 关图鉴");

                // 确认框在最上面：两个键都不抢。
                UiConfirmDialog.Show(new ConfirmRequest { Title = "测试" });
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                bool untouched = FirmwareLibraryPanelUIToolkit.IsOpen && !MechanicCodexPanelUIToolkit.IsOpen && UiConfirmDialog.IsOpen;
                UiConfirmDialog.Cancel();
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
                Expect(untouched && !FirmwareLibraryPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(lib),
                    "确认框开着时固件库键 / 图鉴键都不响应；确认框关掉后再按固件库键关闭固件库（模态释放）");

                // 更高层的面板开着：不在它下面开出看不见的模态（Esc 会先关掉看不见的那个）。
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                bool codexUp = MechanicCodexPanelUIToolkit.IsOpen;
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
                bool libUnder = FirmwareLibraryPanelUIToolkit.IsOpen || InputRouter.IsModalOwner(lib);
                Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                Expect(codexUp && !libUnder && !MechanicCodexPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(lib),
                    "只开着图鉴时按固件库键：不在图鉴下面打开看不见的固件库（不登记模态、不进 Esc 栈）；再按图鉴键关掉图鉴");
                var sgo = new GameObject("fgfw05-stats-probe");
                try
                {
                    var stats = sgo.AddComponent<StatsPanelUIToolkit>();
                    InputRouter.PushModal(stats); // 统计面板（30076）盖在最上层
                    Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                    Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenFirmware));
                    bool blocked = !MechanicCodexPanelUIToolkit.IsOpen && !FirmwareLibraryPanelUIToolkit.IsOpen && !InputRouter.IsModalOwner(codex) && !InputRouter.IsModalOwner(lib)
                                   && UiKitPanelHost.AnyModalAbove(MechanicCodexPanelUIToolkit.Order);
                    InputRouter.PopModal(stats);
                    Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                    bool freed = MechanicCodexPanelUIToolkit.IsOpen && !UiKitPanelHost.AnyModalAbove(MechanicCodexPanelUIToolkit.Order);
                    Press(GameSettings.KeyBindings.GetKey(GameActionId.OpenCodex));
                    Expect(blocked && freed && !MechanicCodexPanelUIToolkit.IsOpen,
                        $"统计面板（{StatsPanelUIToolkit.Order}）盖在最上层时图鉴键 / 固件库键都不在它下面开面板；统计关掉后图鉴键照常开关图鉴");
                }
                finally
                {
                    Object.DestroyImmediate(sgo);
                }

                // 改键：图鉴键重绑后，提示里的按键文字跟着变，新键生效。
                GameSettings.ForceRebind(GameActionId.OpenCodex, new InputChord(KeyCode.F9));
                string rebound = GameText.Format("ui.common.codex_jump", InputDisplay.ForAction(GameActionId.OpenCodex), FirmwareKinds.DisplayName(fw));
                FirmwareLibraryPanelUIToolkit.Open();
                UiToolkitLayoutProbe.ForceLayout(libRoot);
                UiTooltip.NotifyEnter(row);
                Press(KeyCode.F9);
                bool reboundJump = MechanicCodexPanelUIToolkit.IsOpen && codex.SelectedId == entry;
                GameSettings.ResetKeyBindingsToDefault();
                Expect(rebound.Contains(InputDisplay.Key(KeyCode.F9)) && reboundJump, $"图鉴键改成 F9：链接行写“{rebound}”，悬停按 F9 跳转");
                UiTooltip.NotifyLeave(row);
                MechanicCodexPanelUIToolkit.Close();
                FirmwareLibraryPanelUIToolkit.Close();
            }
            finally
            {
                TEngine.GameEvent.RemoveEventListener<string>(CodexHoverLink.JumpEvent, listener);
                GameSettings.ResetKeyBindingsToDefault();
                UiConfirmDialog.ResetForTests();
                UiTooltip.ResetForTests();
                InputRouter.DebugSetReader(null);
                InputRouter.Reset();
                MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(cgo);
                Object.DestroyImmediate(lgo);
            }
        }

        // ── G. 存读档 ───────────────────────────────────────────────────────────

        private static void CheckSaveLoad()
        {
            Line("  · G. 存读档：锁定 / 来源 / 获得时刻真实存档往返；旧档没有字段 = 未锁定、来源未记录");
            CampaignState s = NewState(9510, unlockAll: true);
            s.PlaySeconds = 123.5f;
            string a = PrimitiveInventory.GrantCrafted(s, FirmwareCatalog.FwOverloadId);
            FirmwareLibrary.TrySetLocked(s, a, true);
            string enc = FirmwareKinds.Rows.Where(r => r != null && FirmwareKinds.IsEnemyProtocol(r.Id)).Select(r => r.Id).First();
            string b = PrimitiveInventory.TryGrantEncryptedFirmware(s, "fgfw05-salvage", enc, out _);
            SaveResult saved = CampaignSaveService.Save(Slot, s, SaveReason.Manual);
            LoadResult loaded = CampaignSaveService.Load(Slot);
            PrimitiveChipRecord la = loaded.State != null ? PrimitiveInventory.Find(loaded.State, a) : null;
            PrimitiveChipRecord lb = loaded.State != null ? PrimitiveInventory.Find(loaded.State, b) : null;
            bool ok = saved.Success && loaded.Success && la != null && la.Locked && la.Origin == PrimitiveInventory.OriginCraft && la.AcquiredTick == 123500
                      && lb != null && !lb.Locked && lb.Origin == PrimitiveInventory.OriginEncrypted;
            var plan = FirmwareLibrary.PlanDisassemble(loaded.State, new[] { a, b });
            Expect(ok && plan.SkippedLocked == 1 && plan.Eligible.Count == 1,
                "真实存档 → 读档：锁定、来源（合成台 / 带回的加密固件）、获得时刻都在；读档后批量分解照样跳过锁定的那枚");
            PrimitiveChipRecord legacy = JsonUtility.FromJson<PrimitiveChipRecord>("{\"PartId\":\"old\",\"CardDefId\":\"" + FirmwareCatalog.FwOverloadId + "\",\"State\":0,\"DraftSlot\":-1}");
            Expect(legacy != null && !legacy.Locked && string.IsNullOrEmpty(legacy.Origin) && legacy.AcquiredTick == 0
                   && FirmwareLibrary.OriginText(legacy.Origin) == GameText.Get("fwlib.origin.unknown"),
                $"旧存档的芯片没有新字段：未锁定、来源显示“{FirmwareLibrary.OriginText(legacy?.Origin)}”");
        }

        // ── H. 取用路线 ─────────────────────────────────────────────────────────

        private static void CheckRoute()
        {
            Line("  · H. 取用路线（负向“装配时固件所在的仓库路线被堵”）：真实家园里仓储 → 装配站通畅；装配站被悬崖围住 → 被堵并写明被什么堵住；隔断 → 没有通路");
            CampaignState s = NewHome(9511);
            FirmwareRouteInfo r0 = FirmwareLibrary.EvaluateRoute(s, forceFresh: true);
            Expect(!r0.NoStation && r0.Ok && FirmwareLibrary.RouteText(r0) == GameText.Format("fwlib.detail.route_ok", r0.StorageName),
                $"开局家园：“{FirmwareLibrary.RouteText(r0)}”");
            BuildingRecord station = s.BuildingRecords.First(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeAssemblyStation);
            GridContent.TryGetBuilding(station.BuildingTypeId, out GameConfig.fg.BuildingGrid g);
            GridMath.FootprintBounds(new GridCell(station.GridX, station.GridY), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(station.Rotation), out GridCell min, out GridCell max);
            HomeGridMap map = HomeGridService.MapFor(s);
            byte cliff = GridContent.TerrainCode("cliff");
            // 隔断：离装配站 3 格外画一圈悬崖（两边四周都有空地，但彼此走不通）。仓储在圈里时这一项不适用。
            BuildingRecord storage = s.BuildingRecords.FirstOrDefault(b => b != null && b.RegionId == HomeValleyLayout.RegionId
                && (b.BuildingTypeId == HomeValleyLayout.BuildingTypeWarehouse && b.ConstructionState == BuildingConstructionState.Operational));
            storage ??= s.BuildingRecords.First(b => b != null && b.RegionId == HomeValleyLayout.RegionId && b.BuildingTypeId == HomeValleyLayout.BuildingTypeCore);
            int gap = 3;
            bool storageInside = storage.GridX >= min.X - gap && storage.GridX <= max.X + gap && storage.GridY >= min.Y - gap && storage.GridY <= max.Y + gap;
            if (!storageInside)
            {
                Ring(s, map, min, max, gap + 1, cliff);
                FirmwareRouteInfo r1 = FirmwareLibrary.EvaluateRoute(s, forceFresh: true);
                Expect(r1.Reach == NavService.BuildingReach.Disconnected && !r1.Ok && FirmwareLibrary.RouteText(r1).Contains(GameText.Get("fwlib.route.disconnected")),
                    $"离装配站 {gap + 1} 格外一圈悬崖：两边四周都有空地但走不通 →“{FirmwareLibrary.RouteText(r1)}”");
            }
            else
            {
                Line($"    · 仓储在装配站 {gap} 格内，“隔断”一项不适用（本种子）");
            }
            // 装配站四周紧贴一圈悬崖：目标被围住，说明被什么堵住。
            Ring(s, map, min, max, 1, cliff);
            FirmwareRouteInfo r2 = FirmwareLibrary.EvaluateRoute(s, forceFresh: true);
            string text = FirmwareLibrary.RouteText(r2);
            Expect(r2.Reach == NavService.BuildingReach.ToEnclosed && r2.StationBlockers.Contains(GameText.Get("fwlib.route.terrain"))
                   && text == GameText.Format("fwlib.detail.route_blocked", r2.StorageName, GameText.Format("fwlib.route.station_enclosed", string.Join(FirmwareLibrary.Sep, r2.StationBlockers))),
                $"装配站四周紧贴一圈悬崖 → 被堵：“{text}”");
            // 缓存：不强制刷新时按间隔返回同一结果；换状态立即失效。
            FirmwareRouteInfo cached = FirmwareLibrary.EvaluateRoute(s);
            FirmwareRouteInfo none = FirmwareLibrary.EvaluateRoute(NewState(9512, unlockAll: false));
            Expect(ReferenceEquals(cached, r2) && (none.NoStation || none.Reach == NavService.BuildingReach.Unknown),
                $"检查结果按 firmware.library.route_cache_seconds 缓存（只在详情页显示时检查，不按帧）；没有家园 / 装配站的存档：“{FirmwareLibrary.RouteText(none)}”");
            WorldSimulation.UnloadAll();
        }

        private static void Ring(CampaignState s, HomeGridMap map, GridCell min, GridCell max, int d, byte terrain)
        {
            for (int x = min.X - d; x <= max.X + d; x++)
            {
                for (int y = min.Y - d; y <= max.Y + d; y++)
                {
                    bool edge = x == min.X - d || x == max.X + d || y == min.Y - d || y == max.Y + d;
                    var c = new GridCell(x, y);
                    if (edge && HomeGridService.BuildingAt(s, c) == null)
                    {
                        map.SetTerrain(c, terrain);
                    }
                }
            }
        }

        // ── I. 性能 ─────────────────────────────────────────────────────────────

        private static void CheckPerf()
        {
            Line("  · I. 性能：1000 枚芯片的查询 / 分解计划耗时（Editor batchmode，托管代码未经 IL2CPP，真机通常更快）；面板键不变时刷新 O(1)");
            CampaignState s = NewState(9513, unlockAll: true);
            var ids = FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _)).Select(r => r.Id).ToList();
            var list = new List<PrimitiveChipRecord>(s.PrimitiveChips);
            for (int i = 0; i < 1000; i++)
            {
                list.Add(new PrimitiveChipRecord { PartId = "perf" + i, CardDefId = ids[i % ids.Count], State = PrimitiveChipState.Bag, DraftSlot = -1, AcquiredTick = i });
            }
            s.PrimitiveChips = list.ToArray();
            var rows = new List<FirmwareLibraryRow>(1100);
            var filter = new FirmwareLibraryFilter { Kind = FirmwareKind.Regular, Carrier = FirmwareCarrier.Projectile, Search = "a", Sort = FirmwareLibrarySort.Rarity };
            FirmwareLibrary.Query(s, filter, rows); // 预热
            var sw = Stopwatch.StartNew();
            const int runs = 20;
            for (int i = 0; i < runs; i++)
            {
                FirmwareLibrary.Query(s, new FirmwareLibraryFilter { Sort = FirmwareLibrarySort.Newest }, rows);
            }
            double queryMs = sw.Elapsed.TotalMilliseconds / runs;
            sw.Restart();
            for (int i = 0; i < runs; i++)
            {
                FirmwareLibrary.Query(s, filter, rows);
            }
            double filteredMs = sw.Elapsed.TotalMilliseconds / runs;
            sw.Restart();
            FirmwareDisassemblePlan plan = FirmwareLibrary.PlanDisassemble(s, list.Select(p => p.PartId));
            double planMs = sw.Elapsed.TotalMilliseconds;
            Line($"    · 1000 枚：全部列出 + 排序 {queryMs:0.00} ms / 次，4 个条件 + 搜索 + 排序 {filteredMs:0.00} ms / 次，分解计划 {planMs:0.00} ms");
            // 预算：固件库只在面板打开时每 0.5 秒（或玩家操作后）查询一次；按 120 帧的 8.33 ms 帧预算，单次查询不应超过 4 ms。
            Expect(queryMs < 4.0 && filteredMs < 4.0 && planMs < 20.0 && plan.Eligible.Count == 1000,
                $"1000 枚芯片：查询 {queryMs:0.00} / {filteredMs:0.00} ms（< 4 ms，只在打开时按间隔或操作后查一次），分解计划 {planMs:0.00} ms（只在点按钮时算一次）");

            VisualElement root = MountUxml(UiKitFolder + "FirmwareLibraryPanel.uxml", out GameObject go);
            FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var panel = go.AddComponent<FirmwareLibraryPanelUIToolkit>();
                panel.BindView(root);
                panel.Refresh();
                sw.Restart();
                for (int i = 0; i < 1000; i++)
                {
                    panel.Refresh();
                }
                double idleUs = sw.Elapsed.TotalMilliseconds; // 1000 次的总毫秒 = 每次的微秒
                Expect(idleUs / 1000.0 < 0.05 && panel.RowCount == 1000 + 0,
                    $"面板打开、数据没变时每帧的刷新只比较版本号：{idleUs:0.0} 微秒 / 次（与芯片数无关）；列表虚拟化，只为可见行建元素");
            }
            finally
            {
                FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(go);
            }
        }

        // ── J. 界面 ─────────────────────────────────────────────────────────────

        private static void CheckLibraryPanel()
        {
            Line("  · J1. 固件库面板：数量与容量、下拉筛选即选即生效、空状态、详情、锁定、比较、领取、批量分解确认框（取消零变化 / 确认才执行）、暂停菜单入口");
            CampaignState s = NewState(9514, unlockAll: true);
            VisualElement root = MountUxml(UiKitFolder + "FirmwareLibraryPanel.uxml", out GameObject go);
            FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            double now = 0;
            FirmwareLibraryPanelUIToolkit.Clock = () => now;
            try
            {
                var panel = go.AddComponent<FirmwareLibraryPanelUIToolkit>();
                panel.BindView(root);
                FirmwareLibraryPanelUIToolkit.Open();
                bool empty = FirmwareLibraryPanelUIToolkit.IsOpen && panel.RowCount == 0 && panel.EmptyText == GameText.Get("fwlib.empty") && panel.WarnText.Length == 0
                             && panel.DetailTitleText == GameText.Get("fwlib.detail.none") && !panel.ActionsVisible;
                Expect(empty, $"没有固件芯片：空状态“{panel.EmptyText}”，详情提示“{panel.DetailTitleText}”，没有操作按钮");

                // 常规固件（有 5 种载体读法、能装进机器）与核心固件各取一条；注意“过载”本身是核心固件。
                string overload = RegularFirmware();
                string core = FirmwareKinds.CoreRosterIds.First(id => FirmwareCatalog.TryGet(id, out _));
                string a = PrimitiveInventory.GrantCrafted(s, overload);
                string b = PrimitiveInventory.GrantCrafted(s, core);
                string c = PrimitiveInventory.GrantCrafted(s, overload);
                panel.Refresh();
                int cap = PrimitiveInventory.CapacityOf(s);
                bool listed = panel.RowCount == 3 && panel.CountText == GameText.Format("fwlib.count", 3, 3, PrimitiveInventory.BagCount(s), cap);
                Expect(listed, $"芯片入账后版本号变化，面板立即重建：“{panel.CountText}”");

                // 下拉：选“核心”即生效（与玩家在下拉里选同一路径：改 value 触发回调）。
                panel.KindDropdown.value = panel.KindDropdown.choices[2];
                bool coreOnly = panel.RowCount == 1 && panel.Row(0).FirmwareId == core;
                panel.KindDropdown.value = panel.KindDropdown.choices[0];
                panel.CarrierDropdown.value = panel.CarrierDropdown.choices[1];
                bool carrierOk = panel.RowCount == 2 && Enumerable.Range(0, panel.RowCount).All(i => panel.Row(i).FirmwareId == overload);
                panel.CarrierDropdown.value = panel.CarrierDropdown.choices[0];
                bool choices = panel.CategoryDropdown.choices.Count == 5 && panel.KindDropdown.choices.Count == 3 && panel.ProtocolDropdown.choices.Count == 3
                               && panel.CrackedDropdown.choices.Count == 3 && panel.CarrierDropdown.choices.Count == 6 && panel.RarityDropdown.choices.Count == 4
                               && panel.SortDropdown.choices.Count == 6 && panel.ProtocolDropdown.choices.All(x => !x.Contains("/"));
                panel.SetSearchText("不存在的固件名zz");
                bool filteredEmpty = panel.RowCount == 0 && panel.EmptyText == GameText.Get("fwlib.empty_filtered");
                panel.SetSearchText(string.Empty);
                Line($"    · 诊断：核心 {coreOnly}（{panel.KindDropdown.index}）、载体 {carrierOk}、选项 {choices}（{panel.CategoryDropdown.choices.Count}/{panel.KindDropdown.choices.Count}/{panel.ProtocolDropdown.choices.Count}/{panel.CrackedDropdown.choices.Count}/{panel.CarrierDropdown.choices.Count}/{panel.RarityDropdown.choices.Count}/{panel.SortDropdown.choices.Count}）、空 {filteredEmpty}、行 {panel.RowCount}");
                Expect(coreOnly && carrierOk && choices && filteredEmpty && panel.RowCount == 3,
                    "下拉选“核心”只剩核心固件、选“兼容：射弹”核心固件被排除（即选即生效）；7 个下拉的选项数正确、没有会被当成子菜单的“/”；搜不到时显示“没有符合筛选条件的芯片”");

                // 详情 + 锁定。
                panel.Select(a);
                bool detail = panel.DetailTitleText == FirmwareKinds.DisplayName(overload) && panel.DetailBodyText.Contains(GameText.Get("fwlib.readings_title"))
                              && panel.DetailBodyText.Contains(GameText.Format("fwlib.detail.origin", GameText.Get("fwlib.origin.craft")))
                              && panel.DetailBodyText.Contains(GameText.Format("fwlib.detail.held", 2, GameText.Get("fwlib.loc.storage") + " ×2")) && panel.RouteText.Length > 0
                              && panel.ActionsVisible && !panel.ClaimVisible;
                Click(panel.LockButton);
                bool lockedNow = PrimitiveInventory.Find(s, a).Locked && panel.LockButton.text == GameText.Get("fwlib.unlock") && panel.DetailTitleText.Contains(GameText.Get("fwlib.row.locked"))
                                 && panel.RowText(Enumerable.Range(0, panel.RowCount).First(i => panel.Row(i).Chip.PartId == a)).Contains(GameText.Get("fwlib.row.locked"));
                Line($"    · 诊断：详情 {detail}、锁定 {lockedNow}；正文“{Short(panel.DetailBodyText)}”…；路线“{panel.RouteText}”；操作 {panel.ActionsVisible}、领取 {panel.ClaimVisible}");
                Expect(detail && lockedNow,
                    $"详情（常规固件）：“{panel.DetailTitleText}”——各载体读法、这一枚的来源、持有 2 枚（仓储 ×2）、取用路线；点“锁定”→ 行与标题标［锁定］，按钮变“解除锁定”");

                // 比较。
                Click(panel.CompareAButton);
                panel.Select(b);
                Click(panel.CompareBButton);
                int n = panel.CompareRowCount;
                string cmp0 = panel.CompareRowText(0);
                bool diff0 = panel.CompareRowDiff(0);
                bool compare = n >= 11 && panel.CompareEmptyText.Length == 0 && panel.CompareRowText(0).Contains(FirmwareKinds.DisplayName(overload))
                               && panel.CompareRowText(0).Contains(FirmwareKinds.DisplayName(core)) && panel.CompareRowDiff(0);
                Click(panel.CompareClearButton);
                Line($"    · 诊断：比较首行“{cmp0}”，差异高亮 {diff0}");
                Expect(compare && panel.CompareRowCount == 0 && panel.CompareEmptyText.Length > 0,
                    $"两条固件并排比较 {n} 行（名称、种类、类别、协议、稀有度、负载、5 种载体读法、标签），不同的高亮；“清除比较”复原");

                // 批量分解：全选 → 点“分解所选”→ 确认框（写明后果），取消零变化；再点 → 确认 → 只分解没锁的。
                Click(panel.SelectAllButton);
                int chips0 = s.PrimitiveChips.Length;
                float scrap0 = s.Scrap;
                Click(panel.DisassembleButton);
                ConfirmRequest req = UiConfirmDialog.Current;
                bool asked = UiConfirmDialog.IsOpen && req != null && req.Irreversible
                             && req.Consequences.Contains(GameText.Format("fwlib.confirm.lose", 2, 2 * FirmwareLibrary.DisassembleScrap))
                             && req.Lines.Contains(GameText.Format("fwlib.confirm.skip_locked", 1)) && s.PrimitiveChips.Length == chips0
                             && req.Lines.Any(l => l.StartsWith(GameText.Format("fwlib.confirm.names", string.Empty), StringComparison.Ordinal) && l.Contains("×"))
                             && !req.Lines.Any(l => l == GameText.Format("fwlib.confirm.hidden", 1) || l == GameText.Format("fwlib.confirm.hidden", 2));
                UiConfirmDialog.Cancel();
                bool cancelled = !UiConfirmDialog.IsOpen && s.PrimitiveChips.Length == chips0 && Math.Abs(s.Scrap - scrap0) < 1e-3f && panel.PickedCount == 3;
                Click(panel.DisassembleButton);
                int ack0 = GameLogic.Campaign.Feedback.FeedbackCues.CountOf(GameLogic.Campaign.Feedback.FeedbackCueId.CommandAck);
                UiConfirmDialog.Confirm();
                bool acked = GameLogic.Campaign.Feedback.FeedbackCues.CountOf(GameLogic.Campaign.Feedback.FeedbackCueId.CommandAck) == ack0 + 1;
                bool done = s.PrimitiveChips.Length == chips0 - 2 && PrimitiveInventory.Find(s, a) != null && Math.Abs(s.Scrap - scrap0 - 2 * FirmwareLibrary.DisassembleScrap) < 1e-3f
                            && panel.FeedbackText == GameText.Format("fwlib.result.done", 2, 2 * FirmwareLibrary.DisassembleScrap, 1) && panel.RowCount == 1 && panel.PickedCount == 1;
                Expect(asked && cancelled && done && acked,
                    $"全选 3 枚点“分解所选”：先弹确认框（“{req?.Consequences.FirstOrDefault()}”，“{req?.Lines.FirstOrDefault()}”，不可撤销），此时一枚未动；取消零变化；确认后只分解没锁的 2 枚，提示“{panel.FeedbackText}”");
                // 只剩锁定的：点“分解所选”不弹框，直接说明（拒绝音 + 文字）。
                int deny0 = GameLogic.Campaign.Feedback.FeedbackCues.CountOf(GameLogic.Campaign.Feedback.FeedbackCueId.Denied);
                Click(panel.DisassembleButton);
                Expect(!UiConfirmDialog.IsOpen && panel.FeedbackText == GameText.Get("fwlib.result.nothing") && PrimitiveInventory.Find(s, a) != null
                       && GameLogic.Campaign.Feedback.FeedbackCues.CountOf(GameLogic.Campaign.Feedback.FeedbackCueId.Denied) == deny0 + 1,
                    $"所选的都已锁定：不弹确认框，拒绝音 + 提示“{panel.FeedbackText}”；分解成功时响命令确认音");

                // 勾选的芯片被筛选挡住（看不见）：确认框按名称列出要拆的是什么，并写明“其中 N 枚不在当前筛选结果里”；关掉面板再打开，勾选清空。
                string hiddenPick = PrimitiveInventory.GrantCrafted(s, core);
                panel.Refresh();
                panel.ClearPicks();
                panel.TogglePick(hiddenPick);
                panel.KindDropdown.value = panel.KindDropdown.choices[1]; // 只看常规固件：刚勾选的核心固件被筛掉
                bool pickHidden = Enumerable.Range(0, panel.RowCount).All(i => panel.Row(i).Chip.PartId != hiddenPick) && panel.PickedCount == 1;
                Click(panel.DisassembleButton);
                ConfirmRequest hreq = UiConfirmDialog.Current;
                string namesLine = GameText.Format("fwlib.confirm.names", GameText.Format("fwlib.confirm.name_count", FirmwareKinds.DisplayName(core), 1));
                bool hiddenWarned = UiConfirmDialog.IsOpen && hreq != null && hreq.Lines.Contains(GameText.Format("fwlib.confirm.hidden", 1)) && hreq.Lines.Contains(namesLine);
                UiConfirmDialog.Cancel();
                panel.KindDropdown.value = panel.KindDropdown.choices[0];
                panel.SetOpen(false);
                bool clearedOnClose = panel.PickedCount == 0;
                panel.SetOpen(true);
                bool reopenedClean = panel.PickedCount == 0 && PrimitiveInventory.Find(s, hiddenPick) != null && !panel.DisassembleButton.enabledSelf;
                Expect(pickHidden && hiddenWarned && clearedOnClose && reopenedClean,
                    $"勾选后换筛选把它挡住：确认框写“{namesLine}”与“{GameText.Format("fwlib.confirm.hidden", 1)}”；取消后关掉面板再打开，勾选清空、“分解所选”不可点、芯片未动");
                string manyNames = FirmwareLibraryPanelUIToolkit.SummarizeNames(s, s.PrimitiveChips.Where(c => c != null && FirmwareKinds.IsFirmware(c.CardDefId)).Select(c => c.PartId).ToList());
                int kinds = s.PrimitiveChips.Where(c => c != null && FirmwareKinds.IsFirmware(c.CardDefId)).Select(c => c.CardDefId).Distinct().Count();
                Expect(manyNames.Length > 0 && (kinds <= FirmwareLibraryPanelUIToolkit.ConfirmNameGroups
                           || manyNames.EndsWith(GameText.Format("fwlib.confirm.name_more", kinds - FirmwareLibraryPanelUIToolkit.ConfirmNameGroups), StringComparison.Ordinal)),
                    $"确认框的名称汇总最多列 {FirmwareLibraryPanelUIToolkit.ConfirmNameGroups} 种、按件数从多到少，其余写“等另外 N 种”：“{Short(manyNames)}”");

                // 领取：待领取的芯片显示“领取进仓储”。
                string p = PrimitiveInventory.GrantCrafted(s, overload);
                PrimitiveInventory.Find(s, p).State = PrimitiveChipState.Pending;
                panel.Refresh();
                panel.Select(p);
                bool claimShown = panel.ClaimVisible && panel.RowText(Enumerable.Range(0, panel.RowCount).First(i => panel.Row(i).Chip.PartId == p)).Contains(GameText.Get("fwlib.loc.pending"));
                Click(panel.ClaimButton);
                Expect(claimShown && PrimitiveInventory.Find(s, p).State == PrimitiveChipState.Bag && !panel.ClaimVisible, "待领取的芯片：位置写“待领取”，详情里有“领取进仓储”，点了进仓储");

                // 满仓警告：由定时重读发现（仓储满不在芯片版本号里时也能跟上）。
                while (!PrimitiveInventory.IsFull(s))
                {
                    PrimitiveInventory.GrantCrafted(s, overload);
                }
                now += FirmwareLibrary.RefreshSeconds + 0.01;
                panel.Tick(now);
                Expect(panel.WarnText == GameText.Get("fwlib.over_capacity"), $"仓储满：页眉下写“{panel.WarnText}”");

                // 英文：文字全部走文本键，没有漏键标记。
                GameSettings.SetLanguage(GameLanguage.En);
                FirmwareCatalog.Invalidate();
                panel.Refresh();
                string en = panel.CountText + panel.FooterText + panel.DetailBodyText + panel.KindDropdown.label + string.Join("", panel.SortDropdown.choices);
                bool english = !GameText.ContainsMarker(en) && panel.KindDropdown.label == "Kind" && panel.FooterText.Contains(InputDisplay.ForAction(GameActionId.OpenCodex));
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                FirmwareCatalog.Invalidate();
                panel.Refresh();
                Expect(english && panel.KindDropdown.label == GameText.Get("fwlib.filter.kind"), "切英文：页眉、下拉、详情、脚注全部换语言、没有漏键；脚注里的图鉴键跟着当前绑定");

                Click(panel.CloseButton);
                Expect(!FirmwareLibraryPanelUIToolkit.IsOpen && !panel.PanelVisible && !InputRouter.IsModalOwner(panel), "点关闭：面板收起、模态释放");
                FirmwareLibraryPanelUIToolkit.Open();
                UiEscapeStack.CloseTop();
                Expect(!FirmwareLibraryPanelUIToolkit.IsOpen, "Esc 关闭固件库");
            }
            finally
            {
                UiConfirmDialog.ResetForTests();
                FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                FirmwareLibraryPanelUIToolkit.Clock = () => Time.realtimeSinceStartupAsDouble;
                Object.DestroyImmediate(go);
            }

            // 暂停菜单入口。
            VisualElement pauseRoot = MountUxml(UiKitFolder + "PauseMenu.uxml", out GameObject pgo);
            VisualElement libRoot = MountUxml(UiKitFolder + "FirmwareLibraryPanel.uxml", out GameObject lgo);
            FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = true;
            try
            {
                var pause = pgo.AddComponent<PauseMenuUIToolkit>();
                pause.BindView(pauseRoot);
                var lib = lgo.AddComponent<FirmwareLibraryPanelUIToolkit>();
                lib.BindView(libRoot);
                Click(pause.FirmwareButton);
                Expect(pause.FirmwareButton?.text == GameText.Get("pause.firmware") && FirmwareLibraryPanelUIToolkit.IsOpen && lib.PanelVisible,
                    $"暂停菜单“{pause.FirmwareButton?.text}”按钮打开固件库");
                FirmwareLibraryPanelUIToolkit.Close();
            }
            finally
            {
                FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                Object.DestroyImmediate(pgo);
                Object.DestroyImmediate(lgo);
            }
        }

        private static void CheckLayout()
        {
            Line("  · J2. 布局探针：固件库（中英、缩放极值、四种分辨率、超长文字压测）与图鉴固件页签");
            CampaignState s = NewState(9515, unlockAll: true);
            foreach (string id in FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _)).Select(r => r.Id).Take(12))
            {
                PrimitiveInventory.GrantCrafted(s, id);
            }
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                FirmwareCatalog.Invalidate();
                foreach (float scale in new[] { UiTuningValues.Get("ui.scale_min"), 1f, UiTuningValues.Get("ui.scale_max") })
                {
                    string result = UiToolkitLayoutProbe.Probe(UiKitFolder + "FirmwareLibraryPanel.uxml", "FwLibWindow", stressFill: true, prepare: r =>
                    {
                        var probeGo = new GameObject("__probe_fwlib") { hideFlags = HideFlags.HideAndDontSave };
                        FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = true;
                        FirmwareLibraryPanelUIToolkit p = probeGo.AddComponent<FirmwareLibraryPanelUIToolkit>();
                        p.BindView(r.panel.visualTree);
                        p.Refresh();
                        if (p.RowCount > 1)
                        {
                            p.Select(p.Row(0).Chip.PartId);
                            p.SetCompare(true);
                            p.Select(p.Row(1).Chip.PartId);
                            p.SetCompare(false);
                            p.TogglePick(p.Row(1).Chip.PartId);
                        }
                        Object.DestroyImmediate(probeGo);
                        FirmwareLibraryPanelUIToolkit.InWorldOverrideForTests = false;
                        r.panel.visualTree.Q<VisualElement>("FwLibRoot")?.RemoveFromClassList("uk-hidden");
                    }, uiScale: scale);
                    bool pass = result.StartsWith("PASS", StringComparison.Ordinal);
                    Expect(pass, $"布局探针 FirmwareLibraryPanel.uxml#FwLibWindow [{lang}] 缩放 {scale:0.##}：{(pass ? "PASS" : result.Replace("\n", " | ").Substring(0, Math.Min(600, result.Length)))}");
                }
                string codex = UiToolkitLayoutProbe.Probe(UiKitFolder + "MechanicCodexPanel.uxml", "CodexWindow", stressFill: true, prepare: r =>
                {
                    var probeGo = new GameObject("__probe_codex_fw") { hideFlags = HideFlags.HideAndDontSave };
                    MechanicCodexPanelUIToolkit.InWorldOverrideForTests = true;
                    MechanicCodexPanelUIToolkit p = probeGo.AddComponent<MechanicCodexPanelUIToolkit>();
                    p.BindView(r.panel.visualTree);
                    p.OpenAt(MechanicCodex.FirmwareEntryId(FirmwareCatalog.FwOverloadId));
                    p.SetOpen(false);
                    Object.DestroyImmediate(probeGo);
                    MechanicCodexPanelUIToolkit.InWorldOverrideForTests = false;
                    r.panel.visualTree.Q<VisualElement>("CodexRoot")?.RemoveFromClassList("uk-hidden");
                });
                bool ok = codex.StartsWith("PASS", StringComparison.Ordinal);
                Expect(ok, $"布局探针 MechanicCodexPanel.uxml#CodexWindow（固件页签、完整详情正文）[{lang}]：{(ok ? "PASS" : codex.Replace("\n", " | ").Substring(0, Math.Min(600, codex.Length)))}");
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            FirmwareCatalog.Invalidate();
        }

        // ── 工具 ────────────────────────────────────────────────────────────────

        /// <summary>一条常规固件（5 种载体都有读法），按表顺序取第一条。</summary>
        private static string RegularFirmware() =>
            FirmwareKinds.Rows.Where(r => r != null && FirmwareCatalog.TryGet(r.Id, out _) && FirmwareKinds.KindOf(r.Id) == FirmwareKind.Regular
                                          && CarrierReadings.AllCarriers.All(c => !string.IsNullOrEmpty(FirmwareKinds.Reading(r.Id, c)))).Select(r => r.Id).First();

        private static CampaignState NewState(int seed, bool unlockAll)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgfw05-" + seed, "Standard", seed);
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

        private static CampaignState NewHome(int seed)
        {
            WorldSimulation.UnloadAll();
            GameClock.ResetSession();
            MachineRegistry.ResetForNewCampaign();
            MachineLoadoutRegistry.Clear();
            HomeGridService.Invalidate();
            InputRouter.Reset();
            CampaignState s = CampaignState.CreateNew("fgfw05-home-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            HomeValleyFactory.EnsureBlueprintsSeeded(s);
            WorldSimulation.LoadHome(resume: false);
            return s;
        }

        private static string UiTooltipCodexLine(VisualElement target)
        {
            if (target == null)
            {
                return string.Empty;
            }
            Func<float> clock0 = UiTooltip.Clock;
            float t = 10000f;
            try
            {
                UiTooltip.Clock = () => t;
                UiTooltip.Hide();
                UiTooltip.NotifyEnter(target);
                t += 1f;
                UiTooltip.Tick();
                TooltipContent c = UiTooltip.Content;
                return c?.CodexEntryId != null
                    ? GameText.Format("ui.common.codex_jump", InputDisplay.ForAction(GameActionId.OpenCodex), CodexHoverLink.EntryTitle(c.CodexEntryId))
                    : string.Empty;
            }
            finally
            {
                UiTooltip.NotifyLeave(target);
                UiTooltip.Hide();
                UiTooltip.Clock = clock0;
            }
        }

        /// <summary>让提示真的显示出来并保持显示（不离开、不收起），返回标题。</summary>
        private static string HoverTipKeepVisible(VisualElement target)
        {
            if (target == null)
            {
                return string.Empty;
            }
            Func<float> clock0 = UiTooltip.Clock;
            float t = 20000f;
            UiTooltip.Clock = () => t;
            UiTooltip.NotifyEnter(target);
            t += 1f;
            UiTooltip.Tick();
            UiTooltip.Clock = clock0;
            return UiTooltip.Content?.Title ?? string.Empty;
        }

        private static string HoverTip(VisualElement target)
        {
            Func<float> clock0 = UiTooltip.Clock;
            Func<bool> pin0 = UiTooltip.PinHeld;
            float t = 10000f;
            try
            {
                UiTooltip.Clock = () => t;
                UiTooltip.PinHeld = () => false;
                UiTooltip.Hide();
                if (target == null)
                {
                    return string.Empty;
                }
                UiTooltip.NotifyEnter(target);
                t += 1f;
                UiTooltip.Tick();
                TooltipContent c = UiTooltip.Content;
                return c == null ? string.Empty : (c.Title ?? string.Empty) + "\n" + (c.Body ?? string.Empty);
            }
            finally
            {
                if (target != null)
                {
                    UiTooltip.NotifyLeave(target);
                }
                UiTooltip.Hide();
                UiTooltip.Clock = clock0;
                UiTooltip.PinHeld = pin0;
            }
        }

        private static void Press(KeyCode key)
        {
            InputRouter.DebugClearConsumedKeys();
            Keys.Down = key;
            UiKitInputPump.ProcessLibraryKeys();
            Keys.Down = KeyCode.None;
            InputRouter.DebugClearConsumedKeys();
        }

        private static string Short(string t)
        {
            string x = (t ?? string.Empty).Replace("\n", " / ");
            return x.Length > 80 ? x.Substring(0, 80) + "…" : x;
        }

        private static VisualElement MountUxml(string uxmlPath, out GameObject go)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(UiToolkitLayoutProbe.DefaultPanelSettingsPath));
            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.targetTexture = new RenderTexture(1920, 1080, 0) { hideFlags = HideFlags.HideAndDontSave };
            go = new GameObject("__FgFirmwareLibrarySelfCheck") { hideFlags = HideFlags.HideAndDontSave };
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

        private sealed class Reader : IInputReader
        {
            public KeyCode Down = KeyCode.None;
            public bool GetKey(KeyCode key) => key == Down;
            public bool GetKeyDown(KeyCode key) => key == Down;
            public bool GetMouseButtonDown(int button) => false;
            public bool GetMouseButtonUp(int button) => false;
            public Vector3 MousePosition => new Vector3(-10f, -10f, 0f);
            public float MouseScrollDelta => 0f;
        }

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
