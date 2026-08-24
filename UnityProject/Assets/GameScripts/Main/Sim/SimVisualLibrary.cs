using System;
using System.Collections.Generic;
using UnityEngine;

namespace BinGames.Sim
{
    /// <summary>
    /// 程序化 Mesh 组合体工厂（任务二：3D 表现差异化）。零美术依赖：所有造型由基础几何体
    /// （球/胶囊/锥/环/盘/刺球等）按局部变换拼合成单个 Mesh（<see cref="Mesh.CombineMeshes"/>
    /// 合并到 submesh 0），满足 <see cref="SimRenderer.Draw"/> 每个 VisualId 只认一个
    /// Mesh+Material 的 GPU Instancing 限制。
    ///
    /// 按 ArtId（<c>OrganelleCatalog.ArtId</c> / 基元 / 召唤物 / Carrier 装配挂件）取造型，
    /// 与 VisualId 的映射在 <see cref="CellStageFlow.BuildVisuals"/> 里建（VisualId 是纯渲染层
    /// 整数索引，内核不认识 ArtId 字符串）。
    /// </summary>
    public static class SimVisualLibrary
    {
        // ── 基础几何缓存（局部单位尺寸，拼合时用矩阵缩放/平移/旋转）──
        private static Mesh _sphereUnit;
        private static Mesh _thinCapsuleUnit;
        private static readonly Dictionary<string, Mesh> _cache = new Dictionary<string, Mesh>(64);

        /// <summary>按 ArtId 取（或建并缓存）组合体 Mesh。未知 id 回退为单位球。</summary>
        public static Mesh BuildForArtId(string artId)
        {
            if (string.IsNullOrEmpty(artId))
            {
                return SphereUnit();
            }
            if (_cache.TryGetValue(artId, out Mesh cached))
            {
                return cached;
            }
            Mesh m = Compose(artId);
            _cache[artId] = m;
            return m;
        }

