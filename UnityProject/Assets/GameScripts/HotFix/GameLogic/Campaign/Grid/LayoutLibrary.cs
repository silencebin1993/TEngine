using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GameLogic.Localization;
using UnityEngine;

namespace GameLogic.Campaign.Grid
{
    /// <summary>FG3-LOG-07：布局库里的一个布局（名字 + 条目 + 尺寸）。</summary>
    [Serializable]
    public sealed class LayoutRecord
    {
        public string Name = string.Empty;
        /// <summary>序号（“布局 N”；删掉的不复用）。</summary>
        public int Serial;
        public int Width;
        public int Height;
        public PlanEntryBlock Entries = new PlanEntryBlock();
    }

    /// <summary>布局库文件（玩家配置目录下的 JSON）。</summary>
    [Serializable]
    public sealed class LayoutLibraryFile
    {
        public int Version = LayoutLibrary.FileVersion;
        public int NextSerial = 1;
        public List<LayoutRecord> Layouts = new List<LayoutRecord>();
    }

    /// <summary>
    /// FG3-LOG-07（FG03 FGR-LOG-005“布局库：把一组建筑保存成命名布局，带缩略图，之后可以直接放置；布局库跨存档共享，存在玩家配置目录”；
    /// “布局导出为文本和导入（可选，P2）”；FG13 FGU-11 保存、缩略图、放置、删除；FGT-LOG-013）。
    ///
    /// - 存在 <see cref="Application.persistentDataPath"/>/<c>layout_library.json</c>（与存档目录分开：换存档、新开战役都能用；删存档不删布局）。
    ///   只在改动时整份覆盖写（保存 / 改名 / 删除 / 导入），读一次缓存在内存。文件缺失 = 空库；损坏 / 版本不认识 = 原文件改名备份、按空库继续并如实告诉玩家（不抛异常，不挡游戏）。
    /// - 布局里是条目 ID（建筑类型 / 工具），不是存档里的实例：换一局、换一个版本都能读；本局没解锁的条目在放置时标出来不放（负向“布局里包含未解锁的建筑”），
    ///   本版本不认识的条目标“未知的条目”。
    /// - 缩略图按条目画在一张小贴图上（每种件一种颜色，建筑画占地），界面用完成对释放（<see cref="ReleaseThumbnail"/>）。
    /// - 导出 / 导入：一行文本（前缀 + Base64(JSON)），可以贴给别人。
    /// </summary>
    public static class LayoutLibrary
    {
        public const int FileVersion = 1;
        public const string FileName = "layout_library.json";
        public const string ExportPrefix = "BGLAYOUT1:";

        /// <summary>自检：把文件放到临时目录（用完置 null）。</summary>
        public static string DirectoryOverrideForTests;

        private static LayoutLibraryFile _file;
        private static string _loadedPath;

        public static int Revision { get; private set; }

        /// <summary>最近一次读文件时的问题（损坏 / 版本不认识；空 = 没问题）。</summary>
        public static string LastLoadProblem { get; private set; }

        public static string FilePath => Path.Combine(DirectoryOverrideForTests ?? Application.persistentDataPath, FileName);

        public static int Max => Math.Max(1, GridContent.TuningInt("plan.layout_max"));

        public static IReadOnlyList<LayoutRecord> All
        {
            get
            {
                Ensure();
                return _file.Layouts;
            }
        }

        public static int Count => All.Count;

        /// <summary>丢掉内存缓存，下次从文件重读（自检模拟“换一个存档 / 重启游戏”）。</summary>
        public static void Reload()
        {
            _file = null;
            _loadedPath = null;
            Revision++;
        }

