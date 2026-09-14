using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameLogic.MetabolicSlice.Blueprint
{
    /// <summary>M3-02：单条蓝图的磁盘布局（v1）。全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>。</summary>
    [Serializable]
    public sealed class BlueprintSaveEntry
    {
        public string SourceId;
        public int Kind;
        public float Completeness;
        public float Contamination;
        public int RepeatResolveCount;
    }

    [Serializable]
    public sealed class BlueprintSaveData
    {
        public int Version = BlueprintPersistence.CurrentVersion;
        public BlueprintSaveEntry[] Entries;
    }

    /// <summary>一次 Load 的结果：可能来自磁盘，也可能来自 <see cref="BlueprintMigration"/> 的默认迁移。</summary>
    public readonly struct BlueprintHistory
    {
        public readonly IReadOnlyList<BlueprintEntry> Entries;

        public BlueprintHistory(IReadOnlyList<BlueprintEntry> entries)
        {
            Entries = entries;
        }
    }

    /// <summary>
    /// M3-02：蓝图库跨局持久化 IO。独立 JSON 文件（<see cref="Application.persistentDataPath"/>/
    /// <c>blueprint_library.json</c>），只在离开细胞阶段时整份覆盖写入一次（节奏同
    /// <see cref="GameLogic.Progression.CodexPersistence"/>）。
    ///
    /// 与 CodexPersistence 的关键差异：文件缺失/损坏/版本不认得时**不是**回落空集合——
    /// 蓝图库是新引入的门，缺档回落空集合等于让老存档一夜之间从"什么都能用"变成
    /// "什么都解锁不了"，这不是迁移是倒退。统一回落 <see cref="BlueprintMigration.BuildLegacyDefaults"/>
    /// （全目录默认解锁）。Reject-to-Safe：Load/Save 永不 throw。本期只做 v1，不做版本迁移框架。
    /// </summary>
    public static class BlueprintPersistence
    {
        public const int CurrentVersion = 1;
        private const string FileName = "blueprint_library.json";

        public static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static BlueprintHistory Load()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return BlueprintMigration.BuildLegacyDefaults();
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[BlueprintPersistence] 读取存档路径失败，按全目录默认解锁迁移继续：{e.Message}");
                return BlueprintMigration.BuildLegacyDefaults();
            }

            BlueprintSaveData data;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    TEngine.Log.Warning("[BlueprintPersistence] 存档为空文件，按全目录默认解锁迁移继续。");
                    return BlueprintMigration.BuildLegacyDefaults();
                }

                data = JsonUtility.FromJson<BlueprintSaveData>(json);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[BlueprintPersistence] 存档损坏或无法读取，按全目录默认解锁迁移继续：{e.Message}");
                return BlueprintMigration.BuildLegacyDefaults();
            }

            if (data == null)
            {
                TEngine.Log.Warning("[BlueprintPersistence] 存档反序列化为 null，按全目录默认解锁迁移继续。");
                return BlueprintMigration.BuildLegacyDefaults();
            }

            if (data.Version != CurrentVersion)
            {
                TEngine.Log.Warning(
                    $"[BlueprintPersistence] 存档版本 {data.Version} 不是当前版本 {CurrentVersion}（本期不做迁移），按全目录默认解锁迁移继续。");
                return BlueprintMigration.BuildLegacyDefaults();
            }

            var list = new List<BlueprintEntry>();
            if (data.Entries != null)
            {
                foreach (BlueprintSaveEntry e in data.Entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.SourceId))
                    {
                        continue;
                    }

                    BlueprintSourceKind kind = e.Kind == (int)BlueprintSourceKind.Gene
                        ? BlueprintSourceKind.Gene
                        : BlueprintSourceKind.Organelle;
                    list.Add(new BlueprintEntry(e.SourceId, kind, e.Completeness, e.Contamination, e.RepeatResolveCount));
                }
            }

            return new BlueprintHistory(list);
        }

        /// <summary>整份覆盖写入（不是追加）。磁盘异常只记日志，不抛出。</summary>
        public static void Save(IReadOnlyCollection<BlueprintEntry> entries)
        {
            try
            {
                var arr = new BlueprintSaveEntry[entries.Count];
                int i = 0;
                foreach (BlueprintEntry e in entries)
                {
                    arr[i++] = new BlueprintSaveEntry
                    {
                        SourceId = e.SourceId,
                        Kind = (int)e.Kind,
                        Completeness = e.Completeness,
                        Contamination = e.Contamination,
                        RepeatResolveCount = e.RepeatResolveCount,
                    };
                }

                var data = new BlueprintSaveData { Version = CurrentVersion, Entries = arr };
                File.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[BlueprintPersistence] 蓝图存档写入失败（本局解析进度将丢失）：{e.Message}");
            }
        }
    }
}
