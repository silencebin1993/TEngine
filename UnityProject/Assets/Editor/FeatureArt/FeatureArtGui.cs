using System;
using System.Collections.Generic;
using BinGames.EditorTools.CellArt;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.FeatureArt
{
    internal static class FeatureArtGui
    {
        static float _width;
        static GUIStyle _areaStyle, _labelStyle, _buttonStyle;
        public static float Width => _width > 0 ? _width : Mathf.Clamp(EditorGUIUtility.currentViewWidth - 48f, 96f, 880f);
        public static GUIStyle AreaStyle => _areaStyle ??= new GUIStyle(EditorStyles.textArea) { wordWrap = true, stretchWidth = false };
        public static GUIStyle LabelStyle => _labelStyle ??= new GUIStyle(EditorStyles.label) { wordWrap = true, stretchWidth = false };
        public static GUIStyle ButtonStyle => _buttonStyle ??= new GUIStyle(GUI.skin.button) { wordWrap = true, stretchWidth = false };

        public readonly struct WidthScope : IDisposable
        {
            readonly float _previous;
            public WidthScope(float width) { _previous = _width; _width = Mathf.Max(60f, width); }
            public void Dispose() => _width = _previous;
        }

        public static string Field(string label, string value, bool delayed = false)
        {
            Label(label);
            return delayed ? EditorGUILayout.DelayedTextField(value ?? "", GUILayout.Width(Width))
                : EditorGUILayout.TextField(value ?? "", GUILayout.Width(Width));
        }

        public static int Popup(string label, int index, string[] labels)
        {
            Label(label);
            return EditorGUILayout.Popup(index, labels, GUILayout.Width(Width));
        }

        public static void Label(string text) => GUILayout.Label(text ?? "", LabelStyle, GUILayout.Width(Width));
        public static bool Button(string text) => GUILayout.Button(text, ButtonStyle, GUILayout.Width(Width));

        public sealed class TextArea
        {
            readonly GUIContent _content = new GUIContent();
            string _lastValue;
            float _lastWidth = -1, _height;
            public string Draw(string label, string value)
            {
                value ??= "";
                Label(label);
                if (_lastWidth != Width || _lastValue != value)
                {
                    _lastWidth = Width;
                    _lastValue = value;
                    _content.text = value;
                    _height = Mathf.Max(54f, AreaStyle.CalcHeight(_content, Width));
                }
                // Full wrapped height belongs to the editor's vertical scroll view.
                return EditorGUILayout.TextArea(value, AreaStyle, GUILayout.Width(Width), GUILayout.Height(_height));
            }
        }
    }

    [InitializeOnLoad]
    internal static class FeatureArtAssetCache
    {
        static readonly Dictionary<(string, Type), UnityEngine.Object> Paths = new Dictionary<(string, Type), UnityEngine.Object>();
        static readonly Dictionary<(string, Type), UnityEngine.Object> Locations = new Dictionary<(string, Type), UnityEngine.Object>();
        public static int Revision { get; private set; }
        static FeatureArtAssetCache() => EditorApplication.projectChanged += Clear;
        public static void Clear() { Paths.Clear(); Locations.Clear(); Revision++; }
        public static T Load<T>(string path) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path)) return null;
            var key = (path, typeof(T));
            if (!Paths.TryGetValue(key, out var asset)) Paths[key] = asset = AssetDatabase.LoadAssetAtPath<T>(path);
            return asset as T;
        }
        public static Texture2D Preview(CellArtAsset asset) =>
            Load<Texture2D>(CellArtRegistryService.AssetPathOf(asset.concept)) ?? Load<Texture2D>(CellArtRegistryService.AssetPathOf(asset.preview));
        public static bool TryLocation(string location, Type type, out UnityEngine.Object asset) => Locations.TryGetValue((location, type), out asset);
        public static void StoreLocation(string location, Type type, UnityEngine.Object asset) => Locations[(location, type)] = asset;
    }
}
