#if !UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BinGames.TerrainVisual;
using UnityEngine;

public sealed class OrbisComparisonQa:MonoBehaviour
{
    private string destination;private int errors;private float started;
    private readonly List<Measurement> results=new List<Measurement>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        string[] args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"--orbis-qa");
        if(at<0||at+1>=args.Length)return;
        var qa=new GameObject("Orbis 对比验证").AddComponent<OrbisComparisonQa>();qa.destination=args[at+1];
    }
    private IEnumerator Start()
    {
        started=Time.realtimeSinceStartup;Application.logMessageReceived+=OnLog;Directory.CreateDirectory(destination);
        TerrainStream terrain=FindFirstObjectByType<TerrainStream>();
        OrbisTerrainComparison adapter=FindFirstObjectByType<OrbisTerrainComparison>();
        int expected=(terrain.Profile.ViewRadius*2+1)*(terrain.Profile.ViewRadius*2+1);
        while(terrain.PendingCount>0||terrain.ReadyCount!=expected)yield return null;
        // Uncap only in this explicit benchmark. The normal viewer remains capped at 120 FPS.
        Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
        yield return new WaitForSeconds(2);yield return Measure("continuous",terrain,adapter);
        adapter.Toggle(terrain);
        while(!adapter.Ready)yield return null;
        yield return new WaitForSeconds(3);yield return Measure("orbis",terrain,adapter);
        Camera view=Camera.main;Vector3 oldPosition=view.transform.position;Quaternion oldRotation=view.transform.rotation;
        view.transform.position=new Vector3(14-terrain.WorldOrigin.x,16,38-terrain.WorldOrigin.y);
        view.transform.LookAt(new Vector3(52-terrain.WorldOrigin.x,7,91-terrain.WorldOrigin.y));
        while(terrain.PendingCount>0||terrain.ReadyCount!=expected)yield return null;
        yield return new WaitForSeconds(3);yield return Measure("orbis-near",terrain,adapter);
        view.transform.SetPositionAndRotation(oldPosition,oldRotation);
        adapter.Toggle(terrain);yield return new WaitForSeconds(1);
        terrain.Rebuild(90217);while(terrain.PendingCount>0||terrain.ReadyCount!=expected)yield return null;
        adapter.Toggle(terrain);while(!adapter.Ready)yield return null;
        yield return new WaitForSeconds(2);yield return Measure("orbis-seed-90217",terrain,adapter);
        bool pass=errors==0&&terrain.WfcFailures==0&&adapter.Ready&&terrain.BuildingCount>0&&CheckSpacing();
        foreach(Measurement measurement in results)if(measurement.maxHeightError>.003f)pass=false;
        File.WriteAllText(Path.Combine(destination,"orbis-comparison.json"),JsonUtility.ToJson(new Report{
            pass=pass,errors=errors,measurements=results.ToArray(),note="同一场景，1600×900，关闭 Unity 帧率上限，静止视角各采样 4 秒。连续地形仍负责共同的装饰、水面与区块流送；Orbis 对比区为 1 km，使用 1 m 高度采样。"},true));
        Application.Quit(pass?0:1);
    }
    private IEnumerator Measure(string label,TerrainStream terrain,OrbisTerrainComparison adapter)
    {
        var frames=new List<float>();double begin=Time.realtimeSinceStartupAsDouble,previousFrame=begin;
        while(Time.realtimeSinceStartupAsDouble-begin<4){yield return null;double now=Time.realtimeSinceStartupAsDouble;frames.Add((float)((now-previousFrame)*1000));previousFrame=now;}
        float sum=0;foreach(float ms in frames)sum+=ms;frames.Sort();
        results.Add(new Measurement{backend=label,meanFps=frames.Count*1000/Mathf.Max(1,sum),p95Ms=frames[Mathf.Min(frames.Count-1,Mathf.FloorToInt(frames.Count*.95f))],
            terrainMeshes=label=="continuous"?terrain.ReadyCount:adapter.MeshCount,groundTriangles=label=="continuous"?terrain.TriangleCount:adapter.Triangles,
            maxHeightError=label=="continuous"?0:adapter.HeightError(),buildings=terrain.BuildingCount,bays=terrain.BuildingBayCount});
        var target=new RenderTexture(1600,900,24);var image=new Texture2D(1600,900,TextureFormat.RGB24,false);
        Camera camera=Camera.main;RenderTexture previous=RenderTexture.active,oldTarget=camera.targetTexture;
        try{camera.targetTexture=target;if(label!="continuous")adapter.QueueCameraCapture(camera);camera.Render();RenderTexture.active=target;
            image.ReadPixels(new Rect(0,0,1600,900),0,0);image.Apply();File.WriteAllBytes(Path.Combine(destination,label+".png"),image.EncodeToPNG());}
        finally{camera.targetTexture=oldTarget;RenderTexture.active=previous;Destroy(image);Destroy(target);}
    }
    private bool CheckSpacing()
    {
        var all=new List<Vector2>();
        foreach(var cell in new[]{new Vector2Int(-1,-1),new Vector2Int(0,-1),new Vector2Int(-1,0),Vector2Int.zero})
        {
            Vector2[] points=OrbisFoliageSampler.Sample(cell,32,41729),repeat=OrbisFoliageSampler.Sample(cell,32,41729);
            if(points.Length!=repeat.Length)return false;
            for(int i=0;i<points.Length;i++)if(points[i]!=repeat[i])return false;
            all.AddRange(points);
        }
        for(int a=0;a<all.Count;a++)for(int b=a+1;b<all.Count;b++)
            if((all[a]-all[b]).sqrMagnitude<OrbisFoliageSampler.Distance*OrbisFoliageSampler.Distance-.001f)return false;
        return true;
    }
    private void Update()
    {
        if(Time.realtimeSinceStartup-started<120)return;
        File.WriteAllText(Path.Combine(destination,"orbis-comparison.json"),"{\"pass\":false,\"reason\":\"Orbis 对比超时\"}");Application.Quit(1);
    }
    private void OnLog(string msg,string stack,LogType type){if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert)errors++;}
    private void OnDestroy(){Application.logMessageReceived-=OnLog;}
    [Serializable]private sealed class Measurement{public string backend;public float meanFps,p95Ms,maxHeightError;public int terrainMeshes,groundTriangles,buildings,bays;}
    [Serializable]private sealed class Report{public bool pass;public int errors;public string note;public Measurement[] measurements;}
}
#endif
