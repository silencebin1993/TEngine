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
        public const string ModelKey = "BinGames.Hunyuan.Model";
        public const string LastModelKey = "BinGames.Hunyuan.LastModel";
        public const string GenerateTypeKey = "BinGames.Hunyuan.GenerateType";
        public const string EnablePbrKey = "BinGames.Hunyuan.EnablePBR";
        public const string ResultFormatKey = "BinGames.Hunyuan.ResultFormat";
        public const string UseFaceCountKey = "BinGames.Hunyuan.UseFaceCount";
        public const string FaceCountKey = "BinGames.Hunyuan.FaceCount";
        public const string PolygonTypeKey = "BinGames.Hunyuan.PolygonType";
        public const string ParamsFoldoutKey = "BinGames.Hunyuan.ParamsFoldout";
        public const string Model30 = "hy-3d-3.0";
        public const string Model31 = "hy-3d-3.1";
        public const string ModelExpress = "hy-3d-express";
        public const string GenNormal = "Normal";
        public const string GenLowPoly = "LowPoly";
        public const string GenGeometry = "Geometry";
        public const string FormatFbx = "FBX";
        public const string FormatObj = "OBJ";
        public const string PolyTriangle = "triangle";
        public const string PolyQuad = "quadrilateral";
        public const int FaceCountMin = 3000;
        public const int FaceCountMax = 1500000;
        public const int FaceCountDefault = 5000;
        public const int FaceCountBudgetMax = 12000;
        public static readonly string[] ModelIds = { Model30, Model31, ModelExpress };
        public static readonly string[] ModelLabels =
        {
            "混元 3.0 专业（默认）",
            "混元 3.1 专业",
            "混元极速 Express",
        };
        public static readonly string[] GenerateTypeIds = { GenNormal, GenLowPoly, GenGeometry };
        public static readonly string[] GenerateTypeLabels = { "Normal（默认）", "LowPoly", "Geometry（白模）" };
        public static readonly string[] ResultFormatIds = { FormatFbx, FormatObj };
        public static readonly string[] ResultFormatLabels = { "FBX（推荐）", "OBJ" };
        public static readonly string[] PolygonTypeIds = { PolyTriangle, PolyQuad };
        public static readonly string[] PolygonTypeLabels = { "三角面", "四边面" };
        public static readonly int[] FaceCountPresets = { 3000, 5000, 8000, 12000 };
        public static readonly string[] FaceCountPresetLabels = { "3000", "5000", "8000", "12000" };
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

        public static string SelectedModel => NormalizeModel(EditorPrefs.GetString(ModelKey, Model30));

        public static string LastModel =>
            NormalizeModel(EditorPrefs.GetString(LastModelKey, SelectedModel));

        public static int SelectedModelIndex
        {
            get
            {
                var id = SelectedModel;
                for (var i = 0; i < ModelIds.Length; i++)
                {
                    if (ModelIds[i] == id)
                    {
                        return i;
                    }
                }

                return 0;
            }
        }

        public static bool IsExpress(string model) =>
            string.Equals(NormalizeModel(model), ModelExpress, StringComparison.Ordinal);

        public static string NormalizeModel(string model)
        {
            if (string.Equals(model, Model31, StringComparison.OrdinalIgnoreCase))
            {
                return Model31;
            }

            if (string.Equals(model, ModelExpress, StringComparison.OrdinalIgnoreCase))
            {
                return ModelExpress;
            }

            return Model30;
        }

        public static void SetSelectedModel(string model) =>
            EditorPrefs.SetString(ModelKey, NormalizeModel(model));

        public static string GenerateType
        {
            get
            {
                var raw = EditorPrefs.GetString(GenerateTypeKey, GenNormal) ?? GenNormal;
                if (string.Equals(raw, GenLowPoly, StringComparison.OrdinalIgnoreCase))
                {
                    return GenLowPoly;
                }

                if (string.Equals(raw, GenGeometry, StringComparison.OrdinalIgnoreCase))
                {
                    return GenGeometry;
                }

                return GenNormal;
            }
            set
            {
                var next = GenNormal;
                if (string.Equals(value, GenLowPoly, StringComparison.OrdinalIgnoreCase))
                {
                    next = GenLowPoly;
                }
                else if (string.Equals(value, GenGeometry, StringComparison.OrdinalIgnoreCase))
                {
                    next = GenGeometry;
                }

                if (string.Equals(SelectedModel, Model31, StringComparison.Ordinal) &&
                    next == GenLowPoly)
                {
                    next = GenNormal;
                }

                EditorPrefs.SetString(GenerateTypeKey, next);
                if (next == GenGeometry)
                {
                    EnablePBR = false;
                }
            }
        }

        public static bool EnablePBR
        {
            get
            {
                if (GenerateType == GenGeometry)
                {
                    return false;
                }

                return EditorPrefs.GetBool(EnablePbrKey, true);
            }
            set => EditorPrefs.SetBool(EnablePbrKey, GenerateType == GenGeometry ? false : value);
        }

        public static string ResultFormat
        {
            get
            {
                var raw = EditorPrefs.GetString(ResultFormatKey, FormatFbx) ?? FormatFbx;
                return string.Equals(raw, FormatObj, StringComparison.OrdinalIgnoreCase)
                    ? FormatObj
                    : FormatFbx;
            }
            set => EditorPrefs.SetString(ResultFormatKey,
                string.Equals(value, FormatObj, StringComparison.OrdinalIgnoreCase)
                    ? FormatObj
                    : FormatFbx);
        }

        public static bool UseFaceCount
        {
            get => EditorPrefs.GetBool(UseFaceCountKey, true) && GenerateType != GenLowPoly;
            set => EditorPrefs.SetBool(UseFaceCountKey, value && GenerateType != GenLowPoly);
        }

        public static int FaceCount
        {
            get
            {
                var n = EditorPrefs.GetInt(FaceCountKey, FaceCountDefault);
                return Mathf.Clamp(n, FaceCountMin, FaceCountMax);
            }
            set => EditorPrefs.SetInt(FaceCountKey, Mathf.Clamp(value, FaceCountMin, FaceCountMax));
        }

        public static string PolygonType
        {
            get
            {
                var raw = EditorPrefs.GetString(PolygonTypeKey, PolyTriangle) ?? PolyTriangle;
                return string.Equals(raw, PolyQuad, StringComparison.OrdinalIgnoreCase)
                    ? PolyQuad
                    : PolyTriangle;
            }
            set => EditorPrefs.SetString(PolygonTypeKey,
                string.Equals(value, PolyQuad, StringComparison.OrdinalIgnoreCase)
                    ? PolyQuad
                    : PolyTriangle);
        }

        public static bool ParamsFoldout
        {
            get => EditorPrefs.GetBool(ParamsFoldoutKey, true);
            set => EditorPrefs.SetBool(ParamsFoldoutKey, value);
        }

        public static bool ShouldSendFaceCount =>
            UseFaceCount && GenerateType != GenLowPoly;

        public static bool ShouldSendPolygonType => GenerateType == GenLowPoly;

        public static void DrawModelPopup(int width = 200)
        {
            var idx = SelectedModelIndex;
            var next = EditorGUILayout.Popup(idx, ModelLabels, GUILayout.Width(width));
            if (next != idx && next >= 0 && next < ModelIds.Length)
            {
                SetSelectedModel(ModelIds[next]);
                if (SelectedModel == Model31 && GenerateType == GenLowPoly)
                {
                    GenerateType = GenNormal;
                }
            }
        }

        /// <summary>折叠栏：图生可调参数（与模型下拉同一 Prefs 域）。禁用后处理接口。</summary>
        public static void DrawGenerateParamsFoldout(bool disabled = false)
        {
            EditorGUI.BeginDisabledGroup(disabled);
            ParamsFoldout = EditorGUILayout.Foldout(ParamsFoldout, "混元生成参数", true);
            if (!ParamsFoldout)
            {
                EditorGUI.EndDisabledGroup();
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("当前模型", SelectedModel + " · " + ModelLabels[SelectedModelIndex],
                EditorStyles.miniLabel);

            var genIdx = IndexOf(GenerateTypeIds, GenerateType);
            var lowPolyBlocked = string.Equals(SelectedModel, Model31, StringComparison.Ordinal);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("生成类型");
                EditorGUI.BeginDisabledGroup(lowPolyBlocked && genIdx == 1);
                var nextGen = EditorGUILayout.Popup(genIdx, GenerateTypeLabels);
                EditorGUI.EndDisabledGroup();
                if (nextGen != genIdx && nextGen >= 0 && nextGen < GenerateTypeIds.Length)
                {
                    if (lowPolyBlocked && GenerateTypeIds[nextGen] == GenLowPoly)
                    {
                        // 3.1 不可用 LowPoly
                    }
                    else
                    {
                        GenerateType = GenerateTypeIds[nextGen];
                    }
                }
            }

            if (lowPolyBlocked)
            {
                EditorGUILayout.LabelField("混元 3.1 不支持 LowPoly（已灰掉）。", EditorStyles.miniLabel);
            }

            var geometry = GenerateType == GenGeometry;
            var lowPoly = GenerateType == GenLowPoly;
            EditorGUI.BeginDisabledGroup(geometry);
            var pbr = EditorGUILayout.Toggle("开启 PBR", EnablePBR);
            if (!geometry && pbr != EnablePBR)
            {
                EnablePBR = pbr;
            }

            EditorGUI.EndDisabledGroup();
            if (geometry)
            {
                EditorGUILayout.LabelField("Geometry 下 PBR 无效，已强制关闭。", EditorStyles.miniLabel);
            }

            var fmtIdx = IndexOf(ResultFormatIds, ResultFormat);
            var nextFmt = EditorGUILayout.Popup("结果格式", fmtIdx, ResultFormatLabels);
            if (nextFmt != fmtIdx && nextFmt >= 0 && nextFmt < ResultFormatIds.Length)
            {
                ResultFormat = ResultFormatIds[nextFmt];
            }

            EditorGUI.BeginDisabledGroup(lowPoly);
            if (lowPoly)
            {
                EditorGUILayout.Toggle("自定义面数", false);
                EditorGUILayout.LabelField("LowPoly 下自定义面数不生效、不另扣。", EditorStyles.miniLabel);
            }
            else
            {
                var useFace = EditorGUILayout.Toggle("自定义面数", UseFaceCount);
                if (useFace != UseFaceCount)
                {
                    UseFaceCount = useFace;
                }

                if (useFace)
                {
                    FaceCount = EditorGUILayout.IntSlider("面数", FaceCount, FaceCountMin, FaceCountMax);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel("快捷");
                        for (var i = 0; i < FaceCountPresets.Length; i++)
                        {
                            if (GUILayout.Button(FaceCountPresetLabels[i], GUILayout.Width(56)))
                            {
                                FaceCount = FaceCountPresets[i];
                            }
                        }
                    }

                    if (FaceCount > FaceCountBudgetMax)
                    {
                        var prevColor = GUI.color;
                        GUI.color = Color.yellow;
                        EditorGUILayout.LabelField("超出游戏预算，不可绑定，只可当母带", EditorStyles.miniLabel);
                        GUI.color = prevColor;
                    }
                }
            }

            EditorGUI.EndDisabledGroup();

            if (lowPoly)
            {
                var polyIdx = IndexOf(PolygonTypeIds, PolygonType);
                var nextPoly = EditorGUILayout.Popup("多边形类型", polyIdx, PolygonTypeLabels);
                if (nextPoly != polyIdx && nextPoly >= 0 && nextPoly < PolygonTypeIds.Length)
                {
                    PolygonType = PolygonTypeIds[nextPoly];
                }
            }

            EditorGUI.indentLevel--;
            EditorGUI.EndDisabledGroup();
        }

        public static int EstimateCredits(bool sendMultiView)
        {
            if (IsExpress(SelectedModel))
            {
                return 15 + (EnablePBR ? 10 : 0);
            }

            var n = GenerateType == GenLowPoly ? 25 : (GenerateType == GenGeometry ? 15 : 20);
            if (EnablePBR)
            {
                n += 10;
            }

            if (sendMultiView)
            {
                n += 10;
            }

            if (ShouldSendFaceCount)
            {
                n += 10;
            }

            // 指定 ResultFormat 固定 +5（默认也发 FBX）
            n += 5;
            return n;
        }

        public static string CreditHint(bool sendMultiView)
        {
            var n = EstimateCredits(sendMultiView);
            if (IsExpress(SelectedModel))
            {
                return EnablePBR
                    ? $"估 {n} 积分（极速 + PBR，只发主图）"
                    : $"估 {n} 积分（极速，只发主图）";
            }

            var parts = new List<string> { GenerateType };
            if (EnablePBR)
            {
                parts.Add("PBR");
            }

            parts.Add(ResultFormat);
            if (ShouldSendFaceCount)
            {
                parts.Add("面数" + FaceCount);
            }

            if (sendMultiView)
            {
                parts.Add("多视图");
            }

            return $"估 {n} 积分（" + string.Join("+", parts) + "）";
        }

        static int IndexOf(string[] ids, string value)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (string.Equals(ids[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }

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
            EditorPrefs.DeleteKey(LastModelKey);
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
                "默认 Normal + PBR + FBX；折叠栏可改面数等。成品按整包文件夹落盘（网格+贴图同夹），拷走/删除都整夹操作。\n" +
                "`.fbm` 只是 FBX 材质旁路文件夹约定，不是 catalog 序列化；工具只临时用，成功后尽量清掉。\n" +
                "各页勾「发给混元」，只发勾中的图。默认只发概念图；front / left / right / back 要勾了才发。\n" +
                "API Key 必须是 TokenHub（tokenhub.tencentmaas.com）的 sk- Key，保存后请求带 Bearer。\n" +
                "不要填混元对话 Key 或 CAM SecretId/SecretKey。Key 只留本机，不会入库。",
                MessageType.Info);

            EditorGUILayout.LabelField("出模模型", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                FeatureArtHunyuanSettings.DrawModelPopup(240);
                EditorGUILayout.LabelField(
                    FeatureArtHunyuanSettings.CreditHint(false),
                    EditorStyles.miniLabel);
            }

            FeatureArtHunyuanSettings.DrawGenerateParamsFoldout();
            EditorGUILayout.LabelField(
                FeatureArtHunyuanSettings.CreditHint(false),
                EditorStyles.miniLabel);

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
            if (GUILayout.Button("给 Raw 下白模 FBX 补抽贴图（不重新生成、不扣积分）", GUILayout.Width(360)))
            {
                var n = FeatureArtHunyuanGenerate.TryRepairRawEmbeddedMaps(out var log);
                EditorUtility.DisplayDialog("混元生3D", log ?? (n > 0 ? "已补抽。" : "没有可补的。"), "好");
            }

            EditorGUILayout.LabelField(
                "混元常把 PBR 嵌在单个 FBX 里。对象框全白时点上面，不必再烧积分。",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            if (GUILayout.Button("整理混元整包（不扣积分）", GUILayout.Width(240)))
            {
                var n = FeatureArtHunyuanGenerate.TryMigrateLegacyPackages(out var log);
                EditorUtility.DisplayDialog("混元生3D", log ?? (n > 0 ? "已整理。" : "没有可整理的。"), "好");
            }

            EditorGUILayout.LabelField(
                "把 Organ/ 等根下旧的 FBX、散落贴图、共享 output.fbm 收进 {canonical}/ 整包子目录，并尽量重烘 Prefab。",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            if (GUILayout.Button("从整包重烘全部游戏 Prefab（不扣积分）", GUILayout.Width(320)))
            {
                var n = FeatureArtGamePrefabBaker.FixAddressCollisionsAndBake(out var log);
                EditorUtility.DisplayDialog("混元生3D", log ?? (n > 0 ? "已重烘。" : "没有可烘的。"), "好");
            }

            EditorGUILayout.LabelField(
                "母带改名为 {canonical}_src.fbx（避免与 Prefab 撞 YooAsset Address），再烘 {canonical}.prefab + _runtime.mat。",
                EditorStyles.miniLabel);

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
