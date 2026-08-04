#!/usr/bin/env python3
"""Create First Playable Luban enums/tables/rows via luban_helper."""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA_DIR = ROOT / "Datas"
HELPER = (
    ROOT.parents[1]
    / "UnityProject"
    / ".claude"
    / "skills"
    / "luban-dev"
    / "scripts"
    / "luban_helper.py"
)
TMP = ROOT / "scripts" / "_tmp_rows.json"


def run(*args: str, allow_fail: bool = False) -> int:
    cmd = [sys.executable, str(HELPER), "--data-dir", str(DATA_DIR), *args]
    print("+", " ".join(args), flush=True)
    r = subprocess.run(
        cmd,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    out = (r.stdout or "").strip()
    err = (r.stderr or "").strip()
    if out:
        sys.stdout.buffer.write((out + "\n").encode("utf-8", errors="replace"))
        sys.stdout.buffer.flush()
    if r.returncode != 0:
        if allow_fail:
            if err:
                sys.stderr.buffer.write((err + "\n").encode("utf-8", errors="replace"))
            return r.returncode
        sys.stderr.buffer.write((err + "\n").encode("utf-8", errors="replace"))
        raise SystemExit(r.returncode)
    return 0


def write_json(data) -> str:
    TMP.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    return str(TMP)


def import_rows(table: str, data) -> None:
    path = write_json(data)
    code = run("import", table, path, "--mode", "replace", allow_fail=True)
    if code != 0:
        run("row", "add", table, "--file", path)


def main() -> None:
    if not HELPER.is_file():
        raise SystemExit(f"missing helper: {HELPER}")

    for args in [
        (
            "enum",
            "add",
            "fp.ERoute",
            "--values",
            "None=0:无,Devour=1:吞噬扩张型,Specialist=2:功能特化型,Tech=3:科技统治型",
            "--comment",
            "原型路线",
        ),
        (
            "enum",
            "add",
            "fp.EFormId",
            "--values",
            "Cell=1:细胞,Creature=2:生物",
            "--comment",
            "玩家形态",
        ),
        (
            "enum",
            "add",
            "fp.EMicroChoice",
            "--values",
            "None=0:无,Gluttony=1:贪食囊,Phototaxis=2:趋光纤毛,Metabolic=3:代谢泡",
            "--comment",
            "细胞微选择",
        ),
        (
            "enum",
            "add",
            "fp.EModuleId",
            "--values",
            "None=0:无,Maw=1:吞噬口器,ThickWall=2:厚壁细胞层,Cilia=3:感知纤毛,Nerve=4:协同神经束,Zap=5:原始放电囊,Conduit=6:能量导流组织",
            "--comment",
            "构筑模块",
        ),
        (
            "enum",
            "add",
            "fp.EFoodId",
            "--values",
            "A=1:A型食物,B=2:B型食物",
            "--comment",
            "食物类型",
        ),
        (
            "enum",
            "add",
            "fp.EEnemyId",
            "--values",
            "Threat=1:细胞威胁,Herbivore=2:草食虫,Predator=3:掠食虫,Elite=4:精英",
            "--comment",
            "敌人类型",
        ),
    ]:
        run(*args, allow_fail=True)

    for targs in [
        (
            "table",
            "add",
            "fp.TbGlobal",
            "--mode",
            "one",
            "--vertical",
            "--comment",
            "FP全局公式与门槛",
            "--fields",
            ",".join(
                [
                    "engulfRatio:float:吞噬阈值k",
                    "volumeGainRatio:float:体积增长比",
                    "speedVolumePenalty:float:体积移速惩罚",
                    "speedFloorRatio:float:移速下限比",
                    "contactDamageInterval:float:接触伤害间隔",
                    "evoPointThreshold:int:进化点门槛",
                    "waveInterval:float:波次间隔秒",
                    "waveCount:int:波次数",
                    "enemyBaseAggroRange:float:敌人基础察觉范围",
                    "zapDamage:float:放电伤害",
                    "zapRange:float:放电射程",
                    "zapCooldown:float:放电冷却",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbCellArena",
            "--mode",
            "one",
            "--vertical",
            "--comment",
            "细胞阶段场地与时间轴",
            "--fields",
            ",".join(
                [
                    "arenaHalfSize:float:半边长",
                    "foodConcurrent:int:同时食物数",
                    "foodRespawnDelay:float:食物补充间隔",
                    "foodBRatio:float:B型占比",
                    "threatBaseCount:int:首波威胁数",
                    "hazardCount:int:危险区数量",
                    "hazardRadius:float:危险区半径",
                    "hazardDamagePerSecond:float:危险区DPS",
                    "microChoiceTime:float:微选择触发时间",
                    "forceEvolveTime:float:强制进化时间",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbCreatureArena",
            "--mode",
            "one",
            "--vertical",
            "--comment",
            "生物阶段场地与时间轴",
            "--fields",
            ",".join(
                [
                    "exploreEnd:float:探索阶段结束",
                    "pressureEnd:float:压力阶段结束",
                    "arenaHalfSize:float:半边长",
                    "enemyRespawnDelay:float:刷怪间隔",
                    "enemyCapPhase1:int:阶段1敌人数上限",
                    "enemyCapPhase2:int:阶段2敌人数上限",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbPlayerForm",
            "--index",
            "id",
            "--comment",
            "玩家形态基础面板",
            "--fields",
            ",".join(
                [
                    "id:fp.EFormId:形态ID",
                    "maxHp:float:最大生命",
                    "startVolume:float:初始体积",
                    "maxVolume:float:体积上限",
                    "baseSpeed:float:基础移速",
                    "accel:float:加速度",
                    "drag:float:阻力",
                    "meleeDamage:float:近战伤害",
                    "meleeInterval:float:近战间隔",
                    "meleeRange:float:近战范围",
                    "staminaMax:float:体力上限",
                    "dashCost:float:冲刺消耗",
                    "staminaRegen:float:体力回复",
                    "dashInvulnTime:float:冲刺无敌时长",
                    "staminaRegenDelay:float:体力回复延迟",
                    "dashSpeedMultiplier:float:冲刺移速倍率",
                    "dashDuration:float:冲刺持续",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbFood",
            "--index",
            "id",
            "--comment",
            "食物配置",
            "--fields",
            ",".join(
                [
                    "id:fp.EFoodId:食物ID",
                    "name:string:名称",
                    "volume:float:体积",
                    "evoPoint:int:进化点",
                    "biomass:int:生物质",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbEnemySkill",
            "--index",
            "id",
            "--comment",
            "敌人技能",
            "--fields",
            ",".join(
                [
                    "id:int:技能ID",
                    "name:string:名称",
                    "damage:float:伤害",
                    "cooldown:float:冷却",
                    "range:float:射程",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbEnemy",
            "--index",
            "id",
            "--comment",
            "敌人配置",
            "--fields",
            ",".join(
                [
                    "id:fp.EEnemyId:敌人ID",
                    "name:string:名称",
                    "volume:float:体积",
                    "hp:float:生命",
                    "contactDamage:float:接触伤害",
                    "speed:float:移速",
                    "evoPoint:int:被吞噬进化点",
                    "biomass:int:被吞噬生物质",
                    "skillId:int:技能ID",
                    "tags:string:扩展标签预留",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbWave",
            "--index",
            "waveIndex",
            "--comment",
            "细胞波次",
            "--fields",
            ",".join(
                [
                    "waveIndex:int:波次序号",
                    "triggerTime:float:触发时间",
                    "threatMul:float:威胁数量倍率",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbMicroChoice",
            "--index",
            "id",
            "--comment",
            "细胞微选择",
            "--fields",
            ",".join(
                [
                    "id:fp.EMicroChoice:微选择ID",
                    "name:string:名称",
                    "route:fp.ERoute:路线",
                    "desc:string:描述",
                    "biomassBonus:float:生物质加成",
                    "speedBonus:float:移速加成",
                    "healPerEat:float:每次吞噬回血",
                ]
            ),
        ),
        (
            "table",
            "add",
            "fp.TbModule",
            "--index",
            "id",
            "--comment",
            "构筑模块",
            "--fields",
            ",".join(
                [
                    "id:fp.EModuleId:模块ID",
                    "name:string:名称",
                    "route:fp.ERoute:路线",
                    "price:int:价格",
                    "desc:string:描述",
                    "maxHpFlat:float:生命加算",
                    "speedMulDelta:float:移速增量",
                    "meleeMulDelta:float:近战增量",
                    "staminaMaxFlat:float:体力上限加算",
                    "staminaCostMulDelta:float:体力消耗增量",
                    "staminaRegenMulDelta:float:体力回复增量",
                    "dashInvulnFlat:float:无敌帧加算",
                    "aggroMulDelta:float:察觉范围增量",
                    "killHeal:float:击杀回血",
                    "unlockAbilityId:int:解锁技能ID",
                    "tags:string:扩展标签预留",
                ]
            ),
        ),
    ]:
        run(*targs, allow_fail=True)

    import_rows(
        "fp.TbGlobal",
        {
            "engulfRatio": 1.15,
            "volumeGainRatio": 0.30,
            "speedVolumePenalty": 0.18,
            "speedFloorRatio": 0.55,
            "contactDamageInterval": 0.8,
            "evoPointThreshold": 100,
            "waveInterval": 270,
            "waveCount": 3,
            "enemyBaseAggroRange": 12,
            "zapDamage": 25,
            "zapRange": 3.5,
            "zapCooldown": 1.2,
        },
    )
    import_rows(
        "fp.TbCellArena",
        {
            "arenaHalfSize": 28,
            "foodConcurrent": 6,
            "foodRespawnDelay": 4.5,
            "foodBRatio": 0.3,
            "threatBaseCount": 5,
            "hazardCount": 4,
            "hazardRadius": 3.5,
            "hazardDamagePerSecond": 6,
            "microChoiceTime": 270,
            "forceEvolveTime": 810,
        },
    )
    import_rows(
        "fp.TbCreatureArena",
        {
            "exploreEnd": 240,
            "pressureEnd": 480,
            "arenaHalfSize": 22,
            "enemyRespawnDelay": 6,
            "enemyCapPhase1": 5,
            "enemyCapPhase2": 9,
        },
    )
    import_rows(
        "fp.TbPlayerForm",
        [
            {
                "id": "Cell",
                "maxHp": 60,
                "startVolume": 1.0,
                "maxVolume": 3.0,
                "baseSpeed": 4.0,
                "accel": 12,
                "drag": 4,
                "meleeDamage": 0,
                "meleeInterval": 0,
                "meleeRange": 0,
                "staminaMax": 0,
                "dashCost": 0,
                "staminaRegen": 0,
                "dashInvulnTime": 0,
                "staminaRegenDelay": 0,
                "dashSpeedMultiplier": 0,
                "dashDuration": 0,
            },
            {
                "id": "Creature",
                "maxHp": 140,
                "startVolume": 0,
                "maxVolume": 0,
                "baseSpeed": 5.0,
                "accel": 0,
                "drag": 0,
                "meleeDamage": 15,
                "meleeInterval": 0.6,
                "meleeRange": 1.5,
                "staminaMax": 100,
                "dashCost": 30,
                "staminaRegen": 12,
                "dashInvulnTime": 0.3,
                "staminaRegenDelay": 1.0,
                "dashSpeedMultiplier": 3.2,
                "dashDuration": 0.18,
            },
        ],
    )
    import_rows(
        "fp.TbFood",
        [
            {"id": "A", "name": "A型食物", "volume": 0.5, "evoPoint": 3, "biomass": 8},
            {"id": "B", "name": "B型食物", "volume": 1.2, "evoPoint": 7, "biomass": 18},
        ],
    )
    import_rows(
        "fp.TbEnemySkill",
        [
            {"id": 1001, "name": "精英冲撞", "damage": 38, "cooldown": 7, "range": 0},
            {"id": 2001, "name": "原始放电", "damage": 25, "cooldown": 1.2, "range": 3.5},
        ],
    )
    import_rows(
        "fp.TbEnemy",
        [
            {
                "id": "Threat",
                "name": "细胞威胁",
                "volume": 1.8,
                "hp": 0,
                "contactDamage": 12,
                "speed": 3.2,
                "evoPoint": 6,
                "biomass": 14,
                "skillId": 0,
                "tags": "",
            },
            {
                "id": "Herbivore",
                "name": "草食虫",
                "volume": 0,
                "hp": 40,
                "contactDamage": 8,
                "speed": 3.0,
                "evoPoint": 0,
                "biomass": 0,
                "skillId": 0,
                "tags": "",
            },
            {
                "id": "Predator",
                "name": "掠食虫",
                "volume": 0,
                "hp": 70,
                "contactDamage": 14,
                "speed": 4.5,
                "evoPoint": 0,
                "biomass": 0,
                "skillId": 0,
                "tags": "",
            },
            {
                "id": "Elite",
                "name": "精英",
                "volume": 0,
                "hp": 250,
                "contactDamage": 22,
                "speed": 4.2,
                "evoPoint": 0,
                "biomass": 0,
                "skillId": 1001,
                "tags": "",
            },
        ],
    )
    import_rows(
        "fp.TbWave",
        [
            {"waveIndex": 1, "triggerTime": 0, "threatMul": 1},
            {"waveIndex": 2, "triggerTime": 270, "threatMul": 2},
            {"waveIndex": 3, "triggerTime": 540, "threatMul": 4},
        ],
    )
    import_rows(
        "fp.TbMicroChoice",
        [
            {
                "id": "Gluttony",
                "name": "贪食囊",
                "route": "Devour",
                "desc": "吞噬获得生物质 +25%",
                "biomassBonus": 0.25,
                "speedBonus": 0,
                "healPerEat": 0,
            },
            {
                "id": "Phototaxis",
                "name": "趋光纤毛",
                "route": "Specialist",
                "desc": "移速 +20%",
                "biomassBonus": 0,
                "speedBonus": 0.20,
                "healPerEat": 0,
            },
            {
                "id": "Metabolic",
                "name": "代谢泡",
                "route": "Tech",
                "desc": "每次吞噬回复 3 生命值",
                "biomassBonus": 0,
                "speedBonus": 0,
                "healPerEat": 3,
            },
        ],
    )
    import_rows(
        "fp.TbModule",
        [
            {
                "id": "Maw",
                "name": "吞噬口器",
                "route": "Devour",
                "price": 120,
                "desc": "近战伤害 +60%，击杀普通敌人回血 8",
                "maxHpFlat": 0,
                "speedMulDelta": 0,
                "meleeMulDelta": 0.60,
                "staminaMaxFlat": 0,
                "staminaCostMulDelta": 0,
                "staminaRegenMulDelta": 0,
                "dashInvulnFlat": 0,
                "aggroMulDelta": 0,
                "killHeal": 8,
                "unlockAbilityId": 0,
                "tags": "",
            },
            {
                "id": "ThickWall",
                "name": "厚壁细胞层",
                "route": "Devour",
                "price": 140,
                "desc": "最大生命值 +50，移速 -8%",
                "maxHpFlat": 50,
                "speedMulDelta": -0.08,
                "meleeMulDelta": 0,
                "staminaMaxFlat": 0,
                "staminaCostMulDelta": 0,
                "staminaRegenMulDelta": 0,
                "dashInvulnFlat": 0,
                "aggroMulDelta": 0,
                "killHeal": 0,
                "unlockAbilityId": 0,
                "tags": "",
            },
            {
                "id": "Cilia",
                "name": "感知纤毛",
                "route": "Specialist",
                "price": 150,
                "desc": "移速 +28%，敌人察觉范围 -35%",
                "maxHpFlat": 0,
                "speedMulDelta": 0.28,
                "meleeMulDelta": 0,
                "staminaMaxFlat": 0,
                "staminaCostMulDelta": 0,
                "staminaRegenMulDelta": 0,
                "dashInvulnFlat": 0,
                "aggroMulDelta": -0.35,
                "killHeal": 0,
                "unlockAbilityId": 0,
                "tags": "",
            },
            {
                "id": "Nerve",
                "name": "协同神经束",
                "route": "Specialist",
                "price": 160,
                "desc": "冲刺无敌帧 +0.15 秒，体力消耗 -30%",
                "maxHpFlat": 0,
                "speedMulDelta": 0,
                "meleeMulDelta": 0,
                "staminaMaxFlat": 0,
                "staminaCostMulDelta": -0.30,
                "staminaRegenMulDelta": 0,
                "dashInvulnFlat": 0.15,
                "aggroMulDelta": 0,
                "killHeal": 0,
                "unlockAbilityId": 0,
                "tags": "",
            },
            {
                "id": "Zap",
                "name": "原始放电囊",
                "route": "Tech",
                "price": 190,
                "desc": "解锁远程放电：伤害 25，射程 3.5 米，冷却 1.2 秒",
                "maxHpFlat": 0,
                "speedMulDelta": 0,
                "meleeMulDelta": 0,
                "staminaMaxFlat": 0,
                "staminaCostMulDelta": 0,
                "staminaRegenMulDelta": 0,
                "dashInvulnFlat": 0,
                "aggroMulDelta": 0,
                "killHeal": 0,
                "unlockAbilityId": 2001,
                "tags": "",
            },
            {
                "id": "Conduit",
                "name": "能量导流组织",
                "route": "Tech",
                "price": 170,
                "desc": "体力上限 +40，体力回复 +50%",
                "maxHpFlat": 0,
                "speedMulDelta": 0,
                "meleeMulDelta": 0,
                "staminaMaxFlat": 40,
                "staminaCostMulDelta": 0,
                "staminaRegenMulDelta": 0.50,
                "dashInvulnFlat": 0,
                "aggroMulDelta": 0,
                "killHeal": 0,
                "unlockAbilityId": 0,
                "tags": "",
            },
        ],
    )

    run("validate", allow_fail=True)
    print("FP tables setup done.")


if __name__ == "__main__":
    main()
