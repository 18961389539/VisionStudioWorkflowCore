using System.Reflection;
using System.Runtime.Loader;

namespace VisionStudio.Scripting;

internal static partial class CSharpScriptRunner
{
    // Cache only PE bytes and entry-point metadata, never assemblies, delegates or submission state.
    private sealed record CompiledScript(byte[] Image, string EntryTypeName, string EntryMethodName)
    {
        public async Task<object?> RunAsync(ScriptGlobals globals)
        {
            var context = new AssemblyLoadContext("VisionStudio.Script", isCollectible: true);
            try
            {
                using var stream = new MemoryStream(Image, writable: false);
                var assembly = context.LoadFromStream(stream);
                var entryType = assembly.GetType(EntryTypeName, throwOnError: true)!;
                var method = entryType.GetMethod(EntryMethodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Compiled script submission entry point was not found.");
                var factory = method.CreateDelegate<Func<object[], Task<object?>>>();
                // Roslyn's submission factory receives globals in slot 0 and writes its instance into slot 1.
                var submissions = new object[2];
                submissions[0] = globals;
                return await factory(submissions).ConfigureAwait(false);
            }
            finally
            {
                // Unload is cooperative: frames/results/exceptions must also become unreachable before GC.
                context.Unload();
            }
        }
    }
}
