using EliteSoft.Erwin.AddIn.Services;

using FluentAssertions;

using Xunit;

namespace EliteSoft.Erwin.AddIn.Tests;

/// <summary>
/// <see cref="GlossaryService.IsLoadedForConfig"/> is the single, config-scoped decision behind the
/// <see cref="GlossaryService.IsLoaded"/> getter. Every glossary consumer uses the uniform
/// "if (!IsLoaded) LoadGlossary()" pattern, so this predicate governs whether a glossary loaded
/// under one model is reused for another.
///
/// 2026-07-09 bug (from erwin-addin-debug.log): model A (CoreBanking, CONFIG.ID=2012,
/// USE_EXTERNAL_GLOSSARY=true) loaded the glossary; an MDI switch to model B (Ripper, CONFIG.ID=2014,
/// USE_EXTERNAL_GLOSSARY=false) left _isLoaded=true, so the gate was never re-read and B's column
/// FRAUD_CHECKS.Abc was validated against A's glossary. The fix scopes IsLoaded to the config the
/// load was captured under: when the active config differs, IsLoaded reports false so the next
/// caller reloads under the new config (re-reading USE_EXTERNAL_GLOSSARY, which is false for B -> no
/// validation).
/// </summary>
public class GlossaryConfigScopeTests
{
    [Theory]
    // Loaded and still on the same model -> usable (warm cache, no reload).
    [InlineData(true, 2012, 2012, true)]
    // THE BUG: loaded under model A (2012), active model is now B (2014) -> NOT usable, must reload.
    [InlineData(true, 2012, 2014, false)]
    // Never loaded -> not usable regardless of config ids.
    [InlineData(false, 2012, 2012, false)]
    [InlineData(false, 2012, 2014, false)]
    // ConfigContext not initialized at read time (-1): a load captured under a real config is not
    // usable -> skip / fail-open until it reloads.
    [InlineData(true, 2012, -1, false)]
    // Loaded while uninitialized then a real config becomes active -> not usable.
    [InlineData(true, -1, 2012, false)]
    // Both uninitialized (-1 == -1): consistent, treated as usable (the _isLoaded conjunct still
    // gates real behavior elsewhere).
    [InlineData(true, -1, -1, true)]
    // Cross-repo CONFIG.ID collision: same integer id from a DIFFERENT repo DB. The predicate cannot
    // see the repo, so it reports usable - which is exactly why the explicit Change-DB path calls
    // GlossaryService.Invalidate() to force a reload (this case is covered there, not here).
    [InlineData(true, 5, 5, true)]
    public void IsLoadedForConfig_is_true_only_for_a_load_captured_under_the_active_config(
        bool loaded, int loadedUnderConfigId, int currentConfigId, bool expected)
    {
        GlossaryService.IsLoadedForConfig(loaded, loadedUnderConfigId, currentConfigId)
            .Should().Be(expected);
    }
}
