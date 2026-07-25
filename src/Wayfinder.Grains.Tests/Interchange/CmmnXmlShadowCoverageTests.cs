using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using Flow.Grains.Interchange;
using Flow.Grains.Interfaces.Model;
using FluentAssertions;
using Xunit;

namespace Flow.Grains.Tests.Interchange
{
    // ADO #20 - structural regression guard for the XML shadow-property mechanism.
    // ~~~~~
    // The bug class being killed (not just its instances): this .NET 10 XmlSerializer silently
    // never populates an interface-typed, get-only collection property on Deserialize - no
    // exception, no diagnostic, the collection just stays empty (see CmmnXmlCollectionShadows.cs
    // for the empirical proof). Every such property in the model must therefore be XmlIgnore'd
    // via CmmnXmlSerializer.BuildOverrides and replaced by a concrete-typed shadow carrying the
    // identical XML mapping. These tests re-derive that obligation set by reflection on every
    // run: regenerate the model with a new collection, or add a partial with a new XML-visible
    // interface-typed member, without completing the shadow treatment, and this suite fails with
    // a message naming the offending property - the gap cannot silently reopen.
    //
    // BuildOverrides is reached via reflection deliberately: it is CmmnXmlSerializer's private
    // implementation detail, and widening its visibility for a test would invite production
    // callers. If it is ever renamed, the guard fails loudly at the accessor below rather than
    // silently passing with an empty override set.
    public class CmmnXmlShadowCoverageTests
    {
        private static readonly XmlAttributeOverrides Overrides = GetBuildOverrides();

        private static XmlAttributeOverrides GetBuildOverrides()
        {
            var method = typeof(CmmnXmlSerializer).GetMethod("BuildOverrides", BindingFlags.NonPublic | BindingFlags.Static);
            if (method is null)
            {
                throw new InvalidOperationException(
                    "CmmnXmlSerializer.BuildOverrides (private static) not found - this guard suite verifies its " +
                    "contents against the model's shadow properties; update this accessor alongside any rename.");
            }

            return (XmlAttributeOverrides)method.Invoke(null, null);
        }

