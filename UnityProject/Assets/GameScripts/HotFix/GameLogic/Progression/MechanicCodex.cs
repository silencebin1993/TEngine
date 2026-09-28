using System;
using System.Collections.Generic;
using System.IO;
using GameConfig;
using GameConfig.fg;
using GameLogic.Settings;
using TEngine;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>机制图鉴解锁记录的磁盘布局（v1）。全 public 字段以兼容 <see cref="JsonUtility"/>；只存“解锁过的条目 ID”，条目内容在 fg.TbCodexEntry。</summary>
    [Serializable]
    public sealed class MechanicCodexSaveData
    {
        public int Version = MechanicCodex.FileVersion;
        public string[] Unlocked = Array.Empty<string>();
    }

    /// <summary>一条机制图鉴条目（fg.TbCodexEntry 的运行时视图；links / hooks 已拆好）。</summary>
    public sealed class MechanicCodexEntry
    {
        public string Id;
        public string Tab;
        public string TitleKey;
        public string BodyKey;
        public string HintKey;
        public int SortOrder;
        public string[] Links = Array.Empty<string>();
        public string[] Hooks = Array.Empty<string>();
    }

    /// <summary>
    /// FG1-HUD-01（FG13 FGR-UX-050“系统说明”页签、FGR-UX-051；FG01 第 4 章“图鉴有信号接入 / 接入口 / 核心固件 / 安全模式 / 裸跑条目”）：机制图鉴。
    /// - 条目内容：fg.TbCodexEntry（数据源 tools/cell_tables/fgdata_hud.py），标题 / 正文 / 获取提示走文本键。
    /// - 解锁：条目的 hooks = 首次接触它的引导钩子；<see cref="Core.GuidanceHooks.Raise"/> 每一次触发都调 <see cref="OnHook"/>（不只第一次），
    ///   所以图鉴文件丢失后，下一次接触同一机制仍会解锁。载入时把本机设置里已经见过的钩子补解锁（老玩家不会面对一片剪影）。
    ///   “?”按钮打开某条目 = 玩家正在这个机制的界面上，直接解锁（<see cref="Open"/>）。
    /// - 持久化：机制条目跨存档共享（沿用 <see cref="CodexPersistence"/> 的方式：独立 JSON 文件、整份覆盖写、读失败回落空集合不抛异常）。
    /// - 完整图鉴（固件 / 反应 / 敌人页签、搜索、从悬停提示按键跳转）是 FG2-FW-05 / FG9-UX-01 的交付，沿用本表扩行、本类扩页签。
    /// 开销：只在钩子触发 / 打开面板时工作，O(条目数)；不按帧。
    /// </summary>
    public static class MechanicCodex
    {
        public const int FileVersion = 1;
        private const string FileName = "codex_mechanics.json";

        /// <summary>“打开某条目”的请求（界面订阅；参数为条目 ID）。</summary>
        public static event Action<string> OpenRequested;

        /// <summary>自检把文件放到临时目录。</summary>
        public static string FilePathOverrideForTests;

        private static List<MechanicCodexEntry> _entries;
        private static Dictionary<string, MechanicCodexEntry> _byId;
        private static HashSet<string> _unlocked;
        private static string _loadError;

        public static int Revision { get; private set; } = 1;
        public static int SaveCount { get; private set; }
        public static int OpenCount { get; private set; }
        public static string LastOpenedId { get; private set; }

        public static string FilePath => FilePathOverrideForTests ?? Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>配置表不可用时的原因（界面显示错误态）；可用时为 null。</summary>
        public static string LoadError
        {
            get
            {
                EnsureEntries();
                return _loadError;
            }
        }

        /// <summary>全部条目（按 sortOrder）。</summary>
        public static IReadOnlyList<MechanicCodexEntry> Entries
        {
            get
            {
                EnsureEntries();
                return _entries;
            }
        }

        public static MechanicCodexEntry Find(string id)
        {
            EnsureEntries();
            return id != null && _byId.TryGetValue(id, out MechanicCodexEntry e) ? e : null;
        }

        public static bool IsUnlocked(string id)
        {
            EnsureUnlocked();
            return id != null && _unlocked.Contains(id);
        }

        public static int UnlockedCount
        {
            get
            {
                EnsureEntries();
                EnsureUnlocked();
                int n = 0;
                foreach (MechanicCodexEntry e in _entries)
                {
                    if (_unlocked.Contains(e.Id))
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        /// <summary>引导钩子被触发（每一次）：解锁以它为钩子的条目。新解锁时写盘一次。</summary>
        public static void OnHook(string hookId)
        {
            if (string.IsNullOrEmpty(hookId))
            {
                return;
            }
            EnsureEntries();
            EnsureUnlocked();
            bool changed = false;
            foreach (MechanicCodexEntry e in _entries)
            {
                if (Array.IndexOf(e.Hooks, hookId) >= 0 && _unlocked.Add(e.Id))
                {
                    changed = true;
                }
            }
            if (changed)
            {
                Revision++;
                Save();
            }
        }

        /// <summary>解锁一条（“?”按钮打开时）。返回 true = 这次才解锁。</summary>
        public static bool Unlock(string id)
        {
            if (Find(id) == null)
            {
                return false;
            }
            EnsureUnlocked();
            if (!_unlocked.Add(id))
            {
                return false;
            }
            Revision++;
            Save();
            return true;
        }

        /// <summary>请求打开图鉴并定位到 <paramref name="id"/>（“?”按钮、暂停菜单“图鉴”）。<paramref name="unlock"/> = 从该机制的界面上打开，顺带解锁。</summary>
        public static void Open(string id, bool unlock = true)
        {
            if (unlock && id != null)
            {
                Unlock(id);
            }
            OpenCount++;
            LastOpenedId = id;
            OpenRequested?.Invoke(id);
        }

        // ── 载入 / 保存 ──────────────────────────────────────────────────────────

        public static void Reload()
        {
            _entries = null;
            _byId = null;
            _unlocked = null;
            _loadError = null;
            Revision++;
        }

        public static void ResetForTests()
        {
            Reload();
            _loggedError = false;
            FilePathOverrideForTests = null;
            OpenRequested = null;
            SaveCount = 0;
            OpenCount = 0;
            LastOpenedId = null;
        }

        private static bool _loggedError;

        private static void EnsureEntries()
        {
            // 读表失败（配置还没载入 / 表缺失）时不缓存失败结果：下一次访问再试，配置载入后自动恢复。
            if (_entries != null && _loadError == null)
            {
                return;
            }
            _loadError = null;
            _unlocked = null;
            _entries = new List<MechanicCodexEntry>();
            _byId = new Dictionary<string, MechanicCodexEntry>(StringComparer.Ordinal);
            try
            {
                TbCodexEntry table = ConfigSystem.Instance.Tables?.TbCodexEntry;
                if (table == null)
                {
                    _loadError = "配置表 fg.TbCodexEntry 不存在";
                }
                else
                {
                    foreach (CodexEntry row in table.DataList)
                    {
                        var e = new MechanicCodexEntry
                        {
                            Id = row.Id,
                            Tab = row.Tab,
                            TitleKey = row.TitleKey,
                            BodyKey = row.BodyKey,
                            HintKey = row.HintKey,
                            SortOrder = row.SortOrder,
                            Links = Split(row.Links),
                            Hooks = Split(row.Hooks),
                        };
                        _entries.Add(e);
                        _byId[e.Id] = e;
                    }
                    _entries.Sort((a, b) => a.SortOrder != b.SortOrder ? a.SortOrder.CompareTo(b.SortOrder) : string.CompareOrdinal(a.Id, b.Id));
                }
            }
            catch (Exception ex)
            {
                _loadError = "配置表读取失败：" + ex.Message;
            }
            if (_loadError != null && !_loggedError)
            {
                _loggedError = true;
                Log.Error($"[MechanicCodex] {_loadError}（改 tools/cell_tables/fgdata_hud.py 后重新生成）");
            }
        }

        private static void EnsureUnlocked()
        {
            if (_unlocked != null)
            {
                return;
            }
            EnsureEntries();
            _unlocked = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in LoadFile())
            {
                if (_byId.ContainsKey(id))
                {
                    _unlocked.Add(id);
                }
            }
            // 老玩家：本机设置里已经见过的钩子（本 Story 之前触发过的）补解锁对应条目，不写盘（下一次真正解锁时一起写）。
            foreach (MechanicCodexEntry e in _entries)
            {
                foreach (string h in e.Hooks)
                {
                    if (GameSettings.HasSeenGuidanceHook(h))
                    {
                        _unlocked.Add(e.Id);
                        break;
                    }
                }
            }
        }

        private static IEnumerable<string> LoadFile()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return Array.Empty<string>();
                }
                MechanicCodexSaveData data = JsonUtility.FromJson<MechanicCodexSaveData>(File.ReadAllText(path));
                if (data == null || data.Version != FileVersion)
                {
                    Log.Warning($"[MechanicCodex] 图鉴文件版本不认识（{data?.Version}），按空图鉴继续：{path}");
                    return Array.Empty<string>();
                }
                return data.Unlocked ?? Array.Empty<string>();
            }
            catch (Exception e)
            {
                Log.Warning($"[MechanicCodex] 图鉴文件读取失败，按空图鉴继续：{e.Message}");
                return Array.Empty<string>();
            }
        }

        private static void Save()
        {
            // 编辑模式下（自检 / 编辑器工具触发钩子）不写玩家本机的图鉴文件：只有游戏运行时或自检指定了临时路径才落盘。
            if (FilePathOverrideForTests == null && Application.isEditor && !Application.isPlaying)
            {
                return;
            }
            try
            {
                var list = new List<string>(_unlocked);
                list.Sort(StringComparer.Ordinal);
                var data = new MechanicCodexSaveData { Version = FileVersion, Unlocked = list.ToArray() };
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(path, JsonUtility.ToJson(data));
                SaveCount++;
            }
            catch (Exception e)
            {
                Log.Warning($"[MechanicCodex] 图鉴文件写入失败（本局内仍然解锁）：{e.Message}");
            }
        }

        private static string[] Split(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
            {
                return Array.Empty<string>();
            }
            var parts = new List<string>();
            foreach (string p in csv.Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0)
                {
                    parts.Add(t);
                }
            }
            return parts.ToArray();
        }
    }
}
