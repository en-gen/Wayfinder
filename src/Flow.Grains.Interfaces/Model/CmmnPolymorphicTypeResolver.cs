using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Serialization;

namespace Flow.Grains.Interfaces.Model
{
    // The XSD-generated CMMN model (Spec.CMMN.MODEL.cs) is a polymorphic hierarchy: PlanItemDefinition
    // -> Stage/HumanTask/Milestone/..., CmmnElement -> (nearly everything), etc. System.Text.Json does
    // not infer polymorphism from inheritance alone - without explicit configuration, a property typed
    // as the abstract/base type (e.g. PlanItemDefinition) deserializes back as that declared type,
    // silently losing the concrete subtype identity that PlanItemBehaviorConfiguratorService's type
    // dispatch (and Sentry OnPart matching) depends on. That exact failure mode is why the old
    // Newtonsoft registration used TypeNameHandling.Auto (see git history of Flow.Silo/Program.cs).
    //
    // Rather than hand-maintaining a parallel [JsonDerivedType] list (56 types across the model, several
    // multi-level), this resolver derives the derived-type closure for each polymorphic base type by
    // reflecting over the [XmlInclude] attributes the code generator already emitted for XML schema
    // substitution-group support. Every abstract/extensible type in the model (CmmnElement, Artifact,
    // CmmnElementWithMixedContent, PlanItemDefinition, PlanFragment, ...) already carries the exact
    // "what can appear here" list STJ needs for JsonPolymorphismOptions.DerivedTypes - this just adapts
    // one attribute source to the other, so the model file never needs regeneration-defeating manual
    // touch-ups when new CMMN types are added.
    public class CmmnPolymorphicTypeResolver : DefaultJsonTypeInfoResolver
    {
        // Discriminator values are the bare CLR type name (e.g. "Stage", "HumanTask"). Unique across
        // the CMMN model (the XSD defines one global type per name) and stable across refactors that
        // don't rename the type itself - safer than an assembly-qualified name for a payload that may
        // be persisted (journaled grain state, Orleans wire format) across deployments.
        private static readonly ConcurrentDictionary<Type, JsonDerivedType[]> DerivedTypeCache = new();

        public override JsonTypeInfo GetTypeInfo(Type type, System.Text.Json.JsonSerializerOptions options)
        {
            var jsonTypeInfo = base.GetTypeInfo(type, options);

            if (jsonTypeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return jsonTypeInfo;
            }

            ApplyPolymorphism(jsonTypeInfo, type);
            ApplyPopulateToReadOnlyCollections(jsonTypeInfo);

            return jsonTypeInfo;
        }

        private static void ApplyPolymorphism(JsonTypeInfo jsonTypeInfo, Type type)
        {
            var derivedTypes = DerivedTypeCache.GetOrAdd(type, GetDeclaredDerivedTypes);
            if (derivedTypes.Length == 0)
            {
                return;
            }

            jsonTypeInfo.PolymorphismOptions = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
                IgnoreUnrecognizedTypeDiscriminators = false,
                UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
            };

            foreach (var derivedType in derivedTypes)
            {
                jsonTypeInfo.PolymorphismOptions.DerivedTypes.Add(derivedType);
            }
        }

        // inherit: false - a type's own [XmlInclude] closure already encodes everything reachable from
        // that point (the code generator repeats the full substitution-group list at every extensible
        // level; see e.g. PlanItemDefinition vs. its own child PlanFragment). Inheriting the parent's
        // attributes here would be redundant, not additive.
        private static JsonDerivedType[] GetDeclaredDerivedTypes(Type type) =>
            type.GetCustomAttributes(typeof(XmlIncludeAttribute), inherit: false)
                .Cast<XmlIncludeAttribute>()
                .Select(a => new JsonDerivedType(a.Type, a.Type.Name))
                .ToArray();

        // Every repeatable child element in the generated model (TableItems, PlanItems, Sentries,
        // EntryCriteria, Documentation, ...) is a get-only ICollection<T> property backed by a private
        // field the parameterless constructor initializes to an empty List<T>. System.Text.Json's
        // *default* behavior for a setter-less property is to leave it untouched during deserialization
        // (see dotnet/runtime #31015) - opting these specific properties into
        // JsonObjectCreationHandling.Populate makes STJ call .Add() on the existing (constructor-created)
        // instance instead, which is what makes them round-trip at all.
        //
        // Deliberately scoped to collection-typed properties only (not the blanket
        // JsonSerializerOptions.PreferredObjectCreationHandling): almost every type in this model
        // inherits CmmnElement's Any/Documentation/AnyAttributes collections, so a type-wide or
        // options-wide Populate default makes STJ treat *every* nullable object-reference property
        // (e.g. PlanItemDefinition.DefaultControl, RepetitionRule.Condition) as populate-managed too -
        // and Populate mode cannot represent an explicit JSON null for those (there is no way to
        // "populate null" into a non-null existing reference), which throws
        // InvalidOperationException("Unable to assign 'null' to the property...") for every legitimately
        // absent optional element. Scoping to IEnumerable (excluding string) avoids that entirely.
        private static void ApplyPopulateToReadOnlyCollections(JsonTypeInfo jsonTypeInfo)
        {
            foreach (var property in jsonTypeInfo.Properties)
            {
                if (property.Set is not null) continue;
                if (property.PropertyType == typeof(string)) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) continue;

                property.ObjectCreationHandling = JsonObjectCreationHandling.Populate;
            }
        }
    }
}
