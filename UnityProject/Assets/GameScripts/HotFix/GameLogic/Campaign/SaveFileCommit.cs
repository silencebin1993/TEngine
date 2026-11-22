using System;
using System.IO;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG15-SYS-01（FGR-SYS-003 / FGT-SYS-001）：存档落盘里会被“断电”打断的那一段——先写临时文件（直写磁盘），再一次系统调用原子替换主档、
    /// 旧主档同时转存为备份。只用 System / System.IO：游戏（<see cref="CampaignSaveService"/>）与断电式自检的外部子进程编译的是同一份源码
    /// （子进程用 pwsh 7 的 Add-Type 直接编译本文件，循环写档时被父进程强制结束，见 FgSaveRobustSelfCheck A 段）。
    /// </summary>
    public static class SaveFileCommit
    {
        /// <summary>整份写入临时文件并刷到磁盘。<paramref name="tearForTests"/> = 只写一半就返回 false（进程内模拟崩溃用）。</summary>
        public static bool WriteTemp(string tempPath, byte[] bytes, bool tearForTests = false)
        {
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (tearForTests)
                {
                    fs.Write(bytes, 0, bytes.Length / 2);
                    fs.Flush(true);
                    return false;
                }
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            return true;
        }

        /// <summary>临时文件 → 主档：主档存在时 File.Replace（单次系统调用；<paramref name="bakPath"/> 非 null 时旧主档原子转存为备份），否则 File.Move。</summary>
        public static void Replace(string tempPath, string mainPath, string bakPath)
        {
            if (File.Exists(mainPath))
            {
                File.Replace(tempPath, mainPath, bakPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, mainPath);
            }
        }

        /// <summary>写临时文件 + 替换（断电式子进程循环调用的就是这一段）。</summary>
        public static void WriteAtomic(string mainPath, byte[] bytes, bool keepBackup)
        {
            string temp = mainPath + ".tmp";
            WriteTemp(temp, bytes);
            Replace(temp, mainPath, keepBackup ? mainPath + ".bak" : null);
        }
    }
}
