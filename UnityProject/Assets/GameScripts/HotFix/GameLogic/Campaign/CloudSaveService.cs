using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG15-SYS-01（FGR-SYS-006）：云存档的存储端。Steam Remote Storage 的实现随 Steam 集成（FG15-SYS-04，需要用户的 Steamworks 账号）接入；
    /// 本卡用 <see cref="LocalFolderCloudProvider"/>（本机另一个目录当“云端”）验证同步与冲突处理。离线 / 没有提供者 = 云存档不可用，游戏照常。
    /// </summary>
    public interface ICloudSaveProvider
    {
        bool Available { get; }
        string[] List();
        /// <summary>整份读取；不存在返回 null。</summary>
        byte[] Read(string name);
        /// <summary>只读开头 <paramref name="maxBytes"/> 字节（列表 / 冲突对比只要头部）；不存在返回 null。</summary>
        byte[] ReadHead(string name, int maxBytes);
        bool Write(string name, byte[] data);
        bool Delete(string name);
    }

    /// <summary>本机目录模拟的云端（自检与开发期用；也可作“同步到自定义目录”）。</summary>
    public sealed class LocalFolderCloudProvider : ICloudSaveProvider
    {
        public readonly string Root;
        public bool Online = true;

        public LocalFolderCloudProvider(string root)
        {
            Root = root;
        }

        public bool Available => Online;

        public string[] List()
        {
            if (!Online || !Directory.Exists(Root))
            {
                return Array.Empty<string>();
            }
            return Directory.GetFiles(Root).Select(Path.GetFileName).ToArray();
        }

        public byte[] Read(string name)
        {
            string p = Path.Combine(Root, name);
            return Online && File.Exists(p) ? File.ReadAllBytes(p) : null;
        }

        public byte[] ReadHead(string name, int maxBytes)
        {
            string p = Path.Combine(Root, name);
            if (!Online || !File.Exists(p))
            {
                return null;
            }
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[(int)Math.Min(maxBytes, fs.Length)];
                int n = fs.Read(buf, 0, buf.Length);
                return n == buf.Length ? buf : buf.Take(n).ToArray();
            }
        }

        public bool Write(string name, byte[] data)
        {
            if (!Online)
            {
                return false;
            }
            Directory.CreateDirectory(Root);
            SaveFileCommit.WriteAtomic(Path.Combine(Root, name), data, keepBackup: false);
            return true;
        }

        public bool Delete(string name)
        {
            string p = Path.Combine(Root, name);
            if (!Online || !File.Exists(p))
            {
                return false;
            }
            File.Delete(p);
            return true;
        }
    }

    /// <summary>冲突对话框里一边（本机 / 云端）的存档摘要：存档时间与游戏进度。</summary>
    public sealed class CloudSaveSide
    {
        public string Checksum;
        public string WrittenAtUtc;
        public int Act;
        public int Day;
        public float PlaySeconds;
        public double RealPlaySeconds;
        public bool Readable;
    }

    public sealed class CloudSaveConflict
    {
        public string FileName;
        public int Slot;
        public CloudSaveSide Local;
        public CloudSaveSide Cloud;
    }

    public struct CloudSyncReport
    {
        public bool Ran;
        public int Uploaded;
        public int Downloaded;
        public int Deleted;
        public int Conflicts;
        public int Failed;
    }

    /// <summary>
    /// FG15-SYS-01（FGR-SYS-006 云存档）：主菜单打开时（与退出游戏时）把存档目录里的战役槽与副本跟云端对账。按“上次同步时的校验和”判断哪边改过：
    /// 只有本机改过 → 上传；只有云端改过 → 下载（主档旧版照常转为备份）；两边都改过且不同 → 冲突，主菜单弹出对话框，显示两边的存档时间和游戏进度，由玩家选保留本机还是云端。
    /// 永不自动删除：落选的一份另存为 keep-cloud / keep-local 保留文件；云端被别处删掉的存档不删本机（重新上传）；只有玩家在本机亲手删除的存档才同步删掉云端那份。
    /// 备份（.bak）、临时文件、保留文件不上云。
    /// </summary>
    public static class CloudSaveService
    {
        /// <summary>云存档提供者（null = 不可用）。Steam 版由 FG15-SYS-04 注入。</summary>
        public static ICloudSaveProvider Provider;

        public static readonly List<CloudSaveConflict> Conflicts = new List<CloudSaveConflict>();
        public static CloudSyncReport LastReport { get; private set; }

        public static bool Available => Provider != null && Provider.Available;

        private const string RecordFile = "cloud_sync.json";
        private const int HeadBytes = 64 * 1024;
        private static readonly Regex SyncedName = new Regex(@"^campaign_slot(\d+)\.(json|auto[1-3]\.json|quick\.json)$", RegexOptions.CultureInvariant);
        private static readonly Regex ChecksumRe = new Regex("\"Checksum\"\\s*:\\s*\"([0-9a-fA-F]+)\"", RegexOptions.CultureInvariant);

        [Serializable]
        private sealed class SyncRecord
        {
            public List<SyncEntry> Entries = new List<SyncEntry>();
        }

        [Serializable]
        private sealed class SyncEntry
        {
            public string Name;
            public string Checksum;
        }

        public static bool IsSyncedName(string name) => name != null && SyncedName.IsMatch(name);

        /// <summary>对账一次（主菜单打开时调）。云存档不可用时什么也不做（Ran = false）。冲突留在 <see cref="Conflicts"/> 等玩家选。</summary>
        public static CloudSyncReport Sync()
        {
            Conflicts.Clear();
            var report = new CloudSyncReport();
            if (!Available)
            {
                LastReport = report;
                return report;
            }
            CampaignSaveService.WaitForPendingWrites();
            report.Ran = true;
            string dir = CampaignSaveService.SaveDirectory;
            SyncRecord record = LoadRecord(dir);
            var synced = record.Entries.Where(e => e != null && e.Name != null).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Last().Checksum);
            var names = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir, "campaign_slot*"))
                    {
                        string n = Path.GetFileName(f);
                        if (IsSyncedName(n))
                        {
                            names.Add(n);
                        }
                    }
                }
                foreach (string n in Provider.List())
                {
                    if (IsSyncedName(n))
                    {
                        names.Add(n);
                    }
                }
                names.UnionWith(synced.Keys.Where(IsSyncedName));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CloudSaveService] 列出存档失败：{e.Message}");
                report.Failed++;
                LastReport = report;
                return report;
            }

            foreach (string name in names)
            {
                try
                {
                    string localPath = Path.Combine(dir, name);
                    byte[] localHead = File.Exists(localPath) ? ReadHead(localPath) : null;
                    byte[] cloudHead = Provider.ReadHead(name, HeadBytes);
                    string l = ChecksumOf(localHead);
                    string c = ChecksumOf(cloudHead);
                    synced.TryGetValue(name, out string s);
                    if (l == c)
                    {
                        Set(synced, name, l);
                        continue;
                    }
                    bool localChanged = l != s;
                    bool cloudChanged = c != s;
                    if (localChanged && !cloudChanged)
                    {
                        if (l != null)
                        {
                            if (Provider.Write(name, File.ReadAllBytes(localPath)))
                            {
                                report.Uploaded++;
                                Set(synced, name, l);
                            }
                            else
                            {
                                report.Failed++;
                            }
                        }
                        else if (Provider.Delete(name))
                        {
                            report.Deleted++; // 玩家在本机亲手删掉的存档（上次同步时还在）
                            Set(synced, name, null);
                        }
                        continue;
                    }
                    if (!localChanged && cloudChanged)
                    {
                        if (c != null)
                        {
                            WriteLocal(localPath, Provider.Read(name));
                            report.Downloaded++;
                            Set(synced, name, c);
                        }
                        else if (Provider.Write(name, File.ReadAllBytes(localPath)))
                        {
                            report.Uploaded++; // 云端那份被别处删了：不删本机，重新上传
                            Set(synced, name, l);
                        }
                        continue;
                    }
                    if (l == null || c == null)
                    {
                        // 两边都改过、其中一边已不存在：保留存在的那份（不删除），按单边改动处理
                        if (l != null && Provider.Write(name, File.ReadAllBytes(localPath)))
                        {
                            report.Uploaded++;
                            Set(synced, name, l);
                        }
                        else if (c != null)
                        {
                            WriteLocal(localPath, Provider.Read(name));
                            report.Downloaded++;
                            Set(synced, name, c);
                        }
                        continue;
                    }
                    Match m = SyncedName.Match(name);
                    Conflicts.Add(new CloudSaveConflict
                    {
                        FileName = name,
                        Slot = SlotOf(name, int.Parse(m.Groups[1].Value)),
                        Local = SideOf(localHead),
                        Cloud = SideOf(cloudHead),
                    });
                }
                catch (Exception e)
                {
                    TEngine.Log.Warning($"[CloudSaveService] 同步 {name} 失败：{e.Message}");
                    report.Failed++;
                }
            }
            report.Conflicts = Conflicts.Count;
            SaveRecord(dir, synced);
            LastReport = report;
            TEngine.Log.Info($"[CloudSaveService] 云存档对账：上传 {report.Uploaded}、下载 {report.Downloaded}、删除 {report.Deleted}、冲突 {report.Conflicts}、失败 {report.Failed}");
            return report;
        }

        /// <summary>玩家在冲突对话框里选了一边。落选的一份另存为保留文件（keep-cloud / keep-local），永不删除。</summary>
        public static bool Resolve(CloudSaveConflict conflict, bool keepLocal)
        {
            if (conflict == null || !Available)
            {
                return false;
            }
            string dir = CampaignSaveService.SaveDirectory;
            string localPath = Path.Combine(dir, conflict.FileName);
            try
            {
                byte[] cloud = Provider.Read(conflict.FileName);
                byte[] local = File.Exists(localPath) ? File.ReadAllBytes(localPath) : null;
                if (cloud == null || local == null)
                {
                    return false;
                }
                if (keepLocal)
                {
                    File.WriteAllBytes(CampaignSaveService.KeepPath(conflict.Slot, "cloud"), cloud);
                    if (!Provider.Write(conflict.FileName, local))
                    {
                        return false;
                    }
                }
                else
                {
                    File.Copy(localPath, CampaignSaveService.KeepPath(conflict.Slot, "local"), overwrite: false);
                    WriteLocal(localPath, cloud);
                }
                SyncRecord record = LoadRecord(dir);
                var synced = record.Entries.Where(e => e != null && e.Name != null).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Last().Checksum);
                Set(synced, conflict.FileName, keepLocal ? conflict.Local.Checksum : conflict.Cloud.Checksum);
                SaveRecord(dir, synced);
                Conflicts.Remove(conflict);
                return true;
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CloudSaveService] 处理冲突 {conflict.FileName} 失败：{e.Message}");
                return false;
            }
        }

        private static int SlotOf(string name, int home)
        {
            if (name.EndsWith(".quick.json", StringComparison.Ordinal))
            {
                return CampaignSaveService.QuickSlot(home);
            }
            Match m = Regex.Match(name, @"\.auto([1-3])\.json$");
            return m.Success ? CampaignSaveService.AutoSlot(home, int.Parse(m.Groups[1].Value) - 1) : home;
        }

        private static void WriteLocal(string path, byte[] data)
        {
            if (data == null)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 战役槽主档：旧版照常转为备份；副本没有备份（轮换语义）。
            bool main = !path.EndsWith(".quick.json", StringComparison.Ordinal) && !Regex.IsMatch(path, @"\.auto[1-3]\.json$");
            SaveFileCommit.WriteAtomic(path, data, keepBackup: main);
        }

        private static byte[] ReadHead(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[(int)Math.Min(HeadBytes, fs.Length)];
                int n = fs.Read(buf, 0, buf.Length);
                return n == buf.Length ? buf : buf.Take(n).ToArray();
            }
        }

        /// <summary>存档的身份 = 头部里的内容校验和（内容相同 = 同一份，与文件修改时间无关）；读不出时用整段头部的哈希。</summary>
        private static string ChecksumOf(byte[] head)
        {
            if (head == null)
            {
                return null;
            }
            Match m = ChecksumRe.Match(Encoding.UTF8.GetString(head, 0, Math.Min(head.Length, 1024)));
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : CampaignSaveService.ComputeChecksum(Convert.ToBase64String(head), null);
        }

        private static CloudSaveSide SideOf(byte[] head)
        {
            var side = new CloudSaveSide { Checksum = ChecksumOf(head) };
            if (head == null)
            {
                return side;
            }
            try
            {
                string text = Encoding.UTF8.GetString(head);
                int cut = text.IndexOf(",\"PayloadJson\":\"", StringComparison.Ordinal);
                if (cut < 0)
                {
                    return side;
                }
                CampaignSaveEnvelope env = JsonUtility.FromJson<CampaignSaveEnvelope>(text.Substring(0, cut) + "}");
                CampaignSaveCard card = string.IsNullOrEmpty(env?.CardJson) ? null : JsonUtility.FromJson<CampaignSaveCard>(env.CardJson);
                side.WrittenAtUtc = env?.WrittenAtUtc;
                if (card != null)
                {
                    side.Act = card.Act;
                    side.Day = card.Day;
                    side.PlaySeconds = card.PlaySeconds;
                    side.RealPlaySeconds = card.RealPlaySeconds;
                    side.Readable = true;
                }
            }
            catch
            {
                side.Readable = false;
            }
            return side;
        }

        private static void Set(Dictionary<string, string> synced, string name, string checksum)
        {
            if (checksum == null)
            {
                synced.Remove(name);
            }
            else
            {
                synced[name] = checksum;
            }
        }

        private static SyncRecord LoadRecord(string dir)
        {
            try
            {
                string p = Path.Combine(dir, RecordFile);
                return File.Exists(p) ? JsonUtility.FromJson<SyncRecord>(File.ReadAllText(p)) ?? new SyncRecord() : new SyncRecord();
            }
            catch
            {
                return new SyncRecord();
            }
        }

        private static void SaveRecord(string dir, Dictionary<string, string> synced)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var rec = new SyncRecord { Entries = synced.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new SyncEntry { Name = k.Key, Checksum = k.Value }).ToList() };
                SaveFileCommit.WriteAtomic(Path.Combine(dir, RecordFile), new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(rec)), keepBackup: false);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[CloudSaveService] 写同步记录失败：{e.Message}");
            }
        }
    }
}
