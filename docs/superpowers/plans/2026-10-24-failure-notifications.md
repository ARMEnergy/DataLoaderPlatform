# Failure Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Email a human when a DataLoader run fails, and separately notice runs that never happened or never finished.

**Architecture:** A new `DataLoader.Core/Notifications/` component with two seams — `IFailureNotifier` (should this be reported, and what does it say) and `INotificationSender` (put it on the wire). Both the in-process path (called from `PlatformHost` and `Program.cs`) and a new `DataLoader.Host.exe --watchdog` mode feed the same notifier through a shared `INotificationPayload` interface. Message construction and watchdog decision logic are pure functions, tested without a database or a mail server.

**Tech Stack:** .NET 8, xunit 2.9.2, `Microsoft.Data.SqlClient`, `System.Net.Mail.SmtpClient` (STARTTLS, no credentials). No new NuGet packages. No new SQL objects.

**Spec:** [docs/superpowers/specs/2026-10-24-failure-notifications-design.md](../specs/2026-10-24-failure-notifications-design.md)

---

## Conventions this codebase uses — read before Task 1

- **Tests:** xunit with `[Fact]`, plain `Assert.*`. **No Moq, no FluentAssertions** — fakes are hand-written private sealed classes inside the test class. See `tests/DataLoader.Core.Tests/PlatformHostTests.cs` for the house style.
- **Loggers in tests:** `NullLogger<T>.Instance` from `Microsoft.Extensions.Logging.Abstractions`.
- **Options in tests:** `Options.Create(new PlatformSettings { ... })`.
- **Async in `DataLoader.Core`:** always `.ConfigureAwait(false)`.
- **Nullable reference types are enabled** in every project.
- **XML doc comments explain WHY, not what.** This codebase documents the trap a class exists to avoid. Match that — a comment that only restates the signature is noise here.
- **Never log or email a secret.** See Task 2.

## File structure

| File | Responsibility |
|---|---|
| `src/DataLoader.Core/Notifications/NotificationMessage.cs` | Subject + body value object |
| `src/DataLoader.Core/Notifications/NotificationOutcome.cs` | `Sent` / `Disabled` / `NothingToReport` / `Failed` |
| `src/DataLoader.Core/Notifications/INotificationPayload.cs` | `ShouldNotify` + `BuildMessage` — implemented by both report types |
| `src/DataLoader.Core/Notifications/INotificationSender.cs` | Transport seam |
| `src/DataLoader.Core/Notifications/NullNotificationSender.cs` | Used when disabled |
| `src/DataLoader.Core/Notifications/ErrorTextRedactor.cs` | Strips secrets out of vendor text before it can be emailed |
| `src/DataLoader.Core/Notifications/NotificationSettings.cs` | Bound from `Platform:Notifications`; also loadable straight from `IConfiguration` |
| `src/DataLoader.Core/Notifications/RunFailureReport.cs` | In-process payload: what failed in this invocation |
| `src/DataLoader.Core/Notifications/IFailureNotifier.cs` | Notifier seam |
| `src/DataLoader.Core/Notifications/FailureNotifier.cs` | Decides, builds, sends, never throws |
| `src/DataLoader.Core/Notifications/SmtpNotificationSender.cs` | `System.Net.Mail` transport |
| `src/DataLoader.Core/Watchdog/WatchdogSettings.cs` | Bound from `Platform:Watchdog` |
| `src/DataLoader.Core/Watchdog/WatchdogModels.cs` | `LoaderLastRun`, `StuckRun`, `RecentFailure`, `OverdueLoader` |
| `src/DataLoader.Core/Watchdog/WatchdogReport.cs` | Watchdog payload |
| `src/DataLoader.Core/Watchdog/WatchdogEvaluator.cs` | Pure decision function |
| `src/DataLoader.Core/Watchdog/IWatchdogRepository.cs` | Read seam |
| `src/DataLoader.Core/Watchdog/SqlWatchdogRepository.cs` | Three `SELECT`s against `core.*` |
| `src/DataLoader.Core/Watchdog/WatchdogRunner.cs` | Orchestrates read → evaluate → notify → exit code |
| `src/DataLoader.Core/Hosting/PlatformHost.cs` | **Modify** — stale-row plumbing + notifier call site |
| `src/DataLoader.Host/Program.cs` | **Modify** — widen try, bootstrap notifier, `--watchdog` mode |
| `src/DataLoader.Host/appsettings.json` | **Modify** — `Platform:Notifications`, `Platform:Watchdog` |
| `Watchdog.cmd` | **Create** — dev launcher matching the other 23 |
| `tests/DataLoader.Core.Tests/ErrorTextRedactorTests.cs` | New |
| `tests/DataLoader.Core.Tests/FailureNotifierTests.cs` | New |
| `tests/DataLoader.Core.Tests/RunFailureReportTests.cs` | New |
| `tests/DataLoader.Core.Tests/NotificationSettingsTests.cs` | New |
| `tests/DataLoader.Core.Tests/WatchdogEvaluatorTests.cs` | New |
| `tests/DataLoader.Core.Tests/PlatformHostNotificationTests.cs` | New |
| `tests/DataLoader.Host.Tests/*` | New project — argument parsing |

---

### Task 0: Branch

**Files:** none

- [ ] **Step 1: Create the feature branch**

The repository is currently on `master`. Do not commit this work there.

```bash
git checkout -b feature/failure-notifications
git status --porcelain --untracked-files=no | head
```

Expected: the branch is created. The working tree already contains many modified build artifacts under `publish/` and `**/obj/` — **leave them alone**. Every commit in this plan stages explicit paths, never `git add -A` or `git add .`.

---

### Task 1: Notification primitives

**Files:**
- Create: `src/DataLoader.Core/Notifications/NotificationMessage.cs`
- Create: `src/DataLoader.Core/Notifications/NotificationOutcome.cs`
- Create: `src/DataLoader.Core/Notifications/INotificationPayload.cs`
- Create: `src/DataLoader.Core/Notifications/INotificationSender.cs`
- Create: `src/DataLoader.Core/Notifications/NullNotificationSender.cs`

No tests in this task — these are declarations with no behaviour. Task 3 tests them through `FailureNotifier`.

- [ ] **Step 1: Create `NotificationMessage.cs`**

```csharp
namespace DataLoader.Core.Notifications;

/// <summary>
/// A notification ready for a transport. Plain text only — these are read on
/// phones at 6am, and HTML buys nothing that matters at that moment.
/// </summary>
public sealed record NotificationMessage(string Subject, string Body);
```

- [ ] **Step 2: Create `NotificationOutcome.cs`**

```csharp
namespace DataLoader.Core.Notifications;

/// <summary>
/// What a notification attempt actually did.
///
/// <para>This exists because two requirements would otherwise contradict each
/// other. The notifier must NEVER throw — a broken mail relay must not fail a
/// good load (see <see cref="FailureNotifier"/>). But the watchdog needs a
/// non-zero exit code when it could not deliver, because an undelivered
/// watchdog alert is indistinguishable from "all clear" and would hide
/// everything it exists to report.</para>
///
/// <para>So the notifier reports rather than throws: <see cref="PlatformHost"/>
/// ignores this value, and the watchdog acts on it.</para>
/// </summary>
public enum NotificationOutcome
{
    /// <summary>Delivered to the transport without error.</summary>
    Sent,

    /// <summary>Notifications are switched off in configuration.</summary>
    Disabled,

    /// <summary>Nothing was wrong, so nothing was sent. The common case.</summary>
    NothingToReport,

    /// <summary>The transport failed. Already logged; never thrown.</summary>
    Failed
}
```

- [ ] **Step 3: Create `INotificationPayload.cs`**

```csharp
namespace DataLoader.Core.Notifications;

/// <summary>
/// Something that might be worth telling a human about.
///
/// <para>Both notification sources implement this —
/// <see cref="RunFailureReport"/> for the in-process path and
/// <c>WatchdogReport</c> for the scheduled sweep — so
/// <see cref="FailureNotifier"/> has exactly one code path for sending, and
/// the two decisions that actually differ (is this worth reporting, and what
/// does it say) stay with the type that knows the answer.</para>
///
/// <para>Both members are pure. That is deliberate: it means every subject
/// line and every "should this even send" rule is unit-testable without a
/// notifier, a transport, a clock or a database.</para>
/// </summary>
public interface INotificationPayload
{
    /// <summary>True when a human needs to see this.</summary>
    bool ShouldNotify { get; }

    /// <summary>
    /// Renders the message, WITHOUT the subject prefix. <c>FailureNotifier</c>
    /// prepends that on the single shared send path, so no implementation can
    /// forget it and ship an alert whose DEV and PROD copies read identically.
    ///
    /// <para>Only called when <see cref="ShouldNotify"/> is true.</para>
    ///
    /// <para>🔒 Implementations MUST pass any vendor-supplied text through
    /// <c>ErrorTextRedactor</c> first — email leaves the machine, logs do not.
    /// <c>FailureNotifier</c> redacts the finished message again as a backstop,
    /// because a doc comment is not an enforcement mechanism and this platform
    /// has already leaked one API key through a path that trusted its caller.</para>
    /// </summary>
    NotificationMessage BuildMessage();
}
```

- [ ] **Step 4: Create `INotificationSender.cs`**

```csharp
namespace DataLoader.Core.Notifications;

/// <summary>
/// Puts a <see cref="NotificationMessage"/> on the wire.
///
/// <para>Separate from <see cref="IFailureNotifier"/> so that adding a channel
/// — Zoom, Teams, PagerDuty — is one new class and a config value, with no
/// change to any caller. Implementations MAY throw; <see cref="FailureNotifier"/>
/// is responsible for making sure that never escapes.</para>
/// </summary>
public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Create `NullNotificationSender.cs`**

```csharp
namespace DataLoader.Core.Notifications;

/// <summary>
/// Discards everything. Registered when <c>Platform:Notifications:Enabled</c>
/// is false, so that no call site needs a null check and the notifier's own
/// logic stays under test whether or not mail is switched on.
/// </summary>
public sealed class NullNotificationSender : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
```

- [ ] **Step 6: Build**

```bash
dotnet build src/DataLoader.Core/DataLoader.Core.csproj -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`. The `<see cref="FailureNotifier"/>` and `<c>WatchdogReport</c>` references point at types that do not exist yet — the first is written as `<see cref>` and **will** warn or error if doc-comment validation is on. If the build reports CS1574 for `FailureNotifier`, `PlatformHost` or `RunFailureReport`, change those specific `<see cref="X"/>` to `<c>X</c>` and rebuild. Tasks 3 and 5 create them.

- [ ] **Step 7: Commit**

```bash
git add src/DataLoader.Core/Notifications/
git commit -m "Add notification primitives: message, outcome, payload and transport seams"
```

---

### Task 2: `ErrorTextRedactor`

This is the security-critical class in the feature. Email leaves the machine; the log file does not. Two vendors in this platform are known to put secrets in text that ends up in an `ErrorMessage`:

- **AGSI echoes your own `x-key` back** in the error body's `message` field, as the literal text `API key:` followed by the key.
- **OilX puts `api_key` in the query string**, so any URL inside an exception is sensitive.

**Files:**
- Create: `tests/DataLoader.Core.Tests/ErrorTextRedactorTests.cs`
- Create: `src/DataLoader.Core/Notifications/ErrorTextRedactor.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/ErrorTextRedactorTests.cs`:

```csharp
using DataLoader.Core.Notifications;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// Guards the one genuinely new secret-exposure surface this feature adds:
/// email leaves the machine, whereas the log file it is derived from does not.
///
/// <para>The two shapes below are not hypothetical. AGSI echoes the caller's
/// own x-key back inside the error body's message field, and OilX carries its
/// api_key in the query string — so both already appear in real ErrorMessage
/// text on this platform.</para>
/// </summary>
public class ErrorTextRedactorTests
{
    private const string Mask = "***REDACTED***";

    [Fact]
    public void Redacts_AgsiStyle_ApiKeyEchoedInErrorBody()
    {
        var input = "access denied (API key: abc123XYZ)";

        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain("abc123XYZ", result);
        Assert.Contains(Mask, result);
    }

    [Fact]
    public void Redacts_OilXStyle_ApiKeyInQueryString()
    {
        var input = "GET https://api.oilx.co/files?api_key=sk-9f3a2b&feed=Flow failed with 422";

        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain("sk-9f3a2b", result);
        Assert.Contains("https://api.oilx.co/files", result);
    }

    [Theory]
    [InlineData("apikey=topsecret", "topsecret")]
    [InlineData("api_key: topsecret", "topsecret")]
    [InlineData("x-key: topsecret", "topsecret")]
    [InlineData("password=topsecret", "topsecret")]
    [InlineData("Password: topsecret", "topsecret")]
    [InlineData("token=topsecret", "topsecret")]
    [InlineData("client_secret=topsecret", "topsecret")]
    public void Redacts_KnownKeyBearingTokens(string input, string secret)
    {
        var result = ErrorTextRedactor.Redact(input);

        Assert.DoesNotContain(secret, result);
    }

    [Fact]
    public void LeavesOrdinaryErrorTextAlone()
    {
        // Over-redaction makes alerts useless, which is its own failure mode.
        var input = "Timeout expired after 300 seconds reading unit 2026-10-01 for Platts";

        Assert.Equal(input, ErrorTextRedactor.Redact(input));
    }

