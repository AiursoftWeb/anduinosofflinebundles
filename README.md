# AnduinOS Offline Bundles

[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/anduinosofflinebundles/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/anduinosofflinebundles/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fanduinosofflinebundles)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fanduinosofflinebundles&scope=baseline&ecosystem=all)
[![NuGet version](https://img.shields.io/nuget/v/Aiursoft.AnduinOS.OfflineBundles.svg)](https://www.nuget.org/packages/Aiursoft.AnduinOS.OfflineBundles/)
[![Man hours](https://manhours.aiursoft.com/r/gitlab.aiursoft.com/aiursoft/anduinosofflinebundles.svg)](https://manhours.aiursoft.com/r/gitlab.aiursoft.com/aiursoft/anduinosofflinebundles.html)

Build signed, cumulative offline updates for AnduinOS. One `.atgz` file brings multiple supported older installations to the same package target.

The tool builds, verifies and previews updates. It does not install them; the desktop updater is a separate project.

## Installation

Requirements:

1. [.NET 10 SDK](https://dot.net/).
2. Debian/Ubuntu Linux with `apt-get`, `apt-cache`, `apt-ftparchive` (`apt-utils`), `dpkg`, `dpkg-deb`, `gpg` and `gpgv`.

Once the package is published, install this tool globally:

```bash
dotnet tool install --global Aiursoft.AnduinOS.OfflineBundles
```

For an unpublished checkout, use [local package installation](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/docs/usage.md#build-a-local-tool-package).

## Usage

Run as an ordinary user. Inspect an update using an independently trusted public keyring:

```bash
anduinos-offline-bundles inspect --file update.atgz --keyring trusted-keys.gpg --json
```

Use `build` to create bundles and `verify` to check them. See [commands and validation](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/docs/usage.md), [build profiles](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/docs/build-profiles.md) and the [archive format](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/docs/atgz-v1.md).

For cumulative updates from the official Resolute/amd64 sources, use the [release build profile](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/docs/release-builds.md). It selects the latest target automatically and keeps baseline files and trusted keys outside the checkout.

## Run locally

With the requirements above installed, run from the repository root:

```bash
dotnet run --project src/Aiursoft.AnduinOS.OfflineBundles -- --help
dotnet test
```

Source projects live in `src/`, business tests in `tests/`, and the solution at the root. Tests use the native Linux tools with temporary repositories and signing keys; they do not install packages on the host.

## Run in Microsoft Visual Studio

Open `Aiursoft.AnduinOS.OfflineBundles.sln` and set the CLI project as the startup project. Run the native APT commands and tests on Linux; the .NET runtime alone on Windows does not supply those tools.

## How to contribute

Report bugs, propose features or submit a merge request. Use a feature branch and include the relevant validation. Run `dotnet test` and `./lint.sh` before submitting changes.

## License

[MIT](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/blob/master/LICENSE).
