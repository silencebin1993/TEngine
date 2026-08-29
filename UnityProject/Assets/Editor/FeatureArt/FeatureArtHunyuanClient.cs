using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using UnityEditor;
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

        public static bool TryDownload(string url, string destPath, out string error)
        {
            error = null;
            url = NormalizeFileUrl(url);
            if (string.IsNullOrEmpty(url))
            {
                error = "文件地址无效。";
                return false;
            }

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Exception last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    EditorUtility.DisplayProgressBar("混元生3D", $"下载模型（{attempt}/3）…", 0.35f);
                    if (attempt == 3)
                    {
                        DownloadWithWebClient(url, destPath);
                    }
                    else
                    {
                        DownloadWithHttpClient(url, destPath, useProxy: attempt == 1);
                    }

                    if (File.Exists(destPath) && new FileInfo(destPath).Length > 32)
                    {
                        error = null;
                        return true;
                    }

                    last = new IOException("下载文件是空的。");
                }
                catch (Exception e)
                {
                    last = e;
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            error = "下载失败：" + SanitizeForUi(ShortException(last)) + " 主机=" + HostOf(url);
            return false;
        }

        static void DownloadWithWebClient(string url, string destPath)
        {
            using (var wc = new WebClient())
            {
                wc.Proxy = WebRequest.GetSystemWebProxy();
                if (wc.Proxy != null)
                {
                    wc.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }

                wc.Headers.Add("User-Agent", "BinGamesHunyuan/1.0");
                wc.DownloadFile(url, destPath);
            }
        }

        static void DownloadWithHttpClient(string url, string destPath, bool useProxy)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = useProxy,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            if (useProxy)
            {
                handler.Proxy = WebRequest.GetSystemWebProxy();
                if (handler.Proxy != null)
                {
                    handler.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }
            }

            using (handler)
            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BinGamesHunyuan/1.0");
                using (var resp = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                           .ConfigureAwait(false).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception("HTTP " + (int)resp.StatusCode);
                    }

                    using (var src = resp.Content.ReadAsStreamAsync()
                               .ConfigureAwait(false).GetAwaiter().GetResult())
                    using (var dst = File.Create(destPath))
                    {
                        src.CopyTo(dst);
                    }
                }
            }
        }

        public static string NormalizeFileUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            url = url.Trim();
            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                url = "https:" + url;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return url;
        }

        public static string HostOf(string url)
        {
            try
            {
                return new Uri(url).Host;
            }
            catch
            {
                return "(无法解析)";
            }
        }

        static string ShortException(Exception e)
        {
            var msg = e?.GetBaseException()?.Message ?? e?.Message ?? "未知错误";
            var http = msg.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            var q = msg.IndexOf('?');
            if (http >= 0 && q > http)
            {
                msg = msg.Substring(0, q) + "?…";
            }

            if (msg.Length > 180)
            {
                msg = msg.Substring(0, 180) + "…";
            }

            return msg;
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
            var urls = OrderedFileUrls(files);
            return urls.Count > 0 ? urls[0] : null;
        }

        public static List<string> OrderedFileUrls(List<File3D> files)
        {
            var urls = new List<string>();
            if (files == null)
            {
                return urls;
            }

            void AddType(string type)
            {
                foreach (var f in files)
                {
                    if (f == null || string.IsNullOrEmpty(f.Url) ||
                        !string.Equals(f.Type, type, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!urls.Contains(f.Url))
                    {
                        urls.Add(f.Url);
                    }
                }
            }

            AddType("FBX");
            AddType("OBJ");
            AddType("ZIP");
            AddType("GLB");
            foreach (var f in files)
            {
                if (f != null && !string.IsNullOrEmpty(f.Url) && !urls.Contains(f.Url))
                {
                    urls.Add(f.Url);
                }
            }

            return urls;
        }

        public static bool TryMaterializeModel(string downloaded, string destDir, out string modelPath, out string error)
        {
            modelPath = null;
            error = null;
            Directory.CreateDirectory(destDir);
            var kind = DetectKind(downloaded);
            if (kind == FileKind.Zip)
            {
                ZipFile.ExtractToDirectory(downloaded, destDir);
                ExtractNestedZips(destDir);
                modelPath = FindPreferredModel(destDir);
                if (!string.IsNullOrEmpty(modelPath))
                {
                    return true;
                }

                error = "压缩包里没有 FBX/OBJ/GLB。内含：" + ListExts(destDir);
                return false;
            }

            if (kind == FileKind.Fbx || kind == FileKind.Obj || kind == FileKind.Glb)
            {
                modelPath = Path.Combine(destDir, "model" + ExtOf(kind));
                File.Copy(downloaded, modelPath, true);
                return true;
            }

            if (kind == FileKind.Png || kind == FileKind.Jpeg)
            {
                error = "下到的是预览图，不是模型。";
                return false;
            }

            if (kind == FileKind.Json || kind == FileKind.Html)
            {
                error = "下到的不是模型文件（" + kind + "）。";
                return false;
            }

            error = "无法识别下载文件（无扩展名且不是 FBX/OBJ/GLB/ZIP）。";
            return false;
        }

        public static string ExtractToFolder(string archiveOrModelPath, string destDir)
        {
            TryMaterializeModel(archiveOrModelPath, destDir, out _, out _);
            return destDir;
        }

        public static string FindPreferredModel(string dir)
        {
            string bestFbx = null, bestObj = null, bestGlb = null;
            if (!Directory.Exists(dir))
            {
                return null;
            }

            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var kind = ext == ".fbx" ? FileKind.Fbx
                    : ext == ".obj" ? FileKind.Obj
                    : ext == ".glb" ? FileKind.Glb
                    : DetectKind(path);
                if (kind == FileKind.Fbx && bestFbx == null)
                {
                    bestFbx = path;
                }
                else if (kind == FileKind.Obj && bestObj == null)
                {
                    bestObj = path;
                }
                else if (kind == FileKind.Glb && bestGlb == null)
                {
                    bestGlb = path;
                }
            }

            return bestFbx ?? bestObj ?? bestGlb;
        }

        enum FileKind
        {
            Unknown,
            Zip,
            Fbx,
            Obj,
            Glb,
            Png,
            Jpeg,
            Json,
            Html,
        }

        static string ExtOf(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Fbx: return ".fbx";
                case FileKind.Obj: return ".obj";
                case FileKind.Glb: return ".glb";
                case FileKind.Zip: return ".zip";
                default: return ".bin";
            }
        }

        static FileKind DetectKind(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[80];
                    var n = fs.Read(buf, 0, buf.Length);
                    if (n >= 2 && buf[0] == 0x50 && buf[1] == 0x4B)
                    {
                        return FileKind.Zip;
                    }

                    if (n >= 4 && buf[0] == 0x67 && buf[1] == 0x6C && buf[2] == 0x54 && buf[3] == 0x46)
                    {
                        return FileKind.Glb;
                    }

                    if (n >= 3 && buf[0] == 0x89 && buf[1] == 0x50 && buf[2] == 0x4E)
                    {
                        return FileKind.Png;
                    }

                    if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xD8)
                    {
                        return FileKind.Jpeg;
                    }

                    var text = Encoding.ASCII.GetString(buf, 0, n);
                    if (text.StartsWith("Kaydara", StringComparison.Ordinal) ||
                        text.StartsWith("; FBX", StringComparison.OrdinalIgnoreCase))
                    {
                        return FileKind.Fbx;
                    }

                    var trim = text.TrimStart();
                    if (trim.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                        trim.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                    {
                        return FileKind.Html;
                    }

                    if (trim.StartsWith("{") || trim.StartsWith("["))
                    {
                        return FileKind.Json;
                    }

                    if (LooksLikeObj(trim))
                    {
                        return FileKind.Obj;
                    }
                }
            }
            catch
            {
                // fall through
            }

            return FileKind.Unknown;
        }

        static bool LooksLikeObj(string head)
        {
            if (string.IsNullOrEmpty(head))
            {
                return false;
            }

            return head.StartsWith("#", StringComparison.Ordinal) ||
                   head.StartsWith("v ", StringComparison.Ordinal) ||
                   head.StartsWith("o ", StringComparison.Ordinal) ||
                   head.StartsWith("g ", StringComparison.Ordinal) ||
                   head.StartsWith("s ", StringComparison.Ordinal) ||
                   head.StartsWith("vn ", StringComparison.Ordinal) ||
                   head.StartsWith("vt ", StringComparison.Ordinal) ||
                   head.StartsWith("mtllib", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("usemtl", StringComparison.OrdinalIgnoreCase);
        }

        static void ExtractNestedZips(string dir)
        {
            foreach (var zip in Directory.GetFiles(dir, "*.zip", SearchOption.AllDirectories))
            {
                var nest = Path.Combine(Path.GetDirectoryName(zip) ?? dir,
                    Path.GetFileNameWithoutExtension(zip) + "_unz");
                if (Directory.Exists(nest))
                {
                    continue;
                }

                try
                {
                    ZipFile.ExtractToDirectory(zip, nest);
                }
                catch
                {
                    // ignore nested zip failures
                }
            }
        }

        static string ListExts(string dir)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir))
            {
                return "(空)";
            }

            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(path);
                set.Add(string.IsNullOrEmpty(ext) ? "(无扩展名)" : ext.ToLowerInvariant());
            }

            if (set.Count == 0)
            {
                return "(空)";
            }

            var sb = new StringBuilder();
            foreach (var ext in set)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(ext);
            }

            return sb.ToString();
        }

        public static string TransportError(UnityWebRequest req)
        {
            if (req == null)
            {
                return "请求为空。";
            }

            if (!string.IsNullOrEmpty(req.error) && req.responseCode != 401)
            {
                return "网络错误：" + SanitizeForUi(req.error) +
                       (req.responseCode > 0 ? " HTTP " + req.responseCode : "");
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

        static void AppendJsonEscape(StringBuilder sb, string json, ref int p)
        {
            var n = json[p++];
            if (n == 'u' && p + 4 <= json.Length)
            {
                if (int.TryParse(json.Substring(p, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var code))
                {
                    sb.Append((char)code);
                    p += 4;
                    return;
                }
            }

            switch (n)
            {
                case '"':
                case '\\':
                case '/':
                    sb.Append(n);
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                default:
                    sb.Append(n);
                    break;
            }
        }

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
                        AppendJsonEscape(sb, json, ref p);
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
            var slice = start >= 0 ? json.Substring(start) : json;
            var pos = 0;
            while (pos < slice.Length)
            {
                var urlAt = IndexOfQuotedKey(slice, "url", pos);
                if (urlAt < 0)
                {
                    break;
                }

                var url = NormalizeFileUrl(ReadValueAfterKey(slice, urlAt, 3));
                pos = urlAt + 5;
                if (string.IsNullOrEmpty(url) ||
                    url.StartsWith("http://console.", StringComparison.OrdinalIgnoreCase) ||
                    IsPreviewImageUrl(url))
                {
                    continue;
                }

                var type = NearbyType(slice, urlAt) ?? GuessType(url);
                list.Add(new File3D { Type = type ?? "", Url = url });
            }

            return list;
        }

        static string NearbyType(string json, int urlAt)
        {
            var from = Math.Max(0, urlAt - 200);
            var lastType = -1;
            var pos = from;
            while (pos < urlAt)
            {
                var at = IndexOfQuotedKey(json, "type", pos);
                if (at < 0 || at >= urlAt)
                {
                    break;
                }

                lastType = at;
                pos = at + 6;
            }

            return lastType >= 0 ? ExtractString(json.Substring(lastType), "type") : null;
        }

        static string GuessType(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath.ToLowerInvariant();
                if (path.EndsWith(".fbx")) return "FBX";
                if (path.EndsWith(".obj")) return "OBJ";
                if (path.EndsWith(".glb")) return "GLB";
                if (path.EndsWith(".zip")) return "ZIP";
            }
            catch
            {
                // ignore
            }

            return "";
        }

        static bool IsPreviewImageUrl(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath.ToLowerInvariant();
                return path.EndsWith(".png") || path.EndsWith(".jpg") ||
                       path.EndsWith(".jpeg") || path.EndsWith(".webp") ||
                       path.EndsWith(".gif");
            }
            catch
            {
                return false;
            }
        }
    }
}
