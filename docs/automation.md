# Automated R2 publication

`automate` runs the existing cumulative builder on a schedule and publishes directly to R2. It opens no HTTP port and does not call AnduinOS Home. The website can be developed and deployed independently, consuming the R2 catalog later.

## Configure a worker

Use a dedicated local state volume and one worker replica. Do not run a second publisher against the same catalog from another machine. A lifetime file lock prevents overlapping invocations sharing the state volume; conditional catalog writes detect conflicting remote changes but are not a distributed worker lease.

Copy [automation.example.json](../profiles/automation.example.json) outside the checkout and replace its endpoint, bucket and signing fingerprint. Paths are absolute. The included example enables only the existing Resolute/amd64 profile. Add a job only after preparing and qualifying that suite, release line and architecture's profile and real baseline inputs. The example is not evidence that all four target groups are ready.

Supply the [release build inputs](release-builds.md) and a signing key trusted by the desktop updater. Keep the signing home private and writable by the worker's user, because GnuPG creates locks and agent sockets. Public keyrings and baseline inputs can be read-only. Do not publish archives signed with temporary test keys as official updates.

The worker checks that the configured private signing key already exists before processing jobs or waiting for the next run. It never generates or replaces an identity. Set optional `trusted_keyring_sha256` to the approved public keyring's SHA-256 to reject a changed keyring at startup. Existing settings without this field remain supported. Mount the settings file read-only at `/config/automation.json`; the container's default command reads it directly, without a shell wrapper or JSON environment variable.

Set `ANDUINOS_R2_ACCESS_KEY` and `ANDUINOS_R2_SECRET_KEY` in the runtime environment. The credential needs object read, write and delete access to the selected bucket. No website URL, website token, database, or inbound network port is needed. Never put credentials in the settings file, image, source repository, or logs.

Run the currently due jobs once:

```bash
anduinos-offline-bundles automate --settings /config/automation.json --once
```

Then run continuously:

```bash
anduinos-offline-bundles automate --settings /config/automation.json
```

The first run is immediate. A successful group is next due 48 hours after that run started; a failed group retries after 60 minutes by default. Both intervals are configurable. Restarting respects the saved schedule. `--once` also respects it and returns nonzero if a due job or retention cleanup fails. A configured group failing does not prevent the remaining groups from running.

The worker always uses the profile's latest authenticated APT candidates. It does not require someone to edit a target version every release. A newer package set can generate a new batch even when the OS version stays the same. Unchanged batches reuse the original published object and do not consume another retention slot.

## Container

Build using the repository's normal Docker workflow:

```bash
docker build -t anduinos-offline-bundles .
```

The image contains the .NET CLI, profiles and native APT/GnuPG tools. It runs as UID 10001. Prepare a dedicated `/data` volume and a signing home writable by this user. Mount deployment configuration and baseline inputs separately; do not bake secrets into the image.

```bash
docker run --name offline-bundles --restart unless-stopped \
  --env-file /etc/anduinos-offline-bundles/r2.env \
  --mount type=bind,src=/etc/anduinos-offline-bundles/config,dst=/config,readonly \
  --mount type=bind,src=/var/lib/anduinos-offline-bundles/inputs,dst=/inputs,readonly \
  --mount type=bind,src=/var/lib/anduinos-offline-bundles/signing,dst=/signing \
  --mount type=bind,src=/var/lib/anduinos-offline-bundles/data,dst=/data \
  anduinos-offline-bundles
```

Use container stdout/stderr for run outcomes. SIGTERM cancels the current native build/upload and preserves pending work for restart. No cron daemon, web server or host package installation is required. The process does not install the packages it builds.

Measure disk, memory and build duration with the real input set before choosing the production node and resource limits. Cross-architecture bundle construction is APT resolution, not a substitute for installation and reboot testing on that architecture.

## Catalog contract

The only discovery object is `offline/catalog.json`, read through the R2 S3 endpoint. Schema version 1 contains:

- `schema_version`: currently `1`; reject unsupported schemas.
- `releases`: visible entries, at most three per `(suite, release_line, architecture)`.
- `retired`: worker cleanup records, never display these as downloads.

Each release has `update`, `object_key` and `published_utc`. `update` contains `batch_id`, `release_line`, `suite`, `architecture`, `target_version`, `minimum_version`, `sha256`, `signing_fingerprint`, `size`, `created_utc` and a plain-text `summary`. Minimum version is descriptive; the signed archive carries the actual supported baselines and installer requirements. Website consumers must not infer compatibility from that number alone.

The object key is immutable and derived from the signed manifest and full archive SHA256:

```text
offline/{suite}/{release_line}/{architecture}/{batch_id}/{sha256}.atgz
```

Website readers should sort each group by numeric target version, then creation time descending. Display the summary as text. Never expose the R2 credential. A later website can issue download links using its own read-only access; there is no publisher-to-website registration step. Keep catalog caches and issued download link lifetimes below the 24-hour deletion grace.

## Failure and retention behavior

1. Build using authenticated sources, verify signatures and baseline APT dependency closure, then persist the pending archive name locally.
2. Upload the immutable archive and read it back to verify its full SHA256 and size.
3. Read the current catalog and conditionally replace it using its ETag (`If-Match`), or `If-None-Match: *` on first publication. The catalog is updated last; readers see complete old or new JSON, never a partial upload. Other groups remain unchanged.
4. Keep the newest three releases in each group. Move displaced releases into `retired` with a deletion time at least 24 hours later. Catalog listings can therefore contain twelve visible releases for four configured groups while storage temporarily holds more than twelve files.
5. After the grace period, delete only those recorded retired objects, then remove their cleanup records. Failed cleanup is retried. Unknown bucket objects are never swept.

An upload, verification or catalog failure does not remove an existing release. An uncertain catalog-write response leaves the local archive pending; retry reads the authoritative catalog before publishing again. A rebuilt identical batch keeps the original remote bytes rather than replacing them with a newly timestamped signature. An older target is rejected before upload.

Only this worker may write or prune the catalog. Active and retired batch IDs cannot overlap. Do not manually restore a retired entry while cleanup is running. Replacing the catalog to implement withdrawal or disaster recovery requires stopping the worker first; those administrative commands are not part of this release.

Local state retains the latest three published archives and their build evidence, plus unfinished work. Only files in each dedicated job output directory are pruned. After an interrupted build, that job's abandoned `.build-*` workspace and incomplete sidecars are removed before rebuilding. The volume must not be shared with manual builds or other data. Configure an R2 lifecycle policy to abort abandoned multipart uploads; process termination cannot always abort them itself.

This automation verifies signatures, package hashes, local dependency closure and storage integrity. It does not boot a VM for every scheduled batch. Production signing, input qualification, representative installation/reboot validation, R2 permissions and first real upload remain deployment prerequisites.

## Existing command compatibility

The older `publish` command still performs its original Home API registration for callers that explicitly use it. New automation uses only R2; it does not need the unmerged website implementation or its database. Do not mix legacy publication and catalog-based discovery and assume they share a listing.
