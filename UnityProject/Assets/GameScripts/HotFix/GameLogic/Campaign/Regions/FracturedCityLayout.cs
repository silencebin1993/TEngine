using System.Collections.Generic;
using GameLogic.Campaign.Content;
using UnityEngine;

namespace GameLogic.Campaign.Regions
{
    /// <summary>ER5-REGION-01：破碎都市（第 1 次出击）固定地图空间锚点表，与 <see cref="HomeValleyLayout"/>
    /// 同一纪律——纯 Transform 坐标常量，不写死进任何地形网格/美术资产（2026-09-21 用户裁决：三张地图
    /// 地形走 PCG 后期生成，正式低模走混元+功能美术绑定管线，均不在本 Story 范围；本表只保证空间关系
    /// 可达、不重叠、相机边界在换真实地形/美术后不需要重写）。
    ///
    /// 内容来源：DEMO-CONTENT-LOCK.md §4.1"破碎都市（第1次）"——安全入口/撤离区、侦察走廊两台静默
    /// 侦察机、监听节点+一台静默干扰机、标记器模块掉落、协议终端、周围三箱各40废料、关键物恢复柜。
    /// RegionId 直接复用 <see cref="HomeValleySignal.SilentRuinsRegionId"/>（ER5-SIG-01 已定义并用它
    /// 播种 Locked 态 <see cref="RegionRecord"/>），不新造第二个字符串常量制造漂移风险。</summary>
    public static class FracturedCityLayout
    {
        public const string RegionId = HomeValleySignal.SilentRuinsRegionId;

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

        // ── 固定物件 ID（稳定 region/object ID，STORY-EXECUTION-CARDS.md 第1条明确要求）────────
        public const string EntryEvacId = "fc_entry_evac";
        public const string Scout1SpawnId = "fc_scout_1";
        public const string Scout2SpawnId = "fc_scout_2";
        public const string JammerSpawnId = "fc_jammer";
        public const string ListeningNodeId = "fc_listening_node";
        public const string TerminalId = "fc_terminal";
        public const string Crate1Id = "fc_crate_1";
        public const string Crate2Id = "fc_crate_2";
        public const string Crate3Id = "fc_crate_3";
        public const string RecoveryLockerId = "fc_recovery_locker";

        // ── 关键物内容 ID（节点摧毁/终端读取的产出，供 <see cref="RegionQuestItemRecord.ContentId"/>）──
        public const string MarkerModuleContentId = "quest_marker_module";
        public const string ProtocolDataboxContentId = "quest_protocol_databox";

        // ── 空间锚点（净空半径用于 Validate 不重叠/可达性检查，同 HomeValleyLayout 先例）────────
        public static readonly Anchor EntryEvac = new Anchor(EntryEvacId, new Vector2(0f, -24f), 3f);
        public static readonly Anchor Scout1Spawn = new Anchor(Scout1SpawnId, new Vector2(-10f, -8f), 1.5f);
        public static readonly Anchor Scout2Spawn = new Anchor(Scout2SpawnId, new Vector2(10f, -8f), 1.5f);
        public static readonly Anchor ListeningNode = new Anchor(ListeningNodeId, new Vector2(0f, 4f), 2f);
        public static readonly Anchor JammerSpawn = new Anchor(JammerSpawnId, new Vector2(4f, 4f), 1.5f);
        public static readonly Anchor Terminal = new Anchor(TerminalId, new Vector2(0f, 10f), 1.5f);
        public static readonly Anchor Crate1 = new Anchor(Crate1Id, new Vector2(-8f, 10f), 1f);
        public static readonly Anchor Crate2 = new Anchor(Crate2Id, new Vector2(8f, 10f), 1f);
        public static readonly Anchor Crate3 = new Anchor(Crate3Id, new Vector2(0f, 16f), 1f);
        public static readonly Anchor RecoveryLocker = new Anchor(RecoveryLockerId, new Vector2(0f, -16f), 1.5f);

        /// <summary>相机初始聚焦点——入口安全区与侦察走廊之间。</summary>
        public static readonly Vector2 CameraFocusStart = new Vector2(0f, -14f);
        public const float CameraBoundsHalfExtentX = 24f;
        public const float CameraBoundsHalfExtentZ = 30f;

        // ── 数值调校（唯一真相：ER4-CONTENT-01 EnemyCatalog + DEMO-CONTENT-LOCK.md §4.1，不重复发明）──
        public const float ScoutMaxHealth = 70f; // EnemyCatalog.ScoutId ValuesSummary。
        public const float ScoutMarkIntervalSeconds = 8f;
        public const float JammerMaxHealth = 90f; // EnemyCatalog.JammerId ValuesSummary。
        public const float JammerRadius = 12f; // 同上。
        public const float ListeningNodeMaxHealth = 50f; // 内容锁定表未点名具体 HP，取与静默侦察机同量级。
        public const float ControlJamGraceSeconds = 2f; // "原已受控机器在2秒宽限后回弹"。
        public const int CrateScrapAmount = 40; // DEMO-CONTENT-LOCK.md §4.1第3条"周围三箱各40废料"。
        public const float PoiDiscoveryRadius = 8f; // 玩家机器进入该半径即"发现"该物件，写 DiscoveredNodes。

