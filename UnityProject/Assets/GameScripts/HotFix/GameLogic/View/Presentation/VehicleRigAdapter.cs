// PL-VEH-02 → production/design/presentation-track/handoff/PL-VEH-02.md
using BinGames.Presentation;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Regions;
using UnityEngine;

namespace GameLogic.View.Presentation
{
    /// <summary>
    /// PL-VEH-02 热更适配（O(1) 事件驱动，不每帧跑）：机器表现挂上 / 摘下时在 <see cref="VehicleRigSystem"/> 登记 / 注销两个节点
    /// （白模剪影 <see cref="PlaceholderSilhouette.PartsName"/> 与形变挂点根 <see cref="MachineMorphView.RigName"/>）；
    /// 装配变更（<see cref="CombatSite.MachineWeaponRefreshed"/>）后 MorphRig 可能被重建，只重取那一台的节点。
    /// 安装：<see cref="Install"/>（PresentationHostBridge 第一次 Tick 时调；自检自己调）。
    /// </summary>
    public static class VehicleRigAdapter
    {
        private static bool _installed;
        private static bool _weaponHooked;

        public static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            PresentationHooks.MachineViewAttached += OnAttached;
            PresentationHooks.MachineViewDetached += OnDetached;
        }

        public static VehicleArchetype ArchetypeOf(string chassisId)
        {
            string a = ChassisCatalog.ResolveArchetype(chassisId);
            return a == ChassisCatalog.ChassisTrackId ? VehicleArchetype.Track
                : a == ChassisCatalog.ChassisHoverId ? VehicleArchetype.Hover
                : a == ChassisCatalog.ChassisFixedId ? VehicleArchetype.Fixed
                : VehicleArchetype.Wheel; // 解析不出（null）按轮式
        }

        /// <summary>这台机器表现当前的 MorphRig（RebuildParts 用 Destroy 延迟销毁旧的，同名旧节点本帧还在，所以经挂点组的父节点取新的）。</summary>
        public static Transform MorphRigOf(int logicId, MachineView view)
        {
            Transform g = MachineMorphView.GroupOf(logicId, MorphMask.Limiter);
            if (g != null && g.parent != null && g.parent.parent == view.transform)
            {
                return g.parent;
            }
            return view.transform.Find(MachineMorphView.RigName);
        }

        private static void OnAttached(HomeValleyMachineMarker marker)
        {
            MachineView view = marker?.View;
            if (view == null)
            {
                return;
            }
            if (!_weaponHooked)
            {
                // 第一次挂上时才订阅：MachineMorphView 在同一个 AttachView 里、本接缝之前订阅，事件触发时它先重建 MorphRig、这里后取新节点
                _weaponHooked = true;
                CombatSite.MachineWeaponRefreshed += OnWeaponRefreshed;
            }
            Transform silhouette = view.transform.Find(PlaceholderSilhouette.PartsName);
            VehicleRigSystem.Register(marker.LogicId, view, marker.Site?.Kernel, marker.UnitId, ArchetypeOf(marker.ChassisId),
                silhouette, MorphRigOf(marker.LogicId, view));
        }

        private static bool OwnsView(int logicId, MachineView view)
        {
            Transform n0 = VehicleRigSystem.NodeOf(logicId, 0);
            return n0 != null && n0.parent == view.transform;
        }

        private static void OnDetached(int logicId, MachineView view) => VehicleRigSystem.Unregister(logicId, view);

        private static void OnWeaponRefreshed(CombatSite site, int logicId)
        {
            if (!VehicleRigSystem.IsRegistered(logicId) || site == null || site.IsDisposed
                || !site.TryGetMachineMarker(logicId, out HomeValleyMachineMarker marker) || marker.View == null
                || !OwnsView(logicId, marker.View)) // 登记的是别处那份表现
            {
                return;
            }
            VehicleRigSystem.ReplaceNode(logicId, 1, MorphRigOf(logicId, marker.View));
        }
    }
}
