using System;
using System.Collections.Generic;

using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// A naming-rule condition can depend on a UDP that is MODEL-scoped rather than
/// owned by the rule's target object type - e.g. an "Application" model UDP gating a
/// TABLE prefix model-wide. The same UDP name can be an entity UDP in one model and a
/// model UDP in another, so the engine resolves against the LIVE model: it reads from
/// the entity first and, only when SCAPI reports the property is not on that class,
/// re-reads from the model root via <see cref="NamingValidationEngine.ModelRootProvider"/>.
/// These fixtures reproduce the live failure (rule 1167 in SQL_BUYUKMODEL: TableClass
/// is an entity UDP, Application is a model UDP) without COM.
/// </summary>
public sealed class ModelScopedUdpConditionTests : IDisposable
{
    public void Dispose() => NamingValidationEngine.ModelRootProvider = null;

    // Entity that returns values for its real props and throws erwin's
    // "not valid class id or class name" for anything else (a UDP that is not on
    // the Entity class - e.g. a model-scoped UDP).
    public sealed class PartialEntity
    {
        private readonly Dictionary<string, object> _ok;
        public PartialEntity(Dictionary<string, object> ok) { _ok = ok; }
        public FakeProp Properties(string code)
        {
            if (_ok.TryGetValue(code, out var v)) return new FakeProp(v);
            throw new InvalidOperationException(
                $"Model Properties Component ! {code} is not valid class id or class name for object or property");
        }
    }

    public sealed class FakeRoot
    {
        private readonly Dictionary<string, object> _p;
        public FakeRoot(Dictionary<string, object> p) { _p = p; }
        public FakeProp Properties(string code) => _p.TryGetValue(code, out var v) ? new FakeProp(v) : null;
    }

    public sealed class FakeProp
    {
        public FakeProp(object value) { Value = value; }
        public object Value { get; }
    }

    private static NamingRuleCondition Udp(int order, string connector, string udpName, string values)
        => new()
        {
            OrderIndex = order,
            Connector = connector,
            DependsOnUdpId = 100 + order,
            DependsOnUdpName = udpName,
            DependsOnPropertyValues = values,
        };

    private static NamingStandardRule TableRule(params NamingRuleCondition[] terms)
    {
        var r = new NamingStandardRule
        {
            Id = 1167,
            ObjectType = "Table",
            PropertyCode = "Physical_Name",
            RuleType = NamingRuleKind.Prefix,
            IsActive = true,
            ApplyOn = RuleApplyOn.Both,
        };
        foreach (var t in terms) r.Conditions.Add(t);
        return r;
    }

    [Fact]
    public void ModelScopedUdp_resolves_from_model_root_when_not_on_entity()
    {
        // Application is NOT an entity UDP -> entity read throws -> resolve from model.
        var entity = new PartialEntity(new Dictionary<string, object>());
        NamingValidationEngine.ModelRootProvider =
            () => new FakeRoot(new Dictionary<string, object> { ["Model.Physical.Application"] = "UYG547 | Kurumsal IBMB" });

        var rule = TableRule(Udp(0, null, "Application", "UYG547 | Kurumsal IBMB"));

        NamingValidationEngine.IsRuleApplicable(rule, "Table", entity).Should().BeTrue();
    }

    [Fact]
    public void ModelScopedUdp_not_applicable_when_model_value_differs()
    {
        var entity = new PartialEntity(new Dictionary<string, object>());
        NamingValidationEngine.ModelRootProvider =
            () => new FakeRoot(new Dictionary<string, object> { ["Model.Physical.Application"] = "SOME_OTHER_APP" });

        var rule = TableRule(Udp(0, null, "Application", "UYG547 | Kurumsal IBMB"));

        NamingValidationEngine.IsRuleApplicable(rule, "Table", entity).Should().BeFalse();
    }

    [Fact]
    public void ModelScopedUdp_not_applicable_and_no_crash_when_no_provider()
    {
        // No model root available: the entity-class error stands, value reads empty,
        // the condition is simply not satisfied (graceful, no throw).
        var entity = new PartialEntity(new Dictionary<string, object>());
        NamingValidationEngine.ModelRootProvider = null;

        var rule = TableRule(Udp(0, null, "Application", "UYG547 | Kurumsal IBMB"));

        NamingValidationEngine.IsRuleApplicable(rule, "Table", entity).Should().BeFalse();
    }

    [Fact]
    public void Mixed_entity_udp_AND_model_udp_both_satisfied_is_applicable()
    {
        // The exact live rule 1167: TableClass (entity UDP) = Log  AND  Application
        // (model UDP) = UYG547. The entity term reads directly; the model term falls
        // through to the model root.
        var entity = new PartialEntity(new Dictionary<string, object>
        {
            ["Entity.Physical.TableClass"] = "Log",
        });
        NamingValidationEngine.ModelRootProvider =
            () => new FakeRoot(new Dictionary<string, object> { ["Model.Physical.Application"] = "UYG547 | Kurumsal IBMB" });

        var rule = TableRule(
            Udp(0, null, "TableClass", "Log"),
            Udp(1, "AND", "Application", "UYG547 | Kurumsal IBMB"));

        NamingValidationEngine.IsRuleApplicable(rule, "Table", entity).Should().BeTrue();
    }

    [Fact]
    public void Mixed_entity_udp_AND_model_udp_fails_when_entity_term_wrong()
    {
        // TableClass = History (not Log) -> AND short-circuits false even though the
        // model term would match. Confirms the fold still gates on the entity term.
        var entity = new PartialEntity(new Dictionary<string, object>
        {
            ["Entity.Physical.TableClass"] = "History",
        });
        NamingValidationEngine.ModelRootProvider =
            () => new FakeRoot(new Dictionary<string, object> { ["Model.Physical.Application"] = "UYG547 | Kurumsal IBMB" });

        var rule = TableRule(
            Udp(0, null, "TableClass", "Log"),
            Udp(1, "AND", "Application", "UYG547 | Kurumsal IBMB"));

        NamingValidationEngine.IsRuleApplicable(rule, "Table", entity).Should().BeFalse();
    }
}
