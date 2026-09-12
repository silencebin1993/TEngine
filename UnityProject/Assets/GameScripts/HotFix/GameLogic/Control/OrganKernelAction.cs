using BinGames.Sim;
using GameLogic.Core;
using GameLogic.MetabolicSlice.ContentCatalog;
using GameLogic.MetabolicSlice.Structural;

namespace GameLogic.Control
{
    /// <summary>
    /// 一件器官被直控释放时，落到内核的那**一次**请求是什么形状（M2-03b）。
    ///
    /// 只有四种：弹体 / 扇形 / 持续区域 / 范围状态。它们全部已经是
    /// <see cref="GameLogic.Battle.SimBridge"/> 上的既有入口，本段**没有给内核加任何能力**。
    /// </summary>
    public enum OrganKernelActionKind : byte
    {
        /// <summary>该器官没有可释放的内核动作（未登记 / 只有被动效果 / 已退役）。</summary>
        None = 0,
        /// <summary>内核真弹体（<c>SimBridge.FireProjectile</c>）。</summary>
        Projectile = 1,
        /// <summary>面朝扇形范围伤害（<c>SimBridge.DamageCone</c>）。</summary>
        Cone = 2,
        /// <summary>持续区域（<c>SimBridge.SpawnZone</c>），可钉在瞄准点也可跟随自身。</summary>
        Zone = 3,
        /// <summary>范围状态施加，无伤害（<c>StatusSystem.ApplyTimedArea</c> / <c>SimBridge.ApplyStatusArea</c>）。</summary>
        Status = 4,
    }

    /// <summary>一次器官释放的内核请求参数。全部字段由器官自己的目录条目推导，调用方不得就地编数。</summary>
    public struct OrganKernelAction
    {
        public string OrganId;
        public OrganKernelActionKind Kind;

        /// <summary>弹体半径 / 扇形半径 / 区域半径 / 状态范围半径。</summary>
        public float Radius;
        public float Damage;
        public float Speed;
        public float Lifetime;
        /// <summary>区域或状态的持续秒数。</summary>
        public float Seconds;
        /// <summary>区域的结算间隔。</summary>
        public float Interval;
        public float HalfAngleDeg;
        public int Pierce;
        /// <summary>区域是否跟随释放者（光环 / 跟随召唤）。false = 钉在瞄准点。</summary>
        public bool FollowSelf;
        public SimStatus ApplyStatus;

        public bool IsValid => Kind != OrganKernelActionKind.None;

        public static readonly OrganKernelAction None = default;
    }

    /// <summary>
    /// 器官 id → 一次内核请求（M2-03b）。
    ///
    /// ── 为什么不直接复用 <c>MetabolicSliceBridge</c> 那条路 ──
    /// 那条路从头到尾钉死在玩家本体上：<c>MetabolicSlicePanel.Instance</c> 取装配、
    /// <c>ApplyChassisDamage</c> 里 <c>origin = _sim.PlayerPosition</c>、节奏由一个全局
    /// <c>_timer</c> 驱动。把它改成"任意实体都能用"是一次独立的大重构，
    /// 本段碰它必然超时返工（任务书明确列为非目标）。
    ///
    /// ── 那么这张表凭什么算"与器官对应" ──
    /// 判据**全部**来自 <see cref="OrganelleCatalog"/> 里那件器官自己的条目：
    /// 分类（Attack / Structural）、<see cref="OrganelleDef.AttackFamily"/>、
    /// <see cref="OrganelleDef.TriggerHook"/> 的 Tag/半径/秒数，以及 Luban
    /// <c>cell.OrganModuleParams</c> 行。没有任何一条分支是按 organId 写死的特例，
    /// 所以"这个单位为什么能打出这一发"永远追得到来源——这正是 M2-03 的立论。
    ///
    /// ── 已知降级（如实记录）──
    /// 召唤底盘（<c>SummonFollow</c> / <c>SummonAnchor</c>）在这里落成**区域**而不是真召唤：
    /// 被召唤的 archetype id 只存在于 ComposeEngine 模块实例内部（<c>SummonModule.summonId</c>），
    /// <see cref="OrganelleDef"/> 没有把它暴露出来。凭空指定一个 archetype 等于发明内容，
    /// 所以取语义最近的内核原语：Anchor = 钉在瞄准点的固定杀伤区（"钉一根菌丝炮台"），
    /// Follow = 跟随自身的杀伤区。等召唤 id 有了正式出口再换成 <c>SimBridge.Spawn</c>。
    /// </summary>
    public static class OrganKernelActionTable
    {
        /// <summary>目录/表里没给半径时的兜底。取值与 <c>StructuralHookRunner</c> 的留坑半径同量级。</summary>
        public const float DefaultRadius = 3.5f;

        /// <summary>
        /// 兜底伤害。**刻意是一个常数**：器官的伤害数值今天只在 ComposeEngine 跑完一整条
        /// 装配链之后才以 <c>HitEvent.Damage</c> 的形式出现，热更层没有"问某件器官打多少"的入口。
        /// 在这里复制一份数值管线，等于制造第二个会漂移的真相源。
        /// 形状（打什么、打哪、打多大）由器官决定，数值统一——这是本段刻意接受的最小实现边界。
        /// </summary>
        public const float DefaultDamage = 6f;

        public const float DefaultProjectileSpeed = 14f;
        public const float DefaultProjectileLifetime = 2.5f;
        public const float DefaultConeHalfAngle = 45f;
        public const float DefaultZoneSeconds = 4f;
        public const float DefaultZoneInterval = 0.5f;
        public const float DefaultStatusSeconds = 3f;

