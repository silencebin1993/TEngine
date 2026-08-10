using System;
using System.IO;
using Luban;
using GameConfig;
using TEngine;
using UnityEngine;

/// <summary>
/// 配置加载器。桥接 Luban 生成代码与资源系统。
/// 优先走 YooAsset；FP Demo / Editor 直开场景时回退到 GameRes/Raw 磁盘文件。
/// </summary>
public class ConfigSystem
{
    private static ConfigSystem _instance;

    public static ConfigSystem Instance => _instance ??= new ConfigSystem();

    private bool _init;

    private Tables _tables;

    public Tables Tables
    {
        get
        {
            if (!_init)
            {
                Load();
            }

            return _tables;
        }
    }

    private IResourceModule _resourceModule;
    private bool _resourceModuleResolved;

    /// <summary>
    /// 加载配置。
    /// </summary>
    public void Load()
    {
        _tables = new Tables(LoadByteBuf);
        _init = true;
    }

    /// <summary>
    /// 加载二进制配置。
    /// </summary>
    /// <param name="file">不含扩展名的表文件名，如 fp_tbglobal</param>
    private ByteBuf LoadByteBuf(string file)
    {
        byte[] bytes = TryLoadFromResourceModule(file);
        if (bytes == null)
        {
            bytes = TryLoadFromAssetRaw(file);
        }

        if (bytes == null || bytes.Length == 0)
        {
            throw new Exception($"Config bytes not found: {file}.bytes");
        }

        return new ByteBuf(bytes);
    }

    private byte[] TryLoadFromResourceModule(string file)
    {
        try
        {
            if (!_resourceModuleResolved)
            {
                _resourceModule = ModuleSystem.GetModule<IResourceModule>();
                _resourceModuleResolved = true;
            }

            if (_resourceModule == null)
            {
                return null;
            }

            TextAsset textAsset = _resourceModule.LoadAsset<TextAsset>(file);
            return textAsset != null ? textAsset.bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] TryLoadFromAssetRaw(string file)
    {
        string path = Path.Combine(Application.dataPath, "GameRes", "Raw", "Configs", "bytes", file + ".bytes");
        if (!File.Exists(path))
        {
            return null;
        }

        return File.ReadAllBytes(path);
    }
}
