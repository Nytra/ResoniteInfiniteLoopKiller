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

namespace InfiniteLoopKiller
{
    internal class InfiniteLoopKillerConfig : ConfigSection
    {
        private readonly DefiningConfigKey<int> _maxExecutionsKey = new("MaxExecutions", "Maximum number of executions per update.", () => 10000);
        private readonly DefiningConfigKey<bool> _disregardContextKey = new("DisregardContext", "Whether or not to abort infinitely executing protoflux regardless of execution context.", () => true);

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

        public override Version Version { get; } = new Version(1, 0, 0);
    }

    class ExecutionData
    {
        public int LocalUpdateIndex;
        public int NumExecutions = 1;
        public ProtoFluxNodeGroup? Group;
    }
    
    internal class InfiniteLoopKiller : ConfiguredResoniteMonkey<InfiniteLoopKiller, InfiniteLoopKillerConfig>
    {
        static readonly Dictionary<FrooxEngineContext, ExecutionData> _contextExecutionData = new();

        // The options for these should be provided by your game's game pack.
        protected override IEnumerable<IFeaturePatch> GetFeaturePatches()
        {
            yield return new FeaturePatch<FrooxEngineProtoflux>(PatchCompatibility.Postfix);
        }

        static void ProcessExecution(ProtoFlux.Runtimes.Execution.ExecutionContext context)
        {
            if (context is FrooxEngineContext frooxEngineContext)
            {
                Logger.Debug(() => "FrooxEngineContext");
                Logger.Debug(() => $"Context HashCode: {context.GetHashCode()}");
                Logger.Debug(() => $"World: {frooxEngineContext.World.Name}");
                Logger.Debug(() => $"Group: {frooxEngineContext.Group?.Name ?? "NULL"}");
                Logger.Debug(() => $"LocalUpdateIndex: {frooxEngineContext.Time.LocalUpdateIndex}");
                Logger.Debug(() => $"AbortExecution: {frooxEngineContext.AbortExecution}");

                if (frooxEngineContext.AbortExecution)
                {
                    throw new StackOverflowException($"This execution context has been aborted!");
                }

                if (!_contextExecutionData.ContainsKey(frooxEngineContext))
                {
                    var executionData = new ExecutionData();
                    executionData.LocalUpdateIndex = frooxEngineContext.Time.LocalUpdateIndex;
                    executionData.Group = frooxEngineContext.Group;
                    _contextExecutionData.Add(frooxEngineContext, executionData);
                    Logger.Debug(() => $"Added new context execution data.");
                    Logger.Debug(() => $"New size: {_contextExecutionData.Count}");
                }
                else
                {
                    _contextExecutionData.TryGetValue(frooxEngineContext, out var executionData);
                    if (executionData != null)
                    {
                        Logger.Debug(() => $"Got existing context execution data.");
                        if (executionData.LocalUpdateIndex == frooxEngineContext.Time.LocalUpdateIndex)
                        {
                            executionData.NumExecutions++;
                            Logger.Debug(() => $"NumExecutions: {executionData.NumExecutions}");
                            if (executionData.NumExecutions > ConfigSection.MaxExecutions)
                            {
                                frooxEngineContext.AbortExecution = true;
                                throw new StackOverflowException($"Maximum number of per-update executions ({ConfigSection.MaxExecutions}) for this execution context has been reached!");
                            }
                        }
                        else
                        {
                            _contextExecutionData.Remove(frooxEngineContext);
                            Logger.Debug(() => $"Removed stale context execution data.");
                        }
                    }
                }

                if (ConfigSection.DisregardContext)
                {
                    foreach (var kVP in _contextExecutionData)
                    {
                        if (kVP.Key == frooxEngineContext) continue;
                        if (kVP.Value.LocalUpdateIndex == frooxEngineContext.Time.LocalUpdateIndex
                            && kVP.Value.NumExecutions > ConfigSection.MaxExecutions
                            && kVP.Value.Group == frooxEngineContext.Group)
                        {
                            frooxEngineContext.AbortExecution = true;

                            foreach (var activeContext in frooxEngineContext.Controller._activeContexts)
                            {
                                if (activeContext.Group == frooxEngineContext.Group)
                                {
                                    activeContext.AbortExecution = true;
                                }
                            }

                            throw new StackOverflowException($"A previous execution context aborted this node group!");
                        }
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