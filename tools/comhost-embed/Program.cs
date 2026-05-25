// Workaround for .NET 10 SDK (verified 10.0.102) CreateComHostTask bug:
// MSBuild target _CreateComHost runs to completion but the resulting
// comhost.dll never actually has the .clsidmap resource embedded.
// CoCreateInstance then fails with TYPE_E_CANTLOADLIBRARY (0x80029C4A),
// erwin's Add-In Manager hides our menu entry on validation, and the
// addin appears "missing" even with all registry chains correct.
//
// This tool calls Microsoft.NET.HostModel.ComHost.ComHost.Create
// directly (the same internal API the SDK target uses), bypassing the
// silent skip in the build pipeline. Empirically this DOES embed the
// clsidmap correctly while the MSBuild-driven path doesn't (verified
// 2026-05-26).
//
// Microsoft.NET.HostModel.dll is NOT distributed on NuGet, only inside
// the .NET SDK install. To avoid copying it next to our exe (repo
// bloat + version drift), we reference it with Private=false in the
// csproj and register an AssemblyResolve handler that finds it under
// %ProgramFiles%\dotnet\sdk\<latest>\Microsoft.NET.HostModel.dll at
// runtime. The handler MUST be in place before the first method that
// references HostModel types is JIT-compiled, so Main() defers all
// HostModel work to RunEmbed() via NoInlining.
//
// Usage:
//   comhost-embed <template-comhost> <output-comhost> <clsidmap-json>
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

class Program {
    static int Main(string[] args) {
        // Step 1: register the resolver BEFORE any reference to HostModel
        // types triggers JIT-time assembly lookup.
        AssemblyLoadContext.Default.Resolving += ResolveHostModel;

        if (args.Length < 3) {
            Console.Error.WriteLine("Usage: comhost-embed <template-comhost> <output> <clsidmap-json>");
            return 1;
        }

        // Step 2: now safe to call into HostModel - this method is JIT'd
        // lazily, after the resolver is in place.
        return RunEmbed(args[0], args[1], args[2]);
    }

    private static Assembly ResolveHostModel(AssemblyLoadContext ctx, AssemblyName name) {
        if (name.Name != "Microsoft.NET.HostModel") return null;
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot)) dotnetRoot = @"C:\Program Files\dotnet";
        var sdkDir = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkDir)) return null;
        string newest = null;
        foreach (var dir in Directory.EnumerateDirectories(sdkDir)) {
            var dll = Path.Combine(dir, "Microsoft.NET.HostModel.dll");
            if (!File.Exists(dll)) continue;
            if (newest == null ||
                string.Compare(Path.GetFileName(dir),
                               Path.GetFileName(Path.GetDirectoryName(newest)),
                               StringComparison.Ordinal) > 0) {
                newest = dll;
            }
        }
        return newest != null ? ctx.LoadFromAssemblyPath(newest) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunEmbed(string template, string output, string clsidmap) {
        Console.WriteLine($"Template: {template}");
        Console.WriteLine($"Output:   {output}");
        Console.WriteLine($"ClsidMap: {clsidmap}");

        foreach (var p in new[] { template, clsidmap }) {
            if (!File.Exists(p)) {
                Console.Error.WriteLine($"MISSING: {p}");
                return 2;
            }
        }

        try {
            // typeLibraries is for embedded .tlb files; we have none -> empty dict.
            Microsoft.NET.HostModel.ComHost.ComHost.Create(
                template, output, clsidmap,
                new System.Collections.Generic.Dictionary<int, string>());
            var fi = new FileInfo(output);
            Console.WriteLine($"OK. Output size={fi.Length} lastWrite={fi.LastWriteTime}");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
            return 3;
        }
    }
}