        // ── ER5-SILENT-01：敌人 AI 调校（数值来源见 FracturedCityEnemyAi 类注释——DEMO-CONTENT-LOCK.md
        // §5 只给了 HP/标记间隔/干扰半径，未点名武器伤害/射程/移动速度，以下是本 Story 按既有连射器
        // 数值量级（8伤害/0.45秒冷却）向下取的保守默认值，非文档摘录）───────────────────────
        public const float ScoutMarkRange = 10f; // 侦察机标记距离，超出或被遮挡（"不魔法穿墙"）不生效。
        public const float ScoutFleeTriggerRange = 6f; // 玩家进入此距离，侦察机后撤（"后撤"字面行为）。
        public const float ScoutFleeLeash = 5f; // 后撤不超过出生点这个半径，避免越界闯入干扰机/其它锚点。
        public const float ScoutPatrolRadius = 2.5f; // 无威胁时的巡逻摆动半径。
        public const float ScoutMoveSpeed = 2.2f;
        public const int ScoutScrapLoot = 15; // 击破掉落（正式货物链 HomeValleyCargo.SpawnGroundItem）。

        public const float JammerAttackRange = 8f; // 驻守节点的防御性攻击——干扰机唯一会主动开火的敌人。
        public const float JammerAttackDamage = 6f;
        public const float JammerAttackCooldownSeconds = 2.5f;
        public const int JammerScrapLoot = 20;

        public const float MarkDurationSeconds = 10f; // 标记状态持续时间，干扰机可提前清除或自然过期。

        // 直控攻击（玩家WASD+左键瞄准），同 HomeValleyCombatTargets.TryFindTargetInAim 同一设计语言：
        public const float DirectAttackRange = 12f;
        public const float DirectAttackAimHalfAngleDeg = 60f;

        // ── ER6-REACT-01：标记跳转反应（数值来自 DEMO-CONTENT-LOCK.md §2.4/§5，唯一点名数字）───
        public const float MarkJumpRange = 8f; // "最多额外跳2个8米内目标"。
        public const int MarkJumpMaxTargets = 2;
        public const float MarkJumpDamageFalloff = 0.6f; // "每跳伤害为上一跳的60%"。
        /// <summary>敌方被标记的持续时间——DEMO-CONTENT-LOCK.md 未点名具体秒数（只给跳转范围/衰减/
        /// 目标数），沿用 ER5-SILENT-01 玩家侧标记同一量级（10秒），同类取舍见该 Story 类注释。</summary>
        public const float EnemyMarkDurationSeconds = 10f;

        // ── ER6-REACT-02：铸造重炮/熔穿过载（数值来自 DEMO-CONTENT-LOCK.md §5，唯一点名数字）──
        public const float CannonRange = 20f;
        public const float CannonBaseDamage = 55f;
        public const float CannonCooldownSeconds = 3f;
        public const float CannonAimSeconds = 1f;
        public const float CannonBaseHeatPerShot = 40f;
        public const float OverloadExtraHeatPerShot = 25f; // 40+25=65（反应激活时每发实际积热）。
        public const float OverloadArmorPierceBonus = 0.3f; // "额外穿甲+30%"，HeatResistant 只削这一项。
        public const float WeaponHeatOverheatThreshold = 100f;
        public const float WeaponHeatRecoverThreshold = 60f; // 迟滞下限，"低于60方可恢复"。
        public const float WeaponHeatDissipationPerSecond = 10f;
        public const float HeatSinkBonusDissipationPerSecond = 5f; // 散热鳍额外散热。

        public static IEnumerable<Anchor> AllAnchors()
        {
            yield return EntryEvac;
            yield return Scout1Spawn;
            yield return Scout2Spawn;
            yield return ListeningNode;
            yield return JammerSpawn;
            yield return Terminal;
            yield return Crate1;
            yield return Crate2;
            yield return Crate3;
            yield return RecoveryLocker;
        }

        /// <summary>全部固定物件 ID（供发现/清点/自检遍历，不含机器出生点——机器是从家园带来的，
        /// 不属于本区域固定内容）。</summary>
        public static IEnumerable<string> AllPoiIds()
        {
            foreach (Anchor a in AllAnchors())
            {
                yield return a.Id;
            }
        }

        /// <summary>同 <see cref="HomeValleyLayout.Validate"/>：出生点/物件净空不重叠、相机边界覆盖
        /// 全部锚点。"出生点不与敌射线重叠"（STORY-EXECUTION-CARDS.md 第3条）在本方法里体现为
        /// EntryEvac（玩家出生点）与 Scout/Jammer 锚点的净空距离检查。</summary>
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

            float entryToScout1 = Vector2.Distance(EntryEvac.Position, Scout1Spawn.Position);
            float entryToScout2 = Vector2.Distance(EntryEvac.Position, Scout2Spawn.Position);
            float entryToJammer = Vector2.Distance(EntryEvac.Position, JammerSpawn.Position);
            const float minSafeSpawnDistance = 10f;
            if (entryToScout1 < minSafeSpawnDistance || entryToScout2 < minSafeSpawnDistance ||
                entryToJammer < minSafeSpawnDistance)
            {
                violations.Add("出生点 EntryEvac 距敌方锚点过近，可能与敌射线重叠。");
            }

            if (CameraBoundsHalfExtentX <= 0f || CameraBoundsHalfExtentZ <= 0f)
            {
                violations.Add("相机边界半宽/半高必须为正数（相机无锚点）。");
            }

            return violations;
        }
    }
}
