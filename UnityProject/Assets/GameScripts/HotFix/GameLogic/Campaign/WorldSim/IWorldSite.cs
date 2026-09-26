using System;
using GameLogic.View;
using UnityEngine;

namespace GameLogic.Campaign.WorldSim
{
    /// <summary>地点所在表面的种类（FGR-ARC-014）。</summary>
    public enum WorldSurfaceKind
    {
        /// <summary>星球表面（连续、无限；家园、行进中的突袭都在这里）。</summary>
        Planet = 0,
        /// <summary>Demo 遗留的手工区域（破碎都市、铸造前哨外围）：独立的小表面、局部坐标。
        /// FG8-GEN-02 把它们迁到星球上的阵营领地前，进出它们与进出室内一样是“切换表面”（DEBT-FG0ARCH01-01）。</summary>
        LegacyRegion = 1,
    }

    /// <summary>
    /// FG0-ARCH-01（FGR-ARC-002）：世界里的一个地点（Demo 的一个区域控制器）。**模拟与观察分离**：
    /// - <see cref="SimStep"/>：由 <see cref="WorldSimulation"/> 按统一时钟的固定步调用；只要地点已载入就执行，
    ///   **不得读取“是否被观察”**（FGR-BASE-021：观察不改变结果；自检用观察 / 不观察对照逐字段比较来守护）。
    /// - <see cref="FrameUpdate"/>：只对当前被观察的地点每帧调用——玩家输入、接入、画面对账。
    /// - <see cref="SetObserved"/>：全局镜头换地点时切换表现对象的可见性（不销毁：Demo 的机器位置仍记在表现对象的 Transform 上，DEBT-FG0ARCH01-03）。
    /// </summary>
    public interface IWorldSite
    {
        string SiteId { get; }
        WorldSurfaceKind SurfaceKind { get; }
        bool IsLoaded { get; }
        /// <summary>本地点是否已经全灭（远征地点；家园恒为 false）。</summary>
        bool IsWiped { get; }
        /// <summary>本地点存活的己方机器数（关注点条显示用）。</summary>
        int LiveMachineCount { get; }
        /// <summary>镜头配置（进场时建立；全局镜头据此绑定）。</summary>
        WorldCameraProfile CameraProfile { get; }

        void SimStep(float dt);
        void FrameUpdate(float realDt, float frameScaledDt);
        void SetObserved(bool observed);
        /// <summary>机器实时位置（本地点里有这台机器的表现对象时）。</summary>
        Vector2? LivePosition(int logicId);
        void SyncLiveStateForSave();
        /// <summary>“飞到这个地点”时的默认落点（家园 = 归还核心；远征 = 存活机器的中心）。</summary>
        Vector2 DefaultFocus { get; }
    }

    /// <summary>地点的镜头配置：全局镜头绑定时使用（取代 Demo 每个控制器各建一个 <see cref="CameraDirector"/>）。</summary>
    public sealed class WorldCameraProfile
    {
        public CameraDirector.DirectAnchorProvider DirectAnchor;
        public Func<bool> EnsureDirectTarget;
        /// <summary>方形平移边界的半宽（Demo 区域）；<see cref="DynamicBounds"/> 不为空时用它的矩形代替。</summary>
        public float ArenaHalfExtent = 40f;
        /// <summary>动态矩形边界（星球：已探索范围 + 边距 ∪ 行进中的队伍）。返回 (min, max)。</summary>
        public Func<(Vector2 Min, Vector2 Max)> DynamicBounds;
        public Vector3 FollowOffset = new Vector3(0f, 40f, 0f);
        public float InitialStrategyOrthographicSize = 30f;
        public float InitialDirectOrthographicSize = 14f;
        public Vector2 StartFocus;
        public Color Background = new Color(0.05f, 0.07f, 0.10f);
    }
}