        private static Mesh Compose(string artId)
        {
            switch (artId)
            {
                // ── 24 器官/代谢模块 ──
                case "org/mito": return Combine("org_mito", Capsule(0.34f, 0.32f), Ridges(0.34f, 0.32f, 5));
                case "org/chloro": return Combine("org_chloro", Disc(0.5f, 0.16f), RimBumps(0.5f, 0.16f, 8, 0.06f));
                case "org/vacuole": return Combine("org_vacuole",
                    Sphere(0.5f), At(Sphere(0.22f), Vector3.zero, Vector3.one));
                case "org/golgi": return GolgiStack();
                case "org/merge": return Combine("org_merge",
                    At(Sphere(0.32f), new Vector3(-0.2f, 0, 0), Vector3.one),
                    At(Sphere(0.32f), new Vector3(0.2f, 0, 0), Vector3.one));
                case "org/lens": return Lens();
                case "org/scatter": return Combine("org_scatter",
                    At(Capsule(0.14f, 0.34f), Vector3.zero, Vector3.one, Quaternion.Euler(0, 0, 0)),
                    At(Capsule(0.14f, 0.34f), Vector3.zero, Vector3.one, Quaternion.Euler(0, 90, 0)));
                case "org/swell": return Combine("org_swell",
                    Sphere(0.32f), Ring(0.5f, 0.05f, Quaternion.identity));
                case "org/flagella": return Combine("org_flagella",
                    Sphere(0.4f), EquatorFlagellum(0.4f));
                case "org/lyso": return SpikedSphere("org_lyso", 0.34f, 9, 0.14f, 0.045f);
                case "org/perox": return Combine("org_perox",
                    Sphere(0.34f), At(Cone(0.14f, 0.22f), new Vector3(0, 0.4f, 0), Vector3.one, FromTo(Vector3.up)));
                case "org/aqua": return Combine("org_aqua",
                    Sphere(0.34f), At(TearDrop(), new Vector3(0, 0.34f, 0), Vector3.one));
                case "org/ion": return Combine("org_ion",
                    Sphere(0.3f), JaggedRing(0.48f, 6));
                case "org/radiator": return RadiatorFan();
                case "org/breaker": return Combine("org_breaker",
                    Ring(0.42f, 0.08f, Quaternion.identity), At(Box(new Vector3(0.36f, 0.05f, 0.1f)), Vector3.zero, Vector3.one));
                case "org/synapse": return Combine("org_synapse",
                    Sphere(0.32f), VesicleCluster(0.32f));
                case "org/emitter": return Combine("org_emitter",
                    At(Cone(0.16f, 0.34f), new Vector3(0.34f, 0, 0), Vector3.one, FromTo(Vector3.right)),
                    At(Disc(0.22f, 0.1f), Vector3.zero, Vector3.one));
                case "org/cilia": return Combine("org_cilia",
                    Sphere(0.3f), At(Capsule(0.06f, 0.36f), new Vector3(0, 0.5f, 0), Vector3.one));
                case "org/spine": return SpikedSphere("org_spine", 0.3f, 11, 0.26f, 0.05f);
                case "org/slime": return Combine("org_slime",
                    Sphere(0.3f), At(Sphere(0.42f), Vector3.zero, Vector3.one));
                case "org/receptor": return Combine("org_receptor",
                    Sphere(0.3f), YTentacles());
                case "org/insulate": return Combine("org_insulate",
                    At(Capsule(0.24f, 0.4f), Vector3.zero, Vector3.one, Quaternion.Euler(0, 0, 90)));
                case "org/valve": return Combine("org_valve",
                    Ring(0.4f, 0.09f, Quaternion.identity), At(Disc(0.2f, 0.05f), Vector3.zero, Vector3.one));
                case "org/filter": return Combine("org_filter",
                    At(Capsule(0.22f, 0.36f), Vector3.zero, Vector3.one, Quaternion.Euler(0, 0, 90)), FilterMesh());

                // ── 4 基元：能量/质/光/热 ──
                case "prim/energy": return Sphere(0.3f);
                case "prim/mass": return Box(new Vector3(0.28f, 0.28f, 0.28f));
                case "prim/light": return Octahedron(0.34f);
                case "prim/heat": return Tetra(0.36f);

                // ── 召唤物（任务三）──
                case "summon/spore": return Combine("summon_spore",
                    Sphere(0.24f), At(Cone(0.06f, 0.1f), new Vector3(0, 0.26f, 0), Vector3.one, FromTo(Vector3.up)));
                case "summon/phage": return Combine("summon_phage",
                    At(Sphere(0.22f), new Vector3(0.12f, 0, 0), Vector3.one),
                    At(Capsule(0.09f, 0.16f), new Vector3(-0.2f, 0, 0), new Vector3(1f, 0.6f, 0.6f), Quaternion.Euler(0, 0, 90)));
                case "summon/mycelium": return MyceliumFan();

                // ── Carrier 装配挂件（玩家本体随装配变化，任务二）──
                case "carrier/base": return Capsule(0.32f, 0.45f);
                case "carrier/emitter": return Combine("carrier_emitter",
                    Capsule(0.32f, 0.45f),
                    At(Cone(0.16f, 0.3f), new Vector3(-0.42f, 0, 0), Vector3.one, FromTo(Vector3.left)));
                case "carrier/cilia": return Combine("carrier_cilia",
                    Capsule(0.32f, 0.45f),
                    At(Capsule(0.06f, 0.32f), new Vector3(0.5f, 0, 0), Vector3.one, Quaternion.Euler(0, 0, 90)));

                default:
                    if (TryComposeGeneMarker(artId, out Mesh geneMarkerMesh))
                    {
                        return geneMarkerMesh;
                    }
                    return SphereUnit();
            }
        }

