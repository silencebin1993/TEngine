using System;
using System.Collections.Generic;
using System.Globalization;
using BinGames.Sim.Combat;
using GameLogic.Localization;
using TEngine;

namespace GameLogic.Campaign.Emergence
{
    /// <summary>
    /// EMG2-MAT-01（FG19 FGR-EMG-080；FG19B 第 1 节）：把 fg.TbMatter 17 行编成内核物质数值表（下标 = 物质码）。只在地点载入 / 表重载时编译（O(17)），不按帧。
    /// 内核只认物质码与数值，不认物质名（FGR-EMG-013）；名称 / 悬停文字由本类按物质码查文本键给热更层界面用。
    /// </summary>
    public static class EmgMatterCatalog
    {
        private static readonly string[] DecayModes = { "evaporate", "keep", "dilute", "burn", "absorb", "cool" };

        public sealed class Compiled
        {
            public readonly EmgMatter[] Rows = new EmgMatter[EmgMatterConst.Count + 1];
            public readonly string[] NameKeys = new string[EmgMatterConst.Count + 1];
            public readonly string[] NextKeys = new string[EmgMatterConst.Count + 1];
            public readonly List<string> Problems = new List<string>();
            public bool Ok => Problems.Count == 0;
        }

        private static Compiled _current;

        public static Compiled Current => _current ??= Compile(Load());

        public static void Reload() => _current = null;

        private static IReadOnlyList<GameConfig.fg.Matter> Load()
        {
            try
            {
                return ConfigSystem.Instance.Tables?.TbMatter?.DataList;
            }
            catch (Exception e)
            {
                Log.Error("[EmgMatterCatalog] 读取 fg.TbMatter 失败：" + e.Message);
                return null;
            }
        }

        public static Compiled Compile(IReadOnlyList<GameConfig.fg.Matter> rows)
        {
            var c = new Compiled();
            if (rows == null || rows.Count != EmgMatterConst.Count)
            {
                c.Problems.Add($"fg.TbMatter 应为 {EmgMatterConst.Count} 行（实际 {rows?.Count ?? 0}）");
                return c;
            }
            foreach (GameConfig.fg.Matter r in rows)
            {
                if (r == null || r.Code < 1 || r.Code > EmgMatterConst.Count || EmgMatterConst.MatterOfPort(EmgMatterConst.PortBase + r.Code) == 0)
                {
                    c.Problems.Add($"fg.TbMatter 物质码 {r?.Code} 越界");
                    continue;
                }
                int decay = Array.IndexOf(DecayModes, r.DecayMode);
                byte emit = 0;
                if (!string.IsNullOrEmpty(r.Emit) && r.Emit != "none")
                {
                    int ei = PortCodeOf(r.Emit);
                    emit = EmgMatterConst.MatterOfPort(ei);
                    if (emit == 0)
                    {
                        c.Problems.Add($"{r.Id} emit={r.Emit} 不是物质端口");
                    }
                }
                if (decay < 0)
                {
                    c.Problems.Add($"{r.Id} decayMode={r.DecayMode} 不认识");
                }
                c.Rows[r.Code] = new EmgMatter
                {
                    Flammable = r.Flammable, Conductive = r.Conductive, Cooling = r.Cooling, Corrosive = r.Corrosive, Viscous = r.Viscous, Obscure = r.Obscure,
                    Slippery = r.Slippery, Magnetic = r.Magnetic, Reflective = r.Reflective, Life = r.Lifetime, LifePerAmount = r.LifePerAmount, LifeMax = r.LifeMax,
                    Spread = r.Spread, CoatSeconds = r.CoatSeconds, TempInit = r.TempInit, Hp = r.Hp, MergeOverlap = r.MergeOverlap, ContactDps = r.ContactDps,
                    ContactSlow = r.ContactSlow, EmitEvery = r.EmitEvery, Color = ParseColor(r.Color), Quota = (short)Math.Max(0, r.Quota), CoatBit = (sbyte)r.CoatBit,
                    Layer = (byte)r.Layer, Solid = (byte)(r.Solid != 0 ? 1 : 0), FixedTemp = (byte)(r.FixedTemp != 0 ? 1 : 0), Decay = (byte)Math.Max(0, decay), Emit = emit,
                    Pattern = (byte)Math.Max(0, r.Pattern), StructDps = Math.Max(0f, r.StructDps),
                };
                c.NameKeys[r.Code] = r.NameKey;
                c.NextKeys[r.Code] = r.NextKey;
            }
            for (int m = 1; m <= EmgMatterConst.Count; m++)
            {
                if (!c.Rows[m].Valid)
                {
                    c.Problems.Add($"fg.TbMatter 缺物质码 {m}");
                }
            }
            return c;
        }

        /// <summary>EMG2-MAT-02：物质端口 ID（mat.*）→ 物质码（不是物质端口 / 没登记 = 0）。</summary>
        public static byte CodeOf(string portId) => string.IsNullOrEmpty(portId) || portId == "none" ? (byte)0 : EmgMatterConst.MatterOfPort(PortCodeOf(portId));

        /// <summary>物质端口码（fg.TbChannelPort）；按“码 = 400 + 物质码”的约定与端口表互证（不一致记问题）。</summary>
        private static int PortCodeOf(string portId)
        {
            EmgCompiled c = EmgCatalog.Current;
            return c.PortIndexById.TryGetValue(portId, out int pi) ? c.Ports[pi].Code : 0;
        }

        private static int ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return CombatConst.EffectNeutralColor;
            }
            string h = hex.TrimStart('#');
            return int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : CombatConst.EffectNeutralColor;
        }

        public static string NameOf(int code) => code >= 1 && code <= EmgMatterConst.Count && Current.NameKeys[code] != null ? GameText.Get(Current.NameKeys[code]) : "?";

        public static string NextOf(int code) => code >= 1 && code <= EmgMatterConst.Count && Current.NextKeys[code] != null ? GameText.Get(Current.NextKeys[code]) : string.Empty;
    }
}
