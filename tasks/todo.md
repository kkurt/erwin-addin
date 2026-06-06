# Task: Generate DDL - dynamic "Send" / "Send to Approve" button + no-approval REST callback

## Goal (from user)
In the Generate DDL flow (DdlApprovalDialog, shown after Generate DDL):
- If an APPROVAL is configured for the active config -> button stays **"Send to Approve"**, existing flow runs.
  REST callback is NOT fired by the add-in (admin fires it after the approve step). (user-confirmed)
- If NO approval is configured -> button is **"Send"**; on click -> save -> then, if a REST callback is
  configured for the config, call it (same logic as the admin approval screen). (user-confirmed)
- The REST call must use the SAME logic as admin's approval-screen callback. Moving that logic to the
  shared MetaShared assembly is approved by the user.

## Authoritative facts (from investigation)
- "Approval exists" == `ApprovalConfigService.GetApprovers(configId).Count > 0` (APPROVAL_APPROVER rows).
- REST callback config == `APPROVAL_CALLBACK` (1:1 config) + `APPROVAL_CALLBACK_PARAM` (encrypted params)
  + the referenced REST_API `CONNECTION_DEF` row (host/port/creds, DPAPI-encrypted).
- Reference invoker: `ApprovalCallbackInvoker.InvokeAsync(int configId, DdlApprovalQueue queueRow)`
  (erwin-admin/Services) -> tokens {{DDL}}/{{MODEL}}/{{CONFIG}}/{{NOTE}}/{{SUBMITTED_BY}}/{{DBMS}},
  GET query / POST-PUT JSON body, Basic auth, 30s timeout, returns {Success, HttpStatus, Message}.
  Admin records the outcome via `DdlApprovalService.RecordCallbackResult` -> CALLBACK_STATUS/AT/RESPONSE.
- Both `ApprovalConfigService` and `ApprovalCallbackInvoker` depend ONLY on MetaShared (RepoDbContext,
  entities, IApprovalConfigService/IApprovalCallbackInvoker/IBootstrapService/PasswordEncryptionService)
  + BCL. No WinForms/app deps -> clean to move to MetaShared.
- Add-in already builds `new RepoDbContext(DatabaseService.Instance.GetConfig())` (CorporateContextService),
  so an `IBootstrapService` adapter over DatabaseService is trivial.
- Add-in `DdlApprovalService.Submit` inserts DDL_APPROVAL_QUEUE (CONFIG_ID, MODEL_NAME, MODEL_LOCATOR,
  SOURCE_MODE, DBMS_TYPE, DDL_TEXT, NOTE, STATUS='Pending', SUBMITTED_BY, SUBMITTED_AT) and returns new ID.
- Add-in has NO HttpClient anywhere today; reuse the shared invoker.

## Plan

### Phase 0 - Share the REST logic via MetaShared (erwin-admin)
- [ ] Move `erwin-admin/Services/ApprovalConfigService.cs` -> `erwin-admin/MetaShared/Services/`
      (namespace `EliteSoft.MetaAdmin.Services` -> `EliteSoft.MetaAdmin.Shared.Services`).
- [ ] Move `erwin-admin/Services/ApprovalCallbackInvoker.cs` -> `erwin-admin/MetaShared/Services/`
      (same namespace change).
- [ ] Update admin-app references (DI registration + any `using`) to the new namespace.
- [ ] Build erwin-admin (app + MetaShared) green.

### Phase 1 - Add-in: detect "approval exists" + REST availability
- [ ] Add `Services/AddinBootstrapService.cs` : `IBootstrapService` wrapping `DatabaseService.Instance.GetConfig()`.
- [ ] In `ModelConfigForm.ShowDdlForApproval`: build `ApprovalConfigService(bootstrap)`, compute
      `hasApprovers = svc.GetApprovers(ActiveConfigId).Count > 0`; pass `hasApprovers` to the dialog ctor.

### Phase 2 - Dynamic button text
- [ ] `DdlApprovalDialog`: new ctor param `bool hasApprovers`; set `_btnSend.Text = hasApprovers ? "Send to Approve" : "Send"`.

