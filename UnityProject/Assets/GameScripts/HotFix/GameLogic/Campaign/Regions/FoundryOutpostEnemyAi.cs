using System.Collections.Generic;
using GameLogic.Campaign.Content;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-FOUNDRY-01：铸造护甲机/步进炮/维修机真实 AI 行为，与
    /// <see cref="FracturedCityEnemyAi"/> 同一委托范式——本类只做决策并转发到
    /// <see cref="FoundryOutpostRegion"/> 正式结算入口写状态，不在 Controller 里散落改
    /// <see cref="RegionEnemyRecord"/> 战斗字段。
    ///
    /// ── 数值来源（写入证据文档）──
    /// DEMO-CONTENT-LOCK.md §5 只给了三类敌人的 HP（160/110/80）、护甲机正面减伤40%、步进炮1秒瞄准线
    /// 三项数字；验收卡追加"护甲机守左右掩体/步进炮守重炮回收点/维修机优先救低血同伴/不能出生点无
    /// 预告秒杀"四条行为要求，未点名具体伤害/射程/冷却/移动速度——本类取舍与
    /// <see cref="FracturedCityEnemyAi"/> 同一先例（保守量级 judgment call，非文档摘录，集中写在
    /// <see cref="FoundryOutpostLayout"/> 的"敌人 AI 调校"分区，可独立复核/调参）：
    /// - 护甲机驻守左右掩体不移动（"守"字面行为），固定朝向入口方向，命中方向决定是否触发正面减伤。
    /// - 步进炮驻守重炮回收点不移动，两阶段循环：先进入1秒瞄准线（<see cref="RegionEnemyRecord.SecondaryTimer"/>
    ///   倒数——"先给瞄准线...不能无预告秒杀"字面要求），倒数结束才真正开火，随后进入攻击冷却
    ///   （<see cref="RegionEnemyRecord.CycleCooldownRemaining"/>）。
    /// - 维修机不攻击，优先移动到全场（含自己）血量百分比最低的存活友军身边治疗；玩家进入威胁距离时
    ///   后撤（支援单位不硬扛，同静默侦察机"后撤"先例），不给它攻击手段——"优先救低血同伴"是唯一
    ///   点名的行为，本类不额外发明攻击动词。</summary>
    public static class FoundryOutpostEnemyAi
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

        public delegate bool LineOfSightCheck(Vector2 from, Vector2 to);

        public static void Tick(CampaignState state, float dt, IReadOnlyList<VisibleMachine> machines, LineOfSightCheck hasLineOfSight)
        {
            if (state?.RegionEnemies == null || dt <= 0f)
            {
                return;
            }

            foreach (RegionEnemyRecord enemy in state.RegionEnemies)
            {
                if (enemy.RegionId != FoundryOutpostRegion.RegionId || !enemy.IsAlive)
                {
                    continue;
                }
                if (enemy.EnemyTypeId == EnemyCatalog.ArmorBotId)
                {
                    TickArmorBot(state, enemy, dt, machines, hasLineOfSight);
                }
                else if (enemy.EnemyTypeId == EnemyCatalog.StriderId)
                {
                    TickStrider(state, enemy, dt, machines, hasLineOfSight);
                }
                else if (enemy.EnemyTypeId == EnemyCatalog.RepairBotId)
                {
                    TickRepairBot(state, enemy, dt, machines);
                }
            }
        }

        // ── 铸造护甲机：驻守掩体 + 自卫攻击 ────────────────────────────────

        private static void TickArmorBot(CampaignState state, RegionEnemyRecord enemy, float dt,
            IReadOnlyList<VisibleMachine> machines, LineOfSightCheck hasLineOfSight)
        {
            enemy.CycleCooldownRemaining -= dt;
            if (enemy.CycleCooldownRemaining > 0f)
            {
                return;
            }
            VisibleMachine? target = FindNearestVisible(enemy.Position, machines, hasLineOfSight, FoundryOutpostLayout.ArmorBotAttackRange);
            if (!target.HasValue)
            {
                return; // 无目标不重置冷却，目标一出现立刻可以开火。
            }
            enemy.CycleCooldownRemaining = FoundryOutpostLayout.ArmorBotAttackCooldownSeconds;
            FoundryOutpostRegion.TryEnemyAttackMachine(state, enemy.EnemyInstanceId, target.Value.LogicId, FoundryOutpostLayout.ArmorBotAttackDamage);
        }

        // ── 铸造步进炮：驻守回收点 + 瞄准线→开火两阶段循环 ─────────────────

        private static void TickStrider(CampaignState state, RegionEnemyRecord enemy, float dt,
            IReadOnlyList<VisibleMachine> machines, LineOfSightCheck hasLineOfSight)
        {
            if (enemy.SecondaryTimer > 0f)
            {
                // 已进入瞄准阶段：倒数到 0 才真正开火，不再重新判定目标是否离场——"预告"过的攻击如期
                // 而至，不能靠移动躲开已经瞄准好的一击（同 CannonCombat 两阶段调用的确定性承诺）。
                enemy.SecondaryTimer -= dt;
                if (enemy.SecondaryTimer > 0f)
                {
                    return;
                }
                VisibleMachine? lockedTarget = FindNearestVisible(enemy.Position, machines, hasLineOfSight, FoundryOutpostLayout.StriderAttackRange);
                enemy.CycleCooldownRemaining = FoundryOutpostLayout.StriderAttackCooldownSeconds;
                if (lockedTarget.HasValue)
                {
                    FoundryOutpostRegion.TryEnemyAttackMachine(state, enemy.EnemyInstanceId, lockedTarget.Value.LogicId, FoundryOutpostLayout.StriderAttackDamage);
                    Log.Info($"[FoundryOutpostEnemyAi] {enemy.EnemyInstanceId} 瞄准线结束，开火命中 {lockedTarget.Value.LogicId}。");
                }
                else
                {
                    Log.Info($"[FoundryOutpostEnemyAi] {enemy.EnemyInstanceId} 瞄准线结束，但目标已脱离射程/视线，本次落空。");
                }
                return;
            }

            enemy.CycleCooldownRemaining -= dt;
            if (enemy.CycleCooldownRemaining > 0f)
            {
                return;
            }
            VisibleMachine? target = FindNearestVisible(enemy.Position, machines, hasLineOfSight, FoundryOutpostLayout.StriderAttackRange);
            if (!target.HasValue)
            {
                return; // 无目标时不进入瞄准阶段，也不重置冷却——目标一出现下一帧立刻可以开始瞄准。
            }
            enemy.SecondaryTimer = FoundryOutpostLayout.StriderAimSeconds;
            Log.Info($"[FoundryOutpostEnemyAi] {enemy.EnemyInstanceId} 发现目标 {target.Value.LogicId}，进入1秒瞄准线。");
        }

        // ── 铸造维修机：移动救援全场血量百分比最低的存活友军 + 威胁后撤 ──────

        private static void TickRepairBot(CampaignState state, RegionEnemyRecord enemy, float dt, IReadOnlyList<VisibleMachine> machines)
        {
            // 威胁后撤：玩家机器进入触发距离时优先后撤（支援单位不硬扛），后撤期间暂停救援移动。
            VisibleMachine? threat = null;
            if (machines != null)
            {
                float bestDist = FoundryOutpostLayout.RepairBotFleeTriggerRange;
                foreach (VisibleMachine m in machines)
                {
                    float dist = Vector2.Distance(enemy.Position, m.Position);
                    if (dist <= bestDist)
                    {
                        bestDist = dist;
                        threat = m;
                    }
                }
            }
            if (threat.HasValue)
            {
                Vector2 away = enemy.Position - threat.Value.Position;
                if (away.sqrMagnitude > 0.0001f)
                {
                    Vector2 dir = away.normalized;
                    Vector2 proposed = enemy.Position + dir * (FoundryOutpostLayout.RepairBotMoveSpeed * dt);
                    if (Vector2.Distance(proposed, FoundryOutpostLayout.RepairBotSpawn.Position) <= FoundryOutpostLayout.RepairBotFleeLeash)
                    {
                        enemy.Position = proposed;
                    }
                }
                return;
            }

            // 救援目标：全场（不含自己以外，含自己）血量百分比最低的存活友军。
            RegionEnemyRecord lowestAlly = null;
            float lowestPct = float.MaxValue;
            if (state.RegionEnemies != null)
            {
                foreach (RegionEnemyRecord candidate in state.RegionEnemies)
                {
                    if (candidate.RegionId != FoundryOutpostRegion.RegionId || !candidate.IsAlive || candidate.MaxHealth <= 0f)
                    {
                        continue;
                    }
                    float pct = candidate.Health / candidate.MaxHealth;
                    if (pct < lowestPct)
                    {
                        lowestPct = pct;
                        lowestAlly = candidate;
                    }
                }
            }
            if (lowestAlly == null || lowestPct >= 1f)
            {
                return; // 没有需要救援的目标（含自己在内全部满血）。
            }

            float dist2 = Vector2.Distance(enemy.Position, lowestAlly.Position);
            if (dist2 > FoundryOutpostLayout.RepairBotHealRange)
            {
                if (lowestAlly.EnemyInstanceId != enemy.EnemyInstanceId)
                {
                    Vector2 toAlly = lowestAlly.Position - enemy.Position;
                    if (toAlly.sqrMagnitude > 0.0001f)
                    {
                        Vector2 step = toAlly.normalized * Mathf.Min(FoundryOutpostLayout.RepairBotMoveSpeed * dt, toAlly.magnitude);
                        enemy.Position += step;
                    }
                }
                return;
            }

            enemy.CycleCooldownRemaining -= dt;
            if (enemy.CycleCooldownRemaining > 0f)
            {
                return;
            }
            enemy.CycleCooldownRemaining = FoundryOutpostLayout.RepairBotHealCooldownSeconds;
            if (FoundryOutpostRegion.TryHealEnemy(state, lowestAlly.EnemyInstanceId, FoundryOutpostLayout.RepairBotHealAmount))
            {
                Log.Info($"[FoundryOutpostEnemyAi] {enemy.EnemyInstanceId} 治疗 {lowestAlly.EnemyInstanceId} +{FoundryOutpostLayout.RepairBotHealAmount:F0}。");
            }
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
                    continue;
                }
                bestDist = dist;
                best = m;
            }
            return best;
        }
    }
}