        /// <summary>carrier-visual-feedback story-002：装了槽位基因时在 base mesh 上追加一个按"显性组"
        /// 区分的小 marker（沿用 <c>carrier/emitter_gene</c> 已验证的挂件手法），artId 形如
        /// <c>{baseArtId}::relay|transform|edge|contract</c>。未命中任何已知后缀返回 false，交给调用方回退。</summary>
        private static bool TryComposeGeneMarker(string artId, out Mesh mesh)
        {
            int sep = artId.LastIndexOf("::", StringComparison.Ordinal);
            if (sep < 0)
            {
                mesh = null;
                return false;
            }
            string baseId = artId.Substring(0, sep);
            string suffix = artId.Substring(sep + 2);
            Mesh marker = suffix switch
            {
                "transform" => SpikedSphere("gene_marker_transform", 0.12f, 5, 0.07f, 0.025f),
                "relay" => Ring(0.14f, 0.03f, Quaternion.identity),
                "edge" => Box(new Vector3(0.09f, 0.09f, 0.09f)),
                "contract" => Sphere(0.09f),
                _ => null,
            };
            if (marker == null)
            {
                mesh = null;
                return false;
            }
            string name = (baseId + "_" + suffix).Replace('/', '_');
            mesh = Combine(name, BuildForArtId(baseId), At(marker, new Vector3(0, 0.5f, 0.2f), Vector3.one));
            return true;
        }

        /// <summary>可作为 active Carrier 的 21 个器官的 base ArtId（Epic G IsCarrier 全集）。
        /// 硬编码而非查 <c>OrganelleCatalog</c>：本类在 AOT 程序集 <c>BinGames.Sim</c>，
        /// 不能引用热更层 <c>GameLogic</c> 组装的目录（ADR-0001 分层约束），
        /// emitter/cilia 用玩家本体专属的 <c>carrier/</c> 前缀，其余 19 个用 <c>org/</c> 前缀。</summary>
        private static readonly string[] CarrierBaseArtIds =
        {
            "carrier/emitter", "carrier/cilia",
            "org/vacuole", "org/golgi", "org/merge", "org/lens", "org/scatter", "org/swell",
            "org/flagella", "org/lyso", "org/perox", "org/aqua", "org/ion", "org/radiator",
            "org/breaker", "org/synapse", "org/spine", "org/slime", "org/receptor", "org/valve", "org/filter",
        };

        private static readonly string[] GeneMarkerSuffixes = { "relay", "transform", "edge", "contract" };

        /// <summary>所有已定义 ArtId，供沙盒对比台一键铺开（任务二验收）+ story-002 槽位基因组合 marker
        /// （21 个 Carrier base × 4 组后缀 = 84 项）。</summary>
        public static readonly string[] AllArtIds = BuildAllArtIds();

        private static string[] BuildAllArtIds()
        {
            var ids = new List<string>
            {
                "org/mito", "org/chloro", "org/vacuole", "org/golgi", "org/merge", "org/lens",
                "org/scatter", "org/swell", "org/flagella", "org/lyso", "org/perox", "org/aqua",
                "org/ion", "org/radiator", "org/breaker", "org/synapse", "org/emitter", "org/cilia",
                "org/spine", "org/slime", "org/receptor", "org/insulate", "org/valve", "org/filter",
                "prim/energy", "prim/mass", "prim/light", "prim/heat",
                "summon/spore", "summon/phage", "summon/mycelium",
                "carrier/base", "carrier/emitter", "carrier/cilia",
            };
            foreach (string baseId in CarrierBaseArtIds)
            {
                foreach (string suffix in GeneMarkerSuffixes)
                {
                    ids.Add(baseId + "::" + suffix);
                }
            }
            return ids.ToArray();
        }

        // ══════════════════════════ 基础几何 ══════════════════════════

        private static Mesh SphereUnit() => _sphereUnit ??= BuildEllipsoid(0.5f, 0.5f, 8, 12, "Unit");

        private static Mesh Sphere(float radius) => BuildEllipsoid(radius, radius, 8, 12, "Sphere");

        private static Mesh Disc(float radiusXZ, float radiusY) => BuildEllipsoid(radiusXZ, radiusY, 8, 12, "Disc");

        private static Mesh Capsule(float radius, float cylHalfHeight)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            const int lat = 6, lon = 10;
            int ring = lon + 1;
            int totalRings = 2 * lat + 2;

