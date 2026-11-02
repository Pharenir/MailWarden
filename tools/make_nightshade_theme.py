"""Generates src\\MailWarden.App\\Themes\\NightshadeTheme.xaml from the role table below and the generic.xaml of WinUI.

Why a generator: generic.xaml aliases the control keys to its base brushes with StaticResource, and such an alias is resolved
inside its own dictionary. Overriding only the base brushes changes nothing visible, so the dictionary has to repeat every
alias of the controls in use. The list of aliases comes from generic.xaml, so a Windows App SDK update can regenerate it.

Run by hand after such an update; the build never runs it, and the generated file is committed.

Usage: python tools\\make_nightshade_theme.py [path of generic.xaml]
"""

import os
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

# Version of the package Microsoft.WindowsAppSDK.WinUI that Microsoft.WindowsAppSDK of the app project brings along.
WINUI_PACKAGE_VERSION = "1.8.260803003"
DEFAULT_GENERIC_PATH = (
    Path(os.environ.get("USERPROFILE", ""))
    / ".nuget" / "packages" / "microsoft.windowsappsdk.winui" / WINUI_PACKAGE_VERSION
    / "lib" / "net6.0-windows10.0.17763.0" / "Microsoft.WinUI" / "Themes" / "generic.xaml"
)
OUTPUT_PATH = Path(__file__).resolve().parent.parent / "src" / "MailWarden.App" / "Themes" / "NightshadeTheme.xaml"

PRESENTATION_NAMESPACE = "http://schemas.microsoft.com/winfx/2006/xaml/presentation"
XAML_NAMESPACE = "http://schemas.microsoft.com/winfx/2006/xaml"
XAML_KEY = f"{{{XAML_NAMESPACE}}}Key"

# The dictionary of generic.xaml that holds the dark values, and the one key our palette answers to.
GENERIC_DARK_DICTIONARY_KEY = "Default"
PALETTE_THEME_KEY = "Dark"

# Palette of the Nightshade project: Nightshade.Core\themes\nightshade.json.
WINDOW = "#1A1626"
SURFACE = "#231E33"
SURFACE_HOVER = "#30294A"
BORDER = "#3E355C"
TEXT = "#ECE8F7"
TEXT_MUTED = "#A99FC6"
ACCENT = "#FF8A3D"
ACCENT_HOVER = "#FFA060"
ACCENT_TEXT = "#1A1626"
INPUT = "#17131F"
INPUT_BORDER = "#463C66"
SELECTION = "#4A3B7A"
SELECTION_TEXT = "#FFFFFF"
ERROR = "#FF8A8A"
WARNING = "#FFD166"
BUTTON = "#2E2744"
BUTTON_PRESSED = "#2A2340"

# Shades the palette has no role for: states of the WinUI controls that must stay apart from the roles above.
TRANSPARENT = "#00000000"
THUMB = "#5D5480"
DISABLED_FILL = "#1F1A2D"
ACCENT_PRESSED = "#E67A33"
ACCENT_DISABLED = "#663E355C"
TEXT_DISABLED = "#99A99FC6"
BUTTON_HOVER = "#3E355C"
ACCENT_TEXT_SECONDARY = "#CC1A1626"
ACCENT_TEXT_DISABLED = "#991A1626"
WARNING_BACKGROUND = "#3A2F2A"
ERROR_BACKGROUND = "#3F2530"
DIALOG_SMOKE = "#99000000"

# Accent colors: a few templates ask for them directly. Keeps the accent of Windows out of the palette.
ACCENT_COLORS = {
    "SystemAccentColor": ACCENT,
    "SystemAccentColorLight1": ACCENT_HOVER,
    "SystemAccentColorLight2": ACCENT,
    "SystemAccentColorLight3": ACCENT_HOVER,
    "SystemAccentColorDark1": ACCENT_PRESSED,
    "SystemAccentColorDark2": "#C7662A",
    "SystemAccentColorDark3": "#A85421",
}

