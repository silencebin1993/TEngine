using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameLogic.MetabolicSlice.WildOrgan
{
    /// <summary>M4-R00-02 队列⑤-21 的磁盘布局（v1）。全 public 字段（非属性）以兼容
    /// <see cref="JsonUtility"/>。**故意只覆盖 <see cref="WildOrganState.InField"/> 的实物**——
    /// 见 <see cref="WildOrganPersistence"/> 类型注释的范围说明。</summary>
    [Serializable]
    public sealed class WildOrganFieldSaveEntry
    {
        public string SourceId;
        public int Kind;
        public float PositionX;
        public float PositionY;
        public float Contamination;
    }

    [Serializable]
    public sealed class WildOrganSaveData
    {
        public int Version = WildOrganPersistence.CurrentVersion;
        public WildOrganFieldSaveEntry[] FieldEntries;
    }

    /// <summary>一次 Load 的结果。</summary>
    public readonly struct WildOrganFieldHistory
    {
        public readonly IReadOnlyList<WildOrganFieldSaveEntry> Entries;

        public WildOrganFieldHistory(IReadOnlyList<WildOrganFieldSaveEntry> entries)
        {
            Entries = entries;
        }
    }

    /// <summary>
    /// M4-R00-02 队列⑤-21（M3-R04-WILD-FULL-CHAIN"死亡掉落缺失、存读档整段不存在"）：野生器官
    /// 跨局持久化 IO。独立 JSON 文件（<see cref="Application.persistentDataPath"/>/
    /// <c>wild_organ_field.json</c>），节奏同 <see cref="GameLogic.MetabolicSlice.Blueprint.BlueprintPersistence"/>
    /// ——只在离开细胞阶段时整份覆盖写入一次。
    ///
    /// ── 范围刻意收窄：只存 <see cref="WildOrganState.InField"/> ──
    /// <see cref="WildOrganRegistry"/> 的 <c>_carried</c>/<c>_installed</c> 以及
    /// <see cref="WildOrganInstance.OwnerEntityId"/> 都按 <c>SimEntityId</c> 记账，而
    /// <c>SimEntityId</c>"只在生成它的那个 SimWorld 实例内有效，世界一重建旧值就是垃圾"
    /// （见 <see cref="GameLogic.Progression.ControlPersistence"/> 类型注释的同一结论）——本仓目前
    /// 除"当前受控单位"外没有任何"稳定 id ↔ SimEntityId"的重建机制，这正是队列20号
    /// （编队/命令/教义存读档）被折入 M4-R01 的同一根因。贸然把 Carried/Installed 存盘会在读档后
    /// 产生"查得到但挂在错误个体身上"的串体风险，比不存更糟。只有 InField（物理战利品，纯坐标
    /// + 内容 id，不含任何实体引用）能安全落盘，所以本类只覆盖这一部分——地上的战利品能存活，
    /// 已装备/已携带的部分仍会在重开进程后丢失，这是已知、刻意保留的缺口（见
    /// <c>DEBT-WILDORGAN-OWNED-PERSIST-01</c>），不是遗漏。
    ///
    /// Reject-to-Safe：Load/Save 永不 throw；文件缺失/损坏/版本不认得一律回落空集合
    /// （不像 BlueprintPersistence 那样需要"默认解锁"迁移——空场景本就是合法的初始状态）。
    /// </summary>
    public static class WildOrganPersistence
    {
        public const int CurrentVersion = 1;
        private const string FileName = "wild_organ_field.json";

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static WildOrganFieldHistory Load()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return new WildOrganFieldHistory(Array.Empty<WildOrganFieldSaveEntry>());
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[WildOrganPersistence] 读取存档路径失败，按空场景继续：{e.Message}");
                return new WildOrganFieldHistory(Array.Empty<WildOrganFieldSaveEntry>());
            }

            WildOrganSaveData data;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new WildOrganFieldHistory(Array.Empty<WildOrganFieldSaveEntry>());
                }

                data = JsonUtility.FromJson<WildOrganSaveData>(json);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[WildOrganPersistence] 存档损坏或无法读取，按空场景继续：{e.Message}");
                return new WildOrganFieldHistory(Array.Empty<WildOrganFieldSaveEntry>());
            }

            if (data == null)
            {
                return new WildOrganFieldHistory(Array.Empty<WildOrganFieldSaveEntry>());
            }

            if (data.Version != CurrentVersion)
            {
                TEngine.Log.Warning(
                    $"[WildOrganPersistence] 存档版本 {data.Version} 不是当前版本 {CurrentVersion}（本期不做迁移），按空场景继续。");
                return new WildOrganFieldHistory(Array.Empty<WildOrganFieldSaveEntry>());
            }

            var list = new List<WildOrganFieldSaveEntry>();
            if (data.FieldEntries != null)
            {
                foreach (WildOrganFieldSaveEntry e in data.FieldEntries)
                {
                    if (e != null && !string.IsNullOrEmpty(e.SourceId))
                    {
                        list.Add(e);
                    }
                }
            }

            return new WildOrganFieldHistory(list);
        }

        /// <summary>整份覆盖写入（不是追加）。磁盘异常只记日志，不抛出。</summary>
        public static void Save(IReadOnlyCollection<WildOrganFieldSaveEntry> fieldEntries)
        {
            try
            {
                var arr = new WildOrganFieldSaveEntry[fieldEntries.Count];
                int i = 0;
                foreach (WildOrganFieldSaveEntry e in fieldEntries)
                {
                    arr[i++] = e;
                }

                var data = new WildOrganSaveData { Version = CurrentVersion, FieldEntries = arr };
                File.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[WildOrganPersistence] 野生器官存档写入失败（本局地面战利品将丢失）：{e.Message}");
            }
        }
    }
}
