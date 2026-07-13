using System;
using EliteSoft.Erwin.AddIn.Services;
using FluentAssertions;
using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// Pure-logic tests for the DDL-generator instance config (DdlWorkerConfig).
/// The DB read (DdlWorkerConfigService) needs a live bootstrap DB so it is not
/// unit-tested here; the parsing + keep-alive rules it depends on ARE, since a
/// wrong auth-type parse would silently pick the wrong Mart login path and a
/// wrong keep-alive calc would either ping during a job or let the login time
/// out.
/// </summary>
public class DdlWorkerConfigTests
{
    // ---- ParseAuthType ----

    [Theory]
    [InlineData("SERVER", MartAuthType.Server)]
    [InlineData("server", MartAuthType.Server)]
    [InlineData("  Server  ", MartAuthType.Server)]
    [InlineData("WINDOWS", MartAuthType.Windows)]
    [InlineData("windows", MartAuthType.Windows)]
    public void ParseAuthType_recognizes_both_known_types(string raw, MartAuthType expected)
    {
        var result = DdlWorkerConfig.ParseAuthType(raw, out bool recognized);
        result.Should().Be(expected);
        recognized.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LDAP")]
    [InlineData("Server Authentication")] // the DIALOG display text, not the DB code
    public void ParseAuthType_unknown_defaults_to_windows_and_flags_unrecognized(string raw)
    {
        var result = DdlWorkerConfig.ParseAuthType(raw, out bool recognized);
        result.Should().Be(MartAuthType.Windows, "unknown auth must fall back to the credential-less path");
        recognized.Should().BeFalse();
    }

    // ---- NormalizeKeepAliveMinutes ----

    [Theory]
    [InlineData(5, 5)]
    [InlineData(1, 1)]
    [InlineData(60, 60)]
    [InlineData(1440, 1440)]
    public void NormalizeKeepAlive_passes_through_valid_values(int raw, int expected)
    {
        DdlWorkerConfig.NormalizeKeepAliveMinutes(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NormalizeKeepAlive_non_positive_becomes_default_5(int raw)
    {
        DdlWorkerConfig.NormalizeKeepAliveMinutes(raw).Should().Be(5);
    }

    [Fact]
    public void NormalizeKeepAlive_null_becomes_default_5()
    {
        DdlWorkerConfig.NormalizeKeepAliveMinutes(null).Should().Be(5);
    }

    [Fact]
    public void NormalizeKeepAlive_caps_absurd_values_at_one_day()
    {
        DdlWorkerConfig.NormalizeKeepAliveMinutes(99999).Should().Be(1440);
    }

    // ---- IsKeepAliveDue ----

    private static readonly DateTime T0 = new DateTime(2026, 07, 12, 12, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void IsKeepAliveDue_true_when_idle_and_interval_elapsed()
    {
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(5), 5, jobActive: false, pingActive: false)
            .Should().BeTrue();
    }

    [Fact]
    public void IsKeepAliveDue_true_when_well_past_interval()
    {
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(30), 5, jobActive: false, pingActive: false)
            .Should().BeTrue();
    }

    [Fact]
    public void IsKeepAliveDue_false_before_interval()
    {
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(4).AddSeconds(59), 5, jobActive: false, pingActive: false)
            .Should().BeFalse();
    }

    [Fact]
    public void IsKeepAliveDue_false_while_a_job_is_active_even_if_overdue()
    {
        // Hard requirement: a ping must NEVER run during DDL generation.
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(60), 5, jobActive: true, pingActive: false)
            .Should().BeFalse();
    }

    [Fact]
    public void IsKeepAliveDue_false_while_a_ping_is_already_active()
    {
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(60), 5, jobActive: false, pingActive: true)
            .Should().BeFalse();
    }

    [Fact]
    public void IsKeepAliveDue_treats_non_positive_interval_as_default_5()
    {
        // 3 minutes elapsed, bogus interval 0 -> normalized to 5 -> not due yet.
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(3), 0, jobActive: false, pingActive: false)
            .Should().BeFalse();
        // 6 minutes elapsed -> due.
        DdlWorkerConfig.IsKeepAliveDue(T0, T0.AddMinutes(6), 0, jobActive: false, pingActive: false)
            .Should().BeTrue();
    }
}
