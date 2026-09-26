using System;
using System.Collections.Generic;
using GameLogic.Campaign.Feedback;
using GameLogic.Core;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-INT-01：Direct 的 E 交互唯一实现——归还谷地/破碎都市共用同一套候选排序 + 按住/
    /// 点击进度 + 校验/拒绝框架（同 <see cref="RegionControlSystem"/>/<see cref="RegionSquadCommandSystem"/>
    /// 一样的"Context 委托 + 共享引擎"范式，各自 Controller 一份实例，避免两个区域各写一套判定）。
    ///
    /// ── 五类交互与底层动作的关系（不重新发明业务规则）──
    /// 残骸拆解（归还谷地）：直接复用既有点选下令同一条 <see cref="HomeValleyWorkOrders.TryCreateSalvage"/>
    /// 状态机——E 只是把"已经站在残骸旁的直控机器"立即从 Reserved 推进到 InProgress（
    /// <see cref="HomeValleyWorkOrders.OnArrivedAtWork"/>），真正的拆解耗时/产出/幂等仍由那套已验证过的
    /// 状态机负责，本类不重复计时或改写产出规则。监听节点摧毁（破碎都市）没有等价的耐久状态机
    /// （<see cref="FracturedCityRegion.TryDestroyListeningNode"/> 本身是原子调用），本类才是它"按住"式
    /// 连续判定唯一落点（该方法类注释已明确点名"属于 ER5-INT-01"）。
    /// 战利品装载/终端读取/开箱同样只在到达 100% 进度时调用一次对应的原子方法——所有真正的产出/幂等/
    /// 状态写入仍在 <see cref="FracturedCityRegion"/>/<see cref="HomeValleyCargo"/>，本类不产生"半件货"，
    /// 因为进度没满之前压根没调用底层写入方法。
    ///
    /// ── 撤离确认/信标启动是跨 Story 占位 ──
    /// 两者的真正业务逻辑分别属于 ER5-RETURN-01（撤离/全灭/回城结算）与 ER7-BEACON-01（信标建造/供电/
    /// 解锁）——本类只保证候选能出现、能按住/点击、能给出"占位确认"反馈，真正效果由后续 Story 接手，
    /// 见 DEBT-ER5INT01-01/02。</summary>
    public enum RegionInteractCategory : byte
    {
        WreckageSalvage = 0,
        LootLoad = 1,
        TerminalRead = 2,
        EvacConfirm = 3,
        BeaconActivate = 4,
        /// <summary>ER6-REGION-01：封锁门状态查看——锁定时给出缺项原因（交互拒绝），解锁后仅确认
        /// 状态，不产生任何切场效果（核心分区战斗内容属于 ER7-CORE-01，见
        /// <see cref="FoundryOutpostRegion.CanEnterCoreZone"/> 类注释）。</summary>
        GateCheck = 5,
    }

    public enum RegionInteractFailure : byte
    {
        None = 0,
        /// <summary>没有受控实体（战略视角/意识无处可去）。</summary>
        NoControlledUnit,
        /// <summary>范围内没有任何候选。</summary>
        NoCandidate,
        /// <summary>唯一候选存在但超出 3 米交互范围。</summary>
        OutOfRange,
        /// <summary>候选在范围内，但被其它固定物遮挡/不可达。</summary>
        Occluded,
        /// <summary>目标在进度推进期间消失/已被处理过。</summary>
        TargetGone,
        /// <summary>仓储/货舱已满，装载被拒绝，不提交半件货。</summary>
        CargoFull,
        /// <summary>目标归属不允许当前交互（如敌方物/非法状态）。</summary>
        NotOwned,
        /// <summary>受控机当前失联/失控（干扰场 Suspended），世界交互让位。</summary>
        MachineLostControl,
        /// <summary>模态 UI 打开，世界输入让位。</summary>
        ModalBlocked,
        /// <summary>进度中被取消（离开范围/松开按键/目标失效）。</summary>
        Cancelled,
        /// <summary>ER6-REGION-01：封锁门三灯未全亮，交互拒绝（"重炮解析/熔穿过载蓝图保存/现役机
        /// 实装"缺项文案随 <see cref="RegionInteractResult.PlayerText"/> 一并给出）。</summary>
        GateLocked,
    }

    public readonly struct RegionInteractResult
    {
        public readonly bool Success;
        public readonly RegionInteractFailure Failure;
        public readonly string PlayerText;

        private RegionInteractResult(bool success, RegionInteractFailure failure, string playerText)
        {
            Success = success;
            Failure = failure;
            PlayerText = playerText;
        }

        public static RegionInteractResult Ok(string playerText = null) => new RegionInteractResult(true, RegionInteractFailure.None, playerText);
        public static RegionInteractResult Fail(RegionInteractFailure failure, string playerText) => new RegionInteractResult(false, failure, playerText);
    }

    /// <summary>一个可交互目标。<see cref="Id"/> 是稳定 ID（同一逻辑目标跨帧/存读档后不变），供候选
    /// 切换时判定"是不是同一个目标"（换目标要重置进度，不能让上一个目标攒的进度平移到新目标上）。</summary>
    public sealed class RegionInteractCandidate
    {
        public string Id;
        public RegionInteractCategory Category;
        public Vector2 Position;

        /// <summary>按住需要的秒数；0（或接近 0）表示单次按下即完成的"点击"式交互。</summary>
        public float HoldSeconds;

        /// <summary>同距离/同朝向时的平局优先级，越大越优先。</summary>
        public int Priority;

        /// <summary>提示文案用的动词（"拆解"/"装载"/"读取"/"确认撤离"/"启动信标"）——不含按键名，
        /// 按键名由 UI 层拼接 <see cref="RegionInteractionSystem.InteractKeyLabel"/>，保证重绑后文案
        /// 自动跟着变，不写死"按 E"。</summary>
        public string ActionVerb;

        /// <summary>每帧复检：目标是否仍然合法（不改变任何状态，纯查询）。</summary>
        public Func<RegionInteractResult> Validate;

        /// <summary>进度满 100% 时真正调用一次的底层动作。</summary>
        public Func<RegionInteractResult> Complete;
    }

    /// <summary>宿主 Controller 提供的最小上下文——用委托而不是接口（同
    /// <see cref="RegionControlContext"/>/<see cref="RegionSquadCommandContext"/> 先例）。</summary>
    public sealed class RegionInteractContext
    {
        /// <summary>当前受控机器；null＝战略视角，交互整体让位。</summary>
        public Func<HomeValleyMachineMarker> GetPossessed;

        /// <summary>模态/干扰/镜头过渡之外的"这一刻还能不能交互"检查，返回具体拒绝原因
        /// （<see cref="RegionInteractFailure.None"/>＝可以）。让每个区域按自己的
        /// <c>RegionControlSystem.Availability</c>/镜头模式自行判定，本类不重复猜测。</summary>
        public Func<RegionInteractFailure> GateCheck;

        /// <summary>本帧候选列表——区域量级恒定个位数，每帧重建一次开销可忽略（同
        /// <see cref="FracturedCityController.TickDiscovery"/> 一类个位数循环的既定处理方式）。</summary>
        public Func<List<RegionInteractCandidate>> BuildCandidates;

        /// <summary>玩家当前朝向（用于"指向"排序）；零向量表示无朝向信息，排序退化为纯距离/优先级。</summary>
        public Func<Vector2> GetFacing;

        /// <summary>可达性判定用的障碍物列表（锚点位置+净空半径），与
        /// <see cref="RegionSquadCommandContext.Obstacles"/> 同一份数据来源。</summary>
        public List<(Vector2 Position, float Radius)> Obstacles;
    }

    public sealed class RegionInteractionSystem
    {
        /// <summary>ER5-INT-01 卡片点名"距离≤3米"，与 <see cref="FracturedCityController.InteractRange"/>
        /// 保持同一数值来源（两边都是这张卡定的同一个数）。</summary>
        public const float InteractRange = 3f;

        private const float SubtitleSeconds = 2.5f;

        private RegionInteractContext _ctx;
        private string _activeCandidateId;
        private float _subtitleRemaining;

        public RegionInteractCandidate PrimaryCandidate { get; private set; }

        /// <summary>0～1，当前主候选的按住/点击进度。候选切换、校验失败、离开范围都会清零——
        /// 这正是"不产生半件货"的结构性保证：底层动作只在这个值真正到 1 的那一帧被调用一次。</summary>
        public float Progress01 { get; private set; }

        public RegionInteractFailure LastFailure { get; private set; }
        public string LastFailureText { get; private set; }
        public string LastSubtitle { get; private set; }

        public static string InteractKeyLabel => InputDisplay.ForAction(GameActionId.Interact);

        public void Bind(RegionInteractContext ctx)
        {
            _ctx = ctx;
            ResetAll();
        }

        public void Unbind()
        {
            _ctx = null;
            ResetAll();
        }

        private void ResetAll()
        {
            PrimaryCandidate = null;
            _activeCandidateId = null;
            Progress01 = 0f;
            LastFailure = RegionInteractFailure.NoControlledUnit;
            LastFailureText = null;
            LastSubtitle = null;
            _subtitleRemaining = 0f;
        }

        /// <summary>供 Controller 主动推送一条字幕（如"离开范围，已取消并恢复原状"这类不是由
        /// <see cref="RegionInteractCandidate.Complete"/> 触发、而是被区域自己的看门狗打断的场景）。</summary>
        public void NotifySubtitle(string text)
        {
            LastSubtitle = text;
            _subtitleRemaining = SubtitleSeconds;
        }

        public void Tick(float dt)
        {
            if (_subtitleRemaining > 0f)
            {
                _subtitleRemaining -= dt;
                if (_subtitleRemaining <= 0f)
                {
                    LastSubtitle = null;
                }
            }

            if (_ctx == null)
            {
                ResetAll();
                return;
            }

            HomeValleyMachineMarker possessed = _ctx.GetPossessed?.Invoke();
            if (possessed == null)
            {
                PrimaryCandidate = null;
                _activeCandidateId = null;
                Progress01 = 0f;
                LastFailure = RegionInteractFailure.NoControlledUnit;
                LastFailureText = null;
                return;
            }

            RegionInteractFailure gate = _ctx.GateCheck?.Invoke() ?? RegionInteractFailure.None;
            if (gate != RegionInteractFailure.None)
            {
                PrimaryCandidate = null;
                _activeCandidateId = null;
                Progress01 = 0f;
                LastFailure = gate;
                LastFailureText = null;
                if (gate == RegionInteractFailure.MachineLostControl
                    && InputRouter.ConsumeAction(GameActionId.Interact, InputScope.Direct))
                {
                    RaiseRejection(gate, "受控机失联，暂时无法交互。");
                }
                return;
            }

            List<RegionInteractCandidate> candidates = _ctx.BuildCandidates?.Invoke();
            Vector3 posV3 = possessed.Position3;
            var machinePos = new Vector2(posV3.x, posV3.z);
            Vector2 facing = _ctx.GetFacing?.Invoke() ?? Vector2.zero;

            RegionInteractCandidate best = SelectPrimary(candidates, machinePos, facing, out RegionInteractFailure noneReason);

            if (best == null || best.Id != _activeCandidateId)
            {
                Progress01 = 0f;
            }
            _activeCandidateId = best?.Id;
            PrimaryCandidate = best;

            if (best == null)
            {
                LastFailure = noneReason;
                LastFailureText = null;
                return;
            }

            RegionInteractResult check = best.Validate != null ? best.Validate() : RegionInteractResult.Ok();
            if (!check.Success)
            {
                Progress01 = 0f;
                LastFailure = check.Failure;
                LastFailureText = check.PlayerText;
                // 校验失败时提示条早已显示原因；玩家仍按下 E 的那一帧才是“被拒绝”，出拒绝音与字幕
                // （AC-AUD-001 拒绝/仓满）。不按键只是在看提示，不出声。
                if (InputRouter.ConsumeAction(GameActionId.Interact, InputScope.Direct))
                {
                    RaiseRejection(check.Failure, check.PlayerText);
                }
                return;
            }
            LastFailure = RegionInteractFailure.None;
            LastFailureText = null;

            bool held = InputRouter.GetActionKey(GameActionId.Interact, InputScope.Direct);
            if (best.HoldSeconds <= 0.0001f)
            {
                if (InputRouter.ConsumeAction(GameActionId.Interact, InputScope.Direct))
                {
                    CompleteInteraction(best);
                }
                return;
            }

            if (held)
            {
                Progress01 = Mathf.Clamp01(Progress01 + dt / best.HoldSeconds);
                if (Progress01 >= 1f)
                {
                    CompleteInteraction(best);
                }
            }
            else
            {
                // "按住"语义：松手即作废，不留半程进度——离开 3 米/松开 E 走的是同一条清零路径。
                Progress01 = 0f;
            }
        }

        private void CompleteInteraction(RegionInteractCandidate candidate)
        {
            RegionInteractResult result = candidate.Complete != null
                ? candidate.Complete()
                : RegionInteractResult.Fail(RegionInteractFailure.TargetGone, "没有可执行的动作。");

            if (result.Success)
            {
                LastFailure = RegionInteractFailure.None;
                LastFailureText = null;
                NotifySubtitle(result.PlayerText ?? (candidate.ActionVerb + "完成。"));
            }
            else
            {
                LastFailure = result.Failure;
                LastFailureText = result.PlayerText;
                RaiseRejection(result.Failure, result.PlayerText);
            }
            Progress01 = 0f;
        }

        /// <summary>ER8-CONTENT-01：交互被拒的声音+字幕。满仓单独归“仓满”，其余归“拒绝”；
        /// 玩家自己取消（松手/离开范围）不算拒绝。</summary>
        private static void RaiseRejection(RegionInteractFailure failure, string playerText)
        {
            if (failure == RegionInteractFailure.Cancelled || failure == RegionInteractFailure.ModalBlocked)
            {
                return;
            }
            FeedbackCues.Raise(failure == RegionInteractFailure.CargoFull ? FeedbackCueId.StorageFull : FeedbackCueId.Denied,
                playerText);
        }

        /// <summary>排序：指向 → 距离≤3米（过滤）→ 可达/无遮挡（过滤）→ 优先级 → 稳定 ID。</summary>
        private RegionInteractCandidate SelectPrimary(List<RegionInteractCandidate> candidates, Vector2 machinePos,
            Vector2 facing, out RegionInteractFailure noneReason)
        {
            noneReason = RegionInteractFailure.NoCandidate;
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            bool hasFacing = facing.sqrMagnitude > 0.0001f;
            Vector2 facingNorm = hasFacing ? facing.normalized : Vector2.zero;

            RegionInteractCandidate best = null;
            float bestFacingScore = float.NegativeInfinity;
            float bestDistance = float.PositiveInfinity;
            int bestPriority = int.MinValue;

            bool anyInRange = false;
            bool anyReachable = false;

            foreach (RegionInteractCandidate c in candidates)
            {
                float distance = Vector2.Distance(machinePos, c.Position);
                if (distance > InteractRange)
                {
                    continue;
                }
                anyInRange = true;

                if (!IsReachable(machinePos, c.Position))
                {
                    continue;
                }
                anyReachable = true;

                float facingScore = hasFacing ? Vector2.Dot(facingNorm, (c.Position - machinePos).normalized) : 0f;

                bool better;
                if (best == null)
                {
                    better = true;
                }
                else if (!Mathf.Approximately(facingScore, bestFacingScore))
                {
                    better = facingScore > bestFacingScore;
                }
                else if (!Mathf.Approximately(distance, bestDistance))
                {
                    better = distance < bestDistance;
                }
                else if (c.Priority != bestPriority)
                {
                    better = c.Priority > bestPriority;
                }
                else
                {
                    better = string.CompareOrdinal(c.Id, best.Id) < 0;
                }

                if (better)
                {
                    best = c;
                    bestFacingScore = facingScore;
                    bestDistance = distance;
                    bestPriority = c.Priority;
                }
            }

            if (best != null)
            {
                return best;
            }

            noneReason = !anyInRange ? RegionInteractFailure.OutOfRange
                : !anyReachable ? RegionInteractFailure.Occluded
                : RegionInteractFailure.NoCandidate;
            return null;
        }

        private bool IsReachable(Vector2 from, Vector2 to)
        {
            if (_ctx.Obstacles == null)
            {
                return true;
            }
            foreach ((Vector2 Position, float Radius) obstacle in _ctx.Obstacles)
            {
                // 目标自己所在的锚点不算"挡住自己"——它的净空圈本来就是交互点所在的地方。
                if (Vector2.Distance(obstacle.Position, to) <= obstacle.Radius + 0.1f)
                {
                    continue;
                }
                if (SegmentIntersectsCircle(from, to, obstacle.Position, obstacle.Radius))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SegmentIntersectsCircle(Vector2 a, Vector2 b, Vector2 center, float radius)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq <= 0.0001f)
            {
                return Vector2.Distance(a, center) <= radius;
            }
            float t = Mathf.Clamp01(Vector2.Dot(center - a, ab) / lenSq);
            Vector2 closest = a + ab * t;
            return Vector2.Distance(closest, center) <= radius;
        }
    }
}
