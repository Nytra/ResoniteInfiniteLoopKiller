using FrooxEngine.ProtoFlux;
using HarmonyLib;
using MonkeyLoader.Configuration;
using MonkeyLoader.Patching;
using MonkeyLoader.Resonite;
using MonkeyLoader.Resonite.Features.FrooxEngine;
using System;
using System.Collections.Generic;
using ProtoFlux.Runtimes.Execution;
using ProtoFlux.Core;
using System.Reflection;
using Elements.Core;
using System.Reflection.Emit;

namespace InfiniteLoopKiller
{
    internal class InfiniteLoopKillerConfig : ConfigSection
    {
        private readonly DefiningConfigKey<int> _maxExecutionsKey = new("MaxExecutions", "Maximum number of executions per update.", () => 10000, valueValidator: value => value >= 10000);
        private readonly DefiningConfigKey<bool> _disregardContextKey = new("DisregardContext", "Whether or not to abort infinitely executing protoflux regardless of execution context.", () => true);
        private readonly DefiningConfigKey<bool> _useLoggingKey = new("ExtraDebugLogging", "Whether or not to print additional debug messages to the log.", () => false);

        public override string Description => "Contains the config for InfiniteLoopKiller.";

        public override string Id => "InfiniteLoopKiller Config";

        public int MaxExecutions
        {
            get => _maxExecutionsKey.GetValue()!;
            set => _maxExecutionsKey.SetValue(value);
        }

        public bool DisregardContext
        {
            get => _disregardContextKey.GetValue()!;
            set => _disregardContextKey.SetValue(value);
        }

        public bool UseLogging
        {
            get => _useLoggingKey.GetValue()!;
            set => _useLoggingKey.SetValue(value);
        }

        public override Version Version { get; } = new Version(1, 0, 0);
    }

    class ExecutionData
    {
        public int LocalUpdateIndex;
        public int NumExecutions = 0;
    }

    internal class InfiniteLoopKiller : ConfiguredResoniteMonkey<InfiniteLoopKiller, InfiniteLoopKillerConfig>
    {
        static readonly Dictionary<FrooxEngineContext, ExecutionData> _contextExecutionData = new();

        // The options for these should be provided by your game's game pack.
        protected override IEnumerable<IFeaturePatch> GetFeaturePatches()
        {
            yield return new FeaturePatch<FrooxEngineProtoflux>(PatchCompatibility.Postfix);
        }

        public override bool CanBeDisabled => true;

        static void ExtraDebug(string message)
        {
            if (ConfigSection.UseLogging)
            {
                Logger.Debug(() => message);
            }
        }

        protected override bool OnEngineReady()
        {
            Type t = AccessTools.TypeByName("FrooxEngine.ProtoFlux.ProtoFluxNodeGroup+<>c__DisplayClass107_0");
            if (t != null)
            {
                Logger.Debug(() => "Got stupid type");
                MethodInfo m = AccessTools.Method(t, "<StartAsyncTask>b__0");
                if (m != null)
                {
                    Logger.Debug(() => "Got stupid method");
                    Harmony.Patch(AccessTools.AsyncMoveNext(m), transpiler: new HarmonyMethod(typeof(RemoveLogMethodPatch), "Transpiler"));
                }
            }
            return base.OnEngineReady();
        }

        class RemoveLogMethodPatch
        {
            static readonly MethodInfo _errorLogMethod = AccessTools.Method(typeof(UniLog), "Error");
            public static void FakeLog(string message, bool stackTrace)
            {
                if (!message.StartsWith("Exception when running async node task:\nProtoFlux.Runtimes.Execution.ExecutionAbortedException"))
                {
                    UniLog.Error(message, stackTrace);
                }
            }
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                var patching = typeof(RemoveLogMethodPatch).GetMethod(nameof(FakeLog));
                foreach (var code in codes)
                {
                    if (code.Calls(_errorLogMethod))
                    {
                        yield return new(OpCodes.Call, patching);
                    }
                    else
                    {
                        yield return code;
                    }
                }
            }
        }

