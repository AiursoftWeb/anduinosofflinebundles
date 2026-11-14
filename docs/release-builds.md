# Builds from official release sources

The [Resolute/amd64 profile](../profiles/resolute-amd64.json) builds cumulative AnduinOS 2.0 updates from the official Ubuntu and AnduinOS repositories. It reads the target version from the authenticated AnduinOS `base-files` candidate and calculates package versions automatically. A new security batch can be produced without changing the OS version or editing this profile.

Run after the complete source release has finished publishing and is available from its APT indexes. A successful build authenticates its captured inputs and checks dependency closure; it does not establish that an upstream release is complete or that installation and reboot testing have passed. Publishing archives to users remains a separate operation.

## Prepare reusable inputs

Keep these files in a controlled directory outside the checkout:

```text
inputs/
  keys/
    ubuntu-archive-keyring.gpg
    anduinos-archive-keyring.gpg
  baselines/
    2.0.0.status
    2.0.0.os-release
    2.0.4.status
    2.0.4.os-release
```

Capture each pair from a genuine installation of the named release, with no APT or dpkg transaction active: `/var/lib/dpkg/status` and `/usr/lib/os-release`. Retain the release, architecture, installation layout and capture provenance with these inputs. Never rename a newer system's metadata to manufacture an old baseline. Package databases contain installed-package information and belong in controlled build storage, not a public download.

Use independently trusted public keyrings from the official `ubuntu-keyring` and `anduinos-archive-keyring` packages. On an installation where those packages have been authenticated, their files are `/usr/share/keyrings/ubuntu-archive-keyring.gpg` and `/usr/share/keyrings/anduinos-archive-keyring.gpg`. A key supplied by an untrusted update archive cannot establish that archive's authenticity.

These release baselines are retained inputs, not files to recapture every two days. Add or review baselines when supporting another release, architecture or migration. This initial profile covers the captured 2.0.0 and 2.0.4 installations; additional layouts and user-installed applications require their own validation.

## Build

From a checkout of this repository, with the tool installed:

```sh
anduinos-offline-bundles build \
  --profile profiles/resolute-amd64.json \
  --inputs /srv/anduinos-offline/inputs \
  --output /srv/anduinos-offline/output \
  --signing-home /path/to/controlled/gnupg \
  --signing-key SIGNING_KEY_FINGERPRINT \
  --keyring /path/to/trusted-bundle-keys.gpg \
  --json
```

The profile's adjacent [APT preferences](../profiles/resolute-amd64.preferences) reproduce AnduinOS's priority for its own packages. The resolver still refuses downgrades and unapproved removals. Source signatures and exact DEB hashes are verified, and each baseline is checked again with only the completed bundle's local repository.

The two installation stages cover the reviewed initramfs-to-Dracut migration: install `anduinos-core-system` and `plymouth-anduinos`, then perform the full upgrade. The seven permitted removals name that migration's obsolete initramfs/live-system packages. They are not permission to remove arbitrary user software. Review this policy before extending it to another release line or layout.

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
