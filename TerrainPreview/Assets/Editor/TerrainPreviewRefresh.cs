#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using System.IO;

// Force a scan of linked worktree sources when this isolated project loads.
internal static class TerrainPreviewRefresh
{
    private static AddRequest _jsonModuleRequest;
    [InitializeOnLoadMethod]
    private static void RefreshLinkedSources()
    {
        EditorApplication.delayCall += () =>
        {
            if(!File.ReadAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath),"Packages/manifest.json")).Contains("com.unity.modules.jsonserialize"))
            {
                _jsonModuleRequest=Client.Add("com.unity.modules.jsonserialize@1.0.0");
                EditorApplication.update+=PollPackage;
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        };
    }
    private static void PollPackage()
    {
        if(_jsonModuleRequest==null || !_jsonModuleRequest.IsCompleted)return;
        EditorApplication.update-=PollPackage;
        if(_jsonModuleRequest.Status==StatusCode.Success)Debug.Log("Terrain preview JSON module resolved: "+_jsonModuleRequest.Result.version);
        else Debug.LogError("Terrain preview JSON module failed: "+_jsonModuleRequest.Error.message);
    }
}
#endif
