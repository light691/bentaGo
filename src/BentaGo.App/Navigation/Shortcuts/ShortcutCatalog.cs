using System.Windows.Input;

namespace BentaGo.App.Navigation.Shortcuts;

/// <summary>
/// Lifecycle of a shortcut: Active entries are real gestures (bound exactly
/// once per scope), Reserved entries are documented but intentionally not
/// implemented in BG-004 (greyed in the help overlay, never bound to fake
/// behavior), Future entries are documented but parked for later slices.
/// Per-scope uniqueness on (Scope, Key, Modifiers) is asserted by the
/// ShortcutConflictTests, and the F1 overlay renders this list verbatim, so
/// this class is the single source of truth for every shortcut (bg-004-
/// synthesis §6).
/// </summary>
public enum ShortcutStatus
{
    Active,
    Reserved,
    Future,
}

public sealed record ShortcutEntry(
    string Id,
    string Gesture,
    Key Key,
    ModifierKeys Modifiers,
    string Scope,
    string Action,
    ShortcutStatus Status,
    string DocRef);

public static class ShortcutCatalog
{
    /// <summary>Shell-scoped: window-level gestures that apply to every screen.</summary>
    public const string ScopeShell = "Shell";

    /// <summary>Screen-scoped: applies on one or more authenticated screens; the
    /// binding is window-level and the target command no-ops off-scope.</summary>
    public const string ScopeScreen = "Screen";

    /// <summary>Dialog-scoped: bound inside the modal dialog window (never MainWindow).</summary>
    public const string ScopePaymentDialog = "PaymentDialog";

    /// <summary>Contextual: element-level or IsDefault semantics; never an InputBinding.</summary>
    public const string ScopeContextual = "Contextual";

    /// <summary>
    /// The complete documented shortcut surface (docs/03 §4), one entry per
    /// documented gesture. Rendered verbatim by the F1 help overlay.
    /// </summary>
    public static IReadOnlyList<ShortcutEntry> All { get; } = BuildCatalog();

    /// <summary>
    /// The entries MainWindow generates InputBindings from: Active, bound at
    /// the window scope (not contextual, not dialog-scoped). The
    /// binding/catalog parity test compares MainWindow.InputBindings against
    /// exactly this list.
    /// </summary>
    public static IReadOnlyList<ShortcutEntry> WindowBindings { get; } =
        All.Where(entry => entry.Status == ShortcutStatus.Active
                           && entry.Scope is not (ScopeContextual or ScopePaymentDialog)).ToArray();

    private static ShortcutEntry E(
        string id, string gesture, Key key, ModifierKeys modifiers, string scope,
        string action, ShortcutStatus status, string docRef) =>
        new(id, gesture, key, modifiers, scope, action, status, docRef);

