# Changelog
All notable changes to the com.unity.virtualmesh package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [0.2.0-preview] - 2025-12-12

### Added

- Added third party notice and updated license links
- Exposed simplification error target in baker

### Changed

- Changed StreamingAsset IO code to improve security
- Slightly refactored baker UI
- Moved settings flags from render feature to runtime component
- Updated target engine version to 6000.3.0f1

### Fixed

- Fixed Shader graph shader compilation errors caused by wrong include paths
- Fixed selected LOD for in-frustum shadow casters
- Fixed depth pyramid blit warning about unused invalid target
- Improved manager data allocation timing

## [0.1.0-preview] - 2025-10-07

- Initial Release
