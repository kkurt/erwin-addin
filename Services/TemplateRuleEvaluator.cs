#nullable enable

using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>What an applier must do with one Template rule.</summary>
    public enum TemplateRuleAction
    {
        /// <summary>Write <see cref="TemplateRuleDecision.RenderedValue"/> to <see cref="TemplateRuleDecision.TargetCode"/>.</summary>
        Write,

        /// <summary>
        /// Do not write. <see cref="TemplateRuleDecision.Reason"/> says why and is
        /// meant for the log - a Template rule that refuses to fire must always be
        /// able to explain itself.
        /// </summary>
        Skip,

        /// <summary>
        /// The target already holds the rendered value. Distinct from
        /// <see cref="Skip"/> so the applier can stay quiet: this is the steady
        /// state of a healthy rule, not a fault worth a log line.
        /// </summary>
        NoChange,
    }

    /// <summary>The outcome of evaluating one Template rule against one object.</summary>
    public sealed class TemplateRuleDecision
    {
        private TemplateRuleDecision(
            TemplateRuleAction action, string targetCode, bool targetsUdp,
            string currentValue, string renderedValue, string reason)
        {
            Action = action;
            TargetCode = targetCode;
            TargetsUdp = targetsUdp;
            CurrentValue = currentValue;
            RenderedValue = renderedValue;
            Reason = reason;
        }

        public TemplateRuleAction Action { get; }

        /// <summary>
        /// Where the value goes: a plain erwin property code, or the full
        /// <c>&lt;OwnerClass&gt;.Physical.&lt;UdpName&gt;</c> path for a UDP target.
        /// Empty when the target could not be determined at all.
        /// </summary>
        public string TargetCode { get; }

        /// <summary>True when <see cref="TargetCode"/> is a UDP path (needs the sparse-storage write helper).</summary>
        public bool TargetsUdp { get; }

        /// <summary>The target's value as read before rendering. "" when never read.</summary>
        public string CurrentValue { get; }

        /// <summary>The rendered value; only meaningful for <see cref="TemplateRuleAction.Write"/>.</summary>
        public string RenderedValue { get; }

        /// <summary>Log-ready English explanation for a <see cref="TemplateRuleAction.Skip"/>; "" otherwise.</summary>
        public string Reason { get; }

        internal static TemplateRuleDecision Skip(string reason, string targetCode = "", bool targetsUdp = false)
            => new TemplateRuleDecision(TemplateRuleAction.Skip, targetCode, targetsUdp, "", "", reason);

        internal static TemplateRuleDecision NoChange(string targetCode, bool targetsUdp, string currentValue)
            => new TemplateRuleDecision(TemplateRuleAction.NoChange, targetCode, targetsUdp, currentValue, currentValue, "");

        internal static TemplateRuleDecision Write(string targetCode, bool targetsUdp, string currentValue, string rendered)
            => new TemplateRuleDecision(TemplateRuleAction.Write, targetCode, targetsUdp, currentValue, rendered, "");
    }

    /// <summary>
    /// The SCAPI-free decision half of applying a <see cref="NamingRuleKind.Template"/>
    /// rule: given a rule and two callbacks that touch the model, decide whether to
    /// write, skip (with a reason), or do nothing.
    ///
    /// <para>Why a separate type: the two existing appliers
    /// (<c>ApplyColumnTemplateRules</c>, <c>ApplyPrimaryKeyRules</c>) each inline
    /// the same seven-step decision alongside their COM walk, transaction handling
    /// and dialogs, which is why neither has a single unit test. Every step below
    /// is a pure function of the rule plus two values, so pulling it out makes the
    /// contract testable without erwin running. The COM half (reading the object,
    /// the transaction, the modal, the self-write baseline) stays with the caller,
    /// which is the only part that legitimately needs a live session.</para>
    ///
    /// <para>NO-FALLBACK, inherited from <see cref="NamingTemplateEngine"/>: an
    /// unresolvable token aborts that rule's write and is reported. A half-rendered
    /// value never reaches the model, and no step here ever guesses a default.</para>
    ///
    /// <para>This type is deliberately NOT yet used by the Column and PRIMARY KEY
    /// appliers. Those two work today and have no test coverage; migrating them in
    /// the same change as a bug fix would mis-attribute any regression. See
    /// tasks/todo.md.</para>
    /// </summary>
    public static class TemplateRuleEvaluator
    {
        /// <summary>
        /// Evaluate one Template rule.
        /// </summary>
        /// <param name="rule">The rule. Must be <see cref="NamingRuleKind.Template"/>
        /// with a non-empty ValueTemplate (both guaranteed by
        /// <see cref="NamingStandardService.GetTemplateRules"/>).</param>
        /// <param name="udpPathPrefix">Owner-class prefix for a UDP target, e.g.
        /// <c>"Model.Physical."</c> / <c>"Attribute.Physical."</c>. Ignored for a
        /// property-targeted rule.</param>
        /// <param name="readTargetValue">Reads the target's current value. Called at
        /// most once, and only after the structural guards have passed - a rule that
        /// can never fire must not cost a SCAPI read.</param>
        /// <param name="renderTemplate">Renders the rule's template; the argument is
        /// the target's current value (what <c>{Current}</c> resolves to). May throw
        /// <see cref="TemplateResolutionException"/>, which is converted into a Skip
        /// carrying the rule's ERROR_MESSAGE.</param>
        /// <param name="currentTokenHint">Object-type-specific example shown when a
        /// self-referential template is refused, e.g. <c>"{Current}_SUFFIX"</c>.</param>
        public static TemplateRuleDecision Evaluate(
            NamingStandardRule rule,
            string udpPathPrefix,
            Func<string, string> readTargetValue,
            Func<string, string> renderTemplate,
            string currentTokenHint)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (readTargetValue == null) throw new ArgumentNullException(nameof(readTargetValue));
            if (renderTemplate == null) throw new ArgumentNullException(nameof(renderTemplate));

            // 1. Target dispatch (migration 9): PROPERTY_DEF_ID XOR TARGET_UDP_ID.
            bool udpTarget = rule.TargetUdpId.HasValue;
            string targetCode;
            if (udpTarget)
            {
                // A rule may only fill a UDP defined for its OWN object type.
                // Writing e.g. a MODEL UDP from a per-column pass would rewrite the
                // model value on every column change. Mismatch = admin data error.
                if (!string.Equals(rule.TargetUdpObjectType, rule.ObjectType, StringComparison.OrdinalIgnoreCase))
                {
                    return TemplateRuleDecision.Skip(
                        $"target UDP '{rule.TargetUdpName}' is defined for object type '{rule.TargetUdpObjectType}' but the rule is for '{rule.ObjectType}'");
                }
                if (string.IsNullOrEmpty(udpPathPrefix))
                    throw new ArgumentException("A UDP-targeted rule needs an owner-class path prefix.", nameof(udpPathPrefix));
                targetCode = udpPathPrefix + rule.TargetUdpName;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rule.PropertyCode))
                {
                    // GetTemplateRules already rejects this shape; re-checked here so
                    // the evaluator is safe on any caller, not just that one.
                    return TemplateRuleDecision.Skip("the rule has neither a target property nor a target UDP");
                }
                targetCode = rule.PropertyCode;
            }

            // 2. Self-referential template: reads the very target it writes. Under
            // FILL_MODE=Always each render feeds its own previous output back in, so
            // the value grows without bound and writes a transaction every pass.
            // {Current} is the converging alternative.
            bool selfReferential = udpTarget
                ? NamingTemplateEngine.ReferencesOwnUdp(rule.ValueTemplate, rule.TargetUdpName)
                : NamingTemplateEngine.ReferencesOwnProperty(rule.ValueTemplate, targetCode);
            if (selfReferential)
            {
                return TemplateRuleDecision.Skip(
                    $"template '{rule.ValueTemplate}' references its own target '{(udpTarget ? rule.TargetUdpName : targetCode)}' (self-referential - would loop). Use {{Current}} for the target's own value (e.g. {currentTokenHint})",
                    targetCode, udpTarget);
            }

            // 3. {Current} + OnlyIfEmpty can never write: the mode only runs on an
            // empty target, and an empty target has nothing to reshape.
            if (NamingTemplateEngine.CurrentTokenConflictsWithFillMode(rule.ValueTemplate, rule.TemplateFillMode))
            {
                return TemplateRuleDecision.Skip(
                    $"template '{rule.ValueTemplate}' uses {{Current}} with TEMPLATE_FILL_MODE=OnlyIfEmpty; that pair can never produce a value",
                    targetCode, udpTarget);
            }

            // 3b. An unrecognised fill mode is a property of the ROW, not of the model,
            // so it belongs with the other structural guards. Checked before the read
            // and the render, both of which would otherwise be spent on a rule that is
            // going to be refused anyway - and, worse, could fail first and report a
            // downstream symptom instead of the real defect.
            NamingTemplateEngine.ShouldWrite(rule.TemplateFillMode, "", out bool unknownMode);
            if (unknownMode)
            {
                return TemplateRuleDecision.Skip(
                    $"unknown TEMPLATE_FILL_MODE '{rule.TemplateFillMode}'", targetCode, udpTarget);
            }

            // 4. Read the target BEFORE rendering: {Current} reshapes this very value,
            // and both the fill mode and the idempotency check need it too.
            string currentVal = readTargetValue(targetCode) ?? "";

            // 5. Render. NO-FALLBACK: an unresolved token aborts this rule only.
            string rendered;
            try
            {
                rendered = renderTemplate(currentVal) ?? "";
            }
            catch (TemplateResolutionException tex)
            {
                // The admin ERROR_MESSAGE may be Turkish; it goes to the log, never
                // to a hardcoded-English UI string. The failing token is appended
                // even when a custom message exists: the message says what the admin
                // wants the modeller to do, the token says which part of the template
                // actually broke, and dropping the second makes the first
                // undiagnosable.
                string msg = !string.IsNullOrWhiteSpace(rule.ErrorMessage)
                    ? $"{rule.ErrorMessage} (token '{{{tex.Token}}}': {tex.Message})"
                    : $"could not resolve token '{{{tex.Token}}}' ({tex.Message})";
                return TemplateRuleDecision.Skip(msg, targetCode, udpTarget);
            }

            // 6. FILL_MODE: Always overwrites; OnlyIfEmpty keeps a human value. The
            // unknown-mode case was already refused above.
            if (!NamingTemplateEngine.ShouldWrite(rule.TemplateFillMode, currentVal, out _))
            {
                return TemplateRuleDecision.Skip(
                    $"TEMPLATE_FILL_MODE=OnlyIfEmpty and the target already holds '{currentVal}'", targetCode, udpTarget);
            }

            // 7. Idempotent: never dirty the model with a no-op write.
            if (string.Equals(currentVal, rendered, StringComparison.Ordinal))
                return TemplateRuleDecision.NoChange(targetCode, udpTarget, currentVal);

            return TemplateRuleDecision.Write(targetCode, udpTarget, currentVal, rendered);
        }
    }
}
