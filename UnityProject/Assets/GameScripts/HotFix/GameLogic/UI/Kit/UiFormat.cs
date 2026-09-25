using System;
using System.Globalization;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;

namespace GameLogic.UI.Kit
{
    /// <summary>
    /// FG0-UX-01（FGR-UX-004）：全游戏统一的数字、速率、百分比格式。
    /// 大于等于阈值（fg.TbUiTuning fmt.big_number_threshold，默认 1 万）时：中文用“万”，英文用 k / M；速率统一“/分钟”。
    /// </summary>
    public static class UiFormat
    {
        public static string Number(double value)
        {
            double threshold = UiTuningValues.Get("fmt.big_number_threshold");
            double abs = Math.Abs(value);
            if (abs < threshold)
            {
                return Math.Abs(value - Math.Round(value)) < 1e-6
                    ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
                    : value.ToString("0.#", CultureInfo.InvariantCulture);
            }
            if (GameSettings.Language == GameLanguage.ZhCn)
            {
                return GameText.Format("fmt.wan", (value / 10000.0).ToString("0.#", CultureInfo.InvariantCulture));
            }
            if (abs < 1_000_000)
            {
                return GameText.Format("fmt.thousand", (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture));
            }
            return GameText.Format("fmt.million", (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture));
        }

        public static string PerMinute(double value) => GameText.Format("fmt.per_minute", Number(value));

        public static string Percent(double ratio) =>
            GameText.Format("fmt.percent", (ratio * 100.0).ToString("0", CultureInfo.InvariantCulture));

        public static string Seconds(double seconds) =>
            GameText.Format("fmt.seconds", seconds.ToString("0.#", CultureInfo.InvariantCulture));

        /// <summary>带正负号的增量（数值来源展开用：“+12/分钟”“-30%”）。</summary>
        public static string Signed(double value, string formatted) => (value > 0 ? "+" : string.Empty) + formatted;
    }
}
