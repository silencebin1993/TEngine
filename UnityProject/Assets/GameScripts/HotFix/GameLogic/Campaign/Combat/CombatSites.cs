using System;
using System.Collections.Generic;
using System.Linq;
using BinGames.Sim.Combat;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Regions;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Combat
{
    /// <summary>
    /// FG0-ARCH-03：已载入地点的战斗内核登记表（世界级）。地点载入时 <see cref="Open"/>、卸载时 <see cref="Close"/>；
    /// 机器阵亡、装配变化这两类全局事件由这里分发给机器所在的地点（O(地点数)，与单位数无关）。
    /// 存档的唯一写入口 <see cref="WriteTo"/>（WorldSimulation.SyncAllForSave 调它）。
    /// </summary>
    public static class CombatSites
    {
        private static readonly Dictionary<string, CombatSite> Sites = new Dictionary<string, CombatSite>(StringComparer.Ordinal);
        private static bool _hooked;

        public static IEnumerable<CombatSite> All => Sites.Values;
        public static int Count => Sites.Count;

        public static CombatSite Get(string siteId) =>
            siteId != null && Sites.TryGetValue(siteId, out CombatSite s) && !s.IsDisposed ? s : null;

        /// <summary>
        /// 打开一个地点的战斗内核。<paramref name="resume"/>（读档恢复）时从存档快照恢复；快照损坏 / 格式不认识时如实通知并按记录重建。
        /// 不是恢复（新派遣、新战役）时丢弃该地点残留的旧快照。返回的地点由调用方与记录对账（补齐缺的机器 / 敌人）。
        /// </summary>
        public static CombatSite Open(string siteId, CampaignState state, bool resume, CombatSiteRules rules, out bool restored)
        {
            EnsureHooks();
            Close(siteId, state, dropRecord: false);
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            if (siteId == HomeValleyLayout.RegionId && Nav.NavService.IsBound)
            {
                // FG0-ARCH-06：家园在星球格网上——移动走层级寻路、不穿越不可通行的格子、同阵营移动单位互相分离。
                cfg.NavEnabled = 1;
                cfg.SeparationFactor = Nav.NavService.Tuning("nav.separation_factor", 0.5f);
            }
            var site = new CombatSite(siteId, cfg) { Rules = rules };
            Sites[siteId] = site;
            if (cfg.NavEnabled != 0)
            {
                Nav.NavService.BindCombat(site);
            }
            restored = false;
            CombatSiteRecord rec = FindRecord(state, siteId);
            if (resume && rec != null)
            {
                restored = site.TryRestore(state, rec, out string reasonKey);
                if (!restored)
                {
                    string siteName = SiteName(siteId);
                    string reason = GameText.Has(reasonKey) ? GameText.Get(reasonKey) : reasonKey;
                    Log.Warning($"[CombatSites] {siteId} 战斗快照读不了（{reasonKey}），按记录重建。");
                    NotificationCenter.Post("save_migrated", GameText.Format("combat.load.rebuilt", siteName, reason));
                }
            }
            if (!resume)
            {
                RemoveRecord(state, siteId);
            }
            return site;
        }

        /// <summary>关闭（卸载）一个地点的内核：释放原生容器。<paramref name="dropRecord"/> 时同时删掉存档里这个地点的快照（远征结束）。</summary>
        public static void Close(string siteId, CampaignState state, bool dropRecord)
        {
            if (siteId != null && Sites.TryGetValue(siteId, out CombatSite s))
            {
                s.Dispose();
                Sites.Remove(siteId);
            }
            if (dropRecord)
            {
                RemoveRecord(state, siteId);
            }
        }

        public static void CloseAll()
        {
            foreach (CombatSite s in Sites.Values)
            {
                s.Dispose();
            }
            Sites.Clear();
        }

        public static string SiteName(string siteId)
        {
            if (siteId == HomeValleyLayout.RegionId)
            {
                return GameText.Get("combat.site.home");
            }
            if (siteId == FracturedCityLayout.RegionId)
            {
                return GameText.Get("combat.site.fractured_city");
            }
            if (siteId == FoundryOutpostLayout.RegionId)
            {
                return GameText.Get("combat.site.foundry_outpost");
            }
            if (Economy.TestRangeService.IsRangeSite(siteId))
            {
                return GameText.Get("combat.site.test_range"); // FG5-RND-03：靶场的仿真地点（反应图鉴“首次触发”的地点）
            }
            return siteId;
        }

        // ─────────────────────────────── 全局事件分发 ───────────────────────────────

        private static void EnsureHooks()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            MachineRegistry.MachineDied += OnMachineDied;
            MachineLoadoutRegistry.Changed += OnLoadoutChanged;
            EnsureEmgListening();
        }

        // ── EMG1-BUS-01（FGR-EMG-066）：热更层来源的战斗事件注入（接入 / 断开订阅信号广播；拾取在自动拾取处；夺控 / 归还的生产在 EMG4-HIJ-01 / 02 调同一入口）──

        private static bool _emgListening;

        /// <summary>订阅 <see cref="Signal.SignalUplinkService.UplinkChangedEvent"/>（进程内一次；读档 / 换场景不重复订阅——重复订阅会让同一次接入注入两条）。</summary>
        public static void EnsureEmgListening()
        {
            if (_emgListening)
            {
                return;
            }
            _emgListening = true;
            GameEvent.AddEventListener<Signal.SignalUplinkChange>(Signal.SignalUplinkService.UplinkChangedEvent, OnUplinkChanged);
        }

        public static bool EmgListening => _emgListening;

        /// <summary>本进程经信号广播注入的接入 / 断开条数（自检核对“一次广播只注入一次”）。</summary>
        public static int EmgUplinkInjected { get; private set; }

        /// <summary>
        /// 信号从 <c>PreviousLogicId</c> 到了 <c>CurrentLogicId</c>：离开的机器注入 evt.unlink（Code = 原因），接入的机器注入 evt.uplink。
        /// 同一台机器的核心固件编辑（Previous == Current）不是接入 / 断开。广播在两台机器重算武器（重编译程序）之后发出，所以按新程序判断有没有监听。O(地点数)。
        /// </summary>
        public static void OnUplinkChanged(Signal.SignalUplinkChange c)
        {
            if (c.PreviousLogicId == c.CurrentLogicId)
            {
                return;
            }
            if (c.PreviousLogicId != 0 && InjectEmgForMachine(c.PreviousLogicId, EmgEventKind.Unlink, UnlinkCode(c.Reason, c.CurrentLogicId)) == EmgInjectResult.Enqueued)
            {
                EmgUplinkInjected++;
            }
            if (c.CurrentLogicId != 0 && InjectEmgForMachine(c.CurrentLogicId, EmgEventKind.Uplink, 0) == EmgInjectResult.Enqueued)
            {
                EmgUplinkInjected++;
            }
        }

        /// <summary>断开原因 → evt.unlink 的 Code（FGR-EMG-060 第 18 行：主动 / 切换 / 阵亡 / 断链 / 静默夜；区域卸载另记）。</summary>
        public static ushort UnlinkCode(RegionControlChangeReason reason, int currentLogicId)
        {
            switch (reason)
            {
                case RegionControlChangeReason.PlayerRequest:
                    return currentLogicId != 0 ? EmgCode.UnlinkSwitch : EmgCode.UnlinkLeave;
                case RegionControlChangeReason.DeathRebound:
                    return EmgCode.UnlinkDeath;
                case RegionControlChangeReason.SignalLost:
                case RegionControlChangeReason.CoverageLost:
                    return EmgCode.UnlinkSevered;
                case RegionControlChangeReason.SilentNight:
                    return EmgCode.UnlinkSilentNight;
                case RegionControlChangeReason.RegionUnload:
                    return EmgCode.UnlinkUnload;
                default:
                    return EmgCode.UnlinkOther;
            }
        }

        /// <summary>机器自动拾取了一件地面物（CargoService 调；桥接层之外不点名内核类型，FG14 §5 硬约束 3）。</summary>
        public static bool InjectEmgPickup(int logicId, bool scrap) =>
            InjectEmgForMachine(logicId, EmgEventKind.Pickup, scrap ? EmgCode.PickupScrap : EmgCode.PickupDrop) == EmgInjectResult.Enqueued;

        /// <summary>把一条热更层来源的事件注入机器所在的地点内核（机器只在一个地点；O(地点数)）。不在任何已载入地点返回 NoUnit。</summary>
        public static EmgInjectResult InjectEmgForMachine(int logicId, EmgEventKind kind, ushort code, int otherUnit = 0)
        {
            foreach (CombatSite s in Sites.Values)
            {
                if (!s.IsDisposed && s.TryGetMachineUnit(logicId, out _))
                {
                    return s.InjectEmgMachineEvent(logicId, kind, code, otherUnit);
                }
            }
            return EmgInjectResult.NoUnit;
        }

        private static void OnMachineDied(int logicId)
        {
            foreach (CombatSite s in Sites.Values)
            {
                if (!s.IsDisposed)
                {
                    s.OnMachineDied(logicId);
                }
            }
        }

        private static void OnLoadoutChanged(int logicId)
        {
            if (logicId <= 0)
            {
                return;
            }
            CampaignState state = CampaignSession.Current;
            foreach (CombatSite s in Sites.Values)
            {
                if (!s.IsDisposed && s.TryGetMachineUnit(logicId, out _))
                {
                    s.RefreshMachineWeapon(state, logicId);
                }
            }
        }

        /// <summary>机器进出工厂（HomeValleyFactory 写 IsInFactory 之后调用）：同步到机器所在地点的内核（O(地点数)）。</summary>
        public static void SyncFactoryState(MachineRecord record)
        {
            if (record == null)
            {
                return;
            }
            foreach (CombatSite s in Sites.Values)
            {
                if (!s.IsDisposed)
                {
                    s.SetMachineInFactory(record.LogicId, record.IsInFactory);
                }
            }
        }

        /// <summary>机器所在地点的内核位置（跨地点查询）。</summary>
        public static bool TryGetMachinePosition(int logicId, out Vector2 position)
        {
            foreach (CombatSite s in Sites.Values)
            {
                if (!s.IsDisposed && s.TryGetMachinePosition(logicId, out position))
                {
                    return true;
                }
            }
            position = default;
            return false;
        }

        /// <summary>把机器在当前地点内核里的实时状态（位置、热量、冷却）写回记录——机器要换地点（派遣、撤离）之前调用，
        /// 新地点按记录建单位，热量与冷却不会凭空清零。</summary>
        public static void ExportMachine(int logicId)
        {
            foreach (CombatSite s in Sites.Values)
            {
                if (!s.IsDisposed && s.TryGetMachineUnit(logicId, out _))
                {
                    s.ExportMachine(logicId, includePosition: true);
                }
            }
        }

        // ─────────────────────────────── 存档 ───────────────────────────────

        /// <summary>唯一写入口：每个已载入地点的内核快照写进 <see cref="CampaignState.Combat"/>（按地点替换）。
        /// 还没载入的地点的快照原样保留——读档恢复时家园先载入并自动存档，远征地点稍后才载入，它的快照不能在这之间被抹掉；
        /// 远征结束（地点卸载）时由 <see cref="Close"/>（dropRecord）删除它的快照。</summary>
        public static void WriteTo(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            CampaignFgStateDomains.EnsureAll(state);
            var bySite = new Dictionary<string, CombatSiteRecord>(StringComparer.Ordinal);
            foreach (CombatSiteRecord r in state.Combat.Sites)
            {
                if (r != null && !string.IsNullOrEmpty(r.SiteId))
                {
                    bySite[r.SiteId] = r;
                }
            }
            foreach (KeyValuePair<string, CombatSite> kv in Sites)
            {
                if (kv.Value.IsDisposed)
                {
                    continue;
                }
                CombatSiteRecord r = kv.Value.Snapshot();
                if (r != null)
                {
                    bySite[kv.Key] = r;
                }
            }
            state.Combat.Sites = bySite.Values.OrderBy(r => r.SiteId, StringComparer.Ordinal).ToArray();
        }

        public static CombatSiteRecord FindRecord(CampaignState state, string siteId)
        {
            CombatSiteRecord[] sites = state?.Combat?.Sites;
            if (sites == null)
            {
                return null;
            }
            foreach (CombatSiteRecord r in sites)
            {
                if (r != null && r.SiteId == siteId)
                {
                    return r;
                }
            }
            return null;
        }

        public static void RemoveRecord(CampaignState state, string siteId)
        {
            if (state?.Combat?.Sites == null || siteId == null)
            {
                return;
            }
            if (state.Combat.Sites.Any(r => r != null && r.SiteId == siteId))
            {
                state.Combat.Sites = state.Combat.Sites.Where(r => r != null && r.SiteId != siteId).ToArray();
            }
        }
    }
}
