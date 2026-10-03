using System;
using System.Collections.Generic;
using UnityEngine;

namespace BinGames.TerrainVisual
{
    [CreateAssetMenu(menuName = "地球归还/地形/地貌配置")]
    public sealed class TerrainProfile : ScriptableObject
    {
        [Header("地貌（米）")]
        public int Seed = 41729;
        [Range(8, 64)] public int ChunkSize = 32;
        [Range(2, 8)] public int ViewRadius = 6;
        [Range(0, 30)] public float Relief = 14;
        [Range(40, 220)] public float LandformScale = 110;
        [Range(0, 60)] public float HomeRadius = 25;
        [Range(2, 16)] public float RiverWidth = 6;
        public float WaterLevel = -1.1f;
        [Header("环境色板（美术规则 06）")]
        public Color Soil = new Color32(107, 90, 71, 255);
        public Color Rock = new Color32(122, 119, 113, 255);
        public Color Sand = new Color32(144, 128, 101, 255);
        public Color Pollution = new Color32(138, 139, 90, 255);
        public Color RestoredSoil = new Color32(94, 74, 54, 255);
        public Color RestoredGrass = new Color32(110,139,61,255);
        [Range(0,1)] public float Restoration=.65f;
        public Color Water = new Color32(78, 102, 112, 255);
        [Range(0, 1)] public float PollutionAmount = .22f;
        [Range(0, 1)] public float PropDensity = .55f;
        public TerrainModuleLibrary Library;
        public TerrainDetailKit DetailKit;
        public Material GroundMaterial;
        public Material WaterMaterial;
        public Material PropMaterial;
        public Mesh RockMesh;
        public Mesh RockVariantMesh;
        public Mesh RubbleMesh;
        public Mesh DryTreeMesh;

        public TerrainParameters Snapshot(int seed) => new TerrainParameters(this, seed);
        private void OnValidate(){ChunkSize=Mathf.Clamp(Mathf.RoundToInt(ChunkSize/4f)*4,8,64);}
    }

    // Immutable copy: worker threads never access a ScriptableObject or scene object.
    public sealed class TerrainParameters
    {
        public readonly int Seed, ChunkSize;
        public readonly float Relief, Scale, HomeRadius, RiverWidth, WaterLevel, PollutionAmount,Restoration;
        public readonly Color Soil, Rock, Sand, Pollution, RestoredSoil,RestoredGrass;
        public TerrainParameters(TerrainProfile p, int seed)
        {
            Seed = seed; ChunkSize = p.ChunkSize; Relief = p.Relief; Scale = p.LandformScale;
            HomeRadius = p.HomeRadius; RiverWidth = p.RiverWidth; WaterLevel = p.WaterLevel;
            PollutionAmount = p.PollutionAmount;
            Soil = p.Soil; Rock = p.Rock; Sand = p.Sand; Pollution = p.Pollution;
            RestoredSoil = p.RestoredSoil;
            RestoredGrass=p.RestoredGrass;Restoration=p.Restoration;
        }
    }

    [Serializable]
    public sealed class TerrainModule
    {
        public string Id = "module";
        public Mesh Mesh;
        [Min(.001f)] public float Weight = 1;
        [Tooltip("允许的朝向；每一项按顺时针旋转 90 度")]
        public bool[] Rotations = { true, true, true, true };
        [Tooltip("0 = 封闭/无连接。相邻两边接口编号必须相同。")]
        public int North, East, South, West;
        public float HeightOffset;
        [Range(0, 60)] public float MaxSlope = 15;
        public Color Tint = new Color32(154, 150, 140, 255);
        public bool Empty;
    }

    public readonly struct ModuleVariant
    {
        public readonly int Module, Rotation;
        public readonly float Weight;
        public readonly int N, E, S, W;
        public ModuleVariant(int module, int rotation, float weight, int n, int e, int s, int w)
        {
            Module = module; Rotation = rotation; Weight = weight;
            int[] ports = { n, e, s, w };
            N = ports[(4 - rotation) % 4]; E = ports[(5 - rotation) % 4];
            S = ports[(6 - rotation) % 4]; W = ports[(7 - rotation) % 4];
        }
        public int Port(int d) => d == 0 ? N : d == 1 ? E : d == 2 ? S : W;
    }
}
