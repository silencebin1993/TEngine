using UnityEngine;

namespace BinGames.TerrainVisual
{
    // Preview-only controls: no dependency on the campaign InputRouter or save data.
    public sealed class TerrainLabViewer : MonoBehaviour
    {
        public static System.Action<TerrainStream> SwitchOrbis;
        public static System.Func<string> OrbisStatus;
        public TerrainStream World;
        private float _pitch=48,_yaw=25;
        private string _seed;
        private bool _ui=true;
        private Font _font;
        private GUIStyle _label,_button;
        private float _smoothedFrame;
        private void Start()
        {
            Application.runInBackground=true;Application.targetFrameRate=120;QualitySettings.vSyncCount=0;
            _seed=World.Seed.ToString();
            _font=Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei","SimHei","Arial"},16);
            ResetView();
        }
        public void ResetView()
        {
            transform.position=new Vector3(-78-World.WorldOrigin.x,52, -70-World.WorldOrigin.y);
            transform.LookAt(new Vector3(5-World.WorldOrigin.x,3,22-World.WorldOrigin.y));
            _pitch=transform.eulerAngles.x;_yaw=transform.eulerAngles.y;
        }
        private void Update()
        {
            _smoothedFrame=Mathf.Lerp(_smoothedFrame,Time.unscaledDeltaTime,.05f);
            if(Input.GetKeyDown(KeyCode.Tab))_ui=!_ui;
            if(Input.GetKeyDown(KeyCode.Space))ResetView();
            if(Input.GetMouseButton(1))
            {
                _yaw+=Input.GetAxisRaw("Mouse X")*2;_pitch=Mathf.Clamp(_pitch-Input.GetAxisRaw("Mouse Y")*2,8,85);
                transform.rotation=Quaternion.Euler(_pitch,_yaw,0);
            }
            float speed=(Input.GetKey(KeyCode.LeftShift)?75:28)*Time.unscaledDeltaTime;
            Vector3 forward=Vector3.ProjectOnPlane(transform.forward,Vector3.up).normalized;
            Vector3 right=Vector3.ProjectOnPlane(transform.right,Vector3.up).normalized;
            Vector3 delta=Vector3.zero;
            if(Input.GetKey(KeyCode.W))delta+=forward;if(Input.GetKey(KeyCode.S))delta-=forward;
            if(Input.GetKey(KeyCode.D))delta+=right;if(Input.GetKey(KeyCode.A))delta-=right;
            if(Input.GetKey(KeyCode.E))delta+=Vector3.up;if(Input.GetKey(KeyCode.Q))delta-=Vector3.up;
            transform.position+=delta*speed+transform.forward*Input.mouseScrollDelta.y*5;
            if(World.Surface!=null)
            {
                Vector3 p=transform.position;
                float ground=World.Surface.Sample(p.x+World.WorldOrigin.x,p.z+World.WorldOrigin.y).Height;
                p.y=Mathf.Clamp(p.y,Mathf.Max(ground+2,2),200);transform.position=p;
            }
        }
        private void OnGUI()
        {
            if(!_ui)return;
            if(_label==null)
            {
                _label=new GUIStyle(GUI.skin.label){font=_font,fontSize=15,wordWrap=true};
                _button=new GUIStyle(GUI.skin.button){font=_font,fontSize=14};
                GUI.skin.font=_font;
            }
            GUILayout.BeginArea(new Rect(18,18,350,415),GUI.skin.box);
            GUILayout.Label("地球归还 · 无缝地形试验场",_label);
            GUILayout.Label("WASD 移动 · 右键观察 · 滚轮前后\nQ/E 升降 · Shift 加速 · 空格回到谷地\nTab 隐藏面板",_label);
            GUILayout.BeginHorizontal();GUILayout.Label("种子",_label,GUILayout.Width(42));
            _seed=GUILayout.TextField(_seed,GUILayout.Width(126));
            if(GUILayout.Button("生成",_button) && int.TryParse(_seed,out int seed))World.Rebuild(seed);
            if(GUILayout.Button("换一张",_button)){int next=unchecked(World.Seed+104729);_seed=next.ToString();World.Rebuild(next);}
            GUILayout.EndHorizontal();
            if(SwitchOrbis!=null&&GUILayout.Button("切换 Orbis 地形对比",_button))SwitchOrbis(World);
            if(OrbisStatus!=null)GUILayout.Label(OrbisStatus(),_label);
            GUILayout.BeginHorizontal();
            if(GUILayout.Button("河谷全景",_button))ResetView();
            if(GUILayout.Button("楼墟聚落",_button))SetView(new Vector3(-76,32,26),new Vector3(-48,8,66));
            if(GUILayout.Button("断桥岩谷",_button))SetView(new Vector3(0,39,27),new Vector3(64,12,91));
            GUILayout.EndHorizontal();
            World.ShowBoundaries=GUILayout.Toggle(World.ShowBoundaries," 显示区块边界（检查衔接）");
            bool props=GUILayout.Toggle(World.ShowDecoration," 岩石、枯树与残骸");
            if(props!=World.ShowDecoration)World.SetDecoration(props);
            bool modules=GUILayout.Toggle(World.ShowModules," 连接废墟与旧管道");
            if(modules!=World.ShowModules)World.SetModules(modules);
            string geometry=World.ExternalGround?$"装饰区块 {World.ReadyCount} 块":$"已显示 {World.ReadyCount} 块 · {World.TriangleCount:N0} 地形三角";
            GUILayout.Label($"{geometry}\n待生成 {Mathf.Max(0,World.PendingCount)} 块 · {1/Mathf.Max(.001f,_smoothedFrame):F0} 帧/秒",_label);
            GUILayout.Label($"{World.BuildingCount} 栋建筑 · {World.BuildingBayCount} 个拼装单元\n重点观察：建筑支撑与入口、道路、断桥和岩壁。",_label);
            GUILayout.EndArea();
        }
        private void SetView(Vector3 position,Vector3 target)
        {
            Vector3 offset=new Vector3(World.WorldOrigin.x,0,World.WorldOrigin.y);
            transform.position=position-offset;transform.LookAt(target-offset);
            _pitch=transform.eulerAngles.x;_yaw=transform.eulerAngles.y;
        }
        private void OnDestroy(){if(_font)Destroy(_font);}
    }
}
