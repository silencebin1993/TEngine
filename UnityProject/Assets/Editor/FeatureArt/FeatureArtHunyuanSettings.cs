using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using Sirenix.Utilities.Editor;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>混元 API Key 与任务历史只进 EditorPrefs，禁止落盘到工程文件。</summary>
    public static class FeatureArtHunyuanSettings
    {
        public const string PrefsKey = "BinGames.Hunyuan.ApiKey";
        public const string LastJobKey = "BinGames.Hunyuan.LastJobId";
        public const string LastSlotKey = "BinGames.Hunyuan.LastSlotId";
        public const string LastFolderKey = "BinGames.Hunyuan.LastFolder";
        public const string LastNameKey = "BinGames.Hunyuan.LastName";
        public const string LastFileUrlKey = "BinGames.Hunyuan.LastFileUrl";
        const string HistoryKey = "BinGames.Hunyuan.JobHistory";
        const int HistoryCap = 20;

        public sealed class JobRecord
        {
            public string JobId;
            public string SlotId;
            public string FileName;
            public string Status;
            public long Unix;
        }

        public static bool HasApiKey => !string.IsNullOrEmpty(GetApiKey());
        public static bool HasLastJob => !string.IsNullOrEmpty(LastJobId);
        public static string LastJobId => EditorPrefs.GetString(LastJobKey, "") ?? "";
        public static string LastSlotId => EditorPrefs.GetString(LastSlotKey, "") ?? "";
        public static string LastFolder => EditorPrefs.GetString(LastFolderKey, "") ?? "";
        public static string LastName => EditorPrefs.GetString(LastNameKey, "") ?? "";
        public static string LastFileUrl => EditorPrefs.GetString(LastFileUrlKey, "") ?? "";
        public static bool HasLastFileUrl =>
            !string.IsNullOrEmpty(LastFileUrl) &&
            (LastFileUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             LastFileUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        public static void RememberLast(string jobId, string slotId, string folder, string fileName,
            string status = "进行中")
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return;
            }

            EditorPrefs.SetString(LastJobKey, jobId);
            EditorPrefs.SetString(LastSlotKey, slotId ?? "");
            EditorPrefs.SetString(LastFolderKey, folder ?? "");
            EditorPrefs.SetString(LastNameKey, fileName ?? "");
            UpsertHistory(jobId, slotId, fileName, status);
        }

        public static void RememberFileUrl(string url)
        {
            url = (url ?? "").Trim();
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            EditorPrefs.SetString(LastFileUrlKey, url);
        }

        public static void MarkHistory(string jobId, string status)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                return;
            }

            UpsertHistory(jobId, LastSlotId, LastName, status);
        }

        public static void ClearLastJob()
        {
            EditorPrefs.DeleteKey(LastJobKey);
            EditorPrefs.DeleteKey(LastSlotKey);
            EditorPrefs.DeleteKey(LastFolderKey);
            EditorPrefs.DeleteKey(LastNameKey);
            EditorPrefs.DeleteKey(LastFileUrlKey);
        }

        public static string GetApiKey() => EditorPrefs.GetString(PrefsKey, "") ?? "";

        public static void SetApiKey(string key)
        {
            var trimmed = (key ?? "").Trim().Trim('\u200b', '\u200c', '\u200d', '\ufeff');
            if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(7).Trim();
            }

            if (string.IsNullOrEmpty(trimmed))
            {
                EditorPrefs.DeleteKey(PrefsKey);
                return;
            }

            EditorPrefs.SetString(PrefsKey, trimmed);
        }

        public static void ClearApiKey() => EditorPrefs.DeleteKey(PrefsKey);

        public static string MaskedPreview()
        {
            var key = GetApiKey();
            if (string.IsNullOrEmpty(key))
            {
                return "";
            }

            if (key.Length <= 10)
            {
                return key.Substring(0, Math.Min(3, key.Length)) + "***";
            }

            var head = key.Length >= 8 ? key.Substring(0, 8) : key.Substring(0, 4);
            var tail = key.Substring(key.Length - 4);
            return head + "***" + tail;
        }

        public static string MaskedTail()
        {
            var preview = MaskedPreview();
            if (string.IsNullOrEmpty(preview))
            {
                return "";
            }

            var star = preview.LastIndexOf("***", StringComparison.Ordinal);
            return star >= 0 && star + 3 < preview.Length ? preview.Substring(star + 3) : preview;
        }

        public static List<JobRecord> LoadHistory()
        {
            var list = new List<JobRecord>();
            var raw = EditorPrefs.GetString(HistoryKey, "") ?? "";
            if (string.IsNullOrEmpty(raw))
            {
                return list;
            }

            var lines = raw.Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var p = line.Split('\t');
                if (p.Length < 5)
                {
                    continue;
                }

                long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix);
                list.Add(new JobRecord
                {
                    JobId = p[0],
                    SlotId = p[1],
                    FileName = p[2],
                    Unix = unix,
                    Status = p[4],
                });
            }

            return list;
        }

        static void UpsertHistory(string jobId, string slotId, string fileName, string status)
        {
            var list = LoadHistory();
            JobRecord found = null;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].JobId == jobId)
                {
                    found = list[i];
                    list.RemoveAt(i);
                    break;
                }
            }

            list.Insert(0, new JobRecord
            {
                JobId = jobId,
                SlotId = slotId ?? found?.SlotId ?? "",
                FileName = fileName ?? found?.FileName ?? "",
                Status = status ?? "",
                Unix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            });

            if (list.Count > HistoryCap)
            {
                list.RemoveRange(HistoryCap, list.Count - HistoryCap);
            }

            var sb = new StringBuilder();
            foreach (var e in list)
            {
                sb.Append(Safe(e.JobId)).Append('\t')
                    .Append(Safe(e.SlotId)).Append('\t')
                    .Append(Safe(e.FileName)).Append('\t')
                    .Append(e.Unix.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Safe(e.Status)).Append('\n');
            }

            EditorPrefs.SetString(HistoryKey, sb.ToString());
        }

        static string Safe(string value) =>
            (value ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
    }

    public sealed class FeatureArtHunyuanSettingsPage
    {
        string _draft = "";

        [OnInspectorGUI]
        void Draw()
        {
            SirenixEditorGUI.Title("混元生3D", null, TextAlignment.Left, true);
            SirenixEditorGUI.MessageBox(
                "TokenHub 专业版 LowPoly + FBX。各页概念图/三视图下勾「发给混元」，只发勾中的图。\n" +
                "默认只发概念图；front / left / right / back 要勾了才发，图越多积分越多。\n" +
                "API Key 必须是 TokenHub（tokenhub.tencentmaas.com）的 sk- Key，保存后请求带 Bearer。\n" +
                "不要填混元对话 Key 或 CAM SecretId/SecretKey。Key 只留本机，不会入库。",
                MessageType.Info);

            if (FeatureArtHunyuanSettings.HasApiKey)
            {
                EditorGUILayout.LabelField("已保存 API Key（只读预览）",
                    FeatureArtHunyuanSettings.MaskedPreview(),
                    EditorStyles.boldLabel);
                EditorGUILayout.LabelField("开头+结尾用来认是哪一把，中间已遮罩。这里不能改、也复制不出完整 Key。",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("尚未保存 API Key。");
            }

            _draft = EditorGUILayout.PasswordField("API Key", _draft);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存", GUILayout.Width(72)))
                {
                    FeatureArtHunyuanSettings.SetApiKey(_draft);
                    _draft = "";
                    GUI.FocusControl(null);
                }

                if (GUILayout.Button("清除", GUILayout.Width(72)))
                {
                    FeatureArtHunyuanSettings.ClearApiKey();
                    _draft = "";
                    GUI.FocusControl(null);
                }
            }

            EditorGUILayout.Space();
            SirenixEditorGUI.Title("运行历史", null, TextAlignment.Left, false);
            EditorGUILayout.LabelField("任务 id 是腾讯每次提交后给的号码。可复制，不能改。只留本机。",
                EditorStyles.miniLabel);

            var history = FeatureArtHunyuanSettings.LoadHistory();
            if (history.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "还没有记下的任务。上一笔超时只写了「生成超时（8 分钟）」，任务 id 没进日志、没进 EditorPrefs。TokenHub 也没有任务列表接口，用量页的 Request ID 对不上。那 40 积分对应的 id 找不回来。之后每次提交都会出现在这里。",
                    MessageType.Info);
            }
            else
            {
                foreach (var e in history)
                {
                    var when = DateTimeOffset.FromUnixTimeSeconds(e.Unix).ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    var line = when + "  " + e.Status + "  槽=" + e.SlotId + "  文件=" + e.FileName +
                               "  id=" + e.JobId;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
                        if (GUILayout.Button("复制 id", GUILayout.Width(64)))
                        {
                            GUIUtility.systemCopyBuffer = e.JobId ?? "";
                        }
                    }
                }
            }

            if (FeatureArtHunyuanSettings.HasLastJob)
            {
                EditorGUILayout.Space();
                SirenixEditorGUI.MessageBox(
                    $"待拉取 id={FeatureArtHunyuanSettings.LastJobId}（槽 {FeatureArtHunyuanSettings.LastSlotId}）。\n" +
                    "到对应功能页点「继续拉取上次任务」，只下载，不再提交、不再扣积分。",
                    MessageType.Warning);
                if (GUILayout.Button("放弃待拉取（不退积分，历史仍保留）", GUILayout.Width(240)))
                {
                    FeatureArtHunyuanSettings.ClearLastJob();
                }
            }
        }
    }
}
