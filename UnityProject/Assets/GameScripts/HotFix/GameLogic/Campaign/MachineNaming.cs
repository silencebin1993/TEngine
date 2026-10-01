using System;
using System.Globalization;
using System.Text;
using GameLogic.Campaign.Grid;
using GameLogic.Core;
using GameLogic.Localization;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG4-ECO-07（FG04 FGR-ECO-041“机器改名：名字出现在 HUD、通知、离家报告和结算中”；FG13 FGU-17；承接 DEBT-FG1HUD01-01）：机器名字的唯一数据源。
    /// - 显示：<see cref="Long"/>（接入 HUD、命令栏、名册、详情：起了名 = “名字 #编号”，没起名 = “型号 #编号”，即 0.2 之前的 <c>SignalPresence.MachineLabel</c>）；
    ///   <see cref="Short"/>（通知、字幕、规则、工单、远征准备 / 返回、结算：起了名 = “名字 #编号”，没起名 = “#编号”）。编号永远跟在后面——重名也分得清。
    ///   两种格式只拼名字、型号与编号，不含任何语言的文字，所以不走文本键（中英文一致）。
    /// - 写入：<see cref="TryRename"/> / <see cref="TryResetName"/>（唯一写入口）。名字先去掉首尾空白；空、超过 roster.name_max_length 个字（按字符计，
    ///   中英文、表情都算 1 个）、含换行 / 制表符等控制字符或 &lt; &gt;（UI Toolkit 的富文本标记）都拒绝并给出原因。
    /// 每次调用 O(名字长度)，不分配缓存。
    /// </summary>
    public static class MachineNaming
    {
        /// <summary>名字变更版本号（界面据此重建）。</summary>
        public static int Revision { get; private set; } = 1;

        /// <summary>名字最多多少个字（fg.TbHomeTuning roster.name_max_length；缺行时 16）。</summary>
        public static int MaxLength => Math.Max(4, GridContent.TryGetTuning("roster.name_max_length", out float v) ? (int)Math.Round(v) : 16);

        public enum NameError
        {
            None = 0,
            Empty = 1,
            TooLong = 2,
            InvalidChar = 3,
        }

        /// <summary>按字符（文本元素）计数：“铁锤”= 2，“Hammer”= 6，一个表情 = 1。</summary>
        public static int Length(string s) => string.IsNullOrEmpty(s) ? 0 : new StringInfo(s).LengthInTextElements;

        /// <summary>校验玩家输入的名字。<paramref name="clean"/> = 去掉首尾空白后的名字。</summary>
        public static NameError Validate(string raw, out string clean)
        {
            clean = (raw ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                return NameError.Empty;
            }
            foreach (char c in clean)
            {
                if (char.IsControl(c) || c == '<' || c == '>')
                {
                    return NameError.InvalidChar;
                }
            }
            return Length(clean) > MaxLength ? NameError.TooLong : NameError.None;
        }

        /// <summary>校验失败的玩家可见原因（当前语言）；通过返回 null。</summary>
        public static string ErrorText(NameError error, string clean)
        {
            switch (error)
            {
                case NameError.Empty: return GameText.Get("roster.name.err_empty");
                case NameError.TooLong: return GameText.Format("roster.name.err_too_long", MaxLength, Length(clean));
                case NameError.InvalidChar: return GameText.Get("roster.name.err_invalid");
                default: return null;
            }
        }

        /// <summary>读档时的兜底：存档被手改过（控制字符、&lt; &gt;、超长）时去掉非法字符、截到上限；空 = 没起名。</summary>
        public static string Sanitize(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }
            var sb = new StringBuilder(stored.Length);
            foreach (char c in stored)
            {
                if (!char.IsControl(c) && c != '<' && c != '>')
                {
                    sb.Append(c);
                }
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0)
            {
                return null;
            }
            var info = new StringInfo(s);
            return info.LengthInTextElements > MaxLength ? info.SubstringByTextElements(0, MaxLength) : s;
        }

        /// <summary>改名。成功返回 true，<paramref name="message"/> 是结果说明；失败时是原因（名字不变）。</summary>
        public static bool TryRename(int logicId, string raw, out string message)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                message = GameText.Get("roster.err.unknown");
                return false;
            }
            NameError err = Validate(raw, out string clean);
            if (err != NameError.None)
            {
                message = ErrorText(err, clean);
                return false;
            }
            if (string.Equals(rec.CustomName, clean, StringComparison.Ordinal))
            {
                message = GameText.Get("roster.name.same");
                return false;
            }
            rec.CustomName = clean;
            Revision++;
            GuidanceHooks.Raise(GuidanceHooks.RosterFirstRename);
            message = GameText.Format("roster.name.ok", Long(rec));
            return true;
        }

        /// <summary>恢复默认名（型号 #编号）。</summary>
        public static bool TryResetName(int logicId, out string message)
        {
            if (!MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) || rec == null)
            {
                message = GameText.Get("roster.err.unknown");
                return false;
            }
            if (string.IsNullOrEmpty(rec.CustomName))
            {
                message = GameText.Get("roster.name.same");
                return false;
            }
            rec.CustomName = null;
            Revision++;
            message = GameText.Format("roster.name.ok_reset", Long(rec));
            return true;
        }

        public static bool HasCustomName(MachineRecord rec) => rec != null && !string.IsNullOrEmpty(rec.CustomName);

        /// <summary>型号（“ERC-003”）；没有底盘编号时为空串。</summary>
        public static string Model(MachineRecord rec) =>
            rec == null || string.IsNullOrEmpty(rec.ChassisId) ? string.Empty : rec.ChassisId.ToUpperInvariant().Replace('_', '-');

        private static string Number(MachineRecord rec) => "#" + rec.DisplayNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>长称呼：“铁锤 #5” / “ERC-003 #5”。找不到记录时“#LogicId”。</summary>
        public static string Long(MachineRecord rec)
        {
            if (rec == null)
            {
                return string.Empty;
            }
            if (HasCustomName(rec))
            {
                return rec.CustomName + " " + Number(rec);
            }
            string model = Model(rec);
            return model.Length == 0 ? Number(rec) : model + " " + Number(rec);
        }

        public static string Long(int logicId) =>
            MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null ? Long(rec) : "#" + logicId.ToString(CultureInfo.InvariantCulture);

        /// <summary>短称呼：“铁锤 #5” / “#5”。找不到记录时“#LogicId”。</summary>
        public static string Short(MachineRecord rec)
        {
            if (rec == null)
            {
                return string.Empty;
            }
            return HasCustomName(rec) ? rec.CustomName + " " + Number(rec) : Number(rec);
        }

        public static string Short(int logicId) =>
            MachineRegistry.TryGetRecord(logicId, out MachineRecord rec) && rec != null ? Short(rec) : "#" + logicId.ToString(CultureInfo.InvariantCulture);
    }
}
