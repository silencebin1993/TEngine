// PL-INF-01 → production/design/presentation-track/handoff/PL-INF-01.md
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BinGames.Presentation
{
    /// <summary>
    /// 全部 PL 表现功能的统一 Tick 点（静态）。按登记顺序驱动 <see cref="IPresentationModule"/>：
    /// 宿主总开关（<see cref="PresentationFeatureRegistry.HostId"/>）与模块自己的开关都开才启用，任一关掉立即 OnDisable。
    /// 异常隔离：模块的 OnEnable / Tick / Rebuild / OnDisable 抛异常只记一次日志、该模块被停用并标为故障（重新登记前不再启用），
    /// 宿主不往外抛，其余模块与调用方（GameRoot.OnUpdate 后面的 FG 逻辑）照常走。
    /// 每帧零分配。PL 对象一律挂在 <see cref="Root"/>（[PresentationRoot]）下。
    /// </summary>
    public static class PresentationHost
    {
        public const string RootName = "[PresentationRoot]";

        private static readonly List<IPresentationModule> Modules = new List<IPresentationModule>();
        private static readonly List<bool> Active = new List<bool>();
        private static readonly List<bool> Faulted = new List<bool>();
        private static int _activeCount;
        private static int _seenRevision = -1;
        private static bool _hostOn;
        private static GameObject _root;
        private static int _pendingRebuild; // PL-INF-07：待重建原因位掩码（1 << RebuildReason），下一次 Tick 开头一次性处理

        /// <summary>总开关（对账后读缓存，零查表；开关刚变、还没对账时直接查登记表）。</summary>
        public static bool Enabled => _seenRevision == PresentationFeatureRegistry.Revision ? _hostOn : PresentationFeatureRegistry.IsOn(PresentationFeatureRegistry.HostId);

        /// <summary>总开关关且没有启用中的模块时为 false：调用方可以连拼帧都省掉（关掉总开关后仍为 true，直到下一次 Tick 对账停掉模块）。</summary>
        public static bool NeedsTick => _activeCount > 0 || Enabled;

        public static int ModuleCount => Modules.Count;

        public static int ActiveCount => _activeCount;

        /// <summary>已经 Tick 过的帧数（自检 / 采集对帧用）。</summary>
        public static long FrameCount { get; private set; }

        /// <summary>PL 对象的根节点；第一次取时创建（Play 下 DontDestroyOnLoad，编辑模式下不存进场景）。</summary>
        public static Transform Root
        {
            get
            {
                if (_root == null)
                {
                    _root = new GameObject(RootName);
                    if (Application.isPlaying)
                    {
                        Object.DontDestroyOnLoad(_root);
                    }
                    else
                    {
                        _root.hideFlags = HideFlags.DontSave;
                    }
                }
                return _root.transform;
            }
        }

        /// <summary>根节点当前子物体数（不存在时 0，不会顺手创建）。</summary>
        public static int RootChildCount => _root == null ? 0 : _root.transform.childCount;

        public static bool RootExists => _root != null;

        /// <summary>销毁根节点（自检收尾，免得编辑器里残留空的 [PresentationRoot]）；下次取 <see cref="Root"/> 会重建。</summary>
        public static void DestroyRoot()
        {
            if (_root == null)
            {
                return;
            }
            if (Application.isPlaying)
            {
                Object.Destroy(_root);
            }
            else
            {
                Object.DestroyImmediate(_root);
            }
            _root = null;
        }

        public static void SetEnabled(bool on)
        {
            PresentationFeatureRegistry.Set(PresentationFeatureRegistry.HostId, on);
            Reconcile();
        }

        /// <summary>
        /// 登记一个模块（重复登记忽略）；顺序即 Tick 顺序。模块 Id 必须已在 <see cref="PresentationFeatureRegistry"/> 登记，
        /// 否则抛 ArgumentException（没登记的 Id 永远当关，模块根本不会运行，开 / 关 sim 哈希夹具也会空转通过）。
        /// </summary>
        public static void Register(IPresentationModule module)
        {
            if (module == null || Modules.Contains(module))
            {
                return;
            }
            if (!PresentationFeatureRegistry.IsRegistered(module.Id))
            {
                throw new ArgumentException($"PL 模块 {module.GetType().Name} 的 Id \"{module.Id}\" 没在 PresentationFeatureRegistry.Table 登记（同时要在 FEATURES.md 加同名行）");
            }
            Modules.Add(module);
            Active.Add(false);
            Faulted.Add(false);
            _seenRevision = -1;
            Reconcile();
        }

        public static void Unregister(IPresentationModule module)
        {
            int i = Modules.IndexOf(module);
            if (i < 0)
            {
                return;
            }
            if (Active[i])
            {
                Active[i] = false;
                _activeCount--;
                try
                {
                    module.OnDisable();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
            Modules.RemoveAt(i);
            Active.RemoveAt(i);
            Faulted.RemoveAt(i);
        }

        /// <summary>停用并移除全部模块（自检收尾、读档前清场用）。</summary>
        public static void UnregisterAll()
        {
            for (int i = Modules.Count - 1; i >= 0; i--)
            {
                Unregister(Modules[i]);
            }
        }

        public static bool IsActive(IPresentationModule module)
        {
            int i = Modules.IndexOf(module);
            return i >= 0 && Active[i];
        }

        /// <summary>该模块抛过异常、已被宿主停用（重新登记前不再启用）。</summary>
        public static bool IsFaulted(IPresentationModule module)
        {
            int i = Modules.IndexOf(module);
            return i >= 0 && Faulted[i];
        }

        public static void Tick(in PresentationFrame frame)
        {
            if (_seenRevision != PresentationFeatureRegistry.Revision)
            {
                Reconcile();
            }
            if (!_hostOn)
            {
                _pendingRebuild = 0; // 总开关关着时模块都已停用，重新开启走 OnEnable，不补发重建
                return;
            }
            FrameCount++;
            if (_pendingRebuild != 0)
            {
                int mask = _pendingRebuild;
                _pendingRebuild = 0;
                if ((mask & (1 << (int)RebuildReason.Reload)) != 0)
                {
                    Rebuild(RebuildReason.Reload);
                }
                else if ((mask & (1 << (int)RebuildReason.SiteSwitch)) != 0)
                {
                    Rebuild(RebuildReason.SiteSwitch);
                }
                else
                {
                    // OriginShift 与 Settings 互不包含：同帧都有就各调一次
                    if ((mask & (1 << (int)RebuildReason.OriginShift)) != 0)
                    {
                        Rebuild(RebuildReason.OriginShift);
                    }
                    if ((mask & (1 << (int)RebuildReason.Settings)) != 0)
                    {
                        Rebuild(RebuildReason.Settings);
                    }
                }
            }
            for (int i = 0; i < Modules.Count; i++)
            {
                if (!Active[i])
                {
                    continue;
                }
                try
                {
                    Modules[i].Tick(in frame);
                }
                catch (Exception e)
                {
                    Fault(i, "Tick", e, true);
                }
            }
        }

        /// <summary>
        /// PL-INF-07：登记一个重建原因，下一次 <see cref="Tick"/> 在驱动模块之前统一处理——每种原因同帧最多一次；有 Reload / SiteSwitch 时只调一次
        /// （Reload 优先，视为全部重算），否则 OriginShift、Settings 各调一次（见 <see cref="RebuildReason"/>）。读档 / 切地点由 PresentationHostBridge 检测后调，原点平移由 PresentationSpace.SetOrigin 调。
        /// </summary>
        public static void RequestRebuild(RebuildReason reason) => _pendingRebuild |= 1 << (int)reason;

        /// <summary>立即对全部启用中的模块调 Rebuild（不合并；正常路径用 <see cref="RequestRebuild"/>）。</summary>
        public static void Rebuild(RebuildReason reason)
        {
            for (int i = 0; i < Modules.Count; i++)
            {
                if (!Active[i])
                {
                    continue;
                }
                try
                {
                    Modules[i].Rebuild(reason);
                }
                catch (Exception e)
                {
                    Fault(i, "Rebuild", e, true);
                }
            }
        }

        /// <summary>按登记表对账：该开的 OnEnable、该关的 OnDisable。开关变化后立即调用，或等下一次 Tick 自动调用。OnEnable 成功返回后才算启用。</summary>
        public static void Reconcile()
        {
            _seenRevision = PresentationFeatureRegistry.Revision;
            _hostOn = PresentationFeatureRegistry.IsOn(PresentationFeatureRegistry.HostId);
            for (int i = 0; i < Modules.Count; i++)
            {
                bool want = _hostOn && !Faulted[i] && PresentationFeatureRegistry.IsOn(Modules[i].Id);
                if (want == Active[i])
                {
                    continue;
                }
                try
                {
                    if (want)
                    {
                        Modules[i].OnEnable();
                        Active[i] = true;
                        _activeCount++;
                    }
                    else
                    {
                        Active[i] = false;
                        _activeCount--;
                        Modules[i].OnDisable();
                    }
                }
                catch (Exception e)
                {
                    // OnEnable 中途抛：再调一次 OnDisable 收拾已建的半截对象；OnDisable 自己抛就不再重试
                    Fault(i, want ? "OnEnable" : "OnDisable", e, want);
                }
            }
        }

        private static void Fault(int i, string stage, Exception e, bool callDisable)
        {
            if (!Faulted[i])
            {
                Faulted[i] = true;
                Debug.LogError($"[PresentationHost] PL 模块 {Modules[i].Id}（{Modules[i].GetType().Name}）{stage} 抛异常，已停用，重新登记前不再启用");
                Debug.LogException(e);
            }
            if (Active[i])
            {
                Active[i] = false;
                _activeCount--;
            }
            if (!callDisable)
            {
                return;
            }
            try
            {
                Modules[i].OnDisable();
            }
            catch (Exception e2)
            {
                Debug.LogException(e2);
            }
        }
    }
}
