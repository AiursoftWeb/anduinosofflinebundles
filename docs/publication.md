# Publish an offline update

Publication is a separate step after build and installation qualification. The command verifies the signed archive, uploads it to Cloudflare R2, downloads the stored object to check its SHA256, then registers its metadata with AnduinOS Home. A failed upload or verification never registers a download.

This first stage is invoked manually. It does not schedule builds, create signing keys, issue R2 credentials, enforce paid entitlement, or delete old objects. A valid signature does not prove that installation works: qualify the exact bundle on the supported systems before publishing it. Development test keys and fixture bundles are not production releases.

## Configure the publisher

Create a JSON settings file containing non-secret endpoints:

```json
{
  "service_url": "https://ACCOUNT_ID.r2.cloudflarestorage.com",
  "bucket": "YOUR_BUCKET",
  "home_url": "https://www.anduinos.com"
}
```

Use the actual R2 S3 endpoint for the selected jurisdiction. Configure these environment variables in the publishing environment, without putting their values in command arguments or the repository:

| Variable | Purpose |
| --- | --- |
| `ANDUINOS_R2_ACCESS_KEY` | R2 S3 access key ID |
| `ANDUINOS_R2_SECRET_KEY` | R2 S3 secret, with read/write access to the selected bucket |
| `ANDUINOS_HOME_PUBLISHER_TOKEN` | Token matching Home's `OfflineUpdates:PublisherToken` |

Home also needs read access to the bucket to check objects and generate download URLs. See its offline-update publication documentation for server configuration. No configuration is required to build or inspect bundles locally.

Write a short, plain-text update summary in `summary.txt`, then run:

```bash
anduinos-offline-bundles publish --file update.atgz --keyring trusted-keys.gpg --settings publication.json --summary-file summary.txt --json
```

The external public keyring must already be trusted. Metadata comes from the verified archive, not arbitrary release arguments. Multi-gigabyte uploads use bounded 64 MiB multipart buffers. Cancellation attempts to abort the incomplete multipart upload; configure an R2 incomplete-upload lifecycle rule when deploying, to cover terminated processes too.

## Retry and withdrawal

Objects use `offline/{suite}/{release_line}/{architecture}/{batch_id}/{sha256}.atgz`. There is no mutable `latest.atgz`. Existing objects must match the expected size and metadata, and are downloaded and verified again before registration. If Home is unavailable, keep the object and retry the same command with the same summary. A lost HTTP response may mean registration already succeeded; Home accepts an identical batch without adding a duplicate.

Home refuses conflicting metadata for an existing batch. Its download page selects the highest target patch within each release line, suite and architecture; a late older build cannot replace a newer OS target. Rebuilds of the same target are ordered by archive creation time. Only the latest three active batches per group are shown. This is display selection, not storage retention: the future retention worker must withdraw old entries before deleting their objects.

To withdraw a bad batch, send an authenticated `DELETE /api/offline-updates/{batch_id}` to Home. Withdrawal is idempotent and removes it from the page and new download redirects. It does not delete the object or revoke a previously issued download URL, which expires within one hour. Publishing the same withdrawn batch again is refused. Build and qualify a new batch for a replacement.

Downloads are temporarily anonymous. The hidden-in-navigation URL is not a security boundary. The updater still verifies the archive signature and contents independently of the website, TLS and R2 metadata.
