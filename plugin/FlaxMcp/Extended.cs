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
        object Resolve(JObject spec)
        {
            object target;
            switch(spec.Value<string>("kind"))
            {
                case "actor": target = Find(Guid.Parse(spec.Value<string>("id"))); break;
                case "script": target = Find(Guid.Parse(spec.Value<string>("id")))?.Scripts.FirstOrDefault(s=>s.GetType().FullName==spec.Value<string>("type")); break;
                case "asset": target = FlaxEngine.Content.LoadAsync<Asset>(spec.Value<string>("asset")); ((Asset)target)?.WaitForLoaded(); break;
                case "editor": target = Editor; break;
                case "static": target = TypeNamed(spec.Value<string>("type")); break;
                default: throw new Exception("Target kind must be actor, script, asset, editor, or static");
            }
            foreach(var part in (spec.Value<string>("member") ?? "").Split('.',StringSplitOptions.RemoveEmptyEntries)) target=ReadMember(target,part);
            return target ?? throw new Exception("Target not found");
        }
        object ReadMember(object target,string name)
        {
            var t=target as Type ?? target.GetType(); var flags=BindingFlags.Public | (target is Type ? BindingFlags.Static : BindingFlags.Instance);
            var p=t.GetProperty(name,flags); if(p!=null)return p.GetValue(target is Type ? null : target);
            var f=t.GetField(name,flags); if(f!=null)return f.GetValue(target is Type ? null : target);
            throw new Exception("Member not found: "+name);
        }
        object ConvertValue(JToken token,Type type)
        {
            if(token is JObject obj && obj["$ref"] is JObject spec)return Resolve(spec);
            if(token is JObject typed && typed["$type"]!=null)return typed["value"].ToObject(TypeNamed(typed.Value<string>("$type")));
            if(type==typeof(Type))return TypeNamed(token.ToString());
            if(typeof(FlaxEngine.Object).IsAssignableFrom(type) && token.Type==JTokenType.String)
                return typeof(Asset).IsAssignableFrom(type) ? FlaxEngine.Content.LoadAsync<Asset>(token.ToString()) : Find(Guid.Parse(token.ToString()));
            return token.ToObject(type);
        }
        object Value(object v,int depth=0)
        {
            if(v==null)return null;
            if(v is FlaxEngine.Object o)return new {id=o.ID,type=o.GetType().FullName,asset=o is Asset a ? a.Path : null};
            var t=v.GetType();
            if(t.IsPrimitive || v is string || v is decimal)return v;
            if(v is Guid || t.IsEnum)return v.ToString();
            if(v is IEnumerable seq && depth<2)return seq.Cast<object>().Take(200).Select(x=>Value(x,depth+1)).ToArray();
            if(t.IsValueType){var fields=new JObject();foreach(var f in t.GetFields(BindingFlags.Public|BindingFlags.Instance)){if(depth<3)fields[f.Name]=JToken.FromObject(Value(f.GetValue(v),depth+1)??"null");}return fields;}
            return new {type=t.FullName,text=v.ToString()};
        }
        object Extended(string op,JObject a)
        {
            switch(op)
            {
                case "types":
                    var filter=a.Value<string>("filter") ?? "";
                    return AppDomain.CurrentDomain.GetAssemblies().SelectMany(asm=> {try{return asm.GetTypes();}catch(ReflectionTypeLoadException e){return e.Types.Where(t=>t!=null).ToArray();}}).Where(t=>t.IsPublic && t.FullName!=null && (t.FullName.StartsWith("Flax")||t.FullName.StartsWith("Game")) && t.FullName.IndexOf(filter,StringComparison.OrdinalIgnoreCase)>=0).Take(200).Select(t=>new {name=t.FullName,baseType=t.BaseType?.FullName,isAbstract=t.IsAbstract}).ToArray();
                case "api":
                    var type=TypeNamed(a.Value<string>("type"));
                    return new {type=type.FullName,properties=type.GetProperties().Where(p=>p.GetIndexParameters().Length==0).Select(p=>new{name=p.Name,type=p.PropertyType.FullName,write=p.CanWrite,isStatic=p.GetMethod?.IsStatic}),fields=type.GetFields().Select(f=>new{name=f.Name,type=f.FieldType.FullName,isStatic=f.IsStatic}),methods=type.GetMethods().Where(m=>!m.IsSpecialName && m.DeclaringType!=typeof(object)).Select(m=>new{name=m.Name,returns=m.ReturnType.FullName,isStatic=m.IsStatic,parameters=m.GetParameters().Select(p=>new{name=p.Name,type=p.ParameterType.FullName,optional=p.IsOptional})})};
                case "get":
                    var target=Resolve((JObject)a["target"]);
                    var names=(a["members"] as JArray)?.Values<string>();
                    if(names==null) names=(target as Type ?? target.GetType()).GetProperties(BindingFlags.Public | (target is Type?BindingFlags.Static:BindingFlags.Instance)).Where(p=>p.GetIndexParameters().Length==0).Select(p=>p.Name);
                    var result=new JObject(); foreach(var name in names.Take(100)) {try{result[name]=JToken.FromObject(new{value=Value(ReadMember(target,name))});}catch(Exception e){result[name]=new JObject{{"error",e.GetBaseException().Message}};}} return result;
                case "object_set":
                    if(FlaxEditor.Editor.IsPlayMode && !a.Value<bool>("runtime"))throw new Exception("Set runtime=true to explicitly edit live state");
                    var obj=Resolve((JObject)a["target"]); var tp=obj as Type ?? obj.GetType(); var flags=BindingFlags.Public | (obj is Type?BindingFlags.Static:BindingFlags.Instance);
                    foreach(var prop in ((JObject)a["properties"]).Properties()) {var p=tp.GetProperty(prop.Name,flags);var f=tp.GetField(prop.Name,flags);var converted=ConvertValue(prop.Value,p?.PropertyType??f?.FieldType??throw new Exception("Unknown member "+prop.Name));if(p!=null)p.SetValue(obj is Type?null:obj,converted);else f.SetValue(obj is Type?null:obj,converted);}
                    if(!FlaxEditor.Editor.IsPlayMode)Editor.Scene.MarkAllScenesEdited();return new{updated=true};
                case "call":
                    var instance=Resolve((JObject)a["target"]); var it=instance as Type ?? instance.GetType(); var args=a["args"] as JArray ?? new JArray();
                    var methods=it.GetMethods(BindingFlags.Public | (instance is Type?BindingFlags.Static:BindingFlags.Instance)).Where(m=>m.Name==a.Value<string>("method") && !m.ContainsGenericParameters && m.GetParameters().All(p=>!p.ParameterType.IsByRef)).Where(m=>m.GetParameters().Count(p=>!p.IsOptional)<=args.Count && m.GetParameters().Length>=args.Count).ToArray();
                    if(a["parameterTypes"] is JArray pts)methods=methods.Where(m=>m.GetParameters().Select(p=>p.ParameterType.FullName).SequenceEqual(pts.Values<string>())).ToArray();
                    if(methods.Length!=1)throw new Exception("Method missing or ambiguous. Use api then specify exact parameterTypes.");
                    var method=methods[0]; var pars=method.GetParameters();var values=pars.Select((p,i)=>i<args.Count?ConvertValue(args[i],p.ParameterType):p.DefaultValue).ToArray();
                    return new {value=Value(method.Invoke(instance is Type?null:instance,values))};
                case "batch":
                    var items=(JArray)a["operations"];if(items.Count>128)throw new Exception("Batch limit is 128");
                    var results=new JArray();foreach(JObject item in items){var name=item.Value<string>("op");if(!new[]{"spawn","set","material","delete","select","add_script"}.Contains(name))throw new Exception("Unsupported batch operation");try{results.Add(JToken.FromObject(new{ok=true,result=Execute(name,item["args"] as JObject??new JObject())}));}catch(Exception e){results.Add(JToken.FromObject(new{ok=false,error=e.GetBaseException().Message}));break;}}return new{results,atomic=false};
                case "material_create":
                    var output=ContentPath(a.Value<string>("path")); if(File.Exists(output))throw new Exception("Material exists");
                    var baseMat=FlaxEngine.Content.LoadAsync<MaterialBase>(a.Value<string>("base")??Path.Combine(Globals.EngineContentFolder,"Engine/SingleColorMaterial.flax"));baseMat.WaitForLoaded();var material=baseMat.CreateVirtualInstance();
                    foreach(var p in ((JObject)a["parameters"]).Properties()){var val=p.Value is JObject?ConvertValue(p.Value,typeof(object)):p.Value.ToObject<object>();material.SetParameterValue(p.Name,val);}
                    if(material.Save(output))throw new Exception("Material save failed");return new{path=output};
                case "prefab_create":
                    var prefabPath=ContentPath(a.Value<string>("path"));if(File.Exists(prefabPath))throw new Exception("Prefab exists");return new{result=PrefabManager.CreatePrefab(Actor(a),prefabPath,true),path=prefabPath};
                case "prefab_spawn":
                    var prefab=FlaxEngine.Content.LoadAsync<Prefab>(ContentPath(a.Value<string>("path")));prefab.WaitForLoaded();var spawned=PrefabManager.SpawnPrefab(prefab,Level.Scenes.First());if(a["properties"] is JObject pr)Properties(spawned,pr);Editor.Scene.MarkSceneEdited(spawned.Scene);return Describe(spawned);
                case "import":
                    var folder=ContentPath(a.Value<string>("folder")??"Imported");Directory.CreateDirectory(folder);Editor.ContentDatabase.RefreshFolder(Editor.ContentDatabase.Find(Globals.ProjectContentFolder),true);var folderItem=Editor.ContentDatabase.Find(folder) as FlaxEditor.Content.ContentFolder;if(folderItem==null)throw new Exception("Folder not indexed yet; retry after refresh");Editor.ContentImporting.Import(a.Value<string>("source"),folderItem,true);return new{queued=true,folder};
                case "close_scenes": if(Editor.Scene.IsEdited())throw new Exception("Save scenes before closing");Editor.Scene.CloseAllScenes();return new{requested=true};
                case "undo": Editor.Undo.PerformUndo();return new{requested=true};
                case "redo": Editor.Undo.PerformRedo();return new{requested=true};
                default: throw new Exception("Unsupported extension operation");
            }
        }
    }
}
#endif
