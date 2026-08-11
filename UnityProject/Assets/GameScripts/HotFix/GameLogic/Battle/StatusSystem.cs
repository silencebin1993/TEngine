using System.Collections.Generic;
using BinGames.Sim;
using GameLogic.Core;
using Unity.Mathematics;

namespace GameLogic.Battle
{
    /// <summary>
    /// 状态效果计时。
    ///
    /// 为什么需要它：内核的 <c>StatusMask</c> 只是一个位掩码，**没有时间概念**。
    /// 施加了导电/破体/减速就永久生效，这显然不对。本模块负责"到期取消"。
    ///
    /// 分工刻意如此：内核用位掩码做 job 里的快速分支（零成本），
    /// 时间管理留在热更层（条目数是数百级，不是数万级，O(N) 完全够）。
    /// </summary>
    public sealed class StatusSystem : GameModuleBase
    {
        public override int Priority => ModulePriority.Status;

        private struct Entry
        {
            public int UnitIndex;
            /// <summary>内核会复用槽位，所以要记住施加时的 LogicId 做校验。</summary>
            public int LogicId;
            public SimStatus Status;
            public float TimeLeft;
        }

        /// <summary>限时属性修正。到期时按来源 id 撤销。</summary>
        private struct StatEntry
        {
            public Stats.StatSheet Sheet;
            public int SourceId;
            public float TimeLeft;
        }

        private readonly List<Entry> _entries = new List<Entry>(256);
        private readonly List<StatEntry> _statEntries = new List<StatEntry>(64);
        private SimBridge _sim;
        private ReactionSystem _reaction;

        public int ActiveCount => _entries.Count;
        public int TimedStatCount => _statEntries.Count;

        /// <summary>
        /// 登记一条限时属性修正。修正器本身由调用方先加进 StatSheet，
        /// 本模块只负责到期时 RemoveBySource。
        ///
        /// 叠加语义（刻意如此，别当成 bug）：同来源重复触发会**各自往 StatSheet
        /// 加一条修正器**，但这里只保留一个计时条目并把时间刷到最长。
        /// 效果是"窗口期内连续触发可无上限叠加，然后一起消失"——
        /// 这正是"吞噬回响"这类连吃 build 想要的手感，由时间窗口自然封顶。
        /// 若某张卡需要每层独立计时或需要层数上限，得给每次触发分配唯一 id
        /// 并在这里改成允许多条目。
        /// </summary>
        public void RegisterTimedStat(Stats.StatSheet sheet, int sourceId, float duration)
        {
            if (sheet == null || duration <= 0f)
            {
                return;
            }

            // 同来源重复触发时刷新时间，而不是堆多条——否则连续触发会导致
            // 第一条到期就把后续所有层一起撤销
            for (int i = 0; i < _statEntries.Count; i++)
            {
                if (_statEntries[i].SourceId != sourceId || _statEntries[i].Sheet != sheet)
                {
                    continue;
                }
                StatEntry e = _statEntries[i];
                e.TimeLeft = math.max(e.TimeLeft, duration);
                _statEntries[i] = e;
                return;
            }

            _statEntries.Add(new StatEntry
            {
                Sheet = sheet,
                SourceId = sourceId,
                TimeLeft = duration,
            });
        }

        /// <summary>reaction 可为 null（反应矩阵未注册时状态计时仍要能独立工作）。</summary>
        public void Bind(SimBridge sim, ReactionSystem reaction = null)
        {
            _sim = sim;
            _reaction = reaction;
        }

        public override void OnEnter()
        {
            _entries.Clear();
            _statEntries.Clear();
        }

        public override void OnExit()
        {
            _entries.Clear();
            _statEntries.Clear();
        }

