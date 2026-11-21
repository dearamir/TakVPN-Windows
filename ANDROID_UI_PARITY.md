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

## Status
- [x] Confirmed access to both repositories.
- [x] Created isolated branch `android-ui-parity` in Windows repository.
- [ ] Complete screen/feature inventory from Android source.
- [ ] Map Android screens and components to Windows XAML.
- [ ] Implement UI parity in small commits.
- [ ] Build, launch, and smoke-test.
- [ ] Review and release only after verification.
