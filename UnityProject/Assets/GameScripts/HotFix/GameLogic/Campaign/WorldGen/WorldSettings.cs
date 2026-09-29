using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GameConfig.fg;
using GameLogic.Localization;

namespace GameLogic.Campaign.WorldGen
{
    /// <summary>
    /// FG3-GEN-01（FGR-GEN-070）：一个世界的设置，已按（生成器版本, 设置 ID）解析成生成要用的数值。
    ///
    /// - 生成器 v1：设置 ID 是 fg.TbWorldPreset 里的整套预设（只有 default）。
    /// - 生成器 v2 起：五个分项各自选档（资源丰度 / 敌方据点密度 / 污染强度 / 领地距离 / 起始区），数值按版本行 settingSet 引用的
    ///   fg.TbWorldSettingAxis 取。设置 ID 是“分项代码”：全部标准档 = <c>default</c>，否则 <c>R{资源}O{据点}P{污染}D{领地}S{起始区}</c>
    ///   （每个字母后一位档号）。存档只记这个 ID，数值永远按存档记录的版本取（FGR-GEN-061）。
    /// 解析是纯函数：同一（版本, ID）永远得到同一组数值（浮点表值在生成内核里按 round(x × 65536) 转定点）。
    /// </summary>
    public sealed class WorldSettings
    {
        public const string AxisResource = "resource";
        public const string AxisOutposts = "outposts";
        public const string AxisPollution = "pollution";
        public const string AxisDistance = "distance";
        public const string AxisStart = "start";

        /// <summary>分项（顺序 = 分项代码里的字母顺序 = 分享短码里的混合进制顺序）。</summary>
        public static readonly string[] Axes = { AxisResource, AxisOutposts, AxisPollution, AxisDistance, AxisStart };
        private static readonly char[] AxisLetters = { 'R', 'O', 'P', 'D', 'S' };

        /// <summary>分项名文本键（新游戏设置每一排的标题）。完整键字面量写在这里，文本键扫描能核对到。</summary>
        public static string AxisNameKey(int axisIndex)
        {
            switch (axisIndex)
            {
                case 0: return "world.setting.axis.resource";
                case 1: return "world.setting.axis.outposts";
                case 2: return "world.setting.axis.pollution";
                case 3: return "world.setting.axis.distance";
                default: return "world.setting.axis.start";
            }
        }

        /// <summary>分项简称文本键（设置摘要“资源 低 · 据点 高”）。</summary>
        public static string AxisShortKey(int axisIndex)
        {
            switch (axisIndex)
            {
                case 0: return "world.setting.short.resource";
                case 1: return "world.setting.short.outposts";
                case 2: return "world.setting.short.pollution";
                case 3: return "world.setting.short.distance";
                default: return "world.setting.short.start";
            }
        }

        public int Version { get; private set; }
        /// <summary>规范化后的设置 ID（存档 WorldGenState.WorldSettingsId）。</summary>
        public string Id { get; private set; }
        /// <summary>true = v2 分项设置；false = v1 整套预设。</summary>
        public bool IsAxisBased { get; private set; }
        /// <summary>分项设置的档（与 <see cref="Axes"/> 同序）；v1 为 null。</summary>
        public int[] Levels { get; private set; }

        public float ResourceAbundance { get; private set; } = 1f;
        public float PollutionIntensity { get; private set; } = 1f;
        public float TerritoryDistanceScale { get; private set; } = 1f;
        /// <summary>敌方据点密度倍率（v2；v1 = 1）。</summary>
        public float OutpostDensity { get; private set; } = 1f;
        /// <summary>起始区第 1、3 级半径的缩放（v2 宽松 = 1.5；v1 = 1）。</summary>
        public float StartScale { get; private set; } = 1f;
        /// <summary>v1 的“宽松起始区”（保护矩形 ×1.5 的快照变体）。v2 不用它。</summary>
        public bool LegacyRelaxed { get; private set; }

        /// <summary>v1 预设行（只给 v1 用；显示名）。</summary>
        public WorldPreset Preset { get; private set; }

