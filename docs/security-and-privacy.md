# Security and privacy

## V1 posture

FluxVault keeps diagnostics local and does not send telemetry. Users can export
logs manually when they want support.

## Encryption

V1 stores repository artefacts in plain form. The LATER foundation adds typed
client-side encryption configuration, key-reference records, IPC status, and a
pure encryption planning contract. It does not encrypt chunks, metadata, or
manifests yet.

Encryption configuration must use references only. Raw keys, private-key
blocks, recovery secrets, passwords, and provider tokens must not be stored in
FluxVault configuration, repository manifests, tests, docs examples, or logs.
The configuration validator rejects obvious inline key material in key
reference fields.

## Secrets

Cloud-folder mode does not require FluxVault cloud secrets. Direct cloud
adapter configuration stores credential references only; secret values must stay
outside repository manifests, configuration examples, project files, tests, and
logs. Live credential validation is deferred until explicit account access is
provided.

## Enterprise and fleet

The enterprise/fleet foundation stores disabled-by-default local policy source,
assignment, and local status records. It does not contact a remote management
plane, enrol devices, push policy, collect fleet telemetry, or validate tenant
identity. Diagnostics reports remote management as deferred until those
features are explicitly implemented.

## Consistency claims

The UI and logs must distinguish:

- app-consistent capture
- crash-consistent snapshot capture
- best-effort capture
- failed capture
