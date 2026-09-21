using System;
using System.Security.Cryptography;

namespace GameLogic.Campaign
{
    /// <summary>
    /// ER1-SAVE-02：ERD-SAV-004 确定性——战役随机种子的唯一生成/消费入口。
    ///
    /// 新建战役时调用 <see cref="GenerateSeed"/> 生成一次性种子（生成动作本身不需要可重放——
    /// "新建"就是一次性事件），随后 <see cref="CampaignState.RandomSeed"/> 落盘持久化；
    /// 任何需要"同种子同输入流复现同一结果"的系统都必须通过 <see cref="CreateRng"/> 从这颗种子
    /// 重建随机序列，不得各自调用 <c>UnityEngine.Random</c>/<c>DateTime.Now</c>/
    /// <c>Environment.TickCount</c> 之类的进程态随机源——那些源的输出不随存档一起持久化，
    /// 读档后无法复现，直接违反 ERD-SAV-004。
    ///
    /// 替换的债务：<c>MainMenuUI.StartNewCampaign</c> 此前用 <c>Environment.TickCount</c> 占位
    /// （DEBT-ER1SAVE01-05）。TickCount 精度只有约 15ms，连续快速新建战役（含自动化测试连续调用）
    /// 可能撞出相同种子；<see cref="GenerateSeed"/> 改用 <see cref="RandomNumberGenerator"/>
    /// （CSPRNG）彻底消除这个碰撞风险。
    ///
    /// 范围声明：本 Story 只建立"种子生成 + 播种随机源"这一层机制，并未把现有的
    /// <c>UnityEngine.Random</c> 消费点（如 <c>CellDevourSystem</c> 的结构化掉落、
    /// <c>CardTriggerBus</c> 的触发判定）改接到这颗种子——那些属于战斗/掉落系统本身，
    /// 不在 ER1-SAVE-02 的存档/恢复编排权限范围内，全仓扫描重构会带来不可控的战斗数值风险。
    /// 已在验收证据里登记为遗留缺口，交由各自系统的后续 Story（掉落数量确定性大概率随
    /// ER5-EXP-01/战斗数值相关 Story）评估是否需要接入本服务。
    /// </summary>
    public static class CampaignRandomService
    {
        /// <summary>新建战役时调用一次，生成一颗强随机种子。返回值排除 0（与"未设置"哨兵值混淆）
        /// 与 <see cref="int.MinValue"/>（<see cref="Random"/> 的某些内部路径对它有边界处理），
        /// 命中这两个边界值时改用 1，不重试整个流程（1 也是一颗完全合法、可复现的种子）。</summary>
        public static int GenerateSeed()
        {
            Span<byte> buffer = stackalloc byte[4];
            RandomNumberGenerator.Fill(buffer);
            int seed = BitConverter.ToInt32(buffer);
            return seed == 0 || seed == int.MinValue ? 1 : seed;
        }

        /// <summary>唯一允许的确定性随机源构造入口：同一 <paramref name="state"/>.RandomSeed
        /// 两次调用必须给出同一串后续随机序列——这是 <see cref="System.Random"/> 自身的构造契约，
        /// 本方法只是把它和 <see cref="CampaignState"/> 显式绑在一起，避免调用方各自散落
        /// <c>new Random(...)</c> 传错字段。</summary>
        public static Random CreateRng(CampaignState state) => new Random(state?.RandomSeed ?? 0);

        /// <summary>直接从种子数值构造，供不方便持有 <see cref="CampaignState"/> 实例的测试/工具代码使用。</summary>
        public static Random CreateRng(int seed) => new Random(seed);
    }
}
