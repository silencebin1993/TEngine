using System.Collections.Generic;
using GameConfig;
using GameConfig.fp;

namespace GameLogic.Config
{
    /// <summary>
    /// First Playable 配置访问入口。业务优先通过 FPTuning / FPModuleTable 门面读数。
    /// </summary>
    public static class FPConfig
    {
        public const int DefaultRowId = 1;

        public static Tables Tables => ConfigSystem.Instance.Tables;

        public static Global Global => Tables.TbGlobal.Get(DefaultRowId);

        public static CellArena CellArena => Tables.TbCellArena.Get(DefaultRowId);

        public static CreatureArena CreatureArena => Tables.TbCreatureArena.Get(DefaultRowId);

        public static PlayerForm CellForm => Tables.TbPlayerForm.Get(EFormId.Cell);

        public static PlayerForm CreatureForm => Tables.TbPlayerForm.Get(EFormId.Creature);

        public static Food FoodA => Tables.TbFood.Get(EFoodId.A);

        public static Food FoodB => Tables.TbFood.Get(EFoodId.B);

        public static Enemy Threat => Tables.TbEnemy.Get(EEnemyId.Threat);

        public static Enemy Herbivore => Tables.TbEnemy.Get(EEnemyId.Herbivore);

        public static Enemy Predator => Tables.TbEnemy.Get(EEnemyId.Predator);

        public static Enemy Elite => Tables.TbEnemy.Get(EEnemyId.Elite);

        public static EnemySkill GetSkill(int skillId)
        {
            return skillId <= 0 ? null : Tables.TbEnemySkill.GetOrDefault(skillId);
        }

        public static MicroChoice GetMicroChoice(EMicroChoice id)
        {
            return id == EMicroChoice.None ? null : Tables.TbMicroChoice.GetOrDefault(id);
        }

        public static Module GetModule(EModuleId id)
        {
            return id == EModuleId.None ? null : Tables.TbModule.GetOrDefault(id);
        }

        public static IReadOnlyList<Module> Modules => Tables.TbModule.DataList;

        public static IReadOnlyList<Wave> Waves => Tables.TbWave.DataList;

        public static float[] BuildWaveThreatMultipliers()
        {
            var waves = Tables.TbWave.DataList;
            var ordered = new List<Wave>(waves.Count);
            for (int i = 0; i < waves.Count; i++)
            {
                ordered.Add(waves[i]);
            }

            ordered.Sort((a, b) => a.WaveIndex.CompareTo(b.WaveIndex));
            var result = new float[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                result[i] = ordered[i].ThreatMul;
            }

            return result;
        }
    }
}
