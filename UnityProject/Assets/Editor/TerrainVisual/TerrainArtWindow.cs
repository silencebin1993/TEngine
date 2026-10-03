using System.Collections.Generic;
using BinGames.TerrainVisual;
using UnityEditor;
using UnityEngine;

namespace BinGames.TerrainVisual.Editor
{
    public sealed class TerrainArtWindow : EditorWindow
    {
        private TerrainProfile _profile;
        private TerrainModuleLibrary _library;
        private Vector2 _scroll;
        private UnityEditor.Editor _profileEditor;
        private int _selected;
        private string _status="";
        [MenuItem("地球归还/地形素材管理")]
        public static void Open(){GetWindow<TerrainArtWindow>("地形素材管理").minSize=new Vector2(460,520);}
        private void OnEnable()
        {
            _profile=AssetDatabase.LoadAssetAtPath<TerrainProfile>(TerrainLabBootstrap.AssetsRoot+"/TerrainProfile.asset");
            if(_profile)_library=_profile.Library;
        }
        private void OnGUI()
        {
            EditorGUILayout.LabelField("地球归还 · 地形素材与连接规则",EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("连续地表负责自然过渡；连接规则负责废墟和管道。0 表示无连接；其他编号相同才能相接。旋转会同时旋转模型与四边接口。",MessageType.Info);
            _profile=(TerrainProfile)EditorGUILayout.ObjectField("地貌配置",_profile,typeof(TerrainProfile),false);
            EditorGUI.BeginChangeCheck();
            _library=(TerrainModuleLibrary)EditorGUILayout.ObjectField("连接模块库",_library,typeof(TerrainModuleLibrary),false);
            if(EditorGUI.EndChangeCheck() && _profile){Undo.RecordObject(_profile,"更换连接模块库");_profile.Library=_library;EditorUtility.SetDirty(_profile);}
            if(!_profile || !_library){EditorGUILayout.HelpBox("先通过「地形 → 建立测试场景」创建配置。",MessageType.Warning);return;}
            _scroll=EditorGUILayout.BeginScrollView(_scroll);
            if(_profile.DetailKit)
            {
                EditorGUILayout.LabelField("建筑聚落素材与分层概率",EditorStyles.boldLabel);
                var kit=new SerializedObject(_profile.DetailKit);kit.Update();
                EditorGUILayout.PropertyField(kit.FindProperty("MaxRuinFloors"),new GUIContent("废墟最多楼层"));
                var weights=kit.FindProperty("BuildingWeights");
                string[] bayLabels={"上层留空","水泥底层封顶","水泥底层续层","水泥楼层封顶","水泥楼层续层","金属底层封顶","金属底层续层","金属楼层封顶","金属楼层续层"};
                for(int i=0;i<Mathf.Min(weights.arraySize,bayLabels.Length);i++)
                {var weight=weights.GetArrayElementAtIndex(i);weight.floatValue=EditorGUILayout.Slider(bayLabels[i],weight.floatValue,.001f,8);}
                kit.ApplyModifiedProperties();EditorGUILayout.HelpBox("六面连接自动检查支撑、屋顶与立面族；地块、道路和入口由聚落布局约束。",MessageType.Info);
            }
            EditorGUI.BeginChangeCheck();
            float cellSize=EditorGUILayout.FloatField("模块格宽（米）",_library.CellSize);
            int patchSize=EditorGUILayout.IntSlider("遗迹片区边长",_library.PatchSize,3,12);
            if(EditorGUI.EndChangeCheck())
            {Undo.RecordObject(_library,"修改遗迹片区尺寸");_library.CellSize=Mathf.Max(1,cellSize);_library.PatchSize=patchSize;EditorUtility.SetDirty(_library);}
            if(_library.Modules.Count>0)
            {
                string[]names=_library.Modules.ConvertAll(m=>m.Id).ToArray();_selected=Mathf.Clamp(_selected,0,names.Length-1);
                _selected=EditorGUILayout.Popup("编辑模块",_selected,names);
                TerrainModule m=_library.Modules[_selected];EditorGUI.BeginChangeCheck();
                string id=EditorGUILayout.TextField("名称",m.Id);
                Mesh mesh=(Mesh)EditorGUILayout.ObjectField("网格",m.Mesh,typeof(Mesh),false);
                float weight=EditorGUILayout.Slider("出现权重",m.Weight,.001f,12);
                bool empty=EditorGUILayout.Toggle("空地模块",m.Empty);
                int north=EditorGUILayout.IntField("北边接口",m.North),east=EditorGUILayout.IntField("东边接口",m.East);
                int south=EditorGUILayout.IntField("南边接口",m.South),west=EditorGUILayout.IntField("西边接口",m.West);
                var rotations=new bool[4];EditorGUILayout.BeginHorizontal();EditorGUILayout.PrefixLabel("允许角度");
                for(int r=0;r<4;r++)rotations[r]=GUILayout.Toggle(m.Rotations!=null&&m.Rotations.Length>r&&m.Rotations[r],r*90+"°");
                EditorGUILayout.EndHorizontal();
                float offset=EditorGUILayout.FloatField("离地偏移",m.HeightOffset),slope=EditorGUILayout.Slider("最大坡度",m.MaxSlope,0,60);
                Color tint=EditorGUILayout.ColorField("色调",m.Tint);
                if(EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(_library,"修改地形连接模块");m.Id=id;m.Mesh=mesh;m.Weight=weight;m.Empty=empty;
                    m.North=north;m.East=east;m.South=south;m.West=west;m.Rotations=rotations;m.HeightOffset=offset;m.MaxSlope=slope;m.Tint=tint;
                    EditorUtility.SetDirty(_library);
                }
                if(m.Mesh)EditorGUILayout.LabelField("三角形预算",m.Mesh.triangles.Length/3+" / 1500");
                Rect rect=GUILayoutUtility.GetRect(160,100);GUI.Box(rect,"");
                GUI.Label(new Rect(rect.center.x-35,rect.y+6,80,20),"北 "+m.North);
                GUI.Label(new Rect(rect.x+12,rect.center.y-8,80,20),"西 "+m.West);
                GUI.Label(new Rect(rect.xMax-70,rect.center.y-8,70,20),"东 "+m.East);
                GUI.Label(new Rect(rect.center.x-35,rect.yMax-24,80,20),"南 "+m.South);
            }
            if(GUILayout.Button("添加模块"))
            {Undo.RecordObject(_library,"添加地形模块");_library.Modules.Add(new TerrainModule{Id="新模块"});_selected=_library.Modules.Count-1;EditorUtility.SetDirty(_library);}
            EditorGUILayout.Space();EditorGUILayout.LabelField("地貌与环境色板",EditorStyles.boldLabel);
            UnityEditor.Editor.CreateCachedEditor(_profile,null,ref _profileEditor);
            var settings=_profileEditor.serializedObject;settings.Update();
            string[]fields={"Seed","ChunkSize","ViewRadius","Relief","LandformScale","HomeRadius","RiverWidth","WaterLevel","PropDensity","PollutionAmount","Restoration",
                "Soil","Rock","Sand","Pollution","RestoredSoil","RestoredGrass","Water"};
            string[]labels={"默认种子","区块边长（自动对齐 4 米）","可见范围","地形起伏","山势尺度","家园平地半径","河道宽度","水位","场景件密度","污染色比例","家园恢复程度",
                "废土地色","岩层颜色","沙土颜色","污染颜色","恢复土地","恢复草色","水体颜色"};
            for(int i=0;i<fields.Length;i++)EditorGUILayout.PropertyField(settings.FindProperty(fields[i]),new GUIContent(labels[i]));
            settings.ApplyModifiedProperties();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            if(GUILayout.Button("检查连接规则"))Validate();
            if(GUILayout.Button("保存并刷新预览"))
            {
                AssetDatabase.SaveAssets();TerrainStream stream=Object.FindFirstObjectByType<TerrainStream>();
                if(stream && Application.isPlaying)stream.Rebuild(stream.Seed);
            }
            EditorGUILayout.EndHorizontal();
            if(!string.IsNullOrEmpty(_status))EditorGUILayout.HelpBox(_status,MessageType.Info);
        }
        private void Validate()
        {
            List<ModuleVariant>v=_library.Variants();int n=_library.PatchSize,success=0;
            for(uint seed=1;seed<=12;seed++)if(SocketWfc.CheckConnections(SocketWfc.Solve(v,n,n,seed),v,n,n))success++;
            _status=$"旋转变体 {v.Count} / 63；12 个测试种子通过 {success} 个。";
            List<string>errors=TerrainLabChecks.CheckModelSockets(_library,out int sockets);
            _status+=$"\n模型接口已检查 {sockets} 处。";
            foreach(string error in errors)_status+="\n"+error;
        }
        private void OnDisable(){if(_profileEditor)DestroyImmediate(_profileEditor);}
    }
}
