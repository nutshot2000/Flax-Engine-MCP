using System.IO;
using Flax.Build;
using Flax.Build.NativeCpp;
public class FlaxMcp : GameEditorModule
{
    public override void Init() { base.Init(); BuildNativeCode = false; }
    public override void Setup(BuildOptions options)
    {
        base.Setup(options);
        options.ScriptingAPI.IgnoreMissingDocumentationWarnings = true;
        // flax_exec compiles snippets with the Roslyn build that ships in the engine's Tools folder.
        // File references are copied next to the module, so Flax loads them into the same (collectible) context as the plugin.
        // Loading them into the default context instead fails: a non-collectible assembly cannot depend on a collectible one.
        var tools = Path.Combine(Globals.EngineRoot, "Binaries", "Tools");
        options.ScriptingAPI.FileReferences.Add(Path.Combine(tools, "Microsoft.CodeAnalysis.dll"));
        options.ScriptingAPI.FileReferences.Add(Path.Combine(tools, "Microsoft.CodeAnalysis.CSharp.dll"));
    }
}
