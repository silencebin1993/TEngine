#if !UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace BinGames.TerrainVisual
{
    // Explicit command-line smoke check for the isolated playable build only.
    public sealed class TerrainLabPlayerCheck : MonoBehaviour
    {
        private TerrainStream _world;
        private string _output;
        private int _errors,_stage,_frames;
        private float _started,_settled=-1,_seconds;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string[]args=Environment.GetCommandLineArgs();int index=Array.IndexOf(args,"--terrain-qa");
            if(index<0 || index+1>=args.Length)return;
            var check=new GameObject("试玩包自检").AddComponent<TerrainLabPlayerCheck>();check._output=args[index+1];
            check._world=FindFirstObjectByType<TerrainStream>();check._started=Time.realtimeSinceStartup;
            Application.logMessageReceived+=check.OnLog;
        }
        private void OnLog(string message,string stack,LogType type)
        {if(type==LogType.Error || type==LogType.Exception || type==LogType.Assert)_errors++;}
        private void Update()
        {
            if(Time.realtimeSinceStartup-_started>90){Finish(false,"等待地形超时");return;}
            int expected=_world?(2*_world.Profile.ViewRadius+1)*(2*_world.Profile.ViewRadius+1):int.MaxValue;
            if(!_world || _world.PendingCount>0 || _world.ReadyCount!=expected){_settled=-1;return;}
            if(_settled<0){_settled=Time.realtimeSinceStartup;_frames=0;_seconds=0;return;}
            _frames++;_seconds+=Time.unscaledDeltaTime;
            if(Time.realtimeSinceStartup-_settled<2)return;
            if(_stage++==0)
            {
                Camera camera=Camera.main;
                var target=new RenderTexture(1600,900,24);var image=new Texture2D(1600,900,TextureFormat.RGB24,false);
                RenderTexture previous=RenderTexture.active,oldTarget=camera.targetTexture;
                try
                {
                    camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                    image.ReadPixels(new Rect(0,0,1600,900),0,0);image.Apply();
                    Directory.CreateDirectory(_output);File.WriteAllBytes(Path.Combine(_output,"player.png"),image.EncodeToPNG());
                }
                finally{camera.targetTexture=oldTarget;RenderTexture.active=previous;Destroy(image);Destroy(target);}
                _world.Rebuild(0);_settled=-1;return;
            }
            bool supported=_world.Profile.GroundMaterial.shader.isSupported && _world.Profile.PropMaterial.shader.isSupported
                && _world.Profile.WaterMaterial.shader.isSupported;
            Finish(_errors==0 && _world.WfcFailures==0 && _world.ModuleCount>0 && supported,"");
        }
        private void Finish(bool pass,string reason)
        {
            enabled=false;Directory.CreateDirectory(_output);
            File.WriteAllText(Path.Combine(_output,"player.json"),JsonUtility.ToJson(new Result{
                pass=pass,errors=_errors,chunks=_world?_world.ReadyCount:0,modules=_world?_world.ModuleCount:0,
                wfcFailures=_world?_world.WfcFailures:0,seed=_world?_world.Seed:0,reason=reason,
                settledMeanFps=_frames/Mathf.Max(.001f,_seconds)},true));
            Application.logMessageReceived-=OnLog;Application.Quit(pass?0:1);
        }
        [Serializable] private sealed class Result
        {public bool pass;public int errors,chunks,modules,wfcFailures,seed;public float settledMeanFps;public string reason;}
    }
}
#endif