            for (int r = 0; r < totalRings; r++)
            {
                bool top = r <= lat;
                int y = top ? r : r - lat - 1;
                float phi = top
                    ? y / (float)lat * (Mathf.PI * 0.5f)
                    : Mathf.PI * 0.5f - y / (float)lat * (Mathf.PI * 0.5f);
                float ringRadius = radius * Mathf.Sin(phi);
                float ringY = top
                    ? cylHalfHeight + radius * Mathf.Cos(phi)
                    : -cylHalfHeight - radius * Mathf.Cos(phi);

                for (int x = 0; x <= lon; x++)
                {
                    float theta = x / (float)lon * Mathf.PI * 2f;
                    vertices.Add(new Vector3(ringRadius * Mathf.Cos(theta), ringY, ringRadius * Mathf.Sin(theta)));
                }
            }

            for (int r = 0; r < totalRings - 1; r++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = r * ring + x;
                    int b = a + ring;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }

            return Finalize("Capsule", vertices, triangles);
        }

        /// <summary>锥体：尖端在局部 +X。</summary>
        private static Mesh Cone(float radius, float halfLength)
        {
            var vertices = new List<Vector3> { new Vector3(halfLength, 0, 0), new Vector3(-halfLength, 0, 0) };
            var triangles = new List<int>();
            const int segments = 10;
            int ringStart = vertices.Count;
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments * Mathf.PI * 2f;
                vertices.Add(new Vector3(-halfLength, radius * Mathf.Cos(t), radius * Mathf.Sin(t)));
            }
            const int tip = 0, baseCenter = 1;
            for (int i = 0; i < segments; i++)
            {
                int a = ringStart + i, b = ringStart + i + 1;
                triangles.Add(tip); triangles.Add(a); triangles.Add(b);
                triangles.Add(baseCenter); triangles.Add(b); triangles.Add(a);
            }
            return Finalize("Cone", vertices, triangles);
        }

        private static Mesh Box(Vector3 halfExtents)
        {
            Vector3 h = halfExtents;
            Vector3[] c =
            {
                new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z),
                new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
                new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z),
                new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z),
            };
            int[] tris =
            {
                0,2,1, 0,3,2, // back
                4,5,6, 4,6,7, // front
                0,1,5, 0,5,4, // bottom
                3,7,6, 3,6,2, // top
                0,4,7, 0,7,3, // left
                1,2,6, 1,6,5, // right
            };
            return Finalize("Box", new List<Vector3>(c), new List<int>(tris));
        }

        /// <summary>正四面体。</summary>
        private static Mesh Tetra(float size)
        {
            float a = size;
            Vector3 p0 = new Vector3(a, a, a);
            Vector3 p1 = new Vector3(a, -a, -a);
            Vector3 p2 = new Vector3(-a, a, -a);
            Vector3 p3 = new Vector3(-a, -a, a);
            var verts = new List<Vector3> { p0, p1, p2, p3 };
            var tris = new List<int> { 0, 1, 2, 0, 2, 3, 0, 3, 1, 1, 3, 2 };
            return Finalize("Tetra", verts, tris);
        }

        /// <summary>正八面体（菱形）。</summary>
        private static Mesh Octahedron(float size)
        {
            var verts = new List<Vector3>
            {
                new Vector3(size, 0, 0), new Vector3(-size, 0, 0),
                new Vector3(0, size, 0), new Vector3(0, -size, 0),
                new Vector3(0, 0, size), new Vector3(0, 0, -size),
            };
            var tris = new List<int>
            {
                2,0,4, 2,4,1, 2,1,5, 2,5,0,
                3,4,0, 3,1,4, 3,5,1, 3,0,5,
            };
            return Finalize("Octahedron", verts, tris);
        }

        /// <summary>圆环（甜甜圈），法向沿局部 +Y。</summary>
        private static Mesh Ring(float majorRadius, float minorRadius, Quaternion _unused)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            const int majorSeg = 16, minorSeg = 8;
            for (int i = 0; i <= majorSeg; i++)
            {
                float u = i / (float)majorSeg * Mathf.PI * 2f;
                Vector3 center = new Vector3(Mathf.Cos(u), 0, Mathf.Sin(u)) * majorRadius;
                Vector3 outDir = new Vector3(Mathf.Cos(u), 0, Mathf.Sin(u));
                for (int j = 0; j <= minorSeg; j++)
                {
                    float v = j / (float)minorSeg * Mathf.PI * 2f;
                    Vector3 local = outDir * (Mathf.Cos(v) * minorRadius) + Vector3.up * (Mathf.Sin(v) * minorRadius);
                    verts.Add(center + local);
                }
            }
            int ring = minorSeg + 1;
            for (int i = 0; i < majorSeg; i++)
            {
                for (int j = 0; j < minorSeg; j++)
                {
                    int a = i * ring + j, b = a + ring;
                    tris.Add(a); tris.Add(b); tris.Add(a + 1);
                    tris.Add(a + 1); tris.Add(b); tris.Add(b + 1);
                }
            }
            return Finalize("Ring", verts, tris);
        }

        /// <summary>多刺球：基础球 + N 根锥形刺，费波纳契球面均匀分布。</summary>
        private static Mesh SpikedSphere(string name, float baseRadius, int spikeCount, float spikeLength, float spikeBaseRadius)
        {
            var parts = new List<(Mesh, Matrix4x4)> { (Sphere(baseRadius), Matrix4x4.identity) };
            Mesh spike = Cone(spikeBaseRadius, spikeLength * 0.5f);
            for (int i = 0; i < spikeCount; i++)
            {
                Vector3 dir = FibonacciSpherePoint(i, spikeCount);
                Vector3 pos = dir * (baseRadius + spikeLength * 0.5f);
                Quaternion rot = FromTo(dir);
                parts.Add((spike, Matrix4x4.TRS(pos, rot, Vector3.one)));
            }
            return CombineParts(name, parts);
        }

        private static Vector3 FibonacciSpherePoint(int i, int n)
        {
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            float y = 1f - (i / (float)Mathf.Max(1, n - 1)) * 2f;
            float radiusAtY = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float theta = golden * i;
            return new Vector3(Mathf.Cos(theta) * radiusAtY, y, Mathf.Sin(theta) * radiusAtY);
        }

        // ══════════════════════════ 复合造型 ══════════════════════════

        /// <summary>线粒体内嵴：表面若干短嵴棒（压扁小盒子），沿胶囊体表面环绕分布。</summary>
        private static (Mesh, Matrix4x4) Ridges(float radius, float cylHalfHeight, int count)
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh ridge = Box(new Vector3(0.03f, 0.1f, 0.05f));
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(t), 0, Mathf.Sin(t));
                Vector3 pos = dir * (radius * 0.96f) + new Vector3(0, (i % 2 == 0 ? 1 : -1) * cylHalfHeight * 0.5f, 0);
                parts.Add((ridge, Matrix4x4.TRS(pos, FromTo(dir), Vector3.one)));
            }
            return (CombineParts("mito_ridges", parts), Matrix4x4.identity);
        }

        private static (Mesh, Matrix4x4) RimBumps(float radiusXZ, float radiusY, int count, float bumpSize)
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh bump = Sphere(bumpSize);
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count * Mathf.PI * 2f;
                Vector3 pos = new Vector3(Mathf.Cos(t) * radiusXZ, 0, Mathf.Sin(t) * radiusXZ);
                parts.Add((bump, Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one)));
            }
            return (CombineParts("chloro_bumps", parts), Matrix4x4.identity);
        }

        private static Mesh GolgiStack()
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            for (int i = 0; i < 3; i++)
            {
                float y = (i - 1) * 0.16f;
                float r = 0.44f - i * 0.03f;
                parts.Add((Disc(r, 0.05f), Matrix4x4.TRS(new Vector3(0, y, 0), Quaternion.identity, Vector3.one)));
            }
            return CombineParts("org_golgi", parts);
        }

        /// <summary>双凸透镜：两个压扁半球（用挤压球近似，避免真半球开口留破面）拼合。</summary>
        private static Mesh Lens()
        {
            var parts = new List<(Mesh, Matrix4x4)>
            {
                (Disc(0.42f, 0.22f), Matrix4x4.TRS(new Vector3(0, 0.06f, 0), Quaternion.identity, Vector3.one)),
                (Disc(0.42f, 0.22f), Matrix4x4.TRS(new Vector3(0, -0.06f, 0), Quaternion.identity, Vector3.one)),
            };
            return CombineParts("org_lens", parts);
        }

        private static (Mesh, Matrix4x4) EquatorFlagellum(float sphereRadius)
        {
            Mesh flagellum = Capsule(0.045f, 0.28f);
            Matrix4x4 m = Matrix4x4.TRS(
                new Vector3(sphereRadius + 0.05f, 0, 0), Quaternion.Euler(0, 0, 90), Vector3.one);
            return (flagellum, m);
        }

        private static Mesh TearDrop()
        {
            var parts = new List<(Mesh, Matrix4x4)>
            {
                (Sphere(0.1f), Matrix4x4.identity),
                (Cone(0.1f, 0.09f), Matrix4x4.TRS(new Vector3(0, 0.1f, 0), Quaternion.Euler(0, 0, -90), Vector3.one)),
            };
            return CombineParts("teardrop", parts);
        }

        private static (Mesh, Matrix4x4) JaggedRing(float radius, int teeth)
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh tooth = Tetra(0.05f);
            for (int i = 0; i < teeth; i++)
            {
                float t = i / (float)teeth * Mathf.PI * 2f;
                Vector3 pos = new Vector3(Mathf.Cos(t) * radius, (i % 2 == 0 ? 0.05f : -0.05f), Mathf.Sin(t) * radius);
                parts.Add((tooth, Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one)));
            }
            return (CombineParts("ion_jagged", parts), Matrix4x4.identity);
        }

        private static Mesh RadiatorFan()
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh blade = Box(new Vector3(0.22f, 0.02f, 0.1f));
            const int count = 5;
            for (int i = 0; i < count; i++)
            {
                float ang = (i - (count - 1) * 0.5f) * 22f;
                parts.Add((blade, Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, ang, 0), Vector3.one)));
            }
            return CombineParts("org_radiator", parts);
        }

        private static (Mesh, Matrix4x4) VesicleCluster(float sphereRadius)
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh vesicle = Sphere(0.08f);
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = FibonacciSpherePoint(i, 4);
                Vector3 pos = dir * (sphereRadius + 0.08f);
                parts.Add((vesicle, Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one)));
            }
            return (CombineParts("synapse_vesicles", parts), Matrix4x4.identity);
        }

        private static (Mesh, Matrix4x4) YTentacles()
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh arm = Capsule(0.035f, 0.12f);
            Vector3[] dirs = { new Vector3(0, 1, 0), new Vector3(-0.7f, -0.6f, 0), new Vector3(0.7f, -0.6f, 0) };
            foreach (Vector3 d in dirs)
            {
                Vector3 dir = d.normalized;
                Vector3 pos = dir * 0.42f;
                parts.Add((arm, Matrix4x4.TRS(pos, FromTo(dir), Vector3.one)));
            }
            return (CombineParts("receptor_y", parts), Matrix4x4.identity);
        }

        private static Mesh FilterMesh()
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh bar = Box(new Vector3(0.02f, 0.16f, 0.02f));
            for (int i = -2; i <= 2; i++)
            {
                parts.Add((bar, Matrix4x4.TRS(new Vector3(i * 0.08f, 0, 0), Quaternion.identity, Vector3.one)));
            }
            return CombineParts("org_filter_mesh", parts);
        }

        private static Mesh MyceliumFan()
        {
            var parts = new List<(Mesh, Matrix4x4)>();
            Mesh blade = Box(new Vector3(0.22f, 0.015f, 0.06f));
            const int count = 6;
            for (int i = 0; i < count; i++)
            {
                float ang = i / (float)count * 360f;
                parts.Add((blade, Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, ang, 20f), Vector3.one)));
            }
            return CombineParts("summon_mycelium", parts);
        }

        // ══════════════════════════ 拼合工具 ══════════════════════════

        private static (Mesh, Matrix4x4) At(Mesh mesh, Vector3 pos, Vector3 scale) =>
            (mesh, Matrix4x4.TRS(pos, Quaternion.identity, scale));

        private static (Mesh, Matrix4x4) At(Mesh mesh, Vector3 pos, Vector3 scale, Quaternion rot) =>
            (mesh, Matrix4x4.TRS(pos, rot, scale));

        /// <summary>局部 +X 朝向 dir 的旋转（配合 Cone 的尖端定义在 +X）。</summary>
        private static Quaternion FromTo(Vector3 dir) => Quaternion.FromToRotation(Vector3.right, dir.normalized);

        /// <summary>接受裸 Mesh（视作局部原点，identity 变换）或 (Mesh,Matrix4x4) 元组混合传入，
        /// 省得每个调用点都手写 At(mesh, Vector3.zero, Vector3.one)。</summary>
        private static Mesh Combine(string name, params object[] parts)
        {
            var list = new List<(Mesh, Matrix4x4)>(parts.Length);
            foreach (object p in parts)
            {
                switch (p)
                {
                    case Mesh m:
                        list.Add((m, Matrix4x4.identity));
                        break;
                    case ValueTuple<Mesh, Matrix4x4> t:
                        list.Add(t);
                        break;
                }
            }
            return CombineParts(name, list);
        }

        private static Mesh CombineParts(string name, List<(Mesh mesh, Matrix4x4 xform)> parts)
        {
            var combines = new CombineInstance[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                combines[i] = new CombineInstance { mesh = parts[i].mesh, transform = parts[i].xform };
            }
            var m = new Mesh { name = "Sim_" + name };
            m.CombineMeshes(combines, true, true);
            ApplyTopDownUV(m);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>SimBioGlass 按 UV 当作局部圆盘坐标画软边发光轮廓（<c>p=uv*2-1</c>），不认顶点位置。
        /// <see cref="Combine"/>/<see cref="CombineParts"/> 拼合出的复合体本身不带 UV（合并前各分块也
        /// 没设），会被 shader 当作越界坐标整体裁掉/走样。这里按俯视 XZ 投影补一份：顶点在 XZ 平面上
        /// 距中心越远，UV 越靠近圆盘边缘，配合 shader 的软边/描边逻辑，任意组合体都能画出合理轮廓。</summary>
        private static void ApplyTopDownUV(Mesh m)
        {
            Vector3[] verts = m.vertices;
            float maxR = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                float r = new Vector2(verts[i].x, verts[i].z).magnitude;
                if (r > maxR)
                {
                    maxR = r;
                }
            }
            if (maxR < 1e-5f)
            {
                maxR = 1f;
            }

            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                uvs[i] = new Vector2(0.5f + 0.5f * verts[i].x / maxR, 0.5f + 0.5f * verts[i].z / maxR);
            }
            m.SetUVs(0, uvs);
        }

        private static Mesh BuildEllipsoid(float radiusXZ, float radiusY, int lat, int lon, string name)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();

            for (int y = 0; y <= lat; y++)
            {
                float v = y / (float)lat;
                float theta = v * Mathf.PI;
                float sinTheta = Mathf.Sin(theta);
                float cosTheta = Mathf.Cos(theta);

                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float phi = u * Mathf.PI * 2f;
                    vertices.Add(new Vector3(
                        radiusXZ * sinTheta * Mathf.Cos(phi), radiusY * cosTheta, radiusXZ * sinTheta * Mathf.Sin(phi)));
                }
            }

            int ring = lon + 1;
            for (int y = 0; y < lat; y++)
            {
                for (int x = 0; x < lon; x++)
                {
                    int a = y * ring + x, b = a + ring;
                    triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
                    triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
                }
            }
            return Finalize(name, vertices, triangles);
        }

        private static Mesh Finalize(string name, List<Vector3> vertices, List<int> triangles)
        {
            var m = new Mesh { name = "Sim_" + name };
            m.SetVertices(vertices);
            m.SetTriangles(triangles, 0);
            ApplyTopDownUV(m);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
