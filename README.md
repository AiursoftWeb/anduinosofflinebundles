# Aiursoft AnduinOSOfflineBundles

<a name="anduinos-offline-bundles"></a>
[![MIT licensed](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/LICENSE)
[![Pipeline stat](https://badge-proxy.aiursoft.com/aiursoft/anduinosofflinebundles/badges/master/pipeline.svg)](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/pipelines)
[![Test Coverage](https://badge-proxy.aiursoft.com/aiursoft/anduinosofflinebundles/badges/master/coverage.svg)](https://gitlab.aiursoft.com/aiursoft/anduinosofflinebundles/-/pipelines)
[![S360 compliance](https://s360.aiursoft.com/badge.svg?project=aiursoft%2Fanduinosofflinebundles)](https://s360.aiursoft.com/SupplyChain?tab=projects&project=aiursoft%2Fanduinosofflinebundles&scope=baseline&ecosystem=all)
[![NuGet version](https://img.shields.io/nuget/v/Aiursoft.AnduinOS.OfflineBundles.svg)](https://www.nuget.org/packages/Aiursoft.AnduinOS.OfflineBundles/)
[![Man hours](https://manhours.aiursoft.com/r/github.com/AiursoftWeb/anduinosofflinebundles.svg)](https://manhours.aiursoft.com/r/github.com/AiursoftWeb/anduinosofflinebundles.html)

Build signed, cumulative offline updates for AnduinOS. One `.atgz` file brings multiple supported older installations to the same package target.

## Overview

### What is AnduinOSOfflineBundles?

Build, inspect, verify and publish signed cumulative offline-update bundles for AnduinOS. The tool produces archives; installation belongs to the separate desktop updater.

The tool builds, verifies, previews and publishes updates. It does not install them; the desktop updater is a separate project.

## Installation

Install the CLI with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

**Additional requirements**

- Debian/Ubuntu Linux with `apt-get`, `apt-cache`, `apt-ftparchive` (`apt-utils`), `dpkg`, `dpkg-deb` and `gpgv` (`gpg` is only required when running the test suite).

Install the published tool globally:

```bash
dotnet tool install --global Aiursoft.AnduinOS.OfflineBundles
```

For an unpublished checkout, use [local package installation](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/usage.md#build-a-local-tool-package).

Update an existing installation:

```bash
dotnet tool update --global Aiursoft.AnduinOS.OfflineBundles
```

Remove the CLI:

```bash
dotnet tool uninstall --global Aiursoft.AnduinOS.OfflineBundles
```

## Usage

Run the command to see available options:

```bash
anduinos-offline-bundles --help
```

Run as an ordinary user. Inspect an update using an independently trusted public keyring:

```bash
anduinos-offline-bundles inspect --file update.atgz --keyring trusted-keys.gpg --json
```

Use `build` to create bundles via ESRP Sign and `verify` to check them locally with `gpgv`. See [commands and validation](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/usage.md), [build profiles](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/build-profiles.md) and the [archive format](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/atgz-v1.md).

For cumulative updates from official sources, see the four suite/architecture [release build profiles](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/release-builds.md). Each selects the latest target automatically and keeps baseline files and trusted keys outside the checkout. Enable a group only after its official release sources, original baseline and installation qualification are ready.

Use `automate` to build the latest cumulative updates every 48 hours, sign remotely through ESRP Sign without local private keys, upload them to R2 and maintain a shared download catalog, without a website API. See [automated publication and Docker setup](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/automation.md).

Use `publish` to upload an installation-qualified bundle to R2 and register it with AnduinOS Home. See [publication and retry behavior](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/publication.md).

## Configuration

Configuration is supplied through the selected command and its options. Build profiles, release source baselines and trusted keyrings are explicit inputs; keep original baselines and trusted keys outside the checkout. Run `anduinos-offline-bundles <command> --help` for that command’s current options.

Inspecting an archive requires an independently trusted keyring, not a key taken from the archive itself. ESRP Sign, R2 and website publication settings apply only to the build and publication commands; see the linked guides below.

## Development

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

From the repository root, restore and build:

```bash
dotnet restore Aiursoft.AnduinOS.OfflineBundles.sln
dotnet build Aiursoft.AnduinOS.OfflineBundles.sln --no-restore
```

<a name="run-locally"></a>
<a name="local-run"></a>
Run the tool from source, with the prerequisites and configuration above:

```bash
dotnet run --project src/Aiursoft.AnduinOS.OfflineBundles/Aiursoft.AnduinOS.OfflineBundles.csproj -- --help
```

<a name="run-in-microsoft-visual-studio"></a>
In Microsoft Visual Studio, open `Aiursoft.AnduinOS.OfflineBundles.sln`, select `Aiursoft.AnduinOS.OfflineBundles` as the startup project and press `F5`. Configure the arguments and environment for the task you want to run.

Run the existing tests:

```bash
dotnet test Aiursoft.AnduinOS.OfflineBundles.sln --no-restore
```

Source projects live in `src/`, business tests in `tests/`, and the solution at the root. Tests use the native Linux tools with temporary repositories and signing keys; they do not install packages on the host.

Open `Aiursoft.AnduinOS.OfflineBundles.sln` and set the CLI project as the startup project. Run the native APT commands and tests on Linux; the .NET runtime alone on Windows does not supply those tools.

## Documentation

- [Command reference](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/usage.md)
- [Build profiles](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/build-profiles.md)
- [Archive format](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/atgz-v1.md)
- [Release build profiles](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/release-builds.md)
- [Automated publication and Docker setup](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/automation.md)
- [Publication and retry behavior](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/docs/publication.md)

## How to contribute

There are many ways to contribute to the project: logging bugs, submitting pull requests, reporting issues, and creating suggestions.

Even if you have push rights on the repository, you should create a personal fork and create feature branches there when you need them. This keeps the main repository clean and your workflow cruft out of sight.

We're also interested in your feedback on the future of this project. You can submit a suggestion or feature request through the issue tracker. To make this process more effective, we're asking that these include more information to help define them more clearly.

Report bugs, propose features or submit a merge request. Use a feature branch and include the relevant validation. Run `dotnet test` and `./lint.sh` before submitting changes.

## License

Licensed under the [MIT License](https://github.com/AiursoftWeb/anduinosofflinebundles/blob/master/LICENSE).