        public static WorldSettings FromPreset(WorldPreset p, int version)
        {
            if (p == null)
            {
                throw new ArgumentNullException(nameof(p));
            }
            return new WorldSettings
            {
                Version = version,
                Id = p.Id,
                IsAxisBased = false,
                ResourceAbundance = p.ResourceAbundance,
                PollutionIntensity = p.PollutionIntensity,
                TerritoryDistanceScale = p.TerritoryDistanceScale,
                LegacyRelaxed = p.StartZone == "relaxed",
                Preset = p,
            };
        }

        /// <summary>按（版本, 设置 ID）解析；ID 不认识 / 版本不存在时返回 false 并给出原因。</summary>
        public static bool TryResolve(int version, string id, out WorldSettings settings, out string error)
        {
            settings = null;
            error = null;
            if (!WorldGenContent.TryGetVersion(version, out WorldGenVersion v))
            {
                error = $"生成器版本表缺少 v{version}";
                return false;
            }
            if (!WorldGenContent.HasSet(v.SettingSet))
            {
                if (WorldGenContent.TryGetPreset(version, id, out WorldPreset p))
                {
                    settings = FromPreset(p, version);
                    return true;
                }
                error = $"生成器 v{version} 的世界设置集合里没有 {id ?? "null"}";
                return false;
            }
            if (!TryParseLevels(v, id, out int[] levels))
            {
                error = $"生成器 v{version} 不认识的分项设置代码 {id ?? "null"}";
                return false;
            }
            settings = FromLevels(v, levels);
            return true;
        }

        public static WorldSettings Resolve(int version, string id)
        {
            if (!TryResolve(version, id, out WorldSettings s, out string error))
            {
                throw new KeyNotFoundException(error);
            }
            return s;
        }

        /// <summary>v2：按每个分项的档号取数值。<paramref name="levels"/> 与 <see cref="Axes"/> 同序。</summary>
        public static WorldSettings FromLevels(WorldGenVersion v, int[] levels)
        {
            var s = new WorldSettings { Version = v.Version, IsAxisBased = true, Levels = (int[])levels.Clone() };
            for (int i = 0; i < Axes.Length; i++)
            {
                float value = AxisValue(v, Axes[i], levels[i]);
                switch (Axes[i])
                {
                    case AxisResource: s.ResourceAbundance = value; break;
                    case AxisOutposts: s.OutpostDensity = value; break;
                    case AxisPollution: s.PollutionIntensity = value; break;
                    case AxisDistance: s.TerritoryDistanceScale = value; break;
                    case AxisStart: s.StartScale = value; break;
                }
            }
            s.Id = CodeOf(v, levels);
            return s;
        }

        private static float AxisValue(WorldGenVersion v, string axis, int level)
        {
            foreach (WorldSettingAxis a in WorldGenContent.AxisLevels(v, axis))
            {
                if (a.Level == level)
                {
                    return a.Value;
                }
            }
            throw new KeyNotFoundException($"分项世界设置 {v.SettingSet}.{axis} 没有第 {level} 档");
        }

        /// <summary>每个分项的标准档（isDefault=1）。</summary>
        public static int[] DefaultLevels(WorldGenVersion v)
        {
            var levels = new int[Axes.Length];
            for (int i = 0; i < Axes.Length; i++)
            {
                foreach (WorldSettingAxis a in WorldGenContent.AxisLevels(v, Axes[i]))
                {
                    if (a.IsDefault == 1)
                    {
                        levels[i] = a.Level;
                    }
                }
            }
            return levels;
        }

        /// <summary>每个分项有几档。</summary>
        public static int LevelCount(WorldGenVersion v, int axisIndex) => WorldGenContent.AxisLevels(v, Axes[axisIndex]).Count;

