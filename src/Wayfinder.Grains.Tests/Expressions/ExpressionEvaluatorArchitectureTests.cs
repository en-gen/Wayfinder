using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Wayfinder.Grains.Expressions;
using FluentAssertions;
using Xunit;

namespace Wayfinder.Grains.Tests.Expressions
{
    // WHY THIS TEST EXISTS
    // ~~~~~
    // Finding I-4 (design 05 section D.2): the expression evaluator makes NO outbound grain calls.
    //
    // That edge is finding D3's distributed wait cycle. The old ExpressionGrain resolved its own
    // context - GrainFactory.GetCaseFileItem(caseInstanceId, contextRef).GetSnapshot() - from
    // inside evaluation. On today's topology that is merely an extra hop. Once phase P2 moves
    // case-file items into the case grain it becomes case grain -> evaluator -> case grain, and the
    // case grain is deliberately NOT reentrant (single-turn atomicity, section B.1; StageBehavior's
    // #198 correctness argument depends on it). The inner call then queues behind the outer turn
    // and both block until the response timeout. Not a slowdown - a deadlock.
    //
    // The fix is structural: the caller binds the context and hands over a fully-resolved
    // ExpressionRequest (see ExpressionContext). This test is what keeps it structural. A future,
    // entirely well-meaning "it would be so much simpler to just fetch the item here" reintroduces
    // the cycle, and nothing else in the suite would notice: it would pass every functional test on
    // the current topology and only deadlock after P2 lands.
    //
    // WHAT IT CHECKS, AND WHAT IT DOES NOT
    // ~~~~~
    // The evaluator's dependency graph - constructors, fields, properties, method signatures and
    // base types, walked transitively through Wayfinder types - must not mention
    // Orleans.IGrainFactory or Orleans.IClusterClient. That covers the realistic regression, which
    // is a dependency being TAKEN (a constructor parameter or a field), and it covers inheriting
    // from Orleans.Grain, whose GrainFactory property would hand one over.
    //
    // It does not decompile method bodies, so a static/service-locator grab would slip past it.
    // If you are tempted to write one: don't. Bind at the caller.
    public class ExpressionEvaluatorArchitectureTests
    {
        private static readonly string[] ForbiddenTypeNames =
        {
            "Orleans.IGrainFactory",
            "Orleans.IClusterClient"
        };

        [Fact]
        public void ExpressionEvaluator__DependencyGraph__ContainsNoGrainFactoryOrClusterClient()
        {
            var offenders = ForbiddenReferencesFrom(typeof(ExpressionEvaluator)).ToList();

            offenders.Should().BeEmpty(
                "the expression evaluator must make no outbound grain calls (finding I-4). " +
                "Bind the context at the caller and put it on the ExpressionRequest - see this " +
                "test's remarks for what reintroducing this edge costs. Offending path(s): " +
                string.Join("; ", offenders));
        }

        // Sanity check on the detector itself: a type that DOES take an IGrainFactory must be
        // reported. Without this, a typo in the walk below would turn the test above into one that
        // can never fail - which proves nothing at all.
        [Fact]
        public void TheDetector__GivenATypeThatTakesAGrainFactory__ReportsIt()
        {
            ForbiddenReferencesFrom(typeof(DeliberateGrainFactoryDependency))
                .Should()
                .NotBeEmpty("the detector must actually detect the dependency it exists to forbid");
        }

        private sealed class DeliberateGrainFactoryDependency
        {
            // Referenced only by the detector self-check above.
            public DeliberateGrainFactoryDependency(Orleans.IGrainFactory grainFactory)
            {
                GrainFactory = grainFactory;
            }

            public Orleans.IGrainFactory GrainFactory { get; }
        }

        private static IEnumerable<string> ForbiddenReferencesFrom(Type root)
        {
            var seen = new HashSet<Type>();
            var queue = new Queue<(Type Type, string Path)>();
            queue.Enqueue((root, root.Name));

            while (queue.Count > 0)
            {
                var (type, path) = queue.Dequeue();

                foreach (var referenced in ReferencedTypes(type))
                {
                    foreach (var candidate in Flatten(referenced))
                    {
                        var name = $"{candidate.Namespace}.{candidate.Name}";

                        if (ForbiddenTypeNames.Contains(name))
                        {
                            yield return $"{path} -> {name}";
                            continue;
                        }

                        // Only walk further through this solution's own types; the BCL and Orleans'
                        // own object graphs are not what a regression here would look like, and
                        // walking them would take forever.
                        if (candidate.Assembly != root.Assembly) continue;
                        if (!seen.Add(candidate)) continue;

                        queue.Enqueue((candidate, $"{path} -> {candidate.Name}"));
                    }
                }
            }
        }

        private static IEnumerable<Type> ReferencedTypes(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.DeclaredOnly;

            if (type.BaseType != null) yield return type.BaseType;

            foreach (var i in type.GetInterfaces()) yield return i;

            foreach (var ctor in type.GetConstructors(all))
                foreach (var p in ctor.GetParameters())
                    yield return p.ParameterType;

            foreach (var field in type.GetFields(all)) yield return field.FieldType;

            foreach (var property in type.GetProperties(all)) yield return property.PropertyType;

            foreach (var method in type.GetMethods(all))
            {
                yield return method.ReturnType;
                foreach (var p in method.GetParameters()) yield return p.ParameterType;
            }
        }

        // A dependency can hide inside a generic argument (Func<string, IExecutable>,
        // Task<IGrainFactory>, ...) or an array element, so unwrap those rather than looking only
        // at the outermost type.
        private static IEnumerable<Type> Flatten(Type type)
        {
            if (type == null) yield break;

            if (type.IsByRef || type.IsPointer || type.IsArray)
            {
                foreach (var inner in Flatten(type.GetElementType())) yield return inner;
                yield break;
            }

            yield return type;

            if (!type.IsGenericType) yield break;

            foreach (var argument in type.GetGenericArguments())
                foreach (var inner in Flatten(argument))
                    yield return inner;
        }
    }
}
