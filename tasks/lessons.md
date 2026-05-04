# Lessons Learned

A running log of corrections and non-obvious findings that future sessions
should not have to rediscover. Each entry is a short rule, the reason, and
how to apply it.

## 2026-05-05: PU.Locator is unreliable on r10.10 Mart-bound PUs

**Rule:** never read `pu.Locator?.ToString()` directly when the model lives
on a Mart server. Always go through `Services/PuLocatorReader.cs`.

**Why:** verified against `Mart://Mart/Kursat/MetaRepo` on r10.10 - the
direct property returned `""` even though the model was loaded and the title
bar showed the full mart path. Downstream services that scope on
`MART_PATH` (ConfigContext, Glossary, NamingStandard, PredefinedColumn,
UdpDefinition) silently misclassify the model as a local-file model and
the add-in surfaces "Active model is not on a Mart server" before any of
them can run.

**How to apply:** the helper performs four cascading reads (direct,
`PropertyBag()`, `PropertyBag(null,true)`, main-window title regex). It logs
the failed layers, so if the user reports an empty locator we can see which
fallback finally caught it. `VersionCompareService.ReadActiveLocator` is the
reference DRY consumer.

## 2026-05-05: Form.Shown can fire on a disposed form

**Rule:** every `Form.Shown` / `Form.Load` / `Form.Activated` handler that
runs as a side effect of `Show()` must guard with `if (!form.IsDisposed)`
before touching the form.

**Why:** when the form's synchronous init path (constructor, Load, or a
connect handler driven from `Show()`) hits a failure and calls `Close()` /
`ForceClose()`, the form is disposed before `Show()` returns. The Shown
event still fires post-dispose, raising `ObjectDisposedException` from
inside our handler. The exception bubbles to `ErwinAddIn.Execute()`'s catch
and is reported as "Add-In Error: Cannot access a disposed object", which
masks the real failure that triggered the dispose in the first place.

**How to apply:** existing audit point is
[ErwinAddIn.cs:157](../ErwinAddIn.cs#L157) where the TopMost-reset handler
already has the guard. Any new lifecycle-event subscription on a long-lived
form follows the same pattern.

## 2026-05-05: MART_PATH stem must match admin's parser exactly

**Rule:** when extracting the mart path from a locator, use the same regex
as `VersionCompareService.BuildMartLocatorForTarget`:
`Mart://Mart/(?<path>[^?&]+?)(?:[?&]|$)` with `Trim('/')`. The shared
implementation lives in `ConfigContextService.ParseMartPath`.

**Why:** admin's `ModelMappingService.GetByMartPath` does an exact-match
string compare against the value built by `BrowserPanel.BuildPath`
(e.g. `Kursat/MetaRepo`, no leading slash, no trailing slash). The first
draft of `ParseMartPath` only stopped at `?` and only `TrimEnd`'d the
trailing slash, missing two edge cases observed in the wild: locators that
use `&version=N` instead of `?VNO=N`, and a leading slash from
`Mart://Mart//<lib>/<model>`.

**How to apply:** unit test coverage in
[tests/ErwinAddIn.Tests/ConfigContextServiceTests.cs](../tests/ErwinAddIn.Tests/ConfigContextServiceTests.cs)
codifies the seven accepted shapes plus six rejection cases. Add a new
inline data row before changing the regex.
