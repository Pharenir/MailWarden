# AP-04d test plan: reading pane follows the theme

Source of truth: `work/AP-04d/requirement.md` (D1-D8, cases 1-12, decisions after the first build). Case 13 is manual (Dennis).
Feature tags: `feature:ui` (all), `feature:sync` (Infrastructure Mime tests, as the other Mime tests).

## Files
| File | Content |
|---|---|
| `tests/MailWarden.Infrastructure.Tests/Mime/ReadingDocumentThemeTests.cs` | Builder and sanitizer verdict: cases 1-9, 12, D5 byte identity, `PageBackground` |
| `tests/MailWarden.Presentation.Tests/ReadingPaneThemeTests.cs` | View model: cases 10, 11 and the races |
| `tests/MailWarden.Presentation.Tests/TestSupport/ShellHarness.cs`, `ReadingPaneBuildGuardTests.cs` | Adapted to the new `documentBuilder` signature (extra `ReadingTheme` parameter); no behaviour change |

## Coverage
| Case | Tests |
|---|---|
| 1 plain, dark | Plain_text_in_the_dark_theme_gets_the_dark_style_and_the_text_stays_encoded (Build and BuildFromPlainText) |
| 2 plain, light / D5 | Plain_text_in_the_light_theme_is_byte_identical_to_the_call_without_a_theme, Plain_text_differs_between_the_themes |
| 3 HTML without colours, dark | Html_without_colours_in_the_dark_theme_gets_the_dark_style, Layout_only_style_declarations_..., Same_shapes_without_the_trigger_... |
| 4 colour, dark | Html_with_a_colour_declaration_stays_on_the_light_sheet_in_the_dark_theme |
| 5 each trigger | Each_trigger_alone_keeps_the_light_sheet_in_the_dark_theme (12 shapes), Blocked_or_removed_resources_count_as_own_colours_with_and_without_approval, Approving_remote_images_does_not_change_the_page_style |
| 5a `<style>` | A_style_element_is_no_trigger_because_the_sanitizer_removes_it |
| 6 words in text | Colour_words_as_visible_text_are_no_trigger, Title_attribute_and_html_comment_holding_a_colour_are_no_trigger |
| 7 case/space/escape | Upper_case_and_spaces_do_not_hide_a_colour, A_css_escape_in_the_property_name_counts_as_a_colour_declaration |
| 8 colours, light | Html_with_own_colours_gives_the_same_document_in_both_themes, Html_without_colours_in_the_light_theme_is_byte_identical_... |
| 9 CSP | The_csp_and_the_image_rule_are_the_same_in_both_themes (4 inputs x remote x embedded), Remote_images_stay_blocked_without_approval_in_the_dark_theme |
| 10 theme change, open mail | Theme_change_with_an_open_mail_rebuilds_from_the_held_content_without_reading_again, Plain_text_mail_follows_the_theme, Next_mail_after_a_theme_change_..., Theme_change_keeps_the_selection_in_the_list, Theme_change_keeps_the_approval_of_remote_images, Theme_change_without_approval_still_blocks_remote_images, Mail_with_own_colours_gives_the_same_html_..., Mail_without_colours_announces_the_new_document, Reporting_the_same_theme_twice_changes_nothing |
| 11 no open mail | Theme_change_without_an_open_mail_does_nothing_and_does_not_fail, ..._after_the_mail_was_closed_..., ..._with_a_not_downloaded_mail_..., Failing_builder_during_a_theme_change_does_not_throw |
| 12 hostile | A_style_element_trying_to_reach_the_page_style_..., A_hostile_style_attribute_keeps_the_light_document_unchanged_..., The_sanitized_body_is_the_same_in_both_themes_... |
| Verdict without parse result | Mail_ending_in_the_text_only_path_of_the_sanitizer_has_own_colours (20 000 `style` attributes, no colour: only text is left, still own colours), Record_default_of_the_verdict_errs_towards_light |
| Races | Theme_change_during_show_..., Two_theme_changes_during_show_..., Theme_change_during_load_images_..., Theme_change_during_a_running_theme_rebuild_..., Dark_light_dark_during_one_running_rebuild_..., Late_theme_rebuild_of_an_old_mail_... |
| Junk / fallback | Junk_mail_keeps_no_images_in_every_theme, Inbox_mail_builds_with_embedded_images_..., Theme_change_in_the_plain_text_fallback_builds_nothing_and_keeps_the_notice, Theme_change_keeps_the_notice_of_a_reduced_document |
| 13 | manual (Dennis) |

## Not covered
- Case 13 (look of both themes, Design System at run time, no white flash D6 in WebView2): manual. Only the colour value `PageBackground` is tested.
- `MainWindow` wiring (D8, code-behind): no WinUI in tests.
- Scroll position after a theme change (accepted in the requirement).
- Undefined `ReadingTheme` values: not specified.
