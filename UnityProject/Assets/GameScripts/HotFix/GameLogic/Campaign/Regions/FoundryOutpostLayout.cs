using System.Collections.Generic;
using GameLogic.Campaign.Content;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER6-FOUNDRY-01：铸造前哨（第 2/3 次出击）固定地图空间锚点表——本 Story 只交付外围分区
    /// （DEMO-CONTENT-LOCK.md §4.2），与 <see cref="FracturedCityLayout"/> 同一纪律（纯 Transform 坐标
    /// 常量，不写死地形/美术资产，2026-09-21 用户裁决同上）。核心分区（封锁门/两供能节点/主核心）属于
    /// ER6-REGION-01/ER7-CORE-01，本表只预留 <see cref="CoreGatePosition"/> 一个锚点占位供其扩展，不
    /// 抢先实现门禁逻辑本身。
    ///
    /// ── 范围裁剪：为什么本 Story（敌人）先于 ER6-REGION-01（空间/门禁）交付整个外围地图 ──
    /// DEMO-CONTENT-LOCK.md §4.2 把"两护甲机守左右掩体/步进炮守重炮回收点/取重炮模块和三箱废料/
    /// 可选三种技术缓存/外围撤离点"整段描述成同一个不可拆分的外围场景；ER6-REGION-01 的卡片原文
    /// 只点名"封锁门/两供能节点/主核心/三灯门禁显示"这一层——是外围之上叠加的核心区连接层，不是
    /// 外围本身。STORY-BOARD.md 把 FOUNDRY-01 排在 REGION-01 之前，因此本 Story 承担"外围地图从零
    /// 建立"的职责（镜像 ER5-REGION-01 对破碎都市的角色），REGION-01 在此基础上叠加门禁——与
    /// ER5-REGION-01→ER5-SILENT-01（先空间后敌人）顺序相反，但产出的分工边界同样清晰。</summary>
    public static class FoundryOutpostLayout
    {
        public const string RegionId = "foundry_outpost";

        public readonly struct Anchor
        {
            public readonly string Id;
            public readonly Vector2 Position;
            public readonly float ClearanceRadius;

            public Anchor(string id, Vector2 position, float clearanceRadius)
            {
                Id = id;
                Position = position;
                ClearanceRadius = clearanceRadius;
            }
        }

        // ── 固定物件 ID ──────────────────────────────────────────────────────
        public const string EntryEvacId = "fo_entry_evac";
        public const string ArmorBotLeftSpawnId = "fo_armorbot_left";
        public const string ArmorBotRightSpawnId = "fo_armorbot_right";
        public const string StriderSpawnId = "fo_strider";
        public const string RepairBotSpawnId = "fo_repairbot";
        public const string Crate1Id = "fo_crate_1";
        public const string Crate2Id = "fo_crate_2";
        public const string Crate3Id = "fo_crate_3";
        public const string ArmorCacheId = "fo_cache_armor";
        public const string HeatSinkCacheId = "fo_cache_heatsink";
        public const string ArmorPierceCacheId = "fo_cache_armorpierce";
        public const string RecoveryLockerId = "fo_recovery_locker";
        /// <summary>核心区门——ER6-REGION-01：导航阻挡/物理碰撞/交互拒绝/战役目标校验四重控制的
        /// 唯一物件锚点，见 <see cref="FoundryOutpostRegion.CanEnterCoreZone"/>。</summary>
        public const string CoreGateId = "fo_core_gate";
        /// <summary>核心分区入口占位——门后空间的稳定物件 ID（DEMO-CONTENT-LOCK.md §2.5"同一
        /// foundry_outpost 保留外围/核心两个分区"）。本 Story 只保证该点存在、Validate() 覆盖它，
        /// 两供能节点/主核心的真实内容留 ER7-CORE-01 在此基础上搭建，不抢先实现。</summary>
        public const string CoreZoneEntryId = "fo_core_zone_entry";
        /// <summary>ER6-ADAPT-01："Flanker 多一组侧袭出生组"——只在
        /// <see cref="RegionRecord.AdaptationId"/> 锁定为 <see cref="AdaptationCatalog.Flanker"/>
        /// 时由 <see cref="FoundryOutpostRegion"/> 条件播种，见该类 ReconcileAdaptiveSupportEnemy。</summary>
        public const string FlankerSpawnId = "fo_flanker";
        /// <summary>ER6-ADAPT-01："JammerSupport 多一组干扰支援组"——同上，锁定为
        /// <see cref="AdaptationCatalog.JammerSupport"/> 时条件播种，敌类型复用既有
        /// <see cref="EnemyCatalog.JammerId"/>（"敌方现有技术树内编制变化"，不新造敌人）。</summary>
        public const string JammerSupportSpawnId = "fo_jammer_support";

        // ── 关键物内容 ID ────────────────────────────────────────────────────
        /// <summary>步进炮首次击破掉落，唯一关键物（DEMO-CONTENT-LOCK.md §4.1第4条"关键模块"的
        /// 恢复柜保底机制只覆盖这一个 contentId，三种可选缓存不在保底范围——见
        /// <see cref="FoundryOutpostRegion.RecoveryLockerCheck"/>）。</summary>
        public const string CannonModuleContentId = "quest_cannon_module";
        /// <summary>三种可选技术缓存——各来自一个明确标识的可选拾取点，OnGround→Carried→Recovered/Lost
        /// 同关键物同一生命周期，但不受恢复柜保底（"此机制不重生已领取的废料或可选奖励"）。</summary>
        public const string ArmorCacheContentId = "quest_armor_cache";
        public const string HeatSinkCacheContentId = "quest_heatsink_cache";
        public const string ArmorPierceCacheContentId = "quest_armorpierce_cache";

        // ── 空间锚点 ─────────────────────────────────────────────────────────
        public static readonly Anchor EntryEvac = new Anchor(EntryEvacId, new Vector2(0f, -20f), 3f);
        public static readonly Anchor ArmorBotLeftSpawn = new Anchor(ArmorBotLeftSpawnId, new Vector2(-9f, -2f), 1.5f);
        public static readonly Anchor ArmorBotRightSpawn = new Anchor(ArmorBotRightSpawnId, new Vector2(9f, -2f), 1.5f);
        public static readonly Anchor StriderSpawn = new Anchor(StriderSpawnId, new Vector2(0f, 8f), 2f);
        public static readonly Anchor RepairBotSpawn = new Anchor(RepairBotSpawnId, new Vector2(4f, 2f), 1.5f);
        public static readonly Anchor Crate1 = new Anchor(Crate1Id, new Vector2(-8f, 12f), 1f);
        public static readonly Anchor Crate2 = new Anchor(Crate2Id, new Vector2(8f, 12f), 1f);
        public static readonly Anchor Crate3 = new Anchor(Crate3Id, new Vector2(0f, 18f), 1f);
        public static readonly Anchor ArmorCache = new Anchor(ArmorCacheId, new Vector2(-6f, 6f), 1f);
        public static readonly Anchor HeatSinkCache = new Anchor(HeatSinkCacheId, new Vector2(6f, 6f), 1f);
        public static readonly Anchor ArmorPierceCache = new Anchor(ArmorPierceCacheId, new Vector2(0f, 14f), 1f);
        public static readonly Anchor RecoveryLocker = new Anchor(RecoveryLockerId, new Vector2(0f, -12f), 1.5f);
        public static readonly Anchor CoreGate = new Anchor(CoreGateId, new Vector2(0f, 24f), 2.5f);
        /// <summary>核心分区入口占位，门后约4.5米——ER7-CORE-01 的搭建起点，本 Story 只占位（半径
        /// 收小到 1，确保与门净空不重叠、且不超出相机边界 <see cref="CameraBoundsHalfExtentZ"/>）。</summary>
        public static readonly Anchor CoreZoneEntry = new Anchor(CoreZoneEntryId, new Vector2(0f, 28.5f), 1f);
        /// <summary>侧袭出生点——刻意放在左侧掩体外侧、entry 与掩体连线之外，模拟"从侧翼绕出"而不是
        /// 与既有护甲机同一驻守正面。</summary>
        public static readonly Anchor FlankerSpawn = new Anchor(FlankerSpawnId, new Vector2(-14f, 4f), 1.5f);
        /// <summary>干扰支援出生点——镜像侧袭点在右侧，与既有敌人锚点保持独立净空。</summary>
        public static readonly Anchor JammerSupportSpawn = new Anchor(JammerSupportSpawnId, new Vector2(14f, 4f), 1.5f);
        /// <summary>侧袭单位固定朝向——面朝战场中央（约朝入口/回收点连线方向），供
        /// <see cref="FoundryOutpostRegion.IsFrontalHit"/> 复用既有护甲机正面判定框架。</summary>
        public static readonly Vector2 FlankerFacing = new Vector2(1f, -0.6f).normalized;

        /// <summary>ER6-REGION-01："物理碰撞"边界——门锁定时，任何机器（直控 WASD/编队 Move 命令
        /// 目的地）都不能越过这条 Y 线进入核心分区，与 X 坐标无关（不给"绕路"留一个有限宽度的缺口，
        /// 验收卡第3条"绕路……都不能跳门"字面要求）。取值＝门锚点 Y 减净空半径（门的物理前沿）。</summary>
        public static readonly float CoreGateBlockLineY = CoreGate.Position.y - CoreGate.ClearanceRadius;

        /// <summary>相机初始聚焦点——入口安全区与掩体火力线之间。</summary>
        public static readonly Vector2 CameraFocusStart = new Vector2(0f, -6f);
        public const float CameraBoundsHalfExtentX = 22f;
        /// <summary>ER7-CORE-01：核心分区（两供能节点+主核心）落在 CoreZoneEntry（y=28.5）更深处，
        /// 原 30 已不够覆盖，扩到 52 同时仍覆盖外围最远锚点 EntryEvac（y=-20）。</summary>
        public const float CameraBoundsHalfExtentZ = 52f;

        // ── ER7-CORE-01：核心分区（两供能节点+主核心+Boss 战内容）──────────────
        // 同一 foundry_outpost 会话内部的第三个空间子区（ER6-REGION-01 已确立"核心分区门禁在外围
        // 场景内部解决，不是出发去新地图"），锚点表沿用与外围完全相同的纯 Transform 纪律。
        public const string CoreNode1Id = "fo_core_node_1";
        public const string CoreNode2Id = "fo_core_node_2";
        public const string MainCoreId = "fo_main_core";
        public const string CoreRepairBotSummonId = "fo_core_repairbot";
        /// <summary>信号暴露90增援——ER6-ADAPT-01 出发页只预告，本 Story 是真正的入口实装点
        /// （"不把它伪装成adaptation"字面要求：走独立的 <see cref="Regions.FoundryOutpostRegion.
        /// ReconcileCoreReinforcement"/>，与 Flanker/JammerSupport 那条 adaptation 增援槽位完全独立）。</summary>
        public const string CoreReinforcementSpawnId = "fo_core_reinforcement";
        /// <summary>核心数据盒——Boss Destroyed 后唯一掉落的关键物内容 ID，与铸造重炮模块同一
        /// OnGround→Carried→Recovered/Lost 生命周期。</summary>
        public const string CoreDataContentId = "quest_core_data";

        public static readonly Anchor CoreNode1 = new Anchor(CoreNode1Id, new Vector2(-7f, 38f), 2f);
        public static readonly Anchor CoreNode2 = new Anchor(CoreNode2Id, new Vector2(7f, 38f), 2f);
        public static readonly Anchor MainCore = new Anchor(MainCoreId, new Vector2(0f, 45f), 3f);
        /// <summary>Transition 期间召唤的一次性维修机出生点——与主核心保持最小净空但不重叠。</summary>
        public static readonly Anchor CoreRepairBotSummon = new Anchor(CoreRepairBotSummonId, new Vector2(0f, 40f), 1.5f);
        /// <summary>守入口的90暴露增援护甲机——刻意放在核心分区入口内侧（CoreZoneEntry 之后），
        /// "预告核心战入口护甲机增援"字面要求，不是外围掩体那两台的第三台。</summary>
        public static readonly Anchor CoreReinforcementSpawn = new Anchor(CoreReinforcementSpawnId, new Vector2(-4f, 30f), 1.5f);

        /// <summary>Phase2 区域封锁线——DEMO-CONTENT-LOCK.md"Phase2……新增区域封锁"字面要求：一旦
        /// 触发（提前2秒预警），任何机器都不能再越过这条线退回外围（与 CoreGateBlockLineY 是两条完全
        /// 独立的边界——前者挡"未解锁时进入"，这条挡"Phase2 后逃离"，语义方向相反）。取值介于
        /// CoreZoneEntry（28.5）与两节点（38）之间，不干扰节点/主核心战斗空间。</summary>
        public const float CoreLockoutLineY = 32f;
        public const float CoreLockoutWarnSeconds = 2f; // "提前2秒可见/可听提示"。

        /// <summary>主核心固定朝向——面朝入口方向（散热口在背面）。Phase2"侧后+20%"判定复用与
        /// 护甲机 <see cref="FoundryOutpostRegion.IsFrontalHit"/> 同一锥角框架，但语义相反（背后是
        /// 弱点而非减伤区）。</summary>
        public static readonly Vector2 MainCoreFacing = new Vector2(0f, -1f);
        public const float MainCoreFrontalHalfAngleDeg = 75f; // 与护甲机同一量级，锥角外＝侧后。

        /// <summary>两供能节点共用的 EnemyTypeId——本身不是 <see cref="Content.EnemyCatalog"/> 目录条目
        /// （节点是设施不是敌人角色，无需图鉴/AI 权限等内容层字段），只是 <see cref="RegionEnemyRecord"/>
        /// 复用其 Health/MaxHealth/IsAlive/CycleCooldownRemaining 字段+既有伤害结算管线（含铸造重炮/
        /// 熔穿过载）时的判别标签，唯一消费方 <see cref="Regions.FoundryOutpostCoreBoss"/>。</summary>
        public const string BossNodeTypeId = "boss_node";
        public const string BossCoreTypeId = "boss_core";

        // ── Boss 数值（DEMO-CONTENT-LOCK.md §4.3 逐字）─────────────────────────
        public const float NodeMaxHealth = 100f;
        public const float MainCoreMaxHealth = 600f;
        public const float Phase1TransitionHealthFraction = 0.6f; // "降至60%时……Transition"。
        public const float TransitionDurationSeconds = 5f;
        public const float Phase2BackHitBonusPct = 0.2f; // "侧后+20%"。
        // 主核心自卫攻击（Phase1/Phase2 生效，Transition 不攻击）——DEMO-CONTENT-LOCK.md 未点名具体
        // 数字，按铸造步进炮同一量级取保守 judgment call（同 FoundryOutpostEnemyAi 类注释先例）。
        public const float CoreAttackRange = 16f;
        public const float CoreAttackDamage = 18f;
        public const float CoreAttackCooldownSeconds = 2f;

        // ── 敌人 HP（唯一真相 Content.EnemyCatalog，不重复发明）─────────────────
        public const float ArmorBotMaxHealth = 160f; // EnemyCatalog.ArmorBotId ValuesSummary。
        public const float StriderMaxHealth = 110f; // EnemyCatalog.StriderId ValuesSummary。
        public const float RepairBotMaxHealth = 80f; // EnemyCatalog.RepairBotId ValuesSummary。
        public const int CrateScrapAmount = 40; // DEMO-CONTENT-LOCK.md §4.2第3条"三箱各40废料"。
        public const float PoiDiscoveryRadius = 8f;

        // ── ER6-FOUNDRY-01：敌人 AI 调校（数值来源见 FoundryOutpostEnemyAi 类注释——
        // DEMO-CONTENT-LOCK.md §5 只给了 HP/正面减伤40%/1秒瞄准线三个数字，其余是本 Story 按
        // FracturedCityLayout 既有敌人调校同一保守量级取的judgment call，非文档摘录）───────
        public const float ArmorBotFrontalReductionPct = 0.4f; // 复用 EnemyCatalog.ComputeFrontalArmorReducedDamage。
        public const float ArmorBotFrontalHalfAngleDeg = 75f; // "正面"锥角——供攻击方向判定是否命中正面。
        public static readonly Vector2 ArmorBotLeftFacing = new Vector2(0.4f, -1f).normalized; // 朝向入口/掩体前方。
        public static readonly Vector2 ArmorBotRightFacing = new Vector2(-0.4f, -1f).normalized;
        public const float ArmorBotAttackRange = 9f;
        public const float ArmorBotAttackDamage = 10f;
        public const float ArmorBotAttackCooldownSeconds = 1.6f;
        public const int ArmorBotScrapLoot = 25;

        public const float StriderAimSeconds = 1f; // EnemyCatalog.StriderId ValuesSummary 字面数字。
        public const float StriderAttackRange = 14f;
        public const float StriderAttackDamage = 20f; // "重炮回收点"驻守单位，伤害量级高于护甲机自卫攻击。
        public const float StriderAttackCooldownSeconds = 2.5f; // 开火后到下一次可以重新瞄准的冷却。
        public const int StriderScrapLoot = 30;

        public const float RepairBotMoveSpeed = 2.4f;
        public const float RepairBotHealRange = 6f;
        public const float RepairBotHealAmount = 15f;
        public const float RepairBotHealCooldownSeconds = 3f;
        public const float RepairBotFleeTriggerRange = 5f; // 玩家进入此距离，维修机后撤（支援单位不硬扛）。
        public const float RepairBotFleeLeash = 6f;
        public const int RepairBotScrapLoot = 15;

        // 直控攻击（同 FracturedCityLayout.DirectAttackRange/DirectAttackAimHalfAngleDeg 同一设计语言）：
        public const float DirectAttackRange = 14f;
        public const float DirectAttackAimHalfAngleDeg = 60f;

        public static IEnumerable<Anchor> AllAnchors()
        {
            yield return EntryEvac;
            yield return ArmorBotLeftSpawn;
            yield return ArmorBotRightSpawn;
            yield return StriderSpawn;
            yield return RepairBotSpawn;
            yield return Crate1;
            yield return Crate2;
            yield return Crate3;
            yield return ArmorCache;
            yield return HeatSinkCache;
            yield return ArmorPierceCache;
            yield return RecoveryLocker;
            yield return CoreGate;
            yield return CoreZoneEntry;
            yield return FlankerSpawn;
            yield return JammerSupportSpawn;
            yield return CoreNode1;
            yield return CoreNode2;
            yield return MainCore;
            yield return CoreRepairBotSummon;
            yield return CoreReinforcementSpawn;
        }

        public static IEnumerable<string> AllPoiIds()
        {
            foreach (Anchor a in AllAnchors())
            {
                yield return a.Id;
            }
        }

        /// <summary>同 <see cref="FracturedCityLayout.Validate"/>：出生点/物件净空不重叠、相机边界覆盖
        /// 全部锚点、出生点距敌方锚点足够远。</summary>
        public static List<string> Validate()
        {
            var violations = new List<string>();
            var anchors = new List<Anchor>(AllAnchors());

            for (int i = 0; i < anchors.Count; i++)
            {
                for (int j = i + 1; j < anchors.Count; j++)
                {
                    float minGap = anchors[i].ClearanceRadius + anchors[j].ClearanceRadius;
                    float dist = Vector2.Distance(anchors[i].Position, anchors[j].Position);
                    if (dist < minGap)
                    {
                        violations.Add(
                            $"锚点 {anchors[i].Id} 与 {anchors[j].Id} 净空重叠：距离 {dist:F2} < 所需 {minGap:F2}");
                    }
                }

                Anchor a = anchors[i];
                bool insideBounds =
                    Mathf.Abs(a.Position.x) + a.ClearanceRadius <= CameraBoundsHalfExtentX &&
                    Mathf.Abs(a.Position.y) + a.ClearanceRadius <= CameraBoundsHalfExtentZ;
                if (!insideBounds)
                {
                    violations.Add($"锚点 {a.Id} 超出相机边界（半宽 {CameraBoundsHalfExtentX}/{CameraBoundsHalfExtentZ}）");
                }
            }

            float entryToLeft = Vector2.Distance(EntryEvac.Position, ArmorBotLeftSpawn.Position);
            float entryToRight = Vector2.Distance(EntryEvac.Position, ArmorBotRightSpawn.Position);
            float entryToStrider = Vector2.Distance(EntryEvac.Position, StriderSpawn.Position);
            const float minSafeSpawnDistance = 10f;
            if (entryToLeft < minSafeSpawnDistance || entryToRight < minSafeSpawnDistance || entryToStrider < minSafeSpawnDistance)
            {
                violations.Add("出生点 EntryEvac 距敌方锚点过近，可能与敌射线重叠（\"不能在出生点无预告秒杀玩家\"）。");
            }

            if (CameraBoundsHalfExtentX <= 0f || CameraBoundsHalfExtentZ <= 0f)
            {
                violations.Add("相机边界半宽/半高必须为正数（相机无锚点）。");
            }

            return violations;
        }
    }
}
