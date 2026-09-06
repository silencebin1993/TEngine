using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>codex-cross-run-persistence story-001：图鉴存档的磁盘布局（v1）。
    /// 全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>；只存"发现过的 id"，
    /// 全量目录仍由 <c>DataRegistry</c> 提供，存档不复制静态内容。</summary>
    [Serializable]
    public sealed class CodexSaveData
    {
        public int Version = CodexPersistence.CurrentVersion;
        public int[] EnemyIds;
        public int[] CardIds;
        /// <summary>reaction-depth-and-combat-feel story-004：已发现的具名反应短名（如 "CausticBurn"，
        /// 与 <see cref="GameLogic.MetabolicSlice.ContentCatalog.ReactionFeedbackCatalog"/> 的 key 一致）。
        /// 旧存档没有这个字段时 JsonUtility 反序列化为 null，<see cref="CodexPersistence.Load"/> 按空集合处理。</summary>
        public string[] ReactionIds;
    }

    /// <summary>codex-cross-run-persistence story-001：图鉴发现状态的历史集合（一次 Load 的结果）。</summary>
    public readonly struct CodexHistory
    {
        public readonly HashSet<int> EnemyIds;
        public readonly HashSet<int> CardIds;
        public readonly HashSet<string> ReactionIds;

        public CodexHistory(HashSet<int> enemyIds, HashSet<int> cardIds, HashSet<string> reactionIds)
        {
            EnemyIds = enemyIds;
            CardIds = cardIds;
            ReactionIds = reactionIds;
        }

        public static CodexHistory Empty()
        {
            return new CodexHistory(new HashSet<int>(), new HashSet<int>(), new HashSet<string>());
        }
    }

    /// <summary>
    /// codex-cross-run-persistence story-001：图鉴发现状态的跨局持久化 IO。
    /// 独立 JSON 文件（<see cref="Application.persistentDataPath"/>/<c>codex_discovered.json</c>），
    /// 只在离开细胞阶段时由 <see cref="CodexRegistry.OnExit"/> 批量整份覆盖写入一次。
    ///
    /// Reject-to-Safe：<see cref="Load"/>/<see cref="Save"/> 永不 throw——文件缺失/损坏/磁盘异常
    /// 一律降级为"空历史 + 一条 warning 日志"，绝不能因为存档问题挡住进战斗或退出流程。
    /// 只做 v1，不做版本迁移框架（未知 Version 直接按损坏处理，回落空集合）。
    /// </summary>
    public static class CodexPersistence
    {
        public const int CurrentVersion = 1;
        private const string FileName = "codex_discovered.json";

        /// <summary>存档绝对路径。测试/验收可直接读写这个文件。</summary>
        public static string FilePath
        {
            get { return Path.Combine(Application.persistentDataPath, FileName); }
        }

        /// <summary>读取历史发现集合。文件不存在 → 空集合（首次运行，静默）；
        /// 内容损坏/版本未知 → warning 日志 + 空集合。</summary>
        public static CodexHistory Load()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return CodexHistory.Empty();
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CodexPersistence] 读取存档路径失败，按空图鉴继续：{e.Message}");
                return CodexHistory.Empty();
            }

            CodexSaveData data;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    TEngine.Log.Warning("[CodexPersistence] 存档为空文件，按空图鉴继续。");
                    return CodexHistory.Empty();
                }

                data = JsonUtility.FromJson<CodexSaveData>(json);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CodexPersistence] 存档损坏或无法读取，按空图鉴继续：{e.Message}");
                return CodexHistory.Empty();
            }

            if (data == null)
            {
                TEngine.Log.Warning("[CodexPersistence] 存档反序列化为 null，按空图鉴继续。");
                return CodexHistory.Empty();
            }

            if (data.Version != CurrentVersion)
            {
                TEngine.Log.Warning(
                    $"[CodexPersistence] 存档版本 {data.Version} 不是当前版本 {CurrentVersion}（本期不做迁移），按空图鉴继续。");
                return CodexHistory.Empty();
            }

            return new CodexHistory(ToSet(data.EnemyIds), ToSet(data.CardIds), ToSet(data.ReactionIds));
        }

        /// <summary>整份覆盖写入（不是追加）。磁盘异常只记日志，不抛出。</summary>
        public static void Save(IReadOnlyCollection<int> enemyIds, IReadOnlyCollection<int> cardIds, IReadOnlyCollection<string> reactionIds)
        {
            try
            {
                CodexSaveData data = new CodexSaveData
                {
                    Version = CurrentVersion,
                    EnemyIds = ToArray(enemyIds),
                    CardIds = ToArray(cardIds),
                    ReactionIds = ToArray(reactionIds),
                };
                File.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CodexPersistence] 图鉴存档写入失败（本局发现记录将丢失）：{e.Message}");
            }
        }

        private static HashSet<int> ToSet(int[] ids)
        {
            HashSet<int> set = new HashSet<int>();
            if (ids == null)
            {
                return set;
            }

            for (int i = 0; i < ids.Length; i++)
            {
                set.Add(ids[i]);
            }

            return set;
        }

        private static HashSet<string> ToSet(string[] ids)
        {
            HashSet<string> set = new HashSet<string>();
            if (ids == null)
            {
                return set;
            }

            for (int i = 0; i < ids.Length; i++)
            {
                if (!string.IsNullOrEmpty(ids[i]))
                {
                    set.Add(ids[i]);
                }
            }

            return set;
        }

        private static int[] ToArray(IReadOnlyCollection<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return Array.Empty<int>();
            }

            int[] arr = new int[ids.Count];
            int i = 0;
            foreach (int id in ids)
            {
                arr[i++] = id;
            }

            return arr;
        }

        private static string[] ToArray(IReadOnlyCollection<string> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return Array.Empty<string>();
            }

            string[] arr = new string[ids.Count];
            int i = 0;
            foreach (string id in ids)
            {
                arr[i++] = id;
            }

            return arr;
        }
    }
}
