#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using EliteSoft.MetaAdmin.Shared.Data;
using EliteSoft.MetaAdmin.Shared.Data.Entities;
using EliteSoft.MetaAdmin.Shared.Services;

using Microsoft.EntityFrameworkCore;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Everything the add-in needs to insert one PROMOTION queue row. All
    /// values are resolved by the caller (form/flow) so the service stays a
    /// dumb, transactional writer.
    /// </summary>
    /// <param name="ConfigId">Resolved CONFIG.ID of the open model.</param>
    /// <param name="ModelName">Display model name (like the DDL push row).</param>
    /// <param name="MartPath">Canonical root-relative Mart path (MODEL_CONFIG_MAPPING.MART_PATH shape); becomes MODEL_LOCATOR by the PROMOTION contract.</param>
    /// <param name="SourceMode">The compare source the DDL was generated from (existing DDL push convention).</param>
    /// <param name="DbmsType">DBMS label of the active PU (existing DDL push convention).</param>
    /// <param name="DdlText">The generated DDL (real DDL, like the DDL push row).</param>
    /// <param name="Note">User note typed in the review dialog; may be blank.</param>
    /// <param name="SourceEnvironmentId">Derived source ENVIRONMENT.ID.</param>
    /// <param name="TargetEnvironmentId">Chosen target ENVIRONMENT.ID.</param>
    /// <param name="ModelVersion">The just-saved Mart version being promoted.</param>
    /// <param name="LockTypeCode">Applied lock code (LOCK_TYPE column; 'UNLOCKED' when no lock).</param>
    /// <param name="AutoApprove">True when <see cref="PromotionPlanner.RequiresApprovalVote"/> said no vote is needed.</param>
    public sealed record PromotionSubmitRequest(
        int ConfigId,
        string? ModelName,
        string MartPath,
        string SourceMode,
        string? DbmsType,
        string DdlText,
        string? Note,
        int SourceEnvironmentId,
        int TargetEnvironmentId,
        int ModelVersion,
        string LockTypeCode,
        bool AutoApprove);

    /// <summary>Outcome of <see cref="PromotionService.SubmitPromotion"/>.</summary>
    /// <param name="QueueId">New DDL_APPROVAL_QUEUE.ID.</param>
    /// <param name="AutoApproved">True when the row was inserted Approved and the version row was upserted.</param>
    public sealed record PromotionSubmitResult(int QueueId, bool AutoApproved);

    /// <summary>
    /// A Pending PROMOTION request of the open model, for the General tab's
    /// "Pending vN" indicator.
    /// </summary>
    public sealed record PendingPromotion(
        int Id,
        int? TargetEnvironmentId,
        int? ModelVersion,
        string? SubmittedBy,
        DateTime SubmittedAt);

    /// <summary>
    /// DB layer of the Version Promotion feature. Uses the shared EF
    /// <see cref="RepoDbContext"/> + MetaShared entities (same route as
    /// <see cref="ConfigContextService.GetEffective"/> and
    /// <see cref="SessionTrackingService"/>) so the auto-approve path gets a
    /// real multi-statement transaction and the three DB dialects need no
    /// hand-written SQL. The existing DDL push writer
    /// (<see cref="DdlApprovalService"/>) is deliberately untouched.
    ///
    /// No method swallows exceptions: every DB failure surfaces to the caller
    /// (project rule), which shows it to the user.
    /// </summary>
    public sealed class PromotionService
    {
        private static PromotionService? _instance;
        private static readonly object _lock = new object();

        /// <summary>Singleton accessor (matches the other service singletons).</summary>
        public static PromotionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new PromotionService();
                    }
                }
                return _instance;
            }
        }

        private PromotionService() { }

        /// <summary>
        /// Exact NOTE marker of the auto-approve path (spec contract, verbatim).
        /// </summary>
        public const string AutoApproveNote = "Auto-approved (no approval required for this transition)";

        /// <summary>
        /// The identity written to SUBMITTED_BY / PROMOTED_BY. The target
        /// deployments' Marts are Windows-auth (project decision D2,
        /// 2026-07-22), so the Windows account name IS the Mart login the
        /// web approver matching runs against. Null when unreadable; callers
        /// must treat that as a hard submit blocker.
        /// </summary>
        public static string? ResolveSubmitter()
        {
            try { return Environment.UserName; }
            catch { return null; }
        }

        /// <summary>
        /// The PRIMARY approver names of the model's promotion chain, in SEQ order.
        /// <para>
        /// Since the admin governance rework (erwin-admin 1795ce3) approvers are scoped to
        /// the MODEL - one ordered chain per (CONFIG_ID, MART_PATH) - and that single chain
        /// governs every DDL step and every promote step. The former per-transition model
        /// override (ENVIRONMENT_RELATION_MODEL_APPROVER) is gone, and there is deliberately
        /// NO fallback to the transition list: the server treats "no rows" as "no quorum"
        /// rather than silently using some other list (see
        /// <c>DdlApprovalService.ResolveSlots</c>), and ENVIRONMENT_RELATION_APPROVER now
        /// serves the INTEGRATE flow only. Consequently the result no longer depends on the
        /// chosen transition - every route of a model shares this one list.
        /// </para>
        /// <para>
        /// Resolution is delegated to the server's own reader,
        /// <see cref="ApprovalConfigService.GetModelPromotionApprovers(RepoDbContext, int, string)"/>,
        /// so the add-in cannot drift from the web on ordering or shape. Each slot may carry
        /// up to three BACKUP approvers (the slot is satisfied when its primary OR any backup
        /// approves); backups are not returned here because the add-in only needs to know
        /// WHETHER an approval gate exists, and the web owns who satisfies it.
        /// </para>
        /// <para>
        /// An empty result means no gate, i.e. auto-approve. That is also what keeps the
        /// queue consistent: the server REFUSES to resolve a quorum for a model with an empty
        /// catalog, so a Pending row must never be inserted for one.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> GetModelPromotionApprovers(int configId, string martPath)
        {
            if (configId <= 0)
                throw new ArgumentException("configId must be a resolved CONFIG.ID", nameof(configId));
            if (string.IsNullOrWhiteSpace(martPath))
                throw new ArgumentException("martPath must be the canonical Mart path", nameof(martPath));

            using var ctx = CreateContext();
            return ApprovalConfigService.GetModelPromotionApprovers(ctx, configId, martPath)
                .Select(s => s.Approver)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();
        }

        /// <summary>
        /// MODEL_ENVIRONMENT_VERSION rows of the model at
        /// <paramref name="martPath"/> (exact-match, case per DB collation -
        /// the path comes from the same <see cref="ConfigContextService.ParseMartPath"/>
        /// normalization that matched MODEL_CONFIG_MAPPING, so it is
        /// byte-identical by construction).
        /// </summary>
        public IReadOnlyList<PromotionEnvironmentVersion> GetEnvironmentVersions(string martPath)
        {
            if (string.IsNullOrWhiteSpace(martPath))
                throw new ArgumentException("martPath must be the canonical Mart path", nameof(martPath));

            using var ctx = CreateContext();
            return ctx.ModelEnvironmentVersions
                .Where(v => v.MartPath == martPath)
                .Select(v => new PromotionEnvironmentVersion(v.EnvironmentId, v.Version, v.PromotedBy, v.PromotedAt))
                .ToList();
        }

        /// <summary>
        /// Pending PROMOTION rows of the model at <paramref name="martPath"/>
        /// (for the General tab's "Pending vN" indicator). The SQL filters on
        /// bounded columns only and the MODEL_LOCATOR match runs in memory:
        /// MODEL_LOCATOR is an unbounded column mapped to NCLOB on Oracle, and
        /// Oracle rejects equality predicates on LOBs (ORA-00932), so a
        /// server-side `ModelLocator == martPath` would break every Oracle
        /// deployment while passing on MSSQL/Postgres.
        /// </summary>
        public IReadOnlyList<PendingPromotion> GetPendingPromotions(int configId, string martPath)
        {
            if (configId <= 0)
                throw new ArgumentException("configId must be a resolved CONFIG.ID", nameof(configId));
            if (string.IsNullOrWhiteSpace(martPath))
                throw new ArgumentException("martPath must be the canonical Mart path", nameof(martPath));

            using var ctx = CreateContext();
            return ctx.DdlApprovalQueue
                .Where(q => q.RequestType == DdlApprovalQueue.RequestTypes.Promotion
                         && q.ConfigId == configId
                         && q.Status == DdlApprovalQueue.Statuses.Pending)
                .Select(q => new { q.Id, q.TargetEnvironmentId, q.ModelVersion, q.SubmittedBy, q.SubmittedAt, q.ModelLocator })
                .AsEnumerable()
                .Where(q => string.Equals(q.ModelLocator, martPath, StringComparison.Ordinal))
                .Select(q => new PendingPromotion(q.Id, q.TargetEnvironmentId, q.ModelVersion, q.SubmittedBy, q.SubmittedAt))
                .ToList();
        }

        /// <summary>
        /// The caller's own PROMOTION rows that carry a LOCK_TYPE, as input to
        /// <see cref="PromotionPlanner.SelectMissedUnlocks"/> (the pure filter
        /// decides which still owe a release). Kept broad on purpose so the
        /// decision stays in testable code.
        /// </summary>
        public IReadOnlyList<PromotionQueueRowInfo> GetOwnPromotionLockRows(string submittedBy)
        {
            if (string.IsNullOrWhiteSpace(submittedBy))
                throw new ArgumentException("submittedBy must be set", nameof(submittedBy));

            using var ctx = CreateContext();
            return ctx.DdlApprovalQueue
                .Where(q => q.RequestType == DdlApprovalQueue.RequestTypes.Promotion
                         && q.SubmittedBy == submittedBy
                         && q.LockType != null)
                .Select(q => new PromotionQueueRowInfo(
                    q.Id, q.RequestType, q.Status, q.LockType, q.SubmittedBy, q.ModelLocator, q.ModelVersion))
                .ToList();
        }

        /// <summary>
        /// Latest reject reason of a queue row from DDL_APPROVAL_VOTE, or null
        /// when no reject vote (or no reason) exists. Shown to the user when a
        /// promotion comes back Rejected.
        /// </summary>
        public string? GetRejectReason(int queueId)
        {
            if (queueId <= 0)
                throw new ArgumentException("queueId must be a valid DDL_APPROVAL_QUEUE.ID", nameof(queueId));

            using var ctx = CreateContext();
            return ctx.DdlApprovalVotes
                .Where(v => v.QueueId == queueId && v.Decision == DdlApprovalVote.Decisions.Reject)
                .OrderByDescending(v => v.VotedAt)
                .Select(v => v.Reason)
                .FirstOrDefault();
        }

        /// <summary>
        /// Inserts the PROMOTION queue row and, on the auto-approve branch,
        /// upserts MODEL_ENVIRONMENT_VERSION in the SAME transaction (spec:
        /// "AYNI islemde"). On the approver branch the add-in NEVER writes the
        /// version row - the server upserts it when the vote finalizes.
        /// Returns the new queue ID. Throws on any validation/DB failure.
        /// </summary>
        public PromotionSubmitResult SubmitPromotion(PromotionSubmitRequest request, Action<string>? log)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.ConfigId <= 0)
                throw new ArgumentException("ConfigId must be a resolved CONFIG.ID", nameof(request));
            if (string.IsNullOrWhiteSpace(request.MartPath))
                throw new ArgumentException("MartPath must be the canonical Mart path", nameof(request));
            if (request.MartPath.Length > 500)
                throw new ArgumentException("MartPath exceeds the MART_PATH column limit (500)", nameof(request));
            if (string.IsNullOrWhiteSpace(request.SourceMode))
                throw new ArgumentException("SourceMode must be set", nameof(request));
            if (string.IsNullOrEmpty(request.DdlText))
                throw new ArgumentException("DdlText must be non-empty", nameof(request));
            if (request.SourceEnvironmentId <= 0 || request.TargetEnvironmentId <= 0)
                throw new ArgumentException("Source and target environment ids must be resolved", nameof(request));
            if (request.ModelVersion <= 0)
                throw new ArgumentException("ModelVersion must be the saved Mart version (unknown version blocks the send)", nameof(request));
            if (string.IsNullOrWhiteSpace(request.LockTypeCode))
                throw new ArgumentException("LockTypeCode must be set (use 'UNLOCKED' when no lock was applied)", nameof(request));

            string? submittedBy = ResolveSubmitter();
            if (string.IsNullOrWhiteSpace(submittedBy))
                throw new InvalidOperationException("Cannot resolve the submitting user name; promotion requires a real SUBMITTED_BY for approver matching.");

            DateTime nowUtc = DateTime.UtcNow;
            string status = request.AutoApprove ? DdlApprovalQueue.Statuses.Approved : DdlApprovalQueue.Statuses.Pending;
            string? note = ComposeNote(request.Note, request.AutoApprove);

            log?.Invoke($"Promotion.Submit: config={request.ConfigId}, path='{request.MartPath}', v{request.ModelVersion}, " +
                        $"src={request.SourceEnvironmentId} -> tgt={request.TargetEnvironmentId}, lock={request.LockTypeCode}, " +
                        $"auto={(request.AutoApprove ? "yes" : "no")}, ddlLen={request.DdlText.Length}");

            using var ctx = CreateContext();
            using var tx = ctx.Database.BeginTransaction();

            var row = new DdlApprovalQueue
            {
                ConfigId = request.ConfigId,
                ModelName = request.ModelName,
                ModelLocator = request.MartPath,
                SourceMode = request.SourceMode,
                DbmsType = request.DbmsType,
                DdlText = request.DdlText,
                Note = note,
                Status = status,
                SubmittedBy = submittedBy,
                SubmittedAt = nowUtc,
                RequestType = DdlApprovalQueue.RequestTypes.Promotion,
                SourceEnvironmentId = request.SourceEnvironmentId,
                TargetEnvironmentId = request.TargetEnvironmentId,
                ModelVersion = request.ModelVersion,
                LockType = request.LockTypeCode,
            };
            ctx.DdlApprovalQueue.Add(row);
            ctx.SaveChanges(); // populates row.Id (identity)

            if (request.AutoApprove)
            {
                var existing = ctx.ModelEnvironmentVersions.FirstOrDefault(
                    v => v.EnvironmentId == request.TargetEnvironmentId && v.MartPath == request.MartPath);
                if (existing == null)
                {
                    ctx.ModelEnvironmentVersions.Add(new ModelEnvironmentVersion
                    {
                        EnvironmentId = request.TargetEnvironmentId,
                        MartPath = request.MartPath,
                        Version = request.ModelVersion,
                        QueueId = row.Id,
                        PromotedBy = submittedBy,
                        PromotedAt = nowUtc,
                    });
                }
                else
                {
                    // Subsequent promotion: advance the TARGET's own version by one. The target
                    // environment's version is a monotonic per-environment counter, decoupled
                    // from the source Mart version - byte-for-byte the rule of the server's
                    // single writer (DdlApprovalService.UpsertModelEnvironmentVersion, shared by
                    // the vote path, the two-stage PromoteVersion action and the web promote
                    // endpoint). Writing request.ModelVersion here instead made the add-in and
                    // the web disagree about what a target holds, which (a) 409s the web
                    // Release Management promote of the next hop and (b) breaks this add-in's
                    // own rule-1 source derivation, whose match is `Version == promotedVersion`.
                    existing.Version += 1;
                    existing.QueueId = row.Id;
                    existing.PromotedBy = submittedBy;
                    existing.PromotedAt = nowUtc;
                }
                ctx.SaveChanges();
            }

            tx.Commit();
            log?.Invoke($"Promotion.Submit: inserted queue ID={row.Id}, status={status}" +
                        (request.AutoApprove ? " (version row upserted)" : string.Empty));
            return new PromotionSubmitResult(row.Id, request.AutoApprove);
        }

        /// <summary>
        /// NOTE column composition: the auto-approve marker is a spec-verbatim
        /// string; a user-typed note is preserved above it on its own line.
        /// Pure and public for the unit tests.
        /// </summary>
        public static string? ComposeNote(string? userNote, bool autoApprove)
        {
            bool hasUser = !string.IsNullOrWhiteSpace(userNote);
            if (!autoApprove) return hasUser ? userNote : null;
            return hasUser ? userNote + Environment.NewLine + AutoApproveNote : AutoApproveNote;
        }

        /// <summary>
        /// Opens a fresh <see cref="RepoDbContext"/> against the configured
        /// admin DB. Throws when the add-in has no DB configuration - a
        /// promotion cannot proceed without the repo DB (no silent fallback).
        /// </summary>
        private static RepoDbContext CreateContext()
        {
            var config = DatabaseService.Instance.GetConfig();
            if (config == null || !config.IsConfigured)
                throw new InvalidOperationException("Admin database is not configured; Version Promotion requires the repo DB connection.");
            return new RepoDbContext(config);
        }
    }
}