    [Fact]
    public void Truncate_CutsLongTextAndSaysSo()
    {
        var input = new string('x', 5000);

        var result = ErrorTextRedactor.Truncate(input, 2000);

        Assert.True(result.Length < 2100, $"expected ~2000 chars, got {result.Length}");
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void Truncate_LeavesShortTextUnchanged()
    {
        Assert.Equal("short", ErrorTextRedactor.Truncate("short", 2000));
    }

    [Fact]
    public void HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, ErrorTextRedactor.Redact(null));
        Assert.Equal(string.Empty, ErrorTextRedactor.Redact(""));
        Assert.Equal(string.Empty, ErrorTextRedactor.Truncate(null, 10));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ErrorTextRedactorTests"
```

Expected: compilation failure — `The name 'ErrorTextRedactor' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/DataLoader.Core/Notifications/ErrorTextRedactor.cs`:

```csharp
using System.Text.RegularExpressions;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Removes secrets from vendor-supplied text before it can be emailed.
///
/// <para>⚠ This is NOT belt-and-braces. Two vendors on this platform put
/// credentials into text that reaches <c>LoaderRunResult.ErrorMessage</c>:
/// AGSI echoes the caller's own <c>x-key</c> back inside the error body's
/// <c>message</c> field, and OilX carries its <c>api_key</c> in the query
/// string, so any URL inside an exception is sensitive.</para>
///
/// <para>Logs stay on the machine; email does not. Every piece of error text
/// must pass through here before it enters a <see cref="NotificationMessage"/>.</para>
///
/// <para>Deliberately conservative in the other direction too: over-redacting
/// produces alerts nobody can act on, which is its own failure. Only known
/// key-bearing tokens and URL query strings are touched.</para>
/// </summary>
public static class ErrorTextRedactor
{
    private const string Mask = "***REDACTED***";

    /// <summary>
    /// Strips the query string off any URL. OilX puts its api_key there, and a
    /// query string in an error message is never worth the risk of keeping.
    /// </summary>
    private static readonly Regex UrlQuery = new(
        @"(https?://[^\s?#""']+)\?[^\s""']*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Masks the value after a known key-bearing token. The separator covers
    /// both <c>name=value</c> (query/connection-string style) and
    /// <c>name: value</c> (the AGSI "API key: ..." prose shape). Longer
    /// alternatives come first so <c>api_key</c> is not matched as bare
    /// <c>key</c> with an <c>api_</c> prefix left dangling.
    /// </summary>
    private static readonly Regex KeyValue = new(
        @"\b(client_secret|api[\s_\-]?key|x-key|apikey|secret|password|passwd|pwd|token|key)\b\s*[:=]\s*""?([^\s""'&,;)\]]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns <paramref name="text"/> with secrets masked. Null becomes empty.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var result = UrlQuery.Replace(text, "$1");
        result = KeyValue.Replace(result, m => $"{m.Groups[1].Value}={Mask}");
        return result;
    }

    /// <summary>
    /// Caps length so one pathological exception cannot produce a
    /// multi-megabyte email. Null becomes empty.
    /// </summary>
    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + $"… [truncated, {text.Length} chars total]";
    }

