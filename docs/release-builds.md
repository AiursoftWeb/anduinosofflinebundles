# Builds from official release sources

The release profiles build cumulative AnduinOS updates from the official Ubuntu and AnduinOS repositories. Each reads the target version from the authenticated AnduinOS `base-files` candidate and calculates package versions automatically. A new security batch can be produced without changing the OS version or editing the profile.

Run after the complete source release has finished publishing and is available from its APT indexes. A successful build authenticates its captured inputs and checks dependency closure; it does not establish that an upstream release is complete or that installation and reboot testing have passed. Publishing archives to users remains a separate operation.

## Release groups

| Suite | Architecture | Release line | Original baseline | Profile |
| --- | --- | --- | --- | --- |
| Resolute | AMD64 | 2.0 | 2.0.0 | [resolute-amd64.json](../profiles/resolute-amd64.json) |
| Resolute | ARM64 | 2.0 | 2.0.1 | [resolute-arm64.json](../profiles/resolute-arm64.json) |
| Stonking | AMD64 | 2.1 | 2.1.0 | [stonking-amd64.json](../profiles/stonking-amd64.json) |
| Stonking | ARM64 | 2.1 | 2.1.0 | [stonking-arm64.json](../profiles/stonking-arm64.json) |

ARM64 uses Ubuntu Ports, including its security pocket. The first separate
published ARM64 image is 2.0.1; do not relabel an AMD64 or newer installation
as an ARM64 2.0.0 baseline. Stonking profiles are prepared for the official 2.1
release: they must not be enabled until the official repositories and genuine
first-release inputs exist and installation/reboot qualification passes.
A profile's presence is not a release-readiness claim. Never substitute a
private development repository in a public production job.

Use one automation job per qualified group. Each group independently retains
three batches, giving twelve visible archives when all four groups are enabled.
An absent release must not be replaced with another suite or architecture.

## Prepare reusable inputs

Keep these files in a controlled directory outside the checkout:

```text
inputs/
  keys/
    ubuntu-archive-keyring.gpg
    anduinos-archive-keyring.gpg
    bundle-signing.gpg
  baselines/
    2.0.0.status
    2.0.0.os-release
    resolute-arm64/
      2.0.1.status
      2.0.1.os-release
    stonking-amd64/
      2.1.0.status
      2.1.0.os-release
    stonking-arm64/
      2.1.0.status
      2.1.0.os-release
```

Capture each pair from a genuine original installation for its release group, with no APT or dpkg transaction active: `/var/lib/dpkg/status` and `/usr/lib/os-release`. Retain the release, architecture, installation layout and capture provenance with these inputs. Never rename a newer system's metadata to manufacture an old baseline. Package databases contain installed-package information and belong in controlled build storage, not a public download.

Use independently trusted public keyrings from the official `ubuntu-keyring` and `anduinos-archive-keyring` packages. On an installation where those packages have been authenticated, their files are `/usr/share/keyrings/ubuntu-archive-keyring.gpg` and `/usr/share/keyrings/anduinos-archive-keyring.gpg`. A key supplied by an untrusted update archive cannot establish that archive's authenticity.

Each group's original baseline is a retained input, not something to recapture every two days. Intermediate releases are not required build inputs or an installer allowlist. The goal is one cumulative bundle for installations from its original baseline through the target version within the same release line, suite and architecture; the updater resolves against each machine's actual package state. A system already at the target OS version can still receive newer components, or report `already-installed` when no changes are needed.

The official profiles also include the latest dependencies and recommendations of `anduinos-core-system`, `anduinos-desktop`, `anduinos-desktop-apps` and `anduinos-btrfs-snapshots-manager`. This covers current desktop components missing from the original baseline without forcing them onto clients that do not use them. Versions come from the authenticated sources on every build; there is no per-release package-version list. See [current desktop coverage](build-profiles.md#current-desktop-coverage).

Passing the original baseline closure check alone does not prove every intermediate package state works. Validate intermediate and partially updated installations separately, including dependency transitions and supported layouts. Do not turn individual validation examples into mandatory production baselines. Another release line, suite or architecture needs its own genuine original baseline and qualification.

## Build

From a checkout of this repository, with the tool installed and `ESRPSIGN_API_KEY` / `ESRPSIGN_KEY_PASSPHRASE` exported:

```sh
anduinos-offline-bundles build \
  --profile profiles/resolute-amd64.json \
  --inputs /srv/anduinos-offline/inputs \
  --output /srv/anduinos-offline/output \
  --esrp-endpoint https://esrp-sign.aiursoft.com \
  --esrp-key-id SIGNING_KEY_GUID \
  --keyring /srv/anduinos-offline/inputs/keys/bundle-signing.gpg \
  --json
```

The profile's adjacent [APT preferences](../profiles/official.preferences) reproduce AnduinOS's priority for its own packages. The resolver still refuses downgrades and unapproved removals. Source signatures and exact DEB hashes are verified, and each baseline is checked again with only the completed bundle's local repository.

The Resolute profiles' two installation stages cover the reviewed initramfs-to-Dracut migration: install `anduinos-core-system` and `plymouth-anduinos`, then perform the full upgrade. The seven permitted removals name that migration's obsolete initramfs/live-system packages. They are not permission to remove arbitrary user software. Stonking starts with its native Dracut layout and only uses dist-upgrade, with no permitted removals. Review any future migration separately.

Use development signing keys for evaluation. Before distributing a release, the production signing identity must be trusted by the supported updater versions and the required installation/reboot matrix must pass. The profile neither deploys trust keys nor changes client activation policy.

## Results and repeated builds

JSON stdout contains the file path, target version, batch ID, package count, byte size and SHA-256. Diagnostics go to stderr. Existing verified archives for the same batch are reused without replacing their bytes. A changed package target creates another batch even if the OS version is unchanged. Repeated builds still fetch source indexes and resolve inputs; reuse is an immutable-output guarantee, not an early network/cache shortcut.

Keep the adjacent `.build.json` and `.inputs/` as build evidence. The captured profile includes the actual preferences, public source keys, baseline databases and index hashes. It can be read without the original checkout, although replaying a live source later may fail if that source has removed an exact DEB version.

If a source, signature, dependency or payload fails validation, the command exits unsuccessfully and preserves previous completed archives. Do not treat file existence alone as a successful new build. This command does not upload to R2, register a website release or prune previous archives.

Verify and inspect the returned file independently:

```sh
anduinos-offline-bundles verify --file /path/to/result.atgz \
  --keyring /path/to/trusted-bundle-keys.gpg --json

anduinos-offline-bundles inspect --file /path/to/result.atgz \
  --keyring /path/to/trusted-bundle-keys.gpg \
  --status /srv/anduinos-offline/inputs/baselines/2.0.0.status \
  --os-release /srv/anduinos-offline/inputs/baselines/2.0.0.os-release --json
```

A system already on the same OS version may still need newer package targets. `already-installed` requires that the actual package state satisfies the batch, not just that `VERSION_ID` matches.
