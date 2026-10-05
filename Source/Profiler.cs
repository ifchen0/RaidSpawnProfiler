using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using RimWorld.Planet;
using Verse.AI.Group;

namespace RaidSpawnProfiler
{
    /// <summary>
    /// Diagnostic: times the stages of IncidentWorker.TryExecute (pawn generation, spawning, letters, lords)
    /// and the first few game ticks after it, then logs a breakdown when the incident took longer than a threshold.
    /// Each timed method is measured inclusively (with every other mod's prefixes/postfixes), outermost call only.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Profiler
    {
        private const double LogThresholdMs = 100;
        private const int TicksToWatchAfter = 5;

        private class Entry
        {
            public long Ticks;
            public long SelfTicks;
            public long MaxTicks;
            public int Calls;
        }

        private static readonly Dictionary<MethodBase, Entry> incidentEntries = new Dictionary<MethodBase, Entry>();
        private static readonly Dictionary<MethodBase, Entry> genEntries = new Dictionary<MethodBase, Entry>();
        private static Dictionary<MethodBase, Entry> entries = incidentEntries;
        private static MethodBase genStep;
        private static int genSteps, genGcSteps, gcAtStepStart;
        private static double genMs, genGcMs, genMaxMs;
        private static readonly Dictionary<MethodBase, int> depth = new Dictionary<MethodBase, int>();
        private static readonly List<long> childTicks = new List<long>();
        private static readonly Dictionary<MethodBase, string> names = new Dictionary<MethodBase, string>();
        private static MethodBase tryExecute;
        private static MethodBase doSingleTick;
        private static MethodBase allPawnsUnspawned;
        private static readonly Dictionary<string, int> unspawnedCallers = new Dictionary<string, int>();
        private static bool active;
        private static string incidentName;
        private static int pawnsBefore;
        private static int ticksLeftToWatch;
        private static readonly List<string> tickLines = new List<string>();
        private static string pendingReport;

        static Profiler()
        {
            var harmony = new Harmony("ifchen0.raidspawnprofiler");
            var prefix = new HarmonyMethod(typeof(Profiler), nameof(Prefix)) { priority = Priority.First };
            var finalizer = new HarmonyMethod(typeof(Profiler), nameof(Finalizer)) { priority = Priority.Last };

            tryExecute = AccessTools.Method(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute));
            doSingleTick = AccessTools.Method(typeof(TickManager), "DoSingleTick");

            var targets = new List<MethodBase> { tryExecute, doSingleTick };
            Type pendingGeneration = AccessTools.TypeByName("DeferredRaidGeneration.PendingGeneration");
            genStep = pendingGeneration == null ? null : AccessTools.Method(pendingGeneration, "GenerateNext");
            if (genStep != null)
            {
                targets.Add(genStep);
                MethodInfo finish = AccessTools.Method(AccessTools.TypeByName("DeferredRaidGeneration.DeferredRaids"), "Finish");
                if (finish != null)
                    harmony.Patch(finish, prefix: new HarmonyMethod(typeof(Profiler), nameof(ReportGeneration)));
            }
            void Add(Type type, string method, Type[] args = null)
            {
                MethodBase m = null;
                try { m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args); }
                catch (Exception e) { Log.Warning($"[RaidSpawnProfiler] Lookup failed for {type.Name}.{method}: {e.Message}"); }
                if (m == null)
                    Log.Warning($"[RaidSpawnProfiler] Method not found: {type.Name}.{method}");
                else
                    targets.Add(m);
            }
            void AddOverrides(Type baseType, string method)
            {
                foreach (Type t in GenTypes.AllSubclasses(baseType).Concat(new[] { baseType }))
                {
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (m.Name == method && !m.IsAbstract)
                            targets.Add(m);
                }
            }

