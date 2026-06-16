# Required "object-type-only" existence rule (2026-06-15)

Admin added a second mode to the Required naming rule:
- Required + Property  -> "value must be set" (existing per-object behaviour, unchanged)
- Required + Property "(none)" -> "an object of this type must exist" (object-type-only)
  Stored as MC_NAMING_STANDARD row: RULE_TYPE='Required', PROPERTY_DEF_ID=NULL,
  OBJECT_TYPE_ID set, IS_REQUIRED=1, ERROR_MESSAGE optional.

## User decisions (2026-06-15)
1. Timing/scope: MODEL-OPEN ONLY (same seam as CheckModelRequiredUdpsOnce, once per open).
2. On violation: WARN-ONLY popup (ERROR_MESSAGE), multiple violations consolidated, no block.
3. Object types: ALL mappable types (TABLE/VIEW/INDEX/SUBJECT AREA/COLUMN); nonsensical
   ones (MODEL = always exists, unmapped) are logged-and-skipped, not warned.

## Root cause today
The add-in loader silently drops these rows: INNER JOIN on MC_PROPERTY_DEF +
`WHERE pd.DBMS_VERSION_ID IS NULL` excludes NULL PROPERTY_DEF_ID, and the
`Convert.ToInt32(reader["PROPERTY_DEF_ID"])` read would throw on DBNull. So the
rules never load and nothing is enforced.

## User decision ADDENDUM (2026-06-15)
4. Admin dialog: when Property = "(none)" for a Required rule, DISABLE the ApplyOn combo
   (force Both). ApplyOn is meaningless for a model-level existence assertion.

## Plan (checkable)
- [x] A1. NamingStandardService.GetQuery (MSSQL/Oracle/PostgreSQL): INNER JOIN ->
      LEFT JOIN MC_PROPERTY_DEF; WHERE `... AND (ns.PROPERTY_DEF_ID IS NULL OR pd.DBMS_VERSION_ID IS NULL)`.
- [x] A2. LoadStandards: PROPERTY_DEF_ID read null-safe; NamingStandardRule.PropertyDefId -> int?.
- [x] A3. New `GetObjectExistenceRules()` -> active RuleType==Required rows with empty PropertyCode.
- [x] B1. ScapiCollectTypeForExistence map: TABLE->Entity, VIEW->View, COLUMN->Attribute,
      INDEX->Key_Group, SUBJECT AREA->Subject_Area; MODEL/unknown -> null (log-and-skip).
- [x] B2. CheckRequiredObjectTypesExist(root) in TableTypeMonitorService: existence-only Collect
      (early break), consolidated WARN popup, ERROR_MESSAGE or English default.
- [x] B3. Called from ValidationCoordinatorService.CheckModelRequiredUdpsOnce (after required-UDP, once-per-open).
- [x] C. ApplyOn + DEPENDS_ON_* ignored for existence rules (commented).
- [x] D. Tests: ExistenceRuleTests (GetObjectExistenceRules selection + non-leak + map). 294/294.
- [x] ADMIN. NamingRuleEditDialog: UpdateApplyOnEnabledState (disable+Both when "(none)"),
      wired on property-change + initial state. Compiles clean.
- [x] REVIEW FIX (workflow, major): editing a saved existence rule auto-picked a real property ->
      ApplyOn stayed enabled + unchanged Save silently converted it to a per-property rule.
      Fixed in PopulateFromEntity: re-select the "(none)" Id-0 sentinel for Required+PropertyDefId==null.
- [ ] E. LIVE-VERIFY (user): redeploy add-in + admin. Author Required+(none) TABLE rule;
      empty model open -> warn popup; add a table -> no popup. Edit that rule -> ApplyOn shows
      disabled/Both; unchanged Save keeps PROPERTY_DEF_ID null.

## NOT in scope
- No auto-create of objects (existence cannot be auto-fixed).
- No change to the existing Required + Property (value-must-be-set) path.
- ModelConfigForm.cs / ModelConfigForm.Spike.cs (foreign uncommitted work - untouched).
