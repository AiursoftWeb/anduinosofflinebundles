# Commands and validation

Supply a reviewed build profile, baseline package databases and externally trusted public keys. See [the example profile](../profiles/example-2.0-amd64.json) and [profile preparation](build-profiles.md).

For official Resolute/amd64 updates, see [the release profile and input layout](release-builds.md). Add `--inputs /path/to/inputs` to keep baseline files and source keys separate from the checkout. Target versions are read from signed package metadata; there is no manually maintained target-version argument.

```sh
anduinos-offline-bundles build \
  --profile profiles/example-2.0-amd64.json --output ./dist/bundles \
  --signing-home /path/to/controlled/gnupg --signing-key SIGNING_KEY_FINGERPRINT \
  --keyring /path/to/trusted-bundle-keys.gpg --json

anduinos-offline-bundles verify --file update.atgz \
  --keyring /path/to/trusted-bundle-keys.gpg

anduinos-offline-bundles inspect --file update.atgz \
  --keyring /path/to/trusted-bundle-keys.gpg --json

anduinos-offline-bundles inspect --file update.atgz \
  --keyring /path/to/trusted-bundle-keys.gpg \
  --status /path/to/baseline.status --os-release /path/to/baseline.os-release --json
```

- **build** freezes authenticated APT indexes, simulates each baseline, downloads exact DEBs, creates a signed local repository, and validates each baseline using only that repository before producing an archive. Target version comes from `os-release` inside the resolved version package, normally `base-files`.
- **verify** checks the manifest signature, archive boundaries, file sizes and hashes, repository signatures and package-index consistency. Trust comes from your external keyring, never from an archive-supplied key.
- **inspect** verifies before showing metadata. With a status snapshot and its matching `os-release`, it also produces a local-only APT plan: `update-available`, `already-installed` or `blocked`.

Diagnostics go to stderr; `--json` writes the result to stdout. Exit codes: `0` success, `1` invalid input or operation failure, `2` a valid preview with blockers, `130` cancellation.

## Guarantees and limits

The tool uses private APT configuration, lists, caches and a copied status database. It does not read the host's APT sources, run its APT hooks, replace its status database, or execute `apt install` against the host.

An unapproved removal, downgrade, missing dependency or unmet applicable package target blocks the preview. Unrelated user packages are preserved when APT can resolve the update. A held package or an incompatible third-party package can prevent an update; it must not be reported as complete.

Repeat detection compares package targets as well as the OS version. A newer security batch may still apply when `VERSION_ID` is unchanged. An existing output for the same batch is verified and reused, never silently overwritten. Separate fresh builds are not promised to be byte-identical because signatures and repository timestamps can differ.

Current planning supports one installed architecture (`amd64` or `arm64`, plus `all`) within one release line and suite. Foreign-architecture packages and unfinished dpkg transactions are rejected. The current integration sample is amd64; arm64 needs independent validation. APT pinning, local preferences, maintainer-script behavior, reboot and interruption recovery require installer-stage validation. This planner deliberately does not import arbitrary host APT configuration.

The space figure is **unpacked staging space**, not total installation or snapshot space. Snapshot capability is unknown to the builder. It is not a requirement for applying an update in the future installer.

The builder has no account, licence or payment checks. Access to officially distributed bundles is a separate delivery policy.

See [the archive contract](atgz-v1.md) for format, signing and compatibility details. Keep build reports and captured inputs for reproducibility; distribute only approved, installation-tested archives to users.

## Build a local tool package

To build and install the current source locally, run from the repository root:

```sh
dotnet build -c Release -p:GeneratePackageOnBuild=false
dotnet test --no-restore
dotnet pack -c Release --no-build --output ./dist/packages
dotnet tool install Aiursoft.AnduinOS.OfflineBundles \
  --tool-path ./dist/tools --add-source ./dist/packages --version 10.0.5
./dist/tools/anduinos-offline-bundles --help
```

The tool package contains the internal Core assembly; Core is not a separate NuGet product.

Core is an internal assembly. Keep it without an independent `Version` property: NugetNinja treats versioned library projects as separately published packages. The CLI owns the tool version and includes Core in its package.
