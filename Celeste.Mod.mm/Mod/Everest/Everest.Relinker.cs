﻿using Celeste.Mod.Helpers;
using Ionic.Zip;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace Celeste.Mod {
    public static partial class Everest {
        /// <summary>
        /// Relink mods to point towards Celeste.exe and FNA / XNA properly and to patch older mods to make them remain compatible.
        /// </summary>
        public static class Relinker {

            /// <summary>
            /// The hasher used by Relinker.
            /// </summary>
            public readonly static HashAlgorithm ChecksumHasher = MD5.Create();

            /// <summary>
            /// The current Celeste.exe's checksum.
            /// </summary>
            public static string GameChecksum => _GameChecksum = (_GameChecksum ?? Everest.GetChecksum(Assembly.GetAssembly(typeof(Relinker)).Location).ToHexadecimalString());
            private static string _GameChecksum;

            /// <summary>
            /// A map shared between all invocations of the relinker which maps certain referenced modules onto others. Can be used with <see cref="MonoModder.RelinkModuleMap"/>.
            /// </summary>
            public static Dictionary<string, ModuleDefinition> SharedRelinkModuleMap {
                get {
                    if (_SharedRelinkModuleMap != null)
                        return _SharedRelinkModuleMap;

                    _SharedRelinkModuleMap = new Dictionary<string, ModuleDefinition>();

                    // Iterate over all mod assemblies in the game folder
                    foreach (string path in Directory.GetFiles(PathGame)) {
                        string name = Path.GetFileName(path);
                        if (name.EndsWith(".mm.dll")) {
                            string modAsmName = Path.GetFileNameWithoutExtension(path);

                            string relinkedPath;
                            if (name == "Celeste.Mod.mm.dll")
                                // Remap Celeste.Mod.mm.dll to the Celeste executable
                                relinkedPath = typeof(Celeste).Assembly.Location;
                            else {
                                Logger.Log(LogLevel.Warn, "relinker", $"Found unexpected mod assembly {name}!");

                                // Remap XYZ.mm.dll to XYZ.dll, if it exists
                                relinkedPath = name.Substring(0, modAsmName.Length - 3);
                                string pathRelinked = Path.Combine(PathGame, relinkedPath + ".dll");    
                                if (File.Exists(pathRelinked))
                                    Logger.Log(LogLevel.Info, "relinker", $"-> remapping to {Path.GetFileName(pathRelinked)}");
                                else {
                                    Logger.Log(LogLevel.Info, "relinker", $"-> couldn't remap, ignoring...");
                                    continue;
                                }
                            }

                            // Read the module and put it into the map
                            _SharedRelinkModuleMap[modAsmName] = ModuleDefinition.ReadModule(relinkedPath, new ReaderParameters(ReadingMode.Immediate));
                        }
                    }
                    return _SharedRelinkModuleMap;
                }
            }
            private static Dictionary<string, ModuleDefinition> _SharedRelinkModuleMap;

            /// <summary>
            /// A map shared between all invocations of the relinker which maps certain referenced types / methods / fields / etc. onto others. Can be used with <see cref="MonoModder.RelinkMap"/>.
            /// </summary>
            public static Dictionary<string, object> SharedRelinkMap {
                get {
                    if (_SharedRelinkMap != null)
                        return _SharedRelinkMap;

                    _SharedRelinkMap = new Dictionary<string, object>();

                    // Fix old mods depending on MonoModExt
                    SharedRelinkMap["MonoMod.Utils.MonoModExt"] = "MonoMod.Utils.Extensions";
                    SharedRelinkMap["System.String MonoMod.Utils.Extensions::GetFindableID(Mono.Cecil.MethodReference,System.String,System.String,System.Boolean,System.Boolean)"] =
                        new RelinkMapEntry("MonoMod.Utils.Extensions", "System.String GetID(Mono.Cecil.MethodReference,System.String,System.String,System.Boolean,System.Boolean)");
                    SharedRelinkMap["System.String MonoMod.Utils.Extensions::GetFindableID(System.Reflection.MethodBase,System.String,System.String,System.Boolean,System.Boolean,System.Boolean)"] =
                        new RelinkMapEntry("MonoMod.Utils.Extensions", "System.String GetID(System.Reflection.MethodBase,System.String,System.String,System.Boolean,System.Boolean,System.Boolean)");
                    SharedRelinkMap["Mono.Cecil.ModuleDefinition MonoMod.Utils.Extensions::ReadModule(System.String,Mono.Cecil.ReaderParameters)"] =
                        new RelinkMapEntry("Mono.Cecil.ModuleDefinition", "Mono.Cecil.ModuleDefinition ReadModule(System.String,Mono.Cecil.ReaderParameters)");

                    return _SharedRelinkMap;
                }
            }
            private static Dictionary<string, object> _SharedRelinkMap;

            /// <summary>
            /// Relink a mod .dll, then load it.
            /// </summary>
            /// <param name="meta">The mod metadata, used for caching, among other things.</param>
            /// <param name="stream">The stream to read the .dll from.</param>
            /// <param name="depResolver">An optional dependency resolver.</param>
            /// <param name="checksumsExtra">Any optional checksums</param>
            /// <param name="prePatch">An optional step executed before patching, but after MonoMod has loaded the input assembly.</param>
            /// <returns>The loaded, relinked assembly.</returns>
            [Obsolete("Use the variant with an explicit assembly name instead.")]
            public static Assembly GetRelinkedAssembly(EverestModuleMetadata meta, Stream stream,
                MissingDependencyResolver depResolver = null, string[] checksumsExtra = null, Action<MonoModder> prePatch = null)
                => GetRelinkedAssembly(meta, Path.GetFileNameWithoutExtension(meta.DLL), stream, depResolver, checksumsExtra, prePatch);

            /// <summary>
            /// Relink a .dll to point towards Celeste.exe and FNA / XNA properly at runtime, then load it.
            /// </summary>
            /// <param name="meta">The mod metadata, used for caching, among other things.</param>
            /// <param name="asmname"></param>
            /// <param name="stream">The stream to read the .dll from.</param>
            /// <param name="depResolver">An optional dependency resolver.</param>
            /// <param name="checksumsExtra">Any optional checksums</param>
            /// <param name="prePatch">An optional step executed before patching, but after MonoMod has loaded the input assembly.</param>
            /// <returns>The loaded, relinked assembly.</returns>
            public static Assembly GetRelinkedAssembly(EverestModuleMetadata meta, string asmname, Stream stream,
                MissingDependencyResolver depResolver = null, string[] checksumsExtra = null, Action<MonoModder> prePatch = null) {
                // Ensure the stream is seekable
                if (!stream.CanSeek) {
                    MemoryStream ms = new MemoryStream();
                    stream.CopyTo(ms);
                    stream.Dispose();
                    stream = ms;
                    stream.Seek(0, SeekOrigin.Begin);
                }

                // Determine cache paths
                string cachePath = GetCachedPath(meta, asmname);
                string cacheChecksumPath = Path.ChangeExtension(cachePath, ".sum");

                Assembly asm = null;

                // Try to load the assembly from the cache
                if (TryLoadCachedAssembly(meta, asmname, cachePath, cacheChecksumPath, stream, checksumsExtra, out string[] checksums) is not Assembly cacheAsm) {
                    // Delete cached files
                    File.Delete(cachePath);
                    File.Delete(cacheChecksumPath);

                    try {
                        // Relink the assembly                
                        if (RelinkAssembly(meta, asmname, cachePath, stream, depResolver, prePatch, out string tmpOutPath) is not Assembly relinkedAsm)
                            return null;
                        else
                            asm = relinkedAsm;

                        // Write the checksums for the cached assembly to be loaded in the future
                        // Skip this step if the relinker had to fall back to using a temporary output file
                        if (tmpOutPath == null)
                            File.WriteAllLines(cacheChecksumPath, checksums);
                    } catch (Exception e) {
                        Logger.Log(LogLevel.Warn, "relinker", $"Failed relinking {meta} - {asmname}");
                        e.LogDetailed();
                        return null;
                    }
                } else
                    asm = cacheAsm;

                // Log the assembly load and dependencies
                Logger.Log(LogLevel.Verbose, "relinker", $"Loading assembly for {meta} - {asmname} - {asm.FullName}");

                HashSet<string> loadedAsmNames = AppDomain.CurrentDomain.GetAssemblies().Select(asm => asm.GetName().Name).ToHashSet();
                foreach (AssemblyName asmRef in asm.GetReferencedAssemblies()) {
                    if (loadedAsmNames.Contains(asmRef.Name))
                        Logger.Log(LogLevel.Verbose, "relinker", $"dep. {asm.FullName} -> {asmRef.FullName} found");
                    else
                        Logger.Log(LogLevel.Warn, "relinker", $"dep. {asm.FullName} -> {asmRef.FullName} NOT FOUND");
                }

                return asm;
            }

            private static Assembly TryLoadCachedAssembly(EverestModuleMetadata meta, string asmName, string cachePath, string cacheChecksumsPath, Stream stream, string[] extraChecksums, out string[] curChecksums) {
                // Calculate checksums
                List<string> checksums = new List<string>();
                checksums.Add(GameChecksum);
                checksums.Add(Everest.GetChecksum(ref stream).ToHexadecimalString());

                if (extraChecksums != null)
                    checksums.AddRange(extraChecksums);

                curChecksums = checksums.ToArray();

                // Check if the cached assembly + its checksums exist on disk, and if the checksums match
                if (!File.Exists(cachePath) || !File.Exists(cacheChecksumsPath))
                    return null;

                if (!ChecksumsEqual(curChecksums, File.ReadAllLines(cacheChecksumsPath)))
                    return null;
                
                Logger.Log(LogLevel.Verbose, "relinker", $"Loading cached assembly for {meta} - {asmName}");

                // Try to load the assembly and the module definition
                try {
                    return meta.AssemblyContext.LoadRelinkedAssembly(cachePath);
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, "relinker", $"Failed loading cached assembly for {meta} - {asmName}");
                    e.LogDetailed();
                    return null;
                }
            }

            private static Assembly RelinkAssembly(EverestModuleMetadata meta, string asmname, string outPath, Stream stream, MissingDependencyResolver depResolver, Action<MonoModder> prePatch, out string tmpOutPath) {
                tmpOutPath = null;

                // Ensure the runtime rules module is loaded
                ModuleDefinition runtimeRulesMod = LoadRuntimeRulesModule();

                // Setup the MonoModder
                // Don't dispose it, as it shares a ton of resources
                MonoModder modder = new MonoModder() {
                    CleanupEnabled = false,
                    Input = stream,
                    OutputPath = outPath,

                    RelinkModuleMap = SharedRelinkModuleMap,
                    RelinkMap = SharedRelinkMap,

                    AssemblyResolver = meta.AssemblyContext,
                    MissingDependencyResolver = depResolver
                };
                try {
                    InitMMFlags(modder);

                    // Read and setup debug symbols (if they exist)
                    modder.ReaderParameters.ReadSymbols = true;
                    modder.ReaderParameters.SymbolStream = OpenStream(meta, out _, Path.ChangeExtension(meta.DLL, ".pdb"), Path.ChangeExtension(meta.DLL, ".mdb"));
                    try {
                        modder.Read();
                    } catch {
                        modder.ReaderParameters.ReadSymbols = false;
                        modder.ReaderParameters.SymbolStream?.Dispose();
                        modder.ReaderParameters.SymbolStream = null;

                        // Try again
                        stream.Seek(0, SeekOrigin.Begin);
                        modder.Read();
                    }

                    // Map assembly dependencies
                    modder.MapDependencies();
                    modder.MapDependencies(runtimeRulesMod);

                    // Patch the assembly
                    prePatch?.Invoke(modder);

                    TypeDefinition runtimeRulesType = runtimeRulesMod.GetType("MonoMod.MonoModRules");
                    modder.ParseRules(runtimeRulesMod);
                    if (runtimeRulesType != null)
                        runtimeRulesMod.Types.Add(runtimeRulesType); // MonoMod removes the rules type from the assembly

                    modder.ParseRules(modder.Module);

                    modder.AutoPatch();

                    NETCoreifier.Coreifier.ConvertToNetCore(modder, sharedDeps: true);

                    // Write patched assembly and debug symbols back to disk (always as portable PDBs though)
                    // Fall back to a temporary output path if the given one is unavailable for some reason
                    bool temporaryASM = false;
                    RetryWrite:
                    try {
                        // Try to write with symbols
                        modder.WriterParameters.WriteSymbols = true;
                        modder.WriterParameters.SymbolWriterProvider = new PortablePdbWriterProvider();
                        modder.Write();
                    } catch {
                        try {
                            // Try to write without symbols
                            modder.WriterParameters.SymbolWriterProvider = null;
                            modder.WriterParameters.WriteSymbols = false;
                            modder.Write();
                        } catch (Exception e) when (!temporaryASM) {
                            Logger.Log(LogLevel.Warn, "relinker", "Couldn't write to intended output path - falling back to temporary file...");
                            e.LogDetailed();

                            // Try writing to a temporary file
                            temporaryASM = true;

                            long stamp = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                            tmpOutPath = Path.Combine(Path.GetTempPath(), $"Everest.Relinked.{Path.GetFileNameWithoutExtension(outPath)}.{stamp}.dll");

                            modder.Module.Name += "." + stamp;
                            modder.Module.Assembly.Name.Name += "." + stamp;
                            modder.OutputPath = tmpOutPath;
                            modder.WriterParameters.WriteSymbols = true;

                            goto RetryWrite;
                        }
                    }
                } finally {
                    modder.Module?.Dispose();
                }

                // Try to load the assembly and the module definition
                try {
                    return meta.AssemblyContext.LoadRelinkedAssembly(outPath);
                } catch (Exception e) {
                    Logger.Log(LogLevel.Warn, "relinker", $"Failed loading relinked assembly {meta} - {asmname}");
                    e.LogDetailed();
                    return null;
                }
            }

            private static ModuleDefinition _RuntimeRulesModule;
            private static ModuleDefinition LoadRuntimeRulesModule() {
                if (_RuntimeRulesModule != null)
                    return _RuntimeRulesModule;

                // Find our rules .Mod.mm.dll
                string rulesPath = Path.Combine(
                    Path.GetDirectoryName(typeof(Celeste).Assembly.Location),
                    Path.GetFileNameWithoutExtension(typeof(Celeste).Assembly.Location) + ".Mod.mm.dll"
                );

                if (!File.Exists(rulesPath)) {
                    // Fallback if someone renamed Celeste.exe
                    rulesPath = Path.Combine(
                        Path.GetDirectoryName(typeof(Celeste).Assembly.Location),
                        "Celeste.Mod.mm.dll"
                    );
                }

                if (!File.Exists(rulesPath))
                    throw new InvalidOperationException($"Couldn't find runtime rules .Mod.mm.dll!");

                // Load the module
                return _RuntimeRulesModule = ModuleDefinition.ReadModule(rulesPath, new ReaderParameters(ReadingMode.Immediate));
            }

            private static Stream OpenStream(EverestModuleMetadata meta, out string result, params string[] names) {
                if (!string.IsNullOrEmpty(meta.PathArchive)) {
                    using (ZipFile zip = new ZipFile(meta.PathArchive)) {
                        foreach (ZipEntry entry in zip.Entries) {
                            if (!names.Contains(entry.FileName))
                                continue;
                            result = entry.FileName;
                            return entry.ExtractStream();
                        }
                    }
                    result = null;
                    return null;
                }

                if (!string.IsNullOrEmpty(meta.PathDirectory)) {
                    foreach (string name in names) {
                        string path = name;
                        if (!File.Exists(path))
                            path = Path.Combine(meta.PathDirectory, name);
                        if (!File.Exists(path))
                            continue;
                        result = path;
                        return File.OpenRead(path);
                    }
                }

                result = null;
                return null;
            }

            /// <summary>
            /// Get the cached path of a given mod's relinked .dll
            /// </summary>
            /// <param name="meta">The mod metadata.</param>
            /// <returns>The full path to the cached relinked .dll</returns>
            [Obsolete("Use the variant with an explicit assembly name instead.")]
            public static string GetCachedPath(EverestModuleMetadata meta)
                => GetCachedPath(meta, Path.GetFileNameWithoutExtension(meta.DLL));

            /// <summary>
            /// Get the cached path of a given mod's relinked .dll
            /// </summary>
            /// <param name="meta">The mod metadata.</param>
            /// <param name="asmname"></param>
            /// <returns>The full path to the cached relinked .dll</returns>
            public static string GetCachedPath(EverestModuleMetadata meta, string asmname)
                => Path.Combine(Loader.PathCache, meta.Name + "." + asmname + ".dll");

            /// <summary>
            /// Get the checksum for a given mod's .dll or the containing .zip
            /// </summary>
            /// <param name="meta">The mod metadata.</param>
            /// <returns>A checksum.</returns>
            [Obsolete("Use meta.Hash instead.")]
            public static string GetChecksum(EverestModuleMetadata meta) {
                string path = meta.PathArchive;
                if (string.IsNullOrEmpty(path))
                    path = meta.DLL;
                return GetChecksum(path);
            }
            /// <summary>
            /// Get the checksum for a given file.
            /// </summary>
            /// <param name="path">The file path.</param>
            /// <returns>A checksum.</returns>
            [Obsolete("Use Everest.GetChecksum instead.")]
            public static string GetChecksum(string path) {
                using (FileStream fs = File.OpenRead(path))
                    return ChecksumHasher.ComputeHash(fs).ToHexadecimalString();
            }

            /// <summary>
            /// Determine if both checksum collections are equal.
            /// </summary>
            /// <param name="a">The first checksum array.</param>
            /// <param name="b">The second checksum array.</param>
            /// <returns>True if the contents of both arrays match, false otherwise.</returns>
            public static bool ChecksumsEqual(string[] a, string[] b) {
                if (a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i].Trim() != b[i].Trim())
                        return false;
                return true;
            }

            [PatchInitMMFlags]
            private static void InitMMFlags(MonoModder modder) {
                // This method is automatically filled via MonoModRules to set the same flags used by Everest itself
            }
            private static void SetMMFlag(MonoModder modder, string key, bool value) => modder.SharedData[key] = value;

        }
    }
}