    private static IReadOnlyList<ShortcutEntry> BuildCatalog() =>
    [
        // ---- Function keys (docs/03 §4) ----
        E("help", "F1", Key.F1, ModifierKeys.None, ScopeShell,
          "Help / shortcut list", ShortcutStatus.Active, "docs/03 F1"),
        E("focus-scan", "F2", Key.F2, ModifierKeys.None, ScopeScreen,
          "Focus barcode/search input", ShortcutStatus.Active, "docs/03 F2"),
        E("product-search", "F3", Key.F3, ModifierKeys.None, ScopeScreen,
          "Product search", ShortcutStatus.Active, "docs/03 F3"),
        E("hold-cart", "F4", Key.F4, ModifierKeys.None, ScopeScreen,
          "Hold/suspend current cart", ShortcutStatus.Future, "docs/03 F4"),
        E("refresh", "F5", Key.F5, ModifierKeys.None, ScopeScreen,
          "Refresh current data", ShortcutStatus.Future, "docs/03 F5"),
        E("focus-payment-method", "F6", Key.F6, ModifierKeys.None, ScopePaymentDialog,
          "Focus payment method", ShortcutStatus.Active, "docs/03 F6"),
        E("discount", "F7", Key.F7, ModifierKeys.None, ScopeScreen,
          "Apply permitted discount", ShortcutStatus.Active, "docs/03 F7"),
        E("returns", "F8", Key.F8, ModifierKeys.None, ScopeScreen,
          "Open returns/adjustments", ShortcutStatus.Reserved, "docs/03 F8"),
        E("transactions-f9", "F9", Key.F9, ModifierKeys.None, ScopeScreen,
          "Open today's transactions", ShortcutStatus.Active, "docs/03 F9"),
        E("end-trip", "F10", Key.F10, ModifierKeys.None, ScopeShell,
          "Open end-trip/reconciliation", ShortcutStatus.Active, "docs/03 F10"),
        E("fullscreen", "F11", Key.F11, ModifierKeys.None, ScopeShell,
          "Full screen (reserved)", ShortcutStatus.Reserved, "docs/03 absent; kept reserved"),
        E("checkout", "F12", Key.F12, ModifierKeys.None, ScopeScreen,
          "Checkout (opens payment)", ShortcutStatus.Active, "docs/03 F12"),

        // ---- Confirm / cancel / edit (docs/03 §4) ----
        E("confirm", "Enter", Key.Enter, ModifierKeys.None, ScopeContextual,
          "Confirm / select highlighted item", ShortcutStatus.Active, "docs/03 Enter"),
        E("cancel", "Esc", Key.Escape, ModifierKeys.None, ScopeShell,
          "Close dialog / cancel current edit", ShortcutStatus.Active, "docs/03 Esc"),
        E("remove-line", "Delete", Key.Delete, ModifierKeys.None, ScopeScreen,
          "Remove selected cart line", ShortcutStatus.Active, "docs/03 Delete"),
        E("qty-up", "+", Key.Add, ModifierKeys.None, ScopeScreen,
          "Increase selected cart quantity (stepper or +)", ShortcutStatus.Active, "docs/03 +"),
        E("qty-down", "-", Key.Subtract, ModifierKeys.None, ScopeScreen,
          "Decrease selected cart quantity (stepper or -)", ShortcutStatus.Active, "docs/03 -"),

        // ---- Ctrl chords (docs/03 §4) ----
        E("new-sale", "Ctrl+N", Key.N, ModifierKeys.Control, ScopeScreen,
          "New sale", ShortcutStatus.Future, "docs/03 Ctrl+N"),
        E("print", "Ctrl+P", Key.P, ModifierKeys.Control, ScopeScreen,
          "Print/reprint last receipt", ShortcutStatus.Active, "docs/03 Ctrl+P (BG-007)"),
        E("save-draft", "Ctrl+S", Key.S, ModifierKeys.Control, ScopeScreen,
          "Save draft", ShortcutStatus.Active, "docs/03 Ctrl+S"),
        E("focus-search", "Ctrl+F", Key.F, ModifierKeys.Control, ScopeScreen,
          "Focus search", ShortcutStatus.Active, "docs/03 Ctrl+F"),
        E("reset", "Ctrl+R", Key.R, ModifierKeys.Control, ScopeScreen,
          "Return to current screen's default state", ShortcutStatus.Future, "docs/03 Ctrl+R"),

        // ---- Alt navigation (docs/03 §4) ----
        E("nav-home", "Alt+1", Key.D1, ModifierKeys.Alt, ScopeShell,
          "Home", ShortcutStatus.Active, "docs/03 Alt+1"),
        E("nav-sale", "Alt+2", Key.D2, ModifierKeys.Alt, ScopeShell,
          "New sale", ShortcutStatus.Active, "docs/03 Alt+2"),
        E("nav-stock", "Alt+3", Key.D3, ModifierKeys.Alt, ScopeShell,
          "Stock", ShortcutStatus.Active, "docs/03 Alt+3"),
        E("nav-transactions", "Alt+4", Key.D4, ModifierKeys.Alt, ScopeShell,
          "Transactions", ShortcutStatus.Active, "docs/03 Alt+4"),
        E("nav-reconcile", "Alt+5", Key.D5, ModifierKeys.Alt, ScopeShell,
          "Close/reconcile", ShortcutStatus.Active, "docs/03 Alt+5"),
        E("nav-admin-users", "Alt+6", Key.D6, ModifierKeys.Alt, ScopeShell,
          "Open user management (admin)", ShortcutStatus.Active, "docs/03 Alt navigation"),
        E("nav-admin-import", "Alt+7", Key.D7, ModifierKeys.Alt, ScopeShell,
          "Open catalog import (admin)", ShortcutStatus.Active, "docs/03 Alt navigation"),
        E("nav-admin-overview", "Alt+8", Key.D8, ModifierKeys.Alt, ScopeShell,
          "Open admin overview (admin)", ShortcutStatus.Active, "docs/03 Alt navigation"),

        // ---- Admin chords (docs/03 §4) ----
        E("lock", "Ctrl+Alt+L", Key.L, ModifierKeys.Control | ModifierKeys.Alt, ScopeShell,
          "Lock screen", ShortcutStatus.Reserved, "docs/03 Ctrl+Alt+L"),
        E("backup", "Ctrl+Alt+B", Key.B, ModifierKeys.Control | ModifierKeys.Alt, ScopeShell,
          "Create backup (admin)", ShortcutStatus.Reserved, "docs/03 Ctrl+Alt+B"),
        E("hardware-test", "Ctrl+Alt+T", Key.T, ModifierKeys.Control | ModifierKeys.Alt, ScopeShell,
          "Open printer/scanner diagnostics (admin)", ShortcutStatus.Active, "docs/03 Ctrl+Alt+T (BG-007)"),
    ];
}
