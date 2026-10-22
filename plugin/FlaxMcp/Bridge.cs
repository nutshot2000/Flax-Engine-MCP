#if FLAX_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FlaxEngine;
using FlaxEditor;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlaxMcp
{
    public partial class Bridge : EditorPlugin
    {
        public const string Version = "0.4.0";
        static readonly string[] Capabilities = { "ordered-queue", "asset-path-prefixes", "undo-set", "ref-out-call", "generic-call", "new-objects", "exec", "find", "scene-tree-depth", "unload-scene" };
        string root;
        DateTime heartbeat;
        bool stopped;
        FileStream ownerLock;
        public override void InitializeEditor()
        {
            base.InitializeEditor();
            root = Path.Combine(Globals.ProjectFolder, "Cache", "McpBridge");
            Directory.CreateDirectory(Path.Combine(root, "requests"));
            Directory.CreateDirectory(Path.Combine(root, "receipts"));
            try { ownerLock = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Debug.LogError("MCP bridge already owned by another editor for this project"); return; }
            stopped = File.Exists(Path.Combine(root, "STOP"));
            Prune();
            Editor.EditorUpdate += Update;
            Debug.Log("Flax MCP bridge " + Version + " ready: " + root);
        }
        public override void DeinitializeEditor()
        {
            Editor.EditorUpdate -= Update;
            ownerLock?.Dispose();
            base.DeinitializeEditor();
        }
        void Write(string path, object value)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value));
            // Replacing a file fails with access denied while the MCP server is reading it; the reader only holds it briefly.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 40) { System.Threading.Thread.Sleep(5); }
            }
        }
        // Receipts and screenshots accumulate; keep a couple of days for post-crash inspection.
        void Prune()
        {
            var cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "receipts")).Concat(Directory.GetFiles(root, "*.png")))
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch (IOException) { }
        }
        object Status() => new
        {
            project = Globals.ProjectFolder, version = Version, capabilities = Capabilities, processId = Environment.ProcessId, utc = DateTime.UtcNow,
            engineContent = Globals.EngineContentFolder, projectContent = Globals.ProjectContentFolder,
            compiling = ScriptsBuilder.IsCompiling, compilationFailed = ScriptsBuilder.LastCompilationFailed, compilationCount = ScriptsBuilder.CompilationsCount,
            play = FlaxEditor.Editor.IsPlayMode, paused = Time.GamePaused, state = Editor.StateMachine.CurrentState.GetType().Name, stopped,
            sceneEdited = Editor.Scene.IsEdited(),
            scenes = Level.Scenes.Select(s => new { id = s.ID, name = s.Name, path = FlaxEngine.Content.GetEditorAssetPath(s.ID) }).ToArray()
        };
        void Update()
        {
            try
            {
                if ((DateTime.UtcNow - heartbeat).TotalSeconds > 1) { heartbeat = DateTime.UtcNow; Write(Path.Combine(root, "status.json"), Status()); }
                // File names begin with a timestamp and sequence number, so ordinal order is submission order.
                foreach (var path in Directory.GetFiles(Path.Combine(root, "requests"), "*.json").OrderBy(p => p, StringComparer.Ordinal).Take(8))
                {
                    try { Process(path); }
                    catch (Exception ex) { Debug.LogWarning("MCP request " + Path.GetFileName(path) + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Debug.LogWarning("MCP queue: " + ex.Message); }
        }
        void Process(string path)
        {
            var req = JObject.Parse(File.ReadAllText(path));
            string id = req.Value<string>("id") ?? Path.GetFileNameWithoutExtension(path);
            var receipt = Path.Combine(root, "receipts", id + ".json");
            if (File.Exists(receipt)) { File.Delete(path); return; }
            // A durable started receipt prevents replay following a crash.
            Write(receipt, new { id, state = "started" });
            File.Delete(path);
            object result;
            try
            {
                if (req.Value<DateTime>("deadline").ToUniversalTime() < DateTime.UtcNow) throw new Exception("Request expired before execution");
                result = Execute(req.Value<string>("op"), req["args"] as JObject ?? new JObject());
            }
            catch (Exception ex) { Write(receipt, new { id, state = "failed", error = Describe(ex) }); return; }
            // The operation has run; if its result cannot be recorded, say so rather than leaving it "started".
            try { Write(receipt, new { id, state = "complete", result }); }
            catch (Exception ex) { Write(receipt, new { id, state = "failed", error = "Operation ran but its result could not be recorded: " + Describe(ex), applied = true }); }
        }
        static string Describe(Exception ex)
        {
            var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }
        // The object registry misses some loaded scene roots, so fall back to the loaded hierarchy.
        internal static Actor Find(Guid id) =>
            FlaxEngine.Object.TryFind<Actor>(ref id)
            ?? Level.Scenes.FirstOrDefault(s => s.ID == id)
            ?? Level.Scenes.SelectMany(Descendants).FirstOrDefault(x => x.ID == id);
        Actor Actor(JObject a, string key = "id") => Find(Guid.Parse(a.Value<string>(key) ?? throw new Exception("Missing " + key))) ?? throw new Exception("Actor not found: " + a.Value<string>(key));
        Script ScriptOn(Actor actor, string type) => actor.Scripts.FirstOrDefault(s => s.GetType().FullName == type) ?? throw new Exception("Script " + type + " not attached to " + actor.Name);
        Type TypeNamed(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null) return t;
            }
            throw new Exception("Type not found: " + name);
        }
        object Describe(Actor actor, int depth = int.MaxValue) => new
        {
            id = actor.ID, name = actor.Name, type = actor.GetType().FullName, parent = actor.Parent?.ID,
            position = new[] { actor.Position.X, actor.Position.Y, actor.Position.Z }, scale = new[] { actor.Scale.X, actor.Scale.Y, actor.Scale.Z },
            scripts = actor.Scripts.Select(s => new { id = s.ID, type = s.GetType().FullName }).ToArray(),
            childCount = actor.ChildrenCount,
            children = depth > 0 ? actor.Children.Select(c => Describe(c, depth - 1)).ToArray() : null
        };
        string ContentPath(string relative)
        {
            var dir = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, "Content")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(dir, relative));
            if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) throw new Exception("Path must be under project Content");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }
        // Asset references accept "$engine/..." (engine Content), "$content/..." or a bare relative path (project Content), or an absolute path.
        static string AssetPath(string reference)
        {
            if (reference.StartsWith("$engine/")) return Path.Combine(Globals.EngineContentFolder, reference.Substring(8));
            if (reference.StartsWith("$content/")) return Path.Combine(Globals.ProjectContentFolder, reference.Substring(9));
            if (Guid.TryParse(reference, out _) || Path.IsPathRooted(reference)) return reference;
            return Path.Combine(Globals.ProjectContentFolder, reference);
        }
        static T LoadAsset<T>(string reference) where T : Asset
        {
            var path = AssetPath(reference);
            var asset = Guid.TryParse(path, out var id) ? FlaxEngine.Content.LoadAsync<T>(id) : FlaxEngine.Content.LoadAsync<T>(path);
            if (asset == null) throw new Exception("Asset not found or wrong type: " + reference);
            if (asset.WaitForLoaded()) throw new Exception("Asset failed to load: " + reference);
            return asset;
        }
        // Applies members with editor undo when editing persistent scene objects.
        void SetMembers(object target, JObject props, bool undo)
        {
            var record = undo && !FlaxEditor.Editor.IsPlayMode && target is FlaxEngine.Object;
            if (record) Editor.Undo.RecordBegin(target, "MCP edit");
            try
            {
                var type = target as Type ?? target.GetType();
                var flags = BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
                var instance = target is Type ? null : target;
                foreach (var p in props.Properties())
                {
                    var property = type.GetProperty(p.Name, flags);
                    var field = property == null ? type.GetField(p.Name, flags) : null;
                    var memberType = property?.PropertyType ?? field?.FieldType ?? throw new Exception("No public member " + p.Name + " on " + type.FullName);
                    var value = ConvertValue(p.Value, memberType);
                    if (property != null)
                    {
                        if (!property.CanWrite) throw new Exception(p.Name + " is read-only");
                        property.SetValue(instance, value);
                    }
                    else field.SetValue(instance, value);
                }
            }
            finally { if (record) Editor.Undo.RecordEnd(target, null, null); }
        }
        object Execute(string op, JObject a)
        {
            switch (op)
            {
                case "status": return Status();
                case "scene_tree":
                    var depth = a.Value<int?>("depth") ?? int.MaxValue;
                    return a["id"] != null ? new[] { Describe(Actor(a), depth) } : Level.Scenes.Select(s => Describe(s, depth)).ToArray();
                case "find": return FindActors(a);
                case "types": case "api": case "get": return Extended(op, a);
                case "inspect":
                    object target = Actor(a);
                    if (a["script"] != null) target = ScriptOn((Actor)target, a.Value<string>("script"));
                    return target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => new { name = p.Name, type = p.PropertyType.FullName, writable = p.CanWrite, value = SafeValue(target, p) }).ToArray();
                case "stop": stopped = true; File.WriteAllText(Path.Combine(root, "STOP"), "Stopped"); Editor.Simulation.RequestStopPlay(); return Status();
                case "resume_control": stopped = false; File.Delete(Path.Combine(root, "STOP")); return Status();
            }
            if (stopped) throw new Exception("Bridge stopped. Explicitly resume_control before mutations.");
            switch (op)
            {
                case "play": Editor.Simulation.RequestStartPlayScenes(); return new { requested = true };
                case "pause": Editor.Simulation.RequestPausePlay(); return new { requested = true };
                case "resume_play": Editor.Simulation.RequestResumePlay(); return new { requested = true };
                case "stop_play": Editor.Simulation.RequestStopPlay(); return new { requested = true };
                case "screenshot":
                    var shot = Path.Combine(root, Guid.NewGuid().ToString("N") + ".png");
                    Screenshot.Capture(a.Value<string>("view") == "game" ? Editor.Windows.GameWin.Viewport.Task : Editor.Windows.EditWin.Viewport.Task, shot);
                    return new { path = shot, requested = true };
                case "call": case "object_set": return Extended(op, a);
                case "exec": return Exec(a);
            }
            if (FlaxEditor.Editor.IsPlayMode) throw new Exception("Stop play before editing persistent scene objects");
            if (new[] { "batch", "material_create", "prefab_create", "prefab_spawn", "import", "close_scenes", "undo", "redo" }.Contains(op)) return Extended(op, a);
            switch (op)
            {
                case "compile": FlaxEditor.ScriptsBuilder.Compile(); return new { requested = true };
                case "create_scene":
                    var path = ContentPath(a.Value<string>("path"));
                    if (File.Exists(path)) throw new Exception("Scene already exists");
                    Editor.Scene.CreateSceneFile(path);
                    var scene = Level.LoadSceneFromBytes(File.ReadAllBytes(path));
                    // New scenes come with a default sun, sky, sky light, floor and camera.
                    if (a.Value<bool>("empty")) foreach (var child in scene.Children) FlaxEngine.Object.DestroyNow(child);
                    return new { id = scene.ID, path, children = scene.Children.Select(c => Describe(c, 0)).ToArray() };
                case "save":
                    foreach (var s in Level.Scenes) if (Level.SaveScene(s)) throw new Exception("Scene save failed: " + s.Name);
                    foreach (var s in Level.Scenes) if (Editor.Scene.GetActorNode(s) is FlaxEditor.SceneGraph.Actors.SceneNode node) node.IsEdited = false;
                    return new { saved = true };
                case "open_scene":
                    var scenePath = ContentPath(a.Value<string>("path"));
                    if (!File.Exists(scenePath)) throw new Exception("Scene file missing");
                    var loaded = Level.LoadSceneFromBytes(File.ReadAllBytes(scenePath));
                    return new { id = loaded.ID };
                case "unload_scene":
                    var doomedScene = Actor(a) as Scene ?? throw new Exception("Expected a scene ID");
                    if (Editor.Scene.GetActorNode(doomedScene) is FlaxEditor.SceneGraph.Actors.SceneNode edited && edited.IsEdited && !a.Value<bool>("discard")) throw new Exception("Scene has unsaved changes; save first or pass discard=true");
                    Level.UnloadScene(doomedScene);
                    return new { unloaded = true };
                case "material":
                    var model = Actor(a) as StaticModel ?? throw new Exception("Expected StaticModel");
                    model.Model?.WaitForLoaded();
                    Editor.Undo.RecordBegin(model, "MCP material");
                    model.SetMaterial(a.Value<int?>("slot") ?? 0, LoadAsset<MaterialBase>(a.Value<string>("path")));
                    Editor.Undo.RecordEnd(model, null, null);
                    Editor.Scene.MarkSceneEdited(model.Scene);
                    return Describe(model, 0);
                case "spawn":
                    var type = TypeNamed(a.Value<string>("type") ?? "FlaxEngine.EmptyActor");
                    if (!typeof(Actor).IsAssignableFrom(type) || typeof(Scene).IsAssignableFrom(type) || type.IsAbstract) throw new Exception("Expected a concrete non-scene Actor type");
                    var actor = (Actor)Activator.CreateInstance(type);
                    actor.Name = a.Value<string>("name") ?? type.Name;
                    if (a["properties"] is JObject props) SetMembers(actor, props, false);
                    var parent = a["parent"] == null ? Level.Scenes.FirstOrDefault() ?? throw new Exception("No scene loaded") : Actor(a, "parent");
                    // Unparented, the transform set so far is relative to the parent-to-be. Spawn keeps world transforms,
                    // which would cancel a scaled parent's scale on the child, so restore it as the local transform.
                    var local = actor.LocalTransform;
                    Editor.SceneEditing.Spawn(actor, parent);
                    actor.LocalTransform = local;
                    // The editor applies spawn defaults for some types (e.g. TextRender text), so apply requested values again.
                    if (a["properties"] is JObject after) SetMembers(actor, after, false);
                    return Describe(actor, 0);
                case "set":
                    var act = Actor(a);
                    object obj = a["script"] != null ? ScriptOn(act, a.Value<string>("script")) : act;
                    SetMembers(obj, (JObject)a["properties"], true);
                    Editor.Scene.MarkSceneEdited(act.Scene);
                    return Describe(act, 0);
                case "add_script":
                    var owner = Actor(a);
                    var scriptType = TypeNamed(a.Value<string>("type"));
                    if (!typeof(Script).IsAssignableFrom(scriptType)) throw new Exception("Expected Script type");
                    var script = owner.AddScript(scriptType);
                    if (a["properties"] is JObject scriptProps) SetMembers(script, scriptProps, false);
                    Editor.Scene.MarkSceneEdited(owner.Scene);
                    return Describe(owner, 0);
                case "delete":
                    var doomed = Actor(a);
                    if (doomed is Scene) throw new Exception("Cannot delete scene root; use unload_scene");
                    Editor.SceneEditing.Select(doomed); Editor.SceneEditing.Delete(); return new { deleted = true };
                case "select": Editor.SceneEditing.Select(Actor(a)); return new { selected = true };
                case "camera": SetMembers(Editor.Windows.EditWin.Viewport, (JObject)a["properties"], false); return new { updated = true };
                default: throw new Exception("Unknown operation: " + op);
            }
        }
        object FindActors(JObject a)
        {
            var name = a.Value<string>("name");
            var type = a["type"] != null ? TypeNamed(a.Value<string>("type")) : null;
            var tag = a.Value<string>("tag");
            var limit = a.Value<int?>("limit") ?? 100;
            var matches = Level.Scenes.SelectMany(Descendants).Where(x =>
                (name == null || x.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (type == null || type.IsInstanceOfType(x)) &&
                (tag == null || x.Tags.Any(t => t.ToString() == tag)));
            return matches.Take(limit).Select(x => Describe(x, 0)).ToArray();
        }
        // Actor.GetChildren<T>() only returns direct children.
        static System.Collections.Generic.IEnumerable<Actor> Descendants(Actor actor)
        {
            foreach (var child in actor.Children)
            {
                yield return child;
                foreach (var nested in Descendants(child)) yield return nested;
            }
        }
        object SafeValue(object obj, PropertyInfo p)
        {
            try { var v = p.GetValue(obj); return v is FlaxEngine.Object o ? o.ID.ToString() : v?.ToString(); } catch { return null; }
        }
    }
}
#endif
