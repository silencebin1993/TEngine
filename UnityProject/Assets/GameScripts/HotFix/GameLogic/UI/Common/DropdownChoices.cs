using System.Collections.Generic;
using UnityEngine.UIElements;

namespace GameLogic.UI.Common
{
    /// <summary>
    /// 数据驱动下拉框的共用填充规则。
    /// <see cref="DropdownField.index"/> 是按文本 <c>choices.IndexOf(value)</c> 反查的，两项同名时永远只能选中第一项，
    /// 所以选项文本必须唯一；空列表时显示占位文字并禁用，而不是留一个看不出状态的空白框。
    /// </summary>
    public static class DropdownChoices
    {
        /// <summary>实例 ID（如 <c>pchip_</c> + GUID）的末 4 位，供玩家区分同名实例。前缀对所有实例相同，取头部没有区分度。</summary>
        public static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "?";
            }
            return id.Length <= 4 ? id : id.Substring(id.Length - 4);
        }

        /// <summary>写入选项并保持当前选中项（仍存在时）；失效时回落到第一项，不触发 ValueChanged。</summary>
        public static void Apply(DropdownField dropdown, List<string> choices, string emptyText)
        {
            MakeUnique(choices);
            dropdown.choices = choices;
            if (choices.Count == 0)
            {
                dropdown.SetValueWithoutNotify(emptyText);
                dropdown.SetEnabled(false);
                return;
            }
            dropdown.SetEnabled(true);
            if (!choices.Contains(dropdown.value))
            {
                dropdown.SetValueWithoutNotify(choices[0]);
            }
        }

        /// <summary>下拉菜单按菜单路径语法解析选项：<c>/</c> 是子菜单分隔，<c> #x</c>/<c> %x</c>/<c> &amp;x</c> 是快捷键后缀，
        /// 会被直接剥掉（"聚焦镜（补印 #b76e）" 只显示成 "聚焦镜（补印"）。统一换成全角字符。</summary>
        private static string Sanitize(string label) =>
            label.Replace('/', '／').Replace('#', '＃').Replace('%', '％').Replace('&', '＆');

        private static void MakeUnique(List<string> choices)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < choices.Count; i++)
            {
                choices[i] = Sanitize(choices[i]);
                string label = choices[i];
                int suffix = 2;
                while (!seen.Add(label))
                {
                    label = $"{choices[i]} ({suffix++})";
                }
                choices[i] = label;
            }
        }
    }
}
