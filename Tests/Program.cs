using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Verse;

namespace VVERaid.PerformancePatch
{
    internal static class Program
    {
        private static string[] referenceDirectories;

        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: VVERaid.PerformancePatch.Verify.exe <RimWorld folder> <Workshop 294100 folder>");
                return 1;
            }

            string game = Path.GetFullPath(args[0]);
            string workshop = Path.GetFullPath(args[1]);
            referenceDirectories = new[]
            {
                Path.Combine(game, "RimWorldWin64_Data", "Managed"),
                Path.Combine(workshop, "2009463077", "Current", "Assemblies"),
                Path.Combine(workshop, "3014915404", "1.6", "Assemblies"),
                Path.Combine(workshop, "3720926003", "1.6", "Assemblies")
            };
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            try
            {
                RunChecks(Path.Combine(workshop, "3720926003", "1.6", "Assemblies", "VVERaid.dll"));
                Console.WriteLine("PASS: all offline checks. No game, save or original AI method was started.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            string fileName = new AssemblyName(args.Name).Name + ".dll";
            foreach (string directory in referenceDirectories)
            {
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            return null;
        }

        // Resolve game dependencies only after Main has registered the local assembly resolver.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunChecks(string raidDll)
        {
            Assembly raid = Assembly.LoadFrom(raidDll);
            Type manager = raid.GetType("VVERaid.AI_Manager", true);
            MethodInfo tick = manager.GetMethod("MapComponentTick", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Guid gameModule = typeof(MapPawns).Module.ModuleVersionId;
            Guid raidModule = raid.ManifestModule.ModuleVersionId;
            Check(VehicleScanPatch.IsSupportedBuild(raidModule, gameModule), "Installed builds must be recognized.");
            Check(!VehicleScanPatch.IsSupportedBuild(Guid.Empty, gameModule), "Changed VVE Raid build must be rejected.");
            Check(!VehicleScanPatch.IsSupportedBuild(raidModule, Guid.Empty), "Changed game build must be rejected.");
            Console.WriteLine("PASS: known builds accepted; either unknown build rejected.");

            // Read actual installed IL, not a reimplementation of the original AI method.
            List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(tick);
            var snapshot = original.Select(instruction => new CodeInstruction(instruction)).ToList();
            var rewritten = original.ToList();
            MethodInfo allPawns = typeof(MapPawns).GetProperty("AllPawns").GetGetMethod();
            MethodInfo spawnedPawns = typeof(MapPawns).GetProperty("AllPawnsSpawned").GetGetMethod();
            Check(original.Count(instruction => instruction.Calls(allPawns)) == 2, "Baseline must contain the two verified AllPawns calls.");
            int firstGetter = original.FindIndex(instruction => instruction.Calls(allPawns));
            int otherGetter = original.FindLastIndex(instruction => instruction.Calls(allPawns));
            Check(VehicleScanPatch.TryRewrite(rewritten), "Installed vehicle scan must be patched.");
            Check(rewritten.Count == original.Count, "No instruction may be added or removed.");
            Check(rewritten[firstGetter].Calls(spawnedPawns), "First getter must select spawned pawns.");
            Check(rewritten[otherGetter].Calls(allPawns), "List<Pawn>-dependent hunt path must remain unchanged.");
            Check(rewritten.Count(instruction => instruction.Calls(allPawns)) == 1, "Exactly one AllPawns call must remain.");

            for (int i = 0; i < original.Count; i++)
            {
                CheckSameInstruction(snapshot[i], original[i], "Input instruction mutated at " + i);
                Check(original[i].opcode == rewritten[i].opcode, "Opcode changed at " + i);
                Check(original[i].labels.SequenceEqual(rewritten[i].labels), "Branch labels changed at " + i);
                Check(original[i].blocks.SequenceEqual(rewritten[i].blocks), "Exception blocks changed at " + i);
                if (i != firstGetter)
                    CheckSameInstruction(original[i], rewritten[i], "Unrelated instruction changed at " + i);
            }
            Console.WriteLine("PASS: installed method changes exactly one operand; all " + original.Count + " opcodes, control flow and other operands preserved.");

            var noMatch = new List<CodeInstruction> { new CodeInstruction(OpCodes.Nop), new CodeInstruction(OpCodes.Ret) };
            CheckSkippedUnchanged(noMatch, "Missing pattern");
            CheckSkippedUnchanged(new List<CodeInstruction>(), "Empty method");
            CheckSkippedUnchanged(rewritten.ToList(), "Already patched method");

            var ambiguous = new List<CodeInstruction>
            {
                new CodeInstruction(original[firstGetter]), new CodeInstruction(original[firstGetter + 1]),
                new CodeInstruction(original[firstGetter]), new CodeInstruction(original[firstGetter + 1])
            };
            CheckSkippedUnchanged(ambiguous, "Duplicate vehicle scan");

            MethodInfo pawnOfType = typeof(Enumerable).GetMethod("OfType").MakeGenericMethod(typeof(Pawn));
            var differentType = new List<CodeInstruction>
            {
                new CodeInstruction(original[firstGetter]), new CodeInstruction(OpCodes.Call, pawnOfType)
            };
            CheckSkippedUnchanged(differentType, "Non-vehicle scan");
            var notACall = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldftn, allPawns), new CodeInstruction(original[firstGetter + 1])
            };
            CheckSkippedUnchanged(notACall, "Getter referenced without a call");
            Console.WriteLine("PASS: six absent, ambiguous or incompatible patterns skipped without mutation.");

            var labelMethod = new DynamicMethod("Labels", typeof(void), Type.EmptyTypes);
            var decorated = new List<CodeInstruction>
            {
                new CodeInstruction(original[firstGetter]), new CodeInstruction(original[firstGetter + 1])
            };
            Label label = labelMethod.GetILGenerator().DefineLabel();
            var block = new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock);
            decorated[0].labels.Add(label);
            decorated[0].blocks.Add(block);
            Check(VehicleScanPatch.TryRewrite(decorated), "Labelled getter must be supported.");
            Check(decorated[0].labels.Contains(label) && decorated[0].blocks.Contains(block), "Getter metadata must survive replacement.");
            Console.WriteLine("PASS: replacement preserves branch targets and exception markers.");

            CheckSpawnedScan(rewritten[firstGetter], rewritten[firstGetter + 1]);
        }

