using System;
using System.Collections.Generic;
using System.Globalization;
using GameConfig.fg;
using GameLogic.Localization;
using GameLogic.Notifications;
using TEngine;
using UnityEngine;

namespace GameLogic.Campaign.Content
{
    /// <summary>
    /// FG9-DATA-01（FG09 FGR-FAC-001、FG14 FGR-ARC-005、FGT-FAC-001）：敌人定义的唯一运行时入口。
    /// 所有敌人（Demo 的 6 类 + 主核心供能节点）都在 Luban 表 fg.TbMechEnemy（数据源 tools/cell_tables/fgdata_enemy.py），
    /// Demo 两个区域的编成在 fg.TbEnemyEncounter；原 <c>Content/EnemyCatalog.cs</c> 的硬编码字典已删除。
    ///
    /// 行为按表里的“行为读法 + 标签 + 职能 / 层级”解释（<see cref="Combat.EnemyBehaviorReadings"/>、<see cref="Combat.EnemySpawnBuilder"/>），
    /// 生产代码不按敌人 ID 写分支（IC-REQ-012）；新敌人只加表行即可生成、开火、掉落、出现在情报与靶场里。
    ///
    /// 失败策略（卡片负向“表里缺字段时的可见报错”）：表行缺字段（读法需要的参数为 0、读法 / 标签不认识）或编成引用了不存在的敌人时，
    /// 该敌人不生成（不拿默认值替身），记 Error 并发一条“内容表错误”通知（同一条问题每次会话只通知一次）。
    /// </summary>
    public static class EnemyDefs
    {
        // ── 行为读法（fg.TbMechEnemy.behavior）──
        public const string BehaviorNone = "none";
        public const string BehaviorPatrolMark = "patrol_mark";
        public const string BehaviorJamField = "jam_field";
        public const string BehaviorHoldFire = "hold_fire";
        public const string BehaviorTelegraph = "telegraph";
        public const string BehaviorRepair = "repair";
        /// <summary>FG9-FAC-01（FGR-FAC-008）：偏折场（磁暴棱柱、寂听主脑阶段二）/ 伏击（回声猎手）/ 断链束（寂听者）。</summary>
        public const string BehaviorDeflect = "deflect";
        public const string BehaviorStalk = "stalk";
        public const string BehaviorSever = "sever";
        /// <summary>FG9-FAC-02（FGR-FAC-008 铸造专属语法）：齐射弹幕（对空高炮 / 空爆炮台）、布雷（布雷车）、装填（装填机）、正面推进冲锋（攻城重甲）。</summary>
        public const string BehaviorSalvo = "salvo";
        public const string BehaviorMinelay = "minelay";
        public const string BehaviorReload = "reload";
        public const string BehaviorCharge = "charge";

        // ── 标签（fg.TbMechEnemy.tags）──
        public const string TagStructure = "structure";
        public const string TagExternalHealth = "external_health";
        public const string TagSilentFire = "silent_fire";
        public const string TagAdaptHeat = "adapt_heat";
        /// <summary>FG9-FAC-01：诱饵中继（信号接触列表里的假信号点）；监听节点（范围内核心固件发动时暴露 × effectAmount）。</summary>
        public const string TagDecoy = "decoy";
        public const string TagListenAmp = "listen_amp";
        /// <summary>FG9-FAC-02：齐射只打空中目标；齐射在目标处空爆；武器按“组件与固件”列装配（DEBT-FG9DATA01-02）。</summary>
        public const string TagAntiAir = "anti_air";
        public const string TagAirburst = "airburst";
        public const string TagAssembled = "assembled";
        public static readonly string[] KnownTags = { TagStructure, TagExternalHealth, TagSilentFire, TagAdaptHeat, TagDecoy, TagListenAmp, TagAntiAir, TagAirburst, TagAssembled };

        // ── 职能 / 层级 / 阵营 ──
        public const string RoleStructure = "structure";
        public const string RoleBoss = "boss";
        public const string TierNormal = "normal";
        public const string TierElite = "elite";
        public const string TierBoss = "boss";
        public const string FactionSilent = "silent";
        public const string FactionFoundry = "foundry";

