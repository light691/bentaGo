namespace BentaGo.App;

/// <summary>
/// Every design-system resource key as a compile-time constant. The STA smoke
/// test drives exclusively from <see cref="All"/>, so renaming a token is a
/// compile + test event (bg-004-synthesis §4). Naming rules: brushes end
/// "...Brush", spacing keys are "Thickness.*", font sizes are "FontSize.*",
/// keyed styles carry a ".Kind" suffix.
/// </summary>
public static class DesignTokens
{
    // ---- Surfaces ----
    public const string SurfacePageBrush = "Brush.Surface.Page";
    public const string SurfaceCardBrush = "Brush.Surface.Card";
    public const string SurfaceSunkenBrush = "Brush.Surface.Sunken";
    public const string SurfaceSelectedBrush = "Brush.Surface.Selected";

    // ---- Text ----
    public const string TextPrimaryBrush = "Brush.Text.Primary";
    public const string TextSecondaryBrush = "Brush.Text.Secondary";
    public const string TextDisabledBrush = "Brush.Text.Disabled";
    public const string TextOnAccentBrush = "Brush.Text.OnAccent";
    public const string TextOnBrandBrush = "Brush.Text.OnBrand";

    // ---- Brand / accent ----
    public const string AccentBrush = "Brush.Accent";
    public const string AccentHoverBrush = "Brush.Accent.Hover";
    public const string AccentPressedBrush = "Brush.Accent.Pressed";
    public const string BrandBrush = "Brush.Brand";

    // ---- Borders ----
    public const string BorderDefaultBrush = "Brush.Border.Default";
    public const string BorderStrongBrush = "Brush.Border.Strong";
    public const string DividerBrush = "Brush.Divider";

    // ---- Status ----
    public const string StatusSuccessBrush = "Brush.Status.Success";
    public const string StatusWarningBrush = "Brush.Status.Warning";
    public const string StatusErrorBrush = "Brush.Status.Error";
    public const string StatusInfoBrush = "Brush.Status.Info";
    public const string StatusSuccessBgBrush = "Brush.Status.Success.Bg";
    public const string StatusWarningBgBrush = "Brush.Status.Warning.Bg";
    public const string StatusErrorBgBrush = "Brush.Status.Error.Bg";
    public const string StatusInfoBgBrush = "Brush.Status.Info.Bg";

    // ---- Focus / disabled ----
    public const string FocusBrush = "Brush.Focus";
    public const string StateDisabledBgBrush = "Brush.State.Disabled.Bg";
    public const string StateDisabledFgBrush = "Brush.State.Disabled.Fg";
    public const string StateDisabledBorderBrush = "Brush.State.Disabled.Border";

    // ---- Brand ramp (round-4: pixel-sampled from
    // designs/bentago-wpf-mvp-screen-set.png) ----
    public const string BrandRailBrush = "Brush.Brand.Rail";
    public const string BrandRailHoverBrush = "Brush.Brand.Rail.Hover";
    public const string BrandRailSelectedBrush = "Brush.Brand.Rail.Selected";
    public const string BrandRailBorderBrush = "Brush.Brand.Rail.Border";
    public const string BrandRailKeycapBgBrush = "Brush.Brand.Rail.KeycapBg";
    public const string BrandRailKeycapTextBrush = "Brush.Brand.Rail.KeycapText";
    public const string BrandSecondaryBrush = "Brush.Brand.Secondary";

    // ---- Payment method accents ----
    public const string PayCashBrush = "Brush.Pay.Cash";
    public const string PayGcashBrush = "Brush.Pay.Gcash";
    public const string PayMayaBrush = "Brush.Pay.Maya";
    public const string PayBankBrush = "Brush.Pay.Bank";
    public const string PayCreditBrush = "Brush.Pay.Credit";

    // ---- Type ramp ----
    public const string FontFamilyPrimary = "Font.Family.Primary";
    public const string FontFamilyIconGlyphs = "Font.Family.IconGlyphs";
    public const string FontSizeDisplay = "FontSize.Display";
    public const string FontSizeHeader = "FontSize.Header";
    public const string FontSizeTitle = "FontSize.Title";
    public const string FontSizeBody = "FontSize.Body";
    public const string FontSizeLabel = "FontSize.Label";
    public const string FontSizeStatus = "FontSize.Status";
    public const string FontSizeCaption = "FontSize.Caption";
    public const string FontWeightStrong = "FontWeight.Strong";
    public const string FontWeightSemi = "FontWeight.Semi";