### Phase 3 - No-approval flow: Send -> save -> REST
- [ ] `DdlApprovalDialog.BtnSend_Click`: keep current steps (ConfirmSubmitDialog -> Mart save ->
      DdlApprovalService.Submit). For the `!hasApprovers` branch, after the queue-row insert:
      - build an in-memory `DdlApprovalQueue` (DdlText/ModelName/Note/SubmittedBy/DbmsType/ConfigId),
      - `await new ApprovalCallbackInvoker(approvalCfg, bootstrap).InvokeAsync(configId, queueRow)`,
      - `DdlApprovalService.RecordCallbackResult(newId, result)` (stamp CALLBACK_STATUS/AT/RESPONSE),
      - show the REST result to the user (success/failure); do NOT swallow a failure.
- [ ] Approval branch (`hasApprovers`): unchanged - no REST from the add-in.

### Phase 4 - Add-in DdlApprovalService.RecordCallbackResult
- [ ] Add raw-SQL `UPDATE DDL_APPROVAL_QUEUE SET CALLBACK_STATUS=@s, CALLBACK_AT=@t, CALLBACK_RESPONSE=@r
      WHERE ID=@id` (MSSQL/Oracle/PG dialect parity with the existing Submit inserts).

### Phase 5 - Build + verify
- [ ] Build erwin-addin + erwin-admin green; run add-in test suite.
- [ ] Manual: a config WITH approvers -> "Send to Approve" + current flow; a config WITHOUT approvers
      -> "Send" + REST fired + CALLBACK_* stamped.

## Decisions (user-confirmed 2026-06-06)
1. No-approval queue row STATUS = **'ApprovedBySystem'**.
2. No-approval "Send" STILL inserts a DDL_APPROVAL_QUEUE row. YES.
3. "kaydet" == existing Mart save (SaveCurrentModelWithDescription) still runs. YES.
4. REST: await + show result + stamp CALLBACK_* before closing (blocking). YES.

## Review (done 2026-06-06)
- [x] Phase 0: moved ApprovalConfigService + ApprovalCallbackInvoker into MetaShared/Services
      (namespace -> EliteSoft.MetaAdmin.Shared.Services; +`using EliteSoft.MetaAdmin.Services` for
      PasswordEncryptionService; +[SupportedOSPlatform("windows")]). CompositionRoot unchanged (already
      imports Shared.Services). MetaShared builds 0/0.
- [x] Phase 1: reused the EXISTING `DatabaseService.Instance.BootstrapService` (no new adapter needed).
      ShowDdlForApproval computes `hasApprovers = ApprovalConfigService(bootstrap).GetApprovers(configId).Count>0`
      (fail-safe -> approval path on error), passes it to the dialog.
- [x] Phase 2: DdlApprovalDialog button text `_hasApprovers ? "Send to Approve" : "Send"`.
- [x] Phase 3: BtnSend_Click - status `Pending`/`ApprovedBySystem`; no-approval branch fires shared
      ApprovalCallbackInvoker.InvokeAsync + RecordCallbackResult + conditional success/failure modal.
      Approval branch unchanged. Errors surfaced (no swallow).
- [x] Phase 4: DdlApprovalService.Submit gained a `status` param; new RecordCallbackResult (MSSQL/Oracle/PG
      UPDATE of CALLBACK_STATUS/AT/RESPONSE).
- [x] Phase 5: erwin-addin builds 0/0 + 157 tests pass; MetaShared builds 0/0; admin app compiles (output
      copy blocked only by the running MetaAdmin.Erwin process - not a code error).

## Files changed
- erwin-admin: MetaShared/Services/ApprovalConfigService.cs (moved), MetaShared/Services/ApprovalCallbackInvoker.cs (moved),
  deleted Services/ApprovalConfigService.cs + Services/ApprovalCallbackInvoker.cs.
- erwin-addin: ModelConfigForm.cs (ShowDdlForApproval), Forms/DdlApprovalDialog.cs, Services/DdlApprovalService.cs.

## Not committed. Manual verify pending (needs add-in + admin redeploy with apps closed).
</content>
