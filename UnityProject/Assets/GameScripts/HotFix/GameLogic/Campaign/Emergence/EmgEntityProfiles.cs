using System;
using System.Collections.Generic;
using GameConfig.fg;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>一行编好的实体档案（材质值已代入 −1 列）。字段与内核 EmgProfile 一一对应，由桥接层（Campaign/Combat）转成内核结构。</summary>
    public struct EmgProfileRow
    {
        public string Id;
        public float Mass, Conductive, Flammable, Magnetic;
        public float ResistCold, ResistHeat, ResistElec, ResistCorrode, ResistMag, ResistHack;
        public float Brittle;
        /// <summary>材质码 1～5（钢 / 合金 / 复合 / 陶瓷 / 玻璃，与内核 EmgConst.Mat* 同值）。</summary>
        public byte Material;
        public byte Displace;
    }

    /// <summary>
    /// EMG1-ENT-01（FG19 FGR-EMG-111；FG19B 第 6 节；ADR-EMG-001 ⑦“静态属性走档案表”）：把 fg.TbEntityProfile 编成内核档案表。
    /// 顺序：前 11 行 = 种类默认（<see cref="SlotIds"/>，与内核 EmgConst.Slot* 同序），其后每个底盘一行、敌人 × 5 材质、首领 × 5 材质。
    /// 机器按底盘取行（质量），敌人按 fg.TbMechEnemy 的 material 列取行；规则只读档案的列值，不认底盘 / 敌人 ID（FGR-EMG-013）。
    /// 只在载入 / 表重载时编（O(表行数)），不按帧。表缺失或缺行时返回空（内核保留 FG19B 初值）并报错。
    /// </summary>
    public static class EmgEntityProfiles
    {
        public static readonly string[] SlotIds =
        {
            "entity.projectile", "entity.projectile_heavy", "entity.drone", "entity.sentry", "entity.fragment", "entity.machine",
            "entity.enemy", "entity.boss", "entity.turret", "entity.structure", "entity.wreck",
        };

        public static readonly string[] MaterialIds = { "material.steel", "material.alloy", "material.composite", "material.ceramic", "material.glass" };

        public sealed class Compiled
        {
            public readonly List<EmgProfileRow> Rows = new List<EmgProfileRow>();
            public readonly Dictionary<string, short> ByChassis = new Dictionary<string, short>(StringComparer.Ordinal);
            /// <summary>（材质, 是否首领）→ 行。</summary>
            public readonly Dictionary<string, short> EnemyByMaterial = new Dictionary<string, short>(StringComparer.Ordinal);
            public readonly Dictionary<string, short> BossByMaterial = new Dictionary<string, short>(StringComparer.Ordinal);
            public readonly Dictionary<string, float> Kinetic = new Dictionary<string, float>(StringComparer.Ordinal);
            public int FragCount;
            public float FragDamage, FragSpeed, FragRange, FragThreshold;
            /// <summary>EMG1-FW-01：fragment.era（爆炸反应装甲起爆破片，FG19B 第 2.9 节）。缺行时取 FG19B 初值。</summary>
            public int EraFragCount = BinGames.Sim.Combat.EmgEntityParams.Default.EraFragCount;
            public float EraFragDamage = BinGames.Sim.Combat.EmgEntityParams.Default.EraFragDamage, EraFragSpeed = BinGames.Sim.Combat.EmgEntityParams.Default.EraFragSpeed, EraFragRange = BinGames.Sim.Combat.EmgEntityParams.Default.EraFragRange;
            public readonly List<string> Problems = new List<string>();
            public bool Ok => Problems.Count == 0 && Rows.Count >= SlotIds.Length;
        }

        private static Compiled _current;
        private static IReadOnlyList<EntityProfile> _override;

        public static Compiled Current => _current ??= Compile(_override ?? Load());

        public static void Reload() => _current = null;

        /// <summary>测试注入（null = 真实表）。</summary>
        public static void OverrideForTests(IReadOnlyList<EntityProfile> rows)
        {
            _override = rows;
            _current = null;
        }

        private static IReadOnlyList<EntityProfile> Load()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbEntityProfile?.DataList;
            }
            catch (Exception e)
            {
                Log.Error("[EmgEntityProfiles] 读取 fg.TbEntityProfile 失败：" + e.Message);
                return null;
            }
        }

        public static byte MaterialCode(string materialId)
        {
            int i = Array.IndexOf(MaterialIds, materialId);
            return (byte)(i < 0 ? 0 : i + 1);
        }

        /// <summary>纯函数：表行 → 编好的档案。坏行（材质不认识、数值越界）进问题清单、不进表。</summary>
        public static Compiled Compile(IReadOnlyList<EntityProfile> src)
        {
            var c = new Compiled();
            if (src == null || src.Count == 0)
            {
                c.Problems.Add("fg.TbEntityProfile 为空或不存在（改 tools/cell_tables/fgdata_emg.py 后重新生成）");
                return c;
            }
            var byId = new Dictionary<string, EntityProfile>(StringComparer.Ordinal);
            foreach (EntityProfile r in src)
            {
                if (r?.Id != null)
                {
                    byId[r.Id] = r;
                }
            }
            foreach (string m in MaterialIds)
            {
                if (!byId.ContainsKey(m))
                {
                    c.Problems.Add($"fg.TbEntityProfile 缺材质行 {m}");
                }
            }
            if (c.Problems.Count > 0)
            {
                return c;
            }
            foreach (string slot in SlotIds)
            {
                if (!byId.TryGetValue(slot, out EntityProfile r) || !TryRow(c, byId, r, r.Material, out EmgProfileRow row))
                {
                    c.Problems.Add($"fg.TbEntityProfile 缺种类默认行或行不合法 {slot}");
                    return c;
                }
                c.Rows.Add(row);
            }
            foreach (EntityProfile r in src)
            {
                if (r?.Kind == "chassis" && TryRow(c, byId, r, r.Material, out EmgProfileRow row))
                {
                    c.ByChassis[r.Id] = (short)c.Rows.Count;
                    c.Rows.Add(row);
                }
                else if (r?.Kind == "kinetic")
                {
                    c.Kinetic[r.Id] = r.Kinetic;
                }
                else if (r?.Id == "fragment.wreck_heavy")
                {
                    c.FragCount = r.Count;
                    c.FragDamage = r.Damage;
                    c.FragSpeed = r.Speed;
                    c.FragRange = r.Range;
                    c.FragThreshold = r.Threshold;
                }
                else if (r?.Id == "fragment.era")
                {
                    c.EraFragCount = r.Count;
                    c.EraFragDamage = r.Damage;
                    c.EraFragSpeed = r.Speed;
                    c.EraFragRange = r.Range;
                }
            }
            foreach (string m in MaterialIds)
            {
                if (TryRow(c, byId, byId["entity.enemy"], m, out EmgProfileRow e))
                {
                    c.EnemyByMaterial[m] = (short)c.Rows.Count;
                    c.Rows.Add(e);
                }
                if (TryRow(c, byId, byId["entity.boss"], m, out EmgProfileRow b))
                {
                    c.BossByMaterial[m] = (short)c.Rows.Count;
                    c.Rows.Add(b);
                }
            }
            return c;
        }

        /// <summary>实体 / 底盘行的 −1 列取材质值。</summary>
        private static bool TryRow(Compiled c, Dictionary<string, EntityProfile> byId, EntityProfile r, string materialId, out EmgProfileRow row)
        {
            row = default;
            if (!byId.TryGetValue(materialId ?? string.Empty, out EntityProfile m) || m.Kind != "material")
            {
                c.Problems.Add($"实体档案 {r.Id} 的材质 {materialId} 不认识");
                return false;
            }
            float Pick(float own, float mat) => own >= 0f ? own : mat;
            row = new EmgProfileRow
            {
                Id = r.Id + (materialId == r.Material ? string.Empty : "@" + materialId),
                Mass = r.Mass, Conductive = Pick(r.Conductive, m.Conductive), Flammable = Pick(r.Flammable, m.Flammable), Magnetic = Pick(r.Magnetic, m.Magnetic),
                ResistCold = Pick(r.ResistCold, m.ResistCold), ResistHeat = Pick(r.ResistHeat, m.ResistHeat), ResistElec = Pick(r.ResistElec, m.ResistElec),
                ResistCorrode = Pick(r.ResistCorrode, m.ResistCorrode), ResistMag = Pick(r.ResistMag, m.ResistMag), ResistHack = Pick(r.ResistHack, m.ResistHack),
                Brittle = m.Brittle, Material = MaterialCode(materialId), Displace = (byte)(r.Displace != 0 ? 1 : 0),
            };
            bool ok = row.Mass >= 0f && In01(row.Conductive) && In01(row.Flammable) && In01(row.Magnetic) && In01(row.ResistCold) && In01(row.ResistHeat)
                      && In01(row.ResistElec) && In01(row.ResistCorrode) && In01(row.ResistMag) && In01(row.ResistHack);
            if (!ok)
            {
                c.Problems.Add($"实体档案 {r.Id} 数值越界（导电 / 可燃 / 磁性 / 抗性须在 0～1，质量 ≥ 0）");
            }
            return ok;
        }

        private static bool In01(float v) => v >= 0f && v <= 1f;

        /// <summary>机器的档案行（按底盘；不认识的底盘用种类默认 −1）。</summary>
        public static short ForChassis(string chassisId) => chassisId != null && Current.ByChassis.TryGetValue(chassisId, out short i) ? i : (short)-1;

        /// <summary>敌人的档案行（按 fg.TbMechEnemy 的 material 列；首领另一组，质量 200、不位移）。</summary>
        public static short ForEnemy(string materialId, bool boss)
        {
            Dictionary<string, short> map = boss ? Current.BossByMaterial : Current.EnemyByMaterial;
            return materialId != null && map.TryGetValue(materialId, out short i) ? i : (short)-1;
        }

        public static float KineticOf(string id, float fallback) => Current.Kinetic.TryGetValue(id, out float v) ? v : fallback;
    }
}
