#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>Why a <see cref="ApprovalBlockingIssue"/> refuses an approval submission.</summary>
    public enum ApprovalBlockingIssueKind
    {
        /// <summary>A blocking rule was evaluated against an object and FAILED.</summary>
        Violation,

        /// <summary>
        /// A blocking rule could not be evaluated at all (malformed payload, unmappable
        /// object type, unreachable data). Per the feature contract this is a HARD
        /// failure - an unevaluatable gate is reported, never treated as a pass.
        /// </summary>
        Unevaluatable,
    }

    /// <summary>One reason the Integrate / Promote submission is refused.</summary>
    public sealed class ApprovalBlockingIssue
    {
        public ApprovalBlockingIssue(
            ApprovalBlockingIssueKind kind,
            int ruleId,
            string objectType,
            string objectName,
            string propertyLabel,
            string ruleSummary,
            string reason)
        {
            Kind = kind;
            RuleId = ruleId;
            ObjectType = objectType ?? "";
            ObjectName = objectName ?? "";
            PropertyLabel = propertyLabel ?? "";
            RuleSummary = ruleSummary ?? "";
            Reason = reason ?? "";
        }

        public ApprovalBlockingIssueKind Kind { get; }

        /// <summary>MC_NAMING_STANDARD.ID, or 0 for an issue that belongs to no single rule.</summary>
        public int RuleId { get; }

        /// <summary>Admin OBJECT_TYPE the rule targets ("TABLE", "COLUMN", ...).</summary>
        public string ObjectType { get; }

        /// <summary>The offending object ("CUSTOMER", "CUSTOMER.ID"); empty for rule-level issues.</summary>
        public string ObjectName { get; }

        /// <summary>Friendly property label ("Name", "Comment", "Owner"); empty for object-level rules.</summary>
        public string PropertyLabel { get; }

        /// <summary>Short rendering of the rule payload ("Prefix 'VP_'", "Length &lt;= 30").</summary>
        public string RuleSummary { get; }

        /// <summary>
        /// The admin-authored ERROR_MESSAGE verbatim for a violation (may be Turkish and
        /// must never be translated or reworded), or a code-built English explanation for
        /// an <see cref="ApprovalBlockingIssueKind.Unevaluatable"/> issue.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// One-line rendering for the Debug Log and for any status text that has to carry
        /// the detail where the report dialog is not shown.
        /// </summary>
        public string ToReportLine()
            => $"{Kind} rule#{RuleId} [{RuleSummary}] {ObjectType}" +
               (string.IsNullOrEmpty(PropertyLabel) ? "" : "." + PropertyLabel) +
               (string.IsNullOrEmpty(ObjectName) ? "" : $" on '{ObjectName}'") +
               $": {Reason}";
    }

    /// <summary>
    /// Verdict of ONE blocking rule against ONE model object - the gate's atomic decision,
    /// factored out of the SCAPI walk so it is unit-testable against a fake object.
    /// </summary>
    public sealed class ApprovalRuleObjectVerdict
    {
        private ApprovalRuleObjectVerdict(bool applicable, IReadOnlyList<string> violations, string? evaluationError)
        {
            Applicable = applicable;
            Violations = violations ?? new List<string>();
            EvaluationError = evaluationError;
        }

        /// <summary>The rule's DEPENDS_ON conditions matched this object.</summary>
        public bool Applicable { get; }

        /// <summary>Violation messages (admin ERROR_MESSAGE verbatim when authored).</summary>
        public IReadOnlyList<string> Violations { get; }

        /// <summary>Non-null when the rule could not be evaluated at all against this object.</summary>
        public string? EvaluationError { get; }

        public bool HasEvaluationError => EvaluationError != null;

        internal static ApprovalRuleObjectVerdict Skipped()
            => new ApprovalRuleObjectVerdict(false, new List<string>(), null);

        internal static ApprovalRuleObjectVerdict Checked(IReadOnlyList<string> violations)
            => new ApprovalRuleObjectVerdict(true, violations, null);

        internal static ApprovalRuleObjectVerdict Failed(string error)
            => new ApprovalRuleObjectVerdict(true, new List<string>(), error);
    }

    /// <summary>Outcome of one <see cref="ApprovalBlockingRuleGate.Evaluate"/> run.</summary>
    public sealed class ApprovalBlockingGateResult
    {
        internal ApprovalBlockingGateResult(
            bool enforced,
            IReadOnlyList<ApprovalBlockingIssue> issues,
            int rulesChecked,
            int objectsInspected,
            int suppressedIssueCount)
        {
            Enforced = enforced;
            Issues = issues ?? new List<ApprovalBlockingIssue>();
            RulesChecked = rulesChecked;
            ObjectsInspected = objectsInspected;
            SuppressedIssueCount = suppressedIssueCount;
        }

        /// <summary>False when ENFORCE_APPROVAL_BLOCKING_RULES resolved to off - the gate did nothing.</summary>
        public bool Enforced { get; }

        /// <summary>Every reason the submission is refused. Empty means "go ahead".</summary>
        public IReadOnlyList<ApprovalBlockingIssue> Issues { get; }

        /// <summary>The request must NOT be submitted.</summary>
        public bool Blocked => Issues.Count > 0;

        public int RulesChecked { get; }
        public int ObjectsInspected { get; }

        /// <summary>
        /// Issues beyond <see cref="ApprovalBlockingRuleGate.MaxReportedIssues"/> that were
        /// dropped from <see cref="Issues"/>. Always &gt; 0 only alongside a full report -
        /// the cap is surfaced in the log and the dialog, never silently applied.
        /// </summary>
        public int SuppressedIssueCount { get; }

        internal static ApprovalBlockingGateResult NotEnforced()
            => new ApprovalBlockingGateResult(false, new List<ApprovalBlockingIssue>(), 0, 0, 0);
    }

    /// <summary>
    /// Gate on ENTRY TO THE APPROVAL QUEUE: refuses to submit an Integrate or Promote
    /// request while any naming rule flagged
    /// <c>MC_NAMING_STANDARD.BLOCKS_DDL_APPROVEMENT = 1</c> is violated in the open model.
    /// Armed per config by the two-level <c>ENFORCE_APPROVAL_BLOCKING_RULES</c> toggle
    /// (CONFIG_PROPERTY override -&gt; CORPORATE_PROPERTY default -&gt; built-in false).
    ///
    /// <para><b>DDL generation is NOT gated</b> (admin contract 2026-07-25, superseding the
    /// earlier BLOCKS_DDL_GENERATION design). The modeler may generate and review DDL for a
    /// violating model all day; what they cannot do is push it into the governance queue.
    /// That is why the gate hangs off the SUBMIT actions, not off Generate DDL.</para>
    ///
    /// <para><b>Independent of the approval mechanism.</b> The gate never consults
    /// USE_APPROVEMENT_MECHANISM (a flag being retired): every submission is checked,
    /// whether it ends up Pending, auto-approved or as a promotion request. A rule the
    /// admin marked as blocking must block regardless of who signs off afterwards.</para>
    ///
    /// <para><b>Check only, never fix.</b> The gate performs no SCAPI writes and never
    /// auto-applies a rule - it reads the model, reports, and the caller aborts.</para>
    ///
    /// <para><b>No silent pass.</b> Every path that cannot reach a verdict produces an
    /// <see cref="ApprovalBlockingIssueKind.Unevaluatable"/> issue and blocks: an unreadable
    /// toggle, a ruleset that will not load, a blocking rule the loader never surfaced, a
    /// malformed rule payload (uncompilable regex, empty affix, missing length operand),
    /// an object type with no SCAPI class, and a failed model walk. The one deliberate
    /// exception is a failed read of the rule's TARGET property on one object: that is
    /// treated as an empty value, exactly as the rest of the engine does
    /// (<c>NamingValidationEngine.ReadBuiltinPropertyValue</c>), so a property erwin
    /// simply has not materialised yet (a new table's Name_Qualifier) does not fabricate
    /// a block. The rule's own Required semantics then decide.</para>
    ///
    /// <para><b>APPLY_ON is ignored</b> (decision confirmed 2026-07-25). A submission check
    /// is a whole-model assertion with no per-object create/update event, so there is no
    /// honest <c>isNew</c> to pass. Passing <c>false</c> would make an
    /// <c>APPLY_ON='Create'</c> rule flagged as blocking incapable of ever blocking - a
    /// permanent silent pass wearing the appearance of enforcement, which is precisely
    /// what this feature forbids. The same reasoning already governs the model-wide
    /// object-existence check (<c>TableTypeMonitorService.CheckRequiredObjectTypesExist</c>).
    /// The trade-off - a Create-scoped blocking rule also gates legacy objects that
    /// predate it - is accepted because the gate is opt-in twice: per rule
    /// (BLOCKS_DDL_APPROVEMENT) and per config (ENFORCE_APPROVAL_BLOCKING_RULES).</para>
    ///
    /// <para><b>Threading.</b> SCAPI is STA/UI-thread bound, so <see cref="Evaluate"/> is
    /// fully synchronous and pumps no message loop: WinForms timers cannot re-enter SCAPI
    /// mid-walk (they only deliver WM_TIMER when the loop runs), which is what keeps this
    /// clear of the documented alter-wizard reentrancy/GDI-corruption class. Callers must
    /// not wrap it in Task.Run - a cross-apartment marshal into erwin's RCWs crashes.</para>
    /// </summary>
    public static class ApprovalBlockingRuleGate
    {
        /// <summary>Two-level config key that arms the gate. Admin: Approval Workflow screen.</summary>
        public const string SettingKey = "ENFORCE_APPROVAL_BLOCKING_RULES";

        /// <summary>Debug-log prefix for every line this gate emits.</summary>
        public const string LogPrefix = "[APPROVAL-GATE]";

        /// <summary>
        /// Upper bound on reported issues. A model that violates a rule on every column
        /// would otherwise produce thousands of rows - unusable in the dialog and a flood
        /// in the log. The overflow count is reported (never silently dropped) via
        /// <see cref="ApprovalBlockingGateResult.SuppressedIssueCount"/>.
        /// </summary>
        public const int MaxReportedIssues = 200;

        // Pseudo "SCAPI class" for MODEL rules: the target is the model root itself, not
        // a Collect result. ScapiCollectTypeForExistence returns null for MODEL because a
        // model always exists, so the gate resolves it before consulting that map.
        private const string ModelPseudoClass = "Model";

        // Field delimiter for the dedupe key. A vertical bar would be legal inside an admin
        // ERROR_MESSAGE (they routinely contain punctuation); the ASCII unit separator is
        // not producible through the admin UI, so key fields stay unambiguous.
        private const string KeySeparator = "\u001F";

        #region Pure helpers (no SCAPI, no DB - unit tested)

        /// <summary>
        /// Normalised admin OBJECT_TYPE: trimmed, upper-cased, spaces folded to
        /// underscores, so "Subject Area" / "SUBJECT_AREA" compare equal.
        /// </summary>
        public static string NormalizeObjectType(string? objectType)
            => objectType?.Trim().ToUpperInvariant().Replace(' ', '_') ?? "";

        /// <summary>True when the rule targets the model root itself.</summary>
        public static bool IsModelObjectType(string? objectType)
            => NormalizeObjectType(objectType) == "MODEL";

        /// <summary>
        /// True when the rule targets the table-owned PRIMARY KEY. PK shares the
        /// Key_Group SCAPI class with indexes and alternate keys, so every PK walk must
        /// additionally filter <c>Key_Group_Type == "PK"</c>.
        /// </summary>
        public static bool IsPrimaryKeyObjectType(string? objectType)
            => NormalizeObjectType(objectType) == "PRIMARY_KEY";

        /// <summary>
        /// SCAPI class to walk for an admin OBJECT_TYPE, or null when the type cannot be
        /// mapped (which the gate reports as unevaluatable). Delegates to the existing
        /// <c>TableTypeMonitorService.ScapiCollectTypeForExistence</c> map rather than
        /// introducing a fourth object-type mapping to the three that already disagree.
        /// </summary>
        public static string? ScapiClassFor(string? objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return null;
            if (IsModelObjectType(objectType)) return ModelPseudoClass;
            return TableTypeMonitorService.ScapiCollectTypeForExistence(objectType);
        }

        /// <summary>Short human rendering of a rule's payload, for the report and the log.</summary>
        public static string DescribeRule(NamingStandardRule? rule)
        {
            if (rule == null) return "";
            switch (rule.RuleType)
            {
                case NamingRuleKind.Prefix: return $"Prefix '{rule.Prefix}'";
                case NamingRuleKind.Suffix: return $"Suffix '{rule.Suffix}'";
                case NamingRuleKind.Length: return $"Length {rule.LengthOperator} {rule.LengthValue}";
                case NamingRuleKind.Regexp: return $"Regexp '{rule.RegexpPattern}'";
                case NamingRuleKind.Required:
                    return string.IsNullOrEmpty(rule.PropertyCode) ? "Required (object must exist)" : "Required";
                case NamingRuleKind.Template: return "Template";
                default: return rule.RuleType.ToString();
            }
        }

        /// <summary>
        /// Structural preflight: why this rule can NEVER produce a verdict, or null when
        /// it is evaluable. Runs once per rule, touches no SCAPI and no DB, and catches
        /// exactly the cases the evaluation engine silently swallows today (an
        /// uncompilable regex is logged and emits no violation; an unrecognised length
        /// operator falls through to "ok"). Under the gate's no-silent-pass contract each
        /// of those must block loudly instead.
        /// </summary>
        public static string? GetUnevaluatableReason(NamingStandardRule? rule)
        {
            if (rule == null) return "Rule could not be read.";

            if (rule.RuleType == NamingRuleKind.Template)
                return "Template rules generate a value; they are not validators and can never be checked. " +
                       "Clear \"Blocks DDL Approvement\" on this rule in Rule Management.";

            if (string.IsNullOrWhiteSpace(rule.ObjectType))
                return "Rule has no object type.";

            if (ScapiClassFor(rule.ObjectType) == null)
                return $"Object type '{rule.ObjectType}' has no erwin object class the add-in can inspect.";

            // DANGLING condition source: the row names a UDP / property FK whose LEFT JOIN
            // came back with no name, i.e. the gating definition was deleted in admin while
            // the condition row survived. The engine treats such a term as "does not match"
            // (NamingValidationEngine.MatchSingleCondition), so the rule would apply to
            // NOTHING and pass silently - indistinguishable from a clean model. Structural,
            // so it is caught here once per rule instead of per object.
            foreach (var condition in rule.Conditions)
            {
                if (condition == null) continue;
                bool namesUdp = condition.DependsOnUdpId.HasValue;
                bool namesProperty = condition.DependsOnPropertyDefId.HasValue;

                if (namesUdp && string.IsNullOrEmpty(condition.DependsOnUdpName))
                    return $"The rule's condition references user-defined property #{condition.DependsOnUdpId} " +
                           "which no longer exists, so the rule can never match anything.";

                if (namesProperty && string.IsNullOrEmpty(condition.DependsOnPropertyCode))
                    return $"The rule's condition references property #{condition.DependsOnPropertyDefId} " +
                           "which no longer exists, so the rule can never match anything.";
            }

            // Object-type-only rule (Property "(none)") = "an object of this type must
            // exist". Only the Required type carries that meaning.
            if (string.IsNullOrEmpty(rule.PropertyCode))
            {
                return rule.RuleType == NamingRuleKind.Required
                    ? null
                    : $"Rule targets no property, which only the Required type supports (this rule is {rule.RuleType}).";
            }

            switch (rule.RuleType)
            {
                case NamingRuleKind.Prefix:
                    if (string.IsNullOrEmpty(rule.Prefix))
                        return "Prefix rule has no prefix value.";
                    break;

                case NamingRuleKind.Suffix:
                    if (string.IsNullOrEmpty(rule.Suffix))
                        return "Suffix rule has no suffix value.";
                    break;

                case NamingRuleKind.Length:
                    if (string.IsNullOrEmpty(rule.LengthOperator) || !rule.LengthValue.HasValue)
                        return "Length rule has no operator / length value.";
                    if (!IsSupportedLengthOperator(rule.LengthOperator))
                        return $"Length rule uses an unsupported operator '{rule.LengthOperator}'.";
                    break;

                case NamingRuleKind.Regexp:
                    if (string.IsNullOrEmpty(rule.RegexpPattern))
                        return "Regexp rule has no pattern.";
                    try
                    {
                        _ = Regex.IsMatch(string.Empty, rule.RegexpPattern);
                    }
                    catch (ArgumentException ex)
                    {
                        return $"Regexp rule has an invalid pattern: {ex.Message}";
                    }
                    break;

                case NamingRuleKind.Required:
                    break; // non-empty check needs no payload

                default:
                    // This preflight exists to mirror the engine's silent swallows, and the
                    // engine's own switch ends in a silent default-skip. Without this arm a
                    // seventh rule kind would pass the preflight, emit no violation, and
                    // become a new permanent silent pass. Fail until it is implemented in
                    // BOTH places.
                    return $"Rule type '{rule.RuleType}' is not supported by the DDL gate.";
            }

            return null;
        }

        /// <summary>Operators <c>NamingValidationEngine.EvaluateRule</c> actually implements.</summary>
        public static bool IsSupportedLengthOperator(string? op)
        {
            switch (op)
            {
                case ">=":
                case "<=":
                case ">":
                case "<":
                case "=":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Collapses duplicate issues (same rule, object, property and reason) preserving
        /// first-seen order. A rule can be reached twice for one object when the admin
        /// authored overlapping conditions; the user should see it once.
        /// </summary>
        public static IReadOnlyList<ApprovalBlockingIssue> Dedupe(IEnumerable<ApprovalBlockingIssue>? issues)
        {
            var result = new List<ApprovalBlockingIssue>();
            if (issues == null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var issue in issues)
            {
                if (issue == null) continue;
                // Fields are joined with an explicit separator that cannot occur in an
                // object name or an admin message. Plain concatenation would let
                // distinct issues collide (rule 1 on "2TABLE" vs rule 12 on "TABLE") and a
                // real violation would then vanish from the report.
                string key = string.Join(KeySeparator,
                    issue.RuleId, issue.ObjectType, issue.ObjectName, issue.PropertyLabel, issue.Reason);
                if (seen.Add(key)) result.Add(issue);
            }
            return result;
        }

        #endregion

        /// <summary>
        /// Runs the gate against the open model. Never throws: any failure becomes an
        /// <see cref="ApprovalBlockingIssueKind.Unevaluatable"/> issue so the caller blocks
        /// with an actionable message instead of generating DDL on unverified ground.
        /// </summary>
        /// <param name="session">The add-in's live SCAPI session.</param>
        /// <param name="log">Debug-log sink; may be null.</param>
        public static ApprovalBlockingGateResult Evaluate(dynamic session, Action<string>? log)
        {
            void Write(string message)
            {
                try { log?.Invoke($"{LogPrefix} {message}"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"{LogPrefix} log sink threw: {ex.Message}"); }
            }

            var ctx = ConfigContextService.Instance;

            // 1. Is the gate armed for this config? A READ FAILURE blocks: without the
            //    toggle we cannot know whether the admin wanted DDL gated, and guessing
            //    "off" would silently skip a gate they explicitly turned on. Deliberately
            //    unlike ApplyDdlGenerationGates' fail-open, whose only risk is which
            //    source radio is visible.
            bool enforce;
            try
            {
                if (!ctx.IsInitialized)
                    return Blocked(Rule0(
                        "No configuration is resolved for the active model, so the blocking rules cannot be checked."),
                        Write);

                enforce = ctx.GetEffectiveBool(SettingKey, false);
            }
            catch (Exception ex)
            {
                return Blocked(Rule0(
                    $"The \"{SettingKey}\" setting could not be read from the repository ({ex.Message}). " +
                    "Submission is blocked until the repository is reachable."),
                    Write);
            }

            if (!enforce)
            {
                Write($"{SettingKey} is off for config {ctx.ActiveConfigId} - no gate.");
                return ApprovalBlockingGateResult.NotEnforced();
            }

            // 2. Which rules gate DDL? Isolated query - see LoadApprovalBlockingRules.
            ApprovalBlockingRuleSet ruleSet;
            try
            {
                ruleSet = NamingStandardService.Instance.LoadApprovalBlockingRules(ctx.ActiveConfigId);
            }
            catch (Exception ex)
            {
                return Blocked(Rule0(
                    $"The blocking rules could not be loaded from the repository ({ex.Message}). " +
                    "Submission is blocked until they can be read."),
                    Write);
            }

            var issues = new List<ApprovalBlockingIssue>();

            // A rule the DB flags as blocking but the add-in never loaded can never be
            // checked - report it rather than quietly generating DDL past it.
            foreach (int unresolvedId in ruleSet.UnresolvedRuleIds)
            {
                issues.Add(new ApprovalBlockingIssue(
                    ApprovalBlockingIssueKind.Unevaluatable, unresolvedId, "", "", "", "",
                    "This rule is marked as blocking approval submission but the add-in could not load it " +
                    "(it may be inactive, scoped to another DBMS version, or reference a deleted property)."));
            }

            if (ruleSet.Rules.Count == 0)
            {
                Write($"{SettingKey} is ON for config {ctx.ActiveConfigId} but no evaluable blocking rule is defined.");
                return Finish(true, issues, 0, 0, Write);
            }

            // 3. Structural preflight, once per rule, before any COM traffic.
            var evaluable = new List<NamingStandardRule>();
            foreach (var rule in ruleSet.Rules)
            {
                string? reason = GetUnevaluatableReason(rule);
                if (reason == null) { evaluable.Add(rule); continue; }

                issues.Add(new ApprovalBlockingIssue(
                    ApprovalBlockingIssueKind.Unevaluatable, rule.Id, rule.ObjectType, "",
                    NamingValidationEngine.FriendlyPropertyLabel(rule.PropertyCode),
                    DescribeRule(rule), reason));
            }

            // 4. Walk the model once per object type, evaluating that type's rules.
            int objectsInspected = 0;
            if (evaluable.Count > 0)
            {
                dynamic modelObjects;
                dynamic root;
                try
                {
                    modelObjects = session.ModelObjects;
                    root = modelObjects.Root;
                }
                catch (Exception ex)
                {
                    issues.Add(Rule0($"The open model could not be read ({ex.Message}); the blocking rules were not checked."));
                    return Finish(true, issues, evaluable.Count, 0, Write);
                }

                if (root == null)
                {
                    issues.Add(Rule0("The open model has no root object; the blocking rules were not checked."));
                    return Finish(true, issues, evaluable.Count, 0, Write);
                }

                // Box once so every helper call below binds statically (see GateObject.Instance).
                object modelObjectsRef = modelObjects;
                object rootRef = root;

                // MODEL-scoped UDP conditions resolve through NamingValidationEngine's
                // ModelRootProvider. Production assigns it in ValidationCoordinatorService
                // .StartMonitoring - which the DDL-generator flavor NEVER runs (it skips
                // every validation surface). Without this, a blocking rule conditioned on a
                // model UDP reads empty, never applies, and silently passes in exactly the
                // unattended build, while blocking correctly in the interactive one. Only
                // filled when absent, so the interactive build's own provider always wins;
                // restored in the finally so the gate leaves no global state behind.
                var previousRootProvider = NamingValidationEngine.ModelRootProvider;
                if (previousRootProvider == null)
                {
                    NamingValidationEngine.ModelRootProvider = () => rootRef;
                    Write("model-root provider was unset (DDL-only flavor) - supplied for this run " +
                          "so MODEL-scoped UDP conditions resolve.");
                }

                try
                {
                    foreach (var group in evaluable.GroupBy(r => NormalizeObjectType(r.ObjectType)))
                    {
                        objectsInspected += EvaluateObjectTypeGroup(
                            modelObjectsRef, rootRef, group.Key, group.ToList(), issues, Write);
                    }
                }
                finally
                {
                    NamingValidationEngine.ModelRootProvider = previousRootProvider;
                }
            }

            return Finish(true, issues, evaluable.Count, objectsInspected, Write);
        }

        #region Per-object-type evaluation

        /// <summary>
        /// Evaluates every blocking rule of one admin OBJECT_TYPE. Returns the number of
        /// model objects inspected. A failure to enumerate the type is reported as
        /// unevaluatable for each of its rules - the model cannot be declared clean for a
        /// class we could not walk.
        /// </summary>
        private static int EvaluateObjectTypeGroup(
            object modelObjects,
            object root,
            string normalizedObjectType,
            List<NamingStandardRule> rules,
            List<ApprovalBlockingIssue> issues,
            Action<string> write)
        {
            // Existence rules ("an object of this type must exist") carry no property and
            // are invisible to the per-property path, so they are handled first.
            var existenceRules = rules.Where(r => string.IsNullOrEmpty(r.PropertyCode)).ToList();
            var propertyRules = rules.Where(r => !string.IsNullOrEmpty(r.PropertyCode)).ToList();

            List<GateObject> objects;
            try
            {
                objects = CollectObjects(modelObjects, root, normalizedObjectType, propertyRules);
            }
            catch (Exception ex)
            {
                foreach (var rule in rules)
                {
                    issues.Add(new ApprovalBlockingIssue(
                        ApprovalBlockingIssueKind.Unevaluatable, rule.Id, rule.ObjectType, "",
                        NamingValidationEngine.FriendlyPropertyLabel(rule.PropertyCode),
                        DescribeRule(rule),
                        $"The model's {rule.ObjectType} objects could not be read ({ex.Message}), so this rule could not be checked."));
                }
                return 0;
            }

            foreach (var rule in existenceRules)
                EvaluateExistenceRule(modelObjects, root, rule, normalizedObjectType, objects, issues, write);

            foreach (var rule in propertyRules)
                EvaluatePropertyRule(rule, objects, issues, write);

            return objects.Count;
        }

        /// <summary>
        /// "At least one object of this type must exist." PRIMARY KEY is the exception:
        /// a model-wide "some PK exists somewhere" assertion is meaningless for a
        /// table-owned object, so it is enforced PER TABLE (every entity must own a PK
        /// key group with at least one member) - the same semantic
        /// <c>TableTypeMonitorService.CheckTablePrimaryKeyRequired</c> already uses.
        /// MODEL always exists, so a MODEL existence rule is trivially satisfied.
        /// </summary>
        private static void EvaluateExistenceRule(
            object modelObjects,
            object root,
            NamingStandardRule rule,
            string normalizedObjectType,
            List<GateObject> collected,
            List<ApprovalBlockingIssue> issues,
            Action<string> write)
        {
            string message = !string.IsNullOrEmpty(rule.ErrorMessage)
                ? rule.ErrorMessage
                : $"At least one {rule.ObjectType} must exist in the model.";

            if (normalizedObjectType == "MODEL")
            {
                write($"rule#{rule.Id} existence MODEL - always satisfied.");
                return;
            }

            if (normalizedObjectType == "PRIMARY_KEY")
            {
                List<GateObject> entities;
                try
                {
                    entities = CollectObjects(modelObjects, root, "TABLE", new List<NamingStandardRule>());
                }
                catch (Exception ex)
                {
                    issues.Add(new ApprovalBlockingIssue(
                        ApprovalBlockingIssueKind.Unevaluatable, rule.Id, rule.ObjectType, "", "", DescribeRule(rule),
                        $"The model's tables could not be read ({ex.Message}), so the primary-key requirement could not be checked."));
                    return;
                }

                foreach (var entity in entities)
                {
                    bool hasPk;
                    try { hasPk = HasUsablePrimaryKey(modelObjects, entity.Instance); }
                    catch (Exception ex)
                    {
                        issues.Add(new ApprovalBlockingIssue(
                            ApprovalBlockingIssueKind.Unevaluatable, rule.Id, rule.ObjectType, entity.Display, "",
                            DescribeRule(rule),
                            $"The table's primary key could not be read ({ex.Message})."));
                        continue;
                    }

                    if (!hasPk)
                    {
                        issues.Add(new ApprovalBlockingIssue(
                            ApprovalBlockingIssueKind.Violation, rule.Id, rule.ObjectType, entity.Display, "",
                            DescribeRule(rule), message));
                    }
                }
                return;
            }

            if (collected.Count == 0)
            {
                issues.Add(new ApprovalBlockingIssue(
                    ApprovalBlockingIssueKind.Violation, rule.Id, rule.ObjectType, "", "", DescribeRule(rule), message));
            }
        }

        /// <summary>
        /// Evaluates one property-targeted blocking rule against every object of its type.
        /// Reuses the shared engine (condition fold + per-kind check + the canonical-affix
        /// false-positive suppression) so the gate's verdict matches interactive
        /// validation exactly; only the rule SUBSET differs.
        /// </summary>
        private static void EvaluatePropertyRule(
            NamingStandardRule rule,
            List<GateObject> objects,
            List<ApprovalBlockingIssue> issues,
            Action<string> write)
        {
            string propertyLabel = NamingValidationEngine.FriendlyPropertyLabel(rule.PropertyCode);
            int violations = 0;
            int applicable = 0;
            int propertyReadFailures = 0;

            foreach (var target in objects)
            {
                // A failed read of the TARGET property is an empty value on THIS object,
                // matching NamingValidationEngine.ReadBuiltinPropertyValue: a property erwin
                // has not materialised yet (a new table's Name_Qualifier) must not fabricate
                // a block. The failure count is aggregated below, where "failed on every
                // object" means something different entirely.
                string value = ReadProperty(target.Instance, rule.PropertyCode, out bool readFailed);
                if (readFailed) propertyReadFailures++;

                var verdict = EvaluateRuleAgainstObject(
                    rule, value, target.Instance, target.PkMembership,
                    trace => write($"rule#{rule.Id} on '{target.Display}': {trace}"));

                if (verdict.HasEvaluationError)
                {
                    issues.Add(new ApprovalBlockingIssue(
                        ApprovalBlockingIssueKind.Unevaluatable, rule.Id, rule.ObjectType, target.Display,
                        propertyLabel, DescribeRule(rule), verdict.EvaluationError!));
                    continue;
                }

                if (verdict.Applicable) applicable++;

                foreach (string message in verdict.Violations)
                {
                    issues.Add(new ApprovalBlockingIssue(
                        ApprovalBlockingIssueKind.Violation, rule.Id, rule.ObjectType, target.Display,
                        propertyLabel, DescribeRule(rule), message));
                    violations++;
                }
            }

            // The per-object leniency above is scoped to "erwin has not materialised this
            // property on this object yet". When the read failed on EVERY object the cause
            // is categorically different - the admin's PROPERTY_CODE is not valid for this
            // object class at all - and every check then short-circuits on an empty value,
            // reporting a perfectly clean model. That is the silent pass the gate exists to
            // prevent, so it is raised once for the rule.
            if (objects.Count > 0 && propertyReadFailures == objects.Count)
            {
                issues.Add(new ApprovalBlockingIssue(
                    ApprovalBlockingIssueKind.Unevaluatable, rule.Id, rule.ObjectType, "",
                    propertyLabel, DescribeRule(rule),
                    $"Property '{rule.PropertyCode}' could not be read on any of the {objects.Count} " +
                    $"{rule.ObjectType} object(s), so this rule could not be checked."));
            }

            // Applicable count is logged separately from the object count: without it
            // "checked 250 object(s) -> 0 violation(s)" reads identically whether 250
            // objects were verified clean or the rule's condition matched none of them.
            write($"rule#{rule.Id} [{DescribeRule(rule)}] {rule.ObjectType}.{rule.PropertyCode} " +
                  $"checked {objects.Count} object(s), {applicable} applicable, " +
                  $"{propertyReadFailures} unreadable -> {violations} violation(s).");
        }

        /// <summary>
        /// The gate's atomic decision: does <paramref name="rule"/> flag
        /// <paramref name="value"/> on <paramref name="scapiObject"/>?
        ///
        /// <para>Deliberately reuses the shared engine so a gate verdict and an
        /// interactive-validation verdict can never drift: condition fold via
        /// <c>IsRuleApplicable</c>, per-kind check via <c>EvaluateRule</c>, and the same
        /// canonical-affix false-positive suppression <c>ValidateObjectName</c> applies.
        /// The one thing it does NOT reuse is <c>ValidateObjectName</c> itself, which
        /// evaluates EVERY rule on the property - the gate must report only the rules the
        /// admin marked as blocking.</para>
        ///
        /// <para><b>APPLY_ON is not consulted</b> - see the class remarks. Every blocking
        /// rule is evaluated against every object of its type regardless of Create/Update
        /// scoping, because a submission check has no per-object lifecycle event to test.</para>
        ///
        /// <para>Pure with respect to the add-in's own state: it reads
        /// <paramref name="scapiObject"/> and the loaded rule cache, and writes nothing.</para>
        /// </summary>
        /// <param name="rule">The blocking rule. Assumed to have passed <see cref="GetUnevaluatableReason"/>.</param>
        /// <param name="value">Current value of the rule's target property.</param>
        /// <param name="scapiObject">The model object, for DEPENDS_ON condition reads.</param>
        /// <param name="pkMembership">Caller-resolved "column is in the PK" for rules that
        /// condition on it; erwin exposes no readable Attribute property for it.</param>
        /// <param name="trace">Optional diagnostic sink.</param>
        public static ApprovalRuleObjectVerdict EvaluateRuleAgainstObject(
            NamingStandardRule rule,
            string value,
            object scapiObject,
            bool? pkMembership = null,
            Action<string>? trace = null)
        {
            if (rule == null) return ApprovalRuleObjectVerdict.Failed("Rule could not be read.");
            value ??= "";

            bool applicable;
            try
            {
                // Conditions (MC_NAMING_RULE_CONDITION), folded by the shared engine.
                // NOTE: the engine swallows its own condition-source read failures and
                // returns "" (ReadUdpValue / ReadBuiltinPropertyValue), so this catch is a
                // belt for a future engine that propagates - it is NOT what protects the
                // gate from an unreadable condition. The deterministic causes are handled
                // structurally instead: a dangling UDP/property FK is rejected in
                // GetUnevaluatableReason, and the missing model-root provider is filled in
                // by Evaluate. What remains - a condition UDP that exists in admin but is
                // simply not populated on this object - is indistinguishable from "the
                // value does not match" BY DESIGN of the shared engine, and treating it as
                // a hard failure would block every submission for models where a gating UDP is
                // unset. The per-rule applicable count is logged so a rule that matched
                // nothing is visible rather than hidden behind "0 violations".
                applicable = NamingValidationEngine.IsRuleApplicable(
                    rule, rule.ObjectType, scapiObject, pkMembership);
            }
            catch (Exception ex)
            {
                return ApprovalRuleObjectVerdict.Failed($"The rule's condition could not be evaluated ({ex.Message}).");
            }

            if (!applicable) return ApprovalRuleObjectVerdict.Skipped();

            List<NamingValidationResult> results;
            try
            {
                results = NamingValidationEngine.EvaluateRule(rule, value);
            }
            catch (Exception ex)
            {
                return ApprovalRuleObjectVerdict.Failed($"The rule could not be evaluated against '{value}' ({ex.Message}).");
            }

            var messages = new List<string>();
            foreach (var failure in results.Where(r => r != null && !r.IsValid))
            {
                if (IsPositionalAffixFalsePositive(rule, value, scapiObject, trace)) continue;

                messages.Add(!string.IsNullOrEmpty(failure.ErrorMessage)
                    ? failure.ErrorMessage
                    : "Value does not satisfy the rule.");
            }

            return ApprovalRuleObjectVerdict.Checked(messages);
        }

        /// <summary>
        /// Mirrors the canonical-affix acceptance in
        /// <c>NamingValidationEngine.ValidateObjectName</c>: with two prefix rules on one
        /// property the composed name carries both, but each rule's StartsWith check can
        /// only ever pass for the outermost one, so the inner rule reports an
        /// unsatisfiable violation. A name the full canonical apply would leave UNCHANGED
        /// already carries every applicable affix in its slot - not a real violation, and
        /// certainly not grounds to refuse a submission.
        ///
        /// <para><b>Only a canonical computation that actually INCLUDED this rule proves
        /// anything about it.</b> That guard is load-bearing, not defensive:
        /// <c>ApplyNamingStandards</c> builds its own affix set with a DIFFERENT
        /// applicability test than the gate - it filters by <c>MatchesApplyOn(rule, isNew)</c>
        /// and it resolves conditions WITHOUT the caller-supplied PK membership. Whenever
        /// that drops this rule, the canonical form is computed from the remaining rules,
        /// trivially equals the input, and the suppression would swallow a REAL violation:
        /// </para>
        /// <list type="bullet">
        /// <item><description>APPLY_ON='Create' (or 'Update') - the gate ignores APPLY_ON,
        /// so it flags the object, but under the mismatched framing the rule is excluded
        /// from the apply, canonical == value, and every non-'Both' affix rule flagged as
        /// blocking would silently stop blocking.</description></item>
        /// <item><description>A PK-membership condition - the gate knows the column is in
        /// the primary key (resolved from the Key_Group graph), while
        /// <c>ApplyNamingStandards</c> reads it off the Attribute, gets nothing, and drops
        /// the rule.</description></item>
        /// </list>
        /// <para>So each framing is tried only when this rule participates in it under
        /// BOTH of <c>ApplyNamingStandards</c>' own tests. When neither framing qualifies
        /// the violation stands - suppression is a false-positive reducer, never a pass.</para>
        /// </summary>
        private static bool IsPositionalAffixFalsePositive(
            NamingStandardRule rule, string value, object scapiObject, Action<string>? trace)
        {
            if (rule.RuleType != NamingRuleKind.Prefix && rule.RuleType != NamingRuleKind.Suffix) return false;
            if (string.IsNullOrEmpty(value)) return false;

            try
            {
                // ApplyNamingStandards resolves conditions with the 3-arg overload
                // (pkMembership unavailable to it). If THAT test drops the rule, the
                // canonical form below never carried this rule's affix and proves nothing.
                if (!NamingValidationEngine.IsRuleApplicable(rule, rule.ObjectType, scapiObject))
                {
                    trace?.Invoke("canonical-affix check skipped - the apply path does not consider this rule " +
                                  "applicable to this object (violation kept).");
                    return false;
                }

                foreach (bool isNew in new[] { false, true })
                {
                    // The gate ignores APPLY_ON, but ApplyNamingStandards does not: a
                    // framing this rule is excluded from cannot certify its own affix.
                    if (!NamingValidationEngine.MatchesApplyOn(rule, isNew)) continue;

                    string canonical = NamingValidationEngine.ApplyNamingStandards(
                        rule.ObjectType, value, scapiObject,
                        autoOnly: false, propertyCode: rule.PropertyCode, isNew: isNew);

                    if (string.Equals(canonical, value, StringComparison.Ordinal))
                    {
                        trace?.Invoke($"affix violation dropped - '{value}' is already in canonical affix form.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Suppression is an optimisation against false positives, never a pass:
                // if it cannot run, keep the violation.
                trace?.Invoke($"canonical-affix check failed: {ex.Message} (violation kept).");
            }

            return false;
        }

        #endregion

        #region Model walk

        /// <summary>One inspectable model object plus the context the report needs.</summary>
        private sealed class GateObject
        {
            public GateObject(object instance, string display, bool? pkMembership)
            {
                Instance = instance;
                Display = display ?? "";
                PkMembership = pkMembership;
            }

            /// <summary>
            /// The SCAPI object, held as <see cref="object"/> rather than <c>dynamic</c> on
            /// purpose: passing it to the engine's <c>dynamic</c> parameters then binds
            /// STATICALLY at the call site (the same boxing trick
            /// <c>ValidationCoordinatorService.ValidateDatatypeCandidate</c> uses) instead
            /// of routing the whole call through the runtime binder. Member access on it
            /// still goes through a <c>dynamic</c> parameter inside the helpers.
            /// </summary>
            public object Instance { get; }

            /// <summary>Human identity in the report: "CUSTOMER", "CUSTOMER.ID", view name, ...</summary>
            public string Display { get; }

            /// <summary>Resolved PK membership for columns whose rules condition on it; null otherwise.</summary>
            public bool? PkMembership { get; }
        }

        /// <summary>
        /// Materialises every object of one admin OBJECT_TYPE into a managed list BEFORE
        /// any evaluation. Two reasons: the same list is reused by every rule of that type
        /// (one Collect instead of one per rule), and holding a live COM enumerator open
        /// across evaluation is the shape that wedges when erwin mutates the model.
        /// Throws on an enumeration failure so the caller can report it - a class we could
        /// not walk must never read as "clean".
        /// </summary>
        private static List<GateObject> CollectObjects(
            dynamic modelObjects, dynamic root, string normalizedObjectType, List<NamingStandardRule> propertyRules)
        {
            var result = new List<GateObject>();
            // Boxed alias for calls into the sibling helpers, so they bind statically
            // (see GateObject.Instance); `modelObjects` itself stays dynamic for Collect.
            object modelObjectsRef = modelObjects;

            if (normalizedObjectType == "MODEL")
            {
                object rootRef = root;
                result.Add(new GateObject(rootRef, ReadDisplayName(rootRef, preferPhysical: false), null));
                return result;
            }

            if (normalizedObjectType == "COLUMN")
            {
                // Resolving PK membership costs a Key_Group + Key_Group_Member walk per
                // entity, so pay it only when a rule actually conditions on it.
                bool needPk = propertyRules.Any(NamingValidationEngine.IsPkMembershipCondition);

                dynamic entities = modelObjects.Collect(root, "Entity");
                if (entities == null) return result;

                foreach (dynamic entity in entities)
                {
                    if (entity == null) continue;
                    object entityRef = entity;
                    string tableName = ReadDisplayName(entityRef, preferPhysical: true);

                    HashSet<string> pkMembers = needPk
                        ? ReadPrimaryKeyMemberIds(modelObjectsRef, entityRef)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    dynamic attrs;
                    // A single entity whose children cannot be enumerated must not abort
                    // the whole walk, but it must not pass silently either - surface it
                    // as a read failure of that entity.
                    try { attrs = modelObjects.Collect(entity, "Attribute"); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"columns of table '{tableName}' could not be read: {ex.Message}", ex);
                    }
                    if (attrs == null) continue;

                    foreach (dynamic attr in attrs)
                    {
                        if (attr == null) continue;
                        object attrRef = attr;
                        string columnName = ReadDisplayName(attrRef, preferPhysical: true);
                        // ObjectId is read WITHOUT a swallow when PK membership matters: an
                        // unreadable id silently misses every member and reads as "not a PK
                        // column", de-applying the rule. Propagates to the walk-level catch.
                        bool? pk = needPk ? pkMembers.Contains(ReadObjectIdOrThrow(attrRef)) : (bool?)null;
                        result.Add(new GateObject(attrRef, $"{tableName}.{columnName}", pk));
                    }
                }
                return result;
            }

            string? scapiClass = ScapiClassFor(normalizedObjectType);
            if (scapiClass == null)
                throw new InvalidOperationException($"object type '{normalizedObjectType}' has no erwin object class");

            bool pkOnly = IsPrimaryKeyObjectType(normalizedObjectType);
            // r10 SCAPI exposes no Physical_Name on a View - it only has Name, and admin
            // authors View rules against PROPERTY_CODE 'Name' accordingly.
            bool preferPhysical = normalizedObjectType != "VIEW"
                                  && normalizedObjectType != "INDEX"
                                  && normalizedObjectType != "PRIMARY_KEY"
                                  && normalizedObjectType != "SUBJECT_AREA";

            dynamic collection = modelObjects.Collect(root, scapiClass);
            if (collection == null) return result;

            foreach (dynamic obj in collection)
            {
                if (obj == null) continue;
                object objRef = obj;
                if (pkOnly && !IsPrimaryKeyGroup(objRef)) continue;
                result.Add(new GateObject(objRef, ReadDisplayName(objRef, preferPhysical), null));
            }
            return result;
        }

        /// <summary>
        /// True for a Key_Group that is the table's PRIMARY KEY (vs an index / AK).
        /// <para>
        /// THROWS on a failed <c>Key_Group_Type</c> read instead of answering "not a PK".
        /// Swallowing it produced two different wrong answers from one silent failure: a
        /// real PK group read as not-PK makes <see cref="HasUsablePrimaryKey"/> report
        /// "this table has no primary key" (a FALSE block the user cannot act on), and in
        /// the PK-only walk the same failure drops the object from the set entirely (a
        /// silent pass). Both callers already sit inside a try/catch that turns the
        /// exception into an <see cref="ApprovalBlockingIssueKind.Unevaluatable"/> issue, which
        /// is the honest answer.
        /// </para>
        /// </summary>
        private static bool IsPrimaryKeyGroup(dynamic keyGroup)
        {
            string? kgType = keyGroup.Properties("Key_Group_Type").Value?.ToString();
            return string.Equals(kgType, "PK", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the entity owns a USABLE primary key: a Key_Group of type "PK" with
        /// at least one member. erwin auto-creates an EMPTY XPK group before the user
        /// assigns any column, and an empty PK is not a primary key.
        /// </summary>
        private static bool HasUsablePrimaryKey(dynamic modelObjects, dynamic entity)
        {
            dynamic groups = modelObjects.Collect(entity, "Key_Group");
            if (groups == null) return false;

            foreach (dynamic kg in groups)
            {
                if (kg == null || !IsPrimaryKeyGroup(kg)) continue;

                dynamic members = modelObjects.Collect(kg, "Key_Group_Member");
                if (members == null) return false;
                foreach (dynamic m in members)
                    if (m != null) return true;

                return false; // exactly one PK key group per entity
            }
            return false;
        }

        /// <summary>
        /// Attribute_Ref values of the entity's PK members. Computed once per entity so a
        /// PK-conditioned column rule does not re-walk the key graph per column.
        /// <para>
        /// Every read failure PROPAGATES. This is only called when a blocking rule
        /// conditions on PK membership, and there the distinction matters: a swallowed
        /// failure yields an empty member set, every column then resolves to
        /// "not in the primary key", the rule applies to nothing and the gate reports
        /// PASS - a silent pass on a rule the admin marked as blocking. The caller's
        /// walk-level catch turns the exception into an
        /// <see cref="ApprovalBlockingIssueKind.Unevaluatable"/> issue instead.
        /// </para>
        /// </summary>
        private static HashSet<string> ReadPrimaryKeyMemberIds(dynamic modelObjects, dynamic entity)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            dynamic groups = modelObjects.Collect(entity, "Key_Group");
            if (groups == null) return ids;

            foreach (dynamic kg in groups)
            {
                if (kg == null || !IsPrimaryKeyGroup(kg)) continue;

                dynamic members = modelObjects.Collect(kg, "Key_Group_Member");
                if (members != null)
                {
                    foreach (dynamic m in members)
                    {
                        if (m == null) continue;
                        string? memberRef = m.Properties("Attribute_Ref").Value?.ToString();
                        if (!string.IsNullOrEmpty(memberRef)) ids.Add(memberRef);
                    }
                }
                break; // exactly one PK key group per entity
            }
            return ids;
        }

        /// <summary>
        /// Object identity for PK-member matching. Deliberately unguarded - see the call
        /// site: an empty id would quietly match no PK member and de-apply the rule.
        /// </summary>
        private static string ReadObjectIdOrThrow(dynamic obj)
        {
            string id = obj.ObjectId?.ToString() ?? "";
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException("a column returned no ObjectId, so primary-key membership cannot be resolved");
            return id;
        }

        /// <summary>
        /// Best-effort human identity of a model object. Physical_Name can come back as an
        /// unresolved '%'-prefixed macro (and does not exist at all on r10 Views), so every
        /// reader in this codebase falls back to <c>.Name</c> - this one included.
        /// </summary>
        private static string ReadDisplayName(dynamic obj, bool preferPhysical)
        {
            string fallback = "";
            try { fallback = obj.Name?.ToString() ?? ""; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"{LogPrefix} Name read failed: {ex.Message}"); }

            if (!preferPhysical) return fallback;

            try
            {
                string physical = obj.Properties("Physical_Name").Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(physical) && !physical.StartsWith("%", StringComparison.Ordinal))
                    return physical;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{LogPrefix} Physical_Name read failed: {ex.Message}");
            }

            return fallback;
        }

        /// <summary>
        /// Reads the rule's target property. A SCAPI rejection is an empty value, matching
        /// <c>NamingValidationEngine.ReadBuiltinPropertyValue</c> - see the class remarks
        /// for why this one path is deliberately NOT a per-object hard failure.
        /// <paramref name="readFailed"/> lets the caller aggregate: leniency is meant to
        /// cover a property erwin has not materialised on THIS object yet, so a rule whose
        /// property fails to read on EVERY object is a different animal (an admin
        /// PROPERTY_CODE that is not valid for the object class at all) and must not pass
        /// as "checked N objects, 0 violations".
        /// </summary>
        private static string ReadProperty(dynamic obj, string propertyCode, out bool readFailed)
        {
            try
            {
                readFailed = false;
                return obj.Properties(propertyCode).Value?.ToString() ?? "";
            }
            catch
            {
                readFailed = true;
                return "";
            }
        }

        #endregion

        #region Result assembly

        /// <summary>An issue that belongs to the gate itself rather than to one rule.</summary>
        private static ApprovalBlockingIssue Rule0(string reason)
            => new ApprovalBlockingIssue(ApprovalBlockingIssueKind.Unevaluatable, 0, "", "", "", "", reason);

        private static ApprovalBlockingGateResult Blocked(ApprovalBlockingIssue issue, Action<string> write)
            => Finish(true, new List<ApprovalBlockingIssue> { issue }, 0, 0, write);

        private static ApprovalBlockingGateResult Finish(
            bool enforced, List<ApprovalBlockingIssue> issues, int rulesChecked, int objectsInspected, Action<string> write)
        {
            var deduped = Dedupe(issues);

            int suppressed = 0;
            IReadOnlyList<ApprovalBlockingIssue> reported = deduped;
            if (deduped.Count > MaxReportedIssues)
            {
                suppressed = deduped.Count - MaxReportedIssues;
                reported = deduped.Take(MaxReportedIssues).ToList();
                write($"issue list capped at {MaxReportedIssues}; {suppressed} further issue(s) not listed.");
            }

            if (reported.Count == 0)
            {
                write($"PASS - {rulesChecked} blocking rule(s) checked over {objectsInspected} object(s).");
            }
            else
            {
                write($"BLOCKED - {deduped.Count} issue(s) from {rulesChecked} blocking rule(s) over {objectsInspected} object(s).");
                // Log the FULL deduped set, not just the reported slice: the dialog tells
                // the user the overflow is in the Debug Log, so it has to actually be there.
                foreach (var issue in deduped) write("  " + issue.ToReportLine());
            }

            return new ApprovalBlockingGateResult(enforced, reported, rulesChecked, objectsInspected, suppressed);
        }

        #endregion
    }
}
