using System;
using System.IO;
using System.Linq;
using BinGames.TerrainVisual;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BinGames.TerrainVisual.Editor
{
    [InitializeOnLoad]
    public static class TerrainLabBootstrap
    {
        public const string AssetsRoot = "Assets/GameRes/Raw/TerrainLab";
        public const string ScenePath = AssetsRoot + "/Scenes/TerrainLab.unity";
        private const string Evidence = "F:/Project/BinGames/production/qa/evidence/pcg-terrain-lab";
        private static double _playStarted;
        private static int _errors;
        private static bool _captured;
        private const string RunningKey="PCG.TerrainLab.ValidationRunning";
        private static double _requestCheck;
        private static int _stage;
        private static double _stageAfter;
        private static readonly System.Collections.Generic.List<PlayStage> Stages=new System.Collections.Generic.List<PlayStage>();
        static TerrainLabBootstrap()
        {
            EditorApplication.delayCall += CheckRequest;
            string root=Path.GetDirectoryName(Application.dataPath);
            if(File.Exists(Path.Combine(root,"TerrainLabBootstrap.request")) || File.Exists(Path.Combine(root,"TerrainLabBuild.request")))
                EditorApplication.update += PollRequest;
            Application.logMessageReceived += OnLog;
            if(SessionState.GetBool(RunningKey,false))
            {
                _playStarted=SessionState.GetFloat(RunningKey+".Start",0);
                EditorApplication.update+=MonitorPlay;
            }
        }
        private static void PollRequest()
        {
            if(EditorApplication.timeSinceStartup<_requestCheck)return;
            _requestCheck=EditorApplication.timeSinceStartup+2;CheckRequest();
        }
        private static void OnLog(string condition,string stack,LogType type)
        { if(EditorApplication.isPlaying && (type==LogType.Error || type==LogType.Exception || type==LogType.Assert)
            && !stack.Contains("UnityEditor.Search"))_errors++; }
        private static void CheckRequest()
        {
            if(EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall+=CheckRequest;return; }
            string buildRequest=Path.Combine(Path.GetDirectoryName(Application.dataPath),"TerrainLabBuild.request");
            if(File.Exists(buildRequest) && !EditorApplication.isPlayingOrWillChangePlaymode
                && EditorPrefs.GetString("PCG.TerrainLab.Build."+Application.dataPath)!=File.ReadAllText(buildRequest))
            {
                EditorPrefs.SetString("PCG.TerrainLab.Build."+Application.dataPath,File.ReadAllText(buildRequest));BuildPlayer();
            }
            string request=Path.Combine(Path.GetDirectoryName(Application.dataPath),"TerrainLabBootstrap.request");
            if(!File.Exists(request))return;
            if(EditorPrefs.GetString("PCG.TerrainLab.Request."+Application.dataPath)==File.ReadAllText(request))return;
            if(EditorApplication.isPlaying){SessionState.SetBool(RunningKey,false);EditorApplication.isPlaying=false;return;}
            if(EditorApplication.isPlayingOrWillChangePlaymode)return;
            EditorPrefs.SetString("PCG.TerrainLab.Request."+Application.dataPath,File.ReadAllText(request));
            BuildAndValidate();
        }
        [MenuItem("地球归还/地形/建立测试场景")]
        public static void BuildAndValidate()
        {
            try
            {
                Directory.CreateDirectory(AssetsRoot+"/Generated");Directory.CreateDirectory(AssetsRoot+"/Scenes");
                Directory.CreateDirectory(Evidence);
                AssetDatabase.Refresh();
                TerrainProfile profile=BuildProfile();
                TerrainLabChecks.Run(profile,Evidence+"/checks.json");
                BuildScene(profile);
                _errors=0;_captured=false;
                _stage=0;Stages.Clear();_stageAfter=EditorApplication.timeSinceStartup+2;
                _playStarted=EditorApplication.timeSinceStartup;
                SessionState.SetFloat(RunningKey+".Start",(float)_playStarted);
                SessionState.SetBool(RunningKey,true);
                EditorApplication.update-=MonitorPlay;EditorApplication.update+=MonitorPlay;
                EditorApplication.isPlaying=true;
            }
            catch(Exception ex){File.WriteAllText(Evidence+"/failure.txt",ex.ToString());Debug.LogException(ex);}
        }
        public static TerrainProfile BuildProfile()
        {
            Material ground=MaterialAsset("Ground", "BinGames/Terrain/ContinuousGround");
            Material props=MaterialAsset("Props", "BinGames/Terrain/MatteProps");props.enableInstancing=true;
            Material water=MaterialAsset("Water", "BinGames/Terrain/MutedWater");
            var profile=AssetDatabase.LoadAssetAtPath<TerrainProfile>(AssetsRoot+"/TerrainProfile.asset");
            bool fresh=!profile;
            if(fresh){profile=ScriptableObject.CreateInstance<TerrainProfile>();AssetDatabase.CreateAsset(profile,AssetsRoot+"/TerrainProfile.asset");}
            profile.GroundMaterial=ground;profile.WaterMaterial=water;profile.PropMaterial=props;
            profile.RockMesh=NormalizeModel("pcg_rock_strata_0");
            profile.RockVariantMesh=NormalizeModel("pcg_rock_strata_1");
            profile.RubbleMesh=NormalizeModel("pcg_rubble");profile.DryTreeMesh=NormalizeModel("pcg_dry_tree");
            string libraryPath=AssetsRoot+"/TerrainModules.asset";
            var library=AssetDatabase.LoadAssetAtPath<TerrainModuleLibrary>(libraryPath);
            if(!library && File.Exists(libraryPath))throw new InvalidOperationException("模块库脚本引用无效，请检查素材库资源。");
            if(!library)
            {
                library=ScriptableObject.CreateInstance<TerrainModuleLibrary>();
                library.Modules.Add(new TerrainModule{Id="空地",Empty=true,Weight=5,Rotations=new[]{true,false,false,false}});
                Add(library,"墙直段","pcg_wall_straight",1,0,1,0,1.3f);
                Add(library,"墙转角","pcg_wall_corner",1,1,0,0,.8f);
                Add(library,"墙端头","pcg_wall_end",1,0,0,0,1.2f);
                Add(library,"管直段","pcg_pipe_straight",2,0,2,0,.8f);
                Add(library,"管转角","pcg_pipe_corner",2,2,0,0,.5f);
                Add(library,"管端头","pcg_pipe_end",2,0,0,0,.8f);
                library.Modules.Add(new TerrainModule{Id="桥墩残柱",Mesh=NormalizeModel("pcg_pillar"),Weight=.35f,
                    Tint=Color.white,Rotations=new[]{true,false,false,false}});
                AssetDatabase.CreateAsset(library,AssetsRoot+"/TerrainModules.asset");
            }
            foreach(TerrainModule module in library.Modules)
                if(module.Mesh && module.Mesh.name.StartsWith("pcg_"))module.Mesh=NormalizeModel(module.Mesh.name);
            profile.Library=library;water.SetColor("_Color",profile.Water);
            string detailPath=AssetsRoot+"/TerrainDetailKit.asset";
            var detail=AssetDatabase.LoadAssetAtPath<TerrainDetailKit>(detailPath);
            if(!detail){detail=ScriptableObject.CreateInstance<TerrainDetailKit>();AssetDatabase.CreateAsset(detail,detailPath);profile.HomeRadius=39;profile.Relief=22;}
            string[] models={"cliff_0","cliff_1","cliff_2","ruin_bay","factory_bay","ruin_window","ruin_door","ruin_broken","ruin_roof",
                "factory_window","factory_door","factory_broken","factory_roof","tank","home_core","bridge_deck","bridge_pier","scrub","crate"};
            detail.Models=models.Select(id=>NormalizeModel("pcg_"+id)).ToArray();profile.DetailKit=detail;EditorUtility.SetDirty(detail);
            ground.SetTexture("_SoilTex",ImportTexture("Orbis_Soil",false));
            ground.SetTexture("_CliffTex",ImportTexture("Orbis_Cliffs",false));
            ground.SetTexture("_SoilNormal",ImportTexture("Orbis_Soil_Normal",true));ground.SetFloat("_TextureStrength",.75f);EditorUtility.SetDirty(ground);
            props.SetTexture("_RockTex",ImportTexture("Orbis_Cliffs",false));props.SetFloat("_TextureStrength",.75f);EditorUtility.SetDirty(props);
            EditorUtility.SetDirty(profile);EditorUtility.SetDirty(library);AssetDatabase.SaveAssets();
            return profile;
        }
        private static void Add(TerrainModuleLibrary l,string id,string mesh,int n,int e,int s,int w,float weight)
        { l.Modules.Add(new TerrainModule{Id=id,Mesh=NormalizeModel(mesh),North=n,East=e,South=s,West=w,Weight=weight,Tint=Color.white}); }
        private static Texture2D ImportTexture(string name,bool normal)
        {
            string path=AssetsRoot+"/Textures/"+name+".png";var importer=AssetImporter.GetAtPath(path)as TextureImporter;
            if(importer==null)throw new InvalidOperationException("复用地表贴图未导入："+path);
            TextureImporterType type=normal?TextureImporterType.NormalMap:TextureImporterType.Default;
            if(importer.textureType!=type||importer.maxTextureSize!=2048||importer.wrapMode!=TextureWrapMode.Repeat)
            {importer.textureType=type;importer.maxTextureSize=2048;importer.wrapMode=TextureWrapMode.Repeat;importer.mipmapEnabled=true;importer.SaveAndReimport();}
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        private static Material MaterialAsset(string name,string shaderName)
        {
            string path=AssetsRoot+"/Generated/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);Shader shader=Shader.Find(shaderName);
            if(!shader)throw new InvalidOperationException("着色器未导入："+shaderName);
            if(!material){material=new Material(shader);AssetDatabase.CreateAsset(material,path);}
            else if(material.shader!=shader){material.shader=shader;EditorUtility.SetDirty(material);}
            return material;
        }
        private static Mesh NormalizeModel(string name)
        {
            string target=AssetsRoot+"/Generated/"+name+".asset";
            Mesh existing=AssetDatabase.LoadAssetAtPath<Mesh>(target);
            var model=AssetDatabase.LoadAssetAtPath<GameObject>(AssetsRoot+"/Models/"+name+".fbx");
            if(!model)throw new InvalidOperationException("Blender 模型尚未导入："+name);
            MeshFilter[] filters=model.GetComponentsInChildren<MeshFilter>(true);
            if(filters.Length==0)throw new InvalidOperationException("模型没有网格："+name);
            var mesh=new Mesh{name=name};
            var combine=filters.Select(f=>new CombineInstance{mesh=f.sharedMesh,transform=f.transform.localToWorldMatrix}).ToArray();
            mesh.CombineMeshes(combine,true,true);mesh.RecalculateBounds();
            // The authored FBX forward conversion turns Blender +X/+Y into Unity -X/-Z.
            // Rotate 180 degrees so sockets north/east coincide with model +Z/+X.
            var vertices=mesh.vertices;var normals=mesh.normals;
            for(int i=0;i<vertices.Length;i++){vertices[i].x=-vertices[i].x;vertices[i].z=-vertices[i].z;
                if(i<normals.Length){normals[i].x=-normals[i].x;normals[i].z=-normals[i].z;}}
            mesh.vertices=vertices;mesh.normals=normals;mesh.RecalculateBounds();
            if(mesh.triangles.Length/3>1500)throw new InvalidOperationException("场景件超出 1500 三角预算："+name);
            if(mesh.colors.Length!=mesh.vertexCount)
            {var colors=new Color[mesh.vertexCount];for(int i=0;i<colors.Length;i++)colors[i]=new Color(.48f,.47f,.44f,1);mesh.colors=colors;}
            else {var colors=mesh.colors;for(int i=0;i<colors.Length;i++)colors[i]=colors[i].linear;mesh.colors=colors;}
            if(existing){EditorUtility.CopySerialized(mesh,existing);Object.DestroyImmediate(mesh);EditorUtility.SetDirty(existing);return existing;}
            AssetDatabase.CreateAsset(mesh,target);return mesh;
        }
        private static void BuildScene(TerrainProfile profile)
        {
            Scene scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor=new Color(.53f,.58f,.61f);
            RenderSettings.ambientEquatorColor=new Color(.43f,.41f,.36f);
            RenderSettings.ambientGroundColor=new Color(.26f,.24f,.21f);
            RenderSettings.fog=true;RenderSettings.fogMode=FogMode.Linear;
            RenderSettings.fogStartDistance=150;RenderSettings.fogEndDistance=330;
            RenderSettings.fogColor=new Color32(201,194,174,255);
            var lightGo=new GameObject("下午日光");Light sun=lightGo.AddComponent<Light>();sun.type=LightType.Directional;
            sun.color=new Color(1,.88f,.71f);sun.intensity=1.15f;sun.shadows=LightShadows.Soft;
            sun.shadowStrength=.58f;sun.shadowBias=.04f;lightGo.transform.rotation=Quaternion.Euler(38,-38,0);RenderSettings.sun=sun;
            Material sky=MaterialAsset("Sky","BinGames/Terrain/WastelandSky");sky.SetColor("_SkyTint",new Color(.44f,.54f,.65f));
            sky.SetColor("_GroundColor",RenderSettings.fogColor);sky.SetFloat("_Exposure",1);RenderSettings.skybox=sky;
            var cameraGo=new GameObject("Main Camera");cameraGo.tag="MainCamera";Camera camera=cameraGo.AddComponent<Camera>();
            camera.backgroundColor=RenderSettings.fogColor;camera.clearFlags=CameraClearFlags.Skybox;
            camera.fieldOfView=52;camera.nearClipPlane=.3f;camera.farClipPlane=520;
            cameraGo.transform.position=new Vector3(-54,58,-65);cameraGo.transform.rotation=Quaternion.Euler(43,31,0);
            var worldGo=new GameObject("无缝地形");TerrainStream world=worldGo.AddComponent<TerrainStream>();
            world.Profile=profile;world.Seed=profile.Seed;world.Viewer=cameraGo.transform;
            TerrainLabViewer viewer=cameraGo.AddComponent<TerrainLabViewer>();viewer.World=world;
            QualitySettings.shadowDistance=115;QualitySettings.vSyncCount=0;
            Application.targetFrameRate=120;
            EditorSceneManager.SaveScene(scene,ScenePath);
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            Selection.activeGameObject=worldGo;
        }
        private static void MonitorPlay()
        {
            if(!EditorApplication.isPlaying)return;
            TerrainStream stream=Object.FindFirstObjectByType<TerrainStream>();
            int expected=stream && stream.Profile?(2*stream.Profile.ViewRadius+1)*(2*stream.Profile.ViewRadius+1):int.MaxValue;
            if(stream && stream.ReadyCount==expected && stream.PendingCount<=0 && EditorApplication.timeSinceStartup>_stageAfter)
            {
                if(!stream.Profile.Library || (_stage==0 && stream.ModuleCount==0))
                {
                    File.WriteAllText(Evidence+"/play.json","{\"pass\":false,\"reason\":\"连接废墟未加载\"}");
                    SessionState.SetBool(RunningKey,false);EditorApplication.update-=MonitorPlay;EditorApplication.isPlaying=false;
                    Debug.LogError("试玩中未加载连接废墟；不能仅凭地表检查宣告成功。");return;
                }
                Camera camera=Camera.main;
                string[]names={"valley","ruins","negative-crossing","floating-origin","seed-90217","seed-0","valley-final"};
                Capture(camera,Evidence+"/"+names[_stage]+".png");
                Stages.Add(new PlayStage{view=names[_stage],seed=stream.Seed,chunks=stream.ReadyCount,
                    modules=stream.ModuleCount,wfcFailures=stream.WfcFailures,origin=stream.WorldOrigin.ToString()});
                if(_stage==0)Frame(camera,stream,new Vector3(-76,32,26),new Vector3(-48,8,66));
                else if(_stage==1)Frame(camera,stream,new Vector3(-195,45,-75),new Vector3(-130,1,-10));
                else if(_stage==2)Frame(camera,stream,new Vector3(1240,44,1280),new Vector3(1270,0,1320));
                else if(_stage==3){stream.Rebuild(90217);camera.GetComponent<TerrainLabViewer>().ResetView();}
                else if(_stage==4)stream.Rebuild(0);
                else if(_stage==5){stream.Rebuild(41729);camera.GetComponent<TerrainLabViewer>().ResetView();}
                else if(!_captured)
                {
                    string audit="";
                    foreach(var mesh in new[]{stream.Profile.RockMesh,stream.Profile.Library?stream.Profile.Library.Modules[2].Mesh:null})
                    {
                        if(!mesh){audit+="MISSING MESH\n";continue;}
                        audit+=mesh.name+" bounds="+mesh.bounds+" firstColor="+(mesh.colors.Length>0?mesh.colors[0].ToString():"none")+"\n";
                    }
                    File.WriteAllText(Evidence+"/mesh-audit.txt",audit);
                    Capture(Camera.main,Evidence+"/valley.png");
                    File.WriteAllText(Evidence+"/play.json",JsonUtility.ToJson(new PlayReport{pass=_errors==0&&stream.WfcFailures==0,
                        errors=_errors,chunks=stream.ReadyCount,triangles=stream.TriangleCount,wfcFailures=stream.WfcFailures,seed=stream.Seed,
                        stages=Stages.ToArray()},true));
                    _captured=true;Debug.Log("PCG-TERRAIN-PLAY: "+(_errors==0&&stream.WfcFailures==0?"PASS":"FAIL"));
                    SessionState.SetBool(RunningKey,false);
                    EditorApplication.isPlaying=false;EditorApplication.update-=MonitorPlay;
                }
                _stage++;_stageAfter=EditorApplication.timeSinceStartup+2;
            }
            else if(EditorApplication.timeSinceStartup-_playStarted>120)
            {File.WriteAllText(Evidence+"/failure.txt","地形 Play 验证超时，已生成："+(stream?stream.ReadyCount:0));SessionState.SetBool(RunningKey,false);EditorApplication.isPlaying=false;EditorApplication.update-=MonitorPlay;}
        }
        private static void Frame(Camera camera,TerrainStream stream,Vector3 position,Vector3 target)
        {Vector3 offset=new Vector3(stream.WorldOrigin.x,0,stream.WorldOrigin.y);camera.transform.position=position-offset;camera.transform.LookAt(target-offset);}
        [Serializable] private sealed class PlayStage {public string view,origin;public int seed,chunks,modules,wfcFailures;}
        [Serializable] private sealed class PlayReport {public bool pass;public int errors,chunks,triangles,wfcFailures,seed;public PlayStage[]stages;}
        [MenuItem("地球归还/地形/生成独立试玩版")]
        public static void BuildPlayer()
        {
            string root=Path.GetDirectoryName(Application.dataPath);
            string output=Path.Combine(root,"Builds/TerrainLab/TerrainLab.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{
                scenes=new[]{ScenePath},target=BuildTarget.StandaloneWindows64,
                locationPathName=output,options=BuildOptions.None});
            bool pass=report.summary.result==UnityEditor.Build.Reporting.BuildResult.Succeeded;
            File.WriteAllText(Evidence+"/build.json","{\"pass\":"+(pass?"true":"false")+",\"errors\":"+report.summary.totalErrors+",\"bytes\":"+report.summary.totalSize+"}");
            if(!pass)throw new InvalidOperationException("独立试玩版构建失败："+report.summary.result);
            Debug.Log("PCG-TERRAIN-BUILD: PASS; "+output);
        }
        public static void Capture(Camera camera,string path)
        {
            if(!camera)throw new InvalidOperationException("缺少测试镜头。");
            var rt=new RenderTexture(1600,900,24);var texture=new Texture2D(1600,900,TextureFormat.RGB24,false);
            RenderTexture old=RenderTexture.active,oldTarget=camera.targetTexture;
            try {camera.targetTexture=rt;camera.Render();RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,1600,900),0,0);
                texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());}
            finally{camera.targetTexture=oldTarget;RenderTexture.active=old;Object.DestroyImmediate(texture);Object.DestroyImmediate(rt);}
        }
    }
}
