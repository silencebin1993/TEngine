using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace BinGames.TerrainVisual
{
    public sealed class TerrainStream : MonoBehaviour
    {
        public TerrainProfile Profile;
        public Transform Viewer;
        public int Seed;
        public bool ShowBoundaries;
        public bool ShowModules = true;
        public bool ShowDecoration = true;
        public bool ExternalGround {get;private set;}
        public void SetExternalGround(bool value)
        {ExternalGround=value;foreach(Tile t in _tiles.Values)t.Root.GetComponent<MeshRenderer>().enabled=!value;}
        public int ReadyCount => _tiles.Count;
        public int PendingCount
        {
            get { int count=0;foreach(var pair in _wanted)
                if(!_tiles.TryGetValue(pair.Key,out Tile tile)||tile.Signature!=pair.Value)count++;return count; }
        }
        public int WfcFailures { get; private set; }
        public int ModuleCount { get {int n=0;foreach(Tile tile in _tiles.Values)if(tile.Modules)n+=tile.Modules.transform.childCount;return n;} }
        public int TriangleCount { get; private set; }
        public int BuildingCount {get {int n=0;foreach(Tile t in _tiles.Values)if(t.Details!=null)n+=t.Details.Buildings;return n;}}
        public int BuildingBayCount {get {int n=0;foreach(Tile t in _tiles.Values)if(t.Details!=null)n+=t.Details.Bays;return n;}}
        public TerrainField Field { get; private set; }
        public ITerrainField Surface { get; private set; }
        public Vector2Int WorldOrigin { get; private set; }
        private sealed class Tile
        {
            public GameObject Root, Props, Modules;
            public Mesh Mesh, WaterMesh;
            public int Signature;
            public TerrainDetailResult Details;
        }
        private sealed class Completed
        {
            public Vector2Int Key;
            public TerrainMeshData Data;
            public int Epoch, Signature;
            public string Error;
        }
        private readonly Dictionary<Vector2Int, Tile> _tiles = new Dictionary<Vector2Int, Tile>();
        private readonly Dictionary<Vector2Int, int> _wanted = new Dictionary<Vector2Int, int>();
        private readonly HashSet<Vector2Int> _pending = new HashSet<Vector2Int>();
        private readonly ConcurrentQueue<Completed> _completed = new ConcurrentQueue<Completed>();
        private int _epoch, _running;
        private Vector2Int _lastCenter = new Vector2Int(int.MinValue, int.MinValue);
        private MaterialPropertyBlock _tint;
        private Material _lineMaterial;

        private void Start() { if (Profile && Viewer && Surface==null) Rebuild(Seed == 0 ? Profile.Seed : Seed); }
        public void Rebuild(int seed)
        { SetField(new TerrainField(Profile.Snapshot(seed)),seed); }

        // Campaign integration passes an immutable, thread-safe visual snapshot. This
        // renderer does not change logical heights, resource placement, navigation or saves.
        public void SetField(ITerrainField surface,int seed)
        {
            if(surface==null)throw new ArgumentNullException(nameof(surface));
            Seed = seed; _epoch++; _pending.Clear(); _wanted.Clear();
            foreach (Tile tile in _tiles.Values) Release(tile);
            _tiles.Clear(); TriangleCount = 0; WfcFailures = 0;
            Surface=surface;Field=surface as TerrainField;
            _lastCenter = new Vector2Int(int.MinValue, int.MinValue);
            Recenter();
        }
        private void Update()
        {
            if (!Profile || !Viewer || Surface == null) return;
            Shader.SetGlobalVector("_TerrainWorldOrigin",new Vector4(WorldOrigin.x,WorldOrigin.y,0,0));
            Recenter();
            int uploads = 0;
            while (uploads < 2 && _completed.TryDequeue(out Completed ready))
            {
                if (ready.Epoch != _epoch) continue;
                _pending.Remove(ready.Key);
                if (ready.Error != null) { Debug.LogError(ready.Error); continue; }
                if (!_wanted.TryGetValue(ready.Key, out int signature) || signature != ready.Signature) continue;
                Upload(ready); uploads++;
            }
            while (Volatile.Read(ref _running) < 2)
            {
                bool found = false; Vector2Int next = default; int signature = 0, best = int.MaxValue;
                foreach (var pair in _wanted)
                {
                    if (_pending.Contains(pair.Key) || (_tiles.TryGetValue(pair.Key, out Tile existing) && existing.Signature == pair.Value)) continue;
                    int distance = Mathf.Abs(pair.Key.x - _lastCenter.x) + Mathf.Abs(pair.Key.y - _lastCenter.y);
                    if (distance < best) { found = true; best = distance; next = pair.Key; signature = pair.Value; }
                }
                if (!found) break;
                Schedule(next, signature);
            }
        }
        private static int Step(Vector2Int key, Vector2Int center)
        {
            int d = Mathf.Max(Mathf.Abs(key.x - center.x), Mathf.Abs(key.y - center.y));
            return d <= 2 ? 1 : d <= 4 ? 2 : 4;
        }
        private void Recenter()
        {
            if (!Viewer) return;
            int size = Profile.ChunkSize;
            if (Mathf.Abs(Viewer.position.x) > 1024 || Mathf.Abs(Viewer.position.z) > 1024)
            {
                int dx = Mathf.FloorToInt(Viewer.position.x / size) * size;
                int dz = Mathf.FloorToInt(Viewer.position.z / size) * size;
                WorldOrigin += new Vector2Int(dx, dz);
                Viewer.position -= new Vector3(dx, 0, dz);
                foreach (var p in _tiles) p.Value.Root.transform.localPosition = new Vector3(
                    p.Key.x * size - WorldOrigin.x, 0, p.Key.y * size - WorldOrigin.y);
            }
            Vector2Int center = new Vector2Int(Mathf.FloorToInt((Viewer.position.x + WorldOrigin.x) / size),
                Mathf.FloorToInt((Viewer.position.z + WorldOrigin.y) / size));
            if (center == _lastCenter) return;
            _lastCenter = center; _wanted.Clear();
            int radius = Profile.ViewRadius;
            for (int z = -radius; z <= radius; z++) for (int x = -radius; x <= radius; x++)
            {
                var key = center + new Vector2Int(x, z);
                int step = Step(key, center);
                // Every boundary uses the same world-space 4 m anchors, including during
                // asynchronous LOD replacement. Neighbours need not arrive in the same frame.
                int signature = step | 4 << 4 | 4 << 8 | 4 << 12 | 4 << 16;
                _wanted.Add(key, signature);
            }
            var remove = new List<Vector2Int>();
            foreach (var pair in _tiles) if (!_wanted.ContainsKey(pair.Key)) { Release(pair.Value); remove.Add(pair.Key); }
            foreach (Vector2Int key in remove) _tiles.Remove(key);
        }
        private void Schedule(Vector2Int key, int signature)
        {
            _pending.Add(key); int epoch = _epoch; ITerrainField field = Surface; int size = Profile.ChunkSize;
            float water = Profile.WaterLevel; Interlocked.Increment(ref _running);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var item = new Completed { Key = key, Signature = signature, Epoch = epoch };
                try { item.Data = TerrainMeshBuilder.Build(field, key.x, key.y, size, signature & 15,
                    signature >> 4 & 15, signature >> 8 & 15, signature >> 12 & 15, signature >> 16 & 15, water); }
                catch (Exception e) { item.Error = "地形区块生成失败：" + e; }
                finally { _completed.Enqueue(item); Interlocked.Decrement(ref _running); }
            });
        }
        private void Upload(Completed ready)
        {
            if (_tiles.TryGetValue(ready.Key, out Tile old)) Release(old);
            int size = Profile.ChunkSize;
            var tile = new Tile { Root = new GameObject($"地形 {ready.Key.x}, {ready.Key.y}"), Signature = ready.Signature };
            tile.Root.transform.SetParent(transform, false);
            tile.Root.transform.localPosition = new Vector3(ready.Key.x * size - WorldOrigin.x, 0, ready.Key.y * size - WorldOrigin.y);
            tile.Mesh = TerrainMeshBuilder.Upload(ready.Data, tile.Root.name);
            tile.Root.AddComponent<MeshFilter>().sharedMesh = tile.Mesh;
            var renderer = tile.Root.AddComponent<MeshRenderer>(); renderer.sharedMaterial = Profile.GroundMaterial;
            renderer.enabled=!ExternalGround;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = true;
            if (ready.Data.HasWater)
            {
                var water = new GameObject("水面"); water.transform.SetParent(tile.Root.transform, false);
                float y = Profile.WaterLevel;
                tile.WaterMesh = new Mesh { name = "水面区块" };
                tile.WaterMesh.vertices = new[] { new Vector3(0,y,0),new Vector3(size,y,0),new Vector3(0,y,size),new Vector3(size,y,size) };
                float wx=ready.Key.x*size,wz=ready.Key.y*size;
                tile.WaterMesh.uv=new[]{new Vector2(wx,wz),new Vector2(wx+size,wz),new Vector2(wx,wz+size),new Vector2(wx+size,wz+size)};
                tile.WaterMesh.triangles = new[] { 0,2,1,1,2,3 }; tile.WaterMesh.RecalculateNormals();
                tile.WaterMesh.RecalculateTangents();
                water.AddComponent<MeshFilter>().sharedMesh = tile.WaterMesh;
                var waterRenderer=water.AddComponent<MeshRenderer>();waterRenderer.sharedMaterial = Profile.WaterMaterial;
                waterRenderer.shadowCastingMode=ShadowCastingMode.Off;
            }
            tile.Props = new GameObject("岩石与残骸"); tile.Props.transform.SetParent(tile.Root.transform, false);
            Scatter(ready.Key, tile.Props.transform);
            tile.Props.SetActive(ShowDecoration);
            tile.Modules = new GameObject("连接废墟"); tile.Modules.transform.SetParent(tile.Root.transform, false);
            if(!Profile.DetailKit)BuildSites(ready.Key, tile.Modules.transform);
            tile.Details=TerrainDetailBuilder.Build(Profile,Field,Seed,ready.Key,tile.Modules.transform);
            WfcFailures+=tile.Details.Failures;tile.Modules.SetActive(ShowModules);
            _tiles[ready.Key] = tile; TriangleCount += tile.Mesh.triangles.Length / 3;
        }
        private void Scatter(Vector2Int key, Transform parent)
        {
            int size = Profile.ChunkSize; int spacing = 6;
            int x0 = key.x * size, z0 = key.y * size;
            // Enumerate world-space scatter cells, then assign each centre to exactly one chunk.
            for (int gz = Mathf.FloorToInt(z0 / (float)spacing); gz <= Mathf.FloorToInt((z0 + size) / (float)spacing); gz++)
                for (int gx = Mathf.FloorToInt(x0 / (float)spacing); gx <= Mathf.FloorToInt((x0 + size) / (float)spacing); gx++)
                {
                    float chance = TerrainField.Random01(gx, gz, Seed + 901);
                    float cluster=TerrainField.Noise(gx*.13,gz*.13,Seed+916);
                    if (chance > Profile.PropDensity * .46f * Mathf.Lerp(.16f,1.6f,cluster)) continue;
                    float x = gx * spacing + TerrainField.Random01(gx, gz, Seed + 902) * spacing;
                    float z = gz * spacing + TerrainField.Random01(gx, gz, Seed + 903) * spacing;
                    if (x < x0 || x >= x0 + size || z < z0 || z >= z0 + size) continue;
                    float y = Surface.Sample(x,z).Height; if (y < Profile.WaterLevel + .25f || TerrainLayout.Reserved(x,z)) continue;
                    int px = Mathf.FloorToInt((x+48)/96), pz = Mathf.FloorToInt((z+48)/96);
                    if(Field!=null && Field.HasSite(px,pz) && Vector2.Distance(Field.SiteCenter(px,pz),new Vector2(x,z)) < 19) continue;
                    float type = TerrainField.Random01(gx,gz,Seed+907);
                    Mesh mesh = type < .68f ? Profile.RockMesh : type < .9f ? Profile.RubbleMesh : Profile.DryTreeMesh;
                    if(type<.68f && type>.34f && Profile.RockVariantMesh)mesh=Profile.RockVariantMesh;
                    if (!mesh) continue;
                    float scale = Mathf.Lerp(.32f, 1.55f, TerrainField.Random01(gx,gz,Seed+909));
                    Color color = type < .68f ? Color.white : new Color(.9f,.85f,.78f);
                    Vector3 stretch=new Vector3(scale*Mathf.Lerp(.85f,1.15f,type),scale,scale*Mathf.Lerp(.82f,1.2f,chance));
                    CreateProp(mesh,parent,new Vector3(x-x0,y-.08f,z-z0),Quaternion.Euler(0,type*720,0),stretch,color);
                }
        }
        private void BuildSites(Vector2Int key, Transform parent)
        {
            TerrainModuleLibrary library = Profile.Library; if (!library || Field==null) return;
            int size = Profile.ChunkSize, x0 = key.x*size,z0=key.y*size;
            int minX = Mathf.FloorToInt((x0-20)/96f),maxX = Mathf.CeilToInt((x0+size+20)/96f);
            int minZ = Mathf.FloorToInt((z0-20)/96f),maxZ = Mathf.CeilToInt((z0+size+20)/96f);
            List<ModuleVariant> variants = library.Variants(); int n = library.PatchSize; float cell = library.CellSize;
            for (int pz = minZ; pz <= maxZ; pz++) for (int px = minX; px <= maxX; px++)
            {
                if (!Field.HasSite(px,pz)) continue;
                Vector2 center = Field.SiteCenter(px,pz);
                if (Field.RiverDistance(center.x,center.y) < Profile.RiverWidth*2.8f) continue;
                var constraints = new ulong[n*n]; float angle=TerrainField.Random01(px,pz,Seed+339)*360;
                Quaternion rotation = Quaternion.Euler(0,angle,0);
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                {
                    Vector3 local = rotation*new Vector3((x-(n-1)*.5f)*cell,0,(z-(n-1)*.5f)*cell);
                    Vector3 normal = Field.Normal(center.x+local.x,center.y+local.z);
                    float slope = Vector3.Angle(normal,Vector3.up);
                    for (int v=0;v<variants.Count;v++) if (library.Modules[variants[v].Module].Empty
                        || slope <= library.Modules[variants[v].Module].MaxSlope) constraints[z*n+x] |= 1UL<<v;
                }
                WfcResult solved=SocketWfc.Solve(variants,n,n,TerrainField.Hash(px,pz,Seed+341),constraints);
                if (!solved.Success) { WfcFailures++; Debug.LogWarning(solved.Error); continue; }
                for (int z=0;z<n;z++) for(int x=0;x<n;x++)
                {
                    ModuleVariant variant=variants[solved.Cells[z*n+x]];
                    TerrainModule module=library.Modules[variant.Module]; if(module.Empty || !module.Mesh) continue;
                    Vector3 local=rotation*new Vector3((x-(n-1)*.5f)*cell,0,(z-(n-1)*.5f)*cell);
                    float wx=center.x+local.x,wz=center.y+local.z;
                    if(wx<x0 || wx>=x0+size || wz<z0 || wz>=z0+size) continue;
                    CreateProp(module.Mesh,parent,new Vector3(wx-x0,Field.Height(wx,wz)+module.HeightOffset,wz-z0),
                        rotation*Quaternion.Euler(0,variant.Rotation*90,0),Vector3.one,module.Tint);
                }
            }
        }
        private void CreateProp(Mesh mesh,Transform parent,Vector3 position,Quaternion rotation,Vector3 scale,Color tint)
        {
            var go=new GameObject(mesh.name); go.transform.SetParent(parent,false);
            go.transform.localPosition=position;go.transform.localRotation=rotation;go.transform.localScale=scale;
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=Profile.PropMaterial;
            if(_tint==null)_tint=new MaterialPropertyBlock();
            _tint.Clear();_tint.SetColor("_Color",tint);renderer.SetPropertyBlock(_tint);
        }
        public void SetDecoration(bool value) { ShowDecoration=value;foreach(Tile t in _tiles.Values)t.Props.SetActive(value); }
        public void SetModules(bool value) { ShowModules=value;foreach(Tile t in _tiles.Values)t.Modules.SetActive(value); }
        private void Release(Tile tile)
        {
            if(tile.Details!=null)foreach(Mesh mesh in tile.Details.OwnedMeshes)if(mesh)Destroy(mesh);
            if(tile.Mesh) { TriangleCount-=tile.Mesh.triangles.Length/3; Destroy(tile.Mesh); }
            if(tile.WaterMesh) Destroy(tile.WaterMesh);
            if(tile.Root) Destroy(tile.Root);
        }
        private void OnDestroy()
        {
            _epoch++; foreach(Tile t in _tiles.Values)Release(t);_tiles.Clear();
            if(_lineMaterial)Destroy(_lineMaterial);
        }
        private void OnRenderObject()
        {
            if(!ShowBoundaries || !Profile || Surface==null)return;
            if(!_lineMaterial) { _lineMaterial=new Material(Shader.Find("Hidden/Internal-Colored"));
                _lineMaterial.SetInt("_ZTest",(int)CompareFunction.LessEqual);_lineMaterial.SetInt("_ZWrite",0); }
            _lineMaterial.SetPass(0);GL.Begin(GL.LINES);GL.Color(new Color(.2f,.8f,.85f,.85f));
            int size=Profile.ChunkSize;
            foreach(Vector2Int key in _tiles.Keys)
                for(int i=0;i<size;i++)
                {
                    DrawLine(key.x*size+i,key.y*size,key.x*size+i+1,key.y*size);
                    DrawLine(key.x*size,key.y*size+i,key.x*size,key.y*size+i+1);
                }
            GL.End();
        }
        private void DrawLine(float ax,float az,float bx,float bz)
        {
            GL.Vertex3(ax-WorldOrigin.x,Surface.Sample(ax,az).Height+.04f,az-WorldOrigin.y);
            GL.Vertex3(bx-WorldOrigin.x,Surface.Sample(bx,bz).Height+.04f,bz-WorldOrigin.y);
        }
    }
}