        // ── 编成条件（fg.TbEnemyEncounter.condition）──
        public const string ConditionBase = "base";
        public const string ConditionFlanker = "adaptation_flanker";
        public const string ConditionJammerSupport = "adaptation_jammer_support";
        public const string ConditionCoreReinforcement = "core_reinforcement";
        public const string ConditionBoss = "boss";
        public const string ConditionBossSummon = "boss_summon";
        /// <summary>FG9-FAC-01：首领阶段性召唤（可以多行）；静默反制锁定为某对策时追加的编成（FGR-FAC-004）。</summary>
        public const string ConditionBossWave = "boss_wave";
        public const string ConditionCounterDeflect = "counter_deflect";
        public const string ConditionCounterListen = "counter_listen";
        public const string ConditionCounterJam = "counter_jam";
        /// <summary>FG9-FAC-02：铸造前哨核心区的对空阵地（每次进场都补齐，不进出发预测）；铸造反制锁定为“对空加强 / 雷区”时追加的编成。</summary>
        public const string ConditionBattery = "battery";
        public const string ConditionCounterAntiAir = "counter_antiair";
        public const string ConditionCounterMinefield = "counter_minefield";

        public const string None = "none";
        public const string NotifyContentError = "content_error";

        /// <summary>旧档里首领记录的类型 ID（FG9-DATA-01 前不是敌人表条目）→ 敌人表 ID（与 fgdata_enemy.LEGACY_TYPE_IDS 一致，自检对照）。</summary>
        public static readonly IReadOnlyDictionary<string, string> LegacyTypeIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["boss_node"] = "enemy_core_node",
            ["boss_core"] = "enemy_core_boss",
        };

        private static int _cacheRevision = int.MinValue;
        private static readonly Dictionary<string, string[]> TagCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<string>> ProblemCache = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>去重集合属于哪一份战役存档：换存档（新建 / 读档 / 切换会话）后同一行坏表要重新给玩家发一次可见通知。</summary>
        private static CampaignState _reportedFor;

        /// <summary>报过的可见错误条数（自检用：证明负向路径真的发了通知）。</summary>
        public static int ReportedCount { get; private set; }

        // ── 查询 ──

        public static IReadOnlyList<MechEnemy> Rows => FgContentTables.Enemies;

        public static bool TryGet(string enemyTypeId, out MechEnemy row) => FgContentTables.TryGetEnemy(enemyTypeId, out row);

        public static MechEnemy Get(string enemyTypeId) => FgContentTables.Enemy(enemyTypeId);

        public static string NameOf(string enemyTypeId) =>
            TryGet(enemyTypeId, out MechEnemy row) ? GameText.Get(row.NameKey) : enemyTypeId;

        public static bool IsBoss(MechEnemy row) => row != null && row.Tier == TierBoss;

        public static bool IsElite(MechEnemy row) => row != null && row.Tier == TierElite;

        /// <summary>首领的设施（供能节点之类）：不算“一种敌人”（不进靶场解锁、不进出征情报的敌人计数）。</summary>
        public static bool IsStructure(MechEnemy row) => row != null && row.Role == RoleStructure;

