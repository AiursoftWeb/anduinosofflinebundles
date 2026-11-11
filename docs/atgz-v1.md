# ATGZ format v1

The current contract is a gzip-compressed tar archive. The first two logical entries are `manifest.json` (UTF-8 JSON) and `manifest.asc` (OpenPGP detached signature over the exact JSON bytes). Subsequent entries are the files declared by the signed manifest. PAX regular-file entries are produced by the builder; links and special files are not accepted.

```text
manifest.json
manifest.asc
repository/Packages
repository/Release
repository/InRelease
repository/Release.gpg
repository/pool/<sha256>.deb
...
```

## Identity and capabilities

JSON fields use `snake_case`. `format_version` is `1`, `product` is `AnduinOS`. The manifest includes:

| Fields | Meaning |
| --- | --- |
| `release_line`, `suite`, `architecture`, `target_version` | Compatibility boundaries and the target OS version derived from package contents |
| `batch_id`, `created_utc` | Content/plan batch identity and creation time; the timestamp is not an expiry policy |
| `required_features` | Required reader capabilities; v1 requires `apt-repository-v1` and `sequential-apt-plans-v1` |
| `version_package`, `version_file` | Authoritative package and path for OS version metadata |
| `stages`, `allowed_removals` | Structured APT migration plan and its explicit removal exception list |
| `baselines` | Native status hashes, starting OS versions and resolved per-stage changes |
| `packages` | Name, Debian version, architecture, payload path, size and SHA-256 of each DEB |
| `required_installed_targets` | Minimum versions for applicable installed packages, preventing silent kept-back success |
| `source_indexes` | Hashes and sizes of authenticated source-index inputs |
| `files` | Complete relative payload inventory with sizes and SHA-256 hashes |

Batch identity hashes the release boundaries, target version, version-package rule, ordered migration stages, sorted removal list, package inventory and baseline plans. Repository timestamps and signature bytes are excluded. Same OS version plus changed package targets is a different batch. Readers must not use the OS version alone for repeat detection.

Unknown optional JSON properties can be ignored. Unknown format versions or required capabilities are rejected. Duplicate JSON properties and explicit nulls are rejected; optional fields should be omitted. Future required installer semantics need a new capability or format, not a silently ignored field.

## Verification

Verify the detached signature against an external public keyring before trusting manifest fields. Require every declared file exactly once; reject additional files, missing files, mismatched size/hash, absolute or traversing paths, duplicate paths, links and special entries. Stream payload bytes instead of loading entire bundles into memory.

Current limits are a 4 MiB manifest, a 256 KiB manifest signature, 100,000 payload entries and 64 GiB total unpacked payload. The Packages index is limited to 32 MiB and each other repository metadata file to 1 MiB. Deeper parser/hostile archive testing remains part of release qualification.

Check that the Packages index exactly matches the manifest's package inventory. Its hash and release boundaries must match Release. Both Release.gpg and InRelease must verify with the same signing fingerprint as the outer manifest; the clear-signed contents must equal Release. Expired and revoked trust material is rejected. Revocation information must reach the external trusted keyring; an offline verifier cannot discover new revocations by itself.

Verification checks the signed archive, not the safety of maintainer scripts or a completed installation. The archive may only become a user release after the applicable installation and reboot matrix passes.

## Read-only planning

`inspect` with a native status and matching os-release verifies/extracts the bundle, then invokes APT with only its local repository and an isolated copied status database. It simulates each stage and advances virtual package state from exact Debian control metadata. No user repository, network fallback, host APT hooks or arbitrary script is used.

Package comparison uses `dpkg --compare-versions`. Unapproved removals and downgrades fail. After resolution, applicable installed packages must reach the declared minimum targets and the version package must be installed. An empty plan is `already-installed` only if the OS version also matches. Unmet targets are `blocked`, even if APT exits successfully.

`additional_staging_bytes` describes unpacked bundle storage only. The installer must separately inspect filesystem capacity, package expansion, snapshots, local APT policy, concurrent transactions and required restarts. No snapshot support is a valid future installation mode, not an automatic refusal.

The archive format contains no payment token or entitlement check. Compatibility and package safety checks apply equally to all users.
