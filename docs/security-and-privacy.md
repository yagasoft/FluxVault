# Security and privacy

## V1 posture

FluxVault keeps diagnostics local and does not send telemetry. Users can export
logs manually when they want support.

## Encryption

V1 stores repository artefacts in plain form. The storage format should preserve
a clean future path for encrypted chunks and encrypted metadata, but encryption
is not implemented in v1.

## Secrets

Cloud-folder mode does not require FluxVault cloud secrets. Direct cloud
adapter configuration stores credential references only; secret values must stay
outside repository manifests, configuration examples, project files, tests, and
logs. Live credential validation is deferred until explicit account access is
provided.

## Consistency claims

The UI and logs must distinguish:

- app-consistent capture
- crash-consistent snapshot capture
- best-effort capture
- failed capture
