using System;
using System.IO;
using BinGames.Sim;
using GameLogic.Battle;
using Unity.Mathematics;
using UnityEngine;

namespace GameLogic.Progression
{
    /// <summary>M1-06：控制交接记忆的磁盘布局（v1）。
    /// 全 public 字段（非属性）以兼容 <see cref="JsonUtility"/>；
    /// **故意不存 <c>SimEntityId</c>**——它只在生成它的那个 <c>SimWorld</c> 实例内有效，
    /// 世界一重建（换场景/重开进程）旧值就是垃圾，落盘后读回来当真实 ID 用会直接串体
    /// （把 A 局的某个数值当成 B 局里另一个实体的身份）。跨世界唯一可信的是热更层自己
    /// 分配、按同样顺序重建就能对上的 <see cref="ControlledLogicId"/>。</summary>
    [Serializable]
    public sealed class ControlHandoffSaveData
    {
        public int Version = ControlPersistence.CurrentVersion;
        /// <summary>受控实体的热更层逻辑 id。0 表示没有可用的跨世界标识。</summary>
        public int ControlledLogicId;
        public float AnchorX;
        public float AnchorY;
        /// <summary><c>AnchorX</c>/<c>AnchorY</c> 是否真的被写过。</summary>
        public bool HasAnchor;
    }

    /// <summary>
    /// M1-06：<see cref="ControlHandoffState"/> 的跨局/跨进程持久化 IO。独立 JSON 文件
    /// （<see cref="Application.persistentDataPath"/>/<c>control_state.json</c>）。
    ///
    /// Reject-to-Safe：<see cref="Load"/>/<see cref="Save"/> 永不 throw——文件缺失/损坏/版本
    /// 不认识一律降级为「明确为无」（<see cref="ControlHandoffState.None"/>），绝不能因为存档
    /// 问题挡住进场流程。文件不存在是首次运行的正常情况，不打 warning；内容损坏或版本
    /// 不认识才打 warning。只做 v1，不做版本迁移框架（比当前版本更新的存档一律按不认识处理）。
    /// </summary>
    public static class ControlPersistence
    {
        public const int CurrentVersion = 1;
        private const string FileName = "control_state.json";

        /// <summary>存档绝对路径。测试/验收可直接读写这个文件。</summary>
        public static string FilePath
        {
            get { return Path.Combine(Application.persistentDataPath, FileName); }
        }

        /// <summary>读取上次的控制交接记忆。文件不存在 → 明确为无（首次运行，静默）；
        /// 内容损坏/版本不认识 → warning 日志 + 明确为无。<see cref="ControlHandoffState.ControlledUnitId"/>
        /// 恒为 <see cref="SimEntityId.None"/>——绝不从磁盘复原稳定实体 ID。</summary>
        public static ControlHandoffState Load()
        {
            string path;
            try
            {
                path = FilePath;
                if (!File.Exists(path))
                {
                    return ControlHandoffState.None;
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[ControlPersistence] 读取存档路径失败，按无控制记忆继续：{e.Message}");
                return ControlHandoffState.None;
            }

            ControlHandoffSaveData data;
            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    TEngine.Log.Warning("[ControlPersistence] 存档为空文件，按无控制记忆继续。");
                    return ControlHandoffState.None;
                }

                data = JsonUtility.FromJson<ControlHandoffSaveData>(json);
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[ControlPersistence] 存档损坏或无法读取，按无控制记忆继续：{e.Message}");
                return ControlHandoffState.None;
            }

            if (data == null)
            {
                TEngine.Log.Warning("[ControlPersistence] 存档反序列化为 null，按无控制记忆继续。");
                return ControlHandoffState.None;
            }

            if (data.Version <= 0 || data.Version > CurrentVersion)
            {
                TEngine.Log.Warning(
                    $"[ControlPersistence] 存档版本 {data.Version} 不是可识别的版本（当前 {CurrentVersion}，本期不做迁移），按无控制记忆继续。");
                return ControlHandoffState.None;
            }

            var state = new ControlHandoffState
            {
                ControlledUnitId = SimEntityId.None,
                ControlledLogicId = data.ControlledLogicId,
                FallbackAnchor = new float2(data.AnchorX, data.AnchorY),
                HasAnchor = data.HasAnchor,
            };
            state.HasRecord = state.ControlledLogicId != 0 || state.HasAnchor;
            return state;
        }

        /// <summary>整份覆盖写入（不是追加）。<c>state.HasRecord == false</c> 等价于 <see cref="Clear"/>。
        /// 磁盘异常只记日志，不抛出。</summary>
        public static void Save(in ControlHandoffState state)
        {
            if (!state.HasRecord)
            {
                Clear();
                return;
            }

            try
            {
                var data = new ControlHandoffSaveData
                {
                    Version = CurrentVersion,
                    ControlledLogicId = state.ControlledLogicId,
                    AnchorX = state.FallbackAnchor.x,
                    AnchorY = state.FallbackAnchor.y,
                    HasAnchor = state.HasAnchor,
                };
                File.WriteAllText(FilePath, JsonUtility.ToJson(data));
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[ControlPersistence] 控制记忆写入失败（本次记录将丢失）：{e.Message}");
            }
        }

        /// <summary>删除存档文件。文件本就不存在也算成功，永不 throw。</summary>
        public static void Clear()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[ControlPersistence] 删除存档失败：{e.Message}");
            }
        }
    }
}
