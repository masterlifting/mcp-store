# Workflow release version decision

The Workflow release policy advances from `1.0.4` to `1.0.5`. The published `v1.0.4.zip` is immutable; its verified public archive is 1,514,420 bytes with SHA-256 beginning `65d274`, and its manifest revision is `e664`. The archived assembly's informational version is `1.0.0+e664`.

The source project sets `IncludeSourceRevisionInInformationalVersion=false`, so a newly built assembly would have informational version `1.0.0` and would not be byte-identical to the published DLL. The strict no-op exception applies only when the DLL bytes are unchanged and the manifest revision is the only difference; it does not apply here. This decision changes only the Workflow release version, not the SDK, runtime, platform, allowlist, Dotnet version, or the immutable published asset.

Clean packaging evidence for `1.0.5` remains pending.
