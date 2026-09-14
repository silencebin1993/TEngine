using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace GameLogic.MetabolicSlice.Lineage
{
    /// <summary>M3-03 实施第 5 条：稳定装配签名。用 SHA-256 而非 <see cref="string.GetHashCode"/>——
    /// .NET 的默认字符串哈希按进程加盐，同一输入在不同进程/重启后会得到不同值，不满足
    /// "相同配方生成相同签名"验收。规范化字符串只由 OrganelleId + 有序 GeneIds 拼接，顺序敏感
    /// （基因是有序节点，调换顺序应视为不同配方）。</summary>
    public static class PhenotypeTemplateSignature
    {
        public static string Compute(string organelleId, IReadOnlyList<string> geneIds)
        {
            var sb = new StringBuilder();
            sb.Append(organelleId ?? string.Empty);
            sb.Append('|');
            for (int i = 0; i < geneIds.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }
                sb.Append(geneIds[i]);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    hex.Append(hash[i].ToString("x2"));
                }
                return hex.ToString();
            }
        }
    }
}
