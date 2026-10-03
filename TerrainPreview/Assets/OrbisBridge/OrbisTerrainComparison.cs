using System;
using System.Threading.Tasks;
using BinGames.TerrainVisual;
using Orbis;
using Orbis.Components;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

// Optional preview adapter. Runs the imported library's actual Burst mesh job, adaptive
// quadtree and renderer in a dedicated ECS world. No DOTS dependency enters the campaign module.
public sealed class OrbisTerrainComparison:MonoBehaviour
{
    public const int Size=1024,Resolution=1025;
    private const float HeightScale=128,BaseHeight=0;
    private TerrainStream terrain;private Unity.Entities.World world;
    private BlobAssetReference<OrbisBaker.NativeHeightmap> heightmap;
    private Material material;private OrbisConfigurator config;
    private QuadtreeSchedulingSystem scheduling;
    private Task<ushort[]> sampling;private bool requested;private int sampledSeed;
    private Vector2Int origin;
    public string Status {get;private set;}="连续地图 · 可切换 Orbis 四叉树对比";
    public bool Ready=>world!=null&&sampling==null&&MeshCount>4;
    public int MeshCount=>world==null?0:world.GetExistingSystemManaged<QuadtreeSchedulingSystem>().renderMeshes.Count;
    public int Triangles {get {int n=0;if(world!=null)foreach(var item in world.GetExistingSystemManaged<QuadtreeSchedulingSystem>().renderMeshes.Values)n+=(int)item.mesh.GetIndexCount(0)/3;return n;}}
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if(!FindFirstObjectByType<TerrainStream>())return;
        var adapter=new GameObject("Orbis 原生地形对比").AddComponent<OrbisTerrainComparison>();
        TerrainLabViewer.SwitchOrbis=adapter.Toggle;TerrainLabViewer.OrbisStatus=()=>adapter.Status;
    }
    public void Toggle(TerrainStream target)
    {
        terrain=target;requested=!requested;
        if(!requested){StopWorld();Status="连续地图 · Orbis 对比已关闭";return;}
        Begin();
    }
    private void Begin()
    {
        if(terrain.Field==null){requested=false;Status="当前数据源未提供地貌快照";return;}
        StopWorld();sampledSeed=terrain.Seed;var field=terrain.Field;
        Status="准备 Orbis 高度数据…";
        // Immutable field snapshot: reading samples cannot touch Unity objects on this worker.
        sampling=Task.Run(()=>
        {
            var data=new ushort[Resolution*Resolution];float spacing=Size/(Resolution-1f);
            for(int z=0;z<Resolution;z++)for(int x=0;x<Resolution;x++)
                // Orbis centres R16 around 0.5 before applying heightScale.
                data[z*Resolution+x]=(ushort)Mathf.RoundToInt(Mathf.Clamp01(.5f+(field.Height(x*spacing-Size/2f,z*spacing-Size/2f)-BaseHeight)/HeightScale)*65535);
            return data;
        });
    }
    private void LateUpdate()
    {
        if(!requested)return;
        if(terrain.Seed!=sampledSeed){Begin();return;}
        Vector3 camera=terrain.Viewer.position+new Vector3(terrain.WorldOrigin.x,0,terrain.WorldOrigin.y);
        if(Mathf.Abs(camera.x)>Size/2-60||Mathf.Abs(camera.z)>Size/2-60)
        {requested=false;StopWorld();Status="已离开 1 km Orbis 对比区，恢复连续地图";return;}
        if(sampling!=null)
        {
            if(!sampling.IsCompleted)return;
            if(sampling.IsFaulted){Debug.LogException(sampling.Exception);sampling=null;requested=false;Status="Orbis 高度准备失败";return;}
            ushort[] data=sampling.Result;sampling=null;StartWorld(data);
        }
        if(world==null)return;
        if(origin!=terrain.WorldOrigin)MoveOrigin(terrain.WorldOrigin);
        world.SetTime(new Unity.Core.TimeData(Time.timeAsDouble,Time.deltaTime));
        world.GetExistingSystemManaged<SimulationSystemGroup>().Update();
        world.GetExistingSystemManaged<PresentationSystemGroup>().Update();
        // Keep the original surface visible while the root job first becomes drawable.
        terrain.SetExternalGround(MeshCount>0);
        Status=$"Orbis 原生四叉树 · {MeshCount} 网格\n{Triangles:N0} 地形三角 · 1 km 对比区";
    }
    private void StartWorld(ushort[] data)
    {
        using(var builder=new BlobBuilder(Allocator.Temp))
        {
            ref var root=ref builder.ConstructRoot<OrbisBaker.NativeHeightmap>();
            var heights=builder.Allocate(ref root.Heights,data.Length);var biomes=builder.Allocate(ref root.Biomes,data.Length);
            for(int i=0;i<data.Length;i++){heights[i]=data[i];biomes[i]=32768;}
            heightmap=builder.CreateBlobAssetReference<OrbisBaker.NativeHeightmap>(Allocator.Persistent);
        }
        config=gameObject.AddComponent<OrbisConfigurator>();config.disableFloatingOrigin=true;config.ditherLODTransitions=false;
        config.maximumJobLifetime=8;config.lodDistances.Clear();
        for(int lod=0;lod<=5;lod++)config.lodDistances.Add(new OrbisConfigurator.LodDistanceTransistion{lod=lod,distance=640f/Mathf.Pow(2,lod)});
        OrbisConfigurator.instance=config;OrbisConfigurator.initializationDone=true;
        world=new Unity.Entities.World("Terrain lab Orbis comparison");
        var simulation=world.GetOrCreateSystemManaged<SimulationSystemGroup>();
        var group=world.GetOrCreateSystemManaged<OrbisSystemGroup>();
        group.AddSystemToUpdateList(world.GetOrCreateSystemManaged<QuadtreeSchedulingSystem>());
        scheduling=world.GetExistingSystemManaged<QuadtreeSchedulingSystem>();
        group.AddSystemToUpdateList(world.GetOrCreateSystem<QuadtreeLODSubdivisionSystem>());
        group.SortSystems();simulation.AddSystemToUpdateList(group);
        simulation.AddSystemToUpdateList(world.GetOrCreateSystemManaged<TransformSystemGroup>());
        simulation.AddSystemToUpdateList(world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>());simulation.SortSystems();
        var presentation=world.GetOrCreateSystemManaged<PresentationSystemGroup>();
        presentation.AddSystemToUpdateList(world.GetOrCreateSystemManaged<OrbisRenderingSystem>());presentation.SortSystems();
        material=new Material(terrain.Profile.GroundMaterial);material.SetFloat("_UseOrbis",1);
        material.SetColor("_OrbisSoil",terrain.Profile.Soil);material.SetColor("_OrbisRock",terrain.Profile.Rock);
        material.SetColor("_OrbisRestored",terrain.Profile.RestoredSoil);material.SetColor("_OrbisGrass",terrain.Profile.RestoredGrass);
        material.SetFloat("_OrbisRestoration",terrain.Profile.Restoration);
        EntityManager manager=world.EntityManager;Entity entity=manager.CreateEntity();
        manager.AddComponentData(entity,new LocalToWorld{Value=float4x4.TRS(new float3(-terrain.WorldOrigin.x,BaseHeight,-terrain.WorldOrigin.y),quaternion.identity,1)});
        manager.AddComponentData(entity,new OrbisBaker.TerrainTexture{Data=heightmap});
        manager.AddComponentData(entity,new HeightmapSettings{heightMapID=1,heightScale=HeightScale});
        manager.AddComponentData(entity,new NeedsGeneratedGridTag());
        manager.AddComponentData(entity,new NodeSettings{generateSkirts=true,vertexCount=33,vertexDistances=Size/32f});
        manager.AddComponentData(entity,new QuadtreeLodNode{extents=Size/2f,uvMin=0,uvMax=1});
        manager.AddComponentData(entity,new QuadtreeLODLevel());manager.AddComponentData(entity,new NodeBounds());
        manager.AddComponentData(entity,new FloatingOriginRepositionTag());
        manager.AddComponentData(entity,BiomeSettings.standard());
        // Rendering comparison uses no collision invoker. Production physics stays independent.
        manager.AddComponentData(entity,new QuadtreeSettings{MaxLOD=5,collisionLOD=-1,ID=4242,lodDistanceMultiplier=1});
        manager.AddSharedComponentManaged(entity,new MaterialReference{Value=material});
        manager.AddBuffer<VegetationBuffer>(entity);manager.AddBuffer<VegetationSettings>(entity);
        origin=terrain.WorldOrigin;
    }
    public float HeightError()
    {
        if(world==null||!world.IsCreated)return float.PositiveInfinity;float maximum=0;
        EntityManager manager=world.EntityManager;
        using(var query=manager.CreateEntityQuery(typeof(HasMeshTag),typeof(LocalToWorld)))
        using(var entities=query.ToEntityArray(Allocator.Temp))
            foreach(Entity entity in entities)
            {
                if(!scheduling.renderMeshes.TryGetValue(entity.Index,out var item))continue;
                Matrix4x4 matrix=manager.GetComponentData<LocalToWorld>(entity).Value;Vector3[] vertices=item.mesh.vertices;
                // Skirts are deliberately below the surface; validate only the 33×33 grid.
                for(int i=0;i<Mathf.Min(33*33,vertices.Length);i++)
                {
                    Vector3 point=matrix.MultiplyPoint3x4(vertices[i]);
                    float expected=terrain.Field.Height(point.x+terrain.WorldOrigin.x,point.z+terrain.WorldOrigin.y);
                    maximum=Mathf.Max(maximum,Mathf.Abs(point.y-expected));
                }
            }
        return maximum;
    }
    // Graphics.DrawMesh is queued for the current frame. An explicit camera screenshot
    // before LateUpdate needs the imported renderer's meshes queued for that camera first.
    public void QueueCameraCapture(Camera camera)
    {
        if(world==null||!world.IsCreated)return;EntityManager manager=world.EntityManager;
        using(var query=manager.CreateEntityQuery(typeof(HasMeshTag),typeof(LocalToWorld)))
        using(var entities=query.ToEntityArray(Allocator.Temp))
            foreach(Entity entity in entities)if(scheduling.renderMeshes.TryGetValue(entity.Index,out var item))
                Graphics.DrawMesh(item.mesh,(Matrix4x4)manager.GetComponentData<LocalToWorld>(entity).Value,material,0,camera);
    }
    private void MoveOrigin(Vector2Int next)
    {
        Vector2Int difference=next-origin;EntityManager manager=world.EntityManager;
        using(var query=manager.CreateEntityQuery(typeof(LocalToWorld),typeof(FloatingOriginRepositionTag)))
        using(var entities=query.ToEntityArray(Allocator.Temp))
            foreach(Entity entity in entities)
            {
                var transform=manager.GetComponentData<LocalToWorld>(entity);transform.Value.c3.xyz-=new float3(difference.x,0,difference.y);manager.SetComponentData(entity,transform);
            }
        origin=next;
    }
    private void StopWorld()
    {
        if(terrain)terrain.SetExternalGround(false);
        if(world!=null)
        {
            var meshes=new System.Collections.Generic.List<Mesh>();
            if(scheduling!=null){foreach(var item in scheduling.renderMeshes.Values)meshes.Add(item.mesh);foreach(var mesh in scheduling.fadeoutMeshes.Values)meshes.Add(mesh);}
            if(world.IsCreated)world.Dispose();world=null;scheduling=null;foreach(Mesh mesh in meshes)if(mesh)Destroy(mesh);
        }
        if(heightmap.IsCreated)heightmap.Dispose();
        if(material)Destroy(material);if(config)Destroy(config);
        OrbisConfigurator.initializationDone=false;OrbisConfigurator.instance=null;
        sampling=null;
    }
    private void OnDestroy(){StopWorld();TerrainLabViewer.SwitchOrbis=null;TerrainLabViewer.OrbisStatus=null;}
}
