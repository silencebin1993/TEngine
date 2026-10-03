using System;
using UnityEngine;

namespace BinGames.TerrainVisual
{
    public readonly struct TerrainSample
    {
        public readonly float Height;
        public readonly Color Color;
        public TerrainSample(float height, Color color) { Height = height; Color = color; }
    }

    // A production adapter supplies an immutable world snapshot through this interface.
    // The preview field below is deliberately separate from the campaign's logical generator.
    public interface ITerrainField
    {
        TerrainSample Sample(double x, double z);
        Vector3 Normal(double x, double z);
    }

    public sealed class TerrainField : ITerrainField
    {
        public readonly TerrainParameters Parameters;
        public TerrainField(TerrainParameters parameters) { Parameters = parameters; }

        public static uint Hash(long x, long z, int seed)
        {
            unchecked
            {
                uint v = (uint)x * 0x9e3779b9u ^ (uint)(x >> 32) * 0x85ebca6bu;
                v ^= (uint)z * 0xc2b2ae35u ^ (uint)(z >> 32) * 0x27d4eb2du ^ (uint)seed;
                v ^= v >> 16; v *= 0x7feb352du; v ^= v >> 15; v *= 0x846ca68bu;
                return v ^ (v >> 16);
            }
        }
        public static float Random01(long x, long z, int seed) => (Hash(x, z, seed) & 0xffffff) / 16777216f;
        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        public static float Noise(double x, double z, int seed)
        {
            long ix = (long)Math.Floor(x), iz = (long)Math.Floor(z);
            double tx = Fade(x - ix), tz = Fade(z - iz);
            double a = Random01(ix, iz, seed), b = Random01(ix + 1, iz, seed);
            double c = Random01(ix, iz + 1, seed), d = Random01(ix + 1, iz + 1, seed);
            return (float)((a + (b - a) * tx) * (1 - tz) + (c + (d - c) * tx) * tz);
        }
        private float Fbm(double x, double z, float scale, int salt)
        {
            float n = 0, weight = .57f;
            for (int i = 0; i < 4; i++)
            {
                n += (Noise(x / scale, z / scale, Parameters.Seed + salt + i * 701) * 2 - 1) * weight;
                scale *= .49f; weight *= .46f;
            }
            return n;
        }
        public float RiverDistance(double x, double z)
        {
            double center = 62 + 10 * Math.Sin(z * .014 + Parameters.Seed * .0001)
                + 8 * (Noise(z / 170, 3, Parameters.Seed + 45) - .5);
            return (float)Math.Abs(x - center);
        }
        private float RawHeight(double x, double z)
        {
            float macro = Fbm(x, z, Parameters.Scale, 13);
            float ridge = 1 - Mathf.Abs(Fbm(x + 350, z - 190, Parameters.Scale * .58f, 104));
            float upland = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(-.13f, .46f, macro));
            float h = 1.8f + macro * 2 + upland * Parameters.Relief * (ridge * .75f + .25f);
            // Broad eroded escarpments frame the valley; their bends vary with the seed.
            double warp=18*(Noise(z/120,7,Parameters.Seed+518)-.5);
            float escarpment=Mathf.SmoothStep(0,1,Mathf.InverseLerp(46,82,(float)Math.Abs(x+18+warp)));
            h+=escarpment*Parameters.Relief*(.42f+.16f*Noise(x/37,z/37,Parameters.Seed+515));
            float terrace = Mathf.Floor(h / 3.8f) * 3.8f + Mathf.SmoothStep(0, 1, Mathf.Repeat(h, 3.8f) / 3.8f) * 3.8f;
            h = Mathf.Lerp(h, terrace, .32f);
            float river = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Parameters.RiverWidth * .45f, Parameters.RiverWidth * 2.2f, RiverDistance(x, z)));
            h = Mathf.Lerp(h, Parameters.WaterLevel - 1.35f + .15f * Fbm(x, z, 17, 91), river);
            float radius = (float)Math.Sqrt(x * x + z * z);
            float home = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Parameters.HomeRadius, Parameters.HomeRadius + 24, radius));
            return Mathf.Lerp(h, .15f + .10f * Fbm(x, z, 22, 903), home);
        }
        public Vector2 SiteCenter(int px, int pz)
        {
            if (px == 0 && pz == 0) return new Vector2(-48, 66);
            return new Vector2(px * 96 + (Random01(px, pz, Parameters.Seed + 981) - .5f) * 35,
                pz * 96 + (Random01(px, pz, Parameters.Seed + 982) - .5f) * 35);
        }
        public bool HasSite(int px, int pz) => (px == 0 && pz == 0) || Random01(px, pz, Parameters.Seed + 991) < .22f;
        public float Height(double x, double z)
        {
            float h = RawHeight(x, z);
            int px = (int)Math.Floor((x + 48) / 96), pz = (int)Math.Floor((z + 48) / 96);
            // Site influence includes neighbour patches and is world-coordinate based.
            for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
            {
                int ax = px + dx, az = pz + dz;
                if (!HasSite(ax, az)) continue;
                Vector2 c = SiteCenter(ax, az);
                if (RiverDistance(c.x, c.y) < Parameters.RiverWidth * 2.8f) continue;
                float d = (float)Math.Sqrt((x - c.x) * (x - c.x) + (z - c.y) * (z - c.y));
                float influence = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(24, 35, d));
                if (influence > 0) h = Mathf.Lerp(h, RawHeight(c.x, c.y), influence);
            }
            return h;
        }
        public Vector3 Normal(double x, double z)
        {
            const double e = .4;
            return new Vector3(Height(x - e, z) - Height(x + e, z), (float)(2 * e),
                Height(x, z - e) - Height(x, z + e)).normalized;
        }
        public TerrainSample Sample(double x, double z)
        {
            float h = Height(x, z);
            float geology = Noise(x / 23, z / 23, Parameters.Seed + 303);
            float rock = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(2.5f, 10, h));
            Color c = Color.Lerp(Parameters.Soil, Parameters.Rock, rock);
            c = Color.Lerp(c, Parameters.Sand, Mathf.SmoothStep(0, 1, geology) * .26f * (1 - rock));
            float shore = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(Parameters.WaterLevel, Parameters.WaterLevel + 1.2f, h));
            c = Color.Lerp(c, Parameters.Soil * .6f, shore * .6f);
            float pollution = Mathf.SmoothStep(0, 1, Noise(x / 44, z / 44, Parameters.Seed + 811)) * Parameters.PollutionAmount;
            c = Color.Lerp(c, Parameters.Pollution, pollution);
            float restored = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(12, 24, (float)Math.Sqrt(x * x + z * z)));
            c = Color.Lerp(c, Parameters.RestoredSoil, restored * .6f);
            float moss=Mathf.SmoothStep(0,1,Mathf.InverseLerp(.46f,.66f,Noise(x/8,z/8,Parameters.Seed+817)));
            c=Color.Lerp(c,Parameters.RestoredGrass,restored*moss*Parameters.Restoration*.6f);
            float road=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(2.1f,4.0f,TerrainLayout.RoadDistance((float)x,(float)z)));
            c=Color.Lerp(c,Parameters.Sand*.75f,road*.4f);
            c.a = rock;
            return new TerrainSample(h, c);
        }
    }
}
