using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BinGames.EditorTools.CellArt
{
    /// <summary>
    /// GameRes/Art/Cell/registry.json 的读写与扫盘入列。
    /// 与 tools/cell_art/manage.py 共用同一份登记表约定。
    /// </summary>
    public static class CellArtRegistryService
    {
        public const string CellRelative = "Assets/GameRes/Art/Cell";

        public static string CellAbs =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "GameRes/Art/Cell"));

        public static string RegistryAbs => Path.Combine(CellAbs, "registry.json");

        public static string BoardAbs => Path.Combine(CellAbs, "board.html");

        static readonly HashSet<string> ImgExt = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".webp", ".tga" };

        static readonly HashSet<string> MeshExt = new(StringComparer.OrdinalIgnoreCase)
            { ".fbx", ".glb", ".gltf", ".obj", ".blend" };

        static readonly HashSet<string> AnimExt = new(StringComparer.OrdinalIgnoreCase)
            { ".fbx", ".anim", ".controller" };

        static readonly HashSet<string> VfxExt = new(StringComparer.OrdinalIgnoreCase)
            { ".prefab", ".png", ".jpg", ".jpeg", ".webp", ".vfx", ".asset" };

        static readonly string[] SkipNames = { ".gitkeep", "registry.json", "board.html" };

        public static CellArtRegistry Load()
        {
            if (!File.Exists(RegistryAbs))
            {
                throw new FileNotFoundException("找不到美术登记表", RegistryAbs);
            }

            var root = CellArtJson.AsDict(CellArtJson.Deserialize(File.ReadAllText(RegistryAbs)))
                       ?? throw new Exception("registry.json 解析失败");
            var data = FromDict(root);
            data.dirs ??= new CellArtDirs();
            data.assets ??= new List<CellArtAsset>();
            foreach (var a in data.assets)
            {
                Normalize(a);
            }

            return data;
        }

        public static void Save(CellArtRegistry data)
        {
            data.updated = DateTime.Now.ToString("yyyy-MM-dd");
            data.dirs ??= new CellArtDirs();
            File.WriteAllText(RegistryAbs, CellArtJson.Serialize(ToDict(data)) + "\n");
            AssetDatabase.Refresh();
        }

        static CellArtRegistry FromDict(Dictionary<string, object> root)
        {
            var data = new CellArtRegistry
            {
                version = CellArtJson.IntNullable(root.GetValueOrDefault("version")) ?? 1,
                updated = CellArtJson.Str(root.GetValueOrDefault("updated")),
                pipeline = CellArtJson.Str(root.GetValueOrDefault("pipeline")),
                dirs = new CellArtDirs(),
                status_order = ToStringList(root.GetValueOrDefault("status_order")),
                slots = ToStringList(root.GetValueOrDefault("slots")),
                routes = ToStringList(root.GetValueOrDefault("routes")),
                assets = new List<CellArtAsset>(),
            };
            var dirs = CellArtJson.AsDict(root.GetValueOrDefault("dirs"));
            if (dirs != null)
            {
                data.dirs.concepts = CellArtJson.Str(dirs.GetValueOrDefault("concepts"), "Concepts");
                data.dirs.meshes = CellArtJson.Str(dirs.GetValueOrDefault("meshes"), "Meshes");
                data.dirs.animations = CellArtJson.Str(dirs.GetValueOrDefault("animations"), "Animations");
                data.dirs.vfx = CellArtJson.Str(dirs.GetValueOrDefault("vfx"), "VFX");
                data.dirs.previews = CellArtJson.Str(dirs.GetValueOrDefault("previews"), "Previews");
                data.dirs.board = CellArtJson.Str(dirs.GetValueOrDefault("board"), "board.html");
            }

            var assets = CellArtJson.AsList(root.GetValueOrDefault("assets"));
            if (assets == null)
            {
                return data;
            }

            foreach (var item in assets)
            {
                var d = CellArtJson.AsDict(item);
                if (d == null)
                {
                    continue;
                }

                var a = new CellArtAsset
                {
                    id = CellArtJson.Str(d.GetValueOrDefault("id")),
                    name_zh = CellArtJson.Str(d.GetValueOrDefault("name_zh")),
                    kind = CellArtJson.Str(d.GetValueOrDefault("kind")),
                    slot = CellArtJson.Str(d.GetValueOrDefault("slot")),
                    route = CellArtJson.Str(d.GetValueOrDefault("route")),
                    rarity = CellArtJson.Str(d.GetValueOrDefault("rarity")),
                    concept = CellArtJson.Str(d.GetValueOrDefault("concept")),
                    mesh = CellArtJson.Str(d.GetValueOrDefault("mesh")),
                    anim = CellArtJson.Str(d.GetValueOrDefault("anim")),
                    vfx = CellArtJson.Str(d.GetValueOrDefault("vfx")),
                    preview = CellArtJson.Str(d.GetValueOrDefault("preview")),
                    raw = CellArtJson.Str(d.GetValueOrDefault("raw")),
                    archetype_id = CellArtJson.IntNullable(d.GetValueOrDefault("archetype_id")),
                    status = CellArtJson.Str(d.GetValueOrDefault("status")),
                    tripo_notes = CellArtJson.Str(d.GetValueOrDefault("tripo_notes"), ""),
                    notes = CellArtJson.Str(d.GetValueOrDefault("notes"), ""),
                    needs_review = CellArtJson.Bool(d.GetValueOrDefault("needs_review")),
                    views = new Dictionary<string, string>(),
                    anim_clips = new List<CellArtAnimClip>(),
                };
                var views = CellArtJson.AsDict(d.GetValueOrDefault("views"));
                if (views != null)
                {
                    foreach (var kv in views)
                    {
                        a.views[kv.Key] = CellArtJson.Str(kv.Value);
                    }
                }

                var clips = CellArtJson.AsList(d.GetValueOrDefault("anim_clips"));
                if (clips != null)
                {
                    foreach (var c in clips)
                    {
                        if (c is string path)
                        {
                            a.anim_clips.Add(new CellArtAnimClip { name = Path.GetFileNameWithoutExtension(path), path = path });
                        }
                        else
                        {
                            var cd = CellArtJson.AsDict(c);
                            if (cd != null)
                            {
                                a.anim_clips.Add(new CellArtAnimClip
                                {
                                    name = CellArtJson.Str(cd.GetValueOrDefault("name")),
                                    path = CellArtJson.Str(cd.GetValueOrDefault("path")),
                                });
                            }
                        }
                    }
                }

                data.assets.Add(a);
            }

            return data;
        }

        static Dictionary<string, object> ToDict(CellArtRegistry data)
        {
            var assets = new List<object>();
            foreach (var a in data.assets)
            {
                var views = new Dictionary<string, object>();
                if (a.views != null)
                {
                    foreach (var kv in a.views)
                    {
                        views[kv.Key] = kv.Value;
                    }
                }

                var clips = new List<object>();
                if (a.anim_clips != null)
                {
                    foreach (var c in a.anim_clips)
                    {
                        clips.Add(new Dictionary<string, object> { ["name"] = c.name, ["path"] = c.path });
                    }
                }

                assets.Add(new Dictionary<string, object>
                {
                    ["id"] = a.id,
                    ["name_zh"] = a.name_zh,
                    ["kind"] = a.kind,
                    ["slot"] = a.slot,
                    ["route"] = a.route,
                    ["rarity"] = a.rarity,
                    ["concept"] = a.concept,
                    ["views"] = views,
                    ["mesh"] = a.mesh,
                    ["anim"] = a.anim,
                    ["anim_clips"] = clips,
                    ["vfx"] = a.vfx,
                    ["preview"] = a.preview,
                    ["raw"] = a.raw,
                    ["archetype_id"] = a.archetype_id.HasValue ? (object)a.archetype_id.Value : null,
                    ["status"] = a.status,
                    ["tripo_notes"] = a.tripo_notes ?? "",
                    ["notes"] = a.notes ?? "",
                    ["needs_review"] = a.needs_review,
                });
            }

            return new Dictionary<string, object>
            {
                ["version"] = data.version,
                ["updated"] = data.updated,
                ["pipeline"] = data.pipeline,
                ["dirs"] = new Dictionary<string, object>
                {
                    ["concepts"] = data.dirs.concepts,
                    ["meshes"] = data.dirs.meshes,
                    ["animations"] = data.dirs.animations,
                    ["vfx"] = data.dirs.vfx,
                    ["previews"] = data.dirs.previews,
                    ["board"] = data.dirs.board,
                },
                ["status_order"] = data.status_order ?? new List<string>(),
                ["slots"] = data.slots ?? new List<string>(),
                ["routes"] = data.routes ?? new List<string>(),
                ["assets"] = assets,
            };
        }

        static object DictGet(Dictionary<string, object> d, string key)
        {
            return d != null && d.TryGetValue(key, out var v) ? v : null;
        }

        static List<string> ToStringList(object o)
        {
            var list = CellArtJson.AsList(o);
            if (list == null)
            {
                return new List<string>();
            }

            return list.Select(x => CellArtJson.Str(x)).Where(s => s != null).ToList();
        }

        public static void EnsureFolders(CellArtRegistry data)
        {
            foreach (var name in new[]
                     {
                         data.dirs.concepts, data.dirs.meshes, data.dirs.animations, data.dirs.vfx,
                         data.dirs.previews
                     })
            {
                var dir = Path.Combine(CellAbs, name);
                Directory.CreateDirectory(dir);
                var keep = Path.Combine(dir, ".gitkeep");
                if (!Directory.EnumerateFileSystemEntries(dir).Any() && !File.Exists(keep))
                {
                    File.WriteAllText(keep, "");
                }
            }
        }

        public static string AbsOf(string rel)
        {
            if (string.IsNullOrEmpty(rel))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(CellAbs, rel.Replace('/', Path.DirectorySeparatorChar)));
        }

        public static string RelOf(string absPath)
        {
            var full = Path.GetFullPath(absPath);
            var root = Path.GetFullPath(CellAbs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return full.Substring(root.Length).Replace('\\', '/');
        }

        public static bool PathExists(string rel)
        {
            var abs = AbsOf(rel);
            return !string.IsNullOrEmpty(abs) && (File.Exists(abs) || Directory.Exists(abs));
        }

        public static string AssetPathOf(string rel)
        {
            if (string.IsNullOrEmpty(rel))
            {
                return null;
            }

            return $"{CellRelative}/{rel.Replace('\\', '/')}";
        }

        public static Texture2D LoadPreviewTexture(CellArtAsset asset)
        {
            foreach (var rel in new[] { asset.concept, asset.preview })
            {
                var ap = AssetPathOf(rel);
                if (string.IsNullOrEmpty(ap))
                {
                    continue;
                }

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(ap);
                if (tex != null)
                {
                    return tex;
                }
            }

            return null;
        }

        public static string GuessStatus(CellArtAsset a)
        {
            if (!string.IsNullOrEmpty(a.raw))
            {
                return "unity";
            }

            if (!string.IsNullOrEmpty(a.mesh) && !string.IsNullOrEmpty(a.anim))
            {
                return "blender";
            }

            if (!string.IsNullOrEmpty(a.mesh))
            {
                return "tripo";
            }

            if (!string.IsNullOrEmpty(a.concept) || !string.IsNullOrEmpty(a.preview)
                                                || !string.IsNullOrEmpty(a.vfx) || !string.IsNullOrEmpty(a.anim))
            {
                return "concept";
            }

            return "todo";
        }

        /// <summary>扫盘。apply=false 只返回将要执行的动作说明。</summary>
        public static List<string> Scan(CellArtRegistry data, bool apply)
        {
            EnsureFolders(data);
            var actions = new List<string>();
            var refs = CollectRefs(data);
            var aliases = BuildAliases(data);
            var byId = data.assets.ToDictionary(a => a.id, a => a);

            void Bind(string aid, string field, string rel, string clip = null)
            {
                if (!byId.TryGetValue(aid, out var a))
                {
                    var kind = field == "anim" ? "anim" : field == "vfx" ? "vfx" : InferKind(aid);
                    a = NewStub(aid, kind);
                    data.assets.Add(a);
                    byId[aid] = a;
                    actions.Add($"ADD  {aid} ({kind})");
                }

                if (field == "anim" && !string.IsNullOrEmpty(clip))
                {
                    a.anim_clips ??= new List<CellArtAnimClip>();
                    if (a.anim_clips.All(c => c.path != rel))
                    {
                        a.anim_clips.Add(new CellArtAnimClip { name = clip, path = rel });
                        actions.Add($"CLIP {aid} += {rel}");
                    }

                    if (string.IsNullOrEmpty(a.anim))
                    {
                        a.anim = rel.Contains('/') ? rel.Substring(0, rel.LastIndexOf('/')) : rel;
                    }
                }
                else
                {
                    var cur = GetField(a, field);
                    if (cur != rel)
                    {
                        SetField(a, field, rel);
                        actions.Add($"SET  {aid}.{field} = {rel}");
                    }
                }

                a.status = GuessStatus(a);
            }

            foreach (var p in Enumerate(data.dirs.concepts))
            {
                if (!ImgExt.Contains(Path.GetExtension(p)))
                {
                    continue;
                }

                var rel = RelOf(p);
                if (refs.Contains(rel))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(p);
                var aid = aliases.TryGetValue(stem, out var mapped) ? mapped : stem;
                if (byId.TryGetValue(aid, out var existing) && string.IsNullOrEmpty(existing.concept))
                {
                    if (apply)
                    {
                        Bind(aid, "concept", rel);
                    }
                    else
                    {
                        actions.Add($"LINK concept {rel} -> {aid}");
                    }
                }
                else if (!byId.ContainsKey(aid))
                {
                    if (apply)
                    {
                        var stub = NewStub(aid, InferKind(stem));
                        stub.concept = rel;
                        stub.status = GuessStatus(stub);
                        data.assets.Add(stub);
                        byId[aid] = stub;
                        aliases[stem] = aid;
                    }

                    actions.Add($"{(apply ? "ADD" : "NEW")} concept {rel} -> id={aid}");
                }
                else
                {
                    actions.Add($"ORPHAN concept {rel}");
                }
            }

            foreach (var p in Enumerate(data.dirs.meshes))
            {
                if (!MeshExt.Contains(Path.GetExtension(p)))
                {
                    continue;
                }

                var rel = RelOf(p);
                if (refs.Contains(rel))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(p);
                var aid = aliases.TryGetValue(stem, out var mapped) ? mapped : stem;
                if (apply)
                {
                    Bind(aid, "mesh", rel);
                    aliases[stem] = aid;
                }
                else
                {
                    actions.Add($"LINK mesh {rel} -> {aid}");
                }
            }

            foreach (var p in Enumerate(data.dirs.animations))
            {
                var ext = Path.GetExtension(p);
                if (!AnimExt.Contains(ext) && !MeshExt.Contains(ext))
                {
                    continue;
                }

                var rel = RelOf(p);
                if (refs.Contains(rel))
                {
                    continue;
                }

                ParseAnimPath(rel, out var aid, out var clip);
                if (aliases.TryGetValue(aid, out var mapped))
                {
                    aid = mapped;
                }

                if (apply)
                {
                    Bind(aid, "anim", rel, clip);
                    aliases[aid] = aid;
                }
                else
                {
                    actions.Add($"LINK anim {rel} -> {aid}" + (clip != null ? $" clip={clip}" : ""));
                }
            }

            foreach (var p in Enumerate(data.dirs.vfx))
            {
                var ext = Path.GetExtension(p);
                if (!VfxExt.Contains(ext) && !ImgExt.Contains(ext))
                {
                    continue;
                }

                var rel = RelOf(p);
                if (refs.Contains(rel))
                {
                    continue;
                }

                var parts = rel.Split('/');
                var aid = parts.Length >= 3 ? parts[1] : Path.GetFileNameWithoutExtension(rel);
                if (aid.EndsWith("_preview") || aid.EndsWith("_thumb"))
                {
                    aid = aid.Replace("_preview", "").Replace("_thumb", "");
                }

                if (aliases.TryGetValue(aid, out var mapped))
                {
                    aid = mapped;
                }

                if (apply)
                {
                    Bind(aid, "vfx", rel);
                    aliases[aid] = aid;
                }
                else
                {
                    actions.Add($"LINK vfx {rel} -> {aid}");
                }
            }

            foreach (var p in Enumerate(data.dirs.previews))
            {
                if (!ImgExt.Contains(Path.GetExtension(p)))
                {
                    continue;
                }

                var rel = RelOf(p);
                if (refs.Contains(rel))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(p);
                var aid = aliases.TryGetValue(stem, out var mapped) ? mapped : stem;
                if (apply)
                {
                    Bind(aid, "preview", rel);
                }
                else
                {
                    actions.Add($"LINK preview {rel} -> {aid}");
                }
            }

            if (apply)
            {
                foreach (var a in data.assets)
                {
                    if (!string.IsNullOrEmpty(a.mesh))
                    {
                        continue;
                    }

                    foreach (var ext in MeshExt)
                    {
                        var cand = Path.Combine(CellAbs, data.dirs.meshes, a.id + ext);
                        if (!File.Exists(cand))
                        {
                            continue;
                        }

                        a.mesh = RelOf(cand);
                        a.status = GuessStatus(a);
                        actions.Add($"FILL {a.id}.mesh = {a.mesh}");
                        break;
                    }
                }
            }

            return actions;
        }

        public static void WriteBoardHtml(CellArtRegistry data)
        {
            // 轻量：打开现有 board，或调 Python 生成；这里用内嵌最小 HTML
            var cards = new System.Text.StringBuilder();
            foreach (var a in data.assets)
            {
                var imgRel = !string.IsNullOrEmpty(a.concept) ? a.concept : a.preview;
                var img = PathExists(imgRel)
                    ? $"<img src=\"{Escape(imgRel)}\" />"
                    : $"<div class=\"missing\">{Escape(a.kind)}</div>";
                cards.Append($@"
<article class=""card"" data-kind=""{Escape(a.kind)}"" data-route=""{Escape(a.route)}"" data-status=""{Escape(a.status)}"">
  <div class=""thumb"">{img}</div>
  <h2>{Escape(a.name_zh)}{(a.needs_review ? " · 待审" : "")}</h2>
  <code>{Escape(a.id)}</code>
  <p>{Escape(a.kind)} / {Escape(a.slot)} / {Escape(a.route)} / {Escape(a.status)}</p>
  <p>mesh: {Escape(a.mesh)} · anim: {Escape(a.anim)} · vfx: {Escape(a.vfx)}</p>
</article>");
            }

            var html = $@"<!DOCTYPE html><html lang=""zh-CN""><head><meta charset=""utf-8""/>
<title>Cell Art Board</title>
<style>
body{{margin:0;padding:24px;background:#12141a;color:#e8e6e3;font-family:Segoe UI,Microsoft YaHei,sans-serif}}
.grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:12px}}
.card{{background:#1b1f2a;border:1px solid #2c3344;border-radius:10px;padding:10px}}
.thumb{{aspect-ratio:1;background:#0d0f14;display:grid;place-items:center;margin-bottom:8px}}
.thumb img{{width:100%;height:100%;object-fit:contain}}
.missing{{color:#9aa3b2}} code{{color:#9aa3b2;font-size:12px}}
</style></head><body>
<h1>细胞美术资源板</h1>
<p>{Escape(data.updated)} · {data.assets.Count} 项 · 由 Unity Editor 生成</p>
<div class=""grid"">{cards}</div></body></html>";
            File.WriteAllText(BoardAbs, html);
        }

        static void Normalize(CellArtAsset a)
        {
            a.views ??= new Dictionary<string, string>();
            a.anim_clips ??= new List<CellArtAnimClip>();
            a.slot ??= "none";
            a.route ??= "none";
            a.status ??= "todo";
            a.kind ??= "module";
            a.name_zh ??= a.id;
        }

        static CellArtAsset NewStub(string id, string kind)
        {
            var a = new CellArtAsset
            {
                id = id,
                name_zh = id,
                kind = kind,
                slot = InferSlot(id, kind),
                route = InferRoute(id),
                rarity = "common",
                views = new Dictionary<string, string>(),
                anim_clips = new List<CellArtAnimClip>(),
                status = "todo",
                notes = "Editor scan 自动入列；请补中文名/槽位",
                needs_review = true,
            };
            return a;
        }

        static string InferKind(string stem)
        {
            var s = stem.ToLowerInvariant();
            if (s.StartsWith("vfx_") || s.StartsWith("fx_"))
            {
                return "vfx";
            }

            if (s.StartsWith("anim_") || s.StartsWith("ani_"))
            {
                return "anim";
            }

            if (s.StartsWith("ui_") || s.StartsWith("hud_"))
            {
                return "ui";
            }

            if (s.StartsWith("env_") || s.StartsWith("scene_"))
            {
                return "env";
            }

            if (s.StartsWith("boss_"))
            {
                return "boss";
            }

            if (s.StartsWith("enemy_"))
            {
                return "enemy";
            }

            if (s.StartsWith("player_") || s.StartsWith("cell_base_") || s.StartsWith("base_"))
            {
                return "base";
            }

            return "module";
        }

        static string InferRoute(string stem)
        {
            var s = stem.ToLowerInvariant();
            foreach (var (key, val) in new[]
                     {
                         ("devour", "Devour"), ("agile", "Agile"), ("electric", "Electric"),
                         ("spore", "Spore"), ("nest", "Nest"), ("corrupt", "Corrupt")
                     })
            {
                if (s.Contains(key))
                {
                    return val;
                }
            }

            return "none";
        }

        static string InferSlot(string stem, string kind)
        {
            var s = stem.ToLowerInvariant();
            if (ContainsAny(s, "maw", "mouth", "jaw", "bite"))
            {
                return "maw";
            }

            if (ContainsAny(s, "flagella", "flagellum", "motility", "tail", "propulsion"))
            {
                return "motility";
            }

            if (ContainsAny(s, "crown", "tendril", "cluster", "append", "spore"))
            {
                return "appendage";
            }

            if (ContainsAny(s, "anchor", "territory", "carpet", "nest"))
            {
                return "territory";
            }

            if (ContainsAny(s, "shell", "membrane", "carapace", "armor"))
            {
                return "membrane";
            }

            if (ContainsAny(s, "core", "base", "body", "nucleus"))
            {
                return "core";
            }

            return kind is "vfx" or "anim" or "ui" or "env" ? "none" : "none";
        }

        static bool ContainsAny(string s, params string[] keys) => keys.Any(s.Contains);

        static HashSet<string> CollectRefs(CellArtRegistry data)
        {
            var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in data.assets)
            {
                AddRef(refs, a.concept);
                AddRef(refs, a.mesh);
                AddRef(refs, a.anim);
                AddRef(refs, a.vfx);
                AddRef(refs, a.preview);
                AddRef(refs, a.raw);
                if (a.views != null)
                {
                    foreach (var v in a.views.Values)
                    {
                        AddRef(refs, v);
                    }
                }

                if (a.anim_clips == null)
                {
                    continue;
                }

                foreach (var c in a.anim_clips)
                {
                    AddRef(refs, c?.path);
                }
            }

            return refs;
        }

        static void AddRef(HashSet<string> set, string rel)
        {
            if (!string.IsNullOrEmpty(rel))
            {
                set.Add(rel);
            }
        }

        static Dictionary<string, string> BuildAliases(CellArtRegistry data)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in data.assets)
            {
                map[a.id] = a.id;
                foreach (var rel in new[] { a.concept, a.mesh, a.anim, a.vfx, a.preview })
                {
                    if (!string.IsNullOrEmpty(rel))
                    {
                        map[Path.GetFileNameWithoutExtension(rel)] = a.id;
                    }
                }
            }

            return map;
        }

        static IEnumerable<string> Enumerate(string subdir)
        {
            var root = Path.Combine(CellAbs, subdir);
            if (!Directory.Exists(root))
            {
                yield break;
            }

            foreach (var p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(p);
                if (SkipNames.Contains(name) || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return p;
            }
        }

        static void ParseAnimPath(string rel, out string aid, out string clip)
        {
            var parts = rel.Split('/');
            if (parts.Length >= 3 && parts[0].Equals("Animations", StringComparison.OrdinalIgnoreCase))
            {
                aid = parts[1];
                clip = Path.GetFileNameWithoutExtension(parts[^1]);
                return;
            }

            var stem = Path.GetFileNameWithoutExtension(rel);
            if (stem.Contains("__"))
            {
                var idx = stem.IndexOf("__", StringComparison.Ordinal);
                aid = stem.Substring(0, idx);
                clip = stem.Substring(idx + 2);
                return;
            }

            aid = stem;
            clip = null;
        }

        static string GetField(CellArtAsset a, string field) => field switch
        {
            "concept" => a.concept,
            "mesh" => a.mesh,
            "anim" => a.anim,
            "vfx" => a.vfx,
            "preview" => a.preview,
            "raw" => a.raw,
            _ => null
        };

        static void SetField(CellArtAsset a, string field, string value)
        {
            switch (field)
            {
                case "concept": a.concept = value; break;
                case "mesh": a.mesh = value; break;
                case "anim": a.anim = value; break;
                case "vfx": a.vfx = value; break;
                case "preview": a.preview = value; break;
                case "raw": a.raw = value; break;
            }
        }

        static string Escape(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    [Serializable]
    public class CellArtRegistry
    {
        public int version = 1;
        public string updated;
        public string pipeline;
        public CellArtDirs dirs;
        public List<string> status_order;
        public List<string> slots;
        public List<string> routes;
        public List<CellArtAsset> assets;
    }

    [Serializable]
    public class CellArtDirs
    {
        public string concepts = "Concepts";
        public string meshes = "Meshes";
        public string animations = "Animations";
        public string vfx = "VFX";
        public string previews = "Previews";
        public string board = "board.html";
    }

    [Serializable]
    public class CellArtAsset
    {
        public string id;
        public string name_zh;
        public string kind;
        public string slot;
        public string route;
        public string rarity;
        public string concept;
        public Dictionary<string, string> views;
        public string mesh;
        public string anim;
        public List<CellArtAnimClip> anim_clips;
        public string vfx;
        public string preview;
        public string raw;
        public int? archetype_id;
        public string status;
        public string tripo_notes;
        public string notes;
        public bool needs_review;
    }

    [Serializable]
    public class CellArtAnimClip
    {
        public string name;
        public string path;
    }
}