# Base brushes of WinUI; the aliases below point to them. A key whose original is an AcrylicBrush or a LinearGradientBrush can
# take a SolidColorBrush: every use of these keys is a {ThemeResource} on a property of type Brush.
BASE_BRUSHES = {
    # Our own key: the window background, and with it the title bar. WinUI has no key for it.
    "NightshadeWindowBackgroundBrush": WINDOW,
    "TextFillColorPrimaryBrush": TEXT,
    "TextFillColorSecondaryBrush": TEXT_MUTED,
    "TextFillColorTertiaryBrush": TEXT_MUTED,
    "TextFillColorDisabledBrush": TEXT_DISABLED,
    "TextFillColorInverseBrush": WINDOW,
    "TextOnAccentFillColorPrimaryBrush": ACCENT_TEXT,
    "TextOnAccentFillColorSecondaryBrush": ACCENT_TEXT_SECONDARY,
    "TextOnAccentFillColorDisabledBrush": ACCENT_TEXT_DISABLED,
    "ControlFillColorDefaultBrush": BUTTON,
    "ControlFillColorSecondaryBrush": BUTTON_HOVER,
    "ControlFillColorTertiaryBrush": BUTTON_PRESSED,
    "ControlFillColorDisabledBrush": DISABLED_FILL,
    "ControlFillColorInputActiveBrush": INPUT,
    "ControlFillColorTransparentBrush": TRANSPARENT,
    "ControlStrongFillColorDefaultBrush": THUMB,
    "ControlStrongFillColorDisabledBrush": BORDER,
    "ControlAltFillColorSecondaryBrush": INPUT,
    "ControlAltFillColorQuarternaryBrush": SURFACE_HOVER,
    "SubtleFillColorSecondaryBrush": SURFACE_HOVER,
    "SubtleFillColorTertiaryBrush": BUTTON_PRESSED,
    "SubtleFillColorDisabledBrush": TRANSPARENT,
    "ControlStrokeColorDefaultBrush": BORDER,
    "ControlStrongStrokeColorDefaultBrush": INPUT_BORDER,
    "ControlStrongStrokeColorDisabledBrush": BORDER,
    "ControlElevationBorderBrush": BORDER,
    "TextControlElevationBorderBrush": INPUT_BORDER,
    # The original draws the accent of Windows under a focused input.
    "TextControlElevationBorderFocusedBrush": ACCENT,
    "CardStrokeColorDefaultBrush": BORDER,
    "SurfaceStrokeColorFlyoutBrush": BORDER,
    "SurfaceStrokeColorDefaultBrush": BORDER,
    "DividerStrokeColorDefaultBrush": BORDER,
    "AccentFillColorDefaultBrush": ACCENT,
    "AccentFillColorSecondaryBrush": ACCENT_HOVER,
    "AccentFillColorTertiaryBrush": ACCENT_PRESSED,
    "AccentFillColorDisabledBrush": ACCENT_DISABLED,
    "AccentTextFillColorPrimaryBrush": ACCENT,
    "AccentFillColorSelectedTextBackgroundBrush": SELECTION,
    "AcrylicInAppFillColorDefaultBrush": SURFACE,
    "AcrylicBackgroundFillColorDefaultBrush": SURFACE,
    "SolidBackgroundFillColorBaseBrush": SURFACE,
    "LayerFillColorAltBrush": SURFACE,
    "SystemFillColorCautionBrush": WARNING,
    "SystemFillColorCriticalBrush": ERROR,
    "SystemFillColorCautionBackgroundBrush": WARNING_BACKGROUND,
    "SystemFillColorCriticalBackgroundBrush": ERROR_BACKGROUND,
    "SystemFillColorSuccessBackgroundBrush": SURFACE,
    "SystemFillColorAttentionBackgroundBrush": SURFACE,
    "SystemFillColorAttentionBrush": ACCENT,
    "SmokeFillColorDefaultBrush": DIALOG_SMOKE,
}

