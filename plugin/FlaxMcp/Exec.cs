#if FLAX_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using FlaxEngine;
using FlaxEditor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;

namespace FlaxMcp
{
    /// <summary>Passed to flax_exec snippets as <c>ctx</c>.</summary>
    public class ExecContext
    {
        public readonly List<string> Logs = new List<string>();
        public Editor Editor => Editor.Instance;
        public Scene Scene => Level.Scenes.FirstOrDefault();
        public void Log(object value) => Logs.Add(value?.ToString() ?? "null");
        public Actor Find(string id) => Bridge.Find(Guid.Parse(id));
        public T Asset<T>(string reference) where T : Asset => Bridge.LoadAssetPublic<T>(reference);
        /// <summary>Applies an edit to a scene object as one editor undo step and marks its scene edited.</summary>
        public void Edit(SceneObject target, Action action)
        {
            Editor.Undo.RecordBegin(target, "MCP exec");
            try { action(); }
            finally { Editor.Undo.RecordEnd(target, null, null); }
            var scene = (target as Actor ?? (target as Script)?.Actor)?.Scene;
            if (scene != null) Editor.Scene.MarkSceneEdited(scene);
        }
    }

    public partial class Bridge
    {
        static readonly string[] DefaultUsings = { "System", "System.Linq", "System.Collections.Generic", "System.IO", "FlaxEngine", "FlaxEditor", "FlaxMcp" };
        readonly Dictionary<string, (DateTime stamp, MetadataReference reference)> referenceCache = new Dictionary<string, (DateTime, MetadataReference)>();

        internal static T LoadAssetPublic<T>(string reference) where T : Asset => LoadAsset<T>(reference);

        // After hot reloads, copies of game assemblies from unloading contexts can still be listed; prefer the one
        // in the plugin's own (live) context, where the running game scripts are.
        static Assembly Live(IEnumerable<Assembly> sameName)
        {
            var live = AssemblyLoadContext.GetLoadContext(typeof(Bridge).Assembly);
            var all = sameName.ToArray();
            return all.LastOrDefault(x => AssemblyLoadContext.GetLoadContext(x) == live) ?? all.LastOrDefault();
        }

        // Snippets resolve every dependency to the assembly already loaded in the editor, so engine and game types keep their identity.
        class SnippetLoadContext : AssemblyLoadContext
        {
            public SnippetLoadContext() : base("McpSnippet", true) { }
            protected override Assembly Load(AssemblyName name) => Live(AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic && x.GetName().Name == name.Name));
        }

        string LocateAssembly(Assembly asm)
        {
            if (!string.IsNullOrEmpty(asm.Location)) return asm.Location;
            // Hot-reloaded game modules may be loaded from memory; fall back to their build output.
            var file = asm.GetName().Name + ".dll";
            var binaries = Path.Combine(Globals.ProjectFolder, "Binaries");
            return Directory.Exists(binaries) ? Directory.EnumerateFiles(binaries, file, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        }

        List<MetadataReference> References(List<string> skipped)
        {
            var references = new List<MetadataReference>();
            var latest = AppDomain.CurrentDomain.GetAssemblies().Where(x => !x.IsDynamic).GroupBy(x => x.GetName().Name).Select(Live);
            foreach (var asm in latest)
            {
                var path = LocateAssembly(asm);
                if (path == null || !File.Exists(path)) { skipped.Add(asm.GetName().Name); continue; }
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!referenceCache.TryGetValue(path, out var cached) || cached.stamp != stamp)
                    referenceCache[path] = cached = (stamp, MetadataReference.CreateFromFile(path));
                references.Add(cached.reference);
            }
            return references;
        }

        object Exec(JObject a)
        {
            var code = a.Value<string>("code") ?? throw new Exception("Missing code");
            var usings = DefaultUsings.Concat((a["usings"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>()).Distinct();
            var prefix = string.Concat(usings.Select(u => "using " + u + ";\n")) + "public static class McpSnippet { public static unsafe object Run(FlaxMcp.ExecContext ctx) {\n#line 1\n";
            var skipped = new List<string>();
            var references = References(skipped);
            string[] Compile(string body, MemoryStream output)
            {
                var compilation = CSharpCompilation.Create("McpSnippet_" + Guid.NewGuid().ToString("N"),
                    new[] { CSharpSyntaxTree.ParseText(prefix + body + "\n} }", new CSharpParseOptions(LanguageVersion.Latest)) },
                    references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
                var emitted = compilation.Emit(output);
                return emitted.Success ? null : emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).Select(d => d.ToString()).ToArray();
            }
            // Try the code as an expression whose value is returned, then as statements.
            // Report the expression's errors only when the code looks like one (no trailing ';' or '}').
            using var image = new MemoryStream();
            var trimmed = code.TrimEnd();
            var expressionErrors = Compile("return (object)(" + code + ");", image);
            if (expressionErrors != null)
            {
                image.SetLength(0);
                var statementErrors = Compile(code + "\n;return null;", image);
                var looksExpression = !trimmed.EndsWith(";") && !trimmed.EndsWith("}");
                if (statementErrors != null) throw new Exception("Snippet compilation failed:\n" + string.Join("\n", looksExpression ? expressionErrors : statementErrors));
            }
            image.Position = 0;
            var context = new SnippetLoadContext();
            var ctx = new ExecContext();
            try
            {
                var run = context.LoadFromStream(image).GetType("McpSnippet").GetMethod("Run");
                object result;
                try { result = run.Invoke(null, new object[] { ctx }); }
                catch (TargetInvocationException e) { throw new Exception("Snippet threw " + Describe(e) + (ctx.Logs.Count > 0 ? "\nLogs:\n" + string.Join("\n", ctx.Logs) : ""), e.InnerException); }
                return new { value = Value(result), logs = ctx.Logs, skippedReferences = skipped.Count > 0 ? skipped : null };
            }
            finally { context.Unload(); }
        }
    }
}
#endif
