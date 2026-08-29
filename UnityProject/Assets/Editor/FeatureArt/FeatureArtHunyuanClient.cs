using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BinGames.EditorTools.FeatureArt
{
    /// <summary>混元 OpenAI 兼容 HTTP。禁止把 Key / 请求体打进日志。</summary>
    public static class FeatureArtHunyuanClient
    {
        public const string SubmitUrl = "https://tokenhub.tencentmaas.com/v1/api/3d/submit";
        public const string QueryUrl = "https://tokenhub.tencentmaas.com/v1/api/3d/query";
        public const string ModelName = "hy-3d-3.0";

        public sealed class File3D
        {
            public string Type;
            public string Url;
        }

        public static string BuildSubmitJson(string mainBase64, IReadOnlyList<(string ViewType, string Base64)> views)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"model\":\"").Append(ModelName)
                .Append("\",\"GenerateType\":\"LowPoly\",\"EnablePBR\":false,\"ResultFormat\":\"FBX\",");
            sb.Append("\"ImageBase64\":\"").Append(StripDataUri(mainBase64)).Append('"');
            if (views != null && views.Count > 0)
            {
                sb.Append(",\"MultiViewImages\":[");
                for (var i = 0; i < views.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("{\"ViewType\":\"").Append(views[i].ViewType)
                        .Append("\",\"ViewImageBase64\":\"")
                        .Append(StripDataUri(views[i].Base64)).Append("\"}");
                }

                sb.Append(']');
            }

            sb.Append('}');
            return sb.ToString();
        }

        public static string BuildQueryJson(string jobId) =>
            "{\"model\":\"" + ModelName + "\",\"id\":\"" + (jobId ?? "") + "\"}";

        public static UnityWebRequest PostJson(string url, string json, string apiKey)
        {
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? ""));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyAuth(req, apiKey);
            req.timeout = 120;
            return req;
        }

        public static UnityWebRequest GetFile(string url, string destPath, string apiKey = null)
        {
            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
            req.downloadHandler = new DownloadHandlerFile(destPath);
            if (!string.IsNullOrEmpty(apiKey))
            {
                ApplyAuth(req, apiKey);
            }

            req.timeout = 180;
            return req;
        }

        static void ApplyAuth(UnityWebRequest req, string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return;
            }

            req.SetRequestHeader("Authorization", "Bearer " + apiKey);
        }

        static string StripDataUri(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = value.IndexOf(',');
                if (comma >= 0)
                {
                    return value.Substring(comma + 1);
                }
            }

            return value;
        }

        public static bool IsUnauthorized(UnityWebRequest req)
        {
            return req != null && req.responseCode == 401;
        }

        public static bool TryReadJobId(string json, out string jobId, out string error)
        {
            jobId = FirstJobId(json);
            error = ExtractError(json);
            if (!string.IsNullOrEmpty(jobId) && !IsZeroId(jobId))
            {
                error = null;
                return true;
            }

            jobId = null;
            if (string.IsNullOrEmpty(error))
            {
                error = "提交成功但没有任务 id。";
            }

            return false;
        }

        public static bool TryReadQuery(string json, out string status, out string error, out List<File3D> files)
        {
            status = NormalizeStatus(FirstValue(json, "Status", "status"));
            error = ExtractError(json);
            files = ExtractFiles(json);
            if (string.IsNullOrEmpty(status))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "查询响应没有 status。";
                }

                return false;
            }

            return true;
        }

        public static string NormalizeStatus(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                return status;
            }

            switch (status.Trim().ToLowerInvariant())
            {
                case "done":
                case "completed":
                case "success":
                case "succeeded":
                    return "DONE";
                case "fail":
                case "failed":
                case "error":
                case "cancelled":
                case "canceled":
                    return "FAIL";
                case "wait":
                case "waiting":
                case "queued":
                case "pending":
                case "submitted":
                    return "WAIT";
                case "run":
                case "running":
                case "in_progress":
                case "processing":
                    return "RUN";
                default:
                    return status.ToUpperInvariant();
            }
        }

        public static string PickBestFile(List<File3D> files)
        {
            if (files == null || files.Count == 0)
            {
                return null;
            }

            string Best(string type)
            {
                foreach (var f in files)
                {
                    if (f != null && !string.IsNullOrEmpty(f.Url) &&
                        string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase))
                    {
                        return f.Url;
                    }
                }

                return null;
            }

            return Best("FBX") ?? Best("OBJ") ?? Best("GLB") ?? files[0].Url;
        }

        public static string ExtractToFolder(string archiveOrModelPath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            if (IsZip(archiveOrModelPath))
            {
                ZipFile.ExtractToDirectory(archiveOrModelPath, destDir);
                return destDir;
            }

            var ext = Path.GetExtension(archiveOrModelPath);
            var copy = Path.Combine(destDir, "model" + ext);
            File.Copy(archiveOrModelPath, copy, true);
            return destDir;
        }

        public static string FindPreferredModel(string dir)
        {
            string bestFbx = null, bestObj = null, bestGlb = null;
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".fbx" && bestFbx == null)
                {
                    bestFbx = path;
                }
                else if (ext == ".obj" && bestObj == null)
                {
                    bestObj = path;
                }
                else if (ext == ".glb" && bestGlb == null)
                {
                    bestGlb = path;
                }
            }

            return bestFbx ?? bestObj ?? bestGlb;
        }

        public static string TransportError(UnityWebRequest req)
        {
            if (req == null)
            {
                return "请求为空。";
            }

            if (!string.IsNullOrEmpty(req.error) && req.responseCode != 401)
            {
                return "网络错误。";
            }

            if (req.responseCode >= 400)
            {
                return $"HTTP {req.responseCode}";
            }

            return null;
        }

        public static string SafeBody(UnityWebRequest req)
        {
            try
            {
                return req?.downloadHandler?.text ?? "";
            }
            catch
            {
                return "";
            }
        }

        static bool IsZip(string path)
        {
            if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                using (var fs = File.OpenRead(path))
                {
                    return fs.Length >= 2 && fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B;
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ExtractError(string json)
        {
            var message = FirstValue(json, "ErrorMessage", "error_message", "Message", "message");
            return string.IsNullOrEmpty(message) ? null : SanitizeForUi(message);
        }

        public static string SanitizeForUi(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var sb = new StringBuilder(text.Length);
            var i = 0;
            while (i < text.Length)
            {
                if (i + 3 <= text.Length &&
                    text[i] == 's' && text[i + 1] == 'k' && text[i + 2] == '-')
                {
                    sb.Append("sk-***");
                    i += 3;
                    while (i < text.Length && IsKeyChar(text[i]))
                    {
                        i++;
                    }

                    continue;
                }

                sb.Append(text[i++]);
            }

            return sb.ToString();
        }

        public static string ExtractString(string json, string key) => ExtractValue(json, key);

        static string FirstJobId(string json)
        {
            foreach (var key in new[] { "JobId", "job_id", "jobId", "id" })
            {
                var start = 0;
                while (start < (json?.Length ?? 0))
                {
                    var at = IndexOfQuotedKey(json, key, start);
                    if (at < 0)
                    {
                        break;
                    }

                    var value = ReadValueAfterKey(json, at, key.Length);
                    if (!string.IsNullOrEmpty(value) && !IsZeroId(value))
                    {
                        return value;
                    }

                    start = at + key.Length + 2;
                }
            }

            return null;
        }

        static string FirstValue(string json, params string[] keys)
        {
            if (keys == null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                var value = ExtractValue(json, key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return null;
        }

        static bool IsZeroId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return true;
            }

            for (var i = 0; i < id.Length; i++)
            {
                if (id[i] != '0')
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsKeyChar(char c) =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
            (c >= '0' && c <= '9') || c == '_' || c == '-';

        static string ExtractValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
            {
                return null;
            }

            var i = 0;
            while (i < json.Length)
            {
                var at = IndexOfQuotedKey(json, key, i);
                if (at < 0)
                {
                    return null;
                }

                var value = ReadValueAfterKey(json, at, key.Length);
                if (value != null)
                {
                    return value;
                }

                i = at + key.Length + 2;
            }

            return null;
        }

        static string ReadValueAfterKey(string json, int keyQuoteAt, int keyLength)
        {
            var colon = json.IndexOf(':', keyQuoteAt + keyLength + 2);
            if (colon < 0)
            {
                return null;
            }

            var p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p]))
            {
                p++;
            }

            if (p >= json.Length)
            {
                return null;
            }

            if (json[p] == 'n' && json.IndexOf("null", p, StringComparison.Ordinal) == p)
            {
                return null;
            }

            if (json[p] == '"')
            {
                p++;
                var sb = new StringBuilder();
                while (p < json.Length)
                {
                    var c = json[p++];
                    if (c == '\\' && p < json.Length)
                    {
                        sb.Append(json[p++]);
                        continue;
                    }

                    if (c == '"')
                    {
                        return sb.ToString();
                    }

                    sb.Append(c);
                }

                return null;
            }

            if (json[p] == '-' || char.IsDigit(json[p]))
            {
                var start = p;
                if (json[p] == '-')
                {
                    p++;
                }

                while (p < json.Length && char.IsDigit(json[p]))
                {
                    p++;
                }

                return start < p ? json.Substring(start, p - start) : null;
            }

            return null;
        }

        static int FirstKeyIndex(string json, params string[] keys)
        {
            var best = -1;
            if (keys == null)
            {
                return -1;
            }

            foreach (var key in keys)
            {
                var at = IndexOfQuotedKey(json, key, 0);
                if (at >= 0 && (best < 0 || at < best))
                {
                    best = at;
                }
            }

            return best;
        }

        static int IndexOfQuotedKey(string json, string key, int start)
        {
            var i = start;
            while (i < json.Length)
            {
                var q = json.IndexOf('"', i);
                if (q < 0 || q + key.Length + 1 >= json.Length)
                {
                    return -1;
                }

                var match = true;
                for (var k = 0; k < key.Length; k++)
                {
                    if (char.ToLowerInvariant(json[q + 1 + k]) != char.ToLowerInvariant(key[k]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match && json[q + 1 + key.Length] == '"')
                {
                    return q;
                }

                i = q + 1;
            }

            return -1;
        }

        static List<File3D> ExtractFiles(string json)
        {
            var list = new List<File3D>();
            if (string.IsNullOrEmpty(json))
            {
                return list;
            }

            var start = FirstKeyIndex(json, "ResultFile3Ds", "result_file_3ds", "resultFile3Ds", "data");
            if (start < 0)
            {
                return list;
            }

            var slice = json.Substring(start);
            var pos = 0;
            while (pos < slice.Length)
            {
                var typeAt = IndexOfQuotedKey(slice, "type", pos);
                if (typeAt < 0)
                {
                    break;
                }

                var type = ExtractString(slice.Substring(typeAt), "type");
                var urlAt = IndexOfQuotedKey(slice, "url", typeAt);
                var url = urlAt >= 0 ? ExtractString(slice.Substring(urlAt), "url") : null;
                if (!string.IsNullOrEmpty(url) &&
                    !url.StartsWith("http://console.", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new File3D { Type = type ?? "", Url = url });
                }

                pos = typeAt + 6;
            }

            return list;
        }
    }
}