        public static bool HasTag(MechEnemy row, string tag)
        {
            if (row == null || string.IsNullOrEmpty(tag))
            {
                return false;
            }
            string[] tags = TagsOf(row);
            for (int i = 0; i < tags.Length; i++)
            {
                if (tags[i] == tag)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HasTag(string enemyTypeId, string tag) => TryGet(enemyTypeId, out MechEnemy row) && HasTag(row, tag);

        private static string[] TagsOf(MechEnemy row)
        {
            EnsureCache();
            if (!TagCache.TryGetValue(row.Id ?? string.Empty, out string[] tags))
            {
                tags = string.IsNullOrEmpty(row.Tags) || row.Tags == None
                    ? Array.Empty<string>()
                    : row.Tags.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tags.Length; i++)
                {
                    tags[i] = tags[i].Trim();
                }
                TagCache[row.Id ?? string.Empty] = tags;
            }
            return tags;
        }

        /// <summary>掉落倾向：野外击毁掷普通掉落时用的来源（fg.TbLootDrop.source）。表里查不到 → <paramref name="fallback"/>；写 none → null（不掷）。</summary>
        public static string LootSourceOf(string enemyTypeId, string fallback)
        {
            if (!TryGet(enemyTypeId, out MechEnemy row) || string.IsNullOrEmpty(row.LootSource))
            {
                return fallback;
            }
            return row.LootSource == None ? null : row.LootSource;
        }

        /// <summary>占位颜色（表 viewColor，#RRGGBB）。解析失败返回洋红（一眼看出表坏了），并可见报错。</summary>
        public static Color ViewColor(MechEnemy row)
        {
            if (row != null && ColorUtility.TryParseHtmlString(row.ViewColor, out Color c))
            {
                return c;
            }
            if (row != null)
            {
                ReportBadRow(row.Id, "viewColor=" + row.ViewColor);
            }
            return Color.magenta;
        }

        /// <summary>出征情报的特征文本（表 traitKey，{0}=周期秒 {1}=效果半径 {2}=正面减伤% {3}=瞄准秒 {4}=治疗量）；none → null。</summary>
        public static string TraitText(MechEnemy row)
        {
            if (row == null || string.IsNullOrEmpty(row.TraitKey) || row.TraitKey == None)
            {
                return null;
            }
            return GameText.Format(row.TraitKey,
                Num(row.CycleSeconds), Num(row.EffectRange), Num(row.FrontalDamageReduction * 100f), Num(row.WeaponAim), Num(row.EffectAmount));
        }

        /// <summary>FG9-UX-01：图鉴 / 内容目录用的描述（表 descKey；{0}=效果持续秒 effectSeconds（如断链束照射秒数） {1}=效果半径 {2}=周期秒 {3}=正面减伤% {4}=效果量）。</summary>
        public static string DescText(MechEnemy row) =>
            row == null ? string.Empty : GameText.Format(row.DescKey,
                Num(row.EffectSeconds), Num(row.EffectRange), Num(row.CycleSeconds), Num(row.FrontalDamageReduction * 100f), Num(row.EffectAmount));

        private static string Num(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        // ── 编成（fg.TbEnemyEncounter）──

        /// <summary>某区域某条件下的编成行（按 sortOrder）。O(编成行数)，只在播种 / 进场时调用。</summary>
        public static List<EnemyEncounter> Encounters(string regionId, string condition)
        {
            var list = new List<EnemyEncounter>();
            foreach (EnemyEncounter e in FgContentTables.Encounters)
            {
                if (e != null && e.RegionId == regionId && e.Condition == condition)
                {
                    list.Add(e);
                }
            }
            list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            return list;
        }

        public static bool TryEncounter(string instanceId, out EnemyEncounter row) => FgContentTables.TryGetEncounter(instanceId, out row);

        /// <summary>编成里写的敌人类型；缺编成行时可见报错并返回 null（调用方不生成）。</summary>
        public static string EncounterTypeOf(string regionId, string instanceId)
        {
            if (TryEncounter(instanceId, out EnemyEncounter row) && !string.IsNullOrEmpty(row.EnemyTypeId))
            {
                return row.EnemyTypeId;
            }
            Report("encounter:" + instanceId, GameText.Format("content.enemy.error.missing_encounter", regionId ?? "?", instanceId ?? "?"));
            return null;
        }

        /// <summary>编成行指向的锚点在区域布局里不存在等问题：可见报错。</summary>
        public static void ReportEncounterProblem(string regionId, string detail)
        {
            Report("encounter-problem:" + regionId + ":" + detail,
                GameText.Format("content.enemy.error.missing_encounter", regionId ?? "?", detail ?? "?"));
        }

        /// <summary>正面朝向（编成表 facingX/Y，归一化）。没有编成行 → 朝 -Y（Demo 默认朝入口）。</summary>
        public static Vector2 FacingOf(string instanceId)
        {
            if (TryEncounter(instanceId, out EnemyEncounter row))
            {
                var f = new Vector2(row.FacingX, row.FacingY);
                if (f.sqrMagnitude > 1e-6f)
                {
                    return f.normalized;
                }
            }
            return new Vector2(0f, -1f);
        }

        /// <summary>
        /// 按编成播种一条敌人记录（耐久 = 表 maxHp × 难度敌人耐久倍率，DEBT-FG6DEF09-01）。已存在则不动。
        /// 敌人行缺失 / 不完整时可见报错并返回 null（不生成默认替身）。
        /// </summary>
        public static RegionEnemyRecord SeedFromEncounter(CampaignState state, EnemyEncounter enc, Vector2 position)
        {
            if (state == null || enc == null)
            {
                return null;
            }
            state.RegionEnemies ??= Array.Empty<RegionEnemyRecord>();
            foreach (RegionEnemyRecord existing in state.RegionEnemies)
            {
                if (existing != null && existing.EnemyInstanceId == enc.Id)
                {
                    return existing;
                }
            }
            if (!TryGetValid(enc.RegionId, enc.Id, enc.EnemyTypeId, out MechEnemy row))
            {
                return null;
            }
            float maxHealth = SeedHealth(state, row);
            var rec = new RegionEnemyRecord
            {
                EnemyInstanceId = enc.Id,
                RegionId = enc.RegionId,
                EnemyTypeId = row.Id,
                Position = position,
                Health = maxHealth,
                MaxHealth = maxHealth,
                IsAlive = true,
                CycleCooldownRemaining = enc.InitialCycle,
                SecondaryTimer = 0f,
            };
            var arr = new RegionEnemyRecord[state.RegionEnemies.Length + 1];
            Array.Copy(state.RegionEnemies, arr, state.RegionEnemies.Length);
            arr[arr.Length - 1] = rec;
            state.RegionEnemies = arr;
            return rec;
        }

        /// <summary>播种耐久：表 maxHp × 难度的敌人耐久倍率（首领的耐久真相在状态机里，同样乘倍率，难度说明公开写明）。</summary>
        public static float SeedHealth(CampaignState state, MechEnemy row) =>
            row == null ? 0f : row.MaxHp * Defense.DifficultyService.EnemyHealthMul(state);

        // ── 校验与可见报错 ──

        /// <summary>
        /// 取一行并确认它完整可用；不可用时可见报错（同一问题每份战役存档只通知一次，换存档会重新通知）并返回 false。
        /// <paramref name="where"/>/<paramref name="instanceId"/> 只用于报错文本。
        /// </summary>
        public static bool TryGetValid(string where, string instanceId, string enemyTypeId, out MechEnemy row)
        {
            if (!TryGet(enemyTypeId, out row))
            {
                Report("missing:" + enemyTypeId,
                    GameText.Format("content.enemy.error.missing_row", where ?? "?", instanceId ?? "?", enemyTypeId ?? "null"));
                return false;
            }
            List<string> problems = ProblemsFor(row);
            if (problems.Count > 0)
            {
                ReportBadRow(row.Id, string.Join(", ", problems));
                row = null;
                return false;
            }
            return true;
        }

        /// <summary>一行敌人的结构问题（读法不认识 / 读法需要的参数为 0 / 标签不认识 / 半径为 0 / 有射程没伤害……）。按表版本缓存。</summary>
        public static List<string> ProblemsFor(MechEnemy row)
        {
            EnsureCache();
            string key = row?.Id ?? string.Empty;
            if (ProblemCache.TryGetValue(key, out List<string> cached))
            {
                return cached;
            }
            var problems = new List<string>();
            if (row == null)
            {
                problems.Add("row=null");
            }
            else
            {
                if (!(row.MaxHp > 0f))
                {
                    problems.Add("maxHp");
                }
                if (!(row.Radius > 0f))
                {
                    problems.Add("radius");
                }
                if (!(row.FrontalHalfAngleDeg > 0f))
                {
                    problems.Add("frontalHalfAngleDeg");
                }
                if (string.IsNullOrEmpty(row.NameKey))
                {
                    problems.Add("nameKey");
                }
                if ((row.WeaponRange > 0f) != (row.WeaponDamage > 0f && row.WeaponCooldown > 0f))
                {
                    problems.Add("weaponRange/weaponDamage/weaponCooldown");
                }
                if (!Combat.EnemyBehaviorReadings.TryGet(row.Behavior, out Combat.IEnemyBehaviorReading reading))
                {
                    problems.Add("behavior=" + (row.Behavior ?? "null"));
                }
                else
                {
                    reading.Validate(row, problems);
                }
                foreach (string t in TagsOf(row))
                {
                    if (Array.IndexOf(KnownTags, t) < 0)
                    {
                        problems.Add("tag=" + t);
                    }
                }
            }
            ProblemCache[key] = problems;
            return problems;
        }

        /// <summary>全表的问题清单（自检 / 启动诊断用）。</summary>
        public static List<string> AllProblems()
        {
            var all = new List<string>();
            foreach (MechEnemy row in Rows)
            {
                foreach (string p in ProblemsFor(row))
                {
                    all.Add($"{row?.Id}: {p}");
                }
            }
            return all;
        }

        public static void ReportBadRow(string enemyTypeId, string detail)
        {
            Report("bad:" + enemyTypeId + ":" + detail, GameText.Format("content.enemy.error.bad_row", enemyTypeId ?? "null", detail ?? "?"));
        }

        private static void Report(string dedupeKey, string text)
        {
            Log.Error("[EnemyDefs] " + text);
            CampaignState session = CampaignSession.Current;
            if (!ReferenceEquals(session, _reportedFor))
            {
                Reported.Clear();
                _reportedFor = session;
            }
            if (!Reported.Add(dedupeKey))
            {
                return;
            }
            ReportedCount++;
            try
            {
                NotificationCenter.Post(NotifyContentError, text);
            }
            catch (Exception e)
            {
                Log.Error("[EnemyDefs] 发不出内容表错误通知：" + e.Message);
            }
        }

        /// <summary>测试用：清空“已通知过”的去重集合与计数。</summary>
        public static void ResetReportsForTests()
        {
            Reported.Clear();
            _reportedFor = null;
            ReportedCount = 0;
        }

        private static void EnsureCache()
        {
            int rev = FgContentTables.Revision;
            if (rev == _cacheRevision)
            {
                return;
            }
            _cacheRevision = rev;
            TagCache.Clear();
            ProblemCache.Clear();
            Reported.Clear();
        }

        // ── 旧档升级 ──

        /// <summary>读档时把旧首领记录的类型 ID 改写成敌人表 ID（幂等）。返回改写条数。</summary>
        public static int UpgradeLegacyTypeIds(CampaignState state)
        {
            int n = 0;
            if (state?.RegionEnemies == null)
            {
                return 0;
            }
            foreach (RegionEnemyRecord e in state.RegionEnemies)
            {
                if (e?.EnemyTypeId != null && LegacyTypeIds.TryGetValue(e.EnemyTypeId, out string now))
                {
                    e.EnemyTypeId = now;
                    n++;
                }
            }
            return n;
        }

        // ── 内容目录（图鉴 / 图标 / 显示名 / 音效）──

        private static Dictionary<string, MechanicalContentDef> _defs;
        private static int _defsRevision = int.MinValue;

        /// <summary>内容目录版本（表重载 / 注入 / 切换语言 / 文本表重载后变化），<see cref="MechanicalContentFacade"/> 据此重建。</summary>
        public static int ContentRevision => FgContentTables.Revision * 31 + GameText.Revision * 7 + (int)GameText.Language;

        /// <summary>每种敌人一条内容目录条目（显示名 / 描述 / 数值摘要 / 图标 / 音效都从表与文本键生成）。</summary>
        public static IReadOnlyDictionary<string, MechanicalContentDef> ContentDefs
        {
            get
            {
                int rev = ContentRevision;
                if (_defs == null || rev != _defsRevision)
                {
                    var defs = new Dictionary<string, MechanicalContentDef>(StringComparer.Ordinal);
                    foreach (MechEnemy row in Rows)
                    {
                        if (row != null && !string.IsNullOrEmpty(row.Id) && !defs.ContainsKey(row.Id))
                        {
                            defs[row.Id] = BuildDef(row);
                        }
                    }
                    _defs = defs;
                    _defsRevision = rev;
                }
                return _defs;
            }
        }

        private static MechanicalContentDef BuildDef(MechEnemy row)
        {
            string trait = TraitText(row);
            string hp = "HP" + row.MaxHp.ToString("0", CultureInfo.InvariantCulture);
            return new MechanicalContentDef
            {
                Id = row.Id,
                Category = MechanicalContentCategory.Enemy,
                DisplayName = GameText.Get(row.NameKey),
                Description = DescText(row), // FG9-UX-01：描述里的占位符（例如断链束秒数）按表填上，不再漏出 {0}
                Source = MechanicalContentSource.RegionEncounter,
                SourceDetail = GameText.Get(row.WeaknessKey),
                Slot = row.Role,
                ScrapCost = 0,
                Load = 0,
                ValuesSummary = trait == null ? hp : hp + "；" + trait,
                AiPermission = MechanicalContentAiPermission.EnemyAiOnly,
                IconId = row.IconId,
                ModelId = "silhouette:" + row.Silhouette,
                ActionId = "behavior:" + row.Behavior,
                VfxId = row.VfxId,
                SfxId = row.SfxId,
                PreviewId = "preview_" + row.Id,
                SaveCompatible = true,
                LockedHintText = string.Empty,
                SilhouetteNote = row.Silhouette,
                LegacyFacadeId = null,
                DebtId = "DEBT-ER4CONTENT01-13",
            };
        }
    }
}