        /// <summary>
        /// 对单个单位施加限时状态。duration &lt;= 0 表示永久（由调用方自己管）。
        /// </summary>
        public void ApplyTimed(int unitIndex, SimStatus status, float duration)
        {
            if (_sim == null || !_sim.Running || status == SimStatus.None)
            {
                return;
            }

            SimSnapshot snap = _sim.Snapshot;
            if (_reaction != null && unitIndex >= 0 && unitIndex < snap.Count)
            {
                _reaction.TryReact(unitIndex, (SimStatus)snap.Status[unitIndex], status);
            }

            _sim.ApplyStatusUnit(unitIndex, status, true);

            if (duration <= 0f)
            {
                return;
            }

            int logicId = unitIndex >= 0 && unitIndex < snap.Count ? snap.LogicId[unitIndex] : 0;

            // 同一单位同一状态重复施加时刷新时间（取更长的），而不是堆两条
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].UnitIndex != unitIndex || _entries[i].Status != status)
                {
                    continue;
                }
                if (_entries[i].TimeLeft < duration)
                {
                    Entry e = _entries[i];
                    e.TimeLeft = duration;
                    e.LogicId = logicId;
                    _entries[i] = e;
                }
                return;
            }

            _entries.Add(new Entry
            {
                UnitIndex = unitIndex,
                LogicId = logicId,
                Status = status,
                TimeLeft = duration,
            });
        }

        /// <summary>
        /// 对范围内单位施加限时状态。
        /// 施加本身交给内核（它有空间哈希），本模块只负责登记到期时间。
        /// </summary>
        public void ApplyTimedArea(float2 origin, float radius, SimStatus status,
            float duration, SimFaction faction = SimFaction.Hostile)
        {
            if (_sim == null || !_sim.Running || status == SimStatus.None)
            {
                return;
            }

            // 登记范围内当前命中的单位，并在施加前对每个单位做一次反应判定。
            // 注意：内核的施加发生在下一次 Step，而这里读的是上一帧快照，
            // 所以边缘单位可能有一帧误差。对状态效果来说这个精度完全够，
            // 换取的是不必在内核里再维护一套计时结构。
            SimSnapshot snap = _sim.Snapshot;
            float r2 = radius * radius;
            bool register = duration > 0f;
            for (int i = 0; i < snap.Count; i++)
            {
                if (snap.Alive[i] == 0)
                {
                    continue;
                }
                if (faction != SimFaction.None && snap.Faction[i] != (byte)faction)
                {
                    continue;
                }
                if (math.distancesq(snap.Position[i], origin) > r2)
                {
                    continue;
                }
                _reaction?.TryReact(i, (SimStatus)snap.Status[i], status);
                if (register)
                {
                    Register(i, snap.LogicId[i], status, duration);
                }
            }

            _sim.ApplyStatusArea(origin, radius, status, true, faction);
        }

        private void Register(int unitIndex, int logicId, SimStatus status, float duration)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].UnitIndex != unitIndex || _entries[i].Status != status)
                {
                    continue;
                }
                if (_entries[i].TimeLeft < duration)
                {
                    Entry e = _entries[i];
                    e.TimeLeft = duration;
                    e.LogicId = logicId;
                    _entries[i] = e;
                }
                return;
            }

            _entries.Add(new Entry
            {
                UnitIndex = unitIndex,
                LogicId = logicId,
                Status = status,
                TimeLeft = duration,
            });
        }

        /// <summary>清除某单位的所有限时状态（蜕皮类效果用）。</summary>
        public void ClearTimed(int unitIndex)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].UnitIndex != unitIndex)
                {
                    continue;
                }
                _sim?.ApplyStatusUnit(unitIndex, _entries[i].Status, false);
                _entries.RemoveAt(i);
            }
        }

        public override void OnUpdate(float dt)
        {
            TickTimedStats(dt);

            if (_sim == null || !_sim.Running || _entries.Count == 0)
            {
                return;
            }

            SimSnapshot snap = _sim.Snapshot;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry e = _entries[i];

                // 单位已死或槽位被复用（LogicId 变了）→ 直接丢弃条目。
                // 少了这个校验，状态会被错误地从新生成的单位身上移除。
                if (e.UnitIndex < 0 || e.UnitIndex >= snap.Count
                    || snap.Alive[e.UnitIndex] == 0
                    || snap.LogicId[e.UnitIndex] != e.LogicId)
                {
                    _entries.RemoveAt(i);
                    continue;
                }

                e.TimeLeft -= dt;
                if (e.TimeLeft > 0f)
                {
                    _entries[i] = e;
                    continue;
                }

                _sim.ApplyStatusUnit(e.UnitIndex, e.Status, false);
                _entries.RemoveAt(i);
            }
        }

        private void TickTimedStats(float dt)
        {
            for (int i = _statEntries.Count - 1; i >= 0; i--)
            {
                StatEntry e = _statEntries[i];
                e.TimeLeft -= dt;
                if (e.TimeLeft > 0f)
                {
                    _statEntries[i] = e;
                    continue;
                }
                e.Sheet?.RemoveBySource(e.SourceId);
                _statEntries.RemoveAt(i);
            }
        }
    }
}