    /// <summary>Redact then truncate — the combination every caller actually wants.</summary>
    public static string Clean(string? text, int maxLength = 2000) =>
        Truncate(Redact(text), maxLength);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~ErrorTextRedactorTests"
```

Expected: `Passed! - Failed: 0, Passed: 13`.

If `Redacts_OilXStyle_ApiKeyInQueryString` fails because the URL regex ate the trailing `" failed with 422"`, the character class `[^\s?#""']+` is the fix point — the URL must stop at whitespace.

- [ ] **Step 5: Commit**

```bash
git add src/DataLoader.Core/Notifications/ErrorTextRedactor.cs tests/DataLoader.Core.Tests/ErrorTextRedactorTests.cs
git commit -m "Add ErrorTextRedactor so vendor secrets cannot leave the box by email

AGSI echoes the caller's x-key back in its error body and OilX puts api_key
in the query string, so both already appear in real ErrorMessage text. Logs
stay on the machine; email does not."
```

---

### Task 3: `NotificationSettings`

**Files:**
- Create: `tests/DataLoader.Core.Tests/NotificationSettingsTests.cs`
- Create: `src/DataLoader.Core/Notifications/NotificationSettings.cs`

The unusual requirement here is **rejecting** `SmtpUsername`/`SmtpPassword`. The chosen relay (`armenergy-com.mail.protection.outlook.com`) advertises `STARTTLS` but no `AUTH` verb — it is Exchange Online's inbound MX connector, which takes no credential. If those properties simply did not exist, config binding would ignore them silently and someone would later "fix" the feature by adding a real password that does nothing. So the keys are detected in raw configuration and rejected loudly.

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/NotificationSettingsTests.cs`:

```csharp
using DataLoader.Core.Notifications;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DataLoader.Core.Tests;

public class NotificationSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void FromConfiguration_BindsTheSection()
    {
        var config = Config(
            ("Platform:Notifications:Enabled", "true"),
            ("Platform:Notifications:SmtpHost", "relay.example.com"),
            ("Platform:Notifications:SmtpPort", "25"),
            ("Platform:Notifications:UseSsl", "true"),
            ("Platform:Notifications:From", "svc@armenergy.com"),
            ("Platform:Notifications:To:0", "a@armenergy.com"),
            ("Platform:Notifications:To:1", "b@armenergy.com"),
            ("Platform:Notifications:SubjectPrefix", "[DL]"),
            ("Platform:Notifications:TimeoutSeconds", "45"));

        var settings = NotificationSettings.FromConfiguration(config);

        Assert.True(settings.Enabled);
        Assert.Equal("relay.example.com", settings.SmtpHost);
        Assert.Equal(25, settings.SmtpPort);
        Assert.True(settings.UseSsl);
        Assert.Equal("svc@armenergy.com", settings.From);
        Assert.Equal(new[] { "a@armenergy.com", "b@armenergy.com" }, settings.To);
        Assert.Equal("[DL]", settings.SubjectPrefix);
        Assert.Equal(45, settings.TimeoutSeconds);
    }

    [Fact]
    public void FromConfiguration_MissingSection_YieldsDisabledDefaults()
    {
        var settings = NotificationSettings.FromConfiguration(Config());

        Assert.False(settings.Enabled);
        Assert.Equal(25, settings.SmtpPort);
        Assert.True(settings.UseSsl);
        Assert.Equal("[DataLoader]", settings.SubjectPrefix);
        Assert.Equal(30, settings.TimeoutSeconds);
    }

    [Theory]
    [InlineData("SmtpUsername")]
    [InlineData("SmtpPassword")]
    public void FromConfiguration_RejectsCredentialKeys(string key)
    {
        // The chosen endpoint advertises STARTTLS but no AUTH verb. A credential
        // here does nothing, and config that LOOKS like a working secret invites
        // someone to put a real password in it later.
        var config = Config(($"Platform:Notifications:{key}", "whatever"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => NotificationSettings.FromConfiguration(config));

        Assert.Contains(key, ex.Message);
        Assert.DoesNotContain("whatever", ex.Message);   // never echo the value
    }

    [Fact]
    public void Validate_EnabledWithoutHost_Throws()
    {
        var settings = new NotificationSettings
        {
            Enabled = true, From = "a@armenergy.com", To = { "b@armenergy.com" }
        };

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_EnabledWithoutRecipients_Throws()
    {
        var settings = new NotificationSettings
        {
            Enabled = true, SmtpHost = "relay", From = "a@armenergy.com"
        };

        Assert.Throws<InvalidOperationException>(() => settings.Validate());
    }

    [Fact]
    public void Validate_Disabled_NeverThrows_EvenWhenEmpty()
    {
        // Must stay inert until IT confirms egress; an incomplete disabled
        // config must not stop a loader from running.
        new NotificationSettings { Enabled = false }.Validate();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NotificationSettingsTests"
```

Expected: compilation failure — `NotificationSettings` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/DataLoader.Core/Notifications/NotificationSettings.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Bound from the <c>Platform:Notifications</c> section.
///
/// <para>Note <see cref="FromConfiguration"/>, which builds this straight from
/// <see cref="IConfiguration"/> without DI. That is not a convenience: the
/// bootstrap notifier in <c>Program.cs</c> has to report failures that happen
/// while BUILDING the DI container, so it cannot resolve anything from it.</para>
/// </summary>
public sealed class NotificationSettings
{
    public const string SectionName = "Platform:Notifications";

    /// <summary>
    /// Ships false. The feature stays inert until IT confirms the server can
    /// egress on port 25 and that the tenant's "Reject Direct Send" setting is
    /// off (see the spec, §10).
    /// </summary>
    public bool Enabled { get; set; }

    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 25;

    /// <summary>
    /// STARTTLS. In <c>System.Net.Mail</c> on port 25 this means opportunistic
    /// TLS, not implicit TLS. The chosen endpoint advertises STARTTLS and
    /// Microsoft requires TLS 1.2 on that path, so this must stay true.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    public string From { get; set; } = string.Empty;

    /// <summary>
    /// ⚠ Internal recipients only. The relay is Exchange Online's inbound MX
    /// connector, which accepts mail only for the tenant's own accepted
    /// domains — anything outside <c>@armenergy.com</c> is relay-denied.
    /// </summary>
    public List<string> To { get; set; } = new();

    public string SubjectPrefix { get; set; } = "[DataLoader]";
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Keys that must never appear. The endpoint advertises no <c>AUTH</c>
    /// verb, so a credential here would be dead config that looks alive.
    /// </summary>
    private static readonly string[] ForbiddenKeys = { "SmtpUsername", "SmtpPassword" };

    public static NotificationSettings FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        foreach (var key in ForbiddenKeys)
        {
            if (section[key] is not null)
            {
                throw new InvalidOperationException(
                    $"'{SectionName}:{key}' is set, but the configured SMTP endpoint accepts no " +
                    "credentials — it advertises STARTTLS and no AUTH verb (Exchange Online direct " +
                    "send). Remove the key. If an authenticated relay is genuinely needed, that is a " +
                    "transport change, not a config change — see the failure-notifications design spec.");
            }
        }

        var settings = new NotificationSettings();
        section.Bind(settings);
        return settings;
    }

    /// <summary>
    /// Fails fast on a configuration that cannot work. Only checked when
    /// <see cref="Enabled"/> — a half-filled disabled block must never stop a
    /// loader from running.
    /// </summary>
    public void Validate()
    {
        if (!Enabled) return;

        if (string.IsNullOrWhiteSpace(SmtpHost))
            throw new InvalidOperationException($"'{SectionName}:SmtpHost' is required when Enabled is true.");
        if (string.IsNullOrWhiteSpace(From))
            throw new InvalidOperationException($"'{SectionName}:From' is required when Enabled is true.");
        if (To.Count == 0)
            throw new InvalidOperationException($"'{SectionName}:To' must list at least one recipient when Enabled is true.");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~NotificationSettingsTests"
```

Expected: `Passed! - Failed: 0, Passed: 8`.

`Microsoft.Extensions.Configuration.Binder` is already referenced by `DataLoader.Core`, so `section.Bind` resolves without a package change.

- [ ] **Step 5: Commit**

```bash
git add src/DataLoader.Core/Notifications/NotificationSettings.cs tests/DataLoader.Core.Tests/NotificationSettingsTests.cs
git commit -m "Add NotificationSettings, rejecting credential keys the relay cannot use"
```

---

### Task 4: `RunFailureReport`

**Files:**
- Create: `tests/DataLoader.Core.Tests/RunFailureReportTests.cs`
- Create: `src/DataLoader.Core/Notifications/RunFailureReport.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/RunFailureReportTests.cs`:

```csharp
using DataLoader.Core.Abstractions;
using DataLoader.Core.Notifications;
using Xunit;

namespace DataLoader.Core.Tests;

public class RunFailureReportTests
{
    private static LoaderRunResult Ok(string id) => new()
    {
        LoaderId = id, Success = true, WorkUnitsTotal = 10, WorkUnitsSucceeded = 10, RecordsProcessed = 500
    };

    private static LoaderRunResult Bad(string id, string error, int failed = 2, int total = 14) => new()
    {
        LoaderId = id, Success = false, ErrorMessage = error,
        WorkUnitsTotal = total, WorkUnitsFailed = failed, WorkUnitsSucceeded = total - failed
    };

    private static RunFailureReport Report(
        IEnumerable<LoaderRunResult>? results = null,
        int staleRowsClosed = 0,
        Exception? exception = null) => new()
    {
        HostName = "ARMH-OPSDB01",
        RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        StartedAtUtc = new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc),
        CompletedAtUtc = new DateTime(2026, 10, 24, 11, 5, 0, DateTimeKind.Utc),
        Results = (results ?? Array.Empty<LoaderRunResult>()).ToList(),
        StaleRowsClosed = staleRowsClosed,
        Exception = exception
    };

    // ------------------------------------------------------------ ShouldNotify

    [Fact]
    public void ShouldNotify_False_WhenEverythingSucceeded()
    {
        Assert.False(Report(new[] { Ok("Platts"), Ok("Vulcan") }).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_WhenAnyLoaderFailed()
    {
        Assert.True(Report(new[] { Ok("Platts"), Bad("Vulcan", "boom") }).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_WhenStaleRowsWereClosed_EvenThoughTheRunSucceeded()
    {
        // Stale rows mean an EARLIER run was killed. Nothing else reports that
        // from inside the process, so a clean run still has news to deliver.
        Assert.True(Report(new[] { Ok("Platts") }, staleRowsClosed: 3).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_OnUnhandledException()
    {
        Assert.True(Report(exception: new InvalidOperationException("bang")).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_False_ForAnOverlapSkip()
    {
        // The overlap guard records Success = true with an explanatory message.
        // Another process is already running the loader; that is not a failure.
        var skipped = new LoaderRunResult
        {
            LoaderId = "Platts", Success = true,
            ErrorMessage = "Skipped — another instance already running"
        };

        Assert.False(Report(new[] { skipped }).ShouldNotify);
    }

    // ------------------------------------------------------------- the subject

    [Fact]
    public void Subject_SingleFailingLoader_NamesItAndItsUnitCounts()
    {
        var subject = Report(new[] { Bad("Platts", "boom") }).BuildMessage().Subject;

        // No prefix here — FailureNotifier prepends it on the single send path.
        Assert.Equal("FAILED: Platts on ARMH-OPSDB01 (2/14 units failed)", subject);
    }

    [Fact]
    public void Subject_SeveralFailingLoaders_CountsThem()
    {
        var subject = Report(new[] { Bad("Platts", "a"), Bad("Vulcan", "b"), Ok("CWG") })
            .BuildMessage().Subject;

        Assert.Equal("FAILED: 2 loaders on ARMH-OPSDB01", subject);
    }

    [Fact]
    public void Subject_UnhandledException_NamesTheExceptionType()
    {
        var subject = Report(exception: new InvalidOperationException("bang"))
            .BuildMessage().Subject;

        Assert.Contains("InvalidOperationException", subject);
        Assert.Contains("ARMH-OPSDB01", subject);
    }

    [Fact]
    public void Subject_OnlyStaleRows_IsAWarningNotAFailure()
    {
        var subject = Report(new[] { Ok("Platts") }, staleRowsClosed: 3)
            .BuildMessage().Subject;

        Assert.Contains("WARNING", subject);
        Assert.Contains("3", subject);
    }

    // ---------------------------------------------------------------- the body

    [Fact]
    public void Body_ContainsRunIdHostPerLoaderDetailAndTheLogPath()
    {
        var body = Report(new[] { Ok("Platts"), Bad("Vulcan", "boom") }).BuildMessage().Body;

        Assert.Contains("11111111-2222-3333-4444-555555555555", body);
        Assert.Contains("ARMH-OPSDB01", body);
        Assert.Contains("Platts", body);
        Assert.Contains("Vulcan", body);
        Assert.Contains("boom", body);
        Assert.Contains(@"C:\ETL_Tools\DataLoaderPlatform\Log", body);
    }

    [Fact]
    public void Body_IncludesTheStackTraceWhenThereWasAnException()
    {
        Exception captured;
        try { throw new InvalidOperationException("bang"); }
        catch (Exception ex) { captured = ex; }

        var body = Report(exception: captured).BuildMessage().Body;

        Assert.Contains("InvalidOperationException", body);
        Assert.Contains("bang", body);
    }

    [Fact]
    public void Body_RedactsSecretsOutOfLoaderErrorMessages()
    {
        // The whole point of ErrorTextRedactor — proven at the boundary that
        // actually sends, not only in the redactor's own unit tests.
        var body = Report(new[] { Bad("AGSI", "access denied (API key: abc123XYZ)") })
            .BuildMessage().Body;

        Assert.DoesNotContain("abc123XYZ", body);
        Assert.Contains("REDACTED", body);
    }

    [Fact]
    public void Body_TruncatesAPathologicallyLongErrorMessage()
    {
        var body = Report(new[] { Bad("Platts", new string('x', 50_000)) })
            .BuildMessage().Body;

        Assert.True(body.Length < 10_000, $"body was {body.Length} chars");
        Assert.Contains("truncated", body);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~RunFailureReportTests"
```

Expected: compilation failure — `RunFailureReport` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/DataLoader.Core/Notifications/RunFailureReport.cs`:

```csharp
using System.Text;
using DataLoader.Core.Abstractions;

namespace DataLoader.Core.Notifications;

/// <summary>
/// What one host invocation has to report. Built by <c>PlatformHost</c> at the
/// end of a run, and by <c>Program.cs</c> when the process fails before a host
/// even exists.
///
/// <para>Both members of <see cref="INotificationPayload"/> are pure here, so
/// every rule about what counts as newsworthy and every word of the resulting
/// email is unit-testable with no mail server and no database.</para>
/// </summary>
public sealed class RunFailureReport : INotificationPayload
{
    /// <summary>Where this ran. The first thing anyone reading the alert wants.</summary>
    public required string HostName { get; init; }

    public Guid RunId { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }

    public IReadOnlyList<LoaderRunResult> Results { get; init; } = Array.Empty<LoaderRunResult>();

    /// <summary>
    /// How many <c>core.LoadLog</c> rows this run closed on behalf of EARLIER
    /// runs. Non-zero means a previous invocation was killed mid-unit — news
    /// worth sending even when the current run succeeded, because nothing else
    /// inside the process reports it.
    /// </summary>
    public int StaleRowsClosed { get; init; }

    /// <summary>Set when the process failed outright rather than a loader failing.</summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Names the phase for an <see cref="Exception"/> that happened outside a
    /// loader — "Bootstrap" when the DI container or configuration failed.
    /// </summary>
    public string? FailedStage { get; init; }

    private const int MaxErrorChars = 2000;
    private const string LogPathHint = @"C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log";

    private IReadOnlyList<LoaderRunResult> Failures =>
        Results.Where(r => !r.Success).ToList();

    public bool ShouldNotify =>
        Exception is not null || StaleRowsClosed > 0 || Results.Any(r => !r.Success);

    /// <summary>
    /// No subject prefix here — <see cref="FailureNotifier"/> prepends it on the
    /// single shared send path, so no payload can forget it and ship an alert
    /// whose DEV and PROD copies read identically.
    /// </summary>
    public NotificationMessage BuildMessage() => new(BuildSubject(), BuildBody());

    private string BuildSubject()
    {
        if (Exception is not null)
        {
            var stage = string.IsNullOrWhiteSpace(FailedStage) ? "run" : FailedStage;
            return $"FAILED: {stage} on {HostName} — {Exception.GetType().Name}";
        }

        var failures = Failures;
        if (failures.Count == 1)
        {
            var f = failures[0];
            return $"FAILED: {f.LoaderId} on {HostName} " +
                   $"({f.WorkUnitsFailed}/{f.WorkUnitsTotal} units failed)";
        }
        if (failures.Count > 1)
        {
            return $"FAILED: {failures.Count} loaders on {HostName}";
        }

        // Nothing failed now, so the only reason we are here is orphaned rows.
        return $"WARNING: {StaleRowsClosed} orphaned load-log row(s) on {HostName}";
    }

    private string BuildBody()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Host:      {HostName}");
        sb.AppendLine($"Run id:    {RunId}");
        sb.AppendLine($"Started:   {StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Completed: {CompletedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Duration:  {CompletedAtUtc - StartedAtUtc:hh\\:mm\\:ss}");
        sb.AppendLine();

        if (Exception is not null)
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(FailedStage)
                ? "UNHANDLED EXCEPTION"
                : $"UNHANDLED EXCEPTION during {FailedStage}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(ErrorTextRedactor.Clean(Exception.ToString(), MaxErrorChars));
            sb.AppendLine();
        }

        if (StaleRowsClosed > 0)
        {
            sb.AppendLine($"{StaleRowsClosed} load-log row(s) from an earlier run were left open — " +
                          "that run was killed before it could close them.");
            sb.AppendLine();
        }

        if (Results.Count > 0)
        {
            sb.AppendLine("Loaders");
            sb.AppendLine(new string('-', 60));
            foreach (var r in Results.OrderBy(r => r.Success).ThenBy(r => r.LoaderId))
            {
                sb.AppendLine($"  {(r.Success ? "OK  " : "FAIL")} {r.LoaderId}: " +
                              $"{r.WorkUnitsSucceeded}/{r.WorkUnitsTotal} ok, " +
                              $"{r.WorkUnitsSkipped} skipped, {r.WorkUnitsFailed} failed, " +
                              $"{r.RecordsProcessed} records");
                if (!string.IsNullOrEmpty(r.ErrorMessage))
                    sb.AppendLine($"       {ErrorTextRedactor.Clean(r.ErrorMessage, MaxErrorChars)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"Full output: {LogPathHint}");
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~RunFailureReportTests"
```

Expected: `Passed! - Failed: 0, Passed: 13`.

- [ ] **Step 5: Commit**

```bash
git add src/DataLoader.Core/Notifications/RunFailureReport.cs tests/DataLoader.Core.Tests/RunFailureReportTests.cs
git commit -m "Add RunFailureReport with pure ShouldNotify and message building"
```

---

### Task 5: `FailureNotifier` and `SmtpNotificationSender`

**Files:**
- Create: `tests/DataLoader.Core.Tests/FailureNotifierTests.cs`
- Create: `src/DataLoader.Core/Notifications/IFailureNotifier.cs`
- Create: `src/DataLoader.Core/Notifications/FailureNotifier.cs`
- Create: `src/DataLoader.Core/Notifications/SmtpNotificationSender.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/FailureNotifierTests.cs`:

```csharp
using DataLoader.Core.Abstractions;
using DataLoader.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataLoader.Core.Tests;

public class FailureNotifierTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeSender : INotificationSender
    {
        public readonly List<NotificationMessage> Sent = new();
        public Exception? ThrowThis;
        public TimeSpan Delay = TimeSpan.Zero;

        public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            if (ThrowThis is not null) throw ThrowThis;
            Sent.Add(message);
        }
    }

    private sealed class FakePayload : INotificationPayload
    {
        public bool ShouldNotify { get; init; }
        public string Body { get; init; } = "body";
        public NotificationMessage BuildMessage() => new("subject", Body);
    }

    private static FailureNotifier Build(FakeSender sender, bool enabled = true, int timeoutSeconds = 30) =>
        new(sender,
            new NotificationSettings
            {
                Enabled = enabled,
                SmtpHost = "relay",
                From = "svc@armenergy.com",
                To = { "ops@armenergy.com" },
                TimeoutSeconds = timeoutSeconds
            },
            NullLogger<FailureNotifier>.Instance);

    // --------------------------------------------------------------- outcomes

    [Fact]
    public async Task NothingToReport_WhenPayloadSaysSo_AndNothingIsSent()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = false }, default);

        Assert.Equal(NotificationOutcome.NothingToReport, outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Sent_WhenPayloadSaysSo()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Sent, outcome);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Disabled_SendsNothing_EvenWhenThereIsNewsToReport()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender, enabled: false)
            .NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Disabled, outcome);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task UsesTheConfiguredSubjectPrefix()
    {
        var sender = new FakeSender();
        var notifier = new FailureNotifier(
            sender,
            new NotificationSettings
            {
                Enabled = true, SmtpHost = "relay", From = "a@b.com",
                To = { "c@d.com" }, SubjectPrefix = "[CUSTOM]"
            },
            NullLogger<FailureNotifier>.Instance);

        await notifier.NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        // The payload returns a bare "subject"; prefixing is the notifier's job,
        // so a payload cannot ship an alert with DEV and PROD reading alike.
        Assert.Equal("[CUSTOM] subject", sender.Sent[0].Subject);
    }

    [Fact]
    public async Task RedactsTheFinishedMessage_EvenWhenThePayloadForgot()
    {
        // Defence in depth. RunFailureReport does redact, but the contract is a
        // doc comment, and this platform has already leaked an API key through a
        // path that trusted its caller. A future payload must not be able to
        // reintroduce that by forgetting.
        var sender = new FakeSender();
        var payload = new FakePayload
        {
            ShouldNotify = true,
            Body = "access denied (API key: abc123XYZ)"
        };

        await Build(sender).NotifyAsync(payload, default);

        Assert.DoesNotContain("abc123XYZ", sender.Sent[0].Body);
    }

    // ------------------------------------------------- it must NEVER propagate

    [Fact]
    public async Task NeverThrows_WhenTheSenderThrows()
    {
        // A broken relay must not fail a good load. This is the single most
        // important behaviour in the class.
        var sender = new FakeSender { ThrowThis = new InvalidOperationException("relay down") };

        var outcome = await Build(sender).NotifyAsync(new FakePayload { ShouldNotify = true }, default);

        Assert.Equal(NotificationOutcome.Failed, outcome);
    }

    [Fact]
    public async Task NeverThrows_WhenBuildingTheMessageThrows()
    {
        var sender = new FakeSender();

        var outcome = await Build(sender).NotifyAsync(new ExplodingPayload(), default);

        Assert.Equal(NotificationOutcome.Failed, outcome);
        Assert.Empty(sender.Sent);
    }

    private sealed class ExplodingPayload : INotificationPayload
    {
        public bool ShouldNotify => true;
        public NotificationMessage BuildMessage() =>
            throw new InvalidOperationException("bad payload");
    }

    [Fact]
    public async Task TimesOut_RatherThanHangingTheScheduledTask()
    {
        var sender = new FakeSender { Delay = TimeSpan.FromSeconds(10) };

        var started = DateTime.UtcNow;
        var outcome = await Build(sender, timeoutSeconds: 1)
            .NotifyAsync(new FakePayload { ShouldNotify = true }, default);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(NotificationOutcome.Failed, outcome);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"took {elapsed.TotalSeconds:F1}s — the timeout did not apply");
    }

    // --------------------------------------------- end to end over a real report

    [Fact]
    public async Task SendsARealRunFailureReport_WithRedactionApplied()
    {
        var sender = new FakeSender();
        var report = new RunFailureReport
        {
            HostName = "ARMH-OPSDB01",
            RunId = Guid.NewGuid(),
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Results = new List<LoaderRunResult>
            {
                LoaderRunResult.Failed("AGSI", "access denied (API key: abc123XYZ)", TimeSpan.Zero)
            }
        };

        var outcome = await Build(sender).NotifyAsync(report, default);

        Assert.Equal(NotificationOutcome.Sent, outcome);
        Assert.Contains("AGSI", sender.Sent[0].Subject);
        Assert.DoesNotContain("abc123XYZ", sender.Sent[0].Body);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~FailureNotifierTests"
```

Expected: compilation failure — `FailureNotifier` does not exist.

- [ ] **Step 3: Create `IFailureNotifier.cs`**

```csharp
namespace DataLoader.Core.Notifications;

/// <summary>
/// Reports a failure to a human. One method, because both sources — the
/// in-process run report and the watchdog sweep — arrive as an
/// <see cref="INotificationPayload"/>.
/// </summary>
public interface IFailureNotifier
{
    /// <summary>
    /// Sends <paramref name="payload"/> if it has anything to say.
    ///
    /// <para>⚠ NEVER throws, whatever the transport does. Returns what
    /// happened instead, so a caller that genuinely needs to know — the
    /// watchdog, whose exit code depends on delivery — can act on it, while
    /// the loader host can safely ignore it.</para>
    /// </summary>
    Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create `FailureNotifier.cs`**

```csharp
using Microsoft.Extensions.Logging;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Decides whether a payload is worth sending, renders it, and hands it to the
/// transport — swallowing every failure on the way.
///
/// <para>⚠ The swallowing is the point, not laziness. This runs at the end of
/// an ETL run that may have just loaded millions of rows successfully; an
/// unreachable mail relay must not turn that into a failed job. The same
/// reasoning already governs <c>SqlAdditionalProcessRunner</c>.</para>
///
/// <para>The timeout matters for the same reason: these processes run as
/// scheduled tasks, and a relay that accepts a connection then stalls would
/// otherwise hold the task open indefinitely.</para>
/// </summary>
public sealed class FailureNotifier : IFailureNotifier
{
    private readonly INotificationSender _sender;
    private readonly NotificationSettings _settings;
    private readonly ILogger<FailureNotifier> _logger;

    public FailureNotifier(
        INotificationSender sender,
        NotificationSettings settings,
        ILogger<FailureNotifier> logger)
    {
        _sender = sender;
        _settings = settings;
        _logger = logger;
    }

    public async Task<NotificationOutcome> NotifyAsync(
        INotificationPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            if (!payload.ShouldNotify) return NotificationOutcome.NothingToReport;
            if (!_settings.Enabled)
            {
                _logger.LogInformation(
                    "Notifications are disabled; not sending an alert that would otherwise go out");
                return NotificationOutcome.Disabled;
            }

            var message = Finalise(payload.BuildMessage());

            // The run may already have been cancelled — CancellationToken.None
            // deliberately, so an operator stopping the job still gets told why.
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));

            await _sender.SendAsync(message, timeout.Token).ConfigureAwait(false);

            _logger.LogInformation("Sent failure notification: {Subject}", message.Subject);
            return NotificationOutcome.Sent;
        }
        catch (Exception ex)
        {
            // Never rethrow. The run's own outcome is not this component's to change.
            _logger.LogError(ex, "Failed to send failure notification");
            return NotificationOutcome.Failed;
        }
    }

    /// <summary>
    /// Applies the two things that must happen on EVERY message, whatever the
    /// payload did.
    ///
    /// <para>The subject prefix lives here rather than in each payload so none
    /// of them can omit it and produce an alert whose DEV and PROD copies read
    /// identically.</para>
    ///
    /// <para>🔒 The redaction pass is defence in depth. Payloads are already
    /// required to redact, but a doc comment is not an enforcement mechanism,
    /// and this platform has already leaked an API key through a path that
    /// trusted its caller. Redaction is idempotent, so redacting twice costs
    /// nothing and a future payload cannot reintroduce the leak by forgetting.</para>
    /// </summary>
    private NotificationMessage Finalise(NotificationMessage message) =>
        new($"{_settings.SubjectPrefix} {ErrorTextRedactor.Redact(message.Subject)}".Trim(),
            ErrorTextRedactor.Redact(message.Body));
}
```

- [ ] **Step 5: Create `SmtpNotificationSender.cs`**

```csharp
using System.Net.Mail;

namespace DataLoader.Core.Notifications;

/// <summary>
/// Sends over SMTP with <c>System.Net.Mail</c>.
///
/// <para>⚠ No credentials, by design. The configured endpoint
/// (<c>armenergy-com.mail.protection.outlook.com</c>) is Exchange Online's
/// inbound MX connector — an <c>EHLO</c> probe on 2026-10-24 advertised
/// <c>STARTTLS</c> and NO <c>AUTH</c> verb. Setting
/// <see cref="SmtpClient.Credentials"/> against it does nothing useful, which
/// is why <see cref="NotificationSettings"/> rejects credential keys outright
/// rather than binding them.</para>
///
/// <para>⚠ Consequence of the same fact: recipients must be inside the tenant's
/// accepted domains. Direct send relay-denies external addresses.</para>
///
/// <para>On port 25, <see cref="SmtpClient.EnableSsl"/> means STARTTLS, not
/// implicit TLS. Microsoft requires TLS 1.2 on this path.</para>
///
/// <para>This class is deliberately thin and has no unit tests — there is
/// nothing here to assert that would not just be asserting
/// <c>System.Net.Mail</c>. All the behaviour worth testing lives in
/// <see cref="FailureNotifier"/> and the payloads.</para>
/// </summary>
public sealed class SmtpNotificationSender : INotificationSender
{
    private readonly NotificationSettings _settings;

    public SmtpNotificationSender(NotificationSettings settings)
    {
        settings.Validate();
        _settings = settings;
    }

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
        {
            EnableSsl = _settings.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = null,
            Timeout = Math.Max(1, _settings.TimeoutSeconds) * 1000
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(_settings.From),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false
        };
        foreach (var to in _settings.To) mail.To.Add(to);

        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~FailureNotifierTests"
```

Expected: `Passed! - Failed: 0, Passed: 8`.

- [ ] **Step 7: Commit**

```bash
git add src/DataLoader.Core/Notifications/IFailureNotifier.cs src/DataLoader.Core/Notifications/FailureNotifier.cs src/DataLoader.Core/Notifications/SmtpNotificationSender.cs tests/DataLoader.Core.Tests/FailureNotifierTests.cs
git commit -m "Add FailureNotifier and the SMTP transport

The notifier reports rather than throws: a broken relay must never fail a
load, but the watchdog's exit code depends on knowing delivery failed."
```

---

### Task 6: Wire the notifier into `PlatformHost`

**Files:**
- Modify: `src/DataLoader.Core/Hosting/PlatformHost.cs`
- Create: `tests/DataLoader.Core.Tests/PlatformHostNotificationTests.cs`

Two changes: `SweepStaleLoadLogsAsync` must return its count instead of discarding it, and the notifier is called after `LogSummary`.

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/PlatformHostNotificationTests.cs`:

```csharp
using DataLoader.Core.Abstractions;
using DataLoader.Core.Concurrency;
using DataLoader.Core.Configuration;
using DataLoader.Core.Hosting;
using DataLoader.Core.Notifications;
using DataLoader.Core.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

/// <summary>
/// The host's side of failure notification: who gets told, and when nobody
/// should be.
/// </summary>
public class PlatformHostNotificationTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class FakeModule : ILoaderModule
    {
        private readonly Func<LoaderRunContext, Task<LoaderRunResult>> _run;
        public FakeModule(string id, Func<LoaderRunContext, Task<LoaderRunResult>> run)
        {
            LoaderId = id; _run = run;
        }
        public string LoaderId { get; }
        public string DisplayName => LoaderId;
        public void RegisterServices(IServiceCollection services, IConfiguration configuration) { }
        public Task<LoaderRunResult> RunAsync(IServiceProvider services, LoaderRunContext context) => _run(context);

        public static FakeModule Succeeding(string id) => new(id, _ =>
            Task.FromResult(new LoaderRunResult { LoaderId = id, Success = true }));
        public static FakeModule Failing(string id) => new(id, _ =>
            Task.FromResult(LoaderRunResult.Failed(id, "boom", TimeSpan.Zero)));
    }

    private sealed class FakeRunLog : ILoadLogRepository
    {
        public Task BeginRunAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(Guid runId, bool success, CancellationToken ct) => Task.CompletedTask;
        public Task<LoadLogHandle?> BeginAsync(string l, Guid r, string k, string d, CancellationToken ct) =>
            Task.FromResult<LoadLogHandle?>(null);
        public Task CompleteSuccessAsync(LoadLogHandle h, int n, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteFailureAsync(LoadLogHandle h, string m, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeLock : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOverlapGuard : ILoaderOverlapGuard
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string loaderId, CancellationToken ct) =>
            Task.FromResult<IAsyncDisposable?>(new FakeLock());
    }

    private sealed class FakeAdditionalProcesses : IAdditionalProcessRunner
    {
        public Task RunAsync(string loaderId, Guid runId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeSweeper : ILoadLogSweeper
    {
        public int Returns;
        public Task<int> AbandonStaleAsync(Guid currentRunId, CancellationToken ct) => Task.FromResult(Returns);
    }

    /// <summary>Captures what the host decided to report, without a transport.</summary>
    private sealed class CapturingNotifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();
        public bool Throw;

        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            // The real notifier never throws. This proves the host does not
            // depend on that politeness.
            return Throw
                ? Task.FromException<NotificationOutcome>(new InvalidOperationException("notifier blew up"))
                : Task.FromResult(NotificationOutcome.Sent);
        }

        public RunFailureReport Single() => Assert.IsType<RunFailureReport>(Assert.Single(Payloads));
    }

    private static PlatformHost Build(
        IEnumerable<ILoaderModule> modules,
        IFailureNotifier? notifier = null,
        ILoadLogSweeper? sweeper = null)
        => new(
            new ServiceCollection().BuildServiceProvider(),
            modules.ToList(),
            new FakeRunLog(),
            new FakeOverlapGuard(),
            new FakeAdditionalProcesses(),
            Options.Create(new PlatformSettings { MaxConcurrentLoaders = 1, AbandonStaleLoadLogs = true }),
            NullLogger<PlatformHost>.Instance,
            sweeper,
            notifier);

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Notifies_WhenALoaderFails()
    {
        var notifier = new CapturingNotifier();

        var exit = await Build(new[] { FakeModule.Failing("Platts") }, notifier).RunAsync(default);

        Assert.Equal(1, exit);
        var report = notifier.Single();
        Assert.True(report.ShouldNotify);
        Assert.Equal("Platts", Assert.Single(report.Results).LoaderId);
    }

    [Fact]
    public async Task DoesNotNotify_WhenEverythingSucceeded()
    {
        var notifier = new CapturingNotifier();

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, notifier).RunAsync(default);

        Assert.Equal(0, exit);
        // The host may still hand over a report; what matters is that it says
        // there is nothing to send.
        foreach (var p in notifier.Payloads) Assert.False(p.ShouldNotify);
    }

    [Fact]
    public async Task Notifies_WhenAnEarlierRunLeftRowsOpen_EvenThoughThisRunSucceeded()
    {
        var notifier = new CapturingNotifier();
        var sweeper = new FakeSweeper { Returns = 3 };

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, notifier, sweeper).RunAsync(default);

        Assert.Equal(0, exit);                 // this run was fine
        var report = notifier.Single();
        Assert.Equal(3, report.StaleRowsClosed);
        Assert.True(report.ShouldNotify);      // but there is still news
    }

    [Fact]
    public async Task TheReportCarriesTheRunIdAndHostName()
    {
        var notifier = new CapturingNotifier();

        await Build(new[] { FakeModule.Failing("Platts") }, notifier).RunAsync(default);

        var report = notifier.Single();
        Assert.NotEqual(Guid.Empty, report.RunId);
        Assert.False(string.IsNullOrWhiteSpace(report.HostName));
    }

    [Fact]
    public async Task ANotifierThatThrowsDoesNotChangeTheRunsOutcome()
    {
        // Belt and braces: the real notifier swallows everything, but the host
        // must not rely on that.
        var notifier = new CapturingNotifier { Throw = true };

        var exit = await Build(new[] { FakeModule.Failing("Platts") }, notifier).RunAsync(default);

        Assert.Equal(1, exit);   // the loader's failure, not the notifier's
    }

    [Fact]
    public async Task ASucceedingRunWithAThrowingNotifierStillExitsZero()
    {
        var notifier = new CapturingNotifier { Throw = true };
        var sweeper = new FakeSweeper { Returns = 2 };   // forces a notify attempt

        var exit = await Build(new[] { FakeModule.Succeeding("Platts") }, notifier, sweeper).RunAsync(default);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task WorksWithNoNotifierRegistered()
    {
        // The parameter is optional so existing host tests keep compiling and
        // so a deployment with notifications absent still runs.
        var exit = await Build(new[] { FakeModule.Failing("Platts") }).RunAsync(default);

        Assert.Equal(1, exit);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~PlatformHostNotificationTests"
```

Expected: compilation failure — `PlatformHost` has no constructor taking a 9th argument.

- [ ] **Step 3: Add the notifier field and constructor parameter**

In `src/DataLoader.Core/Hosting/PlatformHost.cs`, add the using and the field:

```csharp
using DataLoader.Core.Notifications;
```

Add after the `_loadLogSweeper` field declaration:

```csharp
    private readonly IFailureNotifier? _notifier;
```

Change the constructor signature's tail — it currently ends with
`ILoadLogSweeper? loadLogSweeper = null)`. Make it:

```csharp
        ILoadLogSweeper? loadLogSweeper = null,
        IFailureNotifier? notifier = null)
```

and add to the body, after `_loadLogSweeper = loadLogSweeper;`:

```csharp
        _notifier = notifier;
```

Both new parameters stay optional so the existing `PlatformHostTests.Build` helper keeps compiling unchanged.

- [ ] **Step 4: Make the sweeper return its count**

Change the signature of `SweepStaleLoadLogsAsync` from `private async Task SweepStaleLoadLogsAsync(` to:

```csharp
    private async Task<int> SweepStaleLoadLogsAsync(Guid runId, CancellationToken cancellationToken)
```

Inside it, replace `if (!_settings.AbandonStaleLoadLogs || _loadLogSweeper is null) return;` with:

```csharp
        if (!_settings.AbandonStaleLoadLogs || _loadLogSweeper is null) return 0;
```

In the `try`, after the existing `LogWarning` call, add:

```csharp
            return closed;
```

In the `catch`, after the existing `LogError` call, add:

```csharp
            return 0;
```

Add one line to the XML doc, above `</summary>`:

```csharp
    /// <para>Returns how many rows were closed. A non-zero count is itself reportable —
    /// it means an earlier invocation was killed — so the caller forwards it into the
    /// run's failure report rather than only logging it.</para>
```

- [ ] **Step 5: Capture the count and notify**

In `RunAsync`, change:

```csharp
        await SweepStaleLoadLogsAsync(runId, cancellationToken).ConfigureAwait(false);
```

to:

```csharp
        var staleRowsClosed = await SweepStaleLoadLogsAsync(runId, cancellationToken).ConfigureAwait(false);
```

Then replace the final two lines of `RunAsync`:

```csharp
        LogSummary(results, sw.Elapsed);
        return allSucceeded ? 0 : 1;
```

with:

```csharp
        LogSummary(results, sw.Elapsed);
        await NotifyAsync(runId, startedAt, results, staleRowsClosed).ConfigureAwait(false);
        return allSucceeded ? 0 : 1;
```

- [ ] **Step 6: Add the `NotifyAsync` helper**

Add this method to `PlatformHost`, after `LogSummary`:

```csharp
    /// <summary>
    /// Emails the run's outcome when there is something to say.
    ///
    /// <para>Placed AFTER the finally that closes <c>core.LoaderRun</c>, not inside it:
    /// that row should close as promptly as possible and SMTP is not prompt.</para>
    ///
    /// <para>Never reached on cancellation — <c>ParallelRunner</c> rethrows
    /// <see cref="OperationCanceledException"/>, so a cancelled run leaves via the
    /// finally. That is deliberate. An operator pressing Ctrl+C or ending the scheduled
    /// task is not a failure; a KILLED process is, and nothing in-process can report
    /// that, which is what the watchdog exists for.</para>
    ///
    /// <para>The try/catch is belt and braces. <see cref="FailureNotifier"/> already
    /// swallows everything, but a run that loaded millions of rows must not be reported
    /// as failed because a mail relay was down, so the host does not rely on another
    /// component's politeness.</para>
    /// </summary>
    private async Task NotifyAsync(
        Guid runId, DateTime startedAt, IReadOnlyList<LoaderRunResult> results, int staleRowsClosed)
    {
        if (_notifier is null) return;

        try
        {
            var report = new RunFailureReport
            {
                HostName = Environment.MachineName,
                RunId = runId,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                Results = results,
                StaleRowsClosed = staleRowsClosed
            };

            // CancellationToken.None — an operator stopping the job should still
            // learn why it was failing.
            await _notifier.NotifyAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure notification threw; the run's own outcome is unchanged");
        }
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release
```

Expected: all tests pass, including the pre-existing `PlatformHostTests`. If any of those broke, the new constructor parameters were not made optional.

- [ ] **Step 8: Commit**

```bash
git add src/DataLoader.Core/Hosting/PlatformHost.cs tests/DataLoader.Core.Tests/PlatformHostNotificationTests.cs
git commit -m "Notify from PlatformHost on failure or orphaned load-log rows

The sweeper's count was previously logged and discarded; it is the only
in-process evidence that an EARLIER run was killed, so it now reaches the
report and sends even when the current run succeeded."
```

---

### Task 7: `WatchdogEvaluator`

**Files:**
- Create: `src/DataLoader.Core/Watchdog/WatchdogSettings.cs`
- Create: `src/DataLoader.Core/Watchdog/WatchdogModels.cs`
- Create: `src/DataLoader.Core/Watchdog/WatchdogReport.cs`
- Create: `src/DataLoader.Core/Watchdog/WatchdogEvaluator.cs`
- Create: `tests/DataLoader.Core.Tests/WatchdogEvaluatorTests.cs`

⚠ **The time-base trap.** `core.LoaderRun_Summary.StartTime` is already **Central** — production's `usp_CompleteLoaderRun` converts it with `AT TIME ZONE 'UTC' AT TIME ZONE 'Central Standard Time'`. `core.LoaderRun.StartedAtUtc` is raw **UTC**. Comparing a Central timestamp against a UTC `now` makes every loader look 5-6 hours overdue. The evaluator's parameter is named `nowCentral` so the caller cannot pass the wrong one without it reading obviously wrong, and a test pins the behaviour.

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/WatchdogEvaluatorTests.cs`:

```csharp
using DataLoader.Core.Watchdog;
using Xunit;

namespace DataLoader.Core.Tests;

public class WatchdogEvaluatorTests
{
    private static readonly DateTime NowCentral = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Unspecified);

    private static WatchdogSettings Settings(params (string Loader, int MaxGapHours)[] loaders)
    {
        var s = new WatchdogSettings { StuckAfterHours = 6, ReportRecentFailureHours = 24 };
        foreach (var (loader, gap) in loaders)
            s.Loaders[loader] = new WatchdogLoaderSettings { MaxGapHours = gap };
        return s;
    }

    private static WatchdogReport Evaluate(
        WatchdogSettings settings,
        IEnumerable<LoaderLastRun>? lastRuns = null,
        IEnumerable<StuckRun>? stuck = null,
        IEnumerable<RecentFailure>? failures = null)
        => WatchdogEvaluator.Evaluate(
            (lastRuns ?? Array.Empty<LoaderLastRun>()).ToList(),
            (stuck ?? Array.Empty<StuckRun>()).ToList(),
            (failures ?? Array.Empty<RecentFailure>()).ToList(),
            settings,
            NowCentral);

    // ---------------------------------------------------------------- overdue

    [Fact]
    public void Overdue_WhenTheGapExceedsMaxGapHours()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-30)) });

        var overdue = Assert.Single(report.Overdue);
        Assert.Equal("Platts", overdue.LoaderName);
        Assert.Equal(26, overdue.MaxGapHours);
    }

    [Fact]
    public void NotOverdue_WhenInsideTheWindow()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-2)) });

        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void NotOverdue_ExactlyAtTheBoundary()
    {
        // Strictly greater-than. A loader that runs every 24h with a 24h
        // threshold must not alert on every single run because of jitter.
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-26)) });

        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void Overdue_WhenAConfiguredLoaderHasNeverRunAtAll()
    {
        var report = Evaluate(Settings(("Platts", 26)));

        var overdue = Assert.Single(report.Overdue);
        Assert.Equal("Platts", overdue.LoaderName);
        Assert.Null(overdue.LastStartCentral);
    }

    [Fact]
    public void Ignored_WhenALoaderRanButIsNotConfigured()
    {
        // Build-only loaders get run by hand once and must not alert forever.
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[]
            {
                new LoaderLastRun("Platts", NowCentral.AddHours(-1)),
                new LoaderLastRun("NUCORE", NowCentral.AddDays(-200))
            });

        Assert.Empty(report.Overdue);
    }

    [Fact]
    public void LoaderNameMatchingIsCaseInsensitive()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("PLATTS", NowCentral.AddHours(-1)) });

        Assert.Empty(report.Overdue);
    }

    // ⚠ the regression guard for the time-base trap
    [Fact]
    public void ComparesAgainstCentral_NotUtc()
    {
        // StartTime in core.LoaderRun_Summary is already Central. Passing a UTC
        // "now" would add ~5h to every gap and report the whole platform
        // overdue. One hour ago, against a 6h threshold, must be quiet.
        var lastRun = new[] { new LoaderLastRun("Criterion", NowCentral.AddHours(-1)) };
        var settings = Settings(("Criterion", 6));

        var correct = WatchdogEvaluator.Evaluate(
            lastRun, Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, NowCentral);
        Assert.Empty(correct.Overdue);

        // Documents the failure mode: the same data against a UTC-shifted clock.
        var wrong = WatchdogEvaluator.Evaluate(
            lastRun, Array.Empty<StuckRun>(), Array.Empty<RecentFailure>(), settings, NowCentral.AddHours(5));
        Assert.Single(wrong.Overdue);
    }

    // ------------------------------------------------------------------ stuck

    [Fact]
    public void StuckRunsArePassedThrough()
    {
        var stuck = new[] { new StuckRun(Guid.NewGuid(), DateTime.UtcNow.AddHours(-20), "ARMH-OPSDB01") };

        var report = Evaluate(Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) }, stuck);

        Assert.Single(report.Stuck);
        Assert.True(report.ShouldNotify);
    }

    // --------------------------------------------------------------- quietness

    [Fact]
    public void ShouldNotify_False_WhenEverythingIsHealthy()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) });

        Assert.False(report.ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_False_OnEmptyInput_WithNoLoadersConfigured()
    {
        Assert.False(Evaluate(new WatchdogSettings()).ShouldNotify);
    }

    [Fact]
    public void ShouldNotify_True_OnRecentFailures()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-1)) },
            failures: new[] { new RecentFailure("Platts", NowCentral.AddHours(-2), 3, 14) });

        Assert.True(report.ShouldNotify);
    }

    [Fact]
    public void DisabledLoadersAreSkipped()
    {
        var settings = Settings(("Platts", 26));
        settings.Loaders["Platts"].Enabled = false;

        Assert.Empty(Evaluate(settings).Overdue);
    }

    // ---------------------------------------------------------------- message

    [Fact]
    public void Subject_SummarisesWhatIsWrong()
    {
        var report = Evaluate(
            Settings(("Platts", 26), ("CWG", 26)),
            stuck: new[] { new StuckRun(Guid.NewGuid(), DateTime.UtcNow.AddHours(-20), "ARMH-OPSDB01") });

        var subject = report.BuildMessage().Subject;

        Assert.Contains("WATCHDOG", subject);
        Assert.Contains("2 loaders overdue", subject);
        Assert.Contains("1 run stuck", subject);
    }

    [Fact]
    public void Body_NamesEveryOverdueLoaderAndHowLateItIs()
    {
        var report = Evaluate(
            Settings(("Platts", 26)),
            new[] { new LoaderLastRun("Platts", NowCentral.AddHours(-30)) });

        var body = report.BuildMessage().Body;

        Assert.Contains("Platts", body);
        Assert.Contains("30", body);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~WatchdogEvaluatorTests"
```

Expected: compilation failure — namespace `DataLoader.Core.Watchdog` does not exist.

- [ ] **Step 3: Create `WatchdogSettings.cs`**

```csharp
namespace DataLoader.Core.Watchdog;

/// <summary>
/// Bound from the <c>Platform:Watchdog</c> section.
///
/// <para>Thresholds are EXPLICIT per loader rather than learned from history.
/// A learned median quietly raises its own goalposts: a loader that has been
/// failing for a week would stop being considered late precisely when it most
/// needs reporting.</para>
/// </summary>
public sealed class WatchdogSettings
{
    public const string SectionName = "Platform:Watchdog";

    /// <summary>
    /// How long a <c>core.LoaderRun</c> row may sit with <c>CompletedAtUtc</c>
    /// NULL before it is treated as a killed process rather than a long run.
    /// </summary>
    public int StuckAfterHours { get; set; } = 6;

    /// <summary>
    /// Look-back for reporting runs that recorded <c>Success = 0</c>. This
    /// overlaps with the in-process email on purpose — it is the safety net for
    /// when that email could not be delivered.
    /// </summary>
    public int ReportRecentFailureHours { get; set; } = 24;

    /// <summary>
    /// Per-loader expectations. A loader absent from here is never reported,
    /// which is what keeps build-only loaders that were run by hand once from
    /// alerting forever.
    /// </summary>
    public Dictionary<string, WatchdogLoaderSettings> Loaders { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One loader's expected cadence.</summary>
public sealed class WatchdogLoaderSettings
{
    /// <summary>
    /// Report the loader when this many hours have passed since its last run
    /// STARTED. Seeded from 30-day observed cadence; see the design spec §2.5.
    /// </summary>
    public int MaxGapHours { get; set; }

    /// <summary>Set false to mute a loader without deleting its threshold.</summary>
    public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 4: Create `WatchdogModels.cs`**

```csharp
namespace DataLoader.Core.Watchdog;

/// <summary>
/// A loader's most recent run start, as recorded in
/// <c>core.LoaderRun_Summary</c>.
///
/// <para>⚠ <paramref name="LastStartCentral"/> is US CENTRAL, not UTC —
/// production's <c>usp_CompleteLoaderRun</c> converts before storing. The name
/// says so because mixing it with a UTC clock reports the entire platform
/// overdue.</para>
/// </summary>
public sealed record LoaderLastRun(string LoaderName, DateTime LastStartCentral);

/// <summary>
/// A <c>core.LoaderRun</c> row that never closed. Its timestamp IS UTC — a
/// different table with a different convention from <see cref="LoaderLastRun"/>.
/// </summary>
public sealed record StuckRun(Guid RunId, DateTime StartedAtUtc, string? HostName);

/// <summary>A run that recorded <c>Success = 0</c>. Timestamp is Central.</summary>
public sealed record RecentFailure(string LoaderName, DateTime StartTimeCentral, int UnitsFailed, int UnitsTotal);

/// <summary>A loader that has not run recently enough.</summary>
public sealed record OverdueLoader(
    string LoaderName,
    DateTime? LastStartCentral,
    double HoursSinceLastRun,
    int MaxGapHours);
```

- [ ] **Step 5: Create `WatchdogReport.cs`**

```csharp
using System.Text;
using DataLoader.Core.Notifications;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// What one watchdog pass found. Implements <see cref="INotificationPayload"/>
/// so it travels the same notifier path as an in-process run failure.
/// </summary>
public sealed class WatchdogReport : INotificationPayload
{
    public IReadOnlyList<OverdueLoader> Overdue { get; init; } = Array.Empty<OverdueLoader>();
    public IReadOnlyList<StuckRun> Stuck { get; init; } = Array.Empty<StuckRun>();
    public IReadOnlyList<RecentFailure> RecentFailures { get; init; } = Array.Empty<RecentFailure>();

    public bool ShouldNotify => Overdue.Count > 0 || Stuck.Count > 0 || RecentFailures.Count > 0;

    public NotificationMessage BuildMessage() => new(BuildSubject(), BuildBody());

    private string BuildSubject()
    {
        var parts = new List<string>();
        if (Overdue.Count > 0) parts.Add($"{Overdue.Count} loader{(Overdue.Count == 1 ? "" : "s")} overdue");
        if (Stuck.Count > 0) parts.Add($"{Stuck.Count} run{(Stuck.Count == 1 ? "" : "s")} stuck");
        if (RecentFailures.Count > 0) parts.Add($"{RecentFailures.Count} recent failure{(RecentFailures.Count == 1 ? "" : "s")}");
        return $"WATCHDOG: {string.Join(", ", parts)}";
    }

    private string BuildBody()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Watchdog pass on {Environment.MachineName} at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        if (Overdue.Count > 0)
        {
            sb.AppendLine("OVERDUE — expected to have run by now");
            sb.AppendLine(new string('-', 60));
            foreach (var o in Overdue.OrderByDescending(o => o.HoursSinceLastRun))
            {
                sb.AppendLine(o.LastStartCentral is null
                    ? $"  {o.LoaderName}: has NEVER run (threshold {o.MaxGapHours}h)"
                    : $"  {o.LoaderName}: last started {o.LastStartCentral:yyyy-MM-dd HH:mm} Central, " +
                      $"{o.HoursSinceLastRun:F1}h ago (threshold {o.MaxGapHours}h)");
            }
            sb.AppendLine();
        }

        if (Stuck.Count > 0)
        {
            sb.AppendLine("STUCK — a run row that never closed, i.e. the process was killed");
            sb.AppendLine(new string('-', 60));
            foreach (var s in Stuck.OrderBy(s => s.StartedAtUtc))
                sb.AppendLine($"  {s.RunId} started {s.StartedAtUtc:yyyy-MM-dd HH:mm} UTC on {s.HostName ?? "unknown host"}");
            sb.AppendLine();
        }

        if (RecentFailures.Count > 0)
        {
            sb.AppendLine("RECENT FAILURES");
            sb.AppendLine(new string('-', 60));
            foreach (var f in RecentFailures.OrderByDescending(f => f.StartTimeCentral))
                sb.AppendLine($"  {f.LoaderName}: {f.StartTimeCentral:yyyy-MM-dd HH:mm} Central, " +
                              $"{f.UnitsFailed}/{f.UnitsTotal} units failed");
            sb.AppendLine();
        }

        sb.AppendLine(@"Full output: C:\ETL_Tools\DataLoaderPlatform\Log\<Loader>.log");
        return sb.ToString();
    }
}
```

- [ ] **Step 6: Create `WatchdogEvaluator.cs`**

```csharp
namespace DataLoader.Core.Watchdog;

/// <summary>
/// Turns run history into a report. A pure function with no database, no clock
/// and no I/O, so every rule here is unit-testable.
/// </summary>
public static class WatchdogEvaluator
{
    /// <summary>
    /// ⚠ <paramref name="nowCentral"/> must be US CENTRAL, matching
    /// <c>core.LoaderRun_Summary.StartTime</c>, which production stores already
    /// converted. Passing a UTC clock adds ~5 hours to every gap and reports
    /// the whole platform overdue — a test pins this.
    ///
    /// <para><paramref name="stuckRuns"/> is already filtered by the query that
    /// produced it, because <c>core.LoaderRun.StartedAtUtc</c> is UTC and the
    /// comparison belongs next to the column, not here where the clock is
    /// Central.</para>
    /// </summary>
    public static WatchdogReport Evaluate(
        IReadOnlyList<LoaderLastRun> lastRuns,
        IReadOnlyList<StuckRun> stuckRuns,
        IReadOnlyList<RecentFailure> recentFailures,
        WatchdogSettings settings,
        DateTime nowCentral)
    {
        var seen = lastRuns
            .GroupBy(r => r.LoaderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(r => r.LastStartCentral), StringComparer.OrdinalIgnoreCase);

        var overdue = new List<OverdueLoader>();

        // Driven by CONFIG, not by the table. A loader nobody configured is
        // never reported — build-only loaders run by hand once would otherwise
        // alert forever. The flip side: a configured loader missing from the
        // table has never run, which is itself overdue.
        foreach (var (loaderName, loaderSettings) in settings.Loaders)
        {
            if (!loaderSettings.Enabled) continue;
            if (loaderSettings.MaxGapHours <= 0) continue;

            if (!seen.TryGetValue(loaderName, out var lastStart))
            {
                overdue.Add(new OverdueLoader(loaderName, null, double.PositiveInfinity, loaderSettings.MaxGapHours));
                continue;
            }

            var hours = (nowCentral - lastStart).TotalHours;

            // Strictly greater-than: a loader on a 24h cadence with a 24h
            // threshold must not alert on ordinary scheduling jitter.
            if (hours > loaderSettings.MaxGapHours)
                overdue.Add(new OverdueLoader(loaderName, lastStart, hours, loaderSettings.MaxGapHours));
        }

        return new WatchdogReport
        {
            Overdue = overdue,
            Stuck = stuckRuns,
            RecentFailures = recentFailures
        };
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~WatchdogEvaluatorTests"
```

Expected: `Passed! - Failed: 0, Passed: 15`.

- [ ] **Step 8: Commit**

```bash
git add src/DataLoader.Core/Watchdog/ tests/DataLoader.Core.Tests/WatchdogEvaluatorTests.cs
git commit -m "Add the watchdog evaluator as a pure function

Thresholds are explicit per loader, not learned: a learned median would raise
its own goalposts for a loader that has been failing all week. Config drives
the loop, so an unconfigured loader is never reported and a configured one
that has never run is."
```

---

### Task 8: Watchdog data access

**Files:**
- Create: `src/DataLoader.Core/Watchdog/IWatchdogRepository.cs`
- Create: `src/DataLoader.Core/Watchdog/SqlWatchdogRepository.cs`

No unit tests: this class is three `SELECT`s and a `DataReader` loop, and testing it without a server would only assert ADO.NET's behaviour. Its output types are fully covered by Task 7, and Task 11 verifies it against production read-only.

⚠ These are **inline `SELECT`s, not stored procedures** — a deliberate departure from this repo's proc-based idiom, because the design commits to adding no SQL objects to production. They are read-only and parameterised.

- [ ] **Step 1: Create `IWatchdogRepository.cs`**

```csharp
namespace DataLoader.Core.Watchdog;

/// <summary>
/// Reads the run history the watchdog evaluates. Read-only by construction —
/// the watchdog adds no SQL objects and writes nothing.
/// </summary>
public interface IWatchdogRepository
{
    /// <summary>Most recent start per loader, in CENTRAL time (see <see cref="LoaderLastRun"/>).</summary>
    Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken cancellationToken);

    /// <summary>Run rows still open after <paramref name="stuckAfterHours"/>, compared in UTC.</summary>
    Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int stuckAfterHours, CancellationToken cancellationToken);

    /// <summary>Runs that recorded Success = 0 within the look-back, in CENTRAL time.</summary>
    Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(
        int lookbackHours, DateTime nowCentral, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Create `SqlWatchdogRepository.cs`**

```csharp
using DataLoader.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// Reads <c>core.LoaderRun_Summary</c> and <c>core.LoaderRun</c>.
///
/// <para>⚠ Inline SELECTs rather than stored procedures, unlike the rest of
/// this platform. The design commits to adding NO SQL objects to production,
/// because <c>core.LoaderRun_Summary</c> already carries exactly the per-loader
/// outcomes the watchdog needs. Everything here is read-only and parameterised.</para>
///
/// <para>⚠ The two tables disagree about time zones.
/// <c>LoaderRun_Summary.StartTime</c> is already CENTRAL (production's
/// <c>usp_CompleteLoaderRun</c> converts before storing);
/// <c>LoaderRun.StartedAtUtc</c> is UTC. Each query below compares against the
/// matching clock, which is why the stuck-run window is applied in SQL with
/// <c>SYSUTCDATETIME()</c> while the failure look-back takes a Central
/// parameter from the caller.</para>
/// </summary>
public sealed class SqlWatchdogRepository : IWatchdogRepository
{
    private readonly PlatformSettings _settings;

    public SqlWatchdogRepository(IOptions<PlatformSettings> settings) => _settings = settings.Value;

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_settings.LoadLogConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    private SqlCommand Command(SqlConnection conn, string sql)
    {
        var cmd = new SqlCommand(sql, conn);
        if (_settings.LoadLogCommandTimeoutSeconds > 0)
            cmd.CommandTimeout = _settings.LoadLogCommandTimeoutSeconds;
        return cmd;
    }

    public async Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken cancellationToken)
    {
        // The blank-LoaderName row is real: a run that produced no core.LoadLog
        // rows leaves the summary's LoaderName NULL, because that column comes
        // from a LEFT JOIN onto LoadLog.
        const string Sql = @"
SELECT LoaderName, MAX(StartTime) AS LastStart
FROM core.LoaderRun_Summary
WHERE LoaderName IS NOT NULL AND LTRIM(RTRIM(LoaderName)) <> ''
  AND StartTime IS NOT NULL
GROUP BY LoaderName;";

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<LoaderLastRun>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new LoaderLastRun(reader.GetString(0), reader.GetDateTime(1)));
        return rows;
    }

    public async Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(
        int stuckAfterHours, CancellationToken cancellationToken)
    {
        const string Sql = @"
SELECT RunId, StartedAtUtc, HostName
FROM core.LoaderRun
WHERE CompletedAtUtc IS NULL
  AND StartedAtUtc < DATEADD(hour, -@StuckAfterHours, SYSUTCDATETIME())
ORDER BY StartedAtUtc;";

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.AddWithValue("@StuckAfterHours", Math.Max(1, stuckAfterHours));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<StuckRun>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new StuckRun(
                reader.GetGuid(0),
                reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(
        int lookbackHours, DateTime nowCentral, CancellationToken cancellationToken)
    {
        const string Sql = @"
SELECT LoaderName, StartTime, ISNULL(UnitsFail, 0), ISNULL(UnitsTotal, 0)
FROM core.LoaderRun_Summary
WHERE Success = 0
  AND LoaderName IS NOT NULL AND LTRIM(RTRIM(LoaderName)) <> ''
  AND StartTime >= DATEADD(hour, -@LookbackHours, @NowCentral)
ORDER BY StartTime DESC;";

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = Command(conn, Sql);
        cmd.Parameters.AddWithValue("@LookbackHours", Math.Max(1, lookbackHours));
        cmd.Parameters.AddWithValue("@NowCentral", nowCentral);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<RecentFailure>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RecentFailure(
                reader.GetString(0), reader.GetDateTime(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        return rows;
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build src/DataLoader.Core/DataLoader.Core.csproj -c Release
```

Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add src/DataLoader.Core/Watchdog/IWatchdogRepository.cs src/DataLoader.Core/Watchdog/SqlWatchdogRepository.cs
git commit -m "Add the watchdog's read-only data access

Inline SELECTs rather than procs, because the design adds no SQL objects:
core.LoaderRun_Summary already carries per-loader outcomes. Each query uses
the clock its own table stores - Central for the summary, UTC for LoaderRun."
```

---

### Task 9: `WatchdogRunner`

**Files:**
- Create: `src/DataLoader.Core/Watchdog/WatchdogRunner.cs`
- Create: `tests/DataLoader.Core.Tests/WatchdogRunnerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/DataLoader.Core.Tests/WatchdogRunnerTests.cs`:

```csharp
using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DataLoader.Core.Tests;

public class WatchdogRunnerTests
{
    private sealed class FakeRepository : IWatchdogRepository
    {
        public List<LoaderLastRun> LastRuns = new();
        public List<StuckRun> Stuck = new();
        public List<RecentFailure> Failures = new();
        public Exception? ThrowThis;

        public Task<IReadOnlyList<LoaderLastRun>> GetLastRunsAsync(CancellationToken ct) =>
            ThrowThis is not null
                ? Task.FromException<IReadOnlyList<LoaderLastRun>>(ThrowThis)
                : Task.FromResult<IReadOnlyList<LoaderLastRun>>(LastRuns);

        public Task<IReadOnlyList<StuckRun>> GetStuckRunsAsync(int h, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StuckRun>>(Stuck);

        public Task<IReadOnlyList<RecentFailure>> GetRecentFailuresAsync(int h, DateTime n, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RecentFailure>>(Failures);
    }

    private sealed class FakeNotifier : IFailureNotifier
    {
        public readonly List<INotificationPayload> Payloads = new();
        public NotificationOutcome Outcome = NotificationOutcome.Sent;

        public Task<NotificationOutcome> NotifyAsync(INotificationPayload payload, CancellationToken ct)
        {
            Payloads.Add(payload);
            return Task.FromResult(Outcome);
        }
    }

    private static WatchdogRunner Build(
        FakeRepository repo, FakeNotifier notifier, params (string Loader, int Gap)[] loaders)
    {
        var settings = new WatchdogSettings();
        foreach (var (loader, gap) in loaders)
            settings.Loaders[loader] = new WatchdogLoaderSettings { MaxGapHours = gap };

        return new WatchdogRunner(
            repo, notifier, Options.Create(settings), NullLogger<WatchdogRunner>.Instance);
    }

    [Fact]
    public async Task ExitsZeroAndSendsNothing_WhenEverythingIsHealthy()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-1)) }
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        Assert.All(notifier.Payloads, p => Assert.False(p.ShouldNotify));
    }

    [Fact]
    public async Task ExitsZeroButReports_WhenALoaderIsOverdue()
    {
        // Finding an overdue loader is the watchdog succeeding, not failing.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
        var report = Assert.IsType<WatchdogReport>(Assert.Single(notifier.Payloads));
        Assert.Single(report.Overdue);
    }

    [Fact]
    public async Task ExitsOne_WhenTheDatabaseCannotBeRead()
    {
        var repo = new FakeRepository { ThrowThis = new InvalidOperationException("db down") };
        var notifier = new FakeNotifier();

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task StillTriesToEmail_WhenTheDatabaseCannotBeRead()
    {
        // An unreachable platform DB is itself alert-worthy, and the in-process
        // notifier cannot report it either — nothing ran.
        var repo = new FakeRepository { ThrowThis = new InvalidOperationException("db down") };
        var notifier = new FakeNotifier();

        await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        var payload = Assert.Single(notifier.Payloads);
        Assert.True(payload.ShouldNotify);
    }

    [Fact]
    public async Task ExitsOne_WhenItFoundSomethingButCouldNotDeliver()
    {
        // An undelivered watchdog alert is indistinguishable from all-clear,
        // so the exit code has to carry the bad news instead.
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };
        var notifier = new FakeNotifier { Outcome = NotificationOutcome.Failed };

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ExitsZero_WhenNotificationsAreMerelyDisabled()
    {
        var repo = new FakeRepository
        {
            LastRuns = { new LoaderLastRun("Platts", DateTime.Now.AddHours(-100)) }
        };
        var notifier = new FakeNotifier { Outcome = NotificationOutcome.Disabled };

        var exit = await Build(repo, notifier, ("Platts", 26)).RunAsync(default);

        Assert.Equal(0, exit);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~WatchdogRunnerTests"
```

Expected: compilation failure — `WatchdogRunner` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/DataLoader.Core/Watchdog/WatchdogRunner.cs`:

```csharp
using DataLoader.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataLoader.Core.Watchdog;

/// <summary>
/// One watchdog pass: read history, evaluate, report, exit.
///
/// <para>This runs as a SEPARATE process from the loaders on its own schedule,
/// which is the entire point — in-process code cannot report its own death, so
/// a killed loader or a scheduled task that never fired is invisible from
/// inside the run it should have been.</para>
///
/// <para>Known limit: this still runs on the same machine, so it cannot report
/// that machine being down. Catching that needs something off-box.</para>
/// </summary>
public sealed class WatchdogRunner
{
    private readonly IWatchdogRepository _repository;
    private readonly IFailureNotifier _notifier;
    private readonly WatchdogSettings _settings;
    private readonly ILogger<WatchdogRunner> _logger;

    public WatchdogRunner(
        IWatchdogRepository repository,
        IFailureNotifier notifier,
        IOptions<WatchdogSettings> settings,
        ILogger<WatchdogRunner> logger)
    {
        _repository = repository;
        _notifier = notifier;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Exit code 0 even when loaders are overdue — finding them is the job
    /// being done, not failing. 1 only when the watchdog itself could not
    /// complete: it could not read the database, or it found something and
    /// could not deliver it.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var nowCentral = CentralNow();

        WatchdogReport report;
        var readFailed = false;

        try
        {
            var lastRuns = await _repository.GetLastRunsAsync(cancellationToken).ConfigureAwait(false);
            var stuck = await _repository
                .GetStuckRunsAsync(_settings.StuckAfterHours, cancellationToken).ConfigureAwait(false);
            var failures = await _repository
                .GetRecentFailuresAsync(_settings.ReportRecentFailureHours, nowCentral, cancellationToken)
                .ConfigureAwait(false);

            report = WatchdogEvaluator.Evaluate(lastRuns, stuck, failures, _settings, nowCentral);

            _logger.LogInformation(
                "Watchdog: {Overdue} overdue, {Stuck} stuck, {Failures} recent failures",
                report.Overdue.Count, report.Stuck.Count, report.RecentFailures.Count);
        }
        catch (Exception ex)
        {
            // An unreachable platform DB is itself worth an alert, and no loader
            // run can report it — none of them got far enough to try.
            _logger.LogError(ex, "Watchdog could not read run history");
            readFailed = true;
            report = null!;

            var failureReport = new RunFailureReport
            {
                HostName = Environment.MachineName,
                RunId = Guid.Empty,
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Exception = ex,
                FailedStage = "Watchdog"
            };
            await _notifier.NotifyAsync(failureReport, CancellationToken.None).ConfigureAwait(false);
        }

        if (readFailed) return 1;

        var outcome = await _notifier.NotifyAsync(report, CancellationToken.None).ConfigureAwait(false);

        // Disabled and NothingToReport are both fine. Failed is not: an
        // undelivered watchdog alert looks exactly like all-clear.
        return outcome == NotificationOutcome.Failed ? 1 : 0;
    }

    /// <summary>
    /// ⚠ Central, not UTC. <c>core.LoaderRun_Summary.StartTime</c> is stored
    /// already converted, so comparing it to a UTC clock would report every
    /// loader ~5 hours overdue.
    /// </summary>
    private static DateTime CentralNow()
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch (TimeZoneNotFoundException)
        {
            // The IANA id, for completeness — this platform runs on Windows.
            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Core.Tests/DataLoader.Core.Tests.csproj -c Release --filter "FullyQualifiedName~WatchdogRunnerTests"
```

Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add src/DataLoader.Core/Watchdog/WatchdogRunner.cs tests/DataLoader.Core.Tests/WatchdogRunnerTests.cs
git commit -m "Add WatchdogRunner

Exit 0 when it finds overdue loaders (that is the job working) and 1 only
when the watchdog itself could not complete - unreadable history, or a
finding it could not deliver, since an undelivered alert looks like all-clear."
```

---

### Task 10: Host wiring — `--watchdog`, the widened try, and DI

**Files:**
- Modify: `src/DataLoader.Host/Program.cs`
- Create: `tests/DataLoader.Host.Tests/DataLoader.Host.Tests.csproj`
- Create: `tests/DataLoader.Host.Tests/HostArgumentsTests.cs`
- Create: `src/DataLoader.Host/HostArguments.cs`
- Modify: `DataLoaderPlatform.sln`

Argument parsing is extracted into its own testable type rather than being tested through `Main`.

- [ ] **Step 1: Create the test project**

Create `tests/DataLoader.Host.Tests/DataLoader.Host.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DataLoader.Host\DataLoader.Host.csproj" />
  </ItemGroup>
</Project>
```

Add it to the solution:

```bash
dotnet sln DataLoaderPlatform.sln add tests/DataLoader.Host.Tests/DataLoader.Host.Tests.csproj
```

`DataLoader.Host` is an executable, so its internal types are not visible to the test project by default. `HostArguments` is therefore declared **public** in Step 3.

- [ ] **Step 2: Write the failing tests**

Create `tests/DataLoader.Host.Tests/HostArgumentsTests.cs`:

```csharp
using DataLoader.Host;
using Xunit;

namespace DataLoader.Host.Tests;

public class HostArgumentsTests
{
    private static readonly string[] Known = { "Platts", "Vulcan", "EnergyAspects" };

    [Fact]
    public void NoArguments_MeansUseConfig()
    {
        var parsed = HostArguments.Parse(Array.Empty<string>(), Known);

        Assert.True(parsed.IsValid);
        Assert.False(parsed.WatchdogMode);
        Assert.Empty(parsed.Loaders);
    }

    [Theory]
    [InlineData("--watchdog")]
    [InlineData("/watchdog")]
    [InlineData("--WATCHDOG")]
    public void WatchdogFlag_SelectsWatchdogMode(string flag)
    {
        var parsed = HostArguments.Parse(new[] { flag }, Known);

        Assert.True(parsed.IsValid);
        Assert.True(parsed.WatchdogMode);
        Assert.Empty(parsed.Loaders);
    }

    [Fact]
    public void WatchdogFlag_IsNotTreatedAsAnUnknownLoader()
    {
        // Before this existed, every unrecognised argument was fatal, so the
        // flag would have exited 1 with "Unknown loader '--watchdog'".
        var parsed = HostArguments.Parse(new[] { "--watchdog" }, Known);

        Assert.True(parsed.IsValid);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void LoaderNames_ResolveToCanonicalCasing()
    {
        var parsed = HostArguments.Parse(new[] { "platts", "VULCAN" }, Known);

        Assert.True(parsed.IsValid);
        Assert.Equal(new[] { "Platts", "Vulcan" }, parsed.Loaders);
    }

    [Fact]
    public void ATypoIsStillFatal()
    {
        var parsed = HostArguments.Parse(new[] { "Pltats" }, Known);

        Assert.False(parsed.IsValid);
        Assert.Contains("Pltats", parsed.Error);
        Assert.Contains("Platts", parsed.Error);   // lists what IS known
    }

    [Fact]
    public void WatchdogCombinedWithALoaderName_IsRejected()
    {
        // They mean different things; silently honouring one would be worse.
        var parsed = HostArguments.Parse(new[] { "--watchdog", "Platts" }, Known);

        Assert.False(parsed.IsValid);
        Assert.Contains("watchdog", parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeveralLoadersAreAllResolved()
    {
        var parsed = HostArguments.Parse(new[] { "Platts", "Vulcan", "EnergyAspects" }, Known);

        Assert.True(parsed.IsValid);
        Assert.Equal(3, parsed.Loaders.Count);
    }
}
```

- [ ] **Step 3: Create `HostArguments.cs`**

Create `src/DataLoader.Host/HostArguments.cs`:

```csharp
namespace DataLoader.Host;

/// <summary>
/// The command line, parsed.
///
/// <para>Pulled out of <c>Program</c> so it can be tested directly. The reason
/// it needs testing: every unrecognised argument used to be fatal, so adding a
/// flag means the flag has to be consumed BEFORE loader-name resolution or it
/// exits 1 with "Unknown loader '--watchdog'".</para>
/// </summary>
public sealed class HostArguments
{
    public bool WatchdogMode { get; private init; }
    public IReadOnlyList<string> Loaders { get; private init; } = Array.Empty<string>();
    public string? Error { get; private init; }
    public bool IsValid => Error is null;

    private static bool IsWatchdogFlag(string arg) =>
        arg.Equals("--watchdog", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/watchdog", StringComparison.OrdinalIgnoreCase);

    public static HostArguments Parse(string[] args, IReadOnlyList<string> knownLoaders)
    {
        if (args.Length == 0) return new HostArguments();

        if (args.Any(IsWatchdogFlag))
        {
            if (args.Length > 1)
            {
                return new HostArguments
                {
                    Error = "--watchdog runs the absence watchdog and takes no other arguments; " +
                            $"got: {string.Join(" ", args)}"
                };
            }
            return new HostArguments { WatchdogMode = true };
        }

        var known = knownLoaders.ToDictionary(n => n, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();
        var unknown = new List<string>();

        foreach (var arg in args)
        {
            if (known.TryGetValue(arg, out var canonical)) resolved.Add(canonical);
            else unknown.Add(arg);
        }

        if (unknown.Count > 0)
        {
            return new HostArguments
            {
                Error = $"Unknown loader(s): {string.Join(", ", unknown)}. " +
                        $"Known: {string.Join(", ", knownLoaders)}"
            };
        }

        return new HostArguments { Loaders = resolved };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/DataLoader.Host.Tests/DataLoader.Host.Tests.csproj -c Release
```

Expected: `Passed! - Failed: 0, Passed: 11`.

- [ ] **Step 5: Rewrite `Program.Main`**

Replace the whole `Main` method in `src/DataLoader.Host/Program.cs` with:

```csharp
    private static async Task<int> Main(string[] args)
    {
        var configuration = BuildConfiguration();

        // Bootstrap logger — used only to log discovery itself.
        using var bootstrapLoggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
            b.AddConfiguration(configuration.GetSection("Logging"));
        });
        var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Bootstrap");

        // ⚠ Everything below is inside the try, deliberately. BuildServiceProvider
        // used to sit OUTSIDE it, so a DI or configuration failure threw straight
        // out of Main with no catch and no notification at all — the one failure
        // class that breaks DI was also the one nobody heard about.
        //
        // The bootstrap notifier is built straight from IConfiguration rather than
        // resolved, for the same reason: it has to work when the container does not.
        // It needs no secret, because the configured relay accepts none.
        try
        {
            // Step 1 — find every loader module shipped with this host.
            var modules = ModuleDiscovery.Discover(bootstrapLogger);
            if (modules.Count == 0)
            {
                bootstrapLogger.LogError("No loader modules discovered — nothing to do");
                return 1;
            }
            bootstrapLogger.LogInformation(
                "Discovered {Count} loader modules: {Names}",
                modules.Count, string.Join(", ", modules.Select(m => m.LoaderId)));

            // Step 2 — parse the command line. --watchdog must be consumed before
            // loader-name resolution, because an unrecognised argument is fatal.
            var parsed = HostArguments.Parse(args, modules.Select(m => m.LoaderId).ToList());
            if (!parsed.IsValid)
            {
                bootstrapLogger.LogError("{Error}", parsed.Error);
                return 1;
            }

            // Step 3 — build the DI container with every discovered module's
            // registrations. Even loaders that won't run this invocation get
            // their services registered, which is harmless — they just don't
            // get resolved.
            await using var services = BuildServiceProvider(configuration, modules, parsed.Loaders);

            using var cts = new CancellationTokenSource();

            if (parsed.WatchdogMode)
            {
                var watchdog = services.GetRequiredService<WatchdogRunner>();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                return await watchdog.RunAsync(cts.Token);
            }

            var host = services.GetRequiredService<PlatformHost>();
            var logger = services.GetRequiredService<ILogger<PlatformHost>>();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                logger.LogWarning("Cancellation requested");
                cts.Cancel();
            };

            try
            {
                return await host.RunAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Run cancelled");
                return 2;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Unhandled exception");
                await NotifyBootstrapFailureAsync(configuration, bootstrapLogger, ex, "run");
                return 1;
            }
        }
        catch (OperationCanceledException)
        {
            bootstrapLogger.LogWarning("Run cancelled during startup");
            return 2;
        }
        catch (Exception ex)
        {
            bootstrapLogger.LogCritical(ex, "Unhandled exception during startup");
            await NotifyBootstrapFailureAsync(configuration, bootstrapLogger, ex, "startup");
            return 1;
        }
    }

    /// <summary>
    /// Reports a failure that happened outside — or while building — the DI
    /// container, so nothing can be resolved from it.
    ///
    /// <para>Builds its own notifier from <see cref="IConfiguration"/>. That is
    /// only possible because the configured relay takes no credential: a
    /// <c>SEE_DB</c> secret would need the database AND the container, neither
    /// of which is available on this path.</para>
    ///
    /// <para>Best-effort and silent on failure — this is the last thing that
    /// runs before a non-zero exit, and it must not replace the real error with
    /// one of its own.</para>
    /// </summary>
    private static async Task NotifyBootstrapFailureAsync(
        IConfiguration configuration, ILogger logger, Exception exception, string stage)
    {
        try
        {
            var settings = NotificationSettings.FromConfiguration(configuration);
            if (!settings.Enabled) return;

            INotificationSender sender = new SmtpNotificationSender(settings);
            var notifier = new FailureNotifier(
                sender, settings, NullLogger<FailureNotifier>.Instance);

            var report = new RunFailureReport
            {
                HostName = Environment.MachineName,
                RunId = Guid.Empty,
                StartedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = DateTime.UtcNow,
                Exception = exception,
                FailedStage = stage
            };

            await notifier.NotifyAsync(report, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not send the startup-failure notification");
        }
    }
```

Delete the now-unused `ParseLoaderArguments` method entirely — `HostArguments.Parse` replaces it.

Add these usings at the top of `Program.cs`:

```csharp
using DataLoader.Core.Notifications;
using DataLoader.Core.Watchdog;
using Microsoft.Extensions.Logging.Abstractions;
```

- [ ] **Step 6: Register the new services**

In `BuildServiceProvider`, add before the `foreach (var module in modules)` loop:

```csharp
        // Notifications. The settings object is registered as a singleton
        // instance rather than through IOptions, because the bootstrap path in
        // Main builds its own copy the same way and the two must not diverge.
        var notificationSettings = NotificationSettings.FromConfiguration(configuration);
        notificationSettings.Validate();
        services.AddSingleton(notificationSettings);

        if (notificationSettings.Enabled)
            services.AddSingleton<INotificationSender, SmtpNotificationSender>();
        else
            services.AddSingleton<INotificationSender, NullNotificationSender>();

        services.AddSingleton<IFailureNotifier, FailureNotifier>();

        // Watchdog.
        services.Configure<WatchdogSettings>(configuration.GetSection(WatchdogSettings.SectionName));
        services.AddSingleton<IWatchdogRepository, SqlWatchdogRepository>();
        services.AddSingleton<WatchdogRunner>();
```

- [ ] **Step 7: Build and run every test**

```bash
dotnet build DataLoaderPlatform.sln -c Release
dotnet test DataLoaderPlatform.sln -c Release
```

Expected: build succeeds with 0 errors; all tests pass. The solution-wide run is required here because `DataLoader.Core` and `DataLoader.Host` both changed (repo test policy).

- [ ] **Step 8: Verify the flag end to end against a real binary**

```bash
dotnet run --project src/DataLoader.Host/DataLoader.Host.csproj -c Release -- --badflag
```

Expected: exits 1, logging `Unknown loader(s): --badflag`.

```bash
dotnet run --project src/DataLoader.Host/DataLoader.Host.csproj -c Release -- --watchdog
```

Expected: the watchdog runs, logs `Watchdog: N overdue, N stuck, N recent failures`, and exits 0. It reads `ARMH-OPSDB01` — **read-only**, and notifications are still `Enabled: false` at this point, so nothing is sent. If it exits 1 with a SQL error, the connection string or the `core.LoaderRun_Summary` queries need checking before going further.

- [ ] **Step 9: Commit**

```bash
git add src/DataLoader.Host/Program.cs src/DataLoader.Host/HostArguments.cs tests/DataLoader.Host.Tests/ DataLoaderPlatform.sln
git commit -m "Wire notifications and --watchdog into the host

BuildServiceProvider used to sit outside the try, so a DI or configuration
failure threw out of Main uncaught - the one failure class that breaks DI was
also the one nobody heard about. The bootstrap notifier builds itself from
IConfiguration, which only works because the relay takes no credential."
```

---

### Task 11: Configuration, launcher, documentation

**Files:**
- Modify: `src/DataLoader.Host/appsettings.json`
- Create: `Watchdog.cmd`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add the config sections**

In `src/DataLoader.Host/appsettings.json`, inside the `"Platform"` object, after `"AbandonStaleAfterHours": 24,` add a comma to that line if needed and insert:

```json
    "Notifications": {
      "Enabled": false,
      "SmtpHost": "armenergy-com.mail.protection.outlook.com",
      "SmtpPort": 25,
      "UseSsl": true,
      "From": "ARMHOPSDB01_SVC@armenergy.com",
      "To": [ "yaroslav.chekhuta@armenergy.com" ],
      "SubjectPrefix": "[DataLoader]",
      "TimeoutSeconds": 30
    },
    "Watchdog": {
      "StuckAfterHours": 6,
      "ReportRecentFailureHours": 24,
      "Loaders": {
        "Criterion":         { "MaxGapHours": 6 },
        "IHSPointLogic":     { "MaxGapHours": 6 },
        "EvolutionMarkets":  { "MaxGapHours": 8 },
        "ICE":               { "MaxGapHours": 12 },
        "EOX":               { "MaxGapHours": 16 },
        "Platts":            { "MaxGapHours": 26 },
        "AGSI":              { "MaxGapHours": 26 },
        "Argus":             { "MaxGapHours": 26 },
        "OPIS":              { "MaxGapHours": 26 },
        "CWG":               { "MaxGapHours": 26 },
        "EnergyAspects":     { "MaxGapHours": 30 },
        "NGX":               { "MaxGapHours": 30 },
        "NGI":               { "MaxGapHours": 30 },
        "Vulcan":            { "MaxGapHours": 30 },
        "OilX":              { "MaxGapHours": 30 },
        "IIR":               { "MaxGapHours": 30 },
        "StormVista":        { "MaxGapHours": 30 },
        "Marex":             { "MaxGapHours": 30 },
        "ModernCommodities": { "MaxGapHours": 30 },
        "Genscape":          { "MaxGapHours": 30 },
        "CME":               { "MaxGapHours": 30 }
      }
    }
```

⚠ `Enabled` is **false**. It stays false until IT confirms port-25 egress from ARMH-OPSDB01 and that the tenant's "Reject Direct Send" setting is off. `EIA` and `NUCORE` are deliberately absent — both ran irregularly during development and would alert constantly.

⚠ `appsettings.json` is strict JSON — do not paste the `//` comments from the spec into it.

- [ ] **Step 2: Verify the JSON still parses**

```bash
dotnet run --project src/DataLoader.Host/DataLoader.Host.csproj -c Release -- --watchdog
```

Expected: runs and exits 0. A malformed file throws at `AddJsonFile(..., optional: false)` with a line number.

- [ ] **Step 3: Create the dev launcher**

Create `Watchdog.cmd` in the repository root, matching the other 23 dev launchers:

```bat
@echo off
cd /d "%~dp0src\DataLoader.Host\bin\Release\net8.0"
DataLoader.Host.exe --watchdog
echo Exit code: %ERRORLEVEL%
pause
```

⚠ This is the **dev** form. The production launcher is different — it does `cd /d "%~dp0Loader"` and appends to `Log\Watchdog.log` with no `pause` — and lives only on the server. It is an IT prerequisite, not a repo file.

- [ ] **Step 4: Document it in CLAUDE.md**

Add this section to `CLAUDE.md`, immediately before the `## Key files` heading:

```markdown
## Failure notifications and the watchdog

Two mechanisms, because neither covers every failure class alone. **In-process**
(`DataLoader.Core/Notifications/`) emails at the end of a run when any loader
failed, when the process throws, or when the load-log sweeper closed rows an
EARLIER run left open. **The watchdog** (`DataLoader.Host.exe --watchdog`, its own
hourly `\DLP\Watchdog` task) reports loaders that have not run inside their
configured `MaxGapHours`, run rows still open after `StuckAfterHours`, and recent
`Success = 0` runs. In-process code cannot report its own death; the watchdog
cannot report the box being down. Both ship in `Platform:Notifications` /
`Platform:Watchdog`.

Four things are silent if you get them wrong:

- 🔒 **Email leaves the box; logs do not.** All vendor error text goes through
  `ErrorTextRedactor` first — AGSI echoes your `x-key` back in its error body and
  OilX puts `api_key` in the query string.
- ⚠ **`core.LoaderRun_Summary.StartTime` is CENTRAL, `core.LoaderRun.StartedAtUtc`
  is UTC.** Production's `usp_CompleteLoaderRun` converts before storing. Mixing
  them reports every loader ~5h overdue; `WatchdogEvaluator` takes `nowCentral`
  and a test pins it.
- ⚠ **The relay accepts NO credentials.** `armenergy-com.mail.protection.outlook.com`
  is Exchange Online's inbound MX connector — an `EHLO` probe advertises STARTTLS
  and no `AUTH` verb. `NotificationSettings` rejects `SmtpUsername`/`SmtpPassword`
  outright so nobody "fixes" it later with a real password. Recipients must be
  internal `@armenergy.com`; direct send relay-denies external addresses.
- ⚠ **The watchdog exits 0 when it finds overdue loaders** — that is the job
  working. It exits 1 only when it could not read history or could not deliver a
  finding, because an undelivered alert looks exactly like all-clear.

`core.LoaderRun_Summary` is a **table** in production (written by prod's
`usp_CompleteLoaderRun`), not a view, and prod's `core.LoaderRun` has a 6th column
`LoaderRunId` the repo DDL does not create. The watchdog depends on both, and
`sql/Core/` has drifted from them — a clean deploy to a fresh server would not
produce a database it can read.

See `docs/superpowers/specs/2026-10-24-failure-notifications-design.md`.
```

- [ ] **Step 5: Run the full suite one more time**

```bash
dotnet build DataLoaderPlatform.sln -c Release
dotnet test DataLoaderPlatform.sln -c Release
```

Expected: 0 errors, every test passing. Record the actual totals — do not claim a number that was not printed.

- [ ] **Step 6: Commit**

```bash
git add src/DataLoader.Host/appsettings.json Watchdog.cmd CLAUDE.md
git commit -m "Add notification and watchdog config, dev launcher, and CLAUDE.md notes

Ships Enabled:false. Watchdog thresholds are seeded from 30-day observed
cadence; EIA and NUCORE are omitted because both ran irregularly during
development and would alert constantly."
```

---

### Task 12: Verify against production, then hand over the deploy

🔒 **`ARMH-OPSDB01` is production. Everything in this task is a READ. Do not deploy.**

- [ ] **Step 1: Confirm the watchdog reads real history correctly**

```bash
dotnet run --project src/DataLoader.Host/DataLoader.Host.csproj -c Release -- --watchdog
```

Expected: exit 0, and a log line of the form `Watchdog: N overdue, N stuck, N recent failures`. As of 2026-10-23 production had **5 stuck runs** (StormVista ×3, NUCORE, EIA) and **43 failures in 7 days**, so `stuck` should be non-zero and `recent failures` should be plausible. All three being zero means the queries are not matching rows — investigate before going further.

- [ ] **Step 2: Sanity-check the overdue list against reality**

Any loader reported overdue should actually be overdue. If the whole platform is listed, the Central/UTC mix-up has reappeared — check that `WatchdogRunner.CentralNow()` is what reaches `WatchdogEvaluator.Evaluate`.

- [ ] **Step 3: Prove an email actually sends, from the server**

Still not a deploy. This needs the IT answers first (port-25 egress, Reject Direct Send). Once they land, the check is a single send from ARMH-OPSDB01 with `Enabled: true` in a **local** `appsettings.Local.json`, which the host already loads and which is never deployed.

Until IT confirms, **stop here and report** rather than flipping `Enabled` in the committed config.

- [ ] **Step 4: Prepare the deployment commands and hand them over**

🔒 Do **not** run these. Write them into the final report for the user to run.

```powershell
# 1. Publish to the share (Src\). This does NOT change what runs.
.\scripts\Publish-ToProd.ps1 -OverwriteSettings

# 2. Copy Src -> Loader. THIS is what changes what runs.
#    (the exact copy step as the team normally performs it)
```

Also state in the report, for IT:

- `Watchdog.cmd` must be created in `C:\ETL_Tools\DataLoaderPlatform\` in the **production** idiom: `cd /d "%~dp0Loader"` then `DataLoader.Host.exe --watchdog >> "%~dp0Log\Watchdog.log" 2>&1`, with no `pause`.
- A new hourly `\DLP\Watchdog` scheduled task running `cmd.exe /C "%DataLoaderPlatform%\Watchdog.cmd"`.
- `-OverwriteSettings` is required: the config gate fails closed on any difference, and these new keys guarantee one.

- [ ] **Step 5: Final commit and summary**

```bash
git log --oneline feature/failure-notifications ^master
```

Report: files changed, test totals as actually printed, what is verified versus unverified (the SMTP send is unverified until IT answers), and the handed-over commands.

---

## Self-review

**Spec coverage.** §3 component layout → Tasks 1-5, 7-9. §4.1 `PlatformHost` call site → Task 6. §4.2 `Program.cs` restructure → Task 10 Step 5. §4.3 never-closed detection → Task 6 Steps 4-5. §5 watchdog → Tasks 7-9, flag in Task 10. §6 email content → Task 4. §6.1 redaction → Task 2, re-asserted at the boundary in Task 4. §7 config → Task 11. §7.1 no-credential transport → Task 3 (rejects keys), Task 5 (`Credentials = null`). §8 testing → every task's test steps; solution-wide run in Task 10 Step 7 and Task 11 Step 5. §10 prerequisites → Task 12. §11 build order → Tasks 1-11 in order.

**Type consistency.** `INotificationPayload.BuildMessage()` takes no argument and is used identically by `RunFailureReport` and `WatchdogReport`; `FailureNotifier.Finalise` is the single place the subject prefix and the backstop redaction are applied (amended after the Task 1 code-quality review — payloads were originally each given the prefix, which let any one of them drop it). `IFailureNotifier.NotifyAsync(INotificationPayload, CancellationToken)` returns `Task<NotificationOutcome>` at every call site — `PlatformHost` discards it, `WatchdogRunner` branches on it. `WatchdogEvaluator.Evaluate` takes the same five parameters in the tests (Task 7) and the runner (Task 9). `ErrorTextRedactor.Clean` is the method `RunFailureReport` calls, and it is defined in Task 2.

**Known deviations from repo convention, each justified inline:** inline `SELECT`s instead of stored procedures (Task 8 — the design adds no SQL objects); a new test project for the host (Task 10 — `HostArguments` must be public because `DataLoader.Host` is an exe).
