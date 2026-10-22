#if FLAX_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using FlaxEngine;
using FlaxEditor;
using Newtonsoft.Json.Linq;

namespace FlaxMcp
{
    public partial class Bridge
    {
        static readonly string[] SystemAssemblyPrefixes = { "System", "Microsoft", "mscorlib", "netstandard", "Newtonsoft" };
        object Resolve(JObject spec)
        {
            object target;
            switch (spec.Value<string>("kind"))
            {
                case "actor": target = Find(Guid.Parse(spec.Value<string>("id"))); break;
                case "script": target = Find(Guid.Parse(spec.Value<string>("id")))?.Scripts.FirstOrDefault(s => s.GetType().FullName == spec.Value<string>("type")); break;
                case "object": var oid = Guid.Parse(spec.Value<string>("id")); target = FlaxEngine.Object.TryFind<FlaxEngine.Object>(ref oid); break;
                case "asset": target = LoadAsset<Asset>(spec.Value<string>("asset")); break;
                case "editor": target = Editor; break;
                case "static": target = TypeNamed(spec.Value<string>("type")); break;
                default: throw new Exception("Target kind must be actor, script, object, asset, editor, or static");
            }
            foreach (var part in (spec.Value<string>("member") ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)) target = ReadMember(target, part);
            return target ?? throw new Exception("Target not found");
        }
        object ReadMember(object target, string name)
        {
            var t = target as Type ?? target.GetType(); var flags = BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
            var p = t.GetProperty(name, flags); if (p != null) return p.GetValue(target is Type ? null : target);
            var f = t.GetField(name, flags); if (f != null) return f.GetValue(target is Type ? null : target);
            throw new Exception("Member not found: " + name + " on " + t.FullName);
        }
        // JSON -> CLR. Special forms: {$ref: target}, {$type: name, value}, {$new: name, args: []}; strings name assets/actors for Object-typed slots.
        object ConvertValue(JToken token, Type type)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token is JObject obj)
            {
                if (obj["$ref"] is JObject spec) return Resolve(spec);
                if (obj["$type"] != null) return obj["value"].ToObject(TypeNamed(obj.Value<string>("$type")));
                if (obj["$new"] != null) return Construct(TypeNamed(obj.Value<string>("$new")), obj["args"] as JArray ?? new JArray());
            }
            if (type == typeof(Type)) return TypeNamed(token.ToString());
            if (typeof(FlaxEngine.Object).IsAssignableFrom(type) && token.Type == JTokenType.String)
            {
                if (typeof(Asset).IsAssignableFrom(type))
                {
                    var asset = LoadAsset<Asset>(token.ToString());
                    if (!type.IsInstanceOfType(asset)) throw new Exception(token + " is " + asset.GetType().Name + ", expected " + type.Name);
                    return asset;
                }
                var id = Guid.Parse(token.ToString());
                return FlaxEngine.Object.TryFind<FlaxEngine.Object>(ref id) ?? throw new Exception("Object not found: " + token);
            }
            if (type.IsArray && token is JArray items)
            {
                var element = type.GetElementType();
                var array = Array.CreateInstance(element, items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(ConvertValue(items[i], element), i);
                return array;
            }
            // Types such as LocalizedString convert implicitly from primitives; JSON deserialization ignores those operators.
            if (token is JValue primitive && primitive.Value != null && !type.IsInstanceOfType(primitive.Value))
            {
                var implicitOp = type.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == type && m.GetParameters()[0].ParameterType.IsInstanceOfType(primitive.Value));
                if (implicitOp != null) return implicitOp.Invoke(null, new[] { primitive.Value });
            }
            return token.ToObject(type);
        }
        object Construct(Type type, JArray args)
        {
            if (args.Count == 0 && type.IsValueType) return Activator.CreateInstance(type);
            var ctors = type.GetConstructors().Where(c => c.GetParameters().Length == args.Count).ToArray();
            foreach (var ctor in ctors)
            {
                try { return ctor.Invoke(ctor.GetParameters().Select((p, i) => ConvertValue(args[i], p.ParameterType)).ToArray()); }
                catch (Exception) when (ctors.Length > 1) { }
            }
            throw new Exception("No constructor of " + type.FullName + " accepts " + args.Count + " arguments");
        }
        object Value(object v, int depth = 0)
        {
            if (v == null) return null;
            if (v is FlaxEngine.Object o) return new { id = o.ID, type = o.GetType().FullName, name = (o as Actor)?.Name, asset = o is Asset a ? a.Path : null };
            var t = v.GetType();
            if (t.IsPrimitive || v is string || v is decimal) return v;
            if (v is Guid || t.IsEnum || v is Type) return v.ToString();
            if (v is IEnumerable seq && depth < 3) return seq.Cast<object>().Take(500).Select(x => Value(x, depth + 1)).ToArray();
            if (t.IsValueType)
            {
                var fields = new JObject();
                if (depth < 4) foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) fields[f.Name] = JToken.FromObject(Value(f.GetValue(v), depth + 1) ?? "null");
                return fields;
            }
            return new { type = t.FullName, text = v.ToString() };
        }
        object Invoke(JObject a)
        {
            var instance = Resolve((JObject)a["target"]);
            var it = instance as Type ?? instance.GetType();
            var args = a["args"] as JArray ?? new JArray();
            var name = a.Value<string>("method");
            var typeArgs = (a["typeArguments"] as JArray)?.Values<string>().Select(TypeNamed).ToArray();
            var candidates = it.GetMethods(BindingFlags.Public | (instance is Type ? BindingFlags.Static : BindingFlags.Instance)).Where(m => m.Name == name);
            candidates = typeArgs == null
                ? candidates.Where(m => !m.ContainsGenericParameters)
                : candidates.Where(m => m.IsGenericMethodDefinition && m.GetGenericArguments().Length == typeArgs.Length).Select(m => m.MakeGenericMethod(typeArgs));
            // ref/out parameters are supported; pointer parameters are left to flax_exec.
            var methods = candidates.Where(m => m.GetParameters().All(p => !p.ParameterType.IsPointer))
                .Where(m => m.GetParameters().Count(p => !p.IsOptional && !p.IsOut) <= args.Count && m.GetParameters().Length >= args.Count).ToArray();
            if (a["parameterTypes"] is JArray pts) methods = methods.Where(m => m.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(pts.Values<string>())).ToArray();
            if (methods.Length != 1) throw new Exception((methods.Length == 0 ? "Method missing: " : "Method ambiguous: ") + name + ". Use api then specify exact parameterTypes" + (typeArgs == null ? " (and typeArguments for generic methods)." : "."));
            var method = methods[0];
            var pars = method.GetParameters();
            var values = pars.Select((p, i) =>
            {
                var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (i < args.Count) return ConvertValue(args[i], type);
                if (p.IsOut) return type.IsValueType ? Activator.CreateInstance(type) : null;
                return p.DefaultValue;
            }).ToArray();
            var result = method.Invoke(instance is Type ? null : instance, values);
            var outs = new JObject();
            for (int i = 0; i < pars.Length; i++) if (pars[i].ParameterType.IsByRef) outs[pars[i].Name] = JToken.FromObject(Value(values[i]) ?? "null");
            return new { value = Value(result), @out = outs.Count > 0 ? outs : null, signature = method.ToString() };
        }
        object Extended(string op, JObject a)
        {
            switch (op)
            {
                case "types":
                    var filter = a.Value<string>("filter") ?? "";
                    var all = a.Value<bool>("all");
                    return AppDomain.CurrentDomain.GetAssemblies()
                        .Where(asm => all || !SystemAssemblyPrefixes.Any(p => asm.GetName().Name.StartsWith(p)))
                        .SelectMany(asm => { try { return asm.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); } })
                        .Where(t => t.IsPublic && t.FullName != null && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Take(a.Value<int?>("limit") ?? 200).Select(t => new { name = t.FullName, baseType = t.BaseType?.FullName, isAbstract = t.IsAbstract, assembly = t.Assembly.GetName().Name }).ToArray();
                case "api":
                    var type = TypeNamed(a.Value<string>("type"));
                    var member = a.Value<string>("member");
                    bool Wanted(string n) => member == null || n.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0;
                    return new
                    {
                        type = type.FullName, baseType = type.BaseType?.FullName,
                        constructors = type.GetConstructors().Select(c => c.GetParameters().Select(p => p.ParameterType.FullName + " " + p.Name)),
                        properties = type.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && Wanted(p.Name)).Select(p => new { name = p.Name, type = p.PropertyType.FullName, write = p.CanWrite, isStatic = p.GetMethod?.IsStatic }),
                        fields = type.GetFields().Where(f => Wanted(f.Name)).Select(f => new { name = f.Name, type = f.FieldType.FullName, isStatic = f.IsStatic }),
                        methods = type.GetMethods().Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object) && Wanted(m.Name)).Select(m => new
                        {
                            name = m.Name, returns = m.ReturnType.FullName ?? m.ReturnType.Name, isStatic = m.IsStatic,
                            genericArguments = m.IsGenericMethodDefinition ? m.GetGenericArguments().Select(g => g.Name).ToArray() : null,
                            parameters = m.GetParameters().Select(p => new { name = p.Name, type = p.ParameterType.FullName ?? p.ParameterType.Name, optional = p.IsOptional, @out = p.IsOut, byRef = p.ParameterType.IsByRef })
                        })
                    };
                case "get":
                    var target = Resolve((JObject)a["target"]);
                    var names = (a["members"] as JArray)?.Values<string>();
                    if (names == null) names = (target as Type ?? target.GetType()).GetProperties(BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance)).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name);
                    var result = new JObject();
                    foreach (var name in names.Take(100))
                    {
                        try { result[name] = JToken.FromObject(new { value = Value(ReadMember(target, name)) }); }
                        catch (Exception e) { result[name] = new JObject { { "error", Describe(e) } }; }
                    }
                    return result;
                case "object_set":
                    if (FlaxEditor.Editor.IsPlayMode && !a.Value<bool>("runtime")) throw new Exception("Set runtime=true to explicitly edit live state");
                    SetMembers(Resolve((JObject)a["target"]), (JObject)a["properties"], true);
                    if (!FlaxEditor.Editor.IsPlayMode) Editor.Scene.MarkAllScenesEdited();
                    return new { updated = true };
                case "call": return Invoke(a);
                case "batch":
                    var items = (JArray)a["operations"]; if (items.Count > 128) throw new Exception("Batch limit is 128");
                    var results = new JArray();
                    foreach (JObject item in items)
                    {
                        var name = item.Value<string>("op");
                        if (!new[] { "spawn", "set", "material", "delete", "select", "add_script" }.Contains(name)) throw new Exception("Unsupported batch operation: " + name);
                        try { results.Add(JToken.FromObject(new { ok = true, result = Execute(name, item["args"] as JObject ?? new JObject()) })); }
                        catch (Exception e) { results.Add(JToken.FromObject(new { ok = false, error = Describe(e) })); break; }
                    }
                    return new { results, atomic = false };
                case "material_create":
                    var output = ContentPath(a.Value<string>("path")); if (File.Exists(output)) throw new Exception("Material exists");
                    var baseMat = LoadAsset<MaterialBase>(a.Value<string>("base") ?? "$engine/Engine/SingleColorMaterial.flax");
                    var material = baseMat.CreateVirtualInstance();
                    foreach (var p in ((JObject)a["parameters"] ?? new JObject()).Properties())
                    {
                        if (material.GetParameter(p.Name) == null) throw new Exception("Material has no parameter " + p.Name + "; available: " + string.Join(", ", material.Parameters.Select(x => x.Name)));
                        material.SetParameterValue(p.Name, p.Value is JObject ? ConvertValue(p.Value, typeof(object)) : p.Value.ToObject<object>());
                    }
                    if (material.Save(output)) throw new Exception("Material save failed");
                    return new { path = output };
                case "prefab_create":
                    var prefabPath = ContentPath(a.Value<string>("path")); if (File.Exists(prefabPath)) throw new Exception("Prefab exists");
                    if (PrefabManager.CreatePrefab(Actor(a), prefabPath, true)) throw new Exception("Prefab creation failed");
                    return new { path = prefabPath };
                case "prefab_spawn":
                    var prefab = LoadAsset<Prefab>(a.Value<string>("path"));
                    var spawned = PrefabManager.SpawnPrefab(prefab, Level.Scenes.FirstOrDefault() ?? throw new Exception("No scene loaded"));
                    if (a["properties"] is JObject pr) SetMembers(spawned, pr, false);
                    Editor.Scene.MarkSceneEdited(spawned.Scene);
                    return Describe(spawned, 1);
                case "import":
                    var folder = ContentPath(a.Value<string>("folder") ?? "Imported"); Directory.CreateDirectory(folder);
                    Editor.ContentDatabase.RefreshFolder(Editor.ContentDatabase.Find(Globals.ProjectContentFolder), true);
                    var folderItem = Editor.ContentDatabase.Find(folder) as FlaxEditor.Content.ContentFolder ?? throw new Exception("Folder not indexed yet; retry after refresh");
                    Editor.ContentImporting.Import(a.Value<string>("source"), folderItem, true);
                    return new { queued = true, folder };
                case "close_scenes": if (Editor.Scene.IsEdited()) throw new Exception("Save scenes before closing"); Editor.Scene.CloseAllScenes(); return new { requested = true };
                case "undo": Editor.Undo.PerformUndo(); return new { requested = true };
                case "redo": Editor.Undo.PerformRedo(); return new { requested = true };
                default: throw new Exception("Unsupported extension operation");
            }
        }
    }
}
#endif
