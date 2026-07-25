using System;
using System.Text.Json;

namespace Flow.Grains.Interfaces.Model
{
    // Shared configuration for the Orleans fallback JSON codec (Microsoft.Orleans.Serialization.SystemTextJson's
    // AddJsonSerializer), used in place of Orleans's [GenerateSerializer] codegen for the XSD-generated CMMN
    // model. Everything else in the solution is swept with [GenerateSerializer] + [Id(n)] and uses Orleans's
    // native serializer; this fallback exists only because the generated model file is frozen (CMMN 1.1 is
    // final - no regeneration planned) and adding [Id(n)] throughout it is not on the table.
    //
    // The silo (Flow.Silo/Program.cs), the TestCluster silo configurator, and the TestCluster client
    // configurator (both in Flow.Grains.Tests.Integration/SiloFixture/ClusterFixture.cs) must all register
    // this identically - a TestCluster's in-process client independently validates serializer coverage for
    // every type reachable from grain interfaces, so a mismatch throws CodecNotFoundException even when the
    // silo side is configured correctly. Centralizing the isSupported predicate and JsonSerializerOptions here
    // keeps the three registrations byte-identical by construction instead of by copy-paste discipline.
    //
    // Polymorphism (CMMN model hierarchy) and Populate-mode handling (get-only collection properties) are
    // both configured in CmmnPolymorphicTypeResolver, scoped per-property rather than via the blanket
    // JsonSerializerOptions.PreferredObjectCreationHandling - see that type for why the global setting
    // breaks every nullable object-reference property in the model.
    public static class OrleansFallbackJsonSerializer
    {
        public static bool IsSupportedType(Type type) =>
            type.Namespace?.StartsWith("Flow.Grains.Interfaces.Model", StringComparison.Ordinal) ?? false;

        public static JsonSerializerOptions Options() => new()
        {
            TypeInfoResolver = new CmmnPolymorphicTypeResolver()
        };
    }
}