        /// <summary>
        /// 解析某件器官的内核动作。查无此器官 / 该器官没有可释放形态时返回
        /// <see cref="OrganKernelAction.None"/>——调用方据此把动作判为不可用，而不是打一发通用弹。
        /// </summary>
        public static OrganKernelAction Resolve(string organId)
        {
            if (string.IsNullOrEmpty(organId))
            {
                return OrganKernelAction.None;
            }

            OrganelleDef def = OrganelleCatalog.Get(organId);
            if (def == null)
            {
                return OrganKernelAction.None;
            }

            return def.Category == OrganelleCategory.Structural
                ? ResolveStructural(organId, def)
                : ResolveAttack(organId, def);
        }

        /// <summary>
        /// 结构器官（壳/甲/常驻分泌）也可能被装到动作槽上——M2-03a 的原型表里
        /// <c>org_confusion_spore</c> 就是这样一件。它没有 <c>CreateModule</c>，
        /// 全部行为写在 <see cref="OrganelleDef.TriggerHook"/> 里，所以按钩子读：
        /// 有反伤比例 → 有杀伤的区域；没有 → 纯挂标记的无伤害范围（迷乱孢子正是这种）。
        /// </summary>
        private static OrganKernelAction ResolveStructural(string organId, OrganelleDef def)
        {
            TriggerHookSpec? hookOrNull = def.TriggerHook;
            if (hookOrNull == null)
            {
                // 只有常驻属性加成、没有任何触发行为的结构器官（屏障结节之类）：
                // 它确实没有"能按出来的东西"，判为无动作比编一个出来诚实。
                return OrganKernelAction.None;
            }

            TriggerHookSpec hook = hookOrNull.Value;
            float radius = hook.LingerRadius > 0f ? hook.LingerRadius : DefaultRadius;
            float seconds = hook.LingerSeconds > 0f ? hook.LingerSeconds : DefaultStatusSeconds;
            SimStatus status = string.IsNullOrEmpty(hook.Tag)
                ? SimStatus.Marked
                : StructuralHookRunner.ParseTag(hook.Tag);

            if (hook.ThornsRatio > 0f)
            {
                return new OrganKernelAction
                {
                    OrganId = organId,
                    Kind = OrganKernelActionKind.Zone,
                    Radius = radius,
                    Damage = DefaultDamage * hook.ThornsRatio,
                    Seconds = seconds,
                    Interval = hook.TickRate > 0f ? hook.TickRate : DefaultZoneInterval,
                    ApplyStatus = status,
                    FollowSelf = true,
                };
            }

            return new OrganKernelAction
            {
                OrganId = organId,
                Kind = OrganKernelActionKind.Status,
                Radius = radius,
                Damage = 0f,
                Seconds = seconds,
                ApplyStatus = status,
            };
        }

        private static OrganKernelAction ResolveAttack(string organId, OrganelleDef def)
        {
            // 已退役的器官不该还能按出来：它的效果已经迁到别的 id 上了，
            // 让它继续开火等于让一条本该消失的内容路径在直控下复活。
            if (def.IsRetired || !def.AttackMethod)
            {
                return OrganKernelAction.None;
            }

            OrganModuleParamsSpec p = DataRegistry.Instance.GetOrganModuleParams(organId);
            var action = new OrganKernelAction
            {
                OrganId = organId,
                Damage = DefaultDamage,
                Radius = DefaultRadius,
            };

            switch (def.AttackFamily)
            {
                case "Cone":
                case "Melee":
                    action.Kind = OrganKernelActionKind.Cone;
                    action.HalfAngleDeg = p.SpreadAngle > 0f ? p.SpreadAngle : DefaultConeHalfAngle;
                    break;

                case "Pool":
                case "Rain":
                    action.Kind = OrganKernelActionKind.Zone;
                    action.Seconds = p.LingerSeconds > 0f ? p.LingerSeconds : DefaultZoneSeconds;
                    action.Interval = p.TickRate > 0f ? p.TickRate : DefaultZoneInterval;
                    action.FollowSelf = false;
                    break;

                case "Aura":
                    action.Kind = OrganKernelActionKind.Zone;
                    action.Radius = p.AuraRadius > 0f ? p.AuraRadius : DefaultRadius;
                    action.Seconds = p.LingerSeconds > 0f ? p.LingerSeconds : DefaultZoneSeconds;
                    action.Interval = p.TickRate > 0f ? p.TickRate : DefaultZoneInterval;
                    action.FollowSelf = true;
                    break;

                case "SummonAnchor":
                    // "钉一根菌丝炮台，定点打附近"——固定在瞄准点的杀伤区（降级理由见类注释）。
                    action.Kind = OrganKernelActionKind.Zone;
                    action.Seconds = DefaultZoneSeconds;
                    action.Interval = DefaultZoneInterval;
                    action.FollowSelf = false;
                    break;

                case "SummonFollow":
                    // 跟随的芽体：跟随自身的杀伤区。
                    action.Kind = OrganKernelActionKind.Zone;
                    action.Seconds = DefaultZoneSeconds;
                    action.Interval = DefaultZoneInterval;
                    action.FollowSelf = true;
                    break;

                default:
                    // Projectile / Beam / Orbit / Wave / Chain / Boomerang 以及未标注 Family 的攻击器官：
                    // 统一落成内核真弹体。弹道的唯一真相在内核，热更层不得再造飞行模拟。
                    action.Kind = OrganKernelActionKind.Projectile;
                    action.Speed = p.BallisticsSpeed > 0f
                        ? DefaultProjectileSpeed * p.BallisticsSpeed
                        : DefaultProjectileSpeed;
                    action.Lifetime = p.BallisticsLifetime > 0f ? p.BallisticsLifetime : DefaultProjectileLifetime;
                    action.Radius = 0.3f;
                    action.Pierce = p.PierceCount > 0 ? p.PierceCount : 1;
                    break;
            }

            return action;
        }
    }
}
