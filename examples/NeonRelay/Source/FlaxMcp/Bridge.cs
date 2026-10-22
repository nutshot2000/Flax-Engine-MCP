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
            Editor.EditorUpdate += Update;
            Debug.Log("Flax MCP bridge ready: " + root);
        }
        public override void DeinitializeEditor() { Editor.EditorUpdate -= Update; ownerLock?.Dispose(); base.DeinitializeEditor(); }
        void Write(string path, object value)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value));
            File.Move(temp, path, true);
        }
        object Status() => new { project = Globals.ProjectFolder, version = "0.3.0", processId=Environment.ProcessId, utc = DateTime.UtcNow, compiling=ScriptsBuilder.IsCompiling, compilationFailed=ScriptsBuilder.LastCompilationFailed, compilationCount=ScriptsBuilder.CompilationsCount, play = FlaxEditor.Editor.IsPlayMode, paused = Time.GamePaused, state = Editor.StateMachine.CurrentState.GetType().Name, stopped, scenes = Level.Scenes.Select(s => new { id = s.ID, name = s.Name }).ToArray() };
        void Update()
        {
            try
            {
                if ((DateTime.UtcNow - heartbeat).TotalSeconds > 1) { heartbeat = DateTime.UtcNow; Write(Path.Combine(root, "status.json"), Status()); }
                foreach (var path in Directory.GetFiles(Path.Combine(root, "requests"), "*.json").Take(8))
                {
                    string id = Path.GetFileNameWithoutExtension(path);
                    var receipt = Path.Combine(root, "receipts", id + ".json");
                    if (File.Exists(receipt)) { File.Delete(path); continue; }
                    var req = JObject.Parse(File.ReadAllText(path));
                    // A durable started receipt prevents replay following a crash.
                    Write(receipt, new { id, state = "started" });
                    try
                    {
                        if (req.Value<DateTime>("deadline").ToUniversalTime() < DateTime.UtcNow) throw new Exception("Request expired before execution");
                        var result = Execute(req.Value<string>("op"), req["args"] as JObject ?? new JObject());
                        Write(receipt, new { id, state = "complete", result });
                    }
                    catch (Exception ex) { Write(receipt, new { id, state = "failed", error = ex.GetBaseException().Message }); }
                    File.Delete(path);
                }
            }
            catch (Exception ex) { Debug.LogWarning("MCP queue: " + ex.Message); }
        }
        Actor Find(Guid id)
        {
            Actor Walk(Actor actor) { if(actor.ID==id)return actor;foreach(var child in actor.Children){var found=Walk(child);if(found!=null)return found;}return null; }
            foreach(var scene in Level.Scenes){var found=Walk(scene);if(found!=null)return found;}return null;
        }
        Actor Actor(JObject a) => Find(Guid.Parse(a.Value<string>("id"))) ?? throw new Exception("Actor not found");
        Type TypeNamed(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType(name)).FirstOrDefault(x => x != null) ?? throw new Exception("Type not found: " + name);
        object Describe(Actor actor) => new { id = actor.ID, name = actor.Name, type = actor.GetType().FullName, parent = actor.Parent?.ID, position = new[] { actor.Position.X, actor.Position.Y, actor.Position.Z }, scale = new[] { actor.Scale.X, actor.Scale.Y, actor.Scale.Z }, scripts = actor.Scripts.Select(s => new { id = s.ID, type = s.GetType().FullName }).ToArray(), children = actor.Children.Select(Describe).ToArray() };
        string ContentPath(string relative)
        {
            var dir = Path.GetFullPath(Path.Combine(Globals.ProjectFolder, "Content")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(dir, relative));
            if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) throw new Exception("Path must be under project Content");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return path;
        }
        void Properties(object target, JObject props)
        {
            foreach (var p in props.Properties())
            {
                var property = target.GetType().GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
                var field = target.GetType().GetField(p.Name, BindingFlags.Public | BindingFlags.Instance);
                var type = property?.PropertyType ?? field?.FieldType ?? throw new Exception("No property: " + p.Name);
                object value;
                if (typeof(FlaxEngine.Object).IsAssignableFrom(type))
                {
                    var reference = p.Value.Type == JTokenType.Null ? null : p.Value.ToString();
                    value = reference == null ? null : typeof(Asset).IsAssignableFrom(type) ? FlaxEngine.Content.LoadAsync<Asset>(reference) : Find(Guid.Parse(reference));
                }
                else value = p.Value.ToObject(type);
                if (property != null) property.SetValue(target, value); else field.SetValue(target, value);
            }
        }
        object Execute(string op, JObject a)
        {
            if (op == "status") return Status();
            if (op == "scene_tree") return Level.Scenes.Select(Describe).ToArray();
            if (op == "types" || op == "api" || op == "get") return Extended(op, a);
            if (op == "inspect")
            {
                var target = (object)Actor(a);
                if (a["script"] != null) target = Actor(a).Scripts.First(s => s.GetType().FullName == a.Value<string>("script"));
                return target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => new { name = p.Name, type = p.PropertyType.FullName, writable = p.CanWrite, value = SafeValue(target, p) }).ToArray();
            }
            if (op == "stop") { stopped = true; File.WriteAllText(Path.Combine(root, "STOP"), "Stopped"); Editor.Simulation.RequestStopPlay(); return Status(); }
            if (op == "resume_control") { stopped = false; File.Delete(Path.Combine(root, "STOP")); return Status(); }
            if (stopped) throw new Exception("Bridge stopped. Explicitly resume_control before mutations.");
            if (op == "play") { Editor.Simulation.RequestStartPlayScenes(); return new { requested = true }; }
            if (op == "pause") { Editor.Simulation.RequestPausePlay(); return new { requested = true }; }
            if (op == "resume_play") { Editor.Simulation.RequestResumePlay(); return new { requested = true }; }
            if (op == "stop_play") { Editor.Simulation.RequestStopPlay(); return new { requested = true }; }
            if (op == "screenshot")
            {
                var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".png");
                Screenshot.Capture(a.Value<string>("view") == "game" ? Editor.Windows.GameWin.Viewport.Task : Editor.Windows.EditWin.Viewport.Task, path);
                return new { path, requested = true };
            }
            if (op == "call" || op == "object_set") return Extended(op, a);
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
                    return new { id = scene.ID, path };
                case "save":
                    foreach (var s in Level.Scenes) if (Level.SaveScene(s)) throw new Exception("Scene save failed: " + s.Name);
                    foreach (var s in Level.Scenes) if(Editor.Scene.GetActorNode(s) is FlaxEditor.SceneGraph.Actors.SceneNode node) node.IsEdited=false;
                    return new { saved = true };
                case "open_scene":
                    var scenePath = ContentPath(a.Value<string>("path"));
                    if (!File.Exists(scenePath)) throw new Exception("Scene file missing");
                    var loaded = Level.LoadSceneFromBytes(File.ReadAllBytes(scenePath));
                    return new { id = loaded.ID };
                case "material":
                    var model = Actor(a) as StaticModel ?? throw new Exception("Expected StaticModel");
                    model.Model?.WaitForLoaded();
                    model.SetMaterial(a.Value<int?>("slot") ?? 0, FlaxEngine.Content.LoadAsync<MaterialBase>(a.Value<string>("path")));
                    Editor.Scene.MarkSceneEdited(model.Scene);
                    return Describe(model);
                case "spawn":
                    var type = TypeNamed(a.Value<string>("type") ?? "FlaxEngine.EmptyActor");
                    if (!typeof(Actor).IsAssignableFrom(type) || typeof(Scene).IsAssignableFrom(type)) throw new Exception("Expected a non-scene Actor type");
                    var actor = (Actor)Activator.CreateInstance(type);
                    actor.Name = a.Value<string>("name") ?? type.Name;
                    if (a["properties"] is JObject props) Properties(actor, props);
                    Editor.SceneEditing.Spawn(actor, a["parent"] == null ? Level.Scenes.First() : Find(Guid.Parse(a.Value<string>("parent"))));
                    return Describe(actor);
                case "set":
                    var act = Actor(a);
                    object obj = act;
                    if (a["script"] != null) obj = act.Scripts.First(s => s.GetType().FullName == a.Value<string>("script"));
                    Properties(obj, (JObject)a["properties"]);
                    Editor.Scene.MarkSceneEdited(act.Scene);
                    return Describe(act);
                case "add_script":
                    var owner = Actor(a);
                    var scriptType = TypeNamed(a.Value<string>("type"));
                    if (!typeof(Script).IsAssignableFrom(scriptType)) throw new Exception("Expected Script type");
                    var script = owner.AddScript(scriptType);
                    if (a["properties"] is JObject scriptProps) Properties(script, scriptProps);
                    Editor.Scene.MarkSceneEdited(owner.Scene);
                    return Describe(owner);
                case "delete":
                    var doomed = Actor(a);
                    if (doomed is Scene) throw new Exception("Cannot delete scene root");
                    Editor.SceneEditing.Select(doomed); Editor.SceneEditing.Delete(); return new { deleted = true };
                case "select": Editor.SceneEditing.Select(Actor(a)); return new { selected = true };
                case "camera":
                    Properties(Editor.Windows.EditWin.Viewport, (JObject)a["properties"]); return new { updated = true };
                default: throw new Exception("Unknown operation: " + op);
            }
        }
        object SafeValue(object obj, PropertyInfo p)
        {
            try { var v = p.GetValue(obj); return v is FlaxEngine.Object o ? o.ID.ToString() : v?.ToString(); } catch { return null; }
        }
    }
}
#endif
