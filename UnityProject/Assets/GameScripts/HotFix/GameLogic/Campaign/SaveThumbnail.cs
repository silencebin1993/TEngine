using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GameLogic.Campaign
{
    /// <summary>
    /// FG15-SYS-01（FGR-SYS-007 存档卡片“缩略图”，DEBT-FG0SAVE01-01）：存档时在主线程把主相机渲染到 320×180 的小图并读回像素（只这一步在主线程），
    /// JPG 编码在后台写入线程（<see cref="Shot.EncodeBase64"/>，ImageConversion.EncodeArrayToJPG 可在任意线程调用），嵌进存档头部。
    /// 没有可渲染的相机（无图形设备的批处理、主菜单）时用程序生成的占位图（深色渐变 + 按幕着色的色带 + 第几日刻度，占位美术，美术阶段可整体替换）。
    /// </summary>
    public static class SaveThumbnail
    {
        public const int Width = 320;
        public const int Height = 180;

        public sealed class Shot
        {
            public Color32[] Pixels;
            public int Width;
            public int Height;
            /// <summary>true = 占位图（没有相机 / 无图形设备）。</summary>
            public bool Placeholder;
            private string _base64;

            /// <summary>编码发生在哪个线程（自检断言编码不在主线程）。</summary>
            public int EncodeThreadId { get; private set; }

            public string EncodeBase64()
            {
                if (_base64 != null || Pixels == null)
                {
                    return _base64;
                }
                byte[] jpg = ImageConversion.EncodeArrayToJPG(Pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)Width, (uint)Height, 0, 75);
                EncodeThreadId = Thread.CurrentThread.ManagedThreadId;
                _base64 = jpg == null || jpg.Length == 0 ? null : Convert.ToBase64String(jpg);
                return _base64;
            }
        }

        /// <summary>截图入口（自检可替换）。</summary>
        public static Func<Shot> CaptureHandler = CaptureMainCamera;

        /// <summary>主线程截一张存档缩略图；失败返回 null（存档照常，卡片不显示缩略图）。</summary>
        public static Shot Capture()
        {
            try
            {
                return CaptureHandler?.Invoke();
            }
            catch (Exception e)
            {
                TEngine.Log.Warning($"[SaveThumbnail] 截缩略图失败，本次存档不带缩略图：{e.Message}");
                return null;
            }
        }

        public static Shot CaptureMainCamera()
        {
            Camera cam = Camera.main;
            if (cam == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return Placeholder(CampaignSession.Current);
            }
            RenderTexture rt = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                return new Shot { Pixels = tex.GetPixels32(), Width = Width, Height = Height };
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(tex);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }
        }

        private static readonly Color32[] ActColors =
        {
            new Color32(64, 168, 150, 255), new Color32(214, 160, 64, 255), new Color32(196, 82, 72, 255), new Color32(126, 118, 206, 255),
        };

        /// <summary>占位缩略图：深色竖向渐变，底部一条按幕着色的色带，色带上按第几日（模 30）点出刻度——不同存档一眼能分开。</summary>
        public static Shot Placeholder(CampaignState s)
        {
            int act = Math.Max(1, s?.Progress?.Act ?? 1);
            int day = Math.Max(0, s?.Clock?.Day ?? 0);
            Color32 band = ActColors[(act - 1) % ActColors.Length];
            var px = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                byte g = (byte)(18 + y * 40 / Height);
                for (int x = 0; x < Width; x++)
                {
                    Color32 c = new Color32((byte)(g / 2), g, (byte)(g + 8), 255);
                    if (y < 18)
                    {
                        c = band;
                        int tick = day % 30;
                        if (x < tick * Width / 30 && x % 10 < 6)
                        {
                            c = new Color32(235, 235, 225, 255);
                        }
                    }
                    px[y * Width + x] = c;
                }
            }
            return new Shot { Pixels = px, Width = Width, Height = Height, Placeholder = true };
        }
    }
}