# Control keys whose color differs from what their alias in generic.xaml would give, and keys generic.xaml aliases to a base
# outside this table. They replace the alias.
DIRECT_BRUSHES = {
    "DefaultTextForegroundThemeBrush": TEXT,
    "ListViewItemBackgroundSelected": SELECTION,
    "ListViewItemBackgroundSelectedPointerOver": SELECTION,
    "ListViewItemBackgroundSelectedPressed": SELECTION,
    "ListViewItemForegroundSelected": SELECTION_TEXT,
    "ListViewItemForegroundSelectedPointerOver": SELECTION_TEXT,
    "ListViewItemForegroundSelectedPressed": SELECTION_TEXT,
    "ListViewItemSelectionIndicatorBrush": ACCENT,
    "TreeViewItemBackgroundSelected": SELECTION,
    "TreeViewItemBackgroundSelectedPointerOver": SELECTION,
    "TreeViewItemBackgroundSelectedPressed": SELECTION,
    "TreeViewItemForegroundSelected": SELECTION_TEXT,
    "TreeViewItemForegroundSelectedPointerOver": SELECTION_TEXT,
    "TreeViewItemForegroundSelectedPressed": SELECTION_TEXT,
    "TreeViewItemSelectionIndicatorForeground": ACCENT,
    "TextControlBackground": INPUT,
    "TextControlBackgroundFocused": INPUT,
    "TextControlBackgroundPointerOver": INPUT,
    "MenuFlyoutPresenterBackground": SURFACE,
    "ComboBoxDropDownBackground": SURFACE,
    "ToolTipBackground": SURFACE,
}

# The control families MailWarden uses; only their aliases are repeated.
ALIAS_KEY_PREFIXES = (
    "Button", "DropDownButton", "ComboBox", "TextControl", "AutoSuggestBox", "ToggleSwitch", "MenuFlyout", "ListViewItem",
    "TreeViewItem", "TreeView", "ScrollBar", "InfoBar", "ContentDialog", "Flyout", "ToolTip", "RepeatButton", "AccentButton",
)

# Keys that must end up in the file with this color (work\AP-04e\requirement.md, E11); a missing one stops the script.
REQUIRED_COLORS = {
    "TextFillColorPrimaryBrush": TEXT,
    "DefaultTextForegroundThemeBrush": TEXT,
    "TextFillColorSecondaryBrush": TEXT_MUTED,
    "ButtonBackground": BUTTON,
    "ComboBoxBackground": BUTTON,
    "TextControlBackground": INPUT,
    "MenuFlyoutPresenterBackground": SURFACE,
    "FlyoutPresenterBackground": SURFACE,
    "SystemFillColorCautionBrush": WARNING,
    "ListViewItemBackgroundSelected": SELECTION,
    "TreeViewItemBackgroundSelected": SELECTION,
    "ListViewItemForegroundSelected": SELECTION_TEXT,
    "AccentFillColorDefaultBrush": ACCENT,
    "TextOnAccentFillColorPrimaryBrush": ACCENT_TEXT,
    "ToggleSwitchFillOn": ACCENT,
    "ContentDialogBackground": SURFACE,
    "SolidBackgroundFillColorBaseBrush": SURFACE,
    "SystemFillColorCriticalBrush": ERROR,
}