        /// <summary>规范化的分项代码：全部标准档 = default。</summary>
        public static string CodeOf(WorldGenVersion v, int[] levels)
        {
            int[] d = DefaultLevels(v);
            bool all = true;
            for (int i = 0; i < Axes.Length; i++)
            {
                all &= levels[i] == d[i];
            }
            if (all)
            {
                return WorldGenContent.DefaultPresetId;
            }
            var sb = new StringBuilder(Axes.Length * 2);
            for (int i = 0; i < Axes.Length; i++)
            {
                sb.Append(AxisLetters[i]).Append(levels[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>解析分项代码（default 或 R?O?P?D?S?），档号必须在该版本的档范围内。</summary>
        public static bool TryParseLevels(WorldGenVersion v, string id, out int[] levels)
        {
            levels = null;
            if (v == null || !WorldGenContent.HasSet(v.SettingSet) || string.IsNullOrEmpty(id))
            {
                return false;
            }
            if (id == WorldGenContent.DefaultPresetId)
            {
                levels = DefaultLevels(v);
                return true;
            }
            if (id.Length != Axes.Length * 2)
            {
                return false;
            }
            var parsed = new int[Axes.Length];
            for (int i = 0; i < Axes.Length; i++)
            {
                char letter = id[i * 2];
                char digit = id[i * 2 + 1];
                if (letter != AxisLetters[i] || digit < '0' || digit > '9')
                {
                    return false;
                }
                parsed[i] = digit - '0';
                if (parsed[i] >= LevelCount(v, i))
                {
                    return false;
                }
            }
            levels = parsed;
            return true;
        }

        /// <summary>显示名：“标准”，或非标准分项逐个列出（“资源 高 · 据点 高 · 污染 高 · 领地 近”）；v1 = 预设名。</summary>
        public string DisplayName()
        {
            if (!IsAxisBased)
            {
                return Preset != null ? GameText.Get(Preset.NameKey) : Id;
            }
            WorldGenVersion v = WorldGenContent.Version(Version);
            int[] d = DefaultLevels(v);
            var parts = new List<string>();
            for (int i = 0; i < Axes.Length; i++)
            {
                if (Levels[i] == d[i])
                {
                    continue;
                }
                parts.Add(GameText.Format("world.setting.pair", GameText.Get(AxisShortKey(i)), LevelName(v, i, Levels[i])));
            }
            return parts.Count == 0 ? GameText.Get("world.setting.all_standard") : string.Join(GameText.Get("world.setting.sep"), parts);
        }

        /// <summary>某分项某档的显示名（当前语言）。</summary>
        public static string LevelName(WorldGenVersion v, int axisIndex, int level)
        {
            foreach (WorldSettingAxis a in WorldGenContent.AxisLevels(v, Axes[axisIndex]))
            {
                if (a.Level == level)
                {
                    return GameText.Get(a.NameKey);
                }
            }
            return level.ToString(CultureInfo.InvariantCulture);
        }

        // ── 种子输入（FGR-GEN-001）──────────────────────────────────────────────────

        /// <summary>
        /// 玩家输入 → 种子：整数（可带负号，在 int 范围内）原样使用；其余文字按 UTF-8 的 FNV-1a 32 位哈希换算（与平台无关，
        /// 同一段文字永远同一个种子）。空白返回 false。
        /// </summary>
        public static bool TryParseSeed(string text, out int seed, out bool fromText)
        {
            seed = 0;
            fromText = false;
            string t = text?.Trim();
            if (string.IsNullOrEmpty(t))
            {
                return false;
            }
            if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
            {
                return true;
            }
            uint h = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(t))
            {
                h ^= b;
                h = unchecked(h * 16777619u);
            }
            seed = unchecked((int)h);
            fromText = true;
            return true;
        }

        // ── 分享短码（FGR-GEN-071）──────────────────────────────────────────────────
        //
        // BGR-XXXX-XXXX-XXXX：12 位 Crockford Base32（去掉 I L O U，60 位）= 种子 32 位 + 生成器版本 6 位 + 设置序号 12 位 + 校验 10 位。
        // 设置序号：v2 起是各分项档号的混合进制（资源 + 据点×3 + ……，按该版本各分项的档数）；v1 是预设在集合里的序号。
        // 导入时版本、设置按短码里的版本解释，所以同一段短码在任何更新的游戏里都生成完全相同的世界（老版本生成器路径保留）。

        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        public const string SharePrefix = "BGR";

        public enum ShareError
        {
            None,
            BadFormat,
            BadChecksum,
            Newer,
            BadSettings,
        }

        /// <summary>设置 → 序号（v2 混合进制；v1 预设序号）。</summary>
        public static int SettingsIndex(WorldSettings s)
        {
            WorldGenVersion v = WorldGenContent.Version(s.Version);
            if (!s.IsAxisBased)
            {
                List<WorldPreset> presets = WorldGenContent.PresetsFor(s.Version);
                for (int i = 0; i < presets.Count; i++)
                {
                    if (presets[i].Id == s.Id)
                    {
                        return i;
                    }
                }
                return 0;
            }
            int index = 0;
            int mul = 1;
            for (int i = 0; i < Axes.Length; i++)
            {
                index += s.Levels[i] * mul;
                mul *= Math.Max(1, LevelCount(v, i));
            }
            return index;
        }

        private static bool TryFromIndex(int version, int index, out WorldSettings s)
        {
            s = null;
            if (!WorldGenContent.TryGetVersion(version, out WorldGenVersion v))
            {
                return false;
            }
            if (!WorldGenContent.HasSet(v.SettingSet))
            {
                List<WorldPreset> presets = WorldGenContent.PresetsFor(version);
                if (index < 0 || index >= presets.Count)
                {
                    return false;
                }
                s = FromPreset(presets[index], version);
                return true;
            }
            var levels = new int[Axes.Length];
            int rest = index;
            for (int i = 0; i < Axes.Length; i++)
            {
                int count = Math.Max(1, LevelCount(v, i));
                levels[i] = rest % count;
                rest /= count;
            }
            if (rest != 0)
            {
                return false;
            }
            s = FromLevels(v, levels);
            return true;
        }

        public static string EncodeShareCode(int seed, WorldSettings s)
        {
            ulong payload = ((ulong)(uint)seed << 18) | ((ulong)(s.Version & 0x3F) << 12) | (ulong)(SettingsIndex(s) & 0xFFF);
            ulong code = (payload << 10) | Checksum(payload);
            var chars = new char[12];
            for (int i = 11; i >= 0; i--)
            {
                chars[i] = Alphabet[(int)(code & 31UL)];
                code >>= 5;
            }
            var sb = new StringBuilder(SharePrefix.Length + 15);
            sb.Append(SharePrefix);
            for (int i = 0; i < 12; i++)
            {
                if (i % 4 == 0)
                {
                    sb.Append('-');
                }
                sb.Append(chars[i]);
            }
            return sb.ToString();
        }

        /// <summary>短码 → 种子与设置。容错：不分大小写、忽略空格与短横、可省略 BGR 前缀、O 当 0、I / L 当 1。</summary>
        public static ShareError TryDecodeShareCode(string text, out int seed, out WorldSettings settings, out int version)
        {
            seed = 0;
            settings = null;
            version = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return ShareError.BadFormat;
            }
            string t = text.Trim().ToUpperInvariant().Replace(" ", string.Empty).Replace("-", string.Empty);
            if (t.StartsWith(SharePrefix, StringComparison.Ordinal))
            {
                t = t.Substring(SharePrefix.Length);
            }
            if (t.Length != 12)
            {
                return ShareError.BadFormat;
            }
            ulong code = 0;
            foreach (char raw in t)
            {
                char c = raw == 'O' ? '0' : (raw == 'I' || raw == 'L') ? '1' : raw;
                int v = Alphabet.IndexOf(c);
                if (v < 0)
                {
                    return ShareError.BadFormat;
                }
                code = (code << 5) | (ulong)v;
            }
            ulong payload = code >> 10;
            if ((code & 0x3FFUL) != Checksum(payload))
            {
                return ShareError.BadChecksum;
            }
            seed = unchecked((int)(uint)(payload >> 18));
            version = (int)((payload >> 12) & 0x3F);
            int index = (int)(payload & 0xFFF);
            if (version > WorldGenVersions.Current)
            {
                return ShareError.Newer;
            }
            if (version < 1 || !TryFromIndex(version, index, out settings))
            {
                return ShareError.BadSettings;
            }
            return ShareError.None;
        }

        private static ulong Checksum(ulong payload)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < 7; i++)
            {
                h ^= (payload >> (i * 8)) & 0xFF;
                h = unchecked(h * 1099511628211UL);
            }
            return (h ^ (h >> 20) ^ (h >> 40)) & 0x3FFUL;
        }
    }
}