        private static void CheckSkippedUnchanged(List<CodeInstruction> instructions, string scenario)
        {
            var before = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
            Check(!VehicleScanPatch.TryRewrite(instructions), scenario + " must be skipped.");
            Check(before.Count == instructions.Count, scenario + " changed the instruction count.");
            for (int i = 0; i < before.Count; i++)
                CheckSameInstruction(before[i], instructions[i], scenario + " modified instruction " + i);
        }

        private static void CheckSpawnedScan(CodeInstruction getter, CodeInstruction ofType)
        {
            // Only the real getter + OfType call are executed. No Unity initialization or AI dispatch.
            var method = new DynamicMethod("SpawnedVehicleScan", typeof(IEnumerable), new[] { typeof(MapPawns) });
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(getter.opcode, (MethodInfo)getter.operand);
            il.Emit(ofType.opcode, (MethodInfo)ofType.operand);
            il.Emit(OpCodes.Ret);
            var scan = (Func<MapPawns, IEnumerable>)method.CreateDelegate(typeof(Func<MapPawns, IEnumerable>));

            // An empty spawn list, with no map/holder graph: a recursive AllPawns lookup cannot work here.
            var mapPawns = (MapPawns)FormatterServices.GetUninitializedObject(typeof(MapPawns));
            var spawned = new List<Pawn>();
            typeof(MapPawns).GetField("pawnsSpawned", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(mapPawns, spawned);
            Check(ReferenceEquals(mapPawns.AllPawnsSpawned, spawned), "Getter must return the existing spawn list.");
            IEnumerator enumerator = scan(mapPawns).GetEnumerator();
            try
            {
                Check(!enumerator.MoveNext(), "An empty map must select no vehicles.");
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
            Console.WriteLine("PASS: rewritten IL compiles and runs against the actual getter without a holder search.");
        }

        private static void CheckSameInstruction(CodeInstruction expected, CodeInstruction actual, string message)
        {
            Check(expected.opcode == actual.opcode && Equals(expected.operand, actual.operand)
                && expected.labels.SequenceEqual(actual.labels) && expected.blocks.SequenceEqual(actual.blocks), message);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}