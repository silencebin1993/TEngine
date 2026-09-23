using System;
using System.Collections.Generic;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-SILENT-01：静默侦察机/静默干扰机的真实 AI 行为——移动、目标选择、标记/清标记、
    /// 攻击全部经本类驱动，真正的状态写入仍然转发到 <see cref="FracturedCityRegion"/> 的正式结算入口
    /// （<see cref="FracturedCityRegion.TryEnemyAttackMachine"/>/<see cref="FracturedCityRegion.TryMarkMachine"/>/
    /// <see cref="FracturedCityRegion.TryClearMark"/>/<see cref="FracturedCityRegion.TryDamageEnemy"/>），
    /// 不在 Controller 里散落直接改 <see cref="RegionEnemyRecord"/> 的战斗相关字段——本类只是需要
    /// Controller 提供"看得到哪些存活友军机器"这一份实时传感器数据（<see cref="VisibleMachine"/>/
    /// <see cref="LineOfSightCheck"/>），因为只有 Controller 持有 <see cref="HomeValleyMachineMarker"/>
    /// 的真实 Transform；位置/冷却字段仍然由本类直接写（同 <see cref="FracturedCityRegion.TickEnemies"/>
    /// 已有的"个位数敌人，O(1) 量级"纪律，不违反热更层性能红线）。
    ///
    /// ── 数值来源（重要，写入证据文档）──
    /// STORY-EXECUTION-CARDS.md ER5-SILENT-01 第2条要求敌人真实能"移动/选目标/攻击"，但
    /// DEMO-CONTENT-LOCK.md §5 只给了 HP70/90、标记间隔8秒、干扰半径12米三个数字，未点名静默侦察/
    /// 干扰机的武器伤害/射程/冷却/移动速度；ERD-ENY-001 职责表把侦察机写成"标记、后撤、呼叫干扰"、
    /// 干扰机写成"建立接管拒绝区、清除标记"，均未列出攻击动词。本类的取舍：
    /// - 侦察机不主动开火，只标记+后撤，忠实保留文档字面（"标记不魔法穿墙"用视线遮挡判定）。
    /// - 干扰机作为"驻守监听节点"的防御单位，给一把明显弱于玩家基础武器（连射器 8伤害/0.45秒冷却）
    ///   的自卫攻击——这是本 Story 明确要解决 DEBT-ER5CTL01-03/DEBT-ER5CMD01-01
    ///   "没有真正会杀死友军的敌方战斗 AI" 这一要求下的取舍，干扰机单独一个真实攻击源已足够让两条
    ///   DEBT 的验证缺口被真实覆盖，不需要给纯侦察定位的单位也强行加一把文档没写的武器。
    /// 具体数值全部集中在 <see cref="FracturedCityLayout"/> 的"敌人 AI 调校"分区，可独立复核/调参。</summary>
    public static class FracturedCityEnemyAi
    {
        public readonly struct VisibleMachine
        {
            public readonly int LogicId;
            public readonly Vector2 Position;

            public VisibleMachine(int logicId, Vector2 position)
            {
                LogicId = logicId;
                Position = position;
            }
        }

        /// <summary>视线遮挡判定委托——由 Controller 提供（复用它已经建好的锚点净空障碍物列表），
        /// 本类不重复实现碰撞判定，只消费结果，符合"标记不魔法穿墙"。</summary>
        public delegate bool LineOfSightCheck(Vector2 from, Vector2 to);

        /// <summary>本帧成功触发"标记"效果的敌人实例 ID——供 Controller 播放扫描脉冲可视化反馈
        /// （失去听觉时的"扫描线"读法）。每次 Tick 调用前清空，调用方不需要自己维护生命周期。</summary>
        public static readonly List<string> MarkedThisTick = new List<string>(4);

        public static void Tick(CampaignState state, float dt, IReadOnlyList<VisibleMachine> machines, LineOfSightCheck hasLineOfSight)
        {
            MarkedThisTick.Clear();
            if (state?.RegionEnemies == null || dt <= 0f)
            {
                return;
            }

            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != FracturedCityRegion.RegionId || !enemy.IsAlive)
                {
                    continue;
                }
                if (enemy.EnemyTypeId == EnemyCatalog.ScoutId)
                {
                    TickScout(state, enemy, dt, machines, hasLineOfSight);
                }
                else if (enemy.EnemyTypeId == EnemyCatalog.JammerId)
                {
                    TickJammer(state, enemy, dt, machines, hasLineOfSight);
                }
            }

            FracturedCityRegion.SweepExpiredMarks(state);
        }

        // ── 静默侦察机：标记（视线内）+ 后撤（受威胁时）+ 巡逻（空闲时）────────────

        private static void TickScout(CampaignState state, RegionEnemyRecord enemy, float dt,
            IReadOnlyList<VisibleMachine> machines, LineOfSightCheck hasLineOfSight)
        {
            VisibleMachine? nearest = FindNearestVisible(enemy.Position, machines, hasLineOfSight, FracturedCityLayout.ScoutMarkRange);
            VisibleMachine? threat = nearest.HasValue &&
                Vector2.Distance(enemy.Position, nearest.Value.Position) <= FracturedCityLayout.ScoutFleeTriggerRange
                ? nearest : (VisibleMachine?)null;

            // 移动：受威胁则后撤（不超过出生点的 leash 半径），否则在出生点附近巡逻摆动。
            Vector2 spawn = enemy.EnemyInstanceId == FracturedCityLayout.Scout1SpawnId
                ? FracturedCityLayout.Scout1Spawn.Position
                : FracturedCityLayout.Scout2Spawn.Position;

            if (threat.HasValue)
            {
                Vector2 away = (enemy.Position - threat.Value.Position);
                if (away.sqrMagnitude > 0.0001f)
                {
                    Vector2 dir = away.normalized;
                    Vector2 proposed = enemy.Position + dir * (FracturedCityLayout.ScoutMoveSpeed * dt);
                    if (Vector2.Distance(proposed, spawn) <= FracturedCityLayout.ScoutFleeLeash)
                    {
                        enemy.Position = proposed;
                    }
                }
            }
            else
            {
                // 简单确定性巡逻：沿 X 轴以固定周期来回摆动，不依赖 RNG（保证测试可复现）。
                float phase = Mathf.Sin(state.PlaySeconds * 0.6f);
                Vector2 patrolTarget = spawn + new Vector2(phase * FracturedCityLayout.ScoutPatrolRadius, 0f);
                Vector2 toTarget = patrolTarget - enemy.Position;
                if (toTarget.sqrMagnitude > 0.01f)
                {
                    Vector2 step = toTarget.normalized * Mathf.Min(FracturedCityLayout.ScoutMoveSpeed * dt, toTarget.magnitude);
                    enemy.Position += step;
                }
            }

            // 标记周期：冷却到点才判定一次，即使没有目标也重置冷却（"每8秒"节奏本身是稳定的，
            // 只有"是否真的命中标记"取决于当下是否存在视线内目标——不魔法穿墙的字面体现）。
            enemy.CycleCooldownRemaining -= dt;
            if (enemy.CycleCooldownRemaining > 0f)
            {
                return;
            }
            enemy.CycleCooldownRemaining = FracturedCityLayout.ScoutMarkIntervalSeconds;

            if (nearest.HasValue)
            {
                FracturedCityRegion.TryMarkMachine(state, nearest.Value.LogicId, FracturedCityLayout.MarkDurationSeconds);
                FracturedCityRegion.BumpAlertFromMark(state); // ERD-ENY-001"呼叫干扰"的最小可用代理。
                MarkedThisTick.Add(enemy.EnemyInstanceId);
                Log.Info($"[FracturedCityEnemyAi] {enemy.EnemyInstanceId} 天线扫描命中机器 {nearest.Value.LogicId}，已标记。");
            }
            else
            {
                Log.Info($"[FracturedCityEnemyAi] {enemy.EnemyInstanceId} 天线扫描周期触发，但视线内无目标，未产生标记。");
            }
        }

        // ── 静默干扰机：驻守节点（不移动）+ 清除半径内标记 + 自卫攻击 ─────────────────

        private static void TickJammer(CampaignState state, RegionEnemyRecord enemy, float dt,
            IReadOnlyList<VisibleMachine> machines, LineOfSightCheck hasLineOfSight)
        {
            // 驻守：不移动（DEMO-CONTENT-LOCK.md"一台静默干扰机守监听节点"）。

            if (machines != null)
            {
                foreach (VisibleMachine m in machines)
                {
                    if (Vector2.Distance(enemy.Position, m.Position) <= FracturedCityLayout.JammerRadius)
                    {
                        FracturedCityRegion.TryClearMark(state, m.LogicId);
                    }
                }
            }

            // CycleCooldownRemaining 对干扰机复用为"自卫攻击冷却"（同一字段，语义按 EnemyTypeId
            // 区分，见 RegionEnemyRecord 类注释）。
            enemy.CycleCooldownRemaining -= dt;
            if (enemy.CycleCooldownRemaining > 0f)
            {
                return;
            }

            VisibleMachine? target = FindNearestVisible(enemy.Position, machines, hasLineOfSight, FracturedCityLayout.JammerAttackRange);
            if (!target.HasValue)
            {
                return; // 没有可攻击目标时不重置冷却，一旦目标出现立刻可以开火，不需要再等一个满周期。
            }

            enemy.CycleCooldownRemaining = FracturedCityLayout.JammerAttackCooldownSeconds;
            FracturedCityRegion.TryEnemyAttackMachine(state, enemy.EnemyInstanceId, target.Value.LogicId, FracturedCityLayout.JammerAttackDamage);
        }

        private static VisibleMachine? FindNearestVisible(Vector2 from, IReadOnlyList<VisibleMachine> machines,
            LineOfSightCheck hasLineOfSight, float maxRange)
        {
            if (machines == null)
            {
                return null;
            }
            VisibleMachine? best = null;
            float bestDist = maxRange;
            foreach (VisibleMachine m in machines)
            {
                float dist = Vector2.Distance(from, m.Position);
                if (dist > bestDist)
                {
                    continue;
                }
                if (hasLineOfSight != null && !hasLineOfSight(from, m.Position))
                {
                    continue; // "标记不魔法穿墙"：视线被锚点净空障碍遮挡时不算看见。
                }
                bestDist = dist;
                best = m;
            }
            return best;
        }
    }
}
