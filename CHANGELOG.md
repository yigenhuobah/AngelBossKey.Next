# Changelog

All notable changes to AngelBossKey Next are documented in this file.

## [Unreleased]

### Changed

- Independent-desktop entry no longer preemptively rejects full-screen or
  exclusive-mode foreground windows; it attempts the Windows desktop switch
  and reports or falls back only when the actual operation fails.

## [0.9.0-preview.1]

### Changed

- Centralized the application version and portable output directory metadata.
- Added a manually triggered unsigned-release preparation workflow with a
  candidate checksum and release-notes draft.
- Added a non-disruptive Windows release validation guide.

### Added

- First unsigned public Preview release channel with portable ZIP and SHA-256
  verification material.

## [0.8.0]

### Added

- Initial public repository release with scene-based window hiding, audio
  muting, automation, on-demand elevation, and independent privacy desktops.