            Add(typeof(PawnGroupKindWorker), nameof(PawnGroupKindWorker.GeneratePawns),
                new[] { typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(bool) });
            Add(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) });
            foreach (string m in new[] { "GenerateOrRedressPawnInternal", "GenerateNewPawnInternal", "TryGenerateNewPawnInternal",
                         "GenerateGearFor", "GenerateInitialHediffs", "GenerateTraits", "GenerateSkills", "GenerateGenes",
                         "GeneratePawnRelations", "RedressPawn", "GenerateBodyType", "GenerateRandomAge",
                         "IsValidCandidateToRedress", "GetValidCandidatesToRedress", "PurchasePermits" })
                Add(typeof(PawnGenerator), m);
            foreach (string m in new[] { "GiveShuffledBioTo", "FillBackstorySlotShuffled", "TryGiveSolidBioTo", "GeneratePawnName",
                         "GenerateFullPawnName", "NameResolvedFrom", "GeneratePawnName_Shuffled", "TryGetRandomUnusedSolidName",
                         "GetBackstoryCategoryFiltersFor" })
                Add(typeof(PawnBioAndNameGenerator), m);
            Add(typeof(PawnApparelGenerator), nameof(PawnApparelGenerator.GenerateStartingApparelFor));
            Add(typeof(PawnWeaponGenerator), nameof(PawnWeaponGenerator.TryGenerateWeaponFor));
            Add(typeof(PawnInventoryGenerator), nameof(PawnInventoryGenerator.GenerateInventoryFor));
            Add(typeof(PawnBioAndNameGenerator), nameof(PawnBioAndNameGenerator.GiveAppropriateBioAndNameTo));
            Add(typeof(Ideo), nameof(Ideo.Notify_MemberGenerated));
            Add(typeof(Scenario), nameof(Scenario.Notify_PawnGenerated));
            Add(typeof(PawnRelationUtility), nameof(PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter));
            Add(typeof(LordMaker), nameof(LordMaker.MakeNewLord));
            Add(typeof(ThingSetMaker), nameof(ThingSetMaker.Generate), new[] { typeof(ThingSetMakerParams) });
            Add(typeof(Pawn), nameof(Pawn.SpawnSetup));
            Add(typeof(WorldPawns), nameof(WorldPawns.RemovePawn));
            targets.Add(AccessTools.PropertyGetter(typeof(PawnsFinder), nameof(PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)));
            targets.Add(AccessTools.PropertyGetter(typeof(PawnsFinder), nameof(PawnsFinder.Temporary)));
            allPawnsUnspawned = AccessTools.PropertyGetter(typeof(MapPawns), nameof(MapPawns.AllPawnsUnspawned));
            targets.Add(allPawnsUnspawned);
            Add(typeof(Verse.AI.Pawn_JobTracker), "TryFindAndStartJob");
            Add(typeof(Verse.AI.Pawn_JobTracker), nameof(Verse.AI.Pawn_JobTracker.EndCurrentJob));
            Add(typeof(MapPawns), nameof(MapPawns.RegisterPawn));
            Add(typeof(PawnDiedOrDownedThoughtsUtility), nameof(PawnDiedOrDownedThoughtsUtility.RemoveDiedThoughts));
            targets.Add(AccessTools.PropertyGetter(typeof(WorldPawns), nameof(WorldPawns.AllPawnsAlive)));
            Add(typeof(NameUseChecker), nameof(NameUseChecker.NameWordIsUsed));
            Add(typeof(NameUseChecker), nameof(NameUseChecker.NameSingleIsUsed));
            Add(typeof(MapGenerator), nameof(MapGenerator.GenerateMap));
            foreach (MethodInfo m in typeof(GetOrGenerateMapUtility).GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.Name == nameof(GetOrGenerateMapUtility.GetOrGenerateMap)))
                targets.Add(m);
            MethodInfo spawn = typeof(GenSpawn).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == nameof(GenSpawn.Spawn) && m.GetParameters()[0].ParameterType == typeof(Thing))
                .OrderByDescending(m => m.GetParameters().Length).FirstOrDefault();
            if (spawn != null)
                targets.Add(spawn);
            foreach (MethodInfo m in typeof(LetterStack).GetMethods().Where(m => m.Name == nameof(LetterStack.ReceiveLetter)
                         && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Letter)))
                targets.Add(m);
            AddOverrides(typeof(PawnsArrivalModeWorker), nameof(PawnsArrivalModeWorker.Arrive));
            AddOverrides(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.SpawnThreats));
            AddOverrides(typeof(RaidStrategyWorker), nameof(RaidStrategyWorker.MakeLords));
            AddOverrides(typeof(PawnGroupKindWorker), "GeneratePawns");

            harmony.Patch(tryExecute, prefix: new HarmonyMethod(typeof(Profiler), nameof(NamePrefix)) { priority = Priority.First + 1 });
            int ok = 0;
            foreach (MethodBase m in targets.Where(m => m != null).Distinct())
            {
                try
                {
                    harmony.Patch(m, prefix: prefix, finalizer: finalizer);
                    names[m] = m.DeclaringType.Name + "." + m.Name;
                    ok++;
                }
                catch (Exception e)
                {
                    Log.Warning($"[RaidSpawnProfiler] Could not patch {m.DeclaringType?.Name}.{m.Name}: {e.Message}");
                }
            }
            MethodInfo setAsBuilder = AccessTools.Method(typeof(LordToil_Siege), "SetAsBuilder");
            if (setAsBuilder != null)
                harmony.Patch(setAsBuilder, prefix: new HarmonyMethod(typeof(Profiler), nameof(SetAsBuilderCheck)));
            LogForeignPatches(harmony.Id, targets);
            Log.Message($"[RaidSpawnProfiler] Timing {ok} methods. Breakdown is logged after any incident over {LogThresholdMs} ms.");
        }

        public static void NamePrefix(IncidentWorker __instance)
        {
            if (!active)
                incidentName = __instance?.def?.defName ?? "?";
        }

        public static void Prefix(MethodBase __originalMethod, out long __state)
        {
            __state = -1;
            if (__originalMethod == doSingleTick)
            {
                if (ticksLeftToWatch > 0)
                    __state = Stopwatch.GetTimestamp();
                return;
            }
            if (__originalMethod == genStep)
            {
                if (active)
                    return;
                active = true;
                entries = genEntries;
                depth.Clear();
                childTicks.Clear();
                gcAtStepStart = GC.CollectionCount(0);
            }
            else if (__originalMethod == tryExecute)
            {
                if (!active)
                {
                    active = true;
                    entries = incidentEntries;
                    entries.Clear();
                    depth.Clear();
                    childTicks.Clear();
                    unspawnedCallers.Clear();
                    pawnsBefore = Find.WorldPawns?.AllPawnsAliveOrDead?.Count ?? -1;
                }
            }
            else if (!active)
                return;

            depth.TryGetValue(__originalMethod, out int d);
            depth[__originalMethod] = d + 1;
            if (d == 0)
            {
                childTicks.Add(0);
                __state = Stopwatch.GetTimestamp();
            }
        }

        public static Exception Finalizer(Exception __exception, MethodBase __originalMethod, long __state)
        {
            try
            {
                if (__originalMethod == doSingleTick)
                {
                    if (__state >= 0)
                        RecordTick(__state);
                    return __exception;
                }
                if (!active)
                    return __exception;
                depth.TryGetValue(__originalMethod, out int d);
                depth[__originalMethod] = Math.Max(0, d - 1);
                if (__state < 0)
                    return __exception;

                if (__originalMethod == allPawnsUnspawned)
                    RecordCaller();
                long elapsed = Stopwatch.GetTimestamp() - __state;
                long children = 0;
                if (childTicks.Count > 0)
                {
                    children = childTicks[childTicks.Count - 1];
                    childTicks.RemoveAt(childTicks.Count - 1);
                }
                if (childTicks.Count > 0)
                    childTicks[childTicks.Count - 1] += elapsed;
                if (!entries.TryGetValue(__originalMethod, out Entry e))
                    entries[__originalMethod] = e = new Entry();
                e.Ticks += elapsed;
                e.SelfTicks += elapsed - children;
                e.Calls++;
                if (elapsed > e.MaxTicks)
                    e.MaxTicks = elapsed;

                if (__originalMethod == genStep)
                {
                    active = false;
                    double stepMs = Ms(elapsed);
                    genSteps++;
                    genMs += stepMs;
                    genMaxMs = Math.Max(genMaxMs, stepMs);
                    if (GC.CollectionCount(0) != gcAtStepStart)
                    {
                        genGcSteps++;
                        genGcMs += stepMs;
                    }
                }
                else if (__originalMethod == tryExecute)
                {
                    active = false;
                    double ms = Ms(elapsed);
                    if (ms >= LogThresholdMs)
                    {
                        pendingReport = BuildReport(ms);
                        tickLines.Clear();
                        ticksLeftToWatch = TicksToWatchAfter;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[RaidSpawnProfiler] " + ex, 0x5A1D);
            }
            return __exception;
        }

        private static readonly HashSet<int> reportedBuilders = new HashSet<int>();

        /// <summary>Diagnostic: logs each siege builder that has no skill tracker (the cause of a SetAsBuilder NRE).</summary>
        public static void SetAsBuilderCheck(Pawn p, LordToil_Siege __instance)
        {
            if (p == null || p.skills != null || !reportedBuilders.Add(p.thingIDNumber))
                return;
            Type deferred = AccessTools.TypeByName("DeferredRaidGeneration.DeferredRaids");
            var ids = deferred == null ? null : AccessTools.Field(deferred, "GeneratedIds")?.GetValue(null) as HashSet<int>;
            Log.Warning($"[RaidSpawnProfiler] Siege builder without skills: {p} id={p.ThingID} kind={p.kindDef?.defName} race={p.def.defName} " +
                        $"humanlike={p.RaceProps.Humanlike} mech={p.RaceProps.IsMechanoid} faction={p.Faction} spawned={p.Spawned} dead={p.Dead} " +
                        $"discarded={p.Discarded} holder={p.ParentHolder} duty={p.mindState?.duty?.def?.defName} " +
                        $"preGenerated={(ids == null ? "?" : ids.Contains(p.thingIDNumber).ToString())} lordPawns={__instance.lord?.ownedPawns.Count}");
        }

        public static void ReportGeneration()
        {
            if (genSteps == 0)
                return;
            var sb = new StringBuilder();
            sb.AppendLine($"[RaidSpawnProfiler] Deferred generation: {genSteps} steps, avg {genMs / genSteps:F1} ms, max {genMaxMs:F1} ms. " +
                          $"Steps with a GC: {genGcSteps}" + (genGcSteps > 0 ? $" (avg {genGcMs / genGcSteps:F1} ms)" : "") +
                          (genSteps > genGcSteps ? $", without: avg {(genMs - genGcMs) / (genSteps - genGcSteps):F1} ms." : "."));
            sb.AppendLine("  per step ms (self) | inclusive ms | calls/step | method");
            foreach (var kv in genEntries.OrderByDescending(kv => kv.Value.SelfTicks).Take(30))
            {
                Entry e = kv.Value;
                sb.AppendLine($"  {Ms(e.SelfTicks) / genSteps,8:F2} | {Ms(e.Ticks) / genSteps,8:F2} | {(double)e.Calls / genSteps,6:F1} | {names[kv.Key]}");
            }
            Log.Message(sb.ToString().TrimEnd());
            genEntries.Clear();
            genSteps = genGcSteps = 0;
            genMs = genGcMs = genMaxMs = 0;
        }

        private static void LogForeignPatches(string ownId, List<MethodBase> targets)
        {
            var sb = new StringBuilder("[RaidSpawnProfiler] Other mods' patches on timed methods:");
            foreach (MethodBase m in targets.Where(m => m != null).Distinct())
            {
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null)
                    continue;
                var owners = info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                    .Where(p => p.owner != ownId).Select(p => p.owner + ":" + p.PatchMethod.DeclaringType?.Name + "." + p.PatchMethod.Name).Distinct().ToList();
                if (owners.Count > 0)
                    sb.Append($"\n  {m.DeclaringType?.Name}.{m.Name}: {string.Join(", ", owners)}");
            }
            Log.Message(sb.ToString());
        }

        private static void RecordCaller()
        {
            var frames = new StackTrace(2, false).GetFrames();
            var chain = new List<string>();
            if (frames != null)
                foreach (StackFrame f in frames)
                {
                    MethodBase m = f.GetMethod();
                    if (m == null || m.DeclaringType == typeof(Profiler))
                        continue;
                    string n = m.DeclaringType != null ? m.DeclaringType.Name + "." + m.Name : m.Name;
                    if (n.Contains("get_AllPawnsUnspawned"))
                        continue;
                    chain.Add(n);
                    if (chain.Count >= 7)
                        break;
                }
            string key = string.Join(" < ", chain);
            unspawnedCallers.TryGetValue(key, out int c);
            unspawnedCallers[key] = c + 1;
        }

        private static void RecordTick(long start)
        {
            tickLines.Add($"  tick +{TicksToWatchAfter - ticksLeftToWatch + 1}: {Ms(Stopwatch.GetTimestamp() - start):F1} ms");
            ticksLeftToWatch--;
            if (ticksLeftToWatch == 0 && pendingReport != null)
            {
                Log.Message(pendingReport + "\nFollowing ticks:\n" + string.Join("\n", tickLines));
                pendingReport = null;
            }
        }

        private static string BuildReport(double totalMs)
        {
            var sb = new StringBuilder();
            int worldNow = Find.WorldPawns?.AllPawnsAliveOrDead?.Count ?? -1;
            int free = Find.WorldPawns?.GetPawnsBySituationCount(WorldPawnSituation.Free) ?? -1;
            sb.AppendLine($"[RaidSpawnProfiler] {incidentName}: {totalMs:F0} ms total. World pawns {pawnsBefore} -> {worldNow} (Free {free}).");
            sb.AppendLine("  inclusive ms |  self ms | calls | avg ms | max ms | method");
            foreach (var kv in entries.OrderByDescending(kv => kv.Value.Ticks))
            {
                Entry e = kv.Value;
                sb.AppendLine($"  {Ms(e.Ticks),10:F1} | {Ms(e.SelfTicks),8:F1} | {e.Calls,5} | {Ms(e.Ticks) / e.Calls,6:F2} | {Ms(e.MaxTicks),6:F1} | {names[kv.Key]}");
            }
            if (unspawnedCallers.Count > 0)
            {
                sb.AppendLine("  AllPawnsUnspawned callers:");
                foreach (var kv in unspawnedCallers.OrderByDescending(kv => kv.Value).Take(8))
                    sb.AppendLine($"    {kv.Value,5} x {kv.Key}");
            }
            return sb.ToString().TrimEnd();
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