        // The XML-serializable model surface: every class the generator (or the hand-authored
        // Interchange layer, which follows the same idiom) stamped with [XmlType].
        private static IEnumerable<Type> XmlModelTypes() =>
            typeof(CmmnElement).Assembly.GetTypes()
                .Where(t => t.IsClass && t.GetCustomAttribute<XmlTypeAttribute>(inherit: false) is not null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

        [Fact]
        public void EveryXmlMappedInterfaceCollection__HasBuildOverridesIgnoreAndConcreteShadow()
        {
            var violations = new List<string>();

            foreach (var type in XmlModelTypes())
            {
                foreach (var property in DeclaredInstanceProperties(type))
                {
                    if (property.Name.EndsWith("Xml", StringComparison.Ordinal)) continue; // shadows themselves - verified in the pairing test
                    if (!IsInterfaceCollection(property.PropertyType)) continue;
                    if (!HasXmlMapping(property)) continue;

                    if (!(Overrides[type, property.Name]?.XmlIgnore ?? false))
                    {
                        violations.Add($"{type.Name}.{property.Name}: XML-mapped interface-typed collection is not ignored in CmmnXmlSerializer.BuildOverrides");
                    }

                    if (type.GetProperty(property.Name + "Xml", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is null)
                    {
                        violations.Add($"{type.Name}.{property.Name}: no concrete-typed {property.Name}Xml shadow property exists (CmmnXmlCollectionShadows.cs)");
                    }
                }
            }

            violations.Should().BeEmpty(
                "every XML-mapped, interface-typed collection property silently deserializes EMPTY under this runtime's " +
                "XmlSerializer unless redirected to a concrete-typed shadow - see CmmnXmlCollectionShadows.cs");
        }

        [Fact]
        public void EveryUnmappedInterfaceEnumerable__IsExplicitlyIgnoredForXml()
        {
            // The engine-only partial projections (Stage.PlanItemDefinitionsNested, Sentry.
            // PlanItemOnParts, ...) carry no XML mapping attributes, but XmlSerializer
            // default-maps every public member it can - and a bare interface-typed enumerable
            // makes serializer CONSTRUCTION throw or the member silently vanish depending on
            // what else is reachable. Either way: each one must be deliberately ignored, via
            // attribute or BuildOverrides.
            var violations = new List<string>();

            foreach (var type in XmlModelTypes())
            {
                foreach (var property in DeclaredInstanceProperties(type))
                {
                    if (property.Name.EndsWith("Xml", StringComparison.Ordinal)) continue;
                    if (!IsInterfaceCollection(property.PropertyType)) continue;
                    if (HasXmlMapping(property)) continue; // covered by the shadow test above

                    var ignoredByAttribute = property.GetCustomAttribute<XmlIgnoreAttribute>() is not null;
                    var ignoredByOverride = Overrides[type, property.Name]?.XmlIgnore ?? false;

                    if (!ignoredByAttribute && !ignoredByOverride)
                    {
                        violations.Add($"{type.Name}.{property.Name}: interface-typed enumerable with no XML mapping must be [XmlIgnore]d or ignored in BuildOverrides");
                    }
                }
            }

            violations.Should().BeEmpty();
        }

        [Fact]
        public void EveryShadowProperty__MirrorsItsOriginalVerbatimOverTheSameInstance()
        {
            var violations = new List<string>();
            var shadowCount = 0;

            foreach (var type in XmlModelTypes())
            {
                foreach (var shadow in DeclaredInstanceProperties(type).Where(p => p.Name.EndsWith("Xml", StringComparison.Ordinal)))
                {
                    shadowCount++;

                    var original = type.GetProperty(shadow.Name[..^3], BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (original is null)
                    {
                        violations.Add($"{type.Name}.{shadow.Name}: no matching original property '{shadow.Name[..^3]}' on the same type");
                        continue;
                    }

                    if (shadow.PropertyType.IsInterface || !IsCollection(shadow.PropertyType))
                    {
                        violations.Add($"{type.Name}.{shadow.Name}: shadow must be a CONCRETE collection type (found {shadow.PropertyType.Name}) - that is the entire point of the shadow");
                    }

                    if (ItemType(original.PropertyType) != ItemType(shadow.PropertyType))
                    {
                        violations.Add($"{type.Name}.{shadow.Name}: item type {ItemType(shadow.PropertyType)?.Name} differs from original's {ItemType(original.PropertyType)?.Name}");
                    }

                    var originalMapping = DescribeXmlMapping(original);
                    var shadowMapping = DescribeXmlMapping(shadow);
                    if (originalMapping != shadowMapping)
                    {
                        violations.Add($"{type.Name}.{shadow.Name}: XML mapping differs from original - original [{originalMapping}] vs shadow [{shadowMapping}]");
                    }

                    // Without both ignore attributes, OrleansFallbackJsonSerializer serializes the
                    // same backing list twice and Populate-doubles it on the way back in - the
                    // exact regression that broke 22 unrelated integration tests when this
                    // mechanism was first written (see CmmnXmlCollectionShadows.cs's remarks).
                    if (shadow.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                    {
                        violations.Add($"{type.Name}.{shadow.Name}: missing [JsonIgnore]");
                    }
                    if (shadow.GetCustomAttribute<IgnoreDataMemberAttribute>() is null)
                    {
                        violations.Add($"{type.Name}.{shadow.Name}: missing [IgnoreDataMember]");
                    }

                    if (!type.IsAbstract)
                    {
                        var instance = Activator.CreateInstance(type);
                        if (!ReferenceEquals(original.GetValue(instance), shadow.GetValue(instance)))
                        {
                            violations.Add($"{type.Name}.{shadow.Name}: shadow does not return the SAME instance as the original - it must be a cast view, never a copy");
                        }
                    }
                }
            }

            violations.Should().BeEmpty();
            shadowCount.Should().Be(33,
                "one shadow per ICollection<T> property in the generated model (see CmmnXmlCollectionShadows.cs's " +
                "COVERAGE remarks) - update this pin alongside a deliberate model change, it exists to catch silent drift");
        }

        private static IEnumerable<PropertyInfo> DeclaredInstanceProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name, StringComparer.Ordinal);

        private static bool IsCollection(Type type) =>
            type != typeof(string) &&
            !typeof(System.Xml.XmlNode).IsAssignableFrom(type) &&
            typeof(IEnumerable).IsAssignableFrom(type);

        private static bool IsInterfaceCollection(Type type) => type.IsInterface && IsCollection(type);

        private static bool HasXmlMapping(PropertyInfo property) =>
            property.GetCustomAttributes<XmlElementAttribute>().Any() ||
            property.GetCustomAttribute<XmlAnyElementAttribute>() is not null ||
            property.GetCustomAttribute<XmlAnyAttributeAttribute>() is not null ||
            property.GetCustomAttribute<XmlArrayAttribute>() is not null;

        private static Type ItemType(Type collectionType) =>
            collectionType.IsGenericType ? collectionType.GetGenericArguments()[0] : null;

        // Canonical, order-insensitive description of a property's XML mapping. XmlElement
        // entries resolve their effective type ((attr.Type ?? collection item type)) so an
        // original's implicit item typing and a shadow's identical implicit typing compare equal,
        // while a dropped Type= on a polymorphic mapping (Stage's nine element names, Sentry's
        // two) is caught as a difference.
        private static string DescribeXmlMapping(PropertyInfo property)
        {
            var parts = property.GetCustomAttributes<XmlElementAttribute>()
                .Select(a => $"element:{a.ElementName}|ns:{a.Namespace}|type:{(a.Type ?? ItemType(property.PropertyType))?.FullName}|dataType:{a.DataType}")
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            if (property.GetCustomAttribute<XmlAnyElementAttribute>() is not null) parts.Add("anyElement");
            if (property.GetCustomAttribute<XmlAnyAttributeAttribute>() is not null) parts.Add("anyAttribute");

            return string.Join(";", parts);
        }
    }
}