    // ---- Named text styles ----
    public const string DisplayTextStyle = "DisplayTextStyle";
    public const string HeaderTextStyle = "HeaderTextStyle";
    public const string TitleTextStyle = "TitleTextStyle";
    public const string BodyTextStyle = "BodyTextStyle";
    public const string LabelTextStyle = "LabelTextStyle";
    public const string StatusTextStyle = "StatusTextStyle";
    public const string CaptionTextStyle = "CaptionTextStyle";
    public const string MoneyTextStyle = "MoneyTextStyle";

    // ---- Spacing / metrics / corners ----
    public const string ThicknessXs = "Thickness.XS";
    public const string ThicknessS = "Thickness.S";
    public const string ThicknessM = "Thickness.M";
    public const string ThicknessL = "Thickness.L";
    public const string ThicknessXl = "Thickness.XL";
    public const string ThicknessXxl = "Thickness.XXL";
    public const string ScreenPadding = "ScreenPadding";
    public const string CardPadding = "CardPadding";
    public const string CornerS = "Corner.S";
    public const string CornerM = "Corner.M";
    public const string CornerL = "Corner.L";
    public const string CornerButton = "Corner.Button";
    public const string ControlHeightDefault = "Control.Height.Default";
    public const string ControlHeightLarge = "Control.Height.Large";
    public const string DialogHostMaxWidth = "DialogHost.MaxWidth";
    public const string DialogHostMaxHeight = "DialogHost.MaxHeight";

    // ---- Shared templates / styles ----
    public const string FocusVisualDefault = "FocusVisual.Default";
    public const string ButtonPrimaryStyle = "Button.PrimaryStyle";
    public const string ButtonSecondaryStyle = "Button.SecondaryStyle";
    public const string ButtonDangerStyle = "Button.DangerStyle";
    public const string ButtonQuietStyle = "Button.QuietStyle";
    public const string ButtonKeypadStyle = "Button.KeypadStyle";
    public const string KeypadButtonAccentStyle = "KeypadButton.AccentStyle";
    public const string CardStyle = "CardStyle";
    public const string BannerSuccessStyle = "Banner.SuccessStyle";
    public const string BannerWarningStyle = "Banner.WarningStyle";
    public const string BannerErrorStyle = "Banner.ErrorStyle";
    public const string BannerInfoBgStyle = "Banner.InfoBgStyle";
    public const string DataGridLowStockRowStyle = "DataGrid.LowStockRowStyle";
    public const string DataGridRowStyle = "DataGrid.RowStyle";
    public const string NavRailItemStyle = "NavRailItemStyle";
    public const string NavRailTextStyle = "NavRailTextStyle";
    public const string NavRailKeyCapStyle = "NavRailKeyCapStyle";
    public const string NavRailKeyCapTextStyle = "NavRailKeyCapTextStyle";
    public const string QuickActionChipStyle = "QuickActionChipStyle";
    public const string QuickActionTextStyle = "QuickActionTextStyle";
    public const string QuickActionKeyCapTextStyle = "QuickActionKeyCapTextStyle";
    public const string ButtonActionBarStyle = "Button.ActionBarStyle";
    public const string ButtonActionBarOutlineStyle = "Button.ActionBarOutlineStyle";
    public const string ButtonActionBarDangerOutlineStyle = "Button.ActionBarDangerOutlineStyle";
    public const string ActionBarGridStyle = "ActionBarGridStyle";
    public const string FieldEditorTallStyle = "FieldEditorTallStyle";
    public const string KpiCardStyle = "KpiCardStyle";
    public const string StatusStripDotStyle = "StatusStripDotStyle";
    public const string MoneyPositiveTextStyle = "MoneyPositiveTextStyle";
    public const string StatusSuccessTitleTextStyle = "StatusSuccessTitleTextStyle";
    public const string StatusBarLeadTextStyle = "StatusBarLeadTextStyle";
    public const string MethodTabButtonStyle = "MethodTabButtonStyle";
    public const string PaymentMethodListButtonStyle = "PaymentMethodListButtonStyle";
    public const string ScanEditorStyle = "ScanEditorStyle";
    public const string FieldLabelStyle = "FieldLabelStyle";
    public const string DialogWindowStyle = "DialogWindowStyle";
    public const string HelpOverlayStyle = "HelpOverlayStyle";
    public const string StatusDotStyle = "StatusDotStyle";
    public const string KeyCapStyle = "KeyCapStyle";
    public const string PlaceholderStyle = "PlaceholderStyle";
    public const string PlaceholderTextStyle = "PlaceholderTextStyle";
    public const string StatusGlyphTextStyle = "StatusGlyphTextStyle";
    public const string LowStockSkuTextStyle = "LowStockSkuTextStyle";
    public const string TagWarningStyle = "Tag.WarningStyle";
    public const string StatusWarningTextStyle = "StatusWarningTextStyle";