def read_control_aliases(generic_path: Path) -> dict[str, str]:
    """Reads the aliases of the control families that point to a brush of the base table.

    Args:
        generic_path: Path of the generic.xaml of WinUI.

    Returns:
        Alias key to base key, in the order of generic.xaml. Keys of the other tables are left out.

    Raises:
        SystemExit: generic.xaml has no dictionary with the dark values.
    """
    theme_dictionaries = ElementTree.parse(generic_path).getroot().find(
        f"{{{PRESENTATION_NAMESPACE}}}ResourceDictionary.ThemeDictionaries")
    if theme_dictionaries is None:
        sys.exit(f"{generic_path} has no theme dictionaries.")

    dark_dictionary = next(
        (dictionary for dictionary in theme_dictionaries if dictionary.get(XAML_KEY) == GENERIC_DARK_DICTIONARY_KEY), None)
    if dark_dictionary is None:
        sys.exit(f"{generic_path} has no theme dictionary '{GENERIC_DARK_DICTIONARY_KEY}'.")

    aliases = {}
    for resource in dark_dictionary.findall(f"{{{PRESENTATION_NAMESPACE}}}StaticResource"):
        key = resource.get(XAML_KEY)
        target = resource.get("ResourceKey")
        if (key.startswith(ALIAS_KEY_PREFIXES) and target in BASE_BRUSHES
                and key not in DIRECT_BRUSHES and key not in BASE_BRUSHES):
            aliases[key] = target

    return aliases


def check_required_colors(aliases: dict[str, str]) -> None:
    """Stops the script when a required key is missing or has another color; generic.xaml may have changed.

    Args:
        aliases: The aliases that go into the file.

    Raises:
        SystemExit: A required key is missing or has another color.
    """
    colors = {**BASE_BRUSHES, **DIRECT_BRUSHES, **{key: BASE_BRUSHES[target] for key, target in aliases.items()}}
    problems = [key for key, color in REQUIRED_COLORS.items() if colors.get(key) != color]
    if problems:
        sys.exit("Missing or with another color, take them into the direct table: " + ", ".join(problems))


def render_dictionary(aliases: dict[str, str]) -> str:
    """Writes the XAML. The base brushes come before the aliases: a StaticResource only finds what is defined above it.

    Args:
        aliases: The aliases that go into the file.

    Returns:
        The text of the file, with line feeds.
    """
    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        "<!-- Generated by tools\\make_nightshade_theme.py from its role table and the generic.xaml of WinUI. Do not edit. -->",
        f'<ResourceDictionary xmlns="{PRESENTATION_NAMESPACE}" xmlns:x="{XAML_NAMESPACE}">',
        "  <ResourceDictionary.ThemeDictionaries>",
        f'    <ResourceDictionary x:Key="{PALETTE_THEME_KEY}">',
        "      <!-- Accent colors -->",
    ]
    lines += [f'      <Color x:Key="{key}">{color}</Color>' for key, color in ACCENT_COLORS.items()]
    lines.append("      <!-- Base brushes -->")
    lines += [f'      <SolidColorBrush x:Key="{key}" Color="{color}" />' for key, color in BASE_BRUSHES.items()]
    lines.append("      <!-- Control keys with a color of their own -->")
    lines += [f'      <SolidColorBrush x:Key="{key}" Color="{color}" />' for key, color in DIRECT_BRUSHES.items()]
    lines.append("      <!-- Aliases of the controls in use, as in generic.xaml -->")
    lines += [f'      <StaticResource x:Key="{key}" ResourceKey="{target}" />' for key, target in aliases.items()]
    lines += [
        "    </ResourceDictionary>",
        "  </ResourceDictionary.ThemeDictionaries>",
        "</ResourceDictionary>",
    ]
    return "\n".join(lines) + "\n"


def main() -> None:
    """Reads generic.xaml, checks the result and writes the dictionary as UTF-8 without BOM with CRLF line ends."""
    generic_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_GENERIC_PATH
    if not generic_path.is_file():
        sys.exit(f"generic.xaml not found: {generic_path}")

    aliases = read_control_aliases(generic_path)
    check_required_colors(aliases)

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8", newline="\r\n") as output_file:
        output_file.write(render_dictionary(aliases))

    print(f"{OUTPUT_PATH}: {len(ACCENT_COLORS)} colors, {len(BASE_BRUSHES)} base brushes, "
          f"{len(DIRECT_BRUSHES)} direct brushes, {len(aliases)} aliases")


if __name__ == "__main__":
    main()
