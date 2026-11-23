using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GameLogic.Campaign;
using GameLogic.Settings;
using UnityEngine;

namespace GameLogic.Diagnostics
{
    /// <summary>一次游玩（从进入一局到离开 / 退出游戏）的本地统计。字段名就是导出文件的列名。</summary>
    [Serializable]
    public sealed class PlaySessionStat
    {
        public string StartedUtc = string.Empty;
        public string Mode = "campaign";
        /// <summary>真实秒（暂停、倍速都按真实时间记）。</summary>
        public float PlaySeconds;
        public int Uplinks;
        public float UplinkSeconds;
        public float AvgUplinkSeconds;
        public int RaidsDestroyed;
        public int RaidsWithdrawn;
        public int RaidsCoreLost;
        public int MachinesLost;
        /// <summary>每一幕用时（真实秒），下标 = 幕 - 1。</summary>
        public float[] ActSeconds = new float[LocalPlayStats.MaxActs];
    }

    [Serializable]
    public sealed class PlayStatsFile
    {
        public int Version = 1;
        public List<PlaySessionStat> Sessions = new List<PlaySessionStat>();
    }

    /// <summary>
    /// FG15-SYS-03 同批（FG-GAP-047 → FG15 FGR-SYS-100 本地统计）：玩家在设置 → 游戏性里勾选同意后才记录，默认不记、不写任何文件；不联网。
    /// 记：每次游玩时长、接入次数与平均时长、突袭结果（全歼 / 撤退 / 核心被毁）、每一幕用时、机器阵亡数。
    /// 存在本机 <c>LocalStats/stats.json</c>（每 60 秒与离开一局时写一次），“导出”另存一份带时间的文件给试玩者自己交给开发者（FG16 平衡用）。
    /// 每帧 O(1)；突袭结果每真实秒扫一次存档里的结算记录（条数有上限 raid.history_max）。
    /// </summary>
    public static class LocalPlayStats
    {
        public const int MaxActs = 6;
        public const float FlushIntervalSeconds = 60f;

        public static string Dir => Path.Combine(CrashReporter.Root, "LocalStats");
        public static string FilePath => Path.Combine(Dir, "stats.json");

        public static bool Enabled => GameSettings.LocalStatsConsent;

        /// <summary>正在记录的这一局（未同意 / 不在局里 = null）。</summary>
        public static PlaySessionStat Current { get; private set; }

        private static PlayStatsFile _file;
        private static CampaignState _campaign;
        private static int _uplinkMachine;
        private static readonly HashSet<int> SeenRaids = new HashSet<int>();
        private static float _raidScanIn;
        private static float _flushIn;
        private static bool _subscribed;

        /// <summary>主线程每帧调用（UiKitInputPump），<paramref name="realDt"/> = 真实秒。</summary>
        public static void Tick(float realDt)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                MachineRegistry.MachineDied += OnMachineDied;
            }
            CampaignState s = CampaignSession.Current;
            if (!Enabled)
            {
                // 撤回同意：这一局没写盘的部分直接丢掉，不再记。
                Current = null;
                _campaign = null;
                return;
            }
            if (!ReferenceEquals(s, _campaign))
            {
                EndSession();
                if (s != null)
                {
                    BeginSession(s);
                }
            }
            if (Current == null || s == null)
            {
                return;
            }
            float dt = Mathf.Max(0f, realDt);
            Current.PlaySeconds += dt;
            int act = Mathf.Clamp(s.Progress != null ? s.Progress.Act : 1, 1, MaxActs);
            Current.ActSeconds[act - 1] += dt;
            int machine = Campaign.Signal.SignalUplinkService.CurrentMachine(s);
            if (machine != 0)
            {
                Current.UplinkSeconds += dt;
                if (machine != _uplinkMachine)
                {
                    Current.Uplinks++;
                }
            }
            _uplinkMachine = machine;
            Current.AvgUplinkSeconds = Current.Uplinks > 0 ? Current.UplinkSeconds / Current.Uplinks : 0f;
            _raidScanIn -= dt;
            if (_raidScanIn <= 0f)
            {
                _raidScanIn = 1f;
                ScanRaids(s, count: true);
            }
            _flushIn -= dt;
            if (_flushIn <= 0f)
            {
                _flushIn = FlushIntervalSeconds;
                Write();
            }
        }

        /// <summary>离开一局（回主菜单、读别的档、退出游戏）：把这一局写进文件。</summary>
        public static void EndSession()
        {
            if (Current != null && Current.PlaySeconds > 0f)
            {
                ScanRaids(_campaign, count: true);
                Write();
            }
            Current = null;
            _campaign = null;
        }

        /// <summary>导出一份带时间的副本，返回路径；没有数据 / 写不了返回 null 并给出原因。</summary>
        public static string Export(out string error)
        {
            error = null;
            Write();
            PlayStatsFile f = Load();
            if (f.Sessions.Count == 0)
            {
                error = "no_data";
                return null;
            }
            try
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "playtest-stats-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.WriteAllText(path, JsonUtility.ToJson(f, true), new UTF8Encoding(false));
                return path;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>读文件（没有 / 坏了 = 空表，不抛异常）。</summary>
        public static PlayStatsFile Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    PlayStatsFile f = JsonUtility.FromJson<PlayStatsFile>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (f != null && f.Sessions != null)
                    {
                        return f;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LocalPlayStats] 统计文件读不了，按空表处理：" + e.Message);
            }
            return new PlayStatsFile();
        }

        public static void ResetForTests()
        {
            Current = null;
            _campaign = null;
            _file = null;
            _uplinkMachine = 0;
            SeenRaids.Clear();
            _raidScanIn = 0f;
            _flushIn = FlushIntervalSeconds;
        }

        private static void BeginSession(CampaignState s)
        {
            _campaign = s;
            _file = Load();
            Current = new PlaySessionStat
            {
                StartedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                Mode = s.Progress != null && s.Progress.IsSandbox ? "sandbox" : "campaign",
            };
            _file.Sessions.Add(Current);
            _uplinkMachine = Campaign.Signal.SignalUplinkService.CurrentMachine(s); // 读档时已经接着的机器不算新的一次接入
            SeenRaids.Clear();
            ScanRaids(s, count: false); // 读档前就结束的突袭不算这一局的
            _raidScanIn = 1f;
            _flushIn = FlushIntervalSeconds;
        }

        private static void ScanRaids(CampaignState s, bool count)
        {
            RaidResultRecord[] all = s?.Raids?.Results?.Results;
            if (all == null)
            {
                return;
            }
            foreach (RaidResultRecord r in all)
            {
                if (r == null || r.EndTick < 0 || !SeenRaids.Add(r.Serial) || !count || Current == null)
                {
                    continue;
                }
                switch (r.Outcome)
                {
                    case "destroyed": Current.RaidsDestroyed++; break;
                    case "withdrawn": Current.RaidsWithdrawn++; break;
                    case "core_lost": Current.RaidsCoreLost++; break;
                }
            }
        }

        private static void OnMachineDied(int logicId)
        {
            if (Current != null && Enabled)
            {
                Current.MachinesLost++;
            }
        }

        private static void Write()
        {
            if (!Enabled || _file == null)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(_file, true), new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LocalPlayStats] 统计写不进去（磁盘满或被占用），下次再写：" + e.Message);
            }
        }
    }
}
