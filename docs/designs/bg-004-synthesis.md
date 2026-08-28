# BG-004 Synthesis — One ContentControl Shell over a Smoke-Tested Design System

**Base: Candidate A** (single `ContentControl` content region + ViewModel-first `NavigationService` with a symbolic focus contract), **grafted from Candidate C** (the design system is built, compiled, and smoke-tested *before any screen exists*; screens are near-pure markup with a zero-literal rule) and **from Candidate B** (screen view models **retained** across navigation so POS cart and reconciliation drafts survive screen switches; `DialogViewModelBase<TResult>` typed dialog base). TabControl hybrid (B) and fresh-VM-per-navigation (A/C) were considered and rejected as documented in §15. All three candidates independently converged on: owner-modal window dialogs with per-window Esc scoping, typed result objects, a single `ShortcutCatalog` driving both the F1 help overlay and the conflict tests, the `BentaGo.Tests.AppShell` namespace for the mandated `--filter AppShell` command, and a scenario-derived read-model seam.

---

## 1. Final concept

- One `MainWindow`: left rail, `ContentControl` content region bound to the active screen view model, `StatusBar` — the BG-001 shell extended, not replaced.
- `NavigationService` (with `INavigationService`) switches screens by string key through implicit `DataTemplate`s; navigation raises **symbolic `FocusRequest`s** only — the shell's one code-behind mechanism resolves them to elements.
- Screen view models are **retained** (POS cart, reconciliation draft survive screen switches); eviction/disposal policy exists and is unit-tested for BG-005's admin screens.
- `Theme.xaml` (tokens) + `ScreenStyles.xaml` (control styles) + `Validation.xaml` (error templates) are built and smoke-tested **before any screen exists**; the six screens are thin `UserControl`s with a zero-literal rule.
- Dialogs are owner-modal windows behind `IDialogService.ShowAsync<TResult>` returning typed results; Esc closes only the top dialog (structural) and focus returns by symbolic request.
- Validation is `INotifyDataErrorInfo`-based (`ValidatableViewModelBase`) with one error template and one field-error text style.
- Every shortcut lives in `ShortcutCatalog.cs` — the single source rendered by the F1 help overlay and asserted by the conflict tests.
- Read-only data flows from the in-process `TwoPeddlersScenario` behind `IAppDataSource`; BG-005 swaps one DI registration.
- Live lanes run headless (in-process STA driver + `tools/BentaGo.LaneDriver`) producing PNG evidence + verdicts; the review pack (six screenshots + 30–60 s keyboard video) is the operator artifact; ffmpeg/AHK are not assumed or installed.

## 2. The BG-004 contract (authoritative recap)

**Files** (docs/05 239–240): WPF resources, shell views, shell view models, navigation, commands, dialog service, validation templates, keyboard help under `src/BentaGo.App/`; view-model and accessibility tests under `tests/BentaGo.Tests/App/`.

**Build** (244–245): implement the six reference screens with placeholder or stable-contract data; standardize shortcuts on docs/03 — **F12 = checkout, F10 = end trip, F1 = help, Alt = navigation, Esc = close dialog**. Conflicting image labels are not behavior.

**You see** (249): a keyboard-only user moves through login, peddler home, sales, payment, stock, and reconciliation at both target resolutions with visible focus and no business logic in code-behind.

**Verify unit** (253): `tests/BentaGo.Tests/App/` covers navigation, command enablement, focus-restoration requests, shortcut conflicts, validation state, dialog results, view-model disposal. `dotnet test -c Release --filter AppShell` — **verified this session**: the literal filter matches tests whose fully-qualified name contains `AppShell` (xunit adapter substring fallback, exit 0 on no-match), so the namespace `BentaGo.Tests.AppShell` makes the mandated command select exactly the BG-004 suite with zero traits.

**Verify live** (255–266): ten lanes (L1–L10) per docs/05, files `bg-004-*.png`.

**Verify perf** (270–273): baseline BG-001 shell navigation first; P95 navigation ≤ 150 ms; managed memory growth ≤ 20 MB after 500 changes.

**Review gate** (275–279): six screenshots at 1366×768 → `artifacts/review/bg-004-<screen>.png`; 30–60 s keyboard-only navigation+focus video → `artifacts/review/bg-004-navigation.mp4` (ffmpeg gdigrab); post for the operator; **stop at merge-ready and wait for the operator's click**.

**Merge** (281–285): clean accessibility/layout/navigation/code-boundary verdicts at the exact BG-004 head SHA; rebase onto `main`, prove patch unchanged; operator squash-merges after the review gate.

**Code-boundary rules** (docs/02 §2, docs/04, docs/00 §8): UI thin; no business rules in views; no domain logic in code-behind; the shell performs no calculations beyond display formatting; viewed code-behind may touch **input, focus, and window lifecycle only** — never data, commands, or business state.

## 3. The six screens (map to `designs/bentago-wpf-mvp-screen-set.png`)

The image is 1536×1024 and presents six reference screens; the brief names them and docs/03 §3 specifies them. Image labels are visual reference only.

