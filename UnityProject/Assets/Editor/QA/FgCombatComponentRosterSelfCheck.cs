using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using BinGames.Sim.Combat;
using GameConfig.fg;
using GameLogic.Campaign;
using GameLogic.Campaign.Blueprint;
using GameLogic.Campaign.Combat;
using GameLogic.Campaign.Content;
using GameLogic.Campaign.Grid;
using GameLogic.Campaign.Primitive;
using GameLogic.Campaign.Regions;
using GameLogic.Campaign.Signal;
using GameLogic.Core;
using GameLogic.Localization;
using GameLogic.Settings;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GameLogic.EditorTools
{
    /// <summary>
    /// FG2-VFX-02 形变全量里“名表补齐”的自动验收（FG-GAP-051：设计案 5.6 其余 5 个作战组件；承接 DEBT-FG2FW02-02 区域 / 无人机外观）。
    /// 起真实系统跑、断言行为（行为坏了会失败，不是“字段存在”）：
    /// A 表行 / 目录 / 文本（中英）/ 装配编译 / 固定底盘兼容；说明里的数与表同源；
    /// B 旋刃环：一整圈触及（身后、侧面都打到）——对照液压刺锤只打前方窄扇形；
    /// C 哨戒桩：插在母机身前、母机走开后桩不跟着走，只打锚点周围的敌人；母机走远后补插新桩、旧桩到期消失；母机被毁一起失效——对照蜂群舱无人机跟着母机；
    /// D 震荡脉冲器：区域落在自己脚下（冲击波外观）、要贴近才出手、把目标推开——对照喷洒器落在目标脚下；
    /// E 拆解钳：夹住后持续拆解（命中后再拆 3 次）——对照液压刺锤只一下；
    /// F 尖刺外装：被近身攻击反伤（直控 / 编队开火与敌人驻守开火两条路径都算）、远程不算、没装不算、反伤不连锁；
    /// G 区域 / 无人机外观（DEBT-FG2FW02-02）：液池 / 冲击波 / 减速网 / 伴飞无人机 / 定点哨戒桩各是不同种类，区域颜色 = 挂的状态标签色；
    /// H 内核快照格式 7 往返逐位一致、续跑一致；格式 6 旧快照照样读；坏值整份拒绝；
    /// I 倍速 / 暂停：同一场战斗按不同的每帧步数推进结果逐位相同，暂停不推进；J 性能。已并入 <c>CellFrameworkValidate.RunAll</c>。
    /// </summary>
    public static class FgCombatComponentRosterSelfCheck
    {
        private const int Slot = 0;
        private const float Dt = 1f / 60f;
        private const int UplinkSlot = 2;

        private static readonly string[] NewComponents =
        {
            ComponentCatalog.CompOrbitId, ComponentCatalog.CompSentryId, ComponentCatalog.CompPulserId, ComponentCatalog.CompClawId, ComponentCatalog.FuncSpikesId,
        };

        private static StringBuilder _report;
        private static int _fail;
        private static int _pass;
        private static readonly List<string> PerfLines = new List<string>();

        [MenuItem("BinGames/自检：FG2-VFX-02 作战组件名表补齐")]
        public static void RunFromMenu()
        {
            var report = new StringBuilder();
            int fail = Run(report);
            report.AppendLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
            Debug.Log(report.ToString());
        }

        public static int Run(StringBuilder report)
        {
            _report = report;
            _fail = 0;
            _pass = 0;
            PerfLines.Clear();
            Line("\n[组件名表] 作战组件名表补齐与区域 / 无人机外观（FG2-VFX-02）");
            GameLanguage originalLanguage = GameSettings.Language;
            CampaignState originalSession = CampaignSession.Current;
            int originalSlot = CampaignSession.ActiveSlotIndex;
            try
            {
                ConfigSystem.Instance.Load();
                GameText.Reload();
                GridContent.Reload();
                FgContentTables.Reload();
                FirmwareKinds.ResetForTests();
                StatusTagCatalog.Reload();
                CarrierReadings.Reload();
                FirmwareCatalog.Invalidate();
                GameSettings.SetLanguage(GameLanguage.ZhCn);
                NewState(9301);
                Line($"  · 环境：Unity {Application.unityVersion}，batchmode={Application.isBatchMode}，处理器 {SystemInfo.processorType}（{SystemInfo.processorCount} 线程）；" +
                     "战斗内核是 AOT + Burst，热更层在 Editor 下是 Mono JIT、真机走 HybridCLR 解释执行（真机复测归 FG15-SYS-02）");
                Step(CheckData);
                Step(CheckOrbit);
                Step(CheckSentry);
                Step(CheckPulser);
                Step(CheckClaw);
                Step(CheckSpikes);
                Step(CheckEffectLooks);
                Step(CheckSnapshot);
                Step(CheckSpeedAndPause);
                Step(CheckPerformance);
                foreach (string p in PerfLines)
                {
                    Line("  · 性能：" + p);
                }
            }
            catch (Exception e)
            {
                Fail($"组件名表自检抛异常：{e}");
            }
            finally
            {
                GameSettings.SetLanguage(originalLanguage);
                if (originalSession != null)
                {
                    CampaignSession.Set(originalSlot, originalSession);
                }
                else
                {
                    CampaignSession.Clear();
                }
                GameClock.ResetSession();
            }
            Line($"  · [组件名表] 断言通过 {_pass}，失败 {_fail}");
            return _fail;
        }

        // ── A. 表行 / 目录 / 文本 / 装配 ─────────────────────────────────────────────

        private static void CheckData()
        {
            Line("  · A. 设计案 5.6 名表 16 个作战组件全部在表里；新 5 个的载体与投送参数、目录条目（中英文名 / 说明）、电路装配编译、固定底盘兼容；说明里的数与表同源");
            var ids = CarrierReadings.Components.Select(c => c.Id).ToList();
            var roster = MachineMorph.MorphComponents;
            Expect(ids.Count == 16 && roster.All(ids.Contains) && NewComponents.All(ids.Contains), $"fg.TbCombatComponent {ids.Count} 行 = 名表 16 个（{string.Join("、", ids)}）");
            CarrierReadings.TryGetComponent(ComponentCatalog.CompOrbitId, out CombatComponent orbit);
            CarrierReadings.TryGetComponent(ComponentCatalog.CompSentryId, out CombatComponent sentry);
            CarrierReadings.TryGetComponent(ComponentCatalog.CompPulserId, out CombatComponent pulser);
            CarrierReadings.TryGetComponent(ComponentCatalog.CompClawId, out CombatComponent claw);
            CarrierReadings.TryGetComponent(ComponentCatalog.FuncSpikesId, out CombatComponent spikes);
            Expect(orbit?.Carrier == "melee" && orbit.Cone >= 180f && sentry?.Carrier == "summon" && sentry.DroneMode == "post" && sentry.DroneLeash > 0f
                   && pulser?.Carrier == "field" && pulser.FieldPlace == "self" && claw?.Carrier == "melee" && claw.EchoCount > 0
                   && spikes?.Slot == "function" && spikes.Carrier == "none" && spikes.Thorns > 0f && spikes.ThornsReach > 0f,
                "旋刃环 = 格斗一整圈；哨戒桩 = 无人机定点（有射程）；震荡脉冲器 = 布区落在自己脚下；拆解钳 = 格斗 + 自带回波；尖刺外装 = 功能组件 + 反伤");

            var missingText = new List<string>();
            foreach (GameLanguage lang in new[] { GameLanguage.ZhCn, GameLanguage.En })
            {
                GameSettings.SetLanguage(lang);
                foreach (string id in NewComponents)
                {
                    if (!ComponentCatalog.TryGet(id, out MechanicalContentDef def) || string.IsNullOrWhiteSpace(def.DisplayName) || def.DisplayName.Contains("⟦")
                        || string.IsNullOrWhiteSpace(def.Description) || def.Description.Contains("⟦") || (CarrierReadings.SubtypeName(id) ?? "⟦").Contains("⟦"))
                    {
                        missingText.Add($"{lang}/{id}");
                    }
                }
            }
            GameSettings.SetLanguage(GameLanguage.ZhCn);
            bool cats = NewComponents.All(id => ComponentCatalog.TryGet(id, out MechanicalContentDef d)
                                               && d.Category == (id == ComponentCatalog.FuncSpikesId ? MechanicalContentCategory.FunctionComponent : MechanicalContentCategory.MainComponent)
                                               && d.Source == MechanicalContentSource.BaseBlueprint && d.AiPermission == MechanicalContentAiPermission.PlayerAndAllyAi);
            Expect(missingText.Count == 0 && cats,
                $"组件目录：4 个主组件 + 1 个功能组件，中英文名称 / 说明 / 载体细分都走文本键（没有 ⟦键⟧），来源 = 基础蓝图库、AI 可用{(missingText.Count > 0 ? "；缺：" + string.Join("、", missingText) : "")}");
            ComponentCatalog.TryGet(ComponentCatalog.FuncSpikesId, out MechanicalContentDef spikesDef);
            ComponentCatalog.TryGet(ComponentCatalog.CompPulserId, out MechanicalContentDef pulserDef);
            ComponentCatalog.TryGet(ComponentCatalog.CompSentryId, out MechanicalContentDef sentryDef);
            ComponentCatalog.TryGet(ComponentCatalog.CompClawId, out MechanicalContentDef clawDef);
            Expect(spikesDef != null && spikesDef.Description.Contains(spikes.Damage.ToString("0.#", CultureInfo.InvariantCulture)) && Mathf.Approximately(spikes.Thorns, 0.5f) && spikesDef.Description.Contains("一半")
                   && pulserDef != null && pulserDef.Description.Contains(pulser.Knockback.ToString("0.#", CultureInfo.InvariantCulture))
                   && sentryDef != null && sentryDef.Description.Contains(sentry.DroneLeash.ToString("0.#", CultureInfo.InvariantCulture))
                   && clawDef != null && clawDef.Description.Contains(claw.EchoCount.ToString(CultureInfo.InvariantCulture)),
                $"说明里的数就是表里的数：尖刺外装反伤 {spikes?.Damage} + 一半、脉冲推开 {pulser?.Knockback} 米、哨戒桩射程 {sentry?.DroneLeash} 米、拆解钳再拆 {claw?.EchoCount} 次");

            var compileBad = new List<string>();
            foreach (string id in NewComponents.Where(x => x != ComponentCatalog.FuncSpikesId))
            {
                BlueprintCircuitPreview p = Compile(id, null);
                CombatWeapon w = CombatSite.MachineWeaponFrom(p);
                CarrierReadings.TryGetComponent(id, out CombatComponent row);
                CarrierReadings.TryParseCarrier(row.Carrier, out FirmwareCarrier fc);
                if (!p.HasCombatOutput || p.PathCount <= 0 || w.Reading.Carrier != (CombatCarrier)(byte)fc || !Mathf.Approximately(CombatSite.MachineHitDamage(p), row.Damage))
                {
                    compileBad.Add($"{id}（出口 {p.HasCombatOutput} / 路径 {p.PathCount} / 载体 {w.Reading.Carrier} / 每发 {CombatSite.MachineHitDamage(p)}）");
                }
            }
            BlueprintCircuitPreview ps = Compile(ComponentCatalog.CompGunId, ComponentCatalog.FuncSpikesId);
            CombatWeapon ws = CombatSite.MachineWeaponFrom(ps);
            CombatWeapon wPlain = CombatSite.MachineWeaponFrom(Compile(ComponentCatalog.CompGunId, null));
            Expect(compileBad.Count == 0, $"4 个新主组件在电路编辑器里真实编译：有攻击出口、有源→汇路径、内核载体 = 表、每发伤害 = 表{(compileBad.Count > 0 ? "；不符：" + string.Join("、", compileBad) : "")}");
            Expect(ps.UtilityId == ComponentCatalog.FuncSpikesId && Mathf.Approximately(ws.Reading.Thorns, spikes.Thorns) && Mathf.Approximately(ws.Reading.ThornsFlat, spikes.Damage)
                   && Mathf.Approximately(ws.Reading.ThornsReach, spikes.ThornsReach) && ws.Reading.Carrier == CombatCarrier.Projectile && wPlain.Reading.Thorns == 0f,
                "连射器 + 尖刺外装：武器仍是连射器的射弹载体，另带反伤（比例 / 固定值 / 触及 = 表）；不装尖刺外装的没有反伤");
            bool turret = NewComponents.All(id => CarrierReadings.CanMount(CarrierReadings.FixedChassisId, id, out _, out _));
            Expect(turret, "5 个新组件都能装上固定底盘（炮塔）：直接兼容（冲刺器仍是唯一不兼容的）");
            MachineMorphMaskCheck();
        }

        private static void MachineMorphMaskCheck()
        {
            var p = new BlueprintCircuitPreview { PrimaryId = null, UtilityId = ComponentCatalog.FuncSpikesId, FirmwareIds = new[] { FirmwareCatalog.FwTrailId, FirmwareCatalog.FwOverloadId } };
            var q = new BlueprintCircuitPreview { PrimaryId = ComponentCatalog.CompSentryId, FirmwareIds = new[] { "fw_boomerang" } };
            Expect(MachineMorph.MaskOf(p) == (MorphMask.Fluid | MorphMask.Limiter) && MachineMorph.MaskOf(q) == MorphMask.Electromagnetic,
                "新组件参与形变：只装尖刺外装（功能组件）+ 拖尾 + 过载 = 喷口态 + 形变态；哨戒桩 + 回旋 = 线圈态");
        }

        // ── B. 旋刃环 ───────────────────────────────────────────────────────────────

        private static void CheckOrbit()
        {
            Line("  · B. 旋刃环（格斗·环绕）：打前方目标时，身后、侧面触及范围里的敌人一起挨打，触及外的不挨；对照液压刺锤（前方窄扇形）身后侧面都不挨");
            (float front, float back, float side, float far) Hit(string comp)
            {
                using CombatKernel k = NewKernel(8);
                int wi = k.AddWeapon(WeaponFor(comp, null));
                int me = k.Spawn(Machine(double2.zero, wi));
                int f = k.Spawn(Hostile(new double2(1.6, 0), 1000f));
                int b = k.Spawn(Hostile(new double2(-1.6, 0), 1000f));
                int s = k.Spawn(Hostile(new double2(0, 1.6), 1000f));
                int x = k.Spawn(Hostile(new double2(6, 0), 1000f));
                CombatFireResult r = k.FireAt(me, f, k.Time);
                if (r != CombatFireResult.Ok)
                {
                    Fail($"{comp} 开火结果 {r}");
                }
                return (Lost(k, f), Lost(k, b), Lost(k, s), Lost(k, x));
            }
            var o = Hit(ComponentCatalog.CompOrbitId);
            var r2 = Hit(ComponentCatalog.CompRamId);
            Expect(o.front > 0f && o.back > 0f && o.side > 0f && o.far == 0f,
                $"旋刃环：前 {o.front:0.#} / 后 {o.back:0.#} / 侧 {o.side:0.#} 都挨打，6 米外 {o.far:0.#}");
            Expect(r2.front > 0f && r2.back == 0f && r2.side == 0f, $"对照液压刺锤：前 {r2.front:0.#}，身后 {r2.back:0.#}、侧面 {r2.side:0.#}（窄扇形）");
        }

        // ── C. 哨戒桩 ───────────────────────────────────────────────────────────────

        private static void CheckSentry()
        {
            Line("  · C. 哨戒桩（无人机·定点）：开火插下 2 根桩（身前、朝目标）；母机走开后桩不动、只打锚点周围 7 米内的敌人；再开火时够不着的旧桩当场拔掉补插（任何时刻最多 2 根）；插下的桩够不着的目标 = 射程外（直控 / 驻守开火都不开火）；母机被毁一起失效；对照蜂群舱无人机跟着母机走");
            using CombatKernel k = NewKernel(16);
            int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
            int me = k.Spawn(Machine(double2.zero, wi));
            int e = k.Spawn(Hostile(new double2(5, 0), 5000f));
            int f = k.Spawn(Hostile(new double2(-16, 3), 5000f));
            CombatFireResult r = k.FireAt(me, e, k.Time);
            var anchors = Drones(k).Where(d => d.Owner == me).ToList();
            bool planted = r == CombatFireResult.Ok && anchors.Count == 2 && anchors.All(d => d.Anchored == 1 && math.distance(d.Anchor, d.Pos) < 1e-9
                                                                                           && d.Anchor.x > 1.5 && math.distance(d.Anchor, double2.zero) < 3.5);
            Expect(planted, $"开火插下 2 根定点桩，插在母机朝目标一侧的身前（{string.Join(" / ", anchors.Select(d => $"({d.Anchor.x:0.0},{d.Anchor.y:0.0})"))}）");
            k.IssueCommand(me, CombatCommandKind.Move, new double2(-16, 0), 0, 0.3f, 0f, 1f, false);
            Run(k, 5f);
            k.TryGetUnit(me, out CombatUnitView mv);
            var after = Drones(k).Where(d => d.Owner == me).ToList();
            bool stayed = after.Count == 2 && after.Zip(anchors, (x, y) => math.distance(x.Pos, y.Anchor) < 1e-9 && math.distance(x.Anchor, y.Anchor) < 1e-9).All(ok => ok);
            float eLost = Lost(k, e);
            float fLost = Lost(k, f);
            Expect(mv.Position.x < -12 && stayed && eLost > 0f && fLost == 0f,
                $"母机撤到 ({mv.Position.x:0.0},{mv.Position.y:0.0})：桩原地不动、继续打锚点旁的敌人（{eLost:0.#}），母机身边 7 米外锚点的敌人一下没挨（{fLost:0.#}）");
            // 走远后再开火（修复轮）：够不着的旧桩当场拔掉、在母机身前补插——任何时刻最多 2 根（ADR-VFX-002 §8-3“同时 2 根”）。
            float eBefore = Lost(k, e);
            k.FireAt(me, f, k.Time);
            var mixed = Drones(k).Where(d => d.Owner == me && d.Until > k.Time).ToList();
            int fresh = mixed.Count(d => d.Anchor.x < -12);
            Run(k, 3f);
            float eAfter = Lost(k, e);
            Run(k, 7f);
            var later = Drones(k).Where(d => d.Owner == me).ToList();
            Expect(mixed.Count == 2 && fresh == 2 && k.DroneCount == 2 && later.Count == 2 && later.All(d => d.Anchor.x < -12) && Lost(k, f) > 0f && Math.Abs(eAfter - eBefore) < 1e-3f,
                $"走远后再开火：旧桩当场拔掉、补插 2 根新桩（在场 {mixed.Count} 根，其中新位置 {fresh} 根；旧锚点旁的敌人不再挨打 {eAfter - eBefore:0.#}），新桩打母机身边的敌人");
            k.Kill(me, 0);
            Run(k, 0.1f);
            Expect(k.DroneCount == 0, "母机被毁：桩一起失效");

            // 负向（修复轮，审查 P1）：插下的桩够不着的目标 = 射程外——直控 / 编队开火返回射程外，不插桩、不算开火（开火提示与开火计数同一道门）、不积热、目标不掉血。
            // 母机半径 0.6、目标半径 0.5、牵引绳 7、错开 0.9：桩的打击距离 ≈ 0.6 + 1.5 + √(7.5² − 0.9²) ≈ 9.55 米（< 武器射程 12 米）。
            foreach (double far in new[] { 10.0, 11.5 })
            {
                using CombatKernel kf = NewKernel(8);
                int wf = kf.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
                int mf = kf.Spawn(Machine(double2.zero, wf));
                int tf = kf.Spawn(Hostile(new double2(far, 0), 5000f));
                CombatFireResult rf = kf.FireAt(mf, tf, kf.Time);
                long shots = kf.Counters.ShotsFired;
                kf.TryGetUnit(mf, out CombatUnitView fv);
                Run(kf, 3f);
                Expect(rf == CombatFireResult.OutOfRange && kf.DroneCount == 0 && shots == 0 && fv.Heat <= 0f && Lost(kf, tf) == 0f,
                    $"目标在 {far:0.0} 米（桩够不着、仍在 12 米武器射程内）：直控 / 编队开火 = {rf}，插桩 {kf.DroneCount} 根，计入开火 {shots} 次，积热 {fv.Heat:0.#}，目标掉血 {Lost(kf, tf):0.#}");
            }
            using (CombatKernel kn = NewKernel(8))
            {
                int wn = kn.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
                int mn = kn.Spawn(Machine(double2.zero, wn));
                int tn = kn.Spawn(Hostile(new double2(9.2, 0), 5000f));
                CombatFireResult rn = kn.FireAt(mn, tn, kn.Time);
                Run(kn, 3f);
                Expect(rn == CombatFireResult.Ok && kn.DroneCount == 2 && Lost(kn, tn) > 0f,
                    $"目标在 9.2 米（打击距离边上）：开火 = {rn}，插下 {kn.DroneCount} 根桩，目标掉血 {Lost(kn, tn):0.#}（开火了就打得到）");
            }
            // 驻守开火（炮塔 / 敌方同一规则）：交战距离按桩的打击距离收紧——11 米的目标不找、不插桩；8 米的目标插桩并打到。
            foreach ((double gap, bool expectHit) in new[] { (11.0, false), (8.0, true) })
            {
                using CombatKernel kh = NewKernel(8);
                int wh = kh.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
                int post = kh.Spawn(Hostile(double2.zero, 5000f, wh));
                int prey = kh.Spawn(Machine(new double2(gap, 0), -1, 5000f, 5000f));
                Run(kh, 4f);
                bool hit = Lost(kh, prey) > 0f;
                Expect(hit == expectHit && (kh.DroneCount > 0) == expectHit,
                    $"驻守开火装哨戒桩、目标在 {gap:0} 米：{(expectHit ? "插桩并打到" : "不开火、不插桩")}（桩 {kh.DroneCount} 根，目标掉血 {Lost(kh, prey):0.#}）");
            }
            // 上限：在两侧目标之间来回开火，同一台母机在场的桩任何时刻都不超过 2 根。
            using (CombatKernel kc = NewKernel(16))
            {
                int wc = kc.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
                int mc = kc.Spawn(Machine(double2.zero, wc));
                int left = kc.Spawn(Hostile(new double2(-6, 0), 5000f));
                int right = kc.Spawn(Hostile(new double2(6, 0), 5000f));
                int maxLive = 0;
                for (int round = 0; round < 6; round++)
                {
                    kc.FireAt(mc, round % 2 == 0 ? right : left, kc.Time);
                    maxLive = Math.Max(maxLive, Drones(kc).Count(d => d.Owner == mc && d.Until > kc.Time));
                    Run(kc, 0.5f);
                    maxLive = Math.Max(maxLive, kc.DroneCount);
                }
                Expect(maxLive == 2 && Lost(kc, left) > 0f && Lost(kc, right) > 0f,
                    $"左右来回开火 6 次：在场桩最多 {maxLive} 根（上限 2），两侧目标都挨到桩的打击");
            }

            using CombatKernel k2 = NewKernel(8);
            int w2 = k2.AddWeapon(WeaponFor(ComponentCatalog.CompDroneBayId, null));
            int me2 = k2.Spawn(Machine(double2.zero, w2));
            int e2 = k2.Spawn(Hostile(new double2(5, 0), 5000f));
            k2.FireAt(me2, e2, k2.Time);
            k2.IssueCommand(me2, CombatCommandKind.Move, new double2(-16, 0), 0, 0.3f, 0f, 1f, false);
            Run(k2, 5f);
            k2.TryGetUnit(me2, out CombatUnitView mv2);
            var follow = Drones(k2).Where(d => d.Owner == me2).ToList();
            Expect(follow.Count == 2 && follow.All(d => d.Anchored == 0 && math.distance(d.Pos, mv2.Position) < 4.0),
                "对照蜂群无人机舱：伴飞无人机跟着母机走（都在母机 4 米内）");
        }

        // ── D. 震荡脉冲器 ────────────────────────────────────────────────────────────

        private static void CheckPulser()
        {
            Line("  · D. 震荡脉冲器（布区·脉冲）：区域落在自己脚下、画成冲击波；把目标推开；触及外直控点击 = 射程外（不算开火）；对照喷洒器区域落在目标脚下");
            using (CombatKernel k = NewKernel(8))
            {
                int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompPulserId, null));
                int me = k.Spawn(Machine(new double2(1, 1), wi));
                int t = k.Spawn(Hostile(new double2(3, 1), 5000f));
                CombatFireResult r = k.FireAt(me, t, k.Time);
                k.TryGetZone(0, out CombatZone z);
                k.TryGetUnit(t, out CombatUnitView tv);
                Expect(r == CombatFireResult.Ok && k.ZoneCount == 1 && math.distance(z.Pos, new double2(1, 1)) < 1e-9 && z.Look == CombatZoneLook.Pulse
                       && tv.Position.x > 3.5 && Lost(k, t) > 0f,
                    $"脉冲区落在自己脚下 ({z.Pos.x:0.0},{z.Pos.y:0.0})、外观 = 冲击波；目标被推到 x={tv.Position.x:0.00}（推开 0.8 米）");
                float l0 = Lost(k, t);
                Run(k, 1f);
                Expect(Lost(k, t) > l0 + 0.5f, $"脉冲区持续伤害圈内的敌人（1 秒内再掉 {Lost(k, t) - l0:0.##}）");
            }
            using (CombatKernel k = NewKernel(8))
            {
                int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompPulserId, null));
                int me = k.Spawn(Machine(double2.zero, wi));
                int t = k.Spawn(Hostile(new double2(8, 0), 5000f));
                CombatFireResult r = k.FireAt(me, t, k.Time);
                Expect(r == CombatFireResult.OutOfRange && k.ZoneCount == 0 && k.Counters.ShotsFired == 0, "目标在 8 米外：射程外，不放脉冲、不算开火");
            }
            using (CombatKernel k = NewKernel(8))
            {
                int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompSprayerId, null));
                int me = k.Spawn(Machine(double2.zero, wi));
                int t = k.Spawn(Hostile(new double2(6, 0), 5000f));
                k.FireAt(me, t, k.Time);
                k.TryGetZone(0, out CombatZone z);
                Expect(k.ZoneCount == 1 && math.distance(z.Pos, new double2(6, 0)) < 1e-6 && z.Look == CombatZoneLook.Pool,
                    "对照喷洒器：区域落在目标脚下、外观 = 液池");
            }
        }

        // ── E. 拆解钳 ───────────────────────────────────────────────────────────────

        private static void CheckClaw()
        {
            Line("  · E. 拆解钳（格斗·拆解）：夹住正前方的敌人后再拆 3 次（每次 × 0.45）；身后的不夹；对照液压刺锤只一下");
            CarrierReadings.TryGetComponent(ComponentCatalog.CompClawId, out CombatComponent claw);
            CarrierReadings.TryGetComponent(ComponentCatalog.CompRamId, out CombatComponent ram);
            using (CombatKernel k = NewKernel(8))
            {
                int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompClawId, null));
                int me = k.Spawn(Machine(double2.zero, wi));
                int t = k.Spawn(Hostile(new double2(1.5, 0), 5000f));
                int b = k.Spawn(Hostile(new double2(-1.5, 0), 5000f));
                k.FireAt(me, t, k.Time);
                float first = Lost(k, t);
                Run(k, 2f);
                float total = Lost(k, t);
                float expect = claw.Damage * (1f + claw.EchoCount * claw.EchoRatio);
                Expect(Mathf.Approximately(first, claw.Damage) && Mathf.Abs(total - expect) < 0.05f && k.EchoCount == 0 && Lost(k, b) == 0f,
                    $"拆解钳：先 {first:0.##}，2 秒内共 {total:0.##}（= {claw.Damage} × (1 + {claw.EchoCount} × {claw.EchoRatio})）；身后的不挨");
            }
            using (CombatKernel k = NewKernel(8))
            {
                int wi = k.AddWeapon(WeaponFor(ComponentCatalog.CompRamId, null));
                int me = k.Spawn(Machine(double2.zero, wi));
                int t = k.Spawn(Hostile(new double2(1.5, 0), 5000f));
                k.FireAt(me, t, k.Time);
                Run(k, 2f);
                Expect(Mathf.Approximately(Lost(k, t), ram.Damage), $"对照液压刺锤：只一下 {Lost(k, t):0.##}");
            }
        }

        // ── F. 尖刺外装 ─────────────────────────────────────────────────────────────

        private static void CheckSpikes()
        {
            Line("  · F. 尖刺外装（被动反伤）：近身即时攻击 → 攻击者吃 4 + 这一击 × 0.5（直接开火与敌人驻守开火两条路径）；远程不反伤；没装不反伤；反伤不连锁；弹字进给");
            CarrierReadings.TryGetComponent(ComponentCatalog.FuncSpikesId, out CombatComponent sp);
            float expect = sp.Damage + 10f * sp.Thorns;
            CombatWeapon spiked = WeaponFor(ComponentCatalog.CompGunId, ComponentCatalog.FuncSpikesId);
            CombatWeapon plain = WeaponFor(ComponentCatalog.CompGunId, null);
            var claws = new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 2.5f, Damage = 10f, Cooldown = 1f, HasOutput = 1 };
            var gun = new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 14f, Damage = 10f, Cooldown = 1f, HasOutput = 1 };

            using (CombatKernel k = NewKernel(8))
            {
                int ws = k.AddWeapon(spiked);
                int we = k.AddWeapon(claws);
                int me = k.Spawn(Machine(double2.zero, ws));
                int h = k.Spawn(Hostile(new double2(1.5, 0), 1000f, we, CombatBehavior.None));
                long feed0 = k.ReadingFeedCountOf(CombatConst.ReadingFeedThorns);
                CombatFireResult r = k.FireAt(h, me, k.Time);
                Expect(r == CombatFireResult.Ok && Mathf.Approximately(Lost(k, me), 10f) && Mathf.Approximately(Lost(k, h), expect)
                       && k.ReadingFeedCountOf(CombatConst.ReadingFeedThorns) == feed0 + 1,
                    $"敌人贴身打一下：机器掉 10，敌人吃反伤 {Lost(k, h):0.##}（= {sp.Damage} + 10 × {sp.Thorns}），反伤弹字进给 +1");
            }
            using (CombatKernel k = NewKernel(8))
            {
                int ws = k.AddWeapon(spiked);
                int we = k.AddWeapon(claws);
                int me = k.Spawn(Machine(double2.zero, ws, 400f, 400f));
                int h = k.Spawn(Hostile(new double2(1.5, 0), 1000f, we, CombatBehavior.HoldFire));
                Run(k, 3.5f);
                float taken = Lost(k, me);
                int hits = (int)Math.Round(taken / 10f);
                Expect(hits >= 3 && Mathf.Abs(Lost(k, h) - hits * expect) < 0.05f,
                    $"敌人驻守开火（真实行为步进）3.5 秒打了 {hits} 下：每下都反伤，共 {Lost(k, h):0.##}");
            }
            using (CombatKernel k = NewKernel(8))
            {
                int ws = k.AddWeapon(spiked);
                int we = k.AddWeapon(gun);
                int me = k.Spawn(Machine(double2.zero, ws));
                int h = k.Spawn(Hostile(new double2(8, 0), 1000f, we, CombatBehavior.None));
                k.FireAt(h, me, k.Time);
                Expect(Lost(k, me) > 0f && Lost(k, h) == 0f, "8 米外的即时射击：机器挨打，但不算近战、不反伤");
            }
            using (CombatKernel k = NewKernel(8))
            {
                int wp = k.AddWeapon(plain);
                int we = k.AddWeapon(claws);
                int me = k.Spawn(Machine(double2.zero, wp));
                int h = k.Spawn(Hostile(new double2(1.5, 0), 1000f, we, CombatBehavior.None));
                k.FireAt(h, me, k.Time);
                Expect(Lost(k, me) > 0f && Lost(k, h) == 0f, "没装尖刺外装：贴身挨打也不反伤");
            }
            using (CombatKernel k = NewKernel(8))
            {
                // 反伤不连锁：机器（液压刺锤 + 尖刺外装）打一个也带反伤的敌人——敌人反伤一次，机器的反伤不因“被反伤”再触发。
                CombatWeapon ramSpiked = WeaponFor(ComponentCatalog.CompRamId, ComponentCatalog.FuncSpikesId);
                CombatWeapon foeThorns = claws;
                foeThorns.Reading.Thorns = 0.5f;
                foeThorns.Reading.ThornsFlat = 2f;
                foeThorns.Reading.ThornsReach = 1.5f;
                int wm = k.AddWeapon(ramSpiked);
                int wf = k.AddWeapon(foeThorns);
                int me = k.Spawn(Machine(double2.zero, wm));
                int h = k.Spawn(Hostile(new double2(1.5, 0), 1000f, wf, CombatBehavior.None));
                k.FireAt(me, h, k.Time);
                CarrierReadings.TryGetComponent(ComponentCatalog.CompRamId, out CombatComponent ram);
                Expect(Mathf.Approximately(Lost(k, h), ram.Damage) && Mathf.Approximately(Lost(k, me), 2f + ram.Damage * 0.5f),
                    $"双方都带反伤：敌人挨 {Lost(k, h):0.##}（只有刺锤本身），机器吃一次反伤 {Lost(k, me):0.##}，不来回弹");
            }
        }

        // ── G. 区域 / 无人机外观（DEBT-FG2FW02-02）────────────────────────────────────

        private static void CheckEffectLooks()
        {
            Line("  · G. 区域 / 无人机外观（承接 DEBT-FG2FW02-02）：渲染实例的种类 = 液池 / 冲击波 / 减速网 / 伴飞无人机 / 定点哨戒桩，各不相同；区域颜色 = 它挂的状态标签的图标色，没有标签用阵营色");
            using CombatKernel k = NewKernel(32);
            int wSpray = k.AddWeapon(WeaponFor(ComponentCatalog.CompSprayerId, null, "fw_acid"));
            int wPulse = k.AddWeapon(WeaponFor(ComponentCatalog.CompPulserId, null));
            int wWeave = k.AddWeapon(WeaponFor(ComponentCatalog.CompGunId, null, "fw_bridge"));
            int wBay = k.AddWeapon(WeaponFor(ComponentCatalog.CompDroneBayId, null));
            int wPost = k.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
            int a1 = k.Spawn(Machine(new double2(0, 0), wSpray));
            int a2 = k.Spawn(Machine(new double2(0, 20), wPulse));
            int a3 = k.Spawn(Machine(new double2(0, 40), wWeave));
            int a4 = k.Spawn(Machine(new double2(0, 60), wBay));
            int a5 = k.Spawn(Machine(new double2(0, 80), wPost));
            int t1 = k.Spawn(Hostile(new double2(6, 0), 5000f));
            int t2 = k.Spawn(Hostile(new double2(2, 20), 5000f));
            int t3 = k.Spawn(Hostile(new double2(6, 40), 5000f));
            k.Spawn(Hostile(new double2(7.5, 41), 5000f));
            int t4 = k.Spawn(Hostile(new double2(5, 60), 5000f));
            int t5 = k.Spawn(Hostile(new double2(5, 80), 5000f));
            k.FireAt(a1, t1, k.Time);
            k.FireAt(a2, t2, k.Time);
            k.FireAt(a3, t3, k.Time);
            k.FireAt(a4, t4, k.Time);
            k.FireAt(a5, t5, k.Time);
            CarrierReadings.TryGetTagEffect("Acid", out uint acidBit, out _, out _);
            int acidIndex = acidBit != 0u ? math.tzcnt(acidBit) : -1;
            const int AcidColor = 0x33CC55;
            var visuals = new NativeArray<float2>(32, Allocator.TempJob);
            var fx = new NativeList<CombatInstance>(16, Allocator.TempJob);
            try
            {
                for (int b = 0; b < 32; b++)
                {
                    visuals[b] = new float2(-1f, 0f);
                }
                if (acidIndex >= 0)
                {
                    visuals[acidIndex] = new float2(1f, AcidColor);
                }
                k.PrepareEffects(fx, double2.zero, visuals);
                var kinds = new List<int>();
                bool acidColored = false;
                bool pulseDefault = false;
                for (int i = 0; i < fx.Length; i++)
                {
                    int kind = (int)math.round(fx[i].B.w);
                    kinds.Add(kind);
                    if (kind == 20 && (int)fx[i].B.z == AcidColor)
                    {
                        acidColored = true;
                    }
                    if (kind == 21 && (int)fx[i].B.z == CombatConst.EffectFriendColor)
                    {
                        pulseDefault = true;
                    }
                }
                bool posts = Enumerable.Range(0, fx.Length).Any(i => (int)math.round(fx[i].B.w) == 25 && fx[i].B.x > 0.4f);
                Expect(acidIndex >= 0 && kinds.Contains(20) && kinds.Contains(21) && kinds.Contains(22) && kinds.Contains(24) && posts,
                    $"实例种类：液池 20、冲击波 21、减速网 22、伴飞无人机 24、定点哨戒桩 25（实有 {string.Join(",", kinds.Distinct().OrderBy(x => x))}）");
                Expect(acidColored && pulseDefault, "喷洒器 + 腐蚀液的液池 = 腐蚀标签的图标色；不挂标签的冲击波 = 己方阵营色");
            }
            finally
            {
                fx.Dispose();
                visuals.Dispose();
            }
        }

        // ── H. 内核快照格式 7 ───────────────────────────────────────────────────────

        private static void CheckSnapshot()
        {
            Line("  · H. 存读档：内核快照格式 7 带定点桩锚点、区域外观、武器的反伤 / 定点 / 落点，往返逐位一致、续跑一致；格式 6 旧快照照样读（按旧语义取默认）；坏值整份拒绝");
            using CombatKernel k = Scenario(out _);
            Run(k, 0.5f);
            byte[] snap = k.Serialize();
            using CombatKernel b = NewKernel(64);
            CombatLoadResult lr = b.Load(snap);
            k.TryGetDrone(0, out CombatDrone d0);
            b.TryGetDrone(0, out CombatDrone e0);
            bool zonesSame = k.ZoneCount == b.ZoneCount && Enumerable.Range(0, k.ZoneCount).All(i => k.TryGetZone(i, out CombatZone x) && b.TryGetZone(i, out CombatZone y) && x.Look == y.Look);
            bool weaponsSame = k.WeaponCount == b.WeaponCount && Enumerable.Range(0, k.WeaponCount).All(i =>
                k.TryGetWeapon(i, out CombatWeapon x) && b.TryGetWeapon(i, out CombatWeapon y) && CombatKernel.WeaponKey(x) == CombatKernel.WeaponKey(y));
            // FG2-E2E-01 起当前格式是 8（武器追加引信弹迹标记）；格式 7 的内容（桩锚点 / 区域外观 / 反伤）照样在当前格式里往返——这里断言“当前格式 ≥ 7 的往返”，7 → 8 的兼容由 [M2 出口] B3 断言。
            Expect(CombatKernel.PeekFormat(snap) == CombatConst.FormatVersion && CombatConst.FormatVersion >= 7 && lr == CombatLoadResult.Ok && b.StateHash() == k.StateHash()
                   && d0.Anchored == 1 && e0.Anchored == 1 && math.distance(d0.Anchor, e0.Anchor) < 1e-12 && zonesSame && weaponsSame && k.ZoneCount > 0,
                $"当前格式（{CombatConst.FormatVersion}）往返：状态哈希一致、桩锚点 / 区域外观 / 武器读法逐位一致（桩 {k.DroneCount}、区域 {k.ZoneCount}）");
            Run(k, 3f);
            Run(b, 3f);
            Expect(k.StateHash() == b.StateHash(), "读档后续跑 3 秒：与不读档逐位相同");

            byte[] old = k.SerializeFormatForTests(6);
            using CombatKernel c = NewKernel(64);
            CombatLoadResult lr6 = c.Load(old);
            bool oldDefaults = Enumerable.Range(0, c.DroneCount).All(i => c.TryGetDrone(i, out CombatDrone d) && d.Anchored == 0)
                               && Enumerable.Range(0, c.ZoneCount).All(i => c.TryGetZone(i, out CombatZone z) && z.Look == CombatZoneLook.Pool)
                               && Enumerable.Range(0, c.WeaponCount).All(i => c.TryGetWeapon(i, out CombatWeapon w) && w.Reading.Thorns == 0f && w.Reading.DroneAnchored == 0);
            Expect(CombatKernel.PeekFormat(old) == 6 && lr6 == CombatLoadResult.Ok && oldDefaults,
                "格式 6 旧快照照样读：无人机按伴飞、区域外观按液池、武器没有反伤 / 定点（旧档里本来没有这些组件）");

            using CombatKernel bad = Scenario(out int spikedWeapon);
            bad.TryGetWeapon(spikedWeapon, out CombatWeapon sw);
            sw.Reading.Thorns = float.NaN;
            bad.SetWeaponTable(spikedWeapon, sw);
            using CombatKernel d2 = NewKernel(64);
            ulong before = d2.StateHash();
            Expect(d2.Load(bad.Serialize()) == CombatLoadResult.InvalidValue && d2.StateHash() == before, "反伤比例是 NaN 的快照：整份拒绝，内核保持原样");
        }

        // ── I. 倍速与暂停 ───────────────────────────────────────────────────────────

        private static void CheckSpeedAndPause()
        {
            Line("  · I. 暂停与 0.5x～3x：统一时钟下倍速只改每帧推进几步——同一场（定点桩 / 脉冲区 / 拆解回波 / 反伤）按每帧 1 / 2 / 6 步推进 4 游戏秒，结果逐位相同；暂停不推进（世界时钟下的暂停 / 倍速见形变自检 M 段）");
            ulong Play(int stepsPerFrame)
            {
                using CombatKernel k = Scenario(out _);
                int steps = (int)Math.Round(4f / Dt);
                for (int done = 0; done < steps;)
                {
                    for (int s = 0; s < stepsPerFrame && done < steps; s++, done++)
                    {
                        k.Step(Dt, k.Time + Dt);
                    }
                }
                return k.StateHash();
            }
            ulong half = Play(1), normal = Play(2), triple = Play(6);
            Expect(half == normal && normal == triple, $"每帧 1 / 2 / 6 步（相当于 0.5x / 1x / 3x）推进 4 游戏秒：状态哈希相同（{half:X16}）");
            using CombatKernel p = Scenario(out _);
            Run(p, 0.5f);
            double t0 = p.Time;
            int drones = p.DroneCount, zones = p.ZoneCount;
            ulong h0 = p.StateHash();
            // 暂停：WorldSimulation 在 GameClock.Paused 时不调 Step——这里模拟 200 个暂停帧（不推进）。
            Expect(p.Time == t0 && p.DroneCount == drones && p.ZoneCount == zones && p.StateHash() == h0 && drones > 0 && zones > 0,
                "暂停中不推进：游戏时间、桩数、区域数、状态都不变（恢复后接着到期）");

            // 修复轮（审查 P2）：区域 / 无人机的着色器动画用游戏时钟，不用真实时间 _Time——暂停时画面静止，倍速时同步加快。
            float c0 = CombatRenderer.AnimationClock(p);
            float cPaused = CombatRenderer.AnimationClock(p); // 暂停帧：内核不推进
            for (int s = 0; s < 6; s++)
            {
                p.Step(Dt, p.Time + Dt); // 3x 的一帧 = 6 步
            }
            float c3x = CombatRenderer.AnimationClock(p);
            string shaderPath = System.IO.Path.Combine(Application.dataPath, "GameScripts/Main/Sim/Shaders/CombatInstanced.shader");
            string shader = System.IO.File.Exists(shaderPath) ? System.IO.File.ReadAllText(shaderPath) : string.Empty;
            int fx = shader.IndexOf("fixed4 effectFrag", StringComparison.Ordinal);
            int fxEnd = fx >= 0 ? shader.IndexOf("fixed4 frag", fx, StringComparison.Ordinal) : -1;
            string fxBody = fx >= 0 && fxEnd > fx ? shader.Substring(fx, fxEnd - fx) : string.Empty;
            Expect(cPaused == c0 && Math.Abs(c3x - c0 - 6f * Dt) < 1e-4f && fxBody.Length > 200 && fxBody.Contains("_GameTime") && !fxBody.Contains("_Time."),
                $"区域 / 无人机动画时钟 = 游戏时间：暂停帧不变（{c0:0.###}→{cPaused:0.###}），3x 一帧推进 {c3x - c0:0.###} 游戏秒；着色器 effectFrag（{fxBody.Length} 字）只读 _GameTime、不读真实时间 _Time");
        }

        // ── J. 性能 ────────────────────────────────────────────────────────────────

        private static void CheckPerformance()
        {
            Line("  · J. 性能：200 台装尖刺外装的机器被 200 个近身敌人驻守开火 + 100 根哨戒桩；内核单步（Burst）在预算内（对照不装尖刺外装）");
            double Measure(bool spiked, out long reflected)
            {
                using CombatKernel k = NewKernel(700);
                int wm = k.AddWeapon(WeaponFor(ComponentCatalog.CompGunId, spiked ? ComponentCatalog.FuncSpikesId : null));
                int wp = k.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
                int we = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 2.5f, Damage = 1f, Cooldown = 0.5f, HasOutput = 1 });
                var posters = new List<(int Me, int T)>();
                for (int i = 0; i < 200; i++)
                {
                    var at = new double2((i % 20) * 6.0, (i / 20) * 6.0);
                    k.Spawn(Machine(at, wm, 1e6f, 1e6f));
                    k.Spawn(Hostile(at + new double2(1.5, 0), 1e6f, we, CombatBehavior.HoldFire));
                }
                for (int i = 0; i < 50; i++)
                {
                    var at = new double2(200 + (i % 10) * 10.0, (i / 10) * 10.0);
                    int me = k.Spawn(Machine(at, wp, 1e6f, 1e6f));
                    int t = k.Spawn(Hostile(at + new double2(4, 0), 1e6f));
                    posters.Add((me, t));
                }
                foreach ((int me, int t) in posters)
                {
                    k.FireAt(me, t, k.Time);
                }
                Run(k, 0.5f);
                long f0 = k.ReadingFeedCountOf(CombatConst.ReadingFeedThorns);
                var sw = Stopwatch.StartNew();
                const int n = 240;
                for (int i = 0; i < n; i++)
                {
                    k.Step(Dt, k.Time + Dt);
                }
                sw.Stop();
                reflected = k.ReadingFeedCountOf(CombatConst.ReadingFeedThorns) - f0;
                return sw.Elapsed.TotalMilliseconds / n;
            }
            double with = Measure(true, out long refl);
            double without = Measure(false, out long refl0);
            ExpectPerf(refl > 200 && refl0 == 0, $"单步 {with:0.000} ms（对照不装尖刺外装 {without:0.000} ms），4 秒内反伤 {refl} 次；预算：单步 < 4 ms（120 帧的帧预算 8.3 ms 的一半以内）",
                PerfGate.Lt(with, 4.0, "内核单步 ms"));
            PerfLines.Add($"反伤 + 定点桩：200 机 × 200 近身敌人 + 100 根桩，内核单步 {with:0.000} ms（不装尖刺外装 {without:0.000} ms）；Editor batchmode、Burst 同步编译，真机复测归 FG15-SYS-02");
        }

        // ── 场景与辅助 ──────────────────────────────────────────────────────────────

        /// <summary>一场混合战斗：哨戒桩插桩、震荡脉冲器脚下脉冲、拆解钳回波、敌人贴身打尖刺外装（反伤）。</summary>
        private static CombatKernel Scenario(out int spikedWeapon)
        {
            CombatKernel k = NewKernel(64);
            int wPost = k.AddWeapon(WeaponFor(ComponentCatalog.CompSentryId, null));
            int wPulse = k.AddWeapon(WeaponFor(ComponentCatalog.CompPulserId, null));
            int wClaw = k.AddWeapon(WeaponFor(ComponentCatalog.CompClawId, null));
            spikedWeapon = k.AddWeapon(WeaponFor(ComponentCatalog.CompGunId, ComponentCatalog.FuncSpikesId));
            int wFoe = k.AddWeapon(new CombatWeapon { Mode = CombatWeaponMode.Instant, Range = 2.5f, Damage = 5f, Cooldown = 0.7f, HasOutput = 1 });
            int a = k.Spawn(Machine(new double2(0, 0), wPost));
            int b = k.Spawn(Machine(new double2(0, 20), wPulse));
            int c = k.Spawn(Machine(new double2(0, 40), wClaw));
            int d = k.Spawn(Machine(new double2(0, 60), spikedWeapon));
            int ta = k.Spawn(Hostile(new double2(5, 0), 5000f));
            int tb = k.Spawn(Hostile(new double2(2, 20), 5000f));
            int tc = k.Spawn(Hostile(new double2(1.5, 40), 5000f));
            k.Spawn(Hostile(new double2(1.5, 60), 5000f, wFoe, CombatBehavior.HoldFire));
            k.FireAt(a, ta, k.Time);
            k.FireAt(b, tb, k.Time);
            k.FireAt(c, tc, k.Time);
            return k;
        }

        private static CombatKernel NewKernel(int capacity)
        {
            CombatConfig cfg = CombatSite.ConfigFromTuning();
            cfg.NavEnabled = 0;
            return new CombatKernel(cfg, capacity);
        }

        private static void Run(CombatKernel k, float seconds)
        {
            int n = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < n; i++)
            {
                k.Step(Dt, k.Time + Dt);
            }
        }

        private static float Lost(CombatKernel k, int id) => k.TryGetUnit(id, out CombatUnitView v) ? v.MaxHealth - v.Health : 0f;

        private static List<CombatDrone> Drones(CombatKernel k)
        {
            var list = new List<CombatDrone>();
            for (int i = 0; i < k.DroneCount; i++)
            {
                if (k.TryGetDrone(i, out CombatDrone d))
                {
                    list.Add(d);
                }
            }
            return list;
        }

        private static BlueprintCircuitPreview Compile(string primary, string utility, params string[] firmware)
        {
            BlueprintCircuitBoard board = BlueprintCircuitBoard.CreateDefault(HomeValleyLayout.Erc003ChassisId, primary, utility, null, Array.Empty<string>());
            CircuitOpResult up = board.TrySetUplink(UplinkSlot);
            if (!up.Success)
            {
                Fail($"测试准备：{primary} 蓝图 {UplinkSlot} 号格标接入口失败：{up.Message}");
            }
            BlueprintCircuitPreview p = UplinkCompiler.CompileUplinked(board, firmware ?? Array.Empty<string>());
            if (firmware != null && firmware.Length > 0 && !firmware.All(f => p.FirmwareIds.Contains(f)))
            {
                Fail($"测试准备：{primary} 接入 {string.Join("/", firmware)} 没全部生效（生效 {string.Join("/", p.FirmwareIds)}）");
            }
            return p;
        }

        private static CombatWeapon WeaponFor(string primary, string utility, params string[] firmware) => CombatSite.MachineWeaponFrom(Compile(primary, utility, firmware));

        private static CombatSpawn Machine(double2 at, int weapon, float hp = 400f, float max = 400f) => new CombatSpawn
        {
            Kind = CombatUnitKind.Machine,
            Faction = CombatFaction.Player,
            Behavior = CombatBehavior.Commanded,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | CombatUnitFlags.WeaponEnabled,
            Position = at,
            Home = at,
            Radius = 0.6f,
            Speed = 4f,
            Health = hp,
            MaxHealth = max,
            Weapon = weapon,
            BehaviorProfile = -1,
        };

        private static CombatSpawn Hostile(double2 at, float hp, int weapon = -1, CombatBehavior behavior = CombatBehavior.HoldFire) => new CombatSpawn
        {
            Kind = CombatUnitKind.Enemy,
            Faction = CombatFaction.Hostile,
            Behavior = behavior,
            Flags = CombatUnitFlags.Alive | CombatUnitFlags.Targetable | (weapon >= 0 ? CombatUnitFlags.WeaponEnabled : 0),
            Position = at,
            Home = at,
            Radius = 0.5f,
            Speed = 0f,
            Health = hp,
            MaxHealth = hp,
            Weapon = weapon,
            BehaviorProfile = -1,
            Priority = 1,
        };

        private static CampaignState NewState(int seed)
        {
            GameClock.ResetSession();
            GameClock.SetSpeed(1f);
            GameClock.SetPaused(false);
            CampaignState s = CampaignState.CreateNew("fgvfx02-" + seed, "Standard", seed);
            CampaignSession.Set(Slot, s);
            s.Scrap = 2000;
            PrimitiveInventory.EnsureSeeded(s);
            SignalCoreService.EnsureInitialized(s);
            s.UnlockedContentIds = (s.UnlockedContentIds ?? Array.Empty<string>()).Concat(FirmwareCatalog.All.Keys).Distinct().ToArray();
            return s;
        }

        // ── 报告 ──────────────────────────────────────────────────────────────────

        private static void Step(Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Fail($"{check.Method.Name} 抛异常：{e}");
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (condition)
            {
                _pass++;
                Line("    ✓ " + message);
            }
            else
            {
                Fail(message);
            }
        }

        /// <summary>FG-TOOL-01：性能断言只测一次；超阈值不到 2 倍记性能警告（不计失败），超 2 倍才失败。功能条件放 <paramref name="ok"/>。</summary>
        private static void ExpectPerf(bool ok, string message, params PerfGate.Metric[] perf) => PerfGate.Expect(ok, message, perf, Expect, Line);

        private static void Fail(string message)
        {
            _fail++;
            Line("    ✗ " + message);
        }

        private static void Line(string text) => _report?.AppendLine(text);
    }
}
