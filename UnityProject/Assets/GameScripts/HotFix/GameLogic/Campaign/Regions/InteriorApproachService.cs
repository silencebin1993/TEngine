using System.Collections.Generic;
using GameLogic.Campaign.Feedback;
using GameLogic.Campaign.WorldGen;
using GameLogic.Core;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>
    /// DEBT-FG8GEN02-10（RTS 右键情境命令）：选中机器后右键室内入口 = 走过去并进入。
    /// 意图（入口 + 哪几台）存进 <see cref="WorldGenState.ApproachEnterAnchorId"/> / <see cref="WorldGenState.ApproachEnterIds"/>，读档接着走；
    /// 移动就是普通的右键移动命令（同一条内核命令槽，不另造移动）；对这些机器下任何新命令（移动 / 攻击 / 待命 / 派工 / 接入）= 这台退出意图（可取消）。
    /// 全部还在意图里的机器都到了入口旁 → 只带它们走 <see cref="InteriorSiteService.TryEnter"/>（到达时再做进入前存档）；
    /// 命令已结束却没到（走不到）的那台退出意图并写明；一台都不剩 → 意图作废。每步 O(意图台数)，没有意图时 O(1)。
    /// </summary>
    public static class InteriorApproachService
    {
        private static readonly List<int> Scratch = new List<int>();

        /// <summary>自检：最近一次意图结束的原因（空 = 进去了 / 没有结束过）。</summary>
        public static string LastOutcome { get; private set; } = string.Empty;

        public static bool HasIntent(CampaignState s) => s?.World != null && !string.IsNullOrEmpty(s.World.ApproachEnterAnchorId) && s.World.ApproachEnterIds is { Length: > 0 };

        /// <summary>登记意图（覆盖上一条：右键新的入口 = 换目标）。调用前移动命令已经下达。</summary>
        public static void Begin(CampaignState s, string anchorId, IReadOnlyList<int> ids)
        {
            if (s?.World == null || string.IsNullOrEmpty(anchorId) || ids == null || ids.Count == 0)
            {
                return;
            }
            var list = new int[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                list[i] = ids[i];
            }
            s.World.ApproachEnterAnchorId = anchorId;
            s.World.ApproachEnterIds = list;
            LastOutcome = string.Empty;
        }

        /// <summary>玩家对这台机器下了新命令：退出意图。</summary>
        public static void Drop(int logicId)
        {
            CampaignState s = CampaignSession.Current;
            if (!HasIntent(s))
            {
                return;
            }
            int[] ids = s.World.ApproachEnterIds;
            int at = System.Array.IndexOf(ids, logicId);
            if (at < 0)
            {
                return;
            }
            var rest = new int[ids.Length - 1];
            System.Array.Copy(ids, 0, rest, 0, at);
            System.Array.Copy(ids, at + 1, rest, at, ids.Length - at - 1);
            s.World.ApproachEnterIds = rest;
            if (rest.Length == 0)
            {
                End(s, GameText.Get("interior.approach.cancelled"), notify: false);
            }
        }

        public static void Cancel(CampaignState s) => End(s, GameText.Get("interior.approach.cancelled"), notify: false);

        /// <summary>
        /// 每步（整步提交之后）检查一次：剔除阵亡 / 离开家园 / 被接入的；全部到了就进去。
        /// <paramref name="hasCommand"/> = 这台机器此刻还有编队命令（走在路上）；<paramref name="possessed"/> = 正在接入直控的那台（直控 = 玩家接管，退出意图）。
        /// </summary>
        public static void Step(CampaignState s, System.Func<int, bool> hasCommand, int possessed)
        {
            if (!HasIntent(s))
            {
                return;
            }
            PlannedAnchor a = WorldGenService.PlanFor(s)?.FindAnchor(s.World.ApproachEnterAnchorId);
            if (a == null || !a.IsIndoorEntrance)
            {
                End(s, GameText.Get("interior.reason.not_found"), notify: true);
                return;
            }
            Vector2 entry = TerritoryAnchorChecks.EntryOf(s, a);
            Scratch.Clear();
            bool allNear = true;
            foreach (int id in s.World.ApproachEnterIds)
            {
                if (id == possessed || !MachineRegistry.TryGetRecord(id, out MachineRecord m) || m == null || !m.IsAlive || m.RegionId != HomeValleyLayout.RegionId)
                {
                    continue;
                }
                if (InteriorSiteService.IsNearEntrance(a, entry, m))
                {
                    Scratch.Add(id);
                    continue;
                }
                if (!GameClock.Paused && hasCommand != null && !hasCommand(id))
                {
                    // 移动命令已结束却没到入口旁（走不到 / 被挡）：不再等它，写明。
                    FeedbackCues.RaiseLocated(FeedbackCueId.Denied, new Vector2(a.EntryX, a.EntryY),
                        GameText.Format("interior.approach.unreachable", MachineNaming.Short(m), InteriorSiteService.SurfaceName(a.SurfaceId)));
                    continue;
                }
                Scratch.Add(id);
                allNear = false;
            }
            if (Scratch.Count == 0)
            {
                End(s, GameText.Get("interior.approach.cancelled"), notify: false);
                return;
            }
            if (Scratch.Count != s.World.ApproachEnterIds.Length)
            {
                s.World.ApproachEnterIds = Scratch.ToArray();
            }
            if (!allNear)
            {
                return;
            }
            var members = new HashSet<int>(Scratch);
            string anchorId = a.Id;
            ClearIntent(s);
            if (!InteriorSiteService.TryEnter(s, anchorId, out string reason, members))
            {
                LastOutcome = reason ?? string.Empty;
                FeedbackCues.RaiseLocated(FeedbackCueId.Denied, new Vector2(a.EntryX, a.EntryY),
                    GameText.Format("interior.approach.failed", InteriorSiteService.SurfaceName(a.SurfaceId), reason));
            }
        }

        private static void End(CampaignState s, string outcome, bool notify)
        {
            if (s?.World == null)
            {
                return;
            }
            ClearIntent(s);
            LastOutcome = outcome ?? string.Empty;
            if (notify)
            {
                FeedbackCues.Raise(FeedbackCueId.Denied, outcome);
            }
        }

        private static void ClearIntent(CampaignState s)
        {
            s.World.ApproachEnterAnchorId = string.Empty;
            s.World.ApproachEnterIds = System.Array.Empty<int>();
        }
    }
}
