# Build profiles

A profile describes a supported release line and architecture, authenticated package sources and actual installed baselines. It is not a weekly hand-written list of package versions. Relative input paths resolve beside the profile, not beside the executable.

The checked-in example contains **placeholders**, not a production release policy. For the official Resolute/amd64 sources and their reviewed migration, see [release builds](release-builds.md). Both profiles need externally captured, genuine baseline files and trusted keys before they can run. Do not turn an illustrative removal or migration into a general permission.

`build --inputs /path/to/inputs` resolves relative baseline and source-keyring paths against that directory. Without it, those paths continue to resolve beside the profile. An optional `preferences_file` always resolves beside the profile, so a checked-in APT policy can stay with its release definition while large baseline files and keys live outside the checkout.

## Baselines

Capture `/var/lib/dpkg/status` and `/usr/lib/os-release` from each supported installed image or VM while no package transaction is active. Keep the complete native status database, including dependency and hold information. A TSV of names and versions alone cannot replace it. Baseline names must be unique tokens.

Use the original release and relevant intermediate installation variants. Add cases for supported system-layout migrations. A successfully generated bundle proves closure only for the baselines supplied; it does not prove every possible third-party package combination works.

## Sources and migration stages

Each source has `uri`, `suites`, `components` and an external `keyring`. A flat repository uses `suites: ["./"]` and no components. Standard signed repositories can use a suite and component list. HTTPS, HTTP and local `file` sources require authenticated APT metadata; unauthenticated repositories are not enabled. Source URLs must not contain embedded credentials.

APT indexes are fetched once per build and captured with their hashes. Exact package versions and hashes come from those authenticated indexes. A mirror changing or removing a DEB during the build causes failure, not substitution. For a reproducible release, supply a retained repository snapshot instead of relying on a moving live mirror.

An optional `preferences_file` supplies a reviewed native APT preferences file to the isolated resolver. It is copied into the captured inputs along with the profile; the host's policy is never imported. The official profile uses AnduinOS's source priority so an upstream Ubuntu package with a higher version cannot replace the AnduinOS version package. Even when a preference allows APT to propose a downgrade, the builder still refuses it. Preferences select input packages; they are not installed on an end user's system.

`stages` contains bounded, structured operations. `install` names packages; the final operation must be `dist-upgrade`. No shell commands or archive-provided scripts are supported. A migration that must install new meta packages before the full upgrade can use:

```json
[
  { "operation": "install", "packages": ["anduinos-core-system", "plymouth-anduinos"] },
  { "operation": "dist-upgrade", "packages": [] }
]
```

This is a migration example, not a universal release rule. `allowed_removals` must name only reviewed obsolete packages for that migration. Everything else is refused. Each stage advances a virtual package database from actual package control metadata; it does not run maintainer scripts.

The target version is read from `version_file` inside the authenticated candidate for `version_package`. For AnduinOS this is normally `usr/lib/os-release` in `base-files`. The version must belong to the selected AnduinOS line and Ubuntu suite.

## Signing and outputs

Provide a controlled GnuPG home and signing-key identifier to `build`. Private keys are not copied into profiles, reports or bundles. Test keys are suitable only for development; the test suite generates its own temporary identities. `--keyring` supplies independently trusted public signing keys.

A successful build produces:

- `AnduinOS-<version>-<architecture>-<batch-prefix>.atgz`: signed immutable archive.
- The adjacent `.build.json`: package inventory, baseline plans, source index hashes, tool versions and validation scope.
- The adjacent `.inputs/`: captured native baseline files, authenticated source indexes, source public keys and a profile with relative input paths.

Keep the sidecars in build evidence storage. They are not needed by an end user and may describe the build environment's installed packages. Original source URLs can cease to serve the captured versions; a saved profile alone is not a retained mirror. The archive itself contains the exact selected DEBs and a signed local repository.

Do not run simultaneous builds into one output directory. If a process is interrupted before the final archive is moved into place, review any orphaned sidecars before retrying; completed archives are never replaced. Temporary working directories are cleaned on ordinary failure or cancellation.

Generated archives have no arbitrary age cutoff. Verification still depends on independently maintained trust: an expired/revoked signing key is refused. Signature validity alone does not make an old package appropriate for a newer system.
