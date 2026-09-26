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
            var site = new CombatSite(siteId, CombatSite.ConfigFromTuning()) { Rules = rules };
            Sites[siteId] = site;
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