        private static void Ensure()
        {
            string path = FilePath;
            if (_file != null && _loadedPath == path)
            {
                return;
            }
            _loadedPath = path;
            LastLoadProblem = null;
            _file = new LayoutLibraryFile();
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }
                string json = File.ReadAllText(path, Encoding.UTF8);
                LayoutLibraryFile f = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<LayoutLibraryFile>(json);
                if (f == null || f.Version != FileVersion || f.Layouts == null)
                {
                    Quarantine(path, GameText.Get("plan.library.load_bad_version"));
                    return;
                }
                f.Layouts.RemoveAll(l => l == null || !PlanEntries.IsValid(l.Entries));
                _file = f;
            }
            catch (Exception e)
            {
                Quarantine(path, e.Message);
            }
        }

        /// <summary>坏文件改名备份（不覆盖玩家的数据），按空库继续。</summary>
        private static void Quarantine(string path, string why)
        {
            LastLoadProblem = GameText.Format("plan.library.load_problem", why ?? string.Empty);
            try
            {
                string backup = path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(path, backup);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[LayoutLibrary] 备份损坏的布局库失败：{e.Message}");
            }
            _file = new LayoutLibraryFile();
        }

        private static bool Write(out string problem)
        {
            problem = null;
            try
            {
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_file), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tmp, path);
                Revision++;
                return true;
            }
            catch (Exception e)
            {
                problem = GameText.Format("plan.library.write_failed", e.Message);
                TEngine.Log.Warning($"[LayoutLibrary] 写入失败：{e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 把一组条目存成新布局。名字空着就叫“布局 N”；满了（plan.layout_max）、件数超上限、没有东西时拒绝并写明原因。
        /// </summary>
        public static bool TrySave(string name, PlanEntryBlock entries, out LayoutRecord saved, out string reason)
        {
            saved = null;
            reason = null;
            Ensure();
            int n = PlanEntries.CountOf(entries);
            if (n == 0)
            {
                reason = GameText.Get("plan.library.nothing_to_save");
                return false;
            }
            if (n > PlanningService.MaxEntries)
            {
                reason = GameText.Format("plan.reason.too_many", n, PlanningService.MaxEntries);
                return false;
            }
            if (_file.Layouts.Count >= Max)
            {
                reason = GameText.Format("plan.library.full", Max);
                return false;
            }
            int serial = _file.NextSerial++;
            PlanEntryBlock copy = PlanEntries.Clone(entries);
            PlanEntries.Bounds(copy, out int minX, out int minY, out int maxX, out int maxY);
            saved = new LayoutRecord
            {
                Name = CleanName(name, serial),
                Serial = serial,
                Width = maxX - minX + 1,
                Height = maxY - minY + 1,
                Entries = copy,
            };
            _file.Layouts.Add(saved);
            if (!Write(out reason))
            {
                _file.Layouts.Remove(saved);
                saved = null;
                return false;
            }
            Core.GuidanceHooks.Raise(Core.GuidanceHooks.BuildFirstLayoutSaved);
            return true;
        }

        public static bool TryRename(int index, string name, out string reason)
        {
            reason = null;
            Ensure();
            if (index < 0 || index >= _file.Layouts.Count)
            {
                reason = GameText.Get("plan.library.not_found");
                return false;
            }
            LayoutRecord l = _file.Layouts[index];
            string old = l.Name;
            l.Name = CleanName(name, l.Serial);
            if (!Write(out reason))
            {
                l.Name = old;
                return false;
            }
            return true;
        }

        public static bool TryDelete(int index, out string reason)
        {
            reason = null;
            Ensure();
            if (index < 0 || index >= _file.Layouts.Count)
            {
                reason = GameText.Get("plan.library.not_found");
                return false;
            }
            LayoutRecord l = _file.Layouts[index];
            _file.Layouts.RemoveAt(index);
            if (!Write(out reason))
            {
                _file.Layouts.Insert(index, l);
                return false;
            }
            return true;
        }

        private static string CleanName(string name, int serial)
        {
            string n = (name ?? string.Empty).Replace("\n", " ").Replace("\r", " ").Trim();
            if (n.Length > 40)
            {
                n = n.Substring(0, 40);
            }
            return n.Length == 0 ? GameText.Format("plan.library.default_name", serial) : n;
        }

        // ── 导出 / 导入（P2）──────────────────────────────────────────────────────

        /// <summary>一行文本：前缀 + Base64(JSON)。</summary>
        public static string Export(LayoutRecord layout)
        {
            if (layout == null)
            {
                return string.Empty;
            }
            string json = JsonUtility.ToJson(layout);
            return ExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        /// <summary>从导出的文本导入成一个新布局（格式不对 / 件数超上限 / 库满时拒绝并写明原因）。</summary>
        public static bool TryImport(string text, out LayoutRecord saved, out string reason)
        {
            saved = null;
            string t = (text ?? string.Empty).Trim();
            if (!t.StartsWith(ExportPrefix, StringComparison.Ordinal))
            {
                reason = GameText.Get("plan.library.import_bad");
                return false;
            }
            LayoutRecord l;
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(t.Substring(ExportPrefix.Length)));
                l = JsonUtility.FromJson<LayoutRecord>(json);
            }
            catch (Exception)
            {
                reason = GameText.Get("plan.library.import_bad");
                return false;
            }
            if (l == null || !PlanEntries.IsValid(l.Entries) || l.Entries.Count == 0)
            {
                reason = GameText.Get("plan.library.import_bad");
                return false;
            }
            return TrySave(l.Name, l.Entries, out saved, out reason);
        }

        // ── 缩略图 ────────────────────────────────────────────────────────────────

        private static readonly Color32 BackColor = new Color32(24, 28, 32, 255);
        private static readonly Color32 BuildingColor = new Color32(120, 160, 210, 255);
        private static readonly Color32 BeltColor = new Color32(230, 190, 70, 255);
        private static readonly Color32 NodeColor = new Color32(240, 130, 50, 255);
        private static readonly Color32 UnderColor = new Color32(170, 120, 70, 255);
        private static readonly Color32 PipeColor = new Color32(70, 200, 200, 255);
        private static readonly Color32 UnknownColor = new Color32(220, 60, 60, 255);

        /// <summary>
        /// 画一张缩略图（plan.thumbnail_px 见方）：布局按比例缩进去、居中；建筑画占地（蓝灰），传送带黄、节点橙、地下传送带两端棕、管线类青，
        /// 认不出来的条目红。调用方负责 <see cref="ReleaseThumbnail"/>。
        /// </summary>
        public static Texture2D BuildThumbnail(PlanEntryBlock entries)
        {
            int px = Math.Max(16, Math.Min(256, GridContent.TuningInt("plan.thumbnail_px")));
            var tex = new Texture2D(px, px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "LayoutThumb" };
            var pixels = new Color32[px * px];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = BackColor;
            }
            PlanEntries.Bounds(entries, out int minX, out int minY, out int maxX, out int maxY);
            int w = maxX - minX + 1;
            int h = maxY - minY + 1;
            float scale = Mathf.Max(1f, Mathf.Max(w, h)) / (px - 4f);
            float offX = (px - w / scale) * 0.5f;
            float offY = (px - h / scale) * 0.5f;
            var cells = new List<GridCell>(25);
            int n = PlanEntries.CountOf(entries);
            for (int i = 0; i < n; i++)
            {
                PlanEntryKind k = PlanEntries.KindOf(entries.Ids[i], out _);
                Color32 color;
                cells.Clear();
                switch (k)
                {
                    case PlanEntryKind.Building:
                        color = BuildingColor;
                        var g = GridContent.Building(entries.Ids[i]);
                        GridMath.FootprintCells(new GridCell(entries.Xs[i], entries.Ys[i]), g.FootprintW, g.FootprintH, GridMath.NormalizeRotation(entries.Rots[i]), cells);
                        break;
                    case PlanEntryKind.Belt:
                        color = BeltColor;
                        cells.Add(new GridCell(entries.Xs[i], entries.Ys[i]));
                        break;
                    case PlanEntryKind.Splitter:
                    case PlanEntryKind.Merger:
                        color = NodeColor;
                        cells.Add(new GridCell(entries.Xs[i], entries.Ys[i]));
                        break;
                    case PlanEntryKind.Underground:
                        color = UnderColor;
                        cells.Add(new GridCell(entries.Xs[i], entries.Ys[i]));
                        cells.Add(new GridCell(entries.X2s[i], entries.Y2s[i]));
                        break;
                    case PlanEntryKind.Unknown:
                        color = UnknownColor;
                        cells.Add(new GridCell(entries.Xs[i], entries.Ys[i]));
                        break;
                    default:
                        color = PipeColor;
                        cells.Add(new GridCell(entries.Xs[i], entries.Ys[i]));
                        break;
                }
                foreach (GridCell c in cells)
                {
                    int x0 = Mathf.FloorToInt(offX + (c.X - minX) / scale);
                    int y0 = Mathf.FloorToInt(offY + (c.Y - minY) / scale);
                    int x1 = Mathf.Max(x0, Mathf.FloorToInt(offX + (c.X - minX + 1) / scale) - 1);
                    int y1 = Mathf.Max(y0, Mathf.FloorToInt(offY + (c.Y - minY + 1) / scale) - 1);
                    for (int y = Mathf.Max(0, y0); y <= Mathf.Min(px - 1, y1); y++)
                    {
                        for (int x = Mathf.Max(0, x0); x <= Mathf.Min(px - 1, x1); x++)
                        {
                            pixels[y * px + x] = color;
                        }
                    }
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        public static void ReleaseThumbnail(Texture2D tex)
        {
            if (tex != null)
            {
                GameLogic.View.UnityObjects.Release(tex);
            }
        }

        /// <summary>缩略图上某个像素的颜色是不是某种件的颜色（自检读画面用）。</summary>
        public static bool IsColor(Color32 c, PlanEntryKind kind)
        {
            Color32 want = kind == PlanEntryKind.Building ? BuildingColor : kind == PlanEntryKind.Belt ? BeltColor
                : kind == PlanEntryKind.Splitter || kind == PlanEntryKind.Merger ? NodeColor : kind == PlanEntryKind.Underground ? UnderColor
                : kind == PlanEntryKind.Unknown ? UnknownColor : PipeColor;
            return c.r == want.r && c.g == want.g && c.b == want.b;
        }
    }
}