    // ---- Validation (Styles/Validation.xaml) ----
    public const string ErrorTemplateTextBox = "ErrorTemplate.TextBox";
    public const string ErrorTemplatePasswordBox = "ErrorTemplate.PasswordBox";
    public const string ErrorTextStyle = "ErrorTextStyle";

    /// <summary>
    /// The full token surface. The STA smoke test resolves every key against
    /// the merged application resources; adding a token means extending this
    /// list, and removing or renaming one breaks compile + test together.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        SurfacePageBrush, SurfaceCardBrush, SurfaceSunkenBrush, SurfaceSelectedBrush,
        TextPrimaryBrush, TextSecondaryBrush, TextDisabledBrush, TextOnAccentBrush, TextOnBrandBrush,
        AccentBrush, AccentHoverBrush, AccentPressedBrush, BrandBrush,
        BorderDefaultBrush, BorderStrongBrush, DividerBrush,
        StatusSuccessBrush, StatusWarningBrush, StatusErrorBrush, StatusInfoBrush,
        StatusSuccessBgBrush, StatusWarningBgBrush, StatusErrorBgBrush, StatusInfoBgBrush,
        FocusBrush,
        StateDisabledBgBrush, StateDisabledFgBrush, StateDisabledBorderBrush,
        BrandRailBrush, BrandRailHoverBrush, BrandRailSelectedBrush, BrandRailBorderBrush,
        BrandRailKeycapBgBrush, BrandRailKeycapTextBrush, BrandSecondaryBrush,
        PayCashBrush, PayGcashBrush, PayMayaBrush, PayBankBrush, PayCreditBrush,
        FontFamilyPrimary, FontFamilyIconGlyphs,
        FontSizeDisplay, FontSizeHeader, FontSizeTitle, FontSizeBody, FontSizeLabel, FontSizeStatus, FontSizeCaption,
        FontWeightStrong, FontWeightSemi,
        DisplayTextStyle, HeaderTextStyle, TitleTextStyle, BodyTextStyle, LabelTextStyle, StatusTextStyle, CaptionTextStyle, MoneyTextStyle,
        ThicknessXs, ThicknessS, ThicknessM, ThicknessL, ThicknessXl, ThicknessXxl,
        ScreenPadding, CardPadding,
        CornerS, CornerM, CornerL, CornerButton,
        ControlHeightDefault, ControlHeightLarge,
        DialogHostMaxWidth, DialogHostMaxHeight,
        FocusVisualDefault,
        ButtonPrimaryStyle, ButtonSecondaryStyle, ButtonDangerStyle, ButtonQuietStyle,
        ButtonKeypadStyle, KeypadButtonAccentStyle,
        CardStyle, BannerSuccessStyle, BannerWarningStyle, BannerErrorStyle, BannerInfoBgStyle,
        DataGridLowStockRowStyle, DataGridRowStyle, NavRailItemStyle, NavRailTextStyle, NavRailKeyCapStyle, NavRailKeyCapTextStyle,
        QuickActionChipStyle, QuickActionTextStyle, QuickActionKeyCapTextStyle,
        ButtonActionBarStyle, ButtonActionBarOutlineStyle, ButtonActionBarDangerOutlineStyle, ActionBarGridStyle, FieldEditorTallStyle, KpiCardStyle,
        StatusStripDotStyle,
        MoneyPositiveTextStyle, StatusSuccessTitleTextStyle, StatusBarLeadTextStyle, MethodTabButtonStyle,
        ScanEditorStyle, FieldLabelStyle, DialogWindowStyle, HelpOverlayStyle,
        StatusDotStyle, PaymentMethodListButtonStyle, KeyCapStyle, PlaceholderStyle, PlaceholderTextStyle, StatusGlyphTextStyle, LowStockSkuTextStyle,
        TagWarningStyle, StatusWarningTextStyle,
        ErrorTemplateTextBox, ErrorTemplatePasswordBox, ErrorTextStyle,
    };
}