#nullable enable

using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// The erwin Mart lock a promotion applies to the promoted model version
    /// while the request waits for approval. Member names map 1:1 (uppercased)
    /// to the PROMOTION_LOCK_TYPE codes the admin side stores, so
    /// <see cref="ConfigContextService.ParseEffectiveEnum{TEnum}"/> parses the
    /// setting value directly and <see cref="PromotionLockTypeCodes.ToCode"/>
    /// renders the exact code for the queue row's LOCK_TYPE column.
    /// </summary>
    public enum PromotionLockType
    {
        /// <summary>No lock is applied ('UNLOCKED').</summary>
        Unlocked,

        /// <summary>erwin Existence Lock ('EXISTENCE').</summary>
        Existence,

        /// <summary>erwin Shared Lock ('SHARED').</summary>
        Shared,

        /// <summary>erwin Update Lock ('UPDATE').</summary>
        Update,

        /// <summary>erwin Exclusive Lock ('EXCLUSIVE'); the built-in default.</summary>
        Exclusive,
    }

    /// <summary>
    /// The exact PROMOTION_LOCK_TYPE codes of the admin contract. Mirrored
    /// here because the add-in references MetaShared only (the admin's
    /// MetaCore constants are not visible to it); casing is part of the
    /// contract and must not change.
    /// </summary>
    public static class PromotionLockTypeCodes
    {
        /// <summary>Code for <see cref="PromotionLockType.Unlocked"/>.</summary>
        public const string Unlocked = "UNLOCKED";

        /// <summary>Code for <see cref="PromotionLockType.Existence"/>.</summary>
        public const string Existence = "EXISTENCE";

        /// <summary>Code for <see cref="PromotionLockType.Shared"/>.</summary>
        public const string Shared = "SHARED";

        /// <summary>Code for <see cref="PromotionLockType.Update"/>.</summary>
        public const string Update = "UPDATE";

        /// <summary>Code for <see cref="PromotionLockType.Exclusive"/>.</summary>
        public const string Exclusive = "EXCLUSIVE";

        /// <summary>
        /// Renders the exact LOCK_TYPE column code for a lock type.
        /// </summary>
        public static string ToCode(PromotionLockType lockType) => lockType switch
        {
            PromotionLockType.Unlocked => Unlocked,
            PromotionLockType.Existence => Existence,
            PromotionLockType.Shared => Shared,
            PromotionLockType.Update => Update,
            PromotionLockType.Exclusive => Exclusive,
            _ => throw new ArgumentOutOfRangeException(nameof(lockType), lockType, "Unknown PROMOTION_LOCK_TYPE"),
        };
    }

    /// <summary>
    /// Applies/releases the erwin Mart lock of a promotion. Abstracted because
    /// SCAPI r10.10 exposes NO lock API (proven 2026-07-22: the live 29-method
    /// ISCPersistenceUnit dispatch dump has no lock/checkout method and the API
    /// reference's Disposition tokens only cover open-time read-only/ignore-locks
    /// requests). Real named locks need a UI-automation implementation (its own
    /// later phase, project decision D1 2026-07-22); until then
    /// <see cref="UnlockedOnlyMartVersionLockService"/> is the only
    /// implementation and any non-UNLOCKED request fails loudly BEFORE the
    /// queue insert - no silent fallback per project rule.
    /// </summary>
    public interface IMartVersionLockService
    {
        /// <summary>True when this implementation can apply/release <paramref name="lockType"/>.</summary>
        bool Supports(PromotionLockType lockType);

        /// <summary>
        /// Applies <paramref name="lockType"/> to <paramref name="version"/> of the
        /// model at <paramref name="martPath"/>. Throws <see cref="NotSupportedException"/>
        /// when the lock type is not supported; the caller surfaces the message
        /// and aborts the submit.
        /// </summary>
        void ApplyLock(string martPath, int version, PromotionLockType lockType, Action<string>? log);

        /// <summary>
        /// Releases the lock previously applied by <see cref="ApplyLock"/>.
        /// Same support contract as <see cref="ApplyLock"/>.
        /// </summary>
        void ReleaseLock(string martPath, int version, PromotionLockType lockType, Action<string>? log);
    }

    /// <summary>
    /// Phase-1 lock implementation: only UNLOCKED is supported (a no-op by
    /// definition). Non-UNLOCKED lock types throw so a deployment whose
    /// effective PROMOTION_LOCK_TYPE is a real lock cannot silently promote
    /// without the lock the admin asked for; those deployments set
    /// PROMOTION_LOCK_TYPE=UNLOCKED until the real lock phase ships.
    /// </summary>
    public sealed class UnlockedOnlyMartVersionLockService : IMartVersionLockService
    {
        /// <inheritdoc />
        public bool Supports(PromotionLockType lockType) => lockType == PromotionLockType.Unlocked;

        /// <inheritdoc />
        public void ApplyLock(string martPath, int version, PromotionLockType lockType, Action<string>? log)
        {
            EnsureSupported(lockType);
            log?.Invoke($"PromotionLock: UNLOCKED - no lock applied ({martPath} v{version})");
        }

        /// <inheritdoc />
        public void ReleaseLock(string martPath, int version, PromotionLockType lockType, Action<string>? log)
        {
            EnsureSupported(lockType);
            log?.Invoke($"PromotionLock: UNLOCKED - no lock to release ({martPath} v{version})");
        }

        private static void EnsureSupported(PromotionLockType lockType)
        {
            if (lockType != PromotionLockType.Unlocked)
                throw new NotSupportedException(
                    $"Mart lock '{PromotionLockTypeCodes.ToCode(lockType)}' is not supported by this add-in build yet. " +
                    "Set PROMOTION_LOCK_TYPE to UNLOCKED in the admin settings, or wait for the lock-capable release.");
        }
    }
}
