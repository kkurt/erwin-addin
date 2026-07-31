using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// HKCU bootstrap credential decryption: what the operator is told when a stored
/// DPAPI blob cannot be decrypted on the current account.
///
/// <para>Prod incident 2026-07-30: opening any model popped
/// "Add-In Error: Key not valid for use in specified state." and nothing else.
/// That is the raw framework text of a <see cref="CryptographicException"/> from
/// <c>ProtectedData.Unprotect</c>, surfaced verbatim by
/// <c>ErwinAddIn.Execute</c>'s catch because the HKCU read runs inside the
/// ModelConfigForm constructor. It named no registry key, no account and no
/// remedy, so the admin had no way to act on it.</para>
///
/// <para>These tests pin both halves: the failure still propagates (never a
/// silent ciphertext fallback) AND it carries the diagnosis plus the fix.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class HkcuBootstrapCredentialTests
{
    // DpapiDecrypt is private static by design - the add-in never exposes a
    // credential-decrypt entry point. Reaching it by reflection tests the real
    // production path without writing to the live
    // HKCU\Software\EliteSoft\MetaRepo\Bootstrap key, which holds the developer's
    // working configuration.
    private static readonly MethodInfo Decrypt =
        typeof(HkcuBootstrapReader).GetMethod("DpapiDecrypt", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "HkcuBootstrapReader.DpapiDecrypt(string, string) not found - signature changed?");

    private static string? Invoke(string? cipher, string valueName)
    {
        try
        {
            return (string?)Decrypt.Invoke(null, new object?[] { cipher, valueName });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Unwrap so tests assert on the exception the add-in actually raises.
            throw ex.InnerException;
        }
    }

    private static string ProtectForThisAccount(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));

    /// <summary>
    /// A well-formed DPAPI blob whose master-key GUID no longer resolves for this
    /// account. This is the in-process stand-in for the prod case: a registry key
    /// copied from another profile/machine, or an account whose master key changed
    /// after an admin-forced password reset. Verified to produce the exact field
    /// message "Key not valid for use in specified state." (random bytes produce
    /// "The data is invalid." instead, which is a different diagnosis).
    /// </summary>
    private static string ForeignBlob()
    {
        byte[] blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes("metapass"), null, DataProtectionScope.CurrentUser);
        for (int i = 24; i < 40; i++)
            blob[i] = (byte)((blob[i] + 7) % 256);
        return Convert.ToBase64String(blob);
    }

    // ---- happy path / no regression ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Absent_credential_decrypts_to_empty_without_touching_dpapi(string? cipher)
    {
        // A trusted-connection install seeds "" for DBUserName/DBPassword; that must
        // stay a valid configuration, not an error.
        Invoke(cipher, "DBPassword").Should().BeEmpty();
    }

    [Fact]
    public void Credential_encrypted_by_this_account_round_trips()
    {
        Invoke(ProtectForThisAccount("metapass"), "DBPassword").Should().Be("metapass");
    }

    // ---- the prod failure ----

    [Fact]
    public void Undecryptable_credential_throws_instead_of_returning_ciphertext()
    {
        string foreign = ForeignBlob();

        Action act = () => Invoke(foreign, "DBPassword");

        // Returning the ciphertext would send garbage to the DB as a password and
        // report "login failed" instead of the real cause (project rule: no silent
        // fallback, no swallowed errors).
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(foreign);
    }

    [Fact]
    public void Undecryptable_credential_keeps_the_crypto_exception_as_inner()
    {
        Action act = () => Invoke(ForeignBlob(), "DBPassword");

        // The original exception must survive for the log; only the user-facing
        // text is replaced.
        act.Should().Throw<InvalidOperationException>()
            .WithInnerException<CryptographicException>()
            .Which.Message.Should().Be("Key not valid for use in specified state.");
    }

    [Theory]
    [InlineData("DBUserName")]
    [InlineData("DBPassword")]
    public void Undecryptable_credential_message_carries_the_full_diagnosis(string valueName)
    {
        Action act = () => Invoke(ForeignBlob(), valueName);

        string message = act.Should().Throw<InvalidOperationException>().Which.Message;

        // Which value, and where it lives - the admin has to find it.
        message.Should().Contain(valueName);
        message.Should().Contain(@"HKCU\Software\EliteSoft\MetaRepo\Bootstrap");
        // Which account it failed on: the whole class of bug is "works for the
        // installer, fails for the erwin user", so naming the account is the
        // fastest way to spot a cross-profile install.
        message.Should().Contain(Environment.UserName);
        // Why.
        message.Should().Contain("different Windows account or machine");
        message.Should().Contain("password reset");
        // How to fix it. Re-running install.bat with NO arguments used to keep the
        // broken blob, so the argument list is part of the instruction.
        message.Should().Contain("install.bat");
        message.Should().Contain("-DBHost");
        message.Should().Contain("bootstrap.seed.json");
    }

    [Fact]
    public void Undecryptable_credential_message_is_not_the_bare_framework_text()
    {
        Action act = () => Invoke(ForeignBlob(), "DBPassword");

        string message = act.Should().Throw<InvalidOperationException>().Which.Message;

        // Regression guard for the prod incident: the popup showed exactly this and
        // nothing more.
        message.Should().NotBe("Key not valid for use in specified state.");
        message.Length.Should().BeGreaterThan(120);
    }

    // ---- corrupt registry value (a different cause, a different message) ----

    [Fact]
    public void Non_base64_credential_reports_corruption_not_a_wrong_account()
    {
        Action act = () => Invoke("not!base64!", "DBPassword");

        var ex = act.Should().Throw<InvalidOperationException>().Which;
        ex.Message.Should().Contain("not valid Base64");
        // Must not misdiagnose a hand-edited value as a cross-account problem.
        ex.Message.Should().NotContain("different Windows account or machine");
        ex.InnerException.Should().BeOfType<FormatException>();
    }
}
