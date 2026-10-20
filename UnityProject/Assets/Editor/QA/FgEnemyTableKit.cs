using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameConfig.fg;
using Luban;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG9-DATA-01 自检工具：按真实 fg.TbMechEnemy / fg.TbEnemyEncounter 构造“改过几列 / 加了几行”的注入表（Luban 字节流），
    /// 证明生产代码确实按表行为（改表 → 行为跟着变）。字段顺序取生成代码里声明的顺序（MetadataToken），与 Luban 读字节的顺序一致；
    /// 构造后回读一遍逐字段比对，顺序不一致会抛异常（不会悄悄写错列）。
    /// </summary>
    internal static class FgEnemyTableKit
    {
        private static FieldInfo[] Fields<T>() => typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.MetadataToken).ToArray();

        /// <summary>一行的全部字段值（按声明顺序，键 = 字段名）。</summary>
        public static Dictionary<string, object> Values<T>(T row)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (FieldInfo f in Fields<T>())
            {
                d[f.Name] = f.GetValue(row);
            }
            return d;
        }

        private static void Write<T>(ByteBuf buf, Dictionary<string, object> values)
        {
            foreach (FieldInfo f in Fields<T>())
            {
                object v = values[f.Name];
                if (f.FieldType == typeof(string))
                {
                    buf.WriteString((string)v ?? string.Empty);
                }
                else if (f.FieldType == typeof(float))
                {
                    buf.WriteFloat(Convert.ToSingle(v));
                }
                else if (f.FieldType == typeof(int))
                {
                    buf.WriteInt(Convert.ToInt32(v));
                }
                else
                {
                    throw new NotSupportedException($"FgEnemyTableKit：{typeof(T).Name}.{f.Name} 类型 {f.FieldType} 不支持");
                }
            }
        }

        /// <summary>
        /// 真实敌人表 → 每行经 <paramref name="edit"/> 改列（返回 false = 删掉这一行）→ 末尾追加 <paramref name="extra"/> 行 → 新表。
        /// </summary>
        public static TbMechEnemy Enemies(Func<Dictionary<string, object>, bool> edit = null, params Dictionary<string, object>[] extra)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (MechEnemy e in ConfigSystem.Instance.Tables.TbMechEnemy.DataList)
            {
                Dictionary<string, object> v = Values(e);
                if (edit == null || edit(v))
                {
                    rows.Add(v);
                }
            }
            rows.AddRange(extra ?? Array.Empty<Dictionary<string, object>>());
            var buf = new ByteBuf();
            buf.WriteSize(rows.Count);
            foreach (Dictionary<string, object> r in rows)
            {
                Write<MechEnemy>(buf, r);
            }
            var table = new TbMechEnemy(buf);
            Verify(rows, table.DataList);
            return table;
        }

        /// <summary>复制一行真实敌人并改列（新敌人 = 已有敌人的副本 + 新 ID，用来证明“按读法 / 标签，不按 ID”）。</summary>
        public static Dictionary<string, object> CloneEnemy(string fromId, string newId, params (string field, object value)[] changes)
        {
            MechEnemy src = ConfigSystem.Instance.Tables.TbMechEnemy.DataList.First(e => e.Id == fromId);
            Dictionary<string, object> v = Values(src);
            v["Id"] = newId;
            foreach ((string field, object value) in changes)
            {
                if (!v.ContainsKey(field))
                {
                    throw new ArgumentException("FgEnemyTableKit：敌人表没有列 " + field);
                }
                v[field] = value;
            }
            return v;
        }

        /// <summary>真实编成表 + 追加 / 改动的行。</summary>
        public static TbEnemyEncounter Encounters(Func<Dictionary<string, object>, bool> edit = null, params Dictionary<string, object>[] extra)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (EnemyEncounter e in ConfigSystem.Instance.Tables.TbEnemyEncounter.DataList)
            {
                Dictionary<string, object> v = Values(e);
                if (edit == null || edit(v))
                {
                    rows.Add(v);
                }
            }
            rows.AddRange(extra ?? Array.Empty<Dictionary<string, object>>());
            var buf = new ByteBuf();
            buf.WriteSize(rows.Count);
            foreach (Dictionary<string, object> r in rows)
            {
                Write<EnemyEncounter>(buf, r);
            }
            var table = new TbEnemyEncounter(buf);
            Verify(rows, table.DataList);
            return table;
        }

        public static Dictionary<string, object> CloneEncounter(string fromId, string newId, params (string field, object value)[] changes)
        {
            EnemyEncounter src = ConfigSystem.Instance.Tables.TbEnemyEncounter.DataList.First(e => e.Id == fromId);
            Dictionary<string, object> v = Values(src);
            v["Id"] = newId;
            foreach ((string field, object value) in changes)
            {
                if (!v.ContainsKey(field))
                {
                    throw new ArgumentException("FgEnemyTableKit：编成表没有列 " + field);
                }
                v[field] = value;
            }
            return v;
        }

        private static void Verify<T>(List<Dictionary<string, object>> expected, IReadOnlyList<T> actual)
        {
            if (expected.Count != actual.Count)
            {
                throw new InvalidOperationException($"FgEnemyTableKit：{typeof(T).Name} 回读行数 {actual.Count} ≠ {expected.Count}");
            }
            for (int i = 0; i < expected.Count; i++)
            {
                Dictionary<string, object> back = Values(actual[i]);
                foreach (KeyValuePair<string, object> kv in expected[i])
                {
                    if (!Equals(Normalize(kv.Value), Normalize(back[kv.Key])))
                    {
                        throw new InvalidOperationException($"FgEnemyTableKit：{typeof(T).Name} 第 {i} 行 {kv.Key} 回读 {back[kv.Key]} ≠ {kv.Value}（字段顺序与字节不一致）");
                    }
                }
            }
        }

        private static object Normalize(object v) => v is float || v is double ? (object)Convert.ToSingle(v) : v ?? string.Empty;
    }
}
