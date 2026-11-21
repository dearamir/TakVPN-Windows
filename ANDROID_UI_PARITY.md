# Android UI parity work for Windows

## Reference and target
- Reference: `dearamir/Takvpn` Android app (`MainActivity.kt` and supporting Kotlin/resources).
- Target: `dearamir/TakVPN-Windows` WPF desktop app.
- Work branch: `android-ui-parity`.
- Keep the Windows VPN engine, Xray/Wintun lifecycle, profile storage, and update mechanism intact while porting the Android user experience.

## Initial repository review
- Android uses a single main activity with custom-built UI, themed palettes, text-color choices, profile/configuration actions, connection status, and additional dialogs/sheets.
- Windows currently has a desktop-specific layout: header, connection status panel, configuration/profile list, configuration details/traffic/log panel, footer/update controls, plus draggable/resizable panels and toolbar ordering.

## Parity goals
1. Rebuild the Windows presentation around the Android app's visual hierarchy, palette, typography, cards, connection status, and action layout.
2. Port Android user-facing features and configuration dialogs where desktop equivalents are appropriate, retaining all Windows-only controls needed for reliable VPN operation and updates.
3. Preserve functional behavior while porting: profile add/edit/remove, QR/import flows where supported, latency tests, profile sorting, traffic counters, logs, theme and text-color customization, language, and connection lifecycle.
4. Keep desktop adaptations (keyboard/mouse, window sizing, multi-column layout) subtle; the UI should feel like the Android app expanded to desktop rather than a separate design.
5. Build and test the Windows app after each coherent change. Do not publish a release until build and smoke tests pass.

## Verified feature inventory and mapping (initial pass)\n\nAndroid source exposes theme palette and text-color preview dialogs, language translations, profile import/add/edit/delete, QR scanning and QR display/share, subscription management and refresh, DNS configuration, smart-connect/fastest-profile selection, ping-one/ping-all and sorting, connection testing, traffic tracking/reset, event logs with copy/clear, app/core update checks and install, appearance toggles, and connection status/animation.\n\nWindows XAML currently has profile add/edit/remove, per-profile and all-profile ping, ping sorting, Xray/Wintun validation, profile details, traffic counters, live logs, layout customization, and app updater. Initial gaps to investigate/port: Android palette and text-color customization, language, subscription management, QR scan/display/share, DNS settings, smart-connect/fastest selection, usage reset, Android-style action sheets and preview dialogs. This list is a source-based inventory; implementation and parity validation are still pending.\n\n## Status
- [x] Confirmed access to both repositories.
- [x] Created isolated branch `android-ui-parity` in Windows repository.
- [x] Complete initial feature inventory from Android source (verified `MainActivity.kt` functions).
- [x] Map current Windows panels and controls against Android feature inventory (initial mapping recorded below).
- [ ] Implement UI parity in small commits.
- [ ] Build, launch, and smoke-test.
- [ ] Review and release only after verification.