| # | Screen | Docs/03 page | Screen key | Initial focus target | Purpose |
|---|---|---|---|---|---|
| 1 | Login | Page 1 | `login` | `Login.UsernameBox` | BG-001 view restyled into the system; Enter submits, F1 help, status + version visible; failure copy never reveals which credential is wrong |
| 2 | Peddler Home | Page 2 | `home` | `Home.NewSaleButton` | Header (greeting, peddler, trip, online/offline, current time), four info cards (Today's Sales, Transactions, Cash Sales, Remaining Items), primary NEW SALE, secondary actions, urgent previous-date-trip banner |
| 3 | Sales / POS | Page 3 | `sales` | `Sales.ScanBox` | Scan/search field, results, cart lines, subtotal/discount/TOTAL, CHECKOUT + CANCEL; checkout opens the payment **dialog** (never a page); scanner Enter keeps focus in the scan field |
| 4 | Payment (dialog) | Page 4 | — (dialog) | `Payment.MethodTabs` | Total on top; method choices Cash/GCash/Maya/Bank Transfer/Credit/Utang/Split; cash: amount received + change + keypad; split: payment lines + remaining balance with confirm disabled until zero; credit: customer/reference; Enter confirms, Esc returns to cart |
| 5 | Stock | Page 6 | `stock` | `Stock.StockSearchBox` | My Stock table: SKU, product, loaded, sold, damaged/lost, returned, on hand, last movement; low-stock filter |
| 6 | Reconciliation | Page 9 | `reconciliation` | `Reconciliation.ActualCashBox` | Trip close: per-method summary + expected cash, actual cash remitted, count table (expected/counted/variance/reason), BALANCED/SHORT/OVER banner, Save Draft / Submit Close / Print Summary; closed state visibly locked |

Screen 4 is a **modal dialog per docs/03 §8** ("Payment and confirmation overlays → WPF modal Window/dialog service, not a browser overlay") and is an in-scope BG-004 deliverable: it proves the dialog service contract. Its tender math (split zero-balance enforcement, change, credit completeness) is BG-005 business logic; BG-004 renders the dialog and gates its commands on state flags only.

`Transactions` (docs/03 Alt+4, Page 8) is **not** one of the six BG-004 screens. Its catalog entry is `Reserved` and unbound (greyed in the F1 overlay) until BG-005. `F8`/`F9`, `Ctrl+Alt+L/B/T`, `F4`/`F7` (Sales) are likewise `Reserved`/`Future` entries — catalogued for lane 4 completeness, never bound to fake behavior.

**Placeholder-data policy:** read-only screens render stable data from the in-process BG-002 scenario through the `IAppDataSource` seam (§12); nothing is persisted, checked out, or closed in BG-004.

## 4. Design-system tokens (Theme.xaml + ScreenStyles.xaml + Validation.xaml)

Built **first** (C-graft sequencing, §17) and smoke-tested before any screen exists. Merge order in `App.xaml`:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Themes/Theme.xaml" />
            <ResourceDictionary Source="Themes/Theme.HighContrast.xaml" /> <!-- empty by default; inserted on HC -->
            <ResourceDictionary Source="Styles/ScreenStyles.xaml" />
            <ResourceDictionary Source="Styles/Validation.xaml" />
            <ResourceDictionary Source="Views/DataTemplates.xaml" />        <!-- VM→View map, BG-001 pattern -->
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

**Layering rule:** `Theme.xaml` = tokens and system states only; `ScreenStyles.xaml` = shared control recipes referencing tokens; per-feature `*Styles.xaml` (if any) = screen-specific composition only and may never define a token. `Validation.xaml` = error templates + error text style. Brushes are consumed via `DynamicResource` (live high-contrast swap), scalars (sizes/spacing/corners) via `StaticResource`.

**Zero-literal rule (C):** the review gate greps `Features/` for `Foreground=`, `Background=`, `FontSize=`, literal `Height>0`, raw `Margin` and fails — screens are markup + bindings + style references.

**Key-naming rule:** brushes end `…Brush`, spacing keys are `Thickness.*`, font sizes are `FontSize.*`, keyed styles carry a `.Kind` suffix. Every key is a `public const string` in `DesignTokens.cs` with `All` list; the smoke test drives exclusively from `DesignTokens.All`, so renaming a key is a compile+test event.

### 4.1 Color tokens (`Theme.xaml`, key prefix `Brush.` / suffix `Brush`)

Semantic, platform-neutral; the exact hex values are **provisional** and must be matched to `designs/bentago-wpf-mvp-screen-set.png` by the implementer (image unreadable in this session). Status is **never color-only** (docs/03 §7): every status renders text + glyph + color.

| Key | Value (placeholder) | Use |
|---|---|---|
| `Brush.Surface.Page` (`BackgroundBrush`) | `#F4F5F7` | Screen background (BG-001 login bg) |
| `Brush.Surface.Card` (`SurfaceBrush`) | `#FFFFFF` | Cards, inputs, dialogs |
| `Brush.Surface.Sunken` (`SurfaceAltBrush`) | `#E8EAED` | Status bar, headers, disabled fills |
| `Brush.Surface.Selected` (`SelectionBackgroundBrush`) | accent tint `#E7F0EC` | Selected row/item, selected rail/tab |
| `Brush.Text.Primary` (`TextPrimaryBrush`) | `#202124` | Primary text |
| `Brush.Text.Secondary` (`TextSecondaryBrush`) | `#5E6470` | Secondary text (BG-001 status text) |
| `Brush.Text.Disabled` (`TextDisabledBrush`) | `#9AA0A6` | Disabled labels/content |
| `Brush.Text.OnAccent` / `Brush.Text.OnBrand` | `#FFFFFF` | Text on accent/brand fills |
| `Brush.Accent` / `Brush.Accent.Hover` / `Brush.Accent.Pressed` | `#0B6E4F` / `#0A5C41` / `#07462F` | Brand accent (placeholder; primary actions, focus) |
| `Brush.Brand` | deep market green, e.g. `#14532D` | Login card title, brand header |
| `Brush.Border.Default` / `Brush.Border.Strong` / `Brush.Divider` | `#D7DAE0` / `#B6BCC5` / `#E0E3E8` | Borders, focus base, gridlines |
| `Brush.Status.Success` / `.Warning` / `.Error` / `.Info` | `#1E7D32` / `#B26A00` / `#B3261E` / `#0B57D0` | Status accents — always paired with text |
| `Brush.Status.Success.Bg` / `.Warning.Bg` / `.Error.Bg` / `.Info.Bg` | pale tints | Banners, low-stock tint, variance badges |
| `Brush.Focus` | `#0B57D0` | Keyboard focus visual |
| `Brush.State.Disabled.Bg` / `.Fg` / `.Border` | `#F1F3F4` / `#9AA0A6` / `#E4E7EB` | Disabled control states (never opacity-only — opacity ghosts in high contrast) |
| `Brush.Pay.Cash` / `.Gcash` / `.Maya` / `.Bank` / `.Credit` | `#1E7D32` / `#0064E0` / `#F5533D` / `#6B4FBB` / `#B26A00` | Payment method accents (selection only; label text always readable) |

### 4.2 Type ramp (`Theme.xaml`)

Segoe UI (`Font.Family.Primary`), POS needs large totals (docs/03 §1).

| Key | Value | Use |
|---|---|---|
| `FontSize.Display` | 40 | POS cart TOTAL, payment total |
| `FontSize.Header` | 26 | Screen headers, greeting |
| `FontSize.Title` | 20 | Card titles, dialog titles |
| `FontSize.Body` | 15 (min readable, docs/03 §7) | Body, table cells |
| `FontSize.Label` | 13 | Field labels, card captions |
| `FontSize.Status` | 12 | Status bar, timestamps |
| `FontSize.Caption` | 11 | Table headers, help, keycap hints |
| `FontWeight.Strong` / `.Semi` | Bold / SemiBold | Money/totals/selected tab / headers |

Named TextBlock styles: `DisplayTextStyle`, `HeaderTextStyle`, `TitleTextStyle`, `BodyTextStyle`, `LabelTextStyle`, `StatusTextStyle`, `CaptionTextStyle`, `MoneyTextStyle`.

### 4.3 Spacing, metrics, corners (`Theme.xaml`)

| Key | Value |
|---|---|
| `Thickness.XS` / `S` / `M` / `L` / `XL` / `XXL` | 4 / 8 / 12 / 16 / 24 / 32 |
| `ScreenPadding` 24, `CardPadding` 16 | |
| `Corner.S` / `M` / `L` | 4 / 8 / 12 |
| `Control.Height.Default` / `.Large` | 38 (BG-001 inputs) / 48 (touch-friendly fallback, docs/01 NFR) |
| `DialogHost.MaxWidth` / `.MaxHeight` | 760 / 600 — payment dialog must fit 1092×614 DIP (1366×768 @125 %) |

### 4.4 Focus visuals and high contrast

- `FocusVisual.Default`: a `ControlTemplate` drawing a 2 px rounded rectangle in `Brush.Focus` (margin −3, `Corner.S`), applied as every interactive control's `FocusVisualStyle`; shown on `IsKeyboardFocusWithin` for composite controls (cards, rail items). Never relies on color alone; the ring is ≥ 2 px.
- **High contrast (lane 9):** `Theme.HighContrast.xaml` declares the **same key set** backed by `DynamicResource` to `SystemColors` (`WindowBrushKey`/`WindowTextBrushKey`/`HighlightBrushKey`/`HighlightTextBrushKey`/`GrayTextBrushKey`/`WindowFrameBrushKey`/`ControlBrushKey`/`ControlTextBrushKey`). `App.xaml.cs` listens to `SystemParameters.StaticPropertyChanged` (and `SystemEvents.UserPreferenceChanged`) and swaps the dictionary in; because styles reference brushes via `DynamicResource`, the degradation is total by construction. Key **parity** between `Theme.xaml` and `Theme.HighContrast.xaml` is asserted by test (superset needed, exact preferred). Color-only affordances carry a non-color channel in HC: selected tab = Bold + underline; DataGrid selection = highlight brush + Bold; banners always contain text; low-stock rows show a bold "LOW" tag. If `SystemParameters.HighContrast` does not fire live on the build host, the lane-9 procedure relaunches the app after the OS theme change.

### 4.5 Control styles (`ScreenStyles.xaml`)

Implicit styles: `Button`, `TextBox`, `PasswordBox`, `DataGrid`, `DataGridRow`/`Cell`/`ColumnHeader`, `StatusBar`, `StatusBarItem`. Keyed styles: `Button.PrimaryStyle` (accent fill, `TextOnAccent`, height 44, bold, `Corner.M`, explicit disabled set), `Button.SecondaryStyle` (outline), `Button.DangerStyle` (error tint, for CANCEL on destructive flows), `Button.QuietStyle` (text button), `Button.KeypadStyle` (72×56, `FontSize` 20 bold) + `KeypadButton.AccentStyle` (Enter), `CardStyle` (Border: `SurfaceBrush`, `BorderBrush` 1, `Corner.L`, `CardPadding`), `Banner.SuccessStyle` / `.WarningStyle` / `.ErrorStyle` (tint bg + matching border + `TextPrimaryBrush` text), `DataGrid.LowStockRowStyle` (DataTrigger on row `IsLow` → warning tint + bold SKU + "LOW" tag), `NavRailItemStyle`, `TabControlStyle`/`TabItemStyle` (only for the Payment method strip rendered as buttons — **no WPF TabControl in the shell**), `ScanEditorStyle` (POS scan field), `FieldLabelStyle`, `DialogWindowStyle` (NoResize, `CenterOwner`, `ShowInTaskbar=false`), `HelpOverlayStyle`, `StatusDotStyle`, `KeyCapStyle` (keyboard-hint labels), `PlaceholderStyle` (greyed reserved actions). **No `_`-mnemonics anywhere** (conflict rule §6.3) and no `Menu`/`ToolBar` in the shell.

### 4.6 Style smoke test (STA, `ResourceSmokeTests`)

With an `AppShellSta` collection fixture (one STA thread, one `Application`, `[CollectionDefinition(DisableParallelization = true)]`, no new packages): (1) every dictionary in the merge order parses and every `DesignTokens.All` key resolves; (2) probe `Button`/`TextBox`/`PasswordBox`/`DataGrid`/`StatusBar` with implicit+keyed styles — instantiate, apply, arrange, `ApplyTemplate`, assert values resolve (no `DynamicResourceExpression` leaks); (3) `Theme.HighContrast.xaml` key parity; (4) `Validation.xaml` templates load and `ErrorTextStyle` resolves. **LayoutProbeTests** (same fixture): each screen view `Measure`/`Arrange` at 1092×614 DIP (1366×768 @125 %), assert bounding rects inside the window and min touch target ≥ 44 DIP.

## 5. Navigation contract

### 5.1 API (`src/BentaGo.App/Navigation/`)

Open-set string keys (BG-005 adds admin screens without recompiling an enum; constants only at call sites).

```csharp
namespace BentaGo.App.Navigation;

public static class ScreenKeys
{
    public const string Login = "login";
    public const string Home = "home";
    public const string Sales = "sales";
    public const string Stock = "stock";
    public const string Reconciliation = "reconciliation";
    // Reserved for BG-005, catalogued so the help overlay can render them greyed:
    public const string Transactions = "transactions";
    public const string Returns = "returns";
}

/// Symbolic focus target. Views declare elements with the FocusTarget attached
/// property (Id = stable name, IsInitial = focus on arrival). Focus is ALWAYS
/// addressed symbolically so requests survive view recreation and are
/// unit-testable without a UI thread.
public sealed record FocusRequest(string ElementName);

public sealed record NavigationEventArgs(string FromKey, string ToKey, string? FocusElement);

/// Screen view models opt out of disposal-on-evict by implementing this marker.
/// BG-004: all six screens implement it (retention is the default, B-graft).
public interface IRetainableScreen { }

public interface INavigationService
{
    /// Key of the active screen, or null before the first Navigate.
    string? CurrentKey { get; }

    /// Raised after the active screen changes (drives the shell ContentControl).
    event EventHandler<NavigationEventArgs>? Navigated;

    /// Raised when focus must move; the shell (MainWindow) only resolves ElementName -> element.
    event EventHandler<FocusRequest>? FocusRequested;

    /// True when the key can be navigated to (registered AND not blocked by the login gate).
    bool CanNavigate(string key);

    /// Activates the registered screen. Returns false for unknown/blocked keys (no throw,
    /// friendly to Alt+4-parked entries). Re-navigating to Current is a no-op (idempotent).
    void Navigate(string key);   // focus defaults from the registry's InitialFocusTarget

    /// Explicit symbolic focus request (dialog restore, F1 overlay return, scan re-focus).
    void RequestFocus(string elementName);
}

public interface IFocusService
{
    event EventHandler<FocusRequest>? Requested;   // VM -> shell; unit tests assert the payload only
    void Request(string elementName);
}
```

Implementation `NavigationService` (singleton, no WPF types):

- ctor `(IServiceProvider services, IReadOnlyList<ScreenRegistration> registry)`; `ScreenRegistration(string Key, Type ViewModelType, string InitialFocusTarget)`.
- **Retention policy (B-graft, decisive):** the service keeps `Dictionary<string, ViewModelBase> _instances`; first `Navigate(key)` resolves the VM from DI and keeps it; navigation away does **not** dispose a VM implementing `IRetainableScreen` (all six BG-004 screens — the POS cart and reconciliation draft survive an Alt+3 detour and back). A VM implementing `IDisposable` but NOT `IRetainableScreen` is disposed once on evict (machinery + tests exist for BG-005 admin screens). `Dispose()` (app shutdown) disposes every instance once, idempotently.
- `Navigate` sequence: resolve registration (unknown → `false` no-op) → if `key == CurrentKey && Current != null` → no-op → create/keep VM → set `CurrentKey`, raise `Navigated`, then raise `FocusRequest(registry[key].InitialFocusTarget)`.
- Navigation timing (perf probe §13) measured here; `LastNavigationMilliseconds` exposed.

`IFocusService` is implemented by the shell-side `FocusCoordinator` (the only UI-bound focus type): resolves the element name over the current `ContentControl` subtree (matching `FocusTarget.Id`), defers via `Loaded` if content is not yet realized, logs misses.

### 5.2 Shell wiring (`MainWindow.xaml` / `MainViewModel`)

- `MainViewModel` (extended) exposes `Current : ViewModelBase?` (bound to the `ContentControl`), `IsHelpOpen`, `StatusText`, `VersionText`, `NavigateCommand` (param = screen key string), `HelpCommand` (replaces the BG-001 F1 `MessageBox`). Subscribes `NavigationService.Navigated` → `SetProperty(ref _current, e.ViewModel)`.
- Screen `DataTemplate`s are implicit by view-model type in `Views/DataTemplates.xaml` (the BG-001 pattern generalized); `MainWindow` never names a screen.
- `MainWindow` code-behind is limited to WPF-forced mechanisms: (1) resolving `FocusRequested` → element → `.Focus()`; (2) window-level `KeyBinding`s (gesture list from `ShortcutCatalog`, params only — no logic). Everything else lives in view models.
- `ObservableObjectFactory.Build()` gains (extend the existing `extra` lambda; the static singleton provider and `BentaGoHost.Build` stay untouched):

```csharp
services.AddSingleton<INavigationService, NavigationService>();
services.AddSingleton<IFocusService, FocusCoordinator>();
services.AddSingleton<IDialogService, DialogService>();
services.AddSingleton<IAppDataSource, ScenarioDataSource>();   // the BG-005 swap line (§12)
services.AddTransient<MainViewModel>();                        // already present
services.AddTransient<LoginViewModel>();                       // already present
services.AddTransient<PeddlerHomeViewModel>();
services.AddTransient<SalesViewModel>();
services.AddTransient<StockViewModel>();
services.AddTransient<ReconciliationViewModel>();
services.AddTransient<PaymentDialogViewModel>();               // dialogs, not screens
services.AddTransient<ConfirmDialogViewModel>();
services.AddTransient<HelpOverlayViewModel>();
```

BG-005 adds a screen by adding one `AddTransient` + one `ScreenKeys` entry + one `DataTemplate`; nothing else in the shell changes.

### 5.3 Why no code-behind ever navigates

1. **Testability:** navigation (key resolution, idempotency, retention/disposal, focus-request emission) is pure logic testable in plain xunit — no Window, no STA, no Dispatcher (docs/05 unit list demands exactly this).
2. **One choke point:** the perf probe (P95) and focus bookkeeping live inside `NavigationService.Navigate`; no route escapes it.
3. **No view knows another view's name:** the registry is the only VM→key map; screens bind only to their own view models.
4. **Code-boundary rule:** navigation is app-shell policy (docs/02 §2); placing it in code-behind would scatter policy into view files and make the review-gate audit grep impossible.

## 6. Alt-mnemonic table and conflicts-none check

### 6.1 Base shortcuts (docs/03 §4, authoritative, not renegotiated)

`ShortcutCatalog.cs` is the single source of truth: entries `(Guid/CatalogId, ShortcutKey, Gesture, Scope, Action, Status: Active|Reserved|Future, DocRef)`. The F1 help overlay renders it (Active rows as shortcuts, Reserved/Future rows greyed — lane 4 asserts every documented shortcut appears); the conflict tests assert on it; `MainWindow.InputBindings` are generated from it (binding/catalog parity test).

| Shortcut | Action | Status in BG-004 |
|---|---|---|
| `F1` | Help / shortcut list | **Active** (shell-level) |
| `F2` | Focus barcode/search input | **Active** (Sales) |
| `F3` | Product search | **Active** (Sales) |
| `F4` | Hold/suspend cart | **Future** (no cart logic in BG-004; greyed) |
| `F5` | Refresh current data | **Future** (greyed; on-demand refresh is trivial) |
| `F6` | Focus payment method | **Active** (Payment dialog) |
| `F7` | Apply permitted discount | **Reserved** (discount policy = BG-005/0001-5) |
| `F8` | Open returns/adjustments | **Reserved** |
| `F9` | Open today's transactions | **Reserved** |
| `F10` | Open end-trip/reconciliation | **Active** (Home) |
| `F12` | Checkout | **Active** (Sales → opens Payment dialog) |
| `Enter` | Confirm / select highlighted item | **Active** (contextual) |
| `Esc` | Close dialog / cancel current edit | **Active** (contextual) |
| `Delete` | Remove selected cart line after confirmation | **Active** (Sales; line removal is session-local) |
| `+` / `-` | Increase/decrease selected quantity | **Active** (Sales; `Key.Add`/`Key.Subtract` only) |
| `Ctrl+N` | New sale | **Future** (greyed) |
| `Ctrl+P` | Print/reprint current document | **Future** |
| `Ctrl+S` | Save draft | **Active** (Reconciliation — toggles draft state display) |
| `Ctrl+F` | Focus search | **Active** (Stock) |
| `Ctrl+R` | Return to current screen's default state | **Future** |
| `Alt+1` | Home | **Active** |
| `Alt+2` | New sale | **Active** |
| `Alt+3` | Stock | **Active** |
| `Alt+4` | Transactions | **Reserved** (not a BG-004 screen; greyed, unbound) |
| `Alt+5` | Close/reconcile | **Active** |
| `Ctrl+Alt+L` | Lock screen | **Reserved** (0001-10/void authority, BG-005) |
| `Ctrl+Alt+B` / `Ctrl+Alt+T` | Backup / hardware test | **Reserved** (BG-007) |

Excluded by docs/03 §4 and design rule: `Ctrl+C`, `Ctrl+V`, `Ctrl+X`, all numeric-keypad keys, and OS/WPF-native gestures (§6.3). Scanner Enter submits the focused scan field only; it never confirms a payment dialog (the dialog is a separate modal window — see §7).

### 6.2 Screen-by-screen active table (BG-004)

| Screen | Active bindings |
|---|---|
| Login | `F1`, `Enter` (IsDefault submit), `Tab`; **no Alt+1..5** (pre-auth) |
| Peddler Home | `F1`, `F10`, `Alt+1..5` (Alt+4 rendered disabled), `Enter`/`Space` activate focused card/button |
| Sales | `F1`, `F2`, `F3`, `F12`, `Ctrl+F`, `Ctrl+Alt+L`(greyed), `Alt+1..5`, `Delete` (line, with confirm dialog), `+`/`-` (qty), `Enter` (scan submit / select highlighted result), `Esc` (clear scan/search text) |
| Payment dialog | `F1` (help above it, only if opened from dialog scope), `F6`, `Enter` (confirm on CONFIRM button), `Esc` (cancel → return to cart) — all Alt nav and other F-keys **inert** (separate modal window owns input scope) |
| Stock | `F1`, `Ctrl+F`, `F5` (greyed), `Alt+1..5` |
| Reconciliation | `F1`, `Ctrl+S` (draft), `Alt+5` (self), `Esc` (cancel current edit field), `Enter` (contextual confirm) |

`Alt+1..5` are identical gestures across screens — allowed because they are **shell-scoped** (one window-level registration per key, one destination each, active on every authenticated screen). `F10` (Home) and `Alt+5` (shell) denote the same destination — same action, both catalogued, no ambiguity. Per-scope uniqueness is asserted by tests.

### 6.3 Conflicts-none check (documented + tested)

1. **No WPF AccessKey mnemonics.** Alt+1..5 are window-level `KeyBinding`s (`Key.D1..D5`, `Modifiers=Alt`); WPF's `AccessKeyManager` processes mnemonics ahead of command bindings, so the **absence** of digit mnemonics is mandatory. Two enforcement layers: `ScreenStyles.xaml` forbids `_` in any `Button`/`RadioButton`/`CheckBox`/`MenuItem` content, and a test **greps every `*.xaml` under `src/BentaGo.App/`** for `AccessKey="…"` and `_` in content. **Letter-mnemonic note (B):** even if letter mnemonics were ever added, B/L/T are banned everywhere because WPF treats `Ctrl+Alt+letter` as AltGr on many layouts — colliding with the documented `Ctrl+Alt+B`/`L`/`T`. (BG-004 uses no letter mnemonics at all.)
2. **No NumPad.** Bindings use `Key.D1..D5` only; no `Key.NumPad*` gesture exists (test).
3. **Native WPF/OS, never rebound:** `Alt+F4` (window close — the Sales F4 binding is `Modifiers=None, Key=F4`; `KeyBinding` requires the exact modifier set, so Alt+F4 never matches it), `Alt+Space` (system menu), `Alt+Tab`/`Alt+Esc`/`Ctrl+Alt+Del` (OS), `Ctrl+Tab`/`Ctrl+Shift+Tab` (WPF focus navigation — moot: no TabControl in the shell; still never registered), `F11` (absent from docs/03; declared Reserved so no future full-screen conflict).
4. **Scope precedence:** element-level (scan-field `Enter`) > screen-level (`F12` on Sales) > shell-level (`Alt+1..5`, `F1`, `F10` at Home) > dialog-window-level (`Esc`, `F6`, `Enter`). A gesture registered once per scope; the catalog is keyed on `(Scope, Key, Modifiers)`, and a duplicate is a unit-test failure.
5. **Text-editing keys untouched** (docs/03 §4 line 92): no `Ctrl+C/V/X/Z`, no Home/End/arrows/Backspace/numpad digits bound; only POS `+`/`-` quantity keys (`Key.Add`/`Key.Subtract`), which do not collide with caret movement.
6. **Pre-auth scope:** Login registers no `Alt+1..5` (test).
7. **Scanner-Enter in Payment (docs/03 line 125):** the scan field lives in the Sales window; Payment is a separate modal window so a scanner burst cannot reach it. Inside Payment, Enter maps to the CONFIRM button, which is **disabled until the completion rule holds** (split zero / cash sufficient / credit fields filled) — a disabled `IsDefault` button is structurally inert (A). C's stricter variant — Enter moves focus to CONFIRM first, second Enter commits — is acceptable; the disabled-`IsDefault` rule is the contract.
8. **Binding/catalog parity (STA):** instantiating `MainWindow`, its `InputBindings` equal exactly the catalog's `Active` entries (catches drift in either direction).

## 7. Dialog service contract

### 7.1 API (`src/BentaGo.App/Dialogs/`)

```csharp
namespace BentaGo.App.Dialogs;

public abstract class DialogViewModelBase<TResult> : ViewModelBase where TResult : class
{
    public event EventHandler<DialogResultRequest<TResult>>? CloseRequested;
    protected void Close(TResult result);   // raise with non-null -> service closes window
    protected void Cancel();                // raise with null      -> dismissed (Esc/system)
}

public sealed record DialogResultRequest<TResult>(TResult? Result) where TResult : class;

public interface IDialogService
{
    /// True while a modal dialog is open (window-level Esc scope + lane assertions).
    bool IsOpen { get; }

    /// Returns the typed result, or null when dismissed (Esc / Cancel).
    /// focusAfterClose: symbolic FocusTarget to re-request after close (caller VM states it).
    Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class;
}
```

`DialogService` (App layer, DI-registered; the **only** type that calls `ShowDialog()`): hosts the VM in `DialogHost` (`Window`, `Owner = Application.Current.MainWindow`, `CenterOwner`, `NoResize`, `ShowInTaskbar=false`, `MaxWidth=760`/`MaxHeight=600` + inner `ScrollViewer` so the payment dialog fits 1092×614 DIP), binds the VM's `CloseRequested` to window close, awaits a `TaskCompletionSource`, returns `Result`, disposes the dialog VM on close (`finally`, once). Feature VMs never see a `Window`. Concrete results: `PaymentDialogResult(IReadOnlyList<PaymentLineDraft> Lines, decimal change fields)` (BG-005 maps drafts to `NewPaymentLine`s), `ConfirmDialogResult(bool)`; help is a shell overlay, not a dialog window (docs/03: overlay).

### 7.2 Semantics (load-bearing rules)

- **Single top-dialog Esc:** every modal window binds `Esc` to its own cancel command; OS window modality structurally prevents Esc from reaching any other window. The stacking rule for BG-005+: a dialog opened while a dialog is open must be **owned by the top dialog window**, so Esc pops exactly one. `IDialogService.IsOpen` is the observable scope for lane 6; a test guards against a second `ShowAsync` from a non-top scope.
- **Focus restoration:** the service captures `Keyboard.FocusedElement` before `ShowDialog`; on close it dispatches `FocusRequest(focusAfterClose)` (and falls back to the captured element), flowing through `IFocusService`. Symbolic addressing means restoration works even when the view was recreated; unit tests assert the request (the "focus restoration requests" unit item), lane 6 asserts the real focus side effect.
- **View-model-driven results:** the dialog VM's `ConfirmCommand`/`CancelCommand` are the only authors of `Result`; `CanConfirm` (the completion gate) is the command-enablement unit-test target. Esc ⇔ dismissed ⇔ `null` result; the invoking VM decides the outcome (e.g. Payment cancelled → cart untouched, scan field re-focused).

## 8. Validation templates and the field-error contract

### 8.1 View-model contract (`src/BentaGo.App/Mvvm/ValidatableViewModelBase.cs`)

Per B (WPF-native), chosen over A's/C's plain-property shapes: `INotifyDataErrorInfo` is the contract WPF's default `ValidatesOnNotifyDataErrors=true` already reacts to, so the red border + error template appear with **zero per-field view glue** — the framework does the risky adornment logic (encode-lessons-in-structure).

```csharp
namespace BentaGo.App.Mvvm;

public abstract class ValidatableViewModelBase : ViewModelBase, INotifyDataErrorInfo
{
    public bool HasErrors { get; }                              // any field has an error
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName);

    /// Sets/clears one field error; null clears. Raises ErrorsChanged and
    /// re-raises HasErrors when the overall state changes.
    protected void SetFieldError(string propertyName, string? message);

    /// Combined setter+validator: mutates via SetProperty, then applies validate(value)
    /// to SetFieldError(propertyName, …). Errors drive command enablement (HasErrors).
    protected bool SetValidatedField<T>(ref T field, T value, Func<T, string?> validate,
                                        [CallerMemberName] string? propertyName = null);

    protected void ClearFieldErrors();
}
```

Contract the view models follow: a field's error copy answers **what happened and what to do next** (docs/03 §7), e.g. Sales `ScanError` verbatim "Barcode not found. Search by product name or ask admin to import it."; Reconciliation `CountedError` "Enter a number, or leave blank if not counted." BG-004's active validations (demonstrating the contract without inventing business rules): Login required username/password on submit (form-level generic failure copy stays, never field-level for authentication failures); Payment split/cash completion gates (enablement, not adornment); parse failures ride `ValidatesOnExceptions` + the themed `ErrorTemplate`. Everything else arrives with BG-005 rules on the same base.

### 8.2 Templates (`Styles/Validation.xaml`)

- **`ErrorTemplate.TextBox` / `ErrorTemplate.PasswordBox`** (`ControlTemplate` for `Validation.ErrorTemplate`): `AdornedElementPlaceholder` wrapped in a `Brush.Status.Error` 1.5 px border + ⚠ glyph, with the corrective message text (`ErrorContent`) rendered by `ErrorTextStyle`. Applied via the shared `TextBox`/`PasswordBox` styles — the shell never hand-places error chrome per field.
- **`ErrorTextStyle`** (`TextBlock`): error color, `FontSize.Label`, wrapping, top margin — also available standalone for form-level message lines (login status).
- Parse + semantic errors converge on the same visual vocabulary; the VM never touches `Validation.*`.

## 9. Module map

```
src/BentaGo.App/
├── App.xaml(.cs)                  # merges Themes/* + Styles/* + Views/DataTemplates.xaml; HC swap hook;
│                                  #   boot unchanged (ObservableObjectFactory) + optional --size WxH lane arg
├── DesignTokens.cs                # token key consts + All (drive smoke tests; renames are compile events)
├── Themes/
│   ├── Theme.xaml                 # tokens: Brush.*, FontSize.*, Thickness.*, Corner.*, FocusVisual.Default
│   └── Theme.HighContrast.xaml    # same keys, SystemColors-backed (key parity tested)
├── Styles/
│   ├── ScreenStyles.xaml          # §4.5 keys
│   └── Validation.xaml            # ErrorTemplate.TextBox/.PasswordBox, ErrorTextStyle
├── Views/
│   └── DataTemplates.xaml         # VM→View DataTemplate map (extended BG-001 pattern)
├── MainWindow.xaml(.cs)           # rail + ContentControl + StatusBar; window KeyBindings from catalog;
│                                  #   ONLY FocusRequest resolution + F1 plumbing
├── MainViewModel.cs               # + Current, IsHelpOpen, NavigateCommand, HelpCommand, Status/Version
├── Navigation/
│   ├── ScreenKeys.cs, FocusRequest.cs, NavigationEventArgs.cs, ScreenRegistration.cs, IRetainableScreen.cs
│   ├── INavigationService.cs, NavigationService.cs, IFocusService.cs, FocusCoordinator.cs
│   ├── ScreenRegistry.cs          # ScreenRegistration.All: keys → VM types → initial focus targets
│   └── Shortcuts/ShortcutCatalog.cs  # single source: (id, gesture, scope, action, Status, DocRef)
├── Dialogs/
│   ├── IDialogService.cs, DialogService.cs, DialogHost.xaml(.cs)
│   ├── DialogViewModelBase.cs
│   └── Payment/ PaymentDialogView.xaml(.cs), PaymentDialogViewModel.cs   (+ Confirm/ dialog)
├── Mvvm/
│   ├── ObservableObjectFactory.cs # + registrations (§5.2)
│   ├── RelayCommand.cs            # + NotifyCanExecuteChanged() (additive, §10)
│   ├── ViewModelBase.cs           # unchanged
│   └── ValidatableViewModelBase.cs
├── Ui/
│   └── FocusTarget.cs             # attached property Id + IsInitial; AutomationIds.cs (stable lane ids)
├── Data/
│   ├── IAppDataSource.cs          # §12 seam
│   ├── AppDataProjections.cs      # snapshot/read models
│   └── ScenarioDataSource.cs      # runs TwoPeddlersScenario over InMemoryLedgerStore once
└── Features/
    ├── Login/        (restyle; code-behind shrinks to PasswordBox marshaling; Enter via IsDefault)
    ├── PeddlerHome/  PeddlerHomeView.xaml(.cs), PeddlerHomeViewModel.cs
    ├── Sales/        SalesView.xaml(.cs), SalesViewModel.cs (+ CartLineDraft, ReceiptState)
    ├── Stock/        StockView.xaml(.cs), StockViewModel.cs
    ├── Reconciliation/ ReconciliationView.xaml(.cs), ReconciliationViewModel.cs
    └── Help/         HelpOverlayView.xaml(.cs), HelpOverlayViewModel.cs (binds ShortcutCatalog)

tools/BentaGo.LaneDriver/          # console: UIA + PrintWindow + SendKeys lane runner (§11)

tests/BentaGo.Tests/App/           # namespace BentaGo.Tests.AppShell → --filter AppShell matches (§10)
```

No new NuGet packages (repo centralizes versions in `Directory.Packages.props`; native WPF + xunit only — the STA fixture is hand-rolled). C# style per `.editorconfig`: file-scoped namespaces, 4-space indent, LF.

## 10. Test plan (mapped to the docs/05 unit list)

Namespace `BentaGo.Tests.AppShell` — **verified this session** that the literal `dotnet test -c Release --filter AppShell` matches by FQN substring (xunit adapter; no-match exits 0). Build/test must use the pinned SDK: `& 'C:\Users\nicky\.dotnet\dotnet.exe' test -c Release --filter AppShell` (the PATH `dotnet` is SDK 8.0.419 and cannot build `net10.0-windows`; verified working build with the user-local SDK this session).

| docs/05 unit item | Test file | What is asserted | UI thread? |
|---|---|---|---|
| navigation | `NavigationTests` | `Navigate(key)` activates registered screen (`CurrentKey` + `Navigated(From,To,FocusElement)`); unknown key returns false/no-op; re-Navigate to current is a no-op; **retained** VMs are reference-equal across 100 switches (no duplicates); `CanNavigate` false before auth and for unknown keys; `RequestFocus` forwards | No |
| command enablement | `CommandEnablementTests` | Empty cart disables CHECKOUT; Payment `CanConfirm` false until split balance zero / cash sufficient / credit complete; Submit-Close gate; Login `CanLogin` (extends BG-001); F12 command parity | No |
| focus restoration requests | `FocusRestorationTests` | Dialog close (confirm AND cancel) raises `FocusRequest(focusAfterClose)`; navigation raises the registry's initial target; F1 overlay close re-requests the invoking target; `IFocusService.Requested` payloads captured via recording fake | No |
| shortcut conflicts | `ShortcutConflictTests` | Catalog: no duplicate `(Scope, Key, Modifiers)` among Active; no NumPad; no `_`-mnemonic content (XAML grep over `src/BentaGo.App/**/*.xaml`); no B/L/T letter mnemonics; reserved F1/F10/F12 not rebound in the same scope; Login scope has no Alt+1..5; every Active entry is bound exactly once; Reserved entries unbound; text-editing keys unbound; `Alt+4`/F8/F9/Ctrl+Alt+L/B/T flagged Reserved | No (+1 STA subcase: `MainWindow` binding/catalog parity) |
| validation state | `ValidationStateTests` | `SetFieldError` → `HasErrors` + `GetErrors` content; clear → restored + `ErrorsChanged`; `SetValidatedField` validator; error copy non-empty and matches docs/03 wording for scan-not-found | No |
| dialog results | `DialogResultTests` | Fake `IDialogService`: confirm path → typed result; Esc/cancel → `null`; result routing from dialog VM state; completion gates; top-dialog stack guard | No |
| view-model disposal | `DisposalTests` | `IRetainableScreen` VMs NOT disposed on navigate-away; non-retainable `IDisposable` VM disposed once on evict (policy machinery); `NavigationService.Dispose` disposes each once; double-dispose safe | No |
| (design system) | `ResourceSmokeTests` | §4.6: load order, every `DesignTokens.All` key resolves, style probes, HC key parity, Validation templates | **STA** |
| (layout) | `LayoutProbeTests` | §4.6: each screen fits 1092×614 DIP; touch targets ≥ 44 DIP; payment dialog fits `DialogHost` caps | **STA** |
| (theme live) | `HighContrastTests` | HC dictionary installed when `SystemParameters.HighContrast` true (fake predicate); resolved `Brush.TextPrimary` equals the SystemColors value in HC mode | **STA** |

**STA tier (≤ 6 tests):** `AppShellStaFixture` — one STA thread + one `Application` per fixture (`[CollectionDefinition("AppShellSta", DisableParallelization = true)]`), `Run<T>(…)` marshals to the STA dispatcher; no new packages. Everything else runs in CI like BG-001 tests.

**Command enablement determinism (C-graft):** extend `RelayCommand` with `public void NotifyCanExecuteChanged()` (raise directly; keep the `CommandManager.RequerySuggested` hook for UI). VM setters that affect enablement call it — `CanExecute` tests become deterministic instead of depending on requery timing.

**Perf proxy:** `DisposalTests` also runs 500 headless navigate/evict cycles asserting managed growth under budget (unit-level proxy for the perf lane; the real probe is §13).

**Live-lane driver** (`LiveLanes/`, env-gated): absent by default so the mandated `--filter AppShell` run stays headless — §11.

## 11. Live lanes L1–L10 and the review pack

**Constraint (verified this session):** Computer Use is UNAVAILABLE — no image-capable model exists in this session, so no lane can be *perceived* by an agent. Lanes are therefore **executed and evidenced as files** (PNG renders + input/focus/verdict logs); the driver asserts *measurable state* (`CurrentKey`, `Keyboard.FocusedElement` name, `IsOpen`, `Validation.HasError`, element bounds) instead of pixels. The review gate artifact is the **operator's pack**; producing PNGs needs no perception (WPF `RenderTargetBitmap` renders any shown window).

**Driver stack (two layers, zero extra installs; AutoHotkey NOT installed — probed — and not required):**

- **Layer A — in-process STA driver** (`tests/BentaGo.Tests.AppShell/LiveLaneDriverTests`, env-gated `BENTAGO_LIVE_LANES=1`): hosts MainWindow + DataTemplates + scenario data on the STA fixture; drives via `AutomationPeer` (`InvokePattern`, named elements from the shared `AutomationIds`), routed-command raises where possible, `Keyboard.FocusedElement` assertions, window counting, and **off-screen `RenderTargetBitmap` screenshots** (no desktop needed). This is the headless core of every lane.
- **Layer B — `tools/BentaGo.LaneDriver` console** (`net10.0-windows`): launches the built `BentaGo.App.exe` (Release; optional `--size WxH` boot arg honored in `App.OnStartup`), drives via `System.Windows.Automation` + `SendKeys` (PowerShell `Add-Type` P/Invoke `SendInput` when needed), captures via `PrintWindow` + `Graphics.CopyFromScreen` fallback → PNG, writes `artifacts/bg004/<lane>.png` + a JSON verdict (`PASS`/`FAIL`/`OPERATOR` with reason). Needs an interactive Windows session; no image-capable model.

**Environment facts (verified this session):** pinned SDK 10.0.400 at `C:\Users\nicky\.dotnet\dotnet.exe` (PATH `dotnet` = SDK 8.0.419, cannot build `net10.0-windows`); runtime `Microsoft.WindowsDesktop.App 10.0.11` machine-wide; `ffmpeg` NOT installed anywhere probed; host = Windows 11 (NT 10.0.26200); `artifacts/` is gitignored (review pack stays operator-local).

### 11.1 Lane mechanics

| Lane | Evidence file | Automated driver | Operator needed? |
|---|---|---|---|
| L1 login @1366 | `bg-004-login-1366.png` | Layer A renders Login at 1366×768 DIP; asserts every named element's bounds inside the window and no scrollbar/overflow | eyeball PNG |
| L2 login @1080 | `bg-004-login-1080.png` | Same at 1920×1080; asserts no excessive stretch (no element wider than its region cap) | eyeball PNG |
| L3 Alt navigation | `bg-004-alt-navigation.png` | Alt+1/2/3/5 via Layer B SendKeys; asserts `CurrentKey` changed AND `FocusedElement` == the destination's initial focus target (AutomationId); renders each destination | eyeball PNG |
| L4 F1 help | `bg-004-shortcuts.png` | F1; asserts help overlay lists EVERY `ShortcutCatalog` entry (Active rendered, Reserved greyed) — "conflicts absent" = equality check with the catalog | eyeball PNG |
| L5 tab focus order | `bg-004-login-focus.png` | Loop Tab on login; records focused AutomationId sequence == Username→Password→Log in; asserts focus ring present | eyeball PNG |
| L6 dialog Esc | `bg-004-dialog.png` | From Sales F12 → asserts `IsOpen`; Esc → asserts dialog closed, `CurrentKey` still `sales`, `FocusRequest("Sales.ScanBox")` raised and real focus returned to the scan field | eyeball PNG |
| L7 validation | `bg-004-validation.png` | Submit empty login / bad scan → asserts `Validation.HasError` + error text non-empty with corrective copy + error border applied | eyeball PNG |
| L8 six-screen switch | `bg-004-navigation-state.png` | Navigate all six in sequence; asserts `CurrentKey` changes each time, exactly one content child (no duplicates), `IsOpen` false between screens (no stale dialogs) | eyeball PNG |
| L9 high contrast | `bg-004-high-contrast.png` | `--sim-hc` flag forces `Theme.HighContrast.xaml`; asserts swapped backgrounds + focus/status/action elements still present (AutomationIds) + brush-sourcing assert. **Real OS toggle = operator step** (Settings → Accessibility → Contrast themes), then rerun | **yes** — OS contrast toggle once |
| L10 125% scaling | `bg-004-scaling.png` | Layer A renders at 1708×960 device px (1366×1.25) + Layer B under a real 125% session asserts no control bounds overflow. Note: at 125% the *logical* work area of a 1366×768 display is 1092×614 DIP — the layout contract and `LayoutProbeTests` target exactly that; lanes launch maximized (POS stations run maximized) | **partial** — host may need operator to set 125% display scale once |

### 11.2 Review-pack procedure (operator artifact; docs/05 275–279)

1. Build Release: `& 'C:\Users\nicky\.dotnet\dotnet.exe' build C:\BentaGo\BentaGo.sln -c Release`.
2. Unit gate: `& 'C:\Users\nicky\.dotnet\dotnet.exe' test -c Release --filter AppShell`.
3. Live lanes: `BENTAGO_LIVE_LANES=1 & 'C:\Users\nicky\.dotnet\dotnet.exe' test -c Release --filter "FullyQualifiedName~LiveLanes"` and/or `dotnet run --project tools/BentaGo.LaneDriver -- all` — writes `bg-004-*.png` + verdicts under `artifacts/bg004/`.
4. Copy the six screenshots @1366×768 into `artifacts/review/bg-004-<screen>.png` (login-1366, peddler-home, sales, payment, stock, reconciliation).
5. **Video:** ffmpeg is NOT installed. Concrete options, in order: (a) operator records the scripted 30–60 s keyboard pass with Xbox Game Bar (`Win+Alt+R`) or Snipping Tool, saving `artifacts/review/bg-004-navigation.mp4`; or (b) operator approves `winget install ffmpeg` once, after which the pass is recorded headlessly with `ffmpeg -f gdigrab -framerate 30 -i desktop artifacts/review/bg-004-navigation.mp4`. The driver additionally emits a per-keystroke frame pack + `bg-004-navigation.trace.txt` (ordered action/focus transcript) so the reviewer has evidence even before the video lands. Scripted pass: login → Alt+2 → F2 → F12 → Esc → Alt+1 → F10 → Ctrl+S → Alt+3 → F1 → Esc.
6. Post screenshots + video for the operator. **STOP at merge-ready; wait for the operator's click.**

## 12. Placeholder data seam (in-process)

Authoritative BG-002 contract consumed here: `TwoPeddlersScenario.Run(ILedgerStore)` (fixed `ScenarioIdSource` seed `two-peddlers-v1`, fixed clock; byte-identical reruns; `LedgerState.FundsOnHand`/`StockOnHand`, movements, audits, documents). BG-004 rule: read-only screens render scenario-derived data in-process; **no BG-004 code writes, computes business totals, or closes anything**.

Seam (C's snapshot shape — smallest surface, one swap line; A/B's method-based shape converges to the same boundary):

```csharp
namespace BentaGo.App.Data;

/// Read-only scenario data seam. BG-005 replaces the implementation with a
/// service-backed provider behind the SAME interface; screens never know.
public interface IAppDataSource
{
    /// Immutable snapshot; screens bind once per navigation.
    AppSnapshot Current { get; }

    /// BG-005: repository change notifications (post-checkout, post-close).
    event EventHandler? Changed;
}

public sealed record AppSnapshot(
    SessionHeader Session,                       // greeting, peddler, trip, online, now
    DashboardSummary Dashboard,                  // TodaySales, Transactions, CashSales, RemainingItems
    IReadOnlyList<StockRow> Stock,               // Sku, Product, Loaded, Sold, DamagedLost, Returned, OnHand, LastMovementUtc, IsLow
    ReconciliationSummary Summary);              // ByMethod, GrossSales, Discounts, NetSales, ExpectedCash
```

`ScenarioDataSource` (singleton): lazily constructs `InMemoryLedgerStore`, runs `TwoPeddlersScenario.Run(store)` once (milliseconds; skip if the store already has movements), and projects pure read models from `store.State` + documents. **The exact BG-005 swap:** one line in `ObservableObjectFactory.Build()`'s `extra` lambda — `services.AddSingleton<IAppDataSource, ScenarioDataSource>()` → `services.AddSingleton<IAppDataSource, SqliteAppDataSource>()` (+ `Changed` events); no view model, no view, no signature change. Nothing else in `BentaGo.App` references the scenario or the in-memory store. `ScenarioReport.Text` is not displayed (it is the golden tool render, not a UI model). The scenario is a closed day (Alice closed + reopened) — the UI renders that state faithfully and annotates "simulated data" in the status bar so lanes read correctly.

## 13. Performance plan (docs/05 268–273)

- **Baseline first:** record BG-001 shell navigation (login → authenticated) P95 at both target resolutions on the pinned host before BG-004 work; save `artifacts/perf/bg-004-nav-baseline.txt`.
- **Metric:** P95 navigation time from command to rendered destination — measured inside `NavigationService.Navigate` (stopwatch) + render event; probed in the LaneDriver (100 timed `Alt+2` navigations after startup, stopwatch around command → destination `Loaded`, via a debug timing file the app writes).
- **Probe:** 100 navigation changes after startup at 1366×768 and 1920×1080; the 500-change memory probe measures managed growth.
- **Rule:** fail when P95 > 150 ms or managed memory grows > 20 MB after 500 changes.
- **Design implications:** scenario snapshot is a singleton computed once; navigation with retention is a single `ContentControl` content swap (no re-instantiation of retained VMs); no images/loads per navigation; `DynamicResource` brush lookup cost is negligible at this control count (probe watches it); no animations/transitions (they fight the perf rule and high contrast).

## 14. Risks

- **DPI scaling (lane 10).** WPF on .NET Core 3+ is per-monitor DPI aware by default (verify exact behavior on .NET 10/Windows 11 26200 at implementation). The worst case is 1366×768 physical at 125 % = **1092×614 DIP work area**; layout uses star-sizing + `ScrollViewer` + fixed pip rows and `LayoutProbeTests` assert the 1092×614 bound; lane L10 launches maximized. Residual risk: the payment dialog (tabs + keypad + amounts) at 1092×614 — mitigated by `DialogHost` size caps (760×600) and inner scroll; keypad buttons keep ≥ 44 DIP touch targets.
- **High contrast (lane 9).** `SystemParameters.HighContrast` timing varies by Windows build; `SystemColors` refresh can require a relaunch. Mitigations: HC swap dictionary with key-parity test, `--sim-hc` lane flag, relaunch step in L9, and status always text + glyph + color (docs/03 §7).
- **.NET 10 WPF quirks.** `net10.0-windows` is a new TFM (runtime 10.0.11 deployed here): XAML compiler behavior, `{DynamicResource SystemColors.*}` in DataTemplates, DataGrid measure under PMv2, and `CommandManager.RequerySuggested` timing need early coverage — `ResourceSmokeTests`/`LayoutProbeTests` (STA) are the tripwire, and `RelayCommand.NotifyCanExecuteChanged()` removes requery timing from unit tests. No third-party UI packages (locked: native WPF).
- **SDK on PATH.** PATH `dotnet` is SDK 8.0.419; only `C:\Users\nicky\.dotnet\dotnet.exe` (10.0.400, the exact `global.json` pin) builds. All commands in this doc use the user-local path; lane scripts should guard with a clear error. Verified: full solution builds 0 warnings / 0 errors Debug with the user-local SDK.
- **ffmpeg absent.** The review video needs an operator install (Game Bar fallback orelse `winget install ffmpeg`); gdigrab needs a live desktop session. Frame pack + trace transcript are the reviewer's fallback evidence.
- **No perception available.** Live lanes assert measurable state and produce files; the operator reviews pixels. Lane-gate "pass" claims must cite the file + verdict JSON, not a visual read.
- **Shared-repo concurrency.** Other orchestration lanes (bg-003) operate in this checkout; `git branch --show-current` was observed flipping branch during this session and an untracked `docs/designs/bg-003-synthesis.md` appeared from another agent. Verify the branch is `bg-004` and stage **only** `docs/designs/bg-004-synthesis.md` immediately before commit and push; never `git add -A`.
- **Provisional palette.** Accent/brand hex values are placeholders until matched to the reference image by the implementer (image unreadable this session); token *structure* is the contract, hues are flagged as provisional in `Theme.xaml`.

## 15. Arena record

**Runners:** three parallel candidate designers (same session model — generation-bound task; diversity came from the three mandated directions, not model families). No dropouts.

**Rubric (grade per axis, 1–5):** keyboard-only navigation; Alt-mnemonic conflict handling; focus restoration contract; dialog top-only Esc scoping; testability without a UI thread; six screens at 1366×768/1920×1080 @125 %; high contrast; code-behind emptiness; design-system structure (mandate: token set in one theme dictionary + per-screen dictionaries); interface depth; contract fidelity (docs/05 unit list, lanes, brief "MVVM via existing" rule).

| Axis | A (ContentControl + NavService) | B (TabControl + windows hybrid) | C (design-system first) |
|---|---|---|---|
| Keyboard-only nav | 5 (one metaphor, shell-level Alt bindings) | 3 (two metaphors: tabs + dialogs; Ctrl+Tab secondary) | 5 (same shell shape as A) |
| Alt conflicts | 5 (no `_` mnemonics, exact-modifier KeyBindings, F11 reserved) | 4 (digit ban + B/L/T ban; TabControl extras) | 5 (digit AccessKey ban + parity test) |
| Focus restoration | 5 (symbolic FocusRequest contract) | 4 (IFocusRouter capture; two APIs) | 5 (FocusTarget/IFocusService) |
| Dialog top-only Esc | 5 (modal windows, structural) | 5 (modal windows, structural) | 5 (modal windows, structural) |
| Headless testability | 5 | 4 (TabControl selection is VM state; still fine) | 5 |
| Resolutions @125% | 5 (1092×614 DIP + maximized convention) | 3 (TabControl header row eats vertical space) | 5 (same DIP math + LayoutProbe) |
| High contrast | 5 (HC redefinition dictionary + swap) | 4 (DynamicResource + HighContrastManager) | 5 (HC parity test, SystemColors sourcing) |
| Code-behind emptiness | 5 (focus marshaling only) | 3 (DialogWindow host + forwarder, both sanctioned) | 5 |
| Design-system structure | 4 (Theme/Controls/per-screen split) | 3 (same tokens; less emphasis) | 5 (tokens-first, zero-literal rule, DesignTokens.cs) |
| Interface depth | 5 (deep NavigationService; small public surface) | 4 (three small interfaces; two-metaphor cost) | 5 (3 interfaces ≤3 members) |
| Contract fidelity | 5 | 4 (Alt+4 placeholder tab; receipt-result deferred) | 5 |

**Base: Candidate A.** The three-way agreement on the shell shape: A and C independently chose the single-`ContentControl` + `NavigationService` shape (docs/03 §8's own mapping: "Window + Grid/DockPanel + ContentControl navigation region + StatusBar"), which is also the minimal delta over the BG-001 shell (its ContentControl + DataTemplate pattern already exists). A scores highest on the mandated unit list (disposal story, no-stale-dialogs), the perf rule (single content swap), and the code-behind audit surface, while matching B and C on dialogs/focus/testability.

**Grafted from C (design-system-first):** build order and gates — Theme.xaml + ScreenStyles.xaml + Validation.xaml + `DesignTokens.cs` + `ResourceSmokeTests`/`LayoutProbeTests` land with **no screens and no shell** existing; screens become near-pure markup with a zero-literal rule and a grep review gate; HC key-parity test; `RelayCommand.NotifyCanExecuteChanged()`; `--size WxH` lane boot arg; DialogHost size caps (760×600); the "parked = catalogued but never bound to fake behavior" shortcut policy; `IAppDataSource` snapshot seam.

**Grafted from B (tab hybrid):** screen view-model **retention** across navigation (POS cart + reconciliation draft survive screen switches — rejects A/C's fresh-VM-per-nav); `DialogViewModelBase<TResult>` with `CloseRequested`/`Cancel` (finer than a plain Confirm/Cancel pair); the AltGr insight (B/L/T letter mnemonics banned if letters are ever used — BG-004 uses none); per-scope mnemonic uniqueness discipline; `IsOpen` guard against a second non-top `ShowAsync`.

**Rejected (with reason):**
- B's **TabControl hybrid** — two navigation metaphors (Alt switches vs Esc closes) split the keyboard model; the TabControl header row costs vertical space at 1366×768; `Ctrl+Tab` cycling becomes a third focus surface the docs/03 table never assigns; "all tabs alive" duplicates what retention gives A for free; docs/03 §8 prescribes the ContentControl region. B's genuine value (state retention, modal windows) is grafted without the tabs.
- A/C's **fresh-VM-per-navigation** — deterministic disposal is attractive and passes the disposal tests, but it destroys the POS cart and reconciliation draft on any screen detour (A admits this; F4-hold is a poor substitute for "I checked a stock price and my cart is gone"). Retention keeps UX and still exercises the disposal contract through the evict-policy machinery + shutdown dispose.
- A/C's **plain-string / Errors-dictionary validation** — adequate, but `INotifyDataErrorInfo` (B) is the WPF-native contract the binding system already reacts to, so the red-border adornment needs zero per-field view glue.
- C's **XxxError-only seam (`GetDashboard()/…` method calls)** — method-based data is fine (A/B converged), but C's immutable `AppSnapshot` exposes the smallest surface (one member) and BG-005's swap is literally one registration line; projection detail stays behind the interface.

**Red-flag screen (design-red-flags.md):** no candidate showed a shallow module (all hide real complexity behind small surfaces); the only leakage risk — shortcut strings appearing both in catalog and in help — is neutralized by making `ShortcutCatalog` the single source rendered by the overlay and asserted by tests; no temporal decomposition (navigation/dialog/validation own their knowledge); pass-through risk in B's `IShellNavigation` adapter is eliminated by rejecting the tab shape.

**Convergence note:** all three candidates independently converged on (1) owner-modal window dialogs with per-window Esc scoping, (2) typed result objects, (3) a single shortcut catalog + conflict tests, (4) the `BentaGo.Tests.AppShell` namespace for the `--filter AppShell` command, (5) a scenario-derived read-model seam swapped by one DI line, (6) `DynamicResource` brush tokens with an HC redefinition dictionary, (7) no `_` digit mnemonics, (8) a headless lane driver without ffmpeg/AHK. Where N candidates converge on the same shape, that is the strong-agreement signal the arena skill describes; this synthesis ships the consensus with the divergences resolved above.

## 16. Open questions for the implementer

1. **Brand accent:** provisional `Brush.Accent` `#0B6E4F` / `Brush.Brand` `#14532D` must be matched to `designs/bentago-wpf-mvp-screen-set.png` visually (no image-capable model this session). Adjust the token rows before screens are styled; token *structure* is fixed.
2. **Alt+4 (Transactions):** visible-disabled rail item + greyed help row (recommended), or omitted entirely until BG-005? Both satisfy the conflict tests; the first keeps lane 4's "every documented shortcut appears" honest.
3. **F4/F7/F8/F9/Ctrl+Alt+L/B/T as parked-not-bound:** confirm "catalogued + greyed in help, never bound" is acceptable (recommended) vs the risk that a reviewer misreads greyed entries as broken shortcuts.
4. **Login restyle scope:** BG-001 `LoginView.xaml.cs` currently carries PasswordBox marshaling + an Enter `KeyDown` handler. Plan: keep only the PasswordBox bridge, delete the redundant Enter handler (the button is already `IsDefault`), move initial focus to `FocusTarget.IsInitial`. Confirm that contract.
5. **Retention vs disposal:** all six screens are `IRetainableScreen` — confirm the mandated "view-model disposal" unit coverage via the evict-policy machinery + shutdown dispose satisfies the contract's intent, since no BG-004 screen actually evicts mid-session.
6. **LiveLanes DPI:** verify WPF PMv2 default on .NET 10/Windows 11 26200 before finalizing the L10 render sizes (1708×960 device px) and the maximized-lane convention.
7. **Video tooling:** Game Bar recording is zero-install; `winget install ffmpeg` makes the lane fully automated. Which does the operator prefer for `bg-004-navigation.mp4`?
8. **`RelayCommand.NotifyCanExecuteChanged()`** is an additive change to the BG-001 `Mvvm/RelayCommand.cs` — confirm adding a public method to an existing BG-001 file is within BG-004's ownership boundary (it is required for deterministic command-enablement tests).

## 17. Next implementation step

Phase (0) per C's sequencing — with **no screens and no shell existing**: `DesignTokens.cs` key constants, `Themes/Theme.xaml` + `Themes/Theme.HighContrast.xaml`, `Styles/ScreenStyles.xaml` + `Styles/Validation.xaml`, the `AppShellSta` fixture + `ResourceSmokeTests` + `LayoutProbeTests` in `tests/BentaGo.Tests/AppShell/`; `dotnet test -c Release --filter AppShell` must pass with zero screens. Then (1) `NavigationService` + `IFocusService` + `MainViewModel` wiring + restyled Login, (2) the five remaining screens as thin markup, (3) dialogs + validation + `ShortcutCatalog` + the full unit suite, (4) `tools/BentaGo.LaneDriver` + live lanes + perf probe, (5) the review pack — then stop at merge-ready for the operator.