        static void ProcessExecution(ProtoFlux.Runtimes.Execution.ExecutionContext context)
        {
            if (!Enabled) return;

            if (context is FrooxEngineContext frooxEngineContext)
            {
                ExtraDebug("FrooxEngineContext");
                ExtraDebug($"Context HashCode: {context.GetHashCode()}");
                ExtraDebug($"World: {frooxEngineContext.World.Name}");
                ExtraDebug($"Group: {frooxEngineContext.Group?.Name ?? "NULL"}");
                ExtraDebug($"LocalUpdateIndex: {frooxEngineContext.Time.LocalUpdateIndex}");
                ExtraDebug($"AbortExecution: {frooxEngineContext.AbortExecution}");

                ExecutionData? executionData;
                if (!_contextExecutionData.ContainsKey(frooxEngineContext))
                {
                    executionData = new ExecutionData();
                    executionData.LocalUpdateIndex = frooxEngineContext.Time.LocalUpdateIndex;
                    _contextExecutionData.Add(frooxEngineContext, executionData);
                    ExtraDebug($"Added new context execution data.");
                    ExtraDebug($"New size: {_contextExecutionData.Count}");
                }
                else
                {
                    if (_contextExecutionData.TryGetValue(frooxEngineContext, out executionData))
                    {
                        ExtraDebug($"Got existing context execution data.");
                    }
                }
                if (executionData != null)
                {
                    if (executionData.LocalUpdateIndex == frooxEngineContext.Time.LocalUpdateIndex)
                    {
                        executionData.NumExecutions++;
                        ExtraDebug($"NumExecutions: {executionData.NumExecutions}");
                        if (executionData.NumExecutions > ConfigSection.MaxExecutions)
                        {
                            Logger.Info(() => $"Aborting an execution context for ProtoFluxNodeGroup: {frooxEngineContext.Group?.Name ?? "NULL"}");
                            frooxEngineContext.AbortExecution = true;
                            _contextExecutionData.Remove(frooxEngineContext);
                            if (ConfigSection.DisregardContext)
                            {
                                Logger.Info(() => $"Disregarding context and aborting execution for entire ProtoFluxNodeGroup: {frooxEngineContext.Group?.Name ?? "NULL"}");
                                foreach (var activeContext in frooxEngineContext.Controller._activeContexts)
                                {
                                    if (activeContext.Group != null && activeContext.Group == frooxEngineContext.Group)
                                    {
                                        activeContext.AbortExecution = true;
                                        _contextExecutionData.Remove(activeContext);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        _contextExecutionData.Remove(frooxEngineContext);
                        ExtraDebug($"Removed stale context execution data.");
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ExecutionContextExtensions), "Execute", new Type[] { typeof(IImpulse), typeof(ProtoFlux.Runtimes.Execution.ExecutionContext) })]
        [HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        class PatchIImpulse
        {
            private static void Postfix(ProtoFlux.Runtimes.Execution.ExecutionContext context)
            {
                ProcessExecution(context);
            }
        }

        [HarmonyPatch(typeof(ExecutionContextExtensions), "ExecuteAsync", new Type[] { typeof(IImpulse), typeof(ProtoFlux.Runtimes.Execution.ExecutionContext) })]
        [HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        class PatchAsyncIImpulse
        {
            private static void Postfix(ProtoFlux.Runtimes.Execution.ExecutionContext context)
            {
                ProcessExecution(context);
            }
        }

        [HarmonyPatch(typeof(ExecutionContextExtensions), "Execute", new Type[] { typeof(ICall), typeof(ProtoFlux.Runtimes.Execution.ExecutionContext) })]
        [HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        class PatchICall
        {
            private static void Postfix(ProtoFlux.Runtimes.Execution.ExecutionContext context)
            {
                ProcessExecution(context);
            }
        }

        [HarmonyPatch(typeof(ExecutionContextExtensions), "Execute", new Type[] { typeof(Call), typeof(ProtoFlux.Runtimes.Execution.ExecutionContext) })]
        [HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        class PatchCall
        {
            private static void Postfix(ProtoFlux.Runtimes.Execution.ExecutionContext context)
            {
                ProcessExecution(context);
            }
        }

        [HarmonyPatch(typeof(ExecutionContextExtensions), "ExecuteAsync", new Type[] { typeof(AsyncCall), typeof(ProtoFlux.Runtimes.Execution.ExecutionContext) })]
        [HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        class PatchAsyncCall
        {
            private static void Postfix(ProtoFlux.Runtimes.Execution.ExecutionContext context)
            {
                ProcessExecution(context);
            }
        }
    }
}