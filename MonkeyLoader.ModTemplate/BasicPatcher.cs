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

namespace InfiniteLoopKiller
{
    internal class InfiniteLoopKillerConfig : ConfigSection
    {
        private readonly DefiningConfigKey<int> _maxExecutionsKey = new("MaxExecutions", "Maximum number of executions per update.", () => 10000, valueValidator: value => value >= 5000);
        private readonly DefiningConfigKey<bool> _disregardContextKey = new("DisregardContext", "Whether or not to abort infinitely executing protoflux regardless of execution context.", () => true);
        private readonly DefiningConfigKey<bool> _useLoggingKey = new("UseLogging", "Whether or not to print debug messages to the log.", () => false);

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
        public ProtoFluxNodeGroup? Group;
    }
    
    internal class InfiniteLoopKiller : ConfiguredResoniteMonkey<InfiniteLoopKiller, InfiniteLoopKillerConfig>
    {
        static readonly Dictionary<FrooxEngineContext, ExecutionData> _contextExecutionData = new();

        //static readonly PropertyInfo _currentDepthProperty = AccessTools.Property(typeof(ExecutionContext), "CurrentDepth");

        // The options for these should be provided by your game's game pack.
        protected override IEnumerable<IFeaturePatch> GetFeaturePatches()
        {
            yield return new FeaturePatch<FrooxEngineProtoflux>(PatchCompatibility.Postfix);
        }

        public override bool CanBeDisabled => true;

        static void Log(Func<object> messageProducer)
        {
            if (ConfigSection.UseLogging)
            {
                Logger.Debug(messageProducer);
            }
        }

        static void ProcessExecution(ProtoFlux.Runtimes.Execution.ExecutionContext context)
        {
            if (!Enabled) return;

            if (context is FrooxEngineContext frooxEngineContext)
            {
                Log(() => "FrooxEngineContext");
                Log(() => $"Context HashCode: {context.GetHashCode()}");
                Log(() => $"World: {frooxEngineContext.World.Name}");
                Log(() => $"Group: {frooxEngineContext.Group?.Name ?? "NULL"}");
                Log(() => $"LocalUpdateIndex: {frooxEngineContext.Time.LocalUpdateIndex}");
                Log(() => $"AbortExecution: {frooxEngineContext.AbortExecution}");

                if (frooxEngineContext.AbortExecution)
                {
                    //throw new StackOverflowException($"This execution context has been aborted!");
                }

                ExecutionData? executionData;
                if (!_contextExecutionData.ContainsKey(frooxEngineContext))
                {
                    executionData = new ExecutionData();
                    executionData.LocalUpdateIndex = frooxEngineContext.Time.LocalUpdateIndex;
                    executionData.Group = frooxEngineContext.Group;
                    _contextExecutionData.Add(frooxEngineContext, executionData);
                    Log(() => $"Added new context execution data.");
                    Log(() => $"New size: {_contextExecutionData.Count}");
                }
                else
                {
                    if (_contextExecutionData.TryGetValue(frooxEngineContext, out executionData))
                    {
                        Log(() => $"Got existing context execution data.");
                    }
                }
                if (executionData != null)
                {
                    if (executionData.LocalUpdateIndex == frooxEngineContext.Time.LocalUpdateIndex)
                    {
                        executionData.NumExecutions++;
                        //_currentDepthProperty.SetValue(frooxEngineContext, frooxEngineContext.CurrentDepth + 1);
                        Log(() => $"NumExecutions: {executionData.NumExecutions}");
                        if (executionData.NumExecutions > ConfigSection.MaxExecutions)
                        {
                            frooxEngineContext.AbortExecution = true;
                            _contextExecutionData.Remove(frooxEngineContext);
                            if (ConfigSection.DisregardContext)
                            {
                                foreach (var activeContext in frooxEngineContext.Controller._activeContexts)
                                {
                                    if (activeContext.Group == frooxEngineContext.Group)
                                    {
                                        activeContext.AbortExecution = true;
                                        _contextExecutionData.Remove(activeContext);
                                    }
                                }
                            }
                            //throw new StackOverflowException($"Maximum number of per-update executions ({ConfigSection.MaxExecutions}) for this execution context has been reached!");
                        }
                    }
                    else
                    {
                        _contextExecutionData.Remove(frooxEngineContext);
                        Log(() => $"Removed stale context execution data.");
                    }
                }
            }
        }

        // This doesn't seem to work very well
        //[HarmonyPatch(typeof(ProtoFluxController), "ReturnContext")]
        //[HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        //class PatchReturnContext
        //{
        //    private static void Prefix(ref FrooxEngineContext context)
        //    {
        //        if (_contextExecutionData.ContainsKey(context))
        //        {
        //            string groupName = context.Group.Name;
        //            Logger.Info(() => $"Removing context for group: {groupName}");
        //            _contextExecutionData.Remove(context);
        //        }
        //    }
        //}

        // This doesn't work well either
        //[HarmonyPatch(typeof(ProtoFluxController), "BorrowContext")]
        //[HarmonyPatchCategory(nameof(InfiniteLoopKiller))]
        //class PatchBorrowContext
        //{
        //    private static void Postfix(FrooxEngineContext __result)
        //    {
        //        if (ConfigSection.DisregardContext)
        //        {
        //            foreach (var kVP in _contextExecutionData)
        //            {
        //                //if (kVP.Key == __result) continue;
        //                if (kVP.Value.LocalUpdateIndex == __result.Time.LocalUpdateIndex
        //                    && kVP.Value.NumExecutions > ConfigSection.MaxExecutions
        //                    && kVP.Value.Group == __result.Group)
        //                {
        //                    __result.AbortExecution = true;

        //                    throw new StackOverflowException($"A previous execution context aborted this node group!");
        //                }
        //            }
        //        }
        //    }
        //}

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