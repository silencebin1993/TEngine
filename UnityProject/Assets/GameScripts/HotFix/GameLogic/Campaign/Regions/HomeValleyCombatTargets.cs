using System;
using System.Collections.Generic;
using System.Linq;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER4-PRIM-05 STORY-EXECUTION-CARDS.md 第1条："接管它命中低威胁残骸靶并从正式事件
    /// 看到聚焦差异"。
    ///
    /// ── 为什么需要这个类（DEBT-ER1R01-GRAPH-01 门禁） ──
    /// ER1-REUSE-01 基线登记的 DEBT-ER1R01-GRAPH-01："3×3 电路图链未接正式战斗，只有调试工具能跑"，
    /// 承接 Story 明确点名 ER4-PRIM-05、最迟门禁也是 ER4-PRIM-05。<see cref="BlueprintCircuitCompiler.CompilePreview"/>
    /// 类注释同样写明"本类只产出预览数据，不驱动真实战斗（不修改任何机器 HP/弹药/世界状态）"——
    /// 在本类之前，玩家在电路板上画的图链从未真正让任何东西掉血，这正是该 DEBT 的字面症状。
    /// 本类是"驱动"首次真正落地的地方。
    ///
    /// ── 不发明弹道/伤害算法 ──
    /// 伤害数值唯一真相仍然是 ComposeEngine（经 <see cref="BlueprintCircuitCompiler.CompilePreview"/> →
    /// <see cref="MachineLoadoutRegistry.ResolveForDirectControl"/>/<see cref="MachineLoadoutRegistry.ResolveForAi"/>
    /// 产出的 <see cref="BlueprintCircuitPreview.TotalNormalizedDamage"/>）——本类不重新计算一次伤害，
    /// 直接使用编译结果。归还谷地是纯管理场景，不运行 SimBridge/SimWorld 内核
    /// （见 <see cref="HomeValleyController"/> 类注释"移动走独立的 Transform 插值……不接 SimBridge——
    /// 避免在没把握的情况下往共享战斗内核里加东西"），ER5/ER6 区域战斗前不适合在这里现推一整套
    /// Sim 弹体/碰撞系统；本类新增的唯一"新算法"是"瞄准方向+射程"这个此前项目里完全不存在的
    /// 极简命中判定（不是伤害/角度/冷却算法的重复实现），仅服务于"验证同一装配在直控/AI下经同一
    /// 出口产出同一伤害"这一验收目的。
    ///
    /// ── 低威胁 = 可反复验证的靶标，不是一次性可摧毁物 ──
    /// PRIMITIVE-FULL-DEMO-SPEC.md §5.1 要求"先装基础镜……命中……再打印精校镜……再次命中……比较
    /// 同一目标/输入的签名和事件"——同一个目标需要在同一局内被命中至少两次。本类因此把它做成
    /// "命中后按 <see cref="RegenSeconds"/> 冷却自动满血复位"的训练靶，而不是打死即消失的敌人。</summary>
    public static class HomeValleyCombatTargets
    {
        public const string LowThreatTargetId = "home_valley_wreckage_target";
        public const float MaxHealth = 60f;
        /// <summary>命中判定的最大射程（世界单位）。归还谷地锚点间距量级在 10～20 之间（见
        /// <see cref="HomeValleyLayout"/>），这个值保证玩家接管机器站在目标附近时打得到，
        /// 不需要精确到像素级走位。</summary>
        public const float EngageRange = 12f;
        /// <summary>瞄准锥半角（度）。&gt;=180 即整圆，这里保留一个真实的"没瞄准方向就打不中"
        /// 判定，同 <c>GameLogic.Battle.SimBridge.DamageCone</c> 的锥形判定同一设计语言（不是复制
        /// 该方法实现，只是同一个"锥形命中"概念在归还谷地场景的独立最小实现）。</summary>
        public const float AimHalfAngleDeg = 60f;
        /// <summary>命中后到自动满血复位的冷却秒数。</summary>
        public const float RegenSeconds = 6f;

        /// <summary>一次命中/未命中结果，供 UI/日志/测试读取——STORY-EXECUTION-CARDS.md
        /// "UI/VFX/SFX/日志与同一事件匹配"要求 UI 与日志展示的是同一份数据，不是各自另算。</summary>
        public readonly struct HitResult
        {
            public readonly bool Success;
            public readonly string FailureReason;
            public readonly float DamageApplied;
            public readonly float RemainingHealth;
            public readonly float MaxHealthValue;
            public readonly string ReactionHint;
            public readonly string CompileSignature;
            public readonly bool IsAiSource;
            public readonly int AttackerLogicId;

            private HitResult(bool success, string failureReason, float damageApplied, float remainingHealth,
                float maxHealthValue, string reactionHint, string compileSignature, bool isAiSource, int attackerLogicId)
            {
                Success = success;
                FailureReason = failureReason;
                DamageApplied = damageApplied;
                RemainingHealth = remainingHealth;
                MaxHealthValue = maxHealthValue;
                ReactionHint = reactionHint;
                CompileSignature = compileSignature;
                IsAiSource = isAiSource;
                AttackerLogicId = attackerLogicId;
            }

            public static HitResult Ok(float damageApplied, float remainingHealth, float maxHealthValue,
                string reactionHint, string compileSignature, bool isAiSource, int attackerLogicId) =>
                new HitResult(true, null, damageApplied, remainingHealth, maxHealthValue, reactionHint,
                    compileSignature, isAiSource, attackerLogicId);

            public static HitResult Fail(string reason, bool isAiSource, int attackerLogicId) =>
                new HitResult(false, reason, 0f, 0f, 0f, null, null, isAiSource, attackerLogicId);

            public string Summarize()
            {
                if (!Success)
                {
                    return $"未命中：{FailureReason}";
                }
                string source = IsAiSource ? $"AI(机器{AttackerLogicId})" : $"直控(机器{AttackerLogicId})";
                return $"{source} 命中，伤害 {DamageApplied:F1}，剩余 {RemainingHealth:F1}/{MaxHealthValue:F0}，" +
                    $"{ReactionHint}，签名 {CompileSignature}";
            }
        }

        /// <summary>最近命中事件环形缓冲（不落盘——纯运行时展示用，同 UI 面板既有"最近3笔收支"先例，
        /// 见 <see cref="UI.Common.EconomyHudToolkit"/>）。容量 20 条足够一局内的验收窗口回看。</summary>
        private const int MaxRecentEvents = 20;
        private static readonly List<HitResult> _recentEvents = new List<HitResult>(MaxRecentEvents);
        public static IReadOnlyList<HitResult> RecentEvents => _recentEvents;

        /// <summary>FG0-ARCH-01：会话开始时清空上一局的最近命中展示缓冲。</summary>
        public static void ResetSessionState()
        {
            _recentEvents.Clear();
        }

        private static void PushEvent(HitResult result)
        {
            _recentEvents.Add(result);
            if (_recentEvents.Count > MaxRecentEvents)
            {
                _recentEvents.RemoveAt(0);
            }
            Log.Info($"[HomeValleyCombatTargets] {result.Summarize()}");
        }

        /// <summary>首次进入才生成唯一的低威胁残骸靶；已存在（第二次进入/读档）原样跳过，
        /// 与 <see cref="Primitive.PrimitiveInventory.EnsureSeeded"/> 同一幂等纪律。</summary>
        public static void EnsureSeeded(CampaignState state)
        {
            if (state == null)
            {
                return;
            }
            state.CombatTargets ??= Array.Empty<CombatTargetRecord>();
            if (state.CombatTargets.Any(t => t.TargetId == LowThreatTargetId))
            {
                return;
            }

            var target = new CombatTargetRecord
            {
                TargetId = LowThreatTargetId,
                RegionId = HomeValleyLayout.RegionId,
                Position = HomeValleyLayout.LowThreatTargetPosition,
                Health = MaxHealth,
                MaxHealth = MaxHealth,
                RegenCooldownRemaining = 0f,
            };
            state.CombatTargets = state.CombatTargets.Append(target).ToArray();
            Log.Info("[HomeValleyCombatTargets] 已播种低威胁残骸靶。");
        }

        public static CombatTargetRecord Find(CampaignState state, string targetId) =>
            state?.CombatTargets?.FirstOrDefault(t => t.TargetId == targetId);

        /// <summary>FG0-ARCH-03：训练靶在家园战斗内核里的单位（中立、血量在记录里）。自动交战的射程与间隔判定在内核，
        /// 命中结算仍走 <see cref="TryAttack"/>（结算后回写镜像）。</summary>
        public static int EnsureDummyUnit(GameLogic.Campaign.Combat.CombatSite site, CampaignState state)
        {
            CombatTargetRecord t = Find(state, LowThreatTargetId);
            if (site == null || t == null)
            {
                return 0;
            }
            if (site.TryGetEnemyUnit(LowThreatTargetId, out int existing))
            {
                return existing;
            }
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = LowThreatTargetId,
                RegionId = HomeValleyLayout.RegionId,
                EnemyTypeId = LowThreatTargetId,
                Position = t.Position,
                Health = t.Health,
                MaxHealth = t.MaxHealth,
                IsAlive = true,
            };
            int unit = site.SpawnEnemy(rec, new BinGames.Sim.Combat.CombatSpawn
            {
                Kind = BinGames.Sim.Combat.CombatUnitKind.Structure,
                Faction = BinGames.Sim.Combat.CombatFaction.Neutral,
                Behavior = BinGames.Sim.Combat.CombatBehavior.None,
                Flags = BinGames.Sim.Combat.CombatUnitFlags.Alive | BinGames.Sim.Combat.CombatUnitFlags.Targetable | BinGames.Sim.Combat.CombatUnitFlags.ExternalHealth,
                Radius = 1f,
                Weapon = -1,
                BehaviorProfile = -1,
                Priority = 1,
                ArmorHalfAngleDeg = 90f,
                ArmorFacing = new Unity.Mathematics.float2(0f, -1f),
                Home = new Unity.Mathematics.double2(t.Position.x, t.Position.y),
            });
            site.SetEngage(unit, EngageRange, 5f);
            return unit;
        }

        /// <summary>把训练靶记录的血量写进内核镜像（命中结算、被动再生之后；O(1)）。</summary>
        public static void SyncDummy(GameLogic.Campaign.Combat.CombatSite site, CampaignState state)
        {
            CombatTargetRecord t = Find(state, LowThreatTargetId);
            if (site == null || t == null || !site.TryGetEnemyUnit(LowThreatTargetId, out int unit))
            {
                return;
            }
            // 打空即“不可选中”（编队攻击随之结束、自动交战不再尝试）；再生满血后恢复。
            site.SetUnitHealth(unit, t.Health, t.MaxHealth, t.Health > 0f);
        }

        /// <summary>被动再生：命中冷却结束后自动满血复位，供 <see cref="HomeValleyController.Update"/>
        /// 逐帧驱动（目标数量个位数，O(1) 量级，不违反热更层性能纪律）。</summary>
        public static void Tick(CampaignState state, float dt)
        {
            if (state?.CombatTargets == null || dt <= 0f)
            {
                return;
            }
            foreach (CombatTargetRecord target in state.CombatTargets)
            {
                if (target.Health >= target.MaxHealth || target.RegenCooldownRemaining <= 0f)
                {
                    continue;
                }
                target.RegenCooldownRemaining = Mathf.Max(0f, target.RegenCooldownRemaining - dt);
                if (target.RegenCooldownRemaining <= 0f)
                {
                    target.Health = target.MaxHealth;
                }
            }
        }

        /// <summary>瞄准判定：从 <paramref name="origin"/> 朝 <paramref name="aimDirection"/> 方向，
        /// 在 <see cref="EngageRange"/>/<see cref="AimHalfAngleDeg"/> 内找一个存活（<see cref="CombatTargetRecord.Health"/>
        /// &gt; 0）的目标。找不到返回 null（合法状态——"没瞄准到"是负向路径之一，不是错误）。</summary>
        public static CombatTargetRecord TryFindTargetInAim(CampaignState state, Vector2 origin, Vector2 aimDirection)
        {
            if (state?.CombatTargets == null || aimDirection.sqrMagnitude < 1e-6f)
            {
                return null;
            }
            Vector2 dirNorm = aimDirection.normalized;
            float cosHalf = Mathf.Cos(AimHalfAngleDeg * Mathf.Deg2Rad);

            foreach (CombatTargetRecord target in state.CombatTargets)
            {
                if (target.RegionId != HomeValleyLayout.RegionId || target.Health <= 0f)
                {
                    continue;
                }
                Vector2 toTarget = target.Position - origin;
                float dist = toTarget.magnitude;
                if (dist > EngageRange || dist < 0.01f)
                {
                    continue;
                }
                float cosAngle = Vector2.Dot(dirNorm, toTarget.normalized);
                if (cosAngle >= cosHalf)
                {
                    return target;
                }
            }
            return null;
        }

        /// <summary>唯一命中结算入口。<paramref name="isAiSource"/> 只决定调用
        /// <see cref="MachineLoadoutRegistry.ResolveForAi"/> 还是 <see cref="MachineLoadoutRegistry.ResolveForDirectControl"/>
        /// ——两者内部是同一个 <see cref="MachineLoadoutRegistry.Resolve"/> 实现，AC-REA-003"AI/玩家
        /// 使用同装配"在结构上成立，不是靠约定。</summary>
        public static HitResult TryAttack(CampaignState state, int attackerLogicId, string targetId, int seed, bool isAiSource)
        {
            if (state == null)
            {
                return HitResult.Fail("没有活动的归还谷地会话。", isAiSource, attackerLogicId);
            }
            CombatTargetRecord target = Find(state, targetId);
            if (target == null)
            {
                return HitResult.Fail($"目标 {targetId} 不存在。", isAiSource, attackerLogicId);
            }
            if (target.Health <= 0f)
            {
                return HitResult.Fail("目标当前处于再生冷却中（HP=0），暂不可命中。", isAiSource, attackerLogicId);
            }

            MachineCombatResolution resolution = isAiSource
                ? MachineLoadoutRegistry.ResolveForAi(state, attackerLogicId, seed)
                : MachineLoadoutRegistry.ResolveForDirectControl(state, attackerLogicId, seed);

            if (!resolution.Success)
            {
                var fail = HitResult.Fail(resolution.FailureReason, isAiSource, attackerLogicId);
                PushEvent(fail);
                return fail;
            }
            if (!resolution.Preview.HasCombatOutput)
            {
                var fail = HitResult.Fail("当前装配没有可攻击的主武器出口（8号汇槽为空）。", isAiSource, attackerLogicId);
                PushEvent(fail);
                return fail;
            }

            float damage = Mathf.Max(0f, resolution.Preview.TotalNormalizedDamage);
            target.Health = Mathf.Max(0f, target.Health - damage);
            if (target.Health <= 0f)
            {
                target.RegenCooldownRemaining = RegenSeconds;
            }

            var ok = HitResult.Ok(damage, target.Health, target.MaxHealth,
                resolution.Preview.ReactionHint, resolution.CompileSignature, isAiSource, attackerLogicId);
            PushEvent(ok);
            if (!isAiSource)
            {
                // ER8 / OBJ-03“建造第一台战斗机并接管”：首次直控命中低威胁靶的一次性事件（幂等）。
                CampaignEventLedger.TryGrant(state, CampaignObjectiveCatalog.DirectHitEventId, "DirectHit", state.PlaySeconds, targetId);
            }
            return ok;
        }

        /// <summary>负向矩阵自检：目标记录恰好一条，不因重复 Enter/Exit 产生重复条目
        /// （同 <see cref="HomeValleyController.SelfCheckNoDuplicates"/> 先例）。</summary>
        public static List<string> SelfCheckNoDuplicates(CampaignState state)
        {
            var violations = new List<string>();
            int count = state?.CombatTargets?.Count(t => t.TargetId == LowThreatTargetId) ?? 0;
            if (count != 1)
            {
                violations.Add($"低威胁残骸靶记录数应为 1，实际 {count}。");
            }
            return violations;
        }
    }
}
