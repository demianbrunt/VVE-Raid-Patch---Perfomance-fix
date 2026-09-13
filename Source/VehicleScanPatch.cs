using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

[assembly: InternalsVisibleTo("VVERaid.PerformancePatch.Verify")]

namespace VVERaid.PerformancePatch
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string LogPrefix = "[VVE Raid Minimal Performance Patch] ";

        static Bootstrap()
        {
            try
            {
                Type manager = AccessTools.TypeByName("VVERaid.AI_Manager");
                MethodInfo tick = manager == null ? null :
                    AccessTools.DeclaredMethod(manager, "MapComponentTick", Type.EmptyTypes);

                if (tick == null || !VehicleScanPatch.IsSupportedBuild(
                    tick.Module.ModuleVersionId, typeof(MapPawns).Module.ModuleVersionId))
                {
                    Log.Warning(LogPrefix + "Unrecognized VVE Raid or RimWorld build. Patch skipped; original code unchanged.");
                    return;
                }

                var transpiler = new HarmonyMethod(typeof(VehicleScanPatch), nameof(VehicleScanPatch.Transpiler))
                {
                    priority = Priority.Last
                };
                new Harmony("local.vveraid.minimalperformance").Patch(tick, transpiler: transpiler);
            }
            catch (Exception exception)
            {
                Log.Warning(LogPrefix + "Could not apply patch: " + exception.GetBaseException().Message);
            }
        }
    }

    internal static class VehicleScanPatch
    {
        // Pin both builds: a future update may change the meaning of the selection or getters.
        internal static bool IsSupportedBuild(Guid raidModule, Guid gameModule)
        {
            return raidModule == new Guid("1d5843c4-4e34-4284-9bc7-c85fcf7c7429")
                && gameModule == new Guid("61e41735-6189-4da4-9d21-0260257b5097");
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            if (TryRewrite(codes))
                Log.Message(Bootstrap.LogPrefix + "Applied: spawned-pawn vehicle scan only; AI and timers unchanged.");
            else
                Log.Warning(Bootstrap.LogPrefix + "Expected exactly one vehicle scan. Patch skipped; instructions unchanged.");
            return codes;
        }

        internal static bool TryRewrite(List<CodeInstruction> codes)
        {
            MethodInfo allPawns = AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawns));
            MethodInfo spawnedPawns = AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawnsSpawned));
            if (allPawns == null || spawnedPawns == null
                || !typeof(IEnumerable).IsAssignableFrom(spawnedPawns.ReturnType))
                return false;

            int match = -1;
            for (int i = 0; i < codes.Count - 1; i++)
            {
                MethodInfo consumer = codes[i + 1].operand as MethodInfo;
                if (!codes[i].Calls(allPawns) || codes[i + 1].opcode != OpCodes.Call
                    || consumer == null || consumer.DeclaringType != typeof(Enumerable)
                    || consumer.Name != nameof(Enumerable.OfType) || !consumer.IsGenericMethod)
                    continue;

                Type elementType = consumer.GetGenericArguments()[0];
                if (elementType.FullName != "Vehicles.VehiclePawn" || elementType.Assembly.GetName().Name != "Vehicles")
                    continue;

                // Do not partially patch ambiguous/modified code, or the separate List<Pawn> consumer.
                if (match >= 0)
                    return false;
                match = i;
            }

            if (match < 0)
                return false;

            // Copy constructor preserves labels and exception blocks; Clone() would discard them.
            codes[match] = new CodeInstruction(codes[match]) { operand = spawnedPawns };
            return true;
        }
    }
}