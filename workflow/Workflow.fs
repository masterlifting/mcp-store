module Workflow

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading
open Common.CE
open Microsoft.Win32.SafeHandles

// Schema 1 is the sole canonical persisted Workflow representation. There is no
// compatibility reader: a task is either this exact document or it is rejected.
[<Literal>]
let SchemaVersion = 1

[<Literal>]
let GeneralProfileId = "general"

[<Literal>]
let SoftwareProfileId = "software"

[<Literal>]
let OpenCodeProfileId = "opencode"

[<Literal>]
let SidecarFileName = "runtime.json"

type Kind =
    | Execution
    | Research

[<RequireQualifiedAccess>]
type EvidenceKind =
    | Build
    | Test
    | Review
    | Observation
    | ExternalEffect
    | Research
    | DecisionEvidence
    | Other of string

// EvidenceSource stays an open semantic primitive because the architecture
// enumerates EvidenceKind but does not fix a source vocabulary.
type EvidenceSource = EvidenceSource of string

type EvidenceValidity =
    | Valid
    | Superseded of string

type Evidence =
    { Id: string
      Kind: EvidenceKind
      Source: EvidenceSource
      Subject: string option
      ProducerRole: string option
      ProducerId: string option
      Reference: string option
      Summary: string }

type EvidenceRecord =
    { Evidence: Evidence
      Validity: EvidenceValidity }

type AcceptanceState =
    | Pending
    | Verified of string list

type Acceptance =
    { Id: string
      Text: string
      State: AcceptanceState }

// Section 21: task semantic contract has two phases. Draft absorbs ordinary
// Coordinator design; the first material StartWorkItem establishes the baseline
// and records the contract fingerprint that later writes must match.
type ContractState =
    | Draft
    | Baselined

// Section 23: the contract-critical semantic content that is fingerprinted.
// The canonical snapshot travels as a command payload for drift restoration; it
// is never persisted as a second writable contract.
type ContractContent =
    { Objective: string
      Scope: string
      NonGoals: string
      AcceptanceCriteria: (string * string) list }

// Section 15: Guards are the mechanically enforceable Profile/task-design policy.
// Decisions are not implemented yet, so decision-backed dispositions cannot be
// produced by a command; only Applicable guards participate in satisfaction.
[<RequireQualifiedAccess>]
type MinimumAuthority =
    | CoordinatorAuthority
    | UserAuthority

type EvidenceRequirement =
    { Kind: EvidenceKind
      MinimumCount: int
      ProducerRole: string option
      RequireIndependentProducer: bool }

type GuardRequirement =
    | EvidenceRequired of EvidenceRequirement

type GuardTarget =
    | TaskTarget
    | WorkItemTarget of string

type GuardCheckpoint =
    | BeforeStart
    | BeforeComplete

type GuardOrigin =
    | ProfileMaterialized
    | TaskDesign

type ApplicabilityPolicy =
    | Always
    | ExplicitDecision of MinimumAuthority

type WaiverPolicy =
    | NotWaivable
    | WaivableBy of MinimumAuthority

[<RequireQualifiedAccess>]
type GuardDisposition =
    | Applicable
    | NotApplicable of string
    | Waived of string

type Guard =
    { Id: string
      Target: GuardTarget
      Checkpoint: GuardCheckpoint
      Origin: GuardOrigin
      Requirement: GuardRequirement
      Applicability: ApplicabilityPolicy
      Waiver: WaiverPolicy
      Disposition: GuardDisposition }

// Section 15.5: a ProfileMaterialized Guard paired with the ProfileGuardSpec.Key
// that materialized it. Carrying the key through the sync event lets evolve persist
// stable key provenance without changing the Guard record/wire shape, so sync
// correlates by key rather than by indistinguishable structured content.
type MaterializedGuard =
    { Guard: Guard
      Key: string option }

// Task-design input for AddGuard; Origin is assigned by the runtime.
type GuardSpec =
    { Id: string
      Target: GuardTarget
      Checkpoint: GuardCheckpoint
      Requirement: GuardRequirement
      Applicability: ApplicabilityPolicy
      Waiver: WaiverPolicy }

// Sections 17-20: Profiles are the primary task-design extension point. A Profile
// is task policy, never a permission grant; the capability envelope describes
// what the task may use, and mandatory policy is materialized as typed Guards.
type CapabilityEnvelope =
    { Required: string list
      Default: string list
      Allowed: string list }

type ProfileRouting =
    { Positive: string list
      Negative: string list }

type RoleDefault =
    { Purpose: string
      Role: string }

// A profile guard is task-class policy: it cannot reference task-specific
// WorkItem IDs, so its Target is always TaskTarget. TaskKind optionally scopes
// the obligation to one Kind; None applies it to both Kinds. Kind scoping keeps
// implementation/build/test gates out of `software + research` without prose.
type ProfileGuardSpec =
    { Key: string
      Target: GuardTarget
      Checkpoint: GuardCheckpoint
      Requirement: EvidenceRequirement
      Applicability: ApplicabilityPolicy
      Waiver: WaiverPolicy
      TaskKind: Kind option }

type ProfilePolicy =
    { Guards: ProfileGuardSpec list
      RequiredSections: string list
      RoleDefaults: RoleDefault list }

type SemanticGuidance =
    { Common: string
      Research: string
      Execution: string }

type ProfileDefinition =
    { SchemaVersion: int
      Id: string
      Description: string
      IdPrefix: string option
      Routing: ProfileRouting
      CapabilityEnvelope: CapabilityEnvelope
      Policy: ProfilePolicy
      SemanticGuidance: SemanticGuidance }

// Built-in profiles are compiled in; project profiles/overlays are discovered
// from the project root. Origin records how the effective profile was formed.
type ProfileOrigin =
    | BuiltIn
    | Project
    | Overlay

type EffectiveProfile =
    { Definition: ProfileDefinition
      Fingerprint: string
      Origin: ProfileOrigin }

// Optional-section shapes for project overlay files: an absent field retains the
// shared value; a present field must not weaken it.
type CapabilityEnvelopeOverlay =
    { Required: string list option
      Default: string list option
      Allowed: string list option }

type ProfilePolicyOverlay =
    { Guards: ProfileGuardSpec list option
      RequiredSections: string list option
      RoleDefaults: RoleDefault list option }

type SemanticGuidanceOverlay =
    { Common: string option
      Research: string option
      Execution: string option }

type ProfileOverlay =
    { SchemaVersion: int
      Id: string
      Description: string option
      IdPrefix: string option
      Routing: ProfileRouting option
      CapabilityEnvelope: CapabilityEnvelopeOverlay option
      Policy: ProfilePolicyOverlay option
      SemanticGuidance: SemanticGuidanceOverlay option }

// Section 22: one explicit operation-based patch model. Runtime never judges
// fuzzy textual materiality; every mutation is a typed operation.
type AcceptanceDraft =
    { Id: string
      Text: string }

[<RequireQualifiedAccess>]
type ContractPatch =
    | AddAcceptanceCriterion of AcceptanceDraft
    | AddGuard of GuardSpec
    | RemoveGuard of string
    | UpdateAcceptanceCriterion of string * string
    | RemoveAcceptanceCriterion of string
    | SetObjective of string
    | SetScope of string
    | SetNonGoals of string

// Section 23: reconciliation is not a special authority path. Restoring the
// recorded canonical contract is Coordinator-authorized and fingerprint-verified;
// accepting an out-of-band rewrite is a User-authorized material change.
[<RequireQualifiedAccess>]
type ReconciliationPlan =
    | RestoreCanonicalContract of ContractContent
    | AcceptExternalContract

// Section 30: durable logical work ownership. Role is the stable responsibility;
// AgentId is the optional concrete identity used to prove independent production.
// Provider/session/instance identities are never persisted as authority.
type Owner =
    { Role: string
      AgentId: string option }

// Section 5.2: reopening is explicit and must name the state it invalidates.
// Qualified access keeps these cases from colliding with the GuardTarget type
// and its WorkItemTarget case.
[<RequireQualifiedAccess>]
type ReopenTarget =
    | AcceptanceCriterionTarget of string
    | WorkItemTarget of string
    | GuardTarget of string

// Section 27.1: terminal completion handoff prose, structurally validated only.
type TerminalHandoff =
    { State: string
      EvidenceSummary: string
      Next: string }

// Section 9: Decisions are first-class provenance entities with stable IDs and
// typed authorization targets. This increment records them as domain objects
// only: nothing is authorized by a Decision yet, ordinary input is
// Coordinator-only, and trusted User/ProfilePolicy provenance is deferred.
type DecisionAuthority =
    | User
    | Coordinator
    | ProfilePolicy

type DecisionKind =
    | UserDecision
    | DesignDecision
    | Assumption
    | PolicyApplication
    | ContractRevision
    | ApplicabilityDecision
    | WaiverDecision

type DecisionTarget =
    | WaiveAcceptanceTarget of string
    | GuardDispositionTarget of string
    | SkipWorkItemTarget of string
    | RequirementChangeTarget of string
    | ReopenTaskTarget of string * ReopenTarget list
    | ContractPatchTarget of string
    | QuestionResolutionTarget of string
    | ReclassificationTarget of string
    | OtherDecisionTarget of string

// Caller input; the runtime assigns Id/Authority/CreatedAt when the Decision is created.
type DecisionDraft =
    { Kind: DecisionKind
      Targets: DecisionTarget list
      Rationale: string }

type Decision =
    { Id: string
      Authority: DecisionAuthority
      Kind: DecisionKind
      Targets: DecisionTarget list
      Rationale: string
      CreatedAt: DateTimeOffset
      ConfirmationRef: string option }

type DecisionRef = DecisionRef of string

// Fail-closed authority remediation until the trusted User-authority ingress
// exists: a blocked operation reports the exact typed authorization it needs so
// callers do not probe with add-decision, which only creates Coordinator authority.
[<RequireQualifiedAccess>]
type DecisionRefStatus =
    | Absent
    | Mismatched
    | Present

type AuthorityMetadata =
    { RequiredAuthority: MinimumAuthority
      Operation: string
      DecisionKind: DecisionKind option
      Target: DecisionTarget option
      DecisionRefStatus: DecisionRefStatus }

// Section 5.2: reopening request. Targets are non-empty and validated against the
// task; authority follows the source lifecycle (Complete -> Coordinator/User,
// Aborted -> User).
type ReopenRequest =
    { Reason: string
      DecisionRef: DecisionRef option
      Targets: ReopenTarget list }

// Section 17.5/25.1: reclassification changes Kind and/or Profile while
// preserving history. Both fields are optional; at least one must change.
type ReclassificationRequest =
    { Kind: Kind option
      Profile: string option
      Reason: string }

// Section 10: Questions are blocking domain entities. TaskWide questions stop
// every pending WorkItem and task completion; WorkItems-scoped questions stop
// only the referenced WorkItems.
type QuestionImpact =
    | TaskWide
    | WorkItems of string list

type QuestionState =
    | Open
    | Resolved of DecisionRef

type QuestionDraft =
    { Id: string
      Text: string
      Impact: QuestionImpact }

type Question =
    { Id: string
      Text: string
      Impact: QuestionImpact
      State: QuestionState }

// Persisted WorkItem states; ResumeCondition/Blocker/SkipDisposition travel with
// the state instead of living as separate WorkItem fields.
type SkipDisposition =
    | Optional
    | NotApplicable
    | Waived

type ResumeCondition = ResumeCondition of string
type Blocker = Blocker of string
type ObservationRef = ObservationRef of string

type WorkItemState =
    | PendingWork
    | ActiveWork
    | WaitingWork of ResumeCondition
    | BlockedWork of Blocker
    | DoneWork
    | SkippedWork of SkipDisposition

// Readiness is derived, never persisted.
type ReadinessReason =
    | WorkItemNotPending
    | UnmetDependency of string
    | UnmetGuard of string
    | OpenQuestion of string

type Readiness =
    | Ready
    | NotReady of ReadinessReason list

type WorkItem =
    { Id: string
      Title: string
      State: WorkItemState
      Result: string option
      AcceptanceRefs: string list
      DependsOn: string list
      // Durable logical owner; None inherits the nearest ancestor's owner.
      Owner: Owner option
      // Valid Evidence explicitly attached by completion; scope for WorkItemTarget Guards.
      EvidenceRefs: string list
      Children: WorkItem list }

// Recursive create-time shape; DependsOn/Children are same-task only.
type WorkItemSpec =
    { Id: string
      Title: string
      DependsOn: string list
      Children: WorkItemSpec list }

type TaskModel =
    { Id: string
      Title: string
      Created: DateTimeOffset
      Kind: Kind
      Profile: string
      ProfileFingerprint: string
      // Section 21/23: contract-critical semantic prose plus the baseline anchor.
      Objective: string
      Scope: string
      NonGoals: string
      ContractState: ContractState
      ContractFingerprint: string
      ContractRevision: int
      StateRevision: int
      Lifecycle: string
      Evidence: EvidenceRecord list
      AcceptanceCriteria: Acceptance list
      Guards: Guard list
      // Section 15.5: stable key provenance for ProfileMaterialized Guards. Maps a
      // materialized Guard.Id to the ProfileGuardSpec.Key that produced it so sync
      // preserves/disposes by key instead of transferring identity between
      // same-content keys. Task-design guards have no entry.
      ProfileGuardKeys: Map<string, string>
      Decisions: Decision list
      Questions: Question list
      WorkItems: WorkItem list
      // Section 27.1: current terminal handoff; superseded handoffs are retained
      // in CompletionHistory so a reopen never deletes completion prose.
      TerminalHandoff: TerminalHandoff option
      CompletionHistory: TerminalHandoff list }

type WorkItemCompletion =
    { Result: string
      EvidenceRefs: string list }

type TaskCommand =
    | StartWorkItem of string
    | CompleteWorkItem of string * WorkItemCompletion
    | WaitWorkItem of string * ResumeCondition
    | BlockWorkItem of string * Blocker
    | ResumeWorkItem of string * ObservationRef option
    | RebindOwner of string * Owner * string
    | AddEvidence of Evidence
    | SupersedeEvidence of string * string
    | VerifyAcceptanceCriterion of string * string list
    | AddGuard of GuardSpec
    // AddGuard carries the caller's GuardSpec; GuardAdded is the decide-produced
    // event carrying the validated Guard (Origin/Disposition assigned by runtime).
    | GuardAdded of Guard
    // Section 25: guard disposition commands carry the optional pre-existing
    // DecisionRef; GuardDispositionSet is the decide-produced event form.
    | MarkGuardNotApplicable of string * DecisionRef option
    | WaiveGuard of string * DecisionRef option
    | GuardDispositionSet of string * GuardDisposition
    // Decisions are provenance-only in this slice; AddDecision assigns the
    // Coordinator authority and the decision ID.
    | AddDecision of DecisionDraft
    | DecisionAdded of Decision
    | AddQuestion of QuestionDraft
    | QuestionOpened of Question
    | ResolveQuestion of string * DecisionRef
    | QuestionResolved of string * DecisionRef
    // Section 30: owner rebinding event; produced by decide from RebindOwner.
    | OwnerRebound of string * Owner
    // Section 5.2: targeted reopen command; decide normalizes it into TaskReopened.
    | ReopenTask of ReopenRequest
    // Section 5.2: targeted reopen event; produced by decide from ReopenTask.
    | TaskReopened of ReopenRequest
    // Section 21.2: baseline event emitted immediately before the first material
    // StartWorkItem; carries the freshly recorded contract fingerprint.
    | ContractBaselined of string
    // Section 22: post-baseline contract patches may carry an optional
    // pre-existing target-bound DecisionRef; ContractPatchApplied is the
    // decide-produced event. The bool records whether the patch was baselined
    // (and therefore created contractRevision history).
    | ApplyContractPatch of ContractPatch * DecisionRef option
    | ContractPatchApplied of ContractPatch * bool
    // Section 23: drift reconciliation is converted into ordinary authority
    // rules rather than bypassing them.
    | ReconcileContractDrift of ReconciliationPlan
    | ContractDriftReconciled of ReconciliationPlan
    // Sections 17.5/20: profile selection/reclassification and profile drift
    // reconciliation. ReclassifyTask/ReconcileProfileDrift are command forms;
    // TaskReclassified/ProfileDriftReconciled are the decide-produced events.
    | ReclassifyTask of ReclassificationRequest
    | TaskReclassified of Kind * string * string
    | ReconcileProfileDrift
    | ProfileDriftReconciled of string
    // The complete resulting ProfileMaterialized guard set for the current
    // Effective Profile, each paired with its persisted key provenance; evolve
    // replaces only profile-materialized guards and refreshes the key map.
    | ProfileGuardsSynced of MaterializedGuard list
    | CompleteTask of TerminalHandoff

type RuntimeError =
    | InvalidInput of string
    | NotFound of string
    | Conflict of expected: int * actual: int
    | InvalidTransition of string
    | PersistenceFailure of string
    // Carries the exact rendered message so the text surface is unchanged while
    // the transport can publish the structured remediation block.
    | AuthorityDenied of metadata: AuthorityMetadata * message: string

// Extracts the structured authority remediation from a runtime error.
let tryAuthorityMetadata (error: RuntimeError) : AuthorityMetadata option =
    match error with
    | AuthorityDenied (metadata, _) -> Some metadata
    | _ -> None

type EvidenceDto =
    { Id: string
      Kind: string
      Source: string
      Subject: string
      ProducerRole: string
      ProducerId: string
      Reference: string
      Summary: string
      Validity: string
      SupersededReason: string }

type AcceptanceDto =
    { Id: string
      Text: string
      State: string
      EvidenceRefs: string list }

// Section 30: owner is an optional nested object; absent means inherit.
type OwnerDto =
    { Role: string
      AgentId: string }

// Section 27.1: terminal handoff wire shape.
type TerminalHandoffDto =
    { State: string
      EvidenceSummary: string
      Next: string }

type WorkItemDto =
    { Id: string
      Title: string
      State: string
      Result: string
      AcceptanceRefs: string list
      DependsOn: string list
      EvidenceRefs: string list
      ResumeCondition: string
      Blocker: string
      SkipDisposition: string
      Owner: OwnerDto option
      Children: WorkItemDto list }

// Guards are serialized with flat, strictly-parsed policy strings so the wire
// contract stays DTO-shaped and never exposes the domain DUs directly.
type GuardDto =
    { Id: string
      Target: string
      Checkpoint: string
      Origin: string
      Requirement: string
      EvidenceKind: string
      MinimumCount: int
      ProducerRole: string
      RequireIndependentProducer: bool
      Applicability: string
      Waiver: string
      Disposition: string }

// Decisions and Questions use the same flat-string wire style as Guards.
type DecisionDto =
    { Id: string
      Authority: string
      Kind: string
      Targets: string list
      Rationale: string
      CreatedAt: string
      ConfirmationRef: string }

type QuestionDto =
    { Id: string
      Text: string
      Impact: string
      WorkItems: string list
      State: string
      Resolution: string }

type TaskDto =
    { SchemaVersion: int
      Id: string
      Title: string
      Created: string
      Kind: string
      Profile: string
      ProfileFingerprint: string
      Objective: string
      Scope: string
      NonGoals: string
      ContractState: string
      ContractFingerprint: string
      ContractRevision: int
      StateRevision: int
      Lifecycle: string
      Evidence: EvidenceDto list
      AcceptanceCriteria: AcceptanceDto list
      Guards: GuardDto list
      ProfileGuardKeys: (string * string) list
      Decisions: DecisionDto list
      Questions: QuestionDto list
      WorkItems: WorkItemDto list
      TerminalHandoff: TerminalHandoffDto option
      CompletionHistory: TerminalHandoffDto list }

type CreateRequest =
    { Id: string
      Title: string
      Kind: Kind
      AcceptanceCriteria: (string * string) list
      WorkItems: WorkItemSpec list }

// Section 9.1 is fail-closed until #13 supplies an adapter-issued signed
// attestation: ordinary runtime and CLI invocation is Coordinator-only. There is
// no in-process User ingress (no receipt factory, no authority parameter, no
// flag/environment input), so a User-required operation cannot be authorized
// here. Untrusted sidecar Decisions claiming User/ProfilePolicy provenance are
// rejected at the DTO/domain boundary rather than trusted.

let private idRegex = Regex("^[A-Za-z]+-[0-9]+$", RegexOptions.CultureInvariant)
let private acceptanceIdRegex = Regex("^AC[0-9]+$", RegexOptions.CultureInvariant)
let private workItemIdRegex = Regex("^W[0-9]+(\\.[0-9]+)*$", RegexOptions.CultureInvariant)
let private evidenceIdRegex = Regex("^E[0-9]+$", RegexOptions.CultureInvariant)
let private guardIdRegex = Regex("^G[0-9]+$", RegexOptions.CultureInvariant)
let private decisionIdRegex = Regex("^D[0-9]+$", RegexOptions.CultureInvariant)
let private questionIdRegex = Regex("^Q[0-9]+$", RegexOptions.CultureInvariant)
let private profileIdRegex = Regex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)
let private capabilityIdRegex = Regex("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)

let private errorMessage error =
    match error with
    | InvalidInput message -> message
    | NotFound message -> message
    | Conflict (expected, actual) -> $"state revision conflict: expected {expected}, actual {actual}"
    | InvalidTransition message -> message
    | PersistenceFailure message -> message
    | AuthorityDenied (_, message) -> message

let private nonEmpty name value =
    if String.IsNullOrWhiteSpace value || value.Contains '\r' || value.Contains '\n' then
        Error (InvalidInput $"{name} must be a non-empty single line")
    else
        Ok(value.Trim())

let private validateId (name: string) (regex: Regex) (value: string) =
    match nonEmpty name value with
    | Error error -> Error error
    | Ok valid when regex.IsMatch valid -> Ok valid
    | Ok _ -> Error (InvalidInput $"{name} has an invalid format")

let private kindToString (kind: Kind) =
    match kind with
    | Execution -> "execution"
    | Research -> "research"

let private parseKind (value: string) : Result<Kind, RuntimeError> =
    match value with
    | "execution" -> Ok Execution
    | "research" -> Ok Research
    | _ -> Error (InvalidInput $"kind must be 'execution' or 'research'")

let private contractStateToString state =
    match state with
    | Draft -> "draft"
    | Baselined -> "baselined"

let private parseContractState (value: string) : Result<ContractState, RuntimeError> =
    match value with
    | "draft" -> Ok Draft
    | "baselined" -> Ok Baselined
    | _ -> Error (InvalidInput "contract state must be 'draft' or 'baselined'")

let private evidenceKindToString (kind: EvidenceKind) =
    match kind with
    | EvidenceKind.Build -> "build"
    | EvidenceKind.Test -> "test"
    | EvidenceKind.Review -> "review"
    | EvidenceKind.Observation -> "observation"
    | EvidenceKind.ExternalEffect -> "externalEffect"
    | EvidenceKind.Research -> "research"
    | EvidenceKind.DecisionEvidence -> "decisionEvidence"
    | EvidenceKind.Other value -> $"other:{value}"

let parseEvidenceKind (value: string) : Result<EvidenceKind, RuntimeError> =
    match value with
    | "build" -> Ok EvidenceKind.Build
    | "test" -> Ok EvidenceKind.Test
    | "review" -> Ok EvidenceKind.Review
    | "observation" -> Ok EvidenceKind.Observation
    | "externalEffect" -> Ok EvidenceKind.ExternalEffect
    | "research" -> Ok EvidenceKind.Research
    | "decisionEvidence" -> Ok EvidenceKind.DecisionEvidence
    | _ when value.StartsWith("other:", StringComparison.Ordinal) ->
        let custom = value.Substring("other:".Length)

        if String.IsNullOrWhiteSpace custom then
            Error (InvalidInput "evidence kind 'other' requires a non-empty value")
        else
            Ok(EvidenceKind.Other(custom.Trim()))
    | _ -> Error (InvalidInput "evidence kind is not recognized")

let private evidenceSourceToString (EvidenceSource value) = value

let private resumeConditionToString (ResumeCondition value) = value
let private blockerToString (Blocker value) = value

let private skipDispositionToString disposition =
    match disposition with
    | Optional -> "optional"
    | NotApplicable -> "notApplicable"
    | Waived -> "waived"

let private parseSkipDisposition value =
    match value with
    | "optional" -> Ok Optional
    | "notApplicable" -> Ok NotApplicable
    | "waived" -> Ok Waived
    | _ -> Error (InvalidInput "work item skip disposition is not recognized")

let private lifecycleToString lifecycle = lifecycle

let private taskId value = validateId "task id" idRegex value
let private acceptanceId value = validateId "acceptance id" acceptanceIdRegex value
let private workItemId value = validateId "work item id" workItemIdRegex value
let private evidenceId value = validateId "evidence id" evidenceIdRegex value
let private guardId value = validateId "guard id" guardIdRegex value
let private decisionId value = validateId "decision id" decisionIdRegex value
let private questionId value = validateId "question id" questionIdRegex value
let private profileId value = validateId "profile id" profileIdRegex value
let private capabilityId value = validateId "capability id" capabilityIdRegex value

let minimumAuthorityToString authority =
    match authority with
    | MinimumAuthority.CoordinatorAuthority -> "coordinator"
    | MinimumAuthority.UserAuthority -> "user"

let decisionRefStatusToString status =
    match status with
    | DecisionRefStatus.Absent -> "absent"
    | DecisionRefStatus.Mismatched -> "mismatched"
    | DecisionRefStatus.Present -> "present"

let private parseMinimumAuthority (value: string) : Result<MinimumAuthority, RuntimeError> =
    match value with
    | "coordinator" -> Ok MinimumAuthority.CoordinatorAuthority
    | "user" -> Ok MinimumAuthority.UserAuthority
    | _ -> Error (InvalidInput "minimum authority is not recognized")

let private guardTargetToString target =
    match target with
    | TaskTarget -> "task"
    | WorkItemTarget id -> $"workItem:{id}"

let parseGuardTarget (value: string) : Result<GuardTarget, RuntimeError> =
    if value = "task" then
        Ok TaskTarget
    elif value.StartsWith("workItem:", StringComparison.Ordinal) then
        value.Substring("workItem:".Length) |> workItemId |> Result.map WorkItemTarget
    else
        Error (InvalidInput "guard target must be 'task' or 'workItem:<W-id>'")

let private guardCheckpointToString checkpoint =
    match checkpoint with
    | BeforeStart -> "beforeStart"
    | BeforeComplete -> "beforeComplete"

let parseGuardCheckpoint (value: string) : Result<GuardCheckpoint, RuntimeError> =
    match value with
    | "beforeStart" -> Ok BeforeStart
    | "beforeComplete" -> Ok BeforeComplete
    | _ -> Error (InvalidInput "guard checkpoint is not recognized")

let private guardOriginToString origin =
    match origin with
    | ProfileMaterialized -> "profileMaterialized"
    | TaskDesign -> "taskDesign"

let private parseGuardOrigin (value: string) : Result<GuardOrigin, RuntimeError> =
    match value with
    | "profileMaterialized" -> Ok ProfileMaterialized
    | "taskDesign" -> Ok TaskDesign
    | _ -> Error (InvalidInput "guard origin is not recognized")

let private applicabilityToString policy =
    match policy with
    | Always -> "always"
    | ExplicitDecision authority -> $"explicitDecision:{minimumAuthorityToString authority}"

let parseApplicability (value: string) : Result<ApplicabilityPolicy, RuntimeError> =
    if value = "always" then
        Ok Always
    elif value.StartsWith("explicitDecision:", StringComparison.Ordinal) then
        value.Substring("explicitDecision:".Length)
        |> parseMinimumAuthority
        |> Result.map ExplicitDecision
    else
        Error (InvalidInput "guard applicability must be 'always' or 'explicitDecision:<authority>'")

let private waiverToString policy =
    match policy with
    | NotWaivable -> "notWaivable"
    | WaivableBy authority -> $"waivableBy:{minimumAuthorityToString authority}"

let parseWaiver (value: string) : Result<WaiverPolicy, RuntimeError> =
    if value = "notWaivable" then
        Ok NotWaivable
    elif value.StartsWith("waivableBy:", StringComparison.Ordinal) then
        value.Substring("waivableBy:".Length)
        |> parseMinimumAuthority
        |> Result.map WaivableBy
    else
        Error (InvalidInput "guard waiver must be 'notWaivable' or 'waivableBy:<authority>'")

let private guardDispositionToString disposition =
    match disposition with
    | GuardDisposition.Applicable -> "applicable"
    | GuardDisposition.NotApplicable reference -> $"notApplicable:{reference}"
    | GuardDisposition.Waived reference -> $"waived:{reference}"

let private parseGuardDisposition (value: string) : Result<GuardDisposition, RuntimeError> =
    if value = "applicable" then
        Ok GuardDisposition.Applicable
    elif value.StartsWith("notApplicable:", StringComparison.Ordinal) then
        value.Substring("notApplicable:".Length)
        |> decisionId
        |> Result.map GuardDisposition.NotApplicable
    elif value.StartsWith("waived:", StringComparison.Ordinal) then
        value.Substring("waived:".Length)
        |> decisionId
        |> Result.map GuardDisposition.Waived
    else
        Error (InvalidInput "guard disposition must be 'applicable', 'notApplicable:<ref>', or 'waived:<ref>'")

let private decisionAuthorityToString authority =
    match authority with
    | User -> "user"
    | Coordinator -> "coordinator"
    | ProfilePolicy -> "profilePolicy"

let private parseDecisionAuthority (value: string) : Result<DecisionAuthority, RuntimeError> =
    match value with
    | "user" -> Ok User
    | "coordinator" -> Ok Coordinator
    | "profilePolicy" -> Ok ProfilePolicy
    | _ -> Error (InvalidInput "decision authority is not recognized")

let decisionKindToString kind =
    match kind with
    | UserDecision -> "userDecision"
    | DesignDecision -> "designDecision"
    | Assumption -> "assumption"
    | PolicyApplication -> "policyApplication"
    | ContractRevision -> "contractRevision"
    | ApplicabilityDecision -> "applicabilityDecision"
    | WaiverDecision -> "waiverDecision"

let parseDecisionKind (value: string) : Result<DecisionKind, RuntimeError> =
    match value with
    | "userDecision" -> Ok UserDecision
    | "designDecision" -> Ok DesignDecision
    | "assumption" -> Ok Assumption
    | "policyApplication" -> Ok PolicyApplication
    | "contractRevision" -> Ok ContractRevision
    | "applicabilityDecision" -> Ok ApplicabilityDecision
    | "waiverDecision" -> Ok WaiverDecision
    | _ -> Error (InvalidInput "decision kind is not recognized")

let private reopenTargetToString target =
    match target with
    | ReopenTarget.AcceptanceCriterionTarget id -> $"acceptance:{id}"
    | ReopenTarget.WorkItemTarget id -> $"workItem:{id}"
    | ReopenTarget.GuardTarget id -> $"guard:{id}"

let decisionTargetToString target =
    match target with
    | WaiveAcceptanceTarget id -> $"waiveAcceptance:{id}"
    | GuardDispositionTarget id -> $"guardDisposition:{id}"
    | SkipWorkItemTarget id -> $"skipWorkItem:{id}"
    | RequirementChangeTarget id -> $"requirementChange:{id}"
    | ContractPatchTarget id -> $"contractPatch:{id}"
    | ReopenTaskTarget (taskIdText, targets) ->
        let encoded = targets |> List.map reopenTargetToString |> String.concat ";"
        $"reopenTask:{taskIdText}|{encoded}"
    | QuestionResolutionTarget id -> $"questionResolution:{id}"
    | ReclassificationTarget text -> $"reclassify:{text}"
    | OtherDecisionTarget text -> $"other:{text}"

// W4/AC19: structured authority remediation for User-required or
// DecisionRef-bound operations. The block is additive to the error envelope so
// text-only errors render without it; the four render helpers above are the
// single source of the wire vocabulary.
let renderAuthorityMetadata (metadata: AuthorityMetadata) : JsonNode =
    let authority = JsonObject()

    authority["required"] <-
        (JsonValue.Create(minimumAuthorityToString metadata.RequiredAuthority) :> JsonNode)

    authority["operation"] <- (JsonValue.Create metadata.Operation :> JsonNode)

    authority["decisionKind"] <-
        match metadata.DecisionKind with
        | Some kind -> (JsonValue.Create(decisionKindToString kind) :> JsonNode)
        | None -> (null : JsonNode)

    authority["target"] <-
        match metadata.Target with
        | Some target -> (JsonValue.Create(decisionTargetToString target) :> JsonNode)
        | None -> (null : JsonNode)

    authority["decisionRefStatus"] <-
        (JsonValue.Create(decisionRefStatusToString metadata.DecisionRefStatus) :> JsonNode)

    authority :> JsonNode

// Reopen targets are encoded as 'acceptance:<AC-id>', 'workItem:<W-id>', or
// 'guard:<G-id>'; the domain validates id format/existence.
let parseReopenTarget (value: string) : Result<ReopenTarget, RuntimeError> =
    if value.StartsWith("acceptance:", StringComparison.Ordinal) then
        Ok(ReopenTarget.AcceptanceCriterionTarget(value.Substring("acceptance:".Length).Trim()))
    elif value.StartsWith("workItem:", StringComparison.Ordinal) then
        Ok(ReopenTarget.WorkItemTarget(value.Substring("workItem:".Length).Trim()))
    elif value.StartsWith("guard:", StringComparison.Ordinal) then
        Ok(ReopenTarget.GuardTarget(value.Substring("guard:".Length).Trim()))
    else
        Error (InvalidInput "reopen target must be 'acceptance:<AC-id>', 'workItem:<W-id>', or 'guard:<G-id>'")

let private parseReopenTargets (value: string) =
    let parts =
        value.Split(';')
        |> Array.toList
        |> List.map (fun item -> item.Trim())
        |> List.filter (fun item -> item <> "")

    if parts.IsEmpty then
        Error (InvalidInput "reopen decision target requires at least one invalidation target")
    else
        parts
        |> List.fold
            (fun state item ->
                result {
                    let! values = state
                    let! parsed = parseReopenTarget item
                    return parsed :: values
                })
            (Ok [])
        |> Result.map List.rev

// Parses the flat target string; semantic validation (id format/existence) is
// performed by the domain so command and wire inputs share one validator.
let parseDecisionTarget (value: string) : Result<DecisionTarget, RuntimeError> =
    let suffix (prefix: string) =
        value.Substring(prefix.Length).Trim()

    if value.StartsWith("waiveAcceptance:", StringComparison.Ordinal) then
        Ok(WaiveAcceptanceTarget(suffix "waiveAcceptance:"))
    elif value.StartsWith("guardDisposition:", StringComparison.Ordinal) then
        Ok(GuardDispositionTarget(suffix "guardDisposition:"))
    elif value.StartsWith("skipWorkItem:", StringComparison.Ordinal) then
        Ok(SkipWorkItemTarget(suffix "skipWorkItem:"))
    elif value.StartsWith("requirementChange:", StringComparison.Ordinal) then
        Ok(RequirementChangeTarget(suffix "requirementChange:"))
    elif value.StartsWith("contractPatch:", StringComparison.Ordinal) then
        Ok(ContractPatchTarget(suffix "contractPatch:"))
    elif value.StartsWith("reopenTask:", StringComparison.Ordinal) then
        let rest = value.Substring("reopenTask:".Length)

        match rest.Split([| '|' |], 2) with
        | [| taskIdText; targetsText |] ->
            parseReopenTargets targetsText
            |> Result.map (fun targets -> ReopenTaskTarget(taskIdText.Trim(), targets))
        | _ ->
            Error (InvalidInput "reopenTask decision target must be 'reopenTask:<taskId>|<targets>'")
    elif value.StartsWith("questionResolution:", StringComparison.Ordinal) then
        Ok(QuestionResolutionTarget(suffix "questionResolution:"))
    elif value.StartsWith("reclassify:", StringComparison.Ordinal) then
        Ok(ReclassificationTarget(suffix "reclassify:"))
    elif value.StartsWith("other:", StringComparison.Ordinal) then
        Ok(OtherDecisionTarget(suffix "other:"))
    else
        Error (InvalidInput "decision target is not recognized")

let private questionImpactToString impact =
    match impact with
    | TaskWide -> "taskWide"
    | WorkItems _ -> "workItems"

let private questionStateToString state =
    match state with
    | Open -> "open"
    | Resolved _ -> "resolved"

// CLI-friendly impact parser; the domain validates the referenced WorkItems.
let parseQuestionImpact (value: string) : Result<QuestionImpact, RuntimeError> =
    if value = "taskWide" then
        Ok TaskWide
    elif value.StartsWith("workItems:", StringComparison.Ordinal) then
        let ids =
            value.Substring("workItems:".Length).Split(',')
            |> Array.toList
            |> List.map (fun item -> item.Trim())
            |> List.filter (fun item -> item <> "")

        if ids.IsEmpty then
            Error (InvalidInput "question impact 'workItems' requires at least one WorkItem")
        else
            Ok(WorkItems ids)
    else
        Error (InvalidInput "question impact must be 'taskWide' or 'workItems:<W-id,...>'")

// Section 23: the canonical contract fingerprint covers Objective/Scope/Non-Goals
// and Acceptance-Criterion IDs/text in a deterministic order. Length-prefixed
// segments keep the canonical form unambiguous without escaping rules.
let private segment (value: string) = $"{value.Length}:{value}"

let private canonicalContract (content: ContractContent) =
    let parts =
        [ "contract-v1"
          content.Objective
          content.Scope
          content.NonGoals
          string content.AcceptanceCriteria.Length ]
        @ (content.AcceptanceCriteria |> List.collect (fun (id, text) -> [ id; text ]))

    parts |> List.map segment |> String.concat "|"

let private sha256Hex (value: string) =
    use algorithm = SHA256.Create()

    value
    |> Encoding.UTF8.GetBytes
    |> algorithm.ComputeHash
    |> Convert.ToHexString
    |> fun hex -> hex.ToLowerInvariant()

// Section 23: the fingerprint is over the canonicalized contract content, so any
// out-of-band rewrite of prose or AC text changes it while AC state does not.
let contractFingerprintOf (content: ContractContent) = sha256Hex (canonicalContract content)

let contractContentOf (task: TaskModel) : ContractContent =
    { Objective = task.Objective
      Scope = task.Scope
      NonGoals = task.NonGoals
      AcceptanceCriteria =
        task.AcceptanceCriteria |> List.map (fun criterion -> criterion.Id, criterion.Text) }

// Section 23: drift is only meaningful once a baseline fingerprint is recorded.
let contractDrift (task: TaskModel) : bool =
    match task.ContractState with
    | Draft -> false
    | Baselined -> contractFingerprintOf (contractContentOf task) <> task.ContractFingerprint

// Section 9.6/22.2: a post-baseline patch is authorized against a deterministic ID
// derived from its exact canonical payload, not from the revision it will create.
let private canonicalGuardSpec (spec: GuardSpec) =
    let kind, minimumCount, producerRole, independent =
        match spec.Requirement with
        | EvidenceRequired requirement ->
            evidenceKindToString requirement.Kind,
            string requirement.MinimumCount,
            requirement.ProducerRole |> Option.defaultValue "",
            string requirement.RequireIndependentProducer

    [ "guard-v1"
      spec.Id
      guardTargetToString spec.Target
      guardCheckpointToString spec.Checkpoint
      kind
      minimumCount
      producerRole
      independent
      applicabilityToString spec.Applicability
      waiverToString spec.Waiver ]
    |> List.map segment
    |> String.concat "|"

let contractPatchIdOf (patch: ContractPatch) =
    let parts =
        match patch with
        | ContractPatch.AddAcceptanceCriterion draft ->
            [ "addAcceptanceCriterion"; draft.Id; draft.Text ]
        | ContractPatch.AddGuard spec -> [ "addGuard"; canonicalGuardSpec spec ]
        | ContractPatch.RemoveGuard id -> [ "removeGuard"; id ]
        | ContractPatch.UpdateAcceptanceCriterion (id, text) ->
            [ "updateAcceptanceCriterion"; id; text ]
        | ContractPatch.RemoveAcceptanceCriterion id ->
            [ "removeAcceptanceCriterion"; id ]
        | ContractPatch.SetObjective text -> [ "setObjective"; text ]
        | ContractPatch.SetScope text -> [ "setScope"; text ]
        | ContractPatch.SetNonGoals text -> [ "setNonGoals"; text ]

    parts |> List.map segment |> String.concat "|" |> sha256Hex

// Section 23: reconciliation is authorized against a deterministic plan ID so the
// exact restore/accept payload is the security target, not the resulting revision.
let private contractReconciliationId (plan: ReconciliationPlan) =
    match plan with
    | ReconciliationPlan.RestoreCanonicalContract content -> "restore-" + contractFingerprintOf content
    | ReconciliationPlan.AcceptExternalContract -> "accept-external"

let private collectResults values =
    values
    |> List.fold
        (fun state item ->
            result {
                let! collected = state
                let! value = item
                return value :: collected
            })
        (Ok [])
    |> Result.map List.rev

let private optionalNonEmpty name value =
    match value with
    | None -> Ok None
    | Some text when String.IsNullOrWhiteSpace text -> Ok None
    | Some text -> nonEmpty name text |> Result.map Some

// Section 17/19: every effective profile uses the same content-derived
// fingerprint, including the built-in general profile.
let private canonicalProfileGuard (spec: ProfileGuardSpec) =
    [ "pguard-v1"
      spec.Key
      guardTargetToString spec.Target
      guardCheckpointToString spec.Checkpoint
      evidenceKindToString spec.Requirement.Kind
      string spec.Requirement.MinimumCount
      spec.Requirement.ProducerRole |> Option.defaultValue ""
      string spec.Requirement.RequireIndependentProducer
      applicabilityToString spec.Applicability
      waiverToString spec.Waiver
      spec.TaskKind |> Option.map kindToString |> Option.defaultValue "" ]

let private canonicalProfile (profile: ProfileDefinition) =
    let routing = profile.Routing
    let envelope = profile.CapabilityEnvelope
    let policy = profile.Policy
    let guidance = profile.SemanticGuidance

    [ "profile-v1"
      profile.Id
      profile.Description
      profile.IdPrefix |> Option.defaultValue ""
      string routing.Positive.Length ]
    @ routing.Positive
    @ [ string routing.Negative.Length ]
    @ routing.Negative
    @ [ string envelope.Required.Length ]
    @ envelope.Required
    @ [ string envelope.Default.Length ]
    @ envelope.Default
    @ [ string envelope.Allowed.Length ]
    @ envelope.Allowed
    @ [ string policy.Guards.Length ]
    @ (policy.Guards |> List.collect canonicalProfileGuard)
    @ [ string policy.RequiredSections.Length ]
    @ policy.RequiredSections
    @ [ string policy.RoleDefaults.Length ]
    @ (policy.RoleDefaults |> List.collect (fun role -> [ role.Purpose; role.Role ]))
    @ [ guidance.Common; guidance.Research; guidance.Execution ]
    |> List.map segment
    |> String.concat "|"
    |> sha256Hex

let private effectiveFingerprint (definition: ProfileDefinition) (origin: ProfileOrigin) =
    canonicalProfile definition

let private makeEffectiveProfile (definition: ProfileDefinition) (origin: ProfileOrigin) =
    { Definition = definition
      Fingerprint = effectiveFingerprint definition origin
      Origin = origin }

let private builtinGeneralDefinition: ProfileDefinition =
    { SchemaVersion = 1
      Id = GeneralProfileId
      Description = "Fallback profile with minimal specialization."
      IdPrefix = None
      Routing = { Positive = []; Negative = [] }
      CapabilityEnvelope = { Required = []; Default = []; Allowed = [] }
      Policy = { Guards = []; RequiredSections = []; RoleDefaults = [] }
      SemanticGuidance = { Common = ""; Research = ""; Execution = "" } }

// Section 17.2: the shared `software` profile owns repository/branch context
// and engineering conventions as guidance, and materializes the
// build/test/review obligations as structured execution Guards. Build and test
// are routine quality gates the Coordinator may mark not-applicable when the
// surface does not apply; review is fail-closed so that uncertain applicability
// retains the requirement (only trusted User authority may drop it). TaskKind
// keeps every implementation gate out of `software + research`.
let private builtinSoftwareDefinition: ProfileDefinition =
    let executionGuard key evidenceKind producerRole applicability waiver : ProfileGuardSpec =
        { Key = key
          Target = TaskTarget
          Checkpoint = BeforeComplete
          Requirement =
            { Kind = evidenceKind
              MinimumCount = 1
              ProducerRole = Some producerRole
              RequireIndependentProducer = false }
          Applicability = applicability
          Waiver = waiver
          TaskKind = Some Execution }

    { SchemaVersion = 1
      Id = SoftwareProfileId
      Description =
        "Shared software profile: repository context, engineering conventions, and risk-driven build/test/review obligations."
      IdPrefix = None
      Routing =
        { Positive =
            [ "software"
              "code"
              "repository"
              "branch"
              "build"
              "test"
              "refactor"
              "bug" ]
          Negative = [] }
      CapabilityEnvelope =
        { Required = []
          Default = []
          Allowed =
            [ "dotnet"
              "rust"
              "database"
              "devops"
              "security"
              "performance"
              "audit" ] }
      Policy =
        { Guards =
            [ executionGuard
                  "build"
                  EvidenceKind.Build
                  "engineer"
                  (ExplicitDecision MinimumAuthority.CoordinatorAuthority)
                  (WaivableBy MinimumAuthority.UserAuthority)
              executionGuard
                  "test"
                  EvidenceKind.Test
                  "tester"
                  (ExplicitDecision MinimumAuthority.CoordinatorAuthority)
                  (WaivableBy MinimumAuthority.UserAuthority)
              executionGuard
                  "review"
                  EvidenceKind.Review
                  "reviewer"
                  (ExplicitDecision MinimumAuthority.UserAuthority)
                  (WaivableBy MinimumAuthority.UserAuthority) ]
          RequiredSections = []
          RoleDefaults =
            [ { Purpose = "implementation"; Role = "engineer" }
              { Purpose = "testing"; Role = "tester" }
              { Purpose = "review"; Role = "reviewer" }
              { Purpose = "architecture"; Role = "architect" } ] }
      SemanticGuidance =
        { Common =
            "Establish repository, branch, and HEAD/working-tree context only when the next software action depends on it; follow the repository's engineering conventions."
          Research =
            "Investigate the codebase read-only. Research does not import implementation, build, or test gates; experiments are evidence-gathering only when useful and authorized."
          Execution =
            "Engineer owns implementation and implementation-side build evidence; tester owns materially applicable test design, implementation, and execution; the reviewer stays independent and read-only. Build/test/review requirements are risk-driven structured Guards, and review is retained when applicability is uncertain. Execution complexity is distinct from architecture sensitivity: escalate to architecture only for contract, boundary, persistence, concurrency, security, or ownership impact. Debugging follows reproduce/evidence/hypothesis/falsification/fix/verify." } }

// Section 17.2: the shared `opencode` profile targets the harness itself. It
// references #2 capabilities/roles without redefining them and never grants
// permissions; its mandatory policy is structured Guards. Execution materializes
// validation and fail-closed review obligations; Research materializes a
// harness-investigation evidence expectation. TaskKind scopes each obligation to
// its Kind, so both Kinds carry meaningful policy without prose reminders.
let private builtinOpenCodeDefinition: ProfileDefinition =
    let executionGuard key evidenceKind producerRole applicability waiver : ProfileGuardSpec =
        { Key = key
          Target = TaskTarget
          Checkpoint = BeforeComplete
          Requirement =
            { Kind = evidenceKind
              MinimumCount = 1
              ProducerRole = Some producerRole
              RequireIndependentProducer = false }
          Applicability = applicability
          Waiver = waiver
          TaskKind = Some Execution }

    let researchGuard key evidenceKind : ProfileGuardSpec =
        { Key = key
          Target = TaskTarget
          Checkpoint = BeforeComplete
          Requirement =
            { Kind = evidenceKind
              MinimumCount = 1
              ProducerRole = None
              RequireIndependentProducer = false }
          Applicability = Always
          Waiver = NotWaivable
          TaskKind = Some Research }

    { SchemaVersion = 1
      Id = OpenCodeProfileId
      Description =
        "Shared OpenCode profile: harness architecture/context, cross-file semantic consistency, and instruction-authority obligations."
      IdPrefix = None
      Routing =
        { Positive =
            [ "opencode"
              "agent"
              "skill"
              "contract"
              "knowledge"
              "composition"
              "permission"
              "plugin"
              "tool"
              "mcp"
              "instruction"
              "markdown"
              "harness" ]
          Negative = [] }
      CapabilityEnvelope =
        { Required = []
          Default = []
          Allowed = [ "audit"; "dotnet"; "security"; "devops" ] }
      Policy =
        { Guards =
            [ executionGuard
                  "validation"
                  EvidenceKind.Test
                  "tester"
                  (ExplicitDecision MinimumAuthority.CoordinatorAuthority)
                  (WaivableBy MinimumAuthority.UserAuthority)
              executionGuard
                  "review"
                  EvidenceKind.Review
                  "reviewer"
                  (ExplicitDecision MinimumAuthority.UserAuthority)
                  (WaivableBy MinimumAuthority.UserAuthority)
              researchGuard "investigation" EvidenceKind.Research ]
          RequiredSections = []
          RoleDefaults =
            [ { Purpose = "architecture"; Role = "architect" }
              { Purpose = "review"; Role = "reviewer" } ] }
      SemanticGuidance =
        { Common =
            "Harness work targets OpenCode agents, skills, contracts, knowledge/composition, permissions, tools/plugins/MCP, model-facing Markdown, and cross-file semantic consistency; instruction authority and permission surfaces are high-impact."
          Research =
            "Investigate the current harness read-only and record version-sensitive runtime/config evidence before concluding."
          Execution =
            "Preserve #2 capability/role ownership and never grant permissions from Profile policy; keep instruction authority and composition consistent across files, and retain validation and independent review until their Guards are satisfied." } }

let private builtinProfiles =
    [ makeEffectiveProfile builtinGeneralDefinition BuiltIn
      makeEffectiveProfile builtinSoftwareDefinition BuiltIn
      makeEffectiveProfile builtinOpenCodeDefinition BuiltIn ]
    |> List.map (fun profile -> profile.Definition.Id, profile)
    |> Map.ofList

let private profileOriginToString origin =
    match origin with
    | BuiltIn -> "builtIn"
    | Project -> "project"
    | Overlay -> "overlay"

let private validateCapabilityList name values =
    result {
        let! valid = values |> List.map (validateId name capabilityIdRegex) |> collectResults

        if (Set.ofList valid).Count <> valid.Length then
            return! Error (InvalidInput $"{name} capabilities must be unique")

        return valid
    }

let private validateProfileGuardSpec (spec: ProfileGuardSpec) =
    result {
        let! key = nonEmpty "profile guard key" spec.Key

        match spec.Target with
        | WorkItemTarget _ ->
            return! Error (InvalidInput $"profile guard '{key}' cannot target a specific WorkItem")
        | TaskTarget -> ()

        let value = spec.Requirement

        let! kind =
            match value.Kind with
            | EvidenceKind.Other custom when String.IsNullOrWhiteSpace custom ->
                Error(InvalidInput "evidence kind 'other' requires a non-empty value")
            | kind -> Ok kind

        if value.MinimumCount < 1 then
            return! Error (InvalidInput "profile guard minimumCount must be at least 1")

        let! producerRole = optionalNonEmpty "profile guard producerRole" value.ProducerRole

        let requirement =
            { value with
                Kind = kind
                ProducerRole = producerRole }

        return { spec with Key = key; Requirement = requirement }
    }

let private validateProfileDefinition (profile: ProfileDefinition) =
    result {
        let! id = profileId profile.Id
        let! description = nonEmpty "profile description" profile.Description
        let! idPrefix = optionalNonEmpty "profile idPrefix" profile.IdPrefix
        let! required = validateCapabilityList "required" profile.CapabilityEnvelope.Required
        let! defaults = validateCapabilityList "default" profile.CapabilityEnvelope.Default
        let! allowed = validateCapabilityList "allowed" profile.CapabilityEnvelope.Allowed
        let! guards = profile.Policy.Guards |> List.map validateProfileGuardSpec |> collectResults

        if (guards |> List.map _.Key |> Set.ofList).Count <> guards.Length then
            return! Error (InvalidInput "profile guard keys must be unique")

        let! requiredSections =
            profile.Policy.RequiredSections |> List.map (nonEmpty "profile required section") |> collectResults

        if (Set.ofList requiredSections).Count <> requiredSections.Length then
            return! Error (InvalidInput "profile required sections must be unique")

        let! roleDefaults =
            profile.Policy.RoleDefaults
            |> List.map (fun role ->
                result {
                    let! purpose = nonEmpty "role default purpose" role.Purpose
                    let! roleName = nonEmpty "role default role" role.Role
                    return { Purpose = purpose; Role = roleName }
                })
            |> collectResults

        if (roleDefaults |> List.map _.Purpose |> Set.ofList).Count <> roleDefaults.Length then
            return! Error (InvalidInput "profile role default purposes must be unique")

        let! routingPositive =
            profile.Routing.Positive |> List.map (nonEmpty "profile routing trigger") |> collectResults

        let! routingNegative =
            profile.Routing.Negative |> List.map (nonEmpty "profile routing trigger") |> collectResults

        return
            { profile with
                Id = id
                Description = description
                IdPrefix = idPrefix
                Routing =
                    { Positive = routingPositive
                      Negative = routingNegative }
                CapabilityEnvelope =
                    { Required = required
                      Default = defaults
                      Allowed = allowed }
                Policy =
                    { Guards = guards
                      RequiredSections = requiredSections
                      RoleDefaults = roleDefaults } }
    }

let private unionList (baseList: string list) (additions: string list) =
    baseList @ (additions |> List.filter (fun value -> not (List.contains value baseList)))

let private applicabilityRank policy =
    match policy with
    | Always -> 2
    | ExplicitDecision MinimumAuthority.UserAuthority -> 1
    | ExplicitDecision MinimumAuthority.CoordinatorAuthority -> 0

let private waiverRank policy =
    match policy with
    | NotWaivable -> 2
    | WaivableBy MinimumAuthority.UserAuthority -> 1
    | WaivableBy MinimumAuthority.CoordinatorAuthority -> 0

let private strongerApplicability left right =
    if applicabilityRank left >= applicabilityRank right then left else right

let private strongerWaiver left right =
    if waiverRank left >= waiverRank right then left else right

// Section 19.2: an overlay may add or strengthen structured policy but may not
// mechanically weaken it; a weakening overlay fails resolution.
let private mergeProfileGuard (shared: ProfileGuardSpec) (overlay: ProfileGuardSpec) =
    result {
        if shared.Target <> overlay.Target then
            return! Error(InvalidInput $"profile guard '{shared.Key}' overlay changes its target")

        if shared.Checkpoint <> overlay.Checkpoint then
            return! Error(InvalidInput $"profile guard '{shared.Key}' overlay changes its checkpoint")

        if shared.Requirement.Kind <> overlay.Requirement.Kind then
            return! Error(InvalidInput $"profile guard '{shared.Key}' overlay changes its evidence kind")

        let! producerRole =
            match shared.Requirement.ProducerRole, overlay.Requirement.ProducerRole with
            | Some sharedRole, Some overlayRole when sharedRole <> overlayRole ->
                Error(InvalidInput $"profile guard '{shared.Key}' overlay changes its producer role")
            | Some sharedRole, _ -> Ok(Some sharedRole)
            | None, overlayRole -> Ok overlayRole

        if applicabilityRank overlay.Applicability < applicabilityRank shared.Applicability then
            return! Error(InvalidInput $"profile guard '{shared.Key}' overlay weakens its applicability")

        if waiverRank overlay.Waiver < waiverRank shared.Waiver then
            return! Error(InvalidInput $"profile guard '{shared.Key}' overlay weakens its waiver policy")

        return
            { shared with
                Requirement =
                    { shared.Requirement with
                        MinimumCount = max shared.Requirement.MinimumCount overlay.Requirement.MinimumCount
                        ProducerRole = producerRole
                        RequireIndependentProducer =
                            shared.Requirement.RequireIndependentProducer
                            || overlay.Requirement.RequireIndependentProducer }
                Applicability = strongerApplicability shared.Applicability overlay.Applicability
                Waiver = strongerWaiver shared.Waiver overlay.Waiver }
    }

let private mergeGuardLists (shared: ProfileGuardSpec list) (overlays: ProfileGuardSpec list) =
    overlays
    |> List.fold
        (fun state overlay ->
            result {
                let! current = state

                match current |> List.tryFind (fun guard -> guard.Key = overlay.Key) with
                | None -> return current @ [ overlay ]
                | Some existing ->
                    let! merged = mergeProfileGuard existing overlay

                    return
                        current
                        |> List.map (fun guard -> if guard.Key = overlay.Key then merged else guard)
            })
        (Ok shared)

let private mergeProfileDefinition (shared: ProfileDefinition) (overlay: ProfileOverlay) =
    result {
        let! description =
            match overlay.Description with
            | Some value -> nonEmpty "profile description" value
            | None -> Ok shared.Description

        let! idPrefix =
            match overlay.IdPrefix with
            | Some value -> optionalNonEmpty "profile idPrefix" (Some value)
            | None -> Ok shared.IdPrefix

        let routing =
            match overlay.Routing with
            | None -> shared.Routing
            | Some value ->
                { Positive = unionList shared.Routing.Positive value.Positive
                  Negative = unionList shared.Routing.Negative value.Negative }

        let envelope =
            match overlay.CapabilityEnvelope with
            | None -> shared.CapabilityEnvelope
            | Some value ->
                { Required = unionList shared.CapabilityEnvelope.Required (value.Required |> Option.defaultValue [])
                  Default = value.Default |> Option.defaultValue shared.CapabilityEnvelope.Default
                  Allowed = unionList shared.CapabilityEnvelope.Allowed (value.Allowed |> Option.defaultValue []) }

        let! policy =
            match overlay.Policy with
            | None -> Ok shared.Policy
            | Some value ->
                result {
                    let! guards =
                        match value.Guards with
                        | None -> Ok shared.Policy.Guards
                        | Some overlayGuards -> mergeGuardLists shared.Policy.Guards overlayGuards

                    let requiredSections =
                        unionList shared.Policy.RequiredSections (value.RequiredSections |> Option.defaultValue [])

                    let! roleDefaults =
                        match value.RoleDefaults with
                        | None -> Ok shared.Policy.RoleDefaults
                        | Some overlayRoles ->
                            // Same-ID overlays may add role defaults but may not
                            // rebind a purpose the shared profile already resolved.
                            overlayRoles
                            |> List.fold
                                (fun state role ->
                                    result {
                                        let! current = state

                                        match current |> List.tryFind (fun existing -> existing.Purpose = role.Purpose) with
                                        | Some existing when existing.Role <> role.Role ->
                                            return!
                                                Error(
                                                    InvalidInput
                                                        $"profile role default '{role.Purpose}' overlay changes its role"
                                                )
                                        | Some _ -> return current
                                        | None -> return current @ [ role ]
                                    })
                                (Ok shared.Policy.RoleDefaults)

                    return
                        { Guards = guards
                          RequiredSections = requiredSections
                          RoleDefaults = roleDefaults }
                }

        let guidance =
            match overlay.SemanticGuidance with
            | None -> shared.SemanticGuidance
            | Some value ->
                let replace overlayText sharedText =
                    match overlayText with
                    | Some text when not (String.IsNullOrWhiteSpace text) -> text
                    | _ -> sharedText

                { Common = replace value.Common shared.SemanticGuidance.Common
                  Research = replace value.Research shared.SemanticGuidance.Research
                  Execution = replace value.Execution shared.SemanticGuidance.Execution }

        let merged =
            { shared with
                Description = description
                IdPrefix = idPrefix
                Routing = routing
                CapabilityEnvelope = envelope
                Policy = policy
                SemanticGuidance = guidance }

        return! validateProfileDefinition merged
    }

let private definitionFromOverlay (overlay: ProfileOverlay) =
    let definition: ProfileDefinition =
        { SchemaVersion = overlay.SchemaVersion
          Id = overlay.Id
          Description = overlay.Description |> Option.defaultValue ""
          IdPrefix = overlay.IdPrefix
          Routing = overlay.Routing |> Option.defaultValue { Positive = []; Negative = [] }
          CapabilityEnvelope =
            overlay.CapabilityEnvelope
            |> Option.map (fun value ->
                { Required = value.Required |> Option.defaultValue []
                  Default = value.Default |> Option.defaultValue []
                  Allowed = value.Allowed |> Option.defaultValue [] }: CapabilityEnvelope)
            |> Option.defaultValue { Required = []; Default = []; Allowed = [] }
          Policy =
            overlay.Policy
            |> Option.map (fun value ->
                { Guards = value.Guards |> Option.defaultValue []
                  RequiredSections = value.RequiredSections |> Option.defaultValue []
                  RoleDefaults = value.RoleDefaults |> Option.defaultValue [] }: ProfilePolicy)
            |> Option.defaultValue { Guards = []; RequiredSections = []; RoleDefaults = [] }
          SemanticGuidance =
            overlay.SemanticGuidance
            |> Option.map (fun value ->
                { Common = value.Common |> Option.defaultValue ""
                  Research = value.Research |> Option.defaultValue ""
                  Execution = value.Execution |> Option.defaultValue "" }: SemanticGuidance)
            |> Option.defaultValue { Common = ""; Research = ""; Execution = "" } }

    validateProfileDefinition definition

// Section 19: project profile files are strict JSON read from
// <project-root>/.opencode/task/profiles. A file whose id matches an existing
// shared profile is an overlay; an unknown id introduces a project profile.
module private ProfileSource =
    open System.Text.Json

    let private properties (element: JsonElement) =
        let values = element.EnumerateObject() |> Seq.toList

        let duplicate =
            values
            |> List.groupBy (fun property -> property.Name)
            |> List.tryFind (fun (_, matches) -> matches.Length > 1)

        match duplicate with
        | Some (name, _) -> Error(InvalidInput $"duplicate JSON property '{name}'")
        | None -> Ok values

    let private objectValue name (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error(InvalidInput $"{name} must be an object")
        else
            properties element

    let private tryProperty name (props: JsonProperty list) =
        props |> List.tryFind (fun item -> item.Name = name) |> Option.map (fun item -> item.Value)

    let private knownProperties name (allowed: string list) (props: JsonProperty list) =
        let actual = props |> List.map _.Name |> Set.ofList
        let unknown = Set.difference actual (Set.ofList allowed) |> Set.toList

        if unknown.IsEmpty then
            Ok()
        else
            Error(InvalidInput $"{name} contains unknown property '{unknown.Head}'")

    let private stringValue name (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.String then
            Error(InvalidInput $"property '{name}' must be a string")
        else
            Ok(element.GetString())

    let private requiredString name (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Error(InvalidInput $"property '{name}' is required")
        | Some value -> stringValue name value

    let private optionalString name (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Ok None
        | Some value -> stringValue name value |> Result.map Some

    let private requiredInt name (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Error(InvalidInput $"property '{name}' is required")
        | Some value ->
            match value.ValueKind with
            | JsonValueKind.Number ->
                match value.TryGetInt32() with
                | true, number -> Ok number
                | false, _ -> Error(InvalidInput $"property '{name}' must be a 32-bit integer")
            | _ -> Error(InvalidInput $"property '{name}' must be an integer")

    let private optionalBool name (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Ok None
        | Some value ->
            match value.ValueKind with
            | JsonValueKind.True -> Ok(Some true)
            | JsonValueKind.False -> Ok(Some false)
            | _ -> Error(InvalidInput $"property '{name}' must be a boolean")

    let private stringList name (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            Error(InvalidInput $"property '{name}' must be an array")
        else
            element.EnumerateArray()
            |> Seq.toList
            |> List.map (fun item ->
                if item.ValueKind <> JsonValueKind.String then
                    Error(InvalidInput $"array '{name}' must contain only strings")
                else
                    Ok(item.GetString()))
            |> collectResults

    let private optionalStringList name (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Ok None
        | Some value -> stringList name value |> Result.map Some

    let private parseGuard (element: JsonElement) =
        result {
            let! props = objectValue "profile guard" element

            do!
                knownProperties
                    "profile guard"
                    [ "key"
                      "target"
                      "checkpoint"
                      "evidenceKind"
                      "minimumCount"
                      "producerRole"
                      "requireIndependentProducer"
                      "applicability"
                      "waiver"
                      "kind" ]
                    props

            let! key = requiredString "key" props
            let! targetText = requiredString "target" props
            let! checkpointText = requiredString "checkpoint" props
            let! kindText = requiredString "evidenceKind" props
            let! minimumCount = requiredInt "minimumCount" props
            let! producerRole = optionalString "producerRole" props
            let! independent = optionalBool "requireIndependentProducer" props
            let! applicabilityText = optionalString "applicability" props
            let! waiverText = optionalString "waiver" props
            let! taskKindText = optionalString "kind" props
            let! target = parseGuardTarget targetText
            let! checkpoint = parseGuardCheckpoint checkpointText
            let! kind = parseEvidenceKind kindText

            let! taskKind =
                match taskKindText with
                | None -> Ok None
                | Some text -> parseKind text |> Result.map Some

            let! applicability =
                match applicabilityText with
                | None -> Ok Always
                | Some text -> parseApplicability text

            let! waiver =
                match waiverText with
                | None -> Ok NotWaivable
                | Some text -> parseWaiver text

            return
                { Key = key
                  Target = target
                  Checkpoint = checkpoint
                  Requirement =
                    { Kind = kind
                      MinimumCount = minimumCount
                      ProducerRole = producerRole
                      RequireIndependentProducer = independent |> Option.defaultValue false }
                  Applicability = applicability
                  Waiver = waiver
                  TaskKind = taskKind }
        }

    let private parseRouting (element: JsonElement) =
        result {
            let! props = objectValue "routing" element
            do! knownProperties "routing" [ "positive"; "negative" ] props
            let! positive = optionalStringList "positive" props
            let! negative = optionalStringList "negative" props

            return
                { Positive = positive |> Option.defaultValue []
                  Negative = negative |> Option.defaultValue [] }
        }

    let private parseEnvelope (element: JsonElement) =
        result {
            let! props = objectValue "capabilityEnvelope" element
            do! knownProperties "capabilityEnvelope" [ "required"; "default"; "allowed" ] props
            let! required = optionalStringList "required" props
            let! defaults = optionalStringList "default" props
            let! allowed = optionalStringList "allowed" props
            return { Required = required; Default = defaults; Allowed = allowed }
        }

    let private parseRoleDefault (element: JsonElement) =
        result {
            let! props = objectValue "roleDefault" element
            do! knownProperties "roleDefault" [ "purpose"; "role" ] props
            let! purpose = requiredString "purpose" props
            let! role = requiredString "role" props
            return { Purpose = purpose; Role = role }
        }

    let private parseArrayOf name parse (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Ok None
        | Some value ->
            if value.ValueKind <> JsonValueKind.Array then
                Error(InvalidInput $"property '{name}' must be an array")
            else
                value.EnumerateArray()
                |> Seq.toList
                |> List.map parse
                |> collectResults
                |> Result.map Some

    let private parsePolicy (element: JsonElement) =
        result {
            let! props = objectValue "policy" element
            do! knownProperties "policy" [ "guards"; "requiredSections"; "roleDefaults" ] props
            let! guards = parseArrayOf "guards" parseGuard props
            let! requiredSections = optionalStringList "requiredSections" props
            let! roleDefaults = parseArrayOf "roleDefaults" parseRoleDefault props
            return { Guards = guards; RequiredSections = requiredSections; RoleDefaults = roleDefaults }
        }

    let private parseGuidance (element: JsonElement) =
        result {
            let! props = objectValue "semanticGuidance" element
            do! knownProperties "semanticGuidance" [ "common"; "research"; "execution" ] props
            let! common = optionalString "common" props
            let! research = optionalString "research" props
            let! execution = optionalString "execution" props
            return { Common = common; Research = research; Execution = execution }
        }

    let parseOverlay (element: JsonElement) =
        result {
            let! props = objectValue "profile" element

            do!
                knownProperties
                    "profile"
                    [ "schemaVersion"
                      "id"
                      "description"
                      "idPrefix"
                      "routing"
                      "capabilityEnvelope"
                      "policy"
                      "semanticGuidance" ]
                    props

            let! schemaVersion = requiredInt "schemaVersion" props
            let! id = requiredString "id" props
            let! description = optionalString "description" props
            let! idPrefix = optionalString "idPrefix" props

            let! routing =
                match tryProperty "routing" props with
                | None -> Ok None
                | Some value -> parseRouting value |> Result.map Some

            let! envelope =
                match tryProperty "capabilityEnvelope" props with
                | None -> Ok None
                | Some value -> parseEnvelope value |> Result.map Some

            let! policy =
                match tryProperty "policy" props with
                | None -> Ok None
                | Some value -> parsePolicy value |> Result.map Some

            let! guidance =
                match tryProperty "semanticGuidance" props with
                | None -> Ok None
                | Some value -> parseGuidance value |> Result.map Some

            return
                { SchemaVersion = schemaVersion
                  Id = id
                  Description = description
                  IdPrefix = idPrefix
                  Routing = routing
                  CapabilityEnvelope = envelope
                  Policy = policy
                  SemanticGuidance = guidance }
        }

// Section 19.1: deterministic ordinal file order. A missing directory means only
// the compiled-in builtins are active. Malformed files, duplicate IDs, invalid
// new profiles, and weakening same-ID overlays all fail resolution; the platform
// composition.json is never consulted here.
let resolveProfiles root =
    result {
        let directory = Path.Combine(Path.GetFullPath root, ".opencode", "task", "profiles")

        if not (Directory.Exists directory) then
            return builtinProfiles
        else
            let! files =
                try
                    Ok(
                        Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                        |> Seq.sortWith (fun left right -> String.CompareOrdinal(left, right))
                        |> Seq.toList
                    )
                with error ->
                    Error(PersistenceFailure $"could not enumerate project profiles: {error.Message}")

            let! parsed =
                files
                |> List.map (fun path ->
                    result {
                        let! text =
                            try
                                Ok(File.ReadAllText path)
                            with error ->
                                Error(PersistenceFailure $"could not read project profile '{path}': {error.Message}")

                        let! overlay =
                            try
                                use document = JsonDocument.Parse text
                                ProfileSource.parseOverlay document.RootElement
                            with
                            | :? JsonException as error ->
                                Error(InvalidInput $"project profile '{path}' is not valid JSON: {error.Message}")
                            | error ->
                                Error(PersistenceFailure $"could not parse project profile '{path}': {error.Message}")

                        if overlay.SchemaVersion <> 1 then
                            return! Error(InvalidInput $"project profile '{path}' schemaVersion must be 1")

                        return path, overlay
                    })
                |> collectResults

            let duplicate =
                parsed
                |> List.map (fun (_, overlay) -> overlay.Id)
                |> List.groupBy (fun value -> value)
                |> List.tryFind (fun (_, matches) -> matches.Length > 1)

            match duplicate with
            | Some (id, _) -> return! Error(InvalidInput $"duplicate project profile id '{id}'")
            | None ->
                let! registry =
                    parsed
                    |> List.fold
                        (fun state (_, overlay) ->
                            result {
                                let! current = state

                                match Map.tryFind overlay.Id current with
                                | Some shared ->
                                    let! merged = mergeProfileDefinition shared.Definition overlay
                                    return Map.add overlay.Id (makeEffectiveProfile merged Overlay) current
                                | None ->
                                    let! definition = definitionFromOverlay overlay
                                    return Map.add overlay.Id (makeEffectiveProfile definition Project) current
                            })
                        (Ok builtinProfiles)

                return registry
    }

let private nextGuardNumber (guards: Guard list) =
    guards
    |> List.choose (fun guard ->
        if guard.Id.StartsWith("G", StringComparison.Ordinal) then
            match Int32.TryParse(guard.Id.Substring 1) with
            | true, value -> Some value
            | _ -> None
        else
            None)
    |> List.fold max 0

let private guardSignature (guard: Guard) =
    let requirement =
        match guard.Requirement with
        | EvidenceRequired value -> value

    [ guardTargetToString guard.Target
      guardCheckpointToString guard.Checkpoint
      evidenceKindToString requirement.Kind
      string requirement.MinimumCount
      requirement.ProducerRole |> Option.defaultValue ""
      string requirement.RequireIndependentProducer
      applicabilityToString guard.Applicability
      waiverToString guard.Waiver ]
    |> String.concat "|"

// Section 15.5: the profile guard Key is its stable policy identity. Two specs
// that share structured content but carry different keys are distinct
// obligations, so Key/TaskKind participate in the identity signature.
let private profileGuardSignature (spec: ProfileGuardSpec) =
    [ spec.Key
      guardTargetToString spec.Target
      guardCheckpointToString spec.Checkpoint
      evidenceKindToString spec.Requirement.Kind
      string spec.Requirement.MinimumCount
      spec.Requirement.ProducerRole |> Option.defaultValue ""
      string spec.Requirement.RequireIndependentProducer
      applicabilityToString spec.Applicability
      waiverToString spec.Waiver
      spec.TaskKind |> Option.map kindToString |> Option.defaultValue "" ]
    |> String.concat "|"

let private profileGuardMatches (guard: Guard) (spec: ProfileGuardSpec) =
    guardSignature guard =
        ([ guardTargetToString spec.Target
           guardCheckpointToString spec.Checkpoint
           evidenceKindToString spec.Requirement.Kind
           string spec.Requirement.MinimumCount
           spec.Requirement.ProducerRole |> Option.defaultValue ""
           string spec.Requirement.RequireIndependentProducer
           applicabilityToString spec.Applicability
           waiverToString spec.Waiver ]
         |> String.concat "|")

// Section 15.5: profile guards are regenerated from the Effective Profile before
// baseline. After baseline they are synced monotonically: existing profile guards
// are never deleted and only new/stronger signatures are added. TaskKind scopes
// each spec to one Kind, so a Kind change filters which obligations materialize.
// Every persisted profile guard has explicit key provenance; keyless guards are
// rejected at the persistence boundary rather than inferred by content.
let private syncProfileGuards
    (existingGuards: Guard list)
    (existingKeys: Map<string, string>)
    (profile: EffectiveProfile)
    (kind: Kind)
    (monotonic: bool)
    : MaterializedGuard list =
    let existingProfileGuards =
        existingGuards |> List.filter (fun guard -> guard.Origin = ProfileMaterialized)

    let specs =
        profile.Definition.Policy.Guards
        |> List.filter (fun spec -> spec.TaskKind = None || spec.TaskKind = Some kind)

    let usedIds = System.Collections.Generic.HashSet<string>(existingGuards |> List.map _.Id)
    let mutable nextId = nextGuardNumber existingGuards + 1

    let allocateId () =
        let mutable candidate = $"G{nextId}"

        while usedIds.Contains candidate do
            nextId <- nextId + 1
            candidate <- $"G{nextId}"

        usedIds.Add candidate |> ignore
        nextId <- nextId + 1
        candidate

    // Each existing guard is paired with the key provenance persisted for it and
    // consumed at most once. A key match is reused only when the structured
    // content is unchanged, so a strengthened/re-scoped same-key spec still
    // materializes a new obligation instead of silently keeping the weaker guard.
    let available =
        System.Collections.Generic.List<Guard * string option>(
            existingProfileGuards |> List.map (fun guard -> guard, Map.tryFind guard.Id existingKeys)
        )

    let tryReuse (spec: ProfileGuardSpec) =
        let index =
            available
            |> Seq.tryFindIndex (fun (guard, key) ->
                key = Some spec.Key && profileGuardMatches guard spec)

        match index with
        | None -> None
        | Some index ->
            let guard, _ = available.[index]
            available.RemoveAt index
            Some guard

    let reused, created =
        specs
        |> List.fold
            (fun (reused, created) spec ->
                match tryReuse spec with
                | Some guard -> reused @ [ { Guard = guard; Key = Some spec.Key } ], created
                | None ->
                    let guard =
                        { Id = allocateId ()
                          Target = spec.Target
                          Checkpoint = spec.Checkpoint
                          Origin = ProfileMaterialized
                          Requirement = EvidenceRequired spec.Requirement
                          Applicability = spec.Applicability
                          Waiver = spec.Waiver
                          Disposition = GuardDisposition.Applicable }

                    reused, created @ [ { Guard = guard; Key = Some spec.Key } ])
            ([], [])

    if monotonic then
        // Retain every existing profile guard in place, refreshing the key assigned
        // to guards matched to a current spec and keeping persisted keys otherwise.
        let assigned =
            reused
            |> List.map (fun materialized -> materialized.Guard.Id, materialized.Key)
            |> Map.ofList

        let retained =
            existingProfileGuards
            |> List.map (fun guard ->
                let key =
                    match Map.tryFind guard.Id assigned with
                    | Some assignedKey -> assignedKey
                    | None -> Map.tryFind guard.Id existingKeys

                { Guard = guard; Key = key })

        retained @ created
    else
        reused @ created

// Section 25.1: a baselined Profile change may proceed under Coordinator
// authority only when it does not weaken the capability envelope.
let private profileWeakening (current: EffectiveProfile) (target: EffectiveProfile) =
    let targetRequired = target.Definition.CapabilityEnvelope.Required

    current.Definition.CapabilityEnvelope.Required
    |> List.exists (fun capability -> not (List.contains capability targetRequired))

// Section 18: effective capabilities are required + applicable defaults +
// the explicitly activated subset of allowed.
let effectiveCapabilities (profile: EffectiveProfile) (activated: string list) =
    let envelope = profile.Definition.CapabilityEnvelope

    let selected =
        activated |> List.filter (fun capability -> List.contains capability envelope.Allowed)

    (envelope.Required @ envelope.Default @ selected) |> List.distinct

let renderProfile (profile: EffectiveProfile) =
    let definition = profile.Definition
    let envelope = definition.CapabilityEnvelope

    let idPrefix = definition.IdPrefix |> Option.defaultValue ""
    let join values = String.concat ", " values

    let lines =
        [ $"id: {definition.Id}"
          $"fingerprint: {profile.Fingerprint}"
          $"origin: {profileOriginToString profile.Origin}"
          $"description: {definition.Description}"
          $"idPrefix: {idPrefix}"
          $"routing.positive: {join definition.Routing.Positive}"
          $"routing.negative: {join definition.Routing.Negative}"
          $"capability.required: {join envelope.Required}"
          $"capability.default: {join envelope.Default}"
          $"capability.allowed: {join envelope.Allowed}"
          $"guards: {definition.Policy.Guards.Length}"
          $"requiredSections: {join definition.Policy.RequiredSections}" ]
        @ (definition.Policy.Guards
           |> List.map (fun guard -> $"guard {guard.Key}: {profileGuardSignature guard}"))

    lines |> String.concat "\n"

module private Dto =
    let private acceptanceDto (acceptance: Acceptance) =
        let state, evidenceRefs =
            match acceptance.State with
            | Pending -> "pending", []
            | Verified refs -> "verified", refs

        { Id = acceptance.Id
          Text = acceptance.Text
          State = state
          EvidenceRefs = evidenceRefs }

    let private evidenceDto (record: EvidenceRecord) =
        let validity, supersededReason =
            match record.Validity with
            | Valid -> "valid", ""
            | Superseded reason -> "superseded", reason

        { Id = record.Evidence.Id
          Kind = evidenceKindToString record.Evidence.Kind
          Source = evidenceSourceToString record.Evidence.Source
          Subject = record.Evidence.Subject |> Option.defaultValue ""
          ProducerRole = record.Evidence.ProducerRole |> Option.defaultValue ""
          ProducerId = record.Evidence.ProducerId |> Option.defaultValue ""
          Reference = record.Evidence.Reference |> Option.defaultValue ""
          Summary = record.Evidence.Summary
          Validity = validity
          SupersededReason = supersededReason }

    let private ownerDto (owner: Owner) =
        { Role = owner.Role
          AgentId = owner.AgentId |> Option.defaultValue "" }

    let private handoffDto (handoff: TerminalHandoff) =
        { State = handoff.State
          EvidenceSummary = handoff.EvidenceSummary
          Next = handoff.Next }

    let rec private workItemDto (workItem: WorkItem) =
        let state, resumeCondition, blocker, skipDisposition =
            match workItem.State with
            | PendingWork -> "pending", "", "", ""
            | ActiveWork -> "active", "", "", ""
            | WaitingWork condition -> "waiting", resumeConditionToString condition, "", ""
            | BlockedWork value -> "blocked", "", blockerToString value, ""
            | DoneWork -> "done", "", "", ""
            | SkippedWork disposition -> "skipped", "", "", skipDispositionToString disposition

        { Id = workItem.Id
          Title = workItem.Title
          State = state
          Result = workItem.Result |> Option.defaultValue ""
          AcceptanceRefs = workItem.AcceptanceRefs
          DependsOn = workItem.DependsOn
          EvidenceRefs = workItem.EvidenceRefs
          ResumeCondition = resumeCondition
          Blocker = blocker
          SkipDisposition = skipDisposition
          Owner = workItem.Owner |> Option.map ownerDto
          Children = workItem.Children |> List.map workItemDto }

    let private guardDto (guard: Guard) =
        let evidenceKind, minimumCount, producerRole, requireIndependentProducer =
            match guard.Requirement with
            | EvidenceRequired requirement ->
                evidenceKindToString requirement.Kind,
                requirement.MinimumCount,
                requirement.ProducerRole |> Option.defaultValue "",
                requirement.RequireIndependentProducer

        { Id = guard.Id
          Target = guardTargetToString guard.Target
          Checkpoint = guardCheckpointToString guard.Checkpoint
          Origin = guardOriginToString guard.Origin
          Requirement = "evidenceRequired"
          EvidenceKind = evidenceKind
          MinimumCount = minimumCount
          ProducerRole = producerRole
          RequireIndependentProducer = requireIndependentProducer
          Applicability = applicabilityToString guard.Applicability
          Waiver = waiverToString guard.Waiver
          Disposition = guardDispositionToString guard.Disposition }

    let private decisionDto (decision: Decision) =
        { Id = decision.Id
          Authority = decisionAuthorityToString decision.Authority
          Kind = decisionKindToString decision.Kind
          Targets = decision.Targets |> List.map decisionTargetToString
          Rationale = decision.Rationale
          CreatedAt = decision.CreatedAt.ToString("O")
          ConfirmationRef = decision.ConfirmationRef |> Option.defaultValue "" }

    let private questionDto (question: Question) =
        let impact, workItems =
            match question.Impact with
            | TaskWide -> "taskWide", []
            | WorkItems ids -> "workItems", ids

        let state, resolution =
            match question.State with
            | Open -> "open", ""
            | Resolved (DecisionRef id) -> "resolved", id

        { Id = question.Id
          Text = question.Text
          Impact = impact
          WorkItems = workItems
          State = state
          Resolution = resolution }

    let fromDomain (task: TaskModel) =
        { SchemaVersion = SchemaVersion
          Id = task.Id
          Title = task.Title
          Created = task.Created.ToString("O")
          Kind = kindToString task.Kind
          Profile = task.Profile
          ProfileFingerprint = task.ProfileFingerprint
          Objective = task.Objective
          Scope = task.Scope
          NonGoals = task.NonGoals
          ContractState = contractStateToString task.ContractState
          ContractFingerprint = task.ContractFingerprint
          ContractRevision = task.ContractRevision
          StateRevision = task.StateRevision
          Lifecycle = lifecycleToString task.Lifecycle
          Evidence = task.Evidence |> List.map evidenceDto
          AcceptanceCriteria = task.AcceptanceCriteria |> List.map acceptanceDto
          Guards = task.Guards |> List.map guardDto
          ProfileGuardKeys = task.ProfileGuardKeys |> Map.toList
          Decisions = task.Decisions |> List.map decisionDto
          Questions = task.Questions |> List.map questionDto
          WorkItems = task.WorkItems |> List.map workItemDto
          TerminalHandoff = task.TerminalHandoff |> Option.map handoffDto
          CompletionHistory = task.CompletionHistory |> List.map handoffDto }

    let private writeString (writer: Utf8JsonWriter) (name: string) (value: string) =
        writer.WriteString(name, value)

    let rec private writeWorkItem (writer: Utf8JsonWriter) (item: WorkItemDto) =
        writer.WriteStartObject()
        writeString writer "id" item.Id
        writeString writer "title" item.Title
        writeString writer "state" item.State
        writeString writer "result" item.Result
        writer.WriteStartArray "acceptanceRefs"
        item.AcceptanceRefs |> List.iter (fun value -> writer.WriteStringValue(value))
        writer.WriteEndArray()
        writer.WriteStartArray "dependsOn"
        item.DependsOn |> List.iter (fun value -> writer.WriteStringValue(value))
        writer.WriteEndArray()

        writer.WriteStartArray "evidenceRefs"
        item.EvidenceRefs |> List.iter (fun value -> writer.WriteStringValue(value))
        writer.WriteEndArray()

        if item.ResumeCondition <> "" then writeString writer "resumeCondition" item.ResumeCondition
        if item.Blocker <> "" then writeString writer "blocker" item.Blocker
        if item.SkipDisposition <> "" then writeString writer "skipDisposition" item.SkipDisposition

        match item.Owner with
        | Some owner ->
            writer.WriteStartObject "owner"
            writeString writer "role" owner.Role
            writeString writer "agentId" owner.AgentId
            writer.WriteEndObject()
        | None -> ()

        writer.WriteStartArray "children"
        item.Children |> List.iter (writeWorkItem writer)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let toJson dto =
        use stream = new MemoryStream()
        let options = JsonWriterOptions(Indented = true)
        use writer = new Utf8JsonWriter(stream, options)
        writer.WriteStartObject()
        writer.WriteNumber("schemaVersion", dto.SchemaVersion)
        writeString writer "id" dto.Id
        writeString writer "title" dto.Title
        writeString writer "created" dto.Created
        writeString writer "kind" dto.Kind
        writeString writer "profile" dto.Profile
        writeString writer "profileFingerprint" dto.ProfileFingerprint
        writeString writer "objective" dto.Objective
        writeString writer "scope" dto.Scope
        writeString writer "nonGoals" dto.NonGoals
        writeString writer "contractState" dto.ContractState
        writeString writer "contractFingerprint" dto.ContractFingerprint
        writer.WriteNumber("contractRevision", dto.ContractRevision)
        writer.WriteNumber("stateRevision", dto.StateRevision)
        writeString writer "lifecycle" dto.Lifecycle
        writer.WriteStartArray "evidence"
        for record in dto.Evidence do
            writer.WriteStartObject()
            writeString writer "id" record.Id
            writeString writer "kind" record.Kind
            writeString writer "source" record.Source
            writeString writer "subject" record.Subject
            writeString writer "producerRole" record.ProducerRole
            writeString writer "producerId" record.ProducerId
            writeString writer "reference" record.Reference
            writeString writer "summary" record.Summary
            writeString writer "validity" record.Validity
            writeString writer "supersededReason" record.SupersededReason
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray "acceptanceCriteria"
        for criterion in dto.AcceptanceCriteria do
            writer.WriteStartObject()
            writeString writer "id" criterion.Id
            writeString writer "text" criterion.Text
            writeString writer "state" criterion.State
            writer.WriteStartArray "evidenceRefs"
            criterion.EvidenceRefs |> List.iter (fun value -> writer.WriteStringValue(value))
            writer.WriteEndArray()
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray "guards"
        for guard in dto.Guards do
            writer.WriteStartObject()
            writeString writer "id" guard.Id
            writeString writer "target" guard.Target
            writeString writer "checkpoint" guard.Checkpoint
            writeString writer "origin" guard.Origin
            writeString writer "requirement" guard.Requirement
            writeString writer "evidenceKind" guard.EvidenceKind
            writer.WriteNumber("minimumCount", guard.MinimumCount)
            writeString writer "producerRole" guard.ProducerRole
            writer.WriteBoolean("requireIndependentProducer", guard.RequireIndependentProducer)
            writeString writer "applicability" guard.Applicability
            writeString writer "waiver" guard.Waiver
            writeString writer "disposition" guard.Disposition
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartObject "profileGuardKeys"
        for (guardIdText, key) in dto.ProfileGuardKeys do
            writeString writer guardIdText key
        writer.WriteEndObject()
        writer.WriteStartArray "decisions"
        for decision in dto.Decisions do
            writer.WriteStartObject()
            writeString writer "id" decision.Id
            writeString writer "authority" decision.Authority
            writeString writer "kind" decision.Kind
            writer.WriteStartArray "targets"
            decision.Targets |> List.iter (fun value -> writer.WriteStringValue(value))
            writer.WriteEndArray()
            writeString writer "rationale" decision.Rationale
            writeString writer "createdAt" decision.CreatedAt
            writeString writer "confirmationRef" decision.ConfirmationRef
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray "questions"
        for question in dto.Questions do
            writer.WriteStartObject()
            writeString writer "id" question.Id
            writeString writer "text" question.Text
            writeString writer "impact" question.Impact
            if question.Impact = "workItems" then
                writer.WriteStartArray "workItems"
                question.WorkItems |> List.iter (fun value -> writer.WriteStringValue(value))
                writer.WriteEndArray()
            writer.WriteString("state", question.State)
            if question.State = "resolved" then writeString writer "resolution" question.Resolution
            writer.WriteEndObject()
        writer.WriteEndArray()
        writer.WriteStartArray "workItems"
        dto.WorkItems |> List.iter (writeWorkItem writer)
        writer.WriteEndArray()

        match dto.TerminalHandoff with
        | Some handoff ->
            writer.WriteStartObject "terminalHandoff"
            writeString writer "state" handoff.State
            writeString writer "evidenceSummary" handoff.EvidenceSummary
            writeString writer "next" handoff.Next
            writer.WriteEndObject()
        | None -> ()

        writer.WriteStartArray "completionHistory"

        for handoff in dto.CompletionHistory do
            writer.WriteStartObject()
            writeString writer "state" handoff.State
            writeString writer "evidenceSummary" handoff.EvidenceSummary
            writeString writer "next" handoff.Next
            writer.WriteEndObject()

        writer.WriteEndArray()

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    let private properties (element: JsonElement) =
        let values = element.EnumerateObject() |> Seq.toList
        let duplicate =
            values
            |> List.groupBy (fun property -> property.Name)
            |> List.tryFind (fun (_, matches) -> matches.Length > 1)

        match duplicate with
        | Some (name, _) -> Error (InvalidInput $"duplicate JSON property '{name}'")
        | None -> Ok values

    let private objectValue (name: string) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error (InvalidInput $"{name} must be an object")
        else
            properties element

    let private exactProperties (name: string) (expected: string list) (actual: JsonProperty list) =
        let actualNames = actual |> List.map (fun property -> property.Name) |> Set.ofList
        let expectedNames = Set.ofList expected
        let unknown = Set.difference actualNames expectedNames |> Set.toList
        let missing = Set.difference expectedNames actualNames |> Set.toList

        if not unknown.IsEmpty then
            Error (InvalidInput $"{name} contains unknown property '{unknown.Head}'")
        elif not missing.IsEmpty then
            Error (InvalidInput $"{name} is missing property '{missing.Head}'")
        else
            Ok()

    // Same strictness as exactProperties, but the optional set may be absent.
    let private propertiesWithin (name: string) (required: string list) (optional: string list) (actual: JsonProperty list) =
        let actualNames = actual |> List.map (fun property -> property.Name) |> Set.ofList
        let allowed = Set.ofList (required @ optional)
        let requiredNames = Set.ofList required
        let unknown = Set.difference actualNames allowed |> Set.toList
        let missing = Set.difference requiredNames actualNames |> Set.toList

        if not unknown.IsEmpty then
            Error (InvalidInput $"{name} contains unknown property '{unknown.Head}'")
        elif not missing.IsEmpty then
            Error (InvalidInput $"{name} is missing property '{missing.Head}'")
        else
            Ok()

    let private property (name: string) (properties: JsonProperty list) =
        properties |> List.find (fun item -> item.Name = name) |> fun item -> item.Value

    let private tryProperty (name: string) (properties: JsonProperty list) =
        properties |> List.tryFind (fun item -> item.Name = name) |> Option.map (fun item -> item.Value)

    let private stringProperty (name: string) (properties: JsonProperty list) =
        let value = property name properties
        if value.ValueKind <> JsonValueKind.String then
            Error (InvalidInput $"property '{name}' must be a string")
        else
            Ok(value.GetString())

    let private optionalStringProperty (name: string) (properties: JsonProperty list) =
        match tryProperty name properties with
        | None -> Ok ""
        | Some value ->
            if value.ValueKind <> JsonValueKind.String then
                Error (InvalidInput $"property '{name}' must be a string")
            else
                Ok(value.GetString())

    let private intProperty (name: string) (properties: JsonProperty list) =
        let value = property name properties
        match value.ValueKind with
        | JsonValueKind.Number ->
            match value.TryGetInt32() with
            | true, number -> Ok number
            | false, _ -> Error (InvalidInput $"property '{name}' must be a 32-bit integer")
        | _ -> Error (InvalidInput $"property '{name}' must be an integer")

    let private arrayProperty (name: string) (properties: JsonProperty list) =
        let value = property name properties
        if value.ValueKind <> JsonValueKind.Array then
            Error (InvalidInput $"property '{name}' must be an array")
        else
            Ok(value.EnumerateArray() |> Seq.toList)

    let private boolProperty (name: string) (properties: JsonProperty list) =
        let value = property name properties
        match value.ValueKind with
        | JsonValueKind.True -> Ok true
        | JsonValueKind.False -> Ok false
        | _ -> Error (InvalidInput $"property '{name}' must be a boolean")

    let private collectResults values =
        values
        |> List.fold
            (fun state item ->
                result {
                    let! collected = state
                    let! value = item
                    return value :: collected
                })
            (Ok [])
        |> Result.map List.rev

    let private stringsProperty (name: string) (properties: JsonProperty list) =
        arrayProperty name properties
        |> Result.bind (fun values ->
            values
            |> List.map (fun value ->
                if value.ValueKind <> JsonValueKind.String then
                    Error (InvalidInput $"array '{name}' must contain only strings")
                else
                    Ok(value.GetString()))
            |> collectResults)

    let private optionalStringsProperty (name: string) (properties: JsonProperty list) =
        match tryProperty name properties with
        | None -> Ok []
        | Some _ -> stringsProperty name properties

    // Profile guard key provenance travels as a JSON object of Guard-id -> key.
    let private stringMapProperty (name: string) (props: JsonProperty list) =
        match tryProperty name props with
        | None -> Ok []
        | Some value when value.ValueKind <> JsonValueKind.Object ->
            Error (InvalidInput $"property '{name}' must be an object")
        | Some value ->
            match properties value with
            | Error error -> Error error
            | Ok entries ->
                entries
                |> List.map (fun property ->
                    if property.Value.ValueKind <> JsonValueKind.String then
                        Error (InvalidInput $"property '{name}' values must be strings")
                    else
                        Ok(property.Name, property.Value.GetString()))
                |> collectResults

    let private parseOwner element : Result<OwnerDto, RuntimeError> =
        result {
            let! props = objectValue "owner" element
            do! propertiesWithin "owner" [ "role" ] [ "agentId" ] props
            let! role = stringProperty "role" props
            let! agentId = optionalStringProperty "agentId" props
            return { Role = role; AgentId = agentId }
        }

    let private parseHandoff element : Result<TerminalHandoffDto, RuntimeError> =
        result {
            let! props = objectValue "terminal handoff" element
            do! exactProperties "terminal handoff" [ "state"; "evidenceSummary"; "next" ] props
            let! state = stringProperty "state" props
            let! evidenceSummary = stringProperty "evidenceSummary" props
            let! next = stringProperty "next" props
            return { State = state; EvidenceSummary = evidenceSummary; Next = next }
        }

    let private parseEvidence element =
        result {
            let! props = objectValue "evidence record" element
            do!
                exactProperties
                    "evidence record"
                    [ "id"
                      "kind"
                      "source"
                      "subject"
                      "producerRole"
                      "producerId"
                      "reference"
                      "summary"
                      "validity"
                      "supersededReason" ]
                    props
            let! id = stringProperty "id" props
            let! kind = stringProperty "kind" props
            let! source = stringProperty "source" props
            let! subject = stringProperty "subject" props
            let! producerRole = stringProperty "producerRole" props
            let! producerId = stringProperty "producerId" props
            let! reference = stringProperty "reference" props
            let! summary = stringProperty "summary" props
            let! validity = stringProperty "validity" props
            let! supersededReason = stringProperty "supersededReason" props
            return
                { Id = id
                  Kind = kind
                  Source = source
                  Subject = subject
                  ProducerRole = producerRole
                  ProducerId = producerId
                  Reference = reference
                  Summary = summary
                  Validity = validity
                  SupersededReason = supersededReason }
        }

    let private parseAcceptance element =
        result {
            let! props = objectValue "acceptance criterion" element
            do! exactProperties "acceptance criterion" [ "id"; "text"; "state"; "evidenceRefs" ] props
            let! id = stringProperty "id" props
            let! text = stringProperty "text" props
            let! state = stringProperty "state" props
            let! evidenceRefs = stringsProperty "evidenceRefs" props
            return
                { Id = id
                  Text = text
                  State = state
                  EvidenceRefs = evidenceRefs }
        }

    let private parseGuard element =
        result {
            let! props = objectValue "guard" element

            do!
                exactProperties
                    "guard"
                    [ "id"
                      "target"
                      "checkpoint"
                      "origin"
                      "requirement"
                      "evidenceKind"
                      "minimumCount"
                      "producerRole"
                      "requireIndependentProducer"
                      "applicability"
                      "waiver"
                      "disposition" ]
                    props

            let! id = stringProperty "id" props
            let! target = stringProperty "target" props
            let! checkpoint = stringProperty "checkpoint" props
            let! origin = stringProperty "origin" props
            let! requirement = stringProperty "requirement" props
            let! evidenceKind = stringProperty "evidenceKind" props
            let! minimumCount = intProperty "minimumCount" props
            let! producerRole = stringProperty "producerRole" props
            let! requireIndependentProducer = boolProperty "requireIndependentProducer" props
            let! applicability = stringProperty "applicability" props
            let! waiver = stringProperty "waiver" props
            let! disposition = stringProperty "disposition" props
            return
                { Id = id
                  Target = target
                  Checkpoint = checkpoint
                  Origin = origin
                  Requirement = requirement
                  EvidenceKind = evidenceKind
                  MinimumCount = minimumCount
                  ProducerRole = producerRole
                  RequireIndependentProducer = requireIndependentProducer
                  Applicability = applicability
                  Waiver = waiver
                  Disposition = disposition }
        }

    let private parseDecision element =
        result {
            let! props = objectValue "decision" element

            do!
                exactProperties
                    "decision"
                    [ "id"; "authority"; "kind"; "targets"; "rationale"; "createdAt"; "confirmationRef" ]
                    props

            let! id = stringProperty "id" props
            let! authority = stringProperty "authority" props
            let! kind = stringProperty "kind" props
            let! targets = stringsProperty "targets" props
            let! rationale = stringProperty "rationale" props
            let! createdAt = stringProperty "createdAt" props
            let! confirmationRef = stringProperty "confirmationRef" props
            return
                { Id = id
                  Authority = authority
                  Kind = kind
                  Targets = targets
                  Rationale = rationale
                  CreatedAt = createdAt
                  ConfirmationRef = confirmationRef }
        }

    let private parseQuestion element =
        result {
            let! props = objectValue "question" element
            do! propertiesWithin "question" [ "id"; "text"; "impact"; "state" ] [ "workItems"; "resolution" ] props
            let! id = stringProperty "id" props
            let! text = stringProperty "text" props
            let! impact = stringProperty "impact" props
            let! workItems = optionalStringsProperty "workItems" props
            let! state = stringProperty "state" props
            let! resolution = optionalStringProperty "resolution" props
            return
                { Id = id
                  Text = text
                  Impact = impact
                  WorkItems = workItems
                  State = state
                  Resolution = resolution }
        }

    let rec private parseWorkItem element =
        result {
            let! props = objectValue "work item" element

            do!
                propertiesWithin
                    "work item"
                    [ "id"; "title"; "state"; "result"; "acceptanceRefs"; "dependsOn"; "evidenceRefs"; "children" ]
                    [ "resumeCondition"; "blocker"; "skipDisposition"; "owner" ]
                    props

            let! id = stringProperty "id" props
            let! title = stringProperty "title" props
            let! state = stringProperty "state" props
            let! result = stringProperty "result" props
            let! acceptanceRefs = stringsProperty "acceptanceRefs" props
            let! dependsOn = optionalStringsProperty "dependsOn" props
            let! evidenceRefs = optionalStringsProperty "evidenceRefs" props
            let! resumeCondition = optionalStringProperty "resumeCondition" props
            let! blocker = optionalStringProperty "blocker" props
            let! skipDisposition = optionalStringProperty "skipDisposition" props
            let! owner =
                match tryProperty "owner" props with
                | None -> Ok None
                | Some element -> parseOwner element |> Result.map Some
            let! childrenJson = arrayProperty "children" props
            let! children = childrenJson |> List.map parseWorkItem |> collectResults
            return
                { Id = id
                  Title = title
                  State = state
                  Result = result
                  AcceptanceRefs = acceptanceRefs
                  DependsOn = dependsOn
                  EvidenceRefs = evidenceRefs
                  ResumeCondition = resumeCondition
                  Blocker = blocker
                  SkipDisposition = skipDisposition
                  Owner = owner
                  Children = children }
        }

    let fromJson (json: string) =
        try
            use document = JsonDocument.Parse(json, JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false))
            result {
                let! props = objectValue "task" document.RootElement
                do!
                    propertiesWithin
                        "task"
                        [ "schemaVersion"; "id"; "title"; "created"; "kind"; "profile"; "profileFingerprint";
                          "objective"; "scope"; "nonGoals"; "contractState"; "contractFingerprint";
                          "contractRevision"; "stateRevision"; "lifecycle"; "evidence"; "acceptanceCriteria";
                          "guards"; "profileGuardKeys"; "decisions"; "questions"; "workItems"; "completionHistory" ]
                        [ "terminalHandoff" ]
                        props
                let! schemaVersion = intProperty "schemaVersion" props
                let! id = stringProperty "id" props
                let! title = stringProperty "title" props
                let! created = stringProperty "created" props
                let! kind = stringProperty "kind" props
                let! profile = stringProperty "profile" props
                let! profileFingerprint = stringProperty "profileFingerprint" props
                let! objective = stringProperty "objective" props
                let! scope = stringProperty "scope" props
                let! nonGoals = stringProperty "nonGoals" props
                let! contractState = stringProperty "contractState" props
                let! contractFingerprint = stringProperty "contractFingerprint" props
                let! contractRevision = intProperty "contractRevision" props
                let! stateRevision = intProperty "stateRevision" props
                let! lifecycle = stringProperty "lifecycle" props
                let! evidenceJson = arrayProperty "evidence" props
                let! acceptanceJson = arrayProperty "acceptanceCriteria" props
                let! workItemsJson = arrayProperty "workItems" props
                let! guardsJson = arrayProperty "guards" props
                let! decisionsJson = arrayProperty "decisions" props
                let! questionsJson = arrayProperty "questions" props
                let! profileGuardKeys = stringMapProperty "profileGuardKeys" props
                let! terminalHandoff =
                    match tryProperty "terminalHandoff" props with
                    | None -> Ok None
                    | Some element -> parseHandoff element |> Result.map Some
                let! completionHistory =
                    arrayProperty "completionHistory" props
                    |> Result.bind (List.map parseHandoff >> collectResults)
                let! evidence = evidenceJson |> List.map parseEvidence |> collectResults
                let! acceptanceCriteria = acceptanceJson |> List.map parseAcceptance |> collectResults
                let! guards = guardsJson |> List.map parseGuard |> collectResults
                let! decisions = decisionsJson |> List.map parseDecision |> collectResults
                let! questions = questionsJson |> List.map parseQuestion |> collectResults
                let! workItems = workItemsJson |> List.map parseWorkItem |> collectResults
                return
                    { SchemaVersion = schemaVersion
                      Id = id
                      Title = title
                      Created = created
                      Kind = kind
                      Profile = profile
                      ProfileFingerprint = profileFingerprint
                      Objective = objective
                      Scope = scope
                      NonGoals = nonGoals
                      ContractState = contractState
                      ContractFingerprint = contractFingerprint
                      ContractRevision = contractRevision
                      StateRevision = stateRevision
                      Lifecycle = lifecycle
                      Evidence = evidence
                      AcceptanceCriteria = acceptanceCriteria
                      Guards = guards
                      ProfileGuardKeys = profileGuardKeys
                      Decisions = decisions
                      Questions = questions
                      WorkItems = workItems
                      TerminalHandoff = terminalHandoff
                      CompletionHistory = completionHistory }
            }
        with
        | :? JsonException as error -> Error (InvalidInput $"invalid JSON: {error.Message}")
        | :? FormatException as error -> Error (InvalidInput $"invalid JSON value: {error.Message}")

    let private parseProfileGuard element =
        result {
            let! props = objectValue "profile guard" element

            do!
                propertiesWithin
                    "profile guard"
                    [ "key"; "target"; "checkpoint"; "evidenceKind"; "minimumCount"; "applicability"; "waiver" ]
                    [ "producerRole"; "requireIndependentProducer"; "kind" ]
                    props

            let! key = stringProperty "key" props
            let! target = stringProperty "target" props
            let! checkpoint = stringProperty "checkpoint" props
            let! evidenceKind = stringProperty "evidenceKind" props
            let! minimumCount = intProperty "minimumCount" props
            let! producerRole = optionalStringProperty "producerRole" props

            let! requireIndependentProducer =
                match tryProperty "requireIndependentProducer" props with
                | None -> Ok false
                | Some _ -> boolProperty "requireIndependentProducer" props

            let! applicability = stringProperty "applicability" props
            let! waiver = stringProperty "waiver" props
            let! kindText = optionalStringProperty "kind" props
            let! parsedTarget = parseGuardTarget target
            let! parsedCheckpoint = parseGuardCheckpoint checkpoint
            let! parsedKind = parseEvidenceKind evidenceKind
            let! parsedApplicability = parseApplicability applicability
            let! parsedWaiver = parseWaiver waiver

            let! parsedTaskKind =
                if String.IsNullOrWhiteSpace kindText then
                    Ok None
                else
                    parseKind kindText |> Result.map Some

            return
                { Key = key
                  Target = parsedTarget
                  Checkpoint = parsedCheckpoint
                  Requirement =
                    { Kind = parsedKind
                      MinimumCount = minimumCount
                      ProducerRole = if String.IsNullOrWhiteSpace producerRole then None else Some producerRole
                      RequireIndependentProducer = requireIndependentProducer }
                  Applicability = parsedApplicability
                  Waiver = parsedWaiver
                  TaskKind = parsedTaskKind }
        }

    let private parseProfileGuardList (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Array then
            Error(InvalidInput "profile policy guards must be an array")
        else
            element.EnumerateArray() |> Seq.toList |> List.map parseProfileGuard |> collectResults

    let private parseRouting element =
        result {
            let! props = objectValue "profile routing" element
            do! propertiesWithin "profile routing" [] [ "positive"; "negative" ] props
            let! positive = optionalStringsProperty "positive" props
            let! negative = optionalStringsProperty "negative" props
            return { Positive = positive; Negative = negative }
        }

    let private parseCapabilityEnvelopeOverlay element =
        result {
            let! props = objectValue "profile capabilityEnvelope" element
            do! propertiesWithin "profile capabilityEnvelope" [] [ "required"; "default"; "allowed" ] props

            let! required =
                match tryProperty "required" props with
                | None -> Ok None
                | Some _ -> stringsProperty "required" props |> Result.map Some

            let! defaults =
                match tryProperty "default" props with
                | None -> Ok None
                | Some _ -> stringsProperty "default" props |> Result.map Some

            let! allowed =
                match tryProperty "allowed" props with
                | None -> Ok None
                | Some _ -> stringsProperty "allowed" props |> Result.map Some

            return
                { Required = required
                  Default = defaults
                  Allowed = allowed }
        }

    let private parseRoleDefault element =
        result {
            let! props = objectValue "profile role default" element
            do! exactProperties "profile role default" [ "purpose"; "role" ] props
            let! purpose = stringProperty "purpose" props
            let! role = stringProperty "role" props
            return { Purpose = purpose; Role = role }
        }

    let private parsePolicyOverlay element =
        result {
            let! props = objectValue "profile policy" element
            do! propertiesWithin "profile policy" [] [ "guards"; "requiredSections"; "roleDefaults" ] props

            let! guards =
                match tryProperty "guards" props with
                | None -> Ok None
                | Some value -> parseProfileGuardList value |> Result.map Some

            let! requiredSections =
                match tryProperty "requiredSections" props with
                | None -> Ok None
                | Some _ -> stringsProperty "requiredSections" props |> Result.map Some

            let! roleDefaults =
                match tryProperty "roleDefaults" props with
                | None -> Ok None
                | Some value ->
                    if value.ValueKind <> JsonValueKind.Array then
                        Error(InvalidInput "profile roleDefaults must be an array")
                    else
                        value.EnumerateArray()
                        |> Seq.toList
                        |> List.map parseRoleDefault
                        |> collectResults
                        |> Result.map Some

            return
                { Guards = guards
                  RequiredSections = requiredSections
                  RoleDefaults = roleDefaults }
        }

    let private parseSemanticGuidanceOverlay element =
        result {
            let! props = objectValue "profile semanticGuidance" element
            do! propertiesWithin "profile semanticGuidance" [] [ "common"; "research"; "execution" ] props
            let! common = optionalStringProperty "common" props
            let! research = optionalStringProperty "research" props
            let! execution = optionalStringProperty "execution" props

            let nonBlank value =
                if String.IsNullOrWhiteSpace value then None else Some value

            return
                { Common = nonBlank common
                  Research = nonBlank research
                  Execution = nonBlank execution }
        }

    // Project profile/overlay files are read-only inputs; they are never
    // serialized back, so only a strict reader is required.
    let profileOverlayFromJson (json: string) =
        try
            use document =
                JsonDocument.Parse(
                    json,
                    JsonDocumentOptions(CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false)
                )

            result {
                let! props = objectValue "profile" document.RootElement

                do!
                    propertiesWithin
                        "profile"
                        [ "schemaVersion"; "id" ]
                        [ "description"; "idPrefix"; "routing"; "capabilityEnvelope"; "policy"; "semanticGuidance" ]
                        props

                let! schemaVersion = intProperty "schemaVersion" props
                let! id = stringProperty "id" props
                let! description = optionalStringProperty "description" props
                let! idPrefix = optionalStringProperty "idPrefix" props

                let! routing =
                    match tryProperty "routing" props with
                    | None -> Ok None
                    | Some value -> parseRouting value |> Result.map Some

                let! capabilityEnvelope =
                    match tryProperty "capabilityEnvelope" props with
                    | None -> Ok None
                    | Some value -> parseCapabilityEnvelopeOverlay value |> Result.map Some

                let! policy =
                    match tryProperty "policy" props with
                    | None -> Ok None
                    | Some value -> parsePolicyOverlay value |> Result.map Some

                let! semanticGuidance =
                    match tryProperty "semanticGuidance" props with
                    | None -> Ok None
                    | Some value -> parseSemanticGuidanceOverlay value |> Result.map Some

                return
                    { SchemaVersion = schemaVersion
                      Id = id
                      Description = if String.IsNullOrWhiteSpace description then None else Some description
                      IdPrefix = if String.IsNullOrWhiteSpace idPrefix then None else Some idPrefix
                      Routing = routing
                      CapabilityEnvelope = capabilityEnvelope
                      Policy = policy
                      SemanticGuidance = semanticGuidance }
            }
        with
        | :? JsonException as error -> Error (InvalidInput $"invalid JSON: {error.Message}")
        | :? FormatException as error -> Error (InvalidInput $"invalid JSON value: {error.Message}")

module private Domain =
    let private collectResults values =
        values
        |> List.fold
            (fun state item ->
                result {
                    let! collected = state
                    let! value = item
                    return value :: collected
                })
            (Ok [])
        |> Result.map List.rev

    let private optionalNonEmpty name value =
        match value with
        | None -> Ok None
        | Some text when String.IsNullOrWhiteSpace text -> Ok None
        | Some text -> nonEmpty name text |> Result.map Some

    let private validateEvidenceRefs (refs: string list) =
        if refs.IsEmpty || refs |> List.exists (fun value -> String.IsNullOrWhiteSpace value) then
            Error (InvalidInput "evidenceRefs must contain at least one non-empty value")
        else
            Ok(refs |> List.map (fun value -> value.Trim()))

    // Shared by the wire boundary and the command boundary so that command
    // payloads cannot persist evidence that the sidecar reader would reject.
    let private validateEvidence (evidence: Evidence) =
        result {
            let! id = evidenceId evidence.Id
            let! kind =
                match evidence.Kind with
                | EvidenceKind.Other value when String.IsNullOrWhiteSpace value ->
                    Error (InvalidInput "evidence kind 'other' requires a non-empty value")
                | kind -> Ok kind
            let! source =
                match evidence.Source with
                | EvidenceSource value -> nonEmpty "evidence source" value |> Result.map EvidenceSource
            let! subject = optionalNonEmpty "evidence subject" evidence.Subject
            let! producerRole = optionalNonEmpty "evidence producerRole" evidence.ProducerRole
            let! producerId = optionalNonEmpty "evidence producerId" evidence.ProducerId
            let! reference = optionalNonEmpty "evidence reference" evidence.Reference
            let! summary = nonEmpty "evidence summary" evidence.Summary
            return
                { evidence with
                    Id = id
                    Kind = kind
                    Source = source
                    Subject = subject
                    ProducerRole = producerRole
                    ProducerId = producerId
                    Reference = reference
                    Summary = summary }
        }

    let private evidenceFromDto (dto: EvidenceDto) =
        result {
            let! kind = parseEvidenceKind dto.Kind
            let! validity =
                match dto.Validity, dto.SupersededReason with
                | "valid", "" -> Ok Valid
                | "superseded", reason -> nonEmpty "superseded reason" reason |> Result.map Superseded
                | "valid", _ -> Error (InvalidInput $"valid evidence '{dto.Id}' cannot contain a superseded reason")
                | _ -> Error (InvalidInput $"evidence '{dto.Id}' has an invalid validity")
            let! evidence =
                validateEvidence
                    { Id = dto.Id
                      Kind = kind
                      Source = EvidenceSource dto.Source
                      Subject = if String.IsNullOrWhiteSpace dto.Subject then None else Some dto.Subject
                      ProducerRole = if String.IsNullOrWhiteSpace dto.ProducerRole then None else Some dto.ProducerRole
                      ProducerId = if String.IsNullOrWhiteSpace dto.ProducerId then None else Some dto.ProducerId
                      Reference = if String.IsNullOrWhiteSpace dto.Reference then None else Some dto.Reference
                      Summary = dto.Summary }
            return { Evidence = evidence; Validity = validity }
        }

    let private acceptanceFromDto (dto: AcceptanceDto) =
        result {
            let! id = acceptanceId dto.Id
            let! text = nonEmpty "acceptance text" dto.Text
            let! state =
                match dto.State, dto.EvidenceRefs with
                | "pending", [] -> Ok Pending
                | "verified", refs -> validateEvidenceRefs refs |> Result.map Verified
                | "pending", _ -> Error (InvalidInput $"pending acceptance '{dto.Id}' cannot contain evidence")
                | _ -> Error (InvalidInput $"acceptance '{dto.Id}' has an invalid state")
            return { Id = id; Text = text; State = state }
        }

    // Section 30: ownership is a durable logical role plus an optional concrete
    // identity. Shape only; platform route existence belongs to integration.
    let private validateOwner (owner: Owner) : Result<Owner, RuntimeError> =
        result {
            let! role = nonEmpty "owner role" owner.Role
            let! agentId = optionalNonEmpty "owner agentId" owner.AgentId
            return { Role = role; AgentId = agentId }
        }

    let private ownerFromDto (dto: OwnerDto) : Result<Owner, RuntimeError> =
        validateOwner
            { Role = dto.Role
              AgentId =
                if String.IsNullOrWhiteSpace dto.AgentId then
                    None
                else
                    Some dto.AgentId }

    // Section 27.1: handoff prose is structurally validated, never semantically.
    let private validateTerminalHandoff (handoff: TerminalHandoff) : Result<TerminalHandoff, RuntimeError> =
        result {
            let! state = nonEmpty "terminal handoff state" handoff.State
            let! evidenceSummary = nonEmpty "terminal handoff evidenceSummary" handoff.EvidenceSummary
            let! next = nonEmpty "terminal handoff next" handoff.Next
            return { State = state; EvidenceSummary = evidenceSummary; Next = next }
        }

    let private handoffFromDto (dto: TerminalHandoffDto) : Result<TerminalHandoff, RuntimeError> =
        validateTerminalHandoff
            { State = dto.State
              EvidenceSummary = dto.EvidenceSummary
              Next = dto.Next }

    let rec private flattenItems (items: WorkItem list) =
        items |> List.collect (fun item -> item :: flattenItems item.Children)

    let rec findWorkItem id (items: WorkItem list) =
        items
        |> List.tryPick (fun item -> if item.Id = id then Some item else findWorkItem id item.Children)

    let private findAcceptance id (criteria: Acceptance list) =
        criteria |> List.tryFind (fun item -> item.Id = id)

    let private findDecision id (decisions: Decision list) =
        decisions |> List.tryFind (fun decision -> decision.Id = id)

    // Section 5.2: every reopen target must name an existing entity so the
    // invalidation is explicit rather than a bare lifecycle flip.
    let private validateReopenTarget (task: TaskModel) target =
        match target with
        | ReopenTarget.AcceptanceCriterionTarget id ->
            result {
                let! valid = acceptanceId id

                if findAcceptance valid task.AcceptanceCriteria |> Option.isNone then
                    return! Error (InvalidInput $"reopen targets unknown Acceptance Criterion '{valid}'")

                return ReopenTarget.AcceptanceCriterionTarget valid
            }
        | ReopenTarget.WorkItemTarget id ->
            result {
                let! valid = workItemId id

                if findWorkItem valid task.WorkItems |> Option.isNone then
                    return! Error (InvalidInput $"reopen targets unknown WorkItem '{valid}'")

                return ReopenTarget.WorkItemTarget valid
            }
        | ReopenTarget.GuardTarget id ->
            result {
                let! valid = guardId id

                if task.Guards |> List.exists (fun guard -> guard.Id = valid) |> not then
                    return! Error (InvalidInput $"reopen targets unknown Guard '{valid}'")

                return ReopenTarget.GuardTarget valid
            }

    let private validateReopenRequest (task: TaskModel) (request: ReopenRequest) =
        result {
            let! reason = nonEmpty "reopen reason" request.Reason

            if request.Targets.IsEmpty then
                return! Error (InvalidInput "reopening requires at least one invalidation target")

            let! targets = request.Targets |> List.map (validateReopenTarget task) |> collectResults

            if (Set.ofList targets).Count <> targets.Length then
                return! Error (InvalidInput "reopen targets must be unique")

            return { request with Reason = reason; Targets = targets }
        }

    // Section 9.2/15.4: a Decision authorizes only when its trusted authority
    // satisfies the required minimum and a typed target exactly matches. User
    // authority has no trusted ingress until #13, and ProfilePolicy never
    // participates in ExplicitDecision checks, so both fail closed.
    let private decisionAuthorizes (decision: Decision) (minimumAuthority: MinimumAuthority) (target: DecisionTarget) =
        let authoritySatisfied =
            match decision.Authority with
            | User -> false
            | ProfilePolicy -> false
            | Coordinator -> minimumAuthority = MinimumAuthority.CoordinatorAuthority

        authoritySatisfied && decision.Targets |> List.contains target

    // Section 15.3: the Guard's own policy decides which authority a
    // non-Applicable disposition requires.
    let private dispositionAuthority (guard: Guard) =
        match guard.Disposition with
        | GuardDisposition.NotApplicable _ ->
            match guard.Applicability with
            | ExplicitDecision authority -> Some authority
            | Always -> None
        | GuardDisposition.Waived _ ->
            match guard.Waiver with
            | WaivableBy authority -> Some authority
            | NotWaivable -> None
        | GuardDisposition.Applicable -> None

    // Section 9.2/15.3: each disposition is authorized only by its own Decision
    // kind. A same-target Decision of the wrong kind (for example an
    // ApplicabilityDecision reused for a waiver) never authorizes the disposition.
    let private dispositionDecisionKind (guard: Guard) =
        match guard.Disposition with
        | GuardDisposition.NotApplicable _ -> Some ApplicabilityDecision
        | GuardDisposition.Waived _ -> Some WaiverDecision
        | GuardDisposition.Applicable -> None

    // A persisted disposition reference is never trusted on its own: it must
    // resolve to an existing Decision of the disposition's kind that authorizes
    // this exact Guard target.
    let private dispositionSatisfied (task: TaskModel) (guard: Guard) (reference: string) =
        match dispositionAuthority guard, dispositionDecisionKind guard with
        | Some authority, Some requiredKind ->
            match decisionId reference with
            | Error _ -> false
            | Ok referenceId ->
                match findDecision referenceId task.Decisions with
                | Some decision ->
                    decision.Kind = requiredKind
                    && decisionAuthorizes decision authority (GuardDispositionTarget guard.Id)
                | None -> false
        | _ -> false

    // The recorded disposition identifies which command must be re-authorized.
    let private dispositionOperation (guard: Guard) =
        match guard.Disposition with
        | GuardDisposition.NotApplicable _ -> "task_apply.mark-not-applicable"
        | GuardDisposition.Waived _
        | GuardDisposition.Applicable -> "task_apply.waive-guard"

    let private dispositionError (task: TaskModel) (guard: Guard) (reference: string) =
        match dispositionAuthority guard with
        | None ->
            InvalidInput $"guard '{guard.Id}' policy does not permit a {guardDispositionToString guard.Disposition} disposition"
        | Some authority ->
            match decisionId reference with
            | Error error -> error
            | Ok referenceId ->
                match findDecision referenceId task.Decisions with
                | None ->
                    AuthorityDenied(
                        { RequiredAuthority = authority
                          Operation = dispositionOperation guard
                          DecisionKind = dispositionDecisionKind guard
                          Target = Some(GuardDispositionTarget guard.Id)
                          DecisionRefStatus = DecisionRefStatus.Absent },
                        $"guard '{guard.Id}' disposition references unknown Decision '{referenceId}'"
                    )
                | Some decision ->
                    let requiredKind =
                        dispositionDecisionKind guard |> Option.defaultValue ApplicabilityDecision

                    AuthorityDenied(
                        { RequiredAuthority = authority
                          Operation = dispositionOperation guard
                          DecisionKind = Some requiredKind
                          Target = Some(GuardDispositionTarget guard.Id)
                          DecisionRefStatus = DecisionRefStatus.Mismatched },
                        $"guard '{guard.Id}' disposition Decision '{referenceId}' has kind {decisionKindToString decision.Kind} and does not authorize the exact Guard target at {minimumAuthorityToString authority} authority as kind {decisionKindToString requiredKind}"
                    )

    let private validateGuardDisposition (task: TaskModel) (guard: Guard) =
        match guard.Disposition with
        | GuardDisposition.Applicable -> Ok()
        | GuardDisposition.NotApplicable reference
        | GuardDisposition.Waived reference ->
            if dispositionSatisfied task guard reference then
                Ok()
            else
                Error(dispositionError task guard reference)

    // Section 9: targets are typed and must reference existing entities. A
    // Decision is provenance-only here, so this validates structure, not authority.
    let private validateDecisionTarget (task: TaskModel) target =
        match target with
        | WaiveAcceptanceTarget id ->
            result {
                let! valid = acceptanceId id

                if findAcceptance valid task.AcceptanceCriteria |> Option.isNone then
                    return! Error (InvalidInput $"decision targets unknown Acceptance Criterion '{valid}'")

                return WaiveAcceptanceTarget valid
            }
        | GuardDispositionTarget id ->
            result {
                let! valid = guardId id

                if task.Guards |> List.exists (fun guard -> guard.Id = valid) |> not then
                    return! Error (InvalidInput $"decision targets unknown Guard '{valid}'")

                return GuardDispositionTarget valid
            }
        | SkipWorkItemTarget id ->
            result {
                let! valid = workItemId id

                if findWorkItem valid task.WorkItems |> Option.isNone then
                    return! Error (InvalidInput $"decision targets unknown WorkItem '{valid}'")

                return SkipWorkItemTarget valid
            }
        | RequirementChangeTarget id ->
            result {
                let! valid = workItemId id

                if findWorkItem valid task.WorkItems |> Option.isNone then
                    return! Error (InvalidInput $"decision targets unknown WorkItem '{valid}'")

                return RequirementChangeTarget valid
            }
        | ReopenTaskTarget (taskIdText, targets) ->
            result {
                let! validTaskId = taskId taskIdText

                if validTaskId <> task.Id then
                    return!
                        Error (
                            InvalidInput
                                $"decision targets reopen of task '{validTaskId}' instead of '{task.Id}'"
                        )

                if targets.IsEmpty then
                    return! Error (InvalidInput "a reopen decision target requires at least one invalidation target")

                let! validTargets = targets |> List.map (validateReopenTarget task) |> collectResults
                return ReopenTaskTarget(validTaskId, validTargets)
            }
        | QuestionResolutionTarget id ->
            result {
                let! valid = questionId id

                if task.Questions |> List.exists (fun question -> question.Id = valid) |> not then
                    return! Error (InvalidInput $"decision targets unknown Question '{valid}'")

                return QuestionResolutionTarget valid
            }
        | ContractPatchTarget id ->
            // The exact canonical patch payload is the security target, so the
            // patch id is a deterministic fingerprint rather than an entity ref.
            nonEmpty "contract patch id" id |> Result.map ContractPatchTarget
        | ReclassificationTarget text ->
            nonEmpty "reclassification target" text |> Result.map ReclassificationTarget
        | OtherDecisionTarget text ->
            nonEmpty "decision target" text |> Result.map OtherDecisionTarget

    // Section 10: WorkItem-scoped impact references existing, unique WorkItems.
    let private validateQuestionImpact (task: TaskModel) (questionIdText: string) (impact: QuestionImpact) =
        match impact with
        | TaskWide -> Ok TaskWide
        | WorkItems ids when ids.IsEmpty ->
            Error (InvalidInput $"question '{questionIdText}' WorkItems impact requires at least one WorkItem")
        | WorkItems ids ->
            result {
                let! valid = ids |> List.map workItemId |> collectResults

                if (Set.ofList valid).Count <> valid.Length then
                    return! Error (InvalidInput $"question '{questionIdText}' WorkItems impact must be unique")

                let unknown =
                    valid |> List.filter (fun id -> findWorkItem id task.WorkItems |> Option.isNone)

                if not unknown.IsEmpty then
                    return! Error (InvalidInput $"question '{questionIdText}' references unknown WorkItem '{unknown.Head}'")

                return WorkItems valid
            }

    let private validateDecisionGraph (task: TaskModel) =
        let duplicateDecisions =
            if (Set.ofList (task.Decisions |> List.map _.Id)).Count <> task.Decisions.Length then
                Some(InvalidInput "Decision IDs must be unique")
            else
                None

        let duplicateQuestions =
            if (Set.ofList (task.Questions |> List.map _.Id)).Count <> task.Questions.Length then
                Some(InvalidInput "Question IDs must be unique")
            else
                None

        let targetError =
            task.Decisions
            |> List.collect (fun decision -> decision.Targets)
            |> List.tryPick (fun target ->
                match validateDecisionTarget task target with
                | Error error -> Some error
                | Ok _ -> None)

        let impactError =
            task.Questions
            |> List.tryPick (fun question ->
                match validateQuestionImpact task question.Id question.Impact with
                | Error error -> Some error
                | Ok _ -> None)

        let resolutionError =
            task.Questions
            |> List.tryPick (fun question ->
                match question.State with
                | Open -> None
                | Resolved (DecisionRef reference) ->
                    match findDecision reference task.Decisions with
                    | None -> Some(InvalidInput $"question '{question.Id}' references unknown Decision '{reference}'")
                    | Some decision when decision.Targets |> List.contains (QuestionResolutionTarget question.Id) -> None
                    | Some _ ->
                        Some(
                            InvalidInput
                                $"question '{question.Id}' resolution Decision '{reference}' does not target it"
                        ))

        // A persisted Guard disposition must be backed by an exact target-bound
        // Decision; a raw sidecar claim is never accepted on its own.
        let guardDispositionError =
            task.Guards
            |> List.tryPick (fun guard ->
                match validateGuardDisposition task guard with
                | Error error -> Some error
                | Ok() -> None)

        match
            duplicateDecisions
            |> Option.orElse duplicateQuestions
            |> Option.orElse targetError
            |> Option.orElse impactError
            |> Option.orElse resolutionError
            |> Option.orElse guardDispositionError
        with
        | Some error -> Error error
        | None -> Ok()

    let private questionBlocksWorkItem (task: TaskModel) (workItemIdText: string) =
        task.Questions
        |> List.exists (fun question ->
            match question.State, question.Impact with
            | Open, TaskWide -> true
            | Open, WorkItems ids -> List.contains workItemIdText ids
            | _ -> false)

    let private hasOpenTaskWideQuestion (task: TaskModel) =
        task.Questions
        |> List.exists (fun question ->
            match question.State, question.Impact with
            | Open, TaskWide -> true
            | _ -> false)

    let private nextDecisionId (task: TaskModel) =
        let highest =
            task.Decisions
            |> List.choose (fun decision ->
                if decision.Id.StartsWith("D", StringComparison.Ordinal) then
                    match Int32.TryParse(decision.Id.Substring 1) with
                    | true, value -> Some value
                    | _ -> None
                else
                    None)
            |> List.fold max 0

        $"D{highest + 1}"

    // Ordinary input is Coordinator-only; Id/Authority/CreatedAt are runtime-assigned.
    let private validateDecisionDraft
        (now: DateTimeOffset)
        (task: TaskModel)
        (draft: DecisionDraft)
        : Result<Decision, RuntimeError> =
        result {
            let! rationale = nonEmpty "decision rationale" draft.Rationale

            if draft.Targets.IsEmpty then
                return! Error (InvalidInput "a decision requires at least one target")

            let! targets = draft.Targets |> List.map (validateDecisionTarget task) |> collectResults

            return
                { Id = nextDecisionId task
                  Authority = Coordinator
                  Kind = draft.Kind
                  Targets = targets
                  Rationale = rationale
                  CreatedAt = now
                  ConfirmationRef = None }
        }

    let private validateQuestionDraft (task: TaskModel) (draft: QuestionDraft) : Result<Question, RuntimeError> =
        result {
            let! id = questionId draft.Id

            if task.Questions |> List.exists (fun question -> question.Id = id) then
                return! Error (InvalidInput $"question '{id}' already exists")

            let! text = nonEmpty "question text" draft.Text
            let! impact = validateQuestionImpact task id draft.Impact
            return { Id = id; Text = text; Impact = impact; State = Open }
        }

    let rec private mapWorkItem id update (items: WorkItem list) =
        items
        |> List.map (fun item ->
            if item.Id = id then
                update item
            else
                { item with Children = mapWorkItem id update item.Children })

    // Path is root-first and ends at the matched item; empty when not found.
    let rec private itemPath id (items: WorkItem list) =
        items
        |> List.tryPick (fun item ->
            if item.Id = id then
                Some [ item ]
            else
                match itemPath id item.Children with
                | Some rest -> Some(item :: rest)
                | None -> None)

    let private ancestorItems id items =
        match itemPath id items with
        | Some path -> path |> List.take (path.Length - 1)
        | None -> []

    let private descendantItems (item: WorkItem) = flattenItems item.Children

    // Section 30: a WorkItem without an explicit owner inherits the nearest
    // ancestor owner; a root with no owner has no effective owner.
    let effectiveOwner (task: TaskModel) (workItemIdText: string) =
        match itemPath workItemIdText task.WorkItems with
        | Some path -> path |> List.rev |> List.tryPick (fun item -> item.Owner)
        | None -> None

    let private isTerminalState state =
        match state with
        | DoneWork
        | SkippedWork _ -> true
        | _ -> false

    let private stateName state =
        match state with
        | PendingWork -> "pending"
        | ActiveWork -> "active"
        | WaitingWork _ -> "waiting"
        | BlockedWork _ -> "blocked"
        | DoneWork -> "done"
        | SkippedWork _ -> "skipped"

    let private findDependencyCycle (items: WorkItem list) =
        let deps = items |> List.map (fun item -> item.Id, item.DependsOn) |> Map.ofList
        let mutable visited = Set.empty

        let rec visit (path: string list) id =
            if List.contains id path then
                Some(List.rev (id :: path))
            elif visited.Contains id then
                None
            else
                visited <- Set.add id visited
                let targets = Map.tryFind id deps |> Option.defaultValue []

                let rec walk remaining =
                    match remaining with
                    | [] -> None
                    | target :: rest ->
                        match visit (id :: path) target with
                        | Some cycle -> Some cycle
                        | None -> walk rest

                walk targets

        deps |> Map.toList |> List.map fst |> List.tryPick (visit [])

    // Same-task dependency scope plus deterministic cycle rejection.
    let private validateWorkTreeDependencies (items: WorkItem list) =
        let all = flattenItems items
        let idSet = all |> List.map _.Id |> Set.ofList

        let scoped =
            all
            |> List.collect (fun item ->
                let ancestors = ancestorItems item.Id items |> List.map _.Id |> Set.ofList
                let descendants = descendantItems item |> List.map _.Id |> Set.ofList

                item.DependsOn
                |> List.choose (fun dependency ->
                    if dependency = item.Id then
                        Some(InvalidInput $"WorkItem '{item.Id}' cannot depend on itself")
                    elif not (idSet.Contains dependency) then
                        Some(InvalidInput $"WorkItem '{item.Id}' depends on unknown WorkItem '{dependency}'")
                    elif ancestors.Contains dependency then
                        Some(InvalidInput $"WorkItem '{item.Id}' cannot depend on ancestor '{dependency}'")
                    elif descendants.Contains dependency then
                        Some(InvalidInput $"WorkItem '{item.Id}' cannot depend on descendant '{dependency}'")
                    else
                        None))

        match scoped with
        | error :: _ -> Error error
        | [] ->
            match findDependencyCycle all with
            | Some cycle ->
                let path = String.concat " -> " cycle
                Error(InvalidInput $"WorkItem dependency cycle detected: {path}")
            | None -> Ok()

    let private validatedResumeCondition (ResumeCondition value) =
        nonEmpty "resume condition" value |> Result.map ResumeCondition

    let private validatedBlocker (Blocker value) =
        nonEmpty "blocker" value |> Result.map Blocker

    let private validatedObservation (ObservationRef value) =
        nonEmpty "observation reference" value |> Result.map ObservationRef

    let rec private workItemFromDto (dto: WorkItemDto) =
        result {
            let! id = workItemId dto.Id
            let! title = nonEmpty "work item title" dto.Title
            let! state =
                match dto.State, dto.Result, dto.ResumeCondition, dto.Blocker, dto.SkipDisposition with
                | "pending", "", "", "", "" -> Ok PendingWork
                | "active", "", "", "", "" -> Ok ActiveWork
                | "waiting", "", condition, "", "" when not (String.IsNullOrWhiteSpace condition) ->
                    Ok(WaitingWork(ResumeCondition(condition.Trim())))
                | "blocked", "", "", blocker, "" when not (String.IsNullOrWhiteSpace blocker) ->
                    Ok(BlockedWork(Blocker(blocker.Trim())))
                | "done", result, "", "", "" when not (String.IsNullOrWhiteSpace result) -> Ok DoneWork
                | "skipped", "", "", "", disposition ->
                    parseSkipDisposition disposition |> Result.map SkippedWork
                | _ -> Error (InvalidInput $"work item '{dto.Id}' has an invalid state/payload combination")
            let! refs = dto.AcceptanceRefs |> List.map acceptanceId |> collectResults
            let! dependsOn = dto.DependsOn |> List.map workItemId |> collectResults
            let! evidenceRefs = dto.EvidenceRefs |> List.map evidenceId |> collectResults
            let! owner =
                match dto.Owner with
                | None -> Ok None
                | Some value -> ownerFromDto value |> Result.map Some
            let! children = dto.Children |> List.map workItemFromDto |> collectResults
            let item : WorkItem =
                { Id = id
                  Title = title
                  State = state
                  Result = if dto.Result = "" then None else Some dto.Result
                  AcceptanceRefs = refs
                  DependsOn = dependsOn
                  EvidenceRefs = evidenceRefs
                  Owner = owner
                  Children = children }
            return item
        }

    let private guardFromDto (dto: GuardDto) =
        result {
            let! id = guardId dto.Id
            let! target = parseGuardTarget dto.Target
            let! checkpoint = parseGuardCheckpoint dto.Checkpoint
            let! origin = parseGuardOrigin dto.Origin
            let! requirement =
                if dto.Requirement <> "evidenceRequired" then
                    Error (InvalidInput $"guard '{dto.Id}' has an unsupported requirement")
                else
                    result {
                        let! kind = parseEvidenceKind dto.EvidenceKind

                        if dto.MinimumCount < 1 then
                            return! Error (InvalidInput $"guard '{dto.Id}' minimumCount must be at least 1")

                        let! producerRole =
                            optionalNonEmpty
                                "guard producerRole"
                                (if String.IsNullOrWhiteSpace dto.ProducerRole then
                                     None
                                 else
                                     Some dto.ProducerRole)

                        return
                            EvidenceRequired
                                { Kind = kind
                                  MinimumCount = dto.MinimumCount
                                  ProducerRole = producerRole
                                  RequireIndependentProducer = dto.RequireIndependentProducer }
                    }
            let! applicability = parseApplicability dto.Applicability
            let! waiver = parseWaiver dto.Waiver
            let! disposition = parseGuardDisposition dto.Disposition
            return
                { Id = id
                  Target = target
                  Checkpoint = checkpoint
                  Origin = origin
                  Requirement = requirement
                  Applicability = applicability
                  Waiver = waiver
                  Disposition = disposition }
        }

    let private decisionFromDto (dto: DecisionDto) : Result<Decision, RuntimeError> =
        result {
            let! id = decisionId dto.Id
            let! authority = parseDecisionAuthority dto.Authority

            // Sidecar JSON is untrusted: only Coordinator provenance is accepted.
            // User and ProfilePolicy Decisions cannot be verified without the #13
            // signed-attestation bridge, so they are rejected explicitly rather
            // than silently demoted.
            do!
                match authority with
                | User ->
                    Error(
                        AuthorityDenied(
                            { RequiredAuthority = MinimumAuthority.UserAuthority
                              Operation = "task_apply.add-decision"
                              DecisionKind = None
                              Target = None
                              DecisionRefStatus = DecisionRefStatus.Absent },
                            $"decision '{dto.Id}' claims User authority, which is rejected until the #13 signed-attestation bridge verifies it"
                        )
                    )
                | ProfilePolicy ->
                    Error(InvalidInput $"decision '{dto.Id}' claims ProfilePolicy provenance, which serialized state cannot establish")
                | Coordinator -> Ok()

            let! kind = parseDecisionKind dto.Kind

            if dto.Targets.IsEmpty then
                return! Error (InvalidInput $"decision '{dto.Id}' requires at least one target")

            let! targets = dto.Targets |> List.map parseDecisionTarget |> collectResults
            let! rationale = nonEmpty "decision rationale" dto.Rationale

            let! createdAt =
                match
                    DateTimeOffset.TryParse(
                        dto.CreatedAt,
                        Globalization.CultureInfo.InvariantCulture,
                        Globalization.DateTimeStyles.RoundtripKind
                    )
                with
                | true, value -> Ok value
                | false, _ -> Error (InvalidInput $"decision '{dto.Id}' createdAt must be an ISO-8601 timestamp")

            let! confirmationRef =
                optionalNonEmpty
                    "decision confirmationRef"
                    (if String.IsNullOrWhiteSpace dto.ConfirmationRef then
                         None
                     else
                         Some dto.ConfirmationRef)

            do!
                match confirmationRef with
                | Some _ ->
                    Error(
                        AuthorityDenied(
                            { RequiredAuthority = MinimumAuthority.UserAuthority
                              Operation = "task_apply.add-decision"
                              DecisionKind = None
                              Target = None
                              DecisionRefStatus = DecisionRefStatus.Absent },
                            $"decision '{dto.Id}' carries an unverifiable confirmationRef, which is rejected until the #13 signed-attestation bridge verifies it"
                        )
                    )
                | None -> Ok()

            return
                { Id = id
                  Authority = authority
                  Kind = kind
                  Targets = targets
                  Rationale = rationale
                  CreatedAt = createdAt
                  ConfirmationRef = confirmationRef }
        }

    let private questionFromDto (dto: QuestionDto) : Result<Question, RuntimeError> =
        result {
            let! id = questionId dto.Id
            let! text = nonEmpty "question text" dto.Text

            let! impact =
                match dto.Impact, dto.WorkItems with
                | "taskWide", [] -> Ok TaskWide
                | "workItems", ids when not ids.IsEmpty ->
                    result {
                        let! valid = ids |> List.map workItemId |> collectResults

                        if (Set.ofList valid).Count <> valid.Length then
                            return! Error (InvalidInput $"question '{dto.Id}' WorkItems impact must be unique")

                        return WorkItems valid
                    }
                | "taskWide", _ -> Error (InvalidInput $"TaskWide question '{dto.Id}' cannot declare WorkItems")
                | "workItems", [] ->
                    Error (InvalidInput $"question '{dto.Id}' WorkItems impact requires at least one WorkItem")
                | _ -> Error (InvalidInput $"question '{dto.Id}' has an invalid impact")

            let! state =
                match dto.State, dto.Resolution with
                | "open", "" -> Ok Open
                | "resolved", reference when not (String.IsNullOrWhiteSpace reference) ->
                    decisionId reference |> Result.map (DecisionRef >> Resolved)
                | "open", _ -> Error (InvalidInput $"open question '{dto.Id}' cannot declare a resolution")
                | "resolved", _ -> Error (InvalidInput $"resolved question '{dto.Id}' requires a decision reference")
                | _ -> Error (InvalidInput $"question '{dto.Id}' has an invalid state")

            return { Id = id; Text = text; Impact = impact; State = state }
        }

    let fromDto (profiles: Map<string, EffectiveProfile>) (strictProfile: bool) (dto: TaskDto) =
        result {
            if dto.SchemaVersion <> SchemaVersion then
                return! Error (InvalidInput $"unsupported schemaVersion {dto.SchemaVersion}")
            let! id = taskId dto.Id
            let! title = nonEmpty "task title" dto.Title
            let! created =
                match DateTimeOffset.TryParse(dto.Created, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
                | true, value -> Ok value
                | false, _ -> Error (InvalidInput "created must be an ISO-8601 timestamp")
            let! kind = parseKind dto.Kind

            // Section 20: production reads tolerate a fingerprint mismatch so
            // drift can be surfaced and reconciled; the strict built-in reader
            // still rejects unknown profiles and mismatched fingerprints.
            do!
                if String.IsNullOrWhiteSpace dto.Profile then
                    Error(InvalidInput "profile is required")
                elif strictProfile then
                    match Map.tryFind dto.Profile profiles with
                    | None -> Error(InvalidInput $"unknown profile '{dto.Profile}'")
                    | Some profile when profile.Fingerprint <> dto.ProfileFingerprint ->
                        Error(InvalidInput $"{dto.Profile} profile fingerprint does not match")
                    | Some _ -> Ok()
                elif String.IsNullOrWhiteSpace dto.ProfileFingerprint then
                    Error(InvalidInput "profileFingerprint is required")
                else
                    Ok()

            let! contractState = parseContractState dto.ContractState
            let! contractFingerprint =
                match contractState, dto.ContractFingerprint with
                | Draft, "" -> Ok ""
                | Draft, _ -> Error (InvalidInput "a draft contract cannot carry a recorded fingerprint")
                | Baselined, value when not (String.IsNullOrWhiteSpace value) -> Ok value
                | Baselined, _ -> Error (InvalidInput "a baselined contract requires a recorded fingerprint")
            if dto.ContractRevision < 1 || dto.StateRevision < 0 then
                return! Error (InvalidInput "revision values are out of range")
            if dto.Lifecycle <> "open" && dto.Lifecycle <> "complete" && dto.Lifecycle <> "aborted" then
                return! Error (InvalidInput "lifecycle must be 'open', 'complete', or 'aborted'")
            let! evidence = dto.Evidence |> List.map evidenceFromDto |> collectResults
            let! acceptanceCriteria = dto.AcceptanceCriteria |> List.map acceptanceFromDto |> collectResults
            let! guards = dto.Guards |> List.map guardFromDto |> collectResults
            let! decisions = dto.Decisions |> List.map decisionFromDto |> collectResults
            let! questions = dto.Questions |> List.map questionFromDto |> collectResults
            let! workItems = dto.WorkItems |> List.map workItemFromDto |> collectResults
            let! terminalHandoff =
                match dto.TerminalHandoff with
                | None -> Ok None
                | Some handoff -> handoffFromDto handoff |> Result.map Some
            let! completionHistory = dto.CompletionHistory |> List.map handoffFromDto |> collectResults
            if acceptanceCriteria.IsEmpty then
                return! Error (InvalidInput "a task requires at least one Acceptance Criterion")
            if workItems.IsEmpty then
                return! Error (InvalidInput "a task requires at least one WorkItem")
            let allItems = flattenItems workItems
            let allAcceptanceIds = acceptanceCriteria |> List.map _.Id
            let allWorkItemIds = allItems |> List.map _.Id
            let allEvidenceIds = evidence |> List.map _.Evidence.Id
            let allGuardIds = guards |> List.map _.Id
            if (Set.ofList allAcceptanceIds).Count <> allAcceptanceIds.Length then
                return! Error (InvalidInput "Acceptance Criterion IDs must be unique")
            if (Set.ofList allWorkItemIds).Count <> allWorkItemIds.Length then
                return! Error (InvalidInput "WorkItem IDs must be unique")
            if (Set.ofList allEvidenceIds).Count <> allEvidenceIds.Length then
                return! Error (InvalidInput "Evidence IDs must be unique")
            if (Set.ofList allGuardIds).Count <> allGuardIds.Length then
                return! Error (InvalidInput "Guard IDs must be unique")
            let! profileGuardKeys =
                if
                    (dto.ProfileGuardKeys |> List.map fst |> Set.ofList |> Set.count)
                    <> dto.ProfileGuardKeys.Length
                then
                    Error(InvalidInput "profile guard key provenance must be unique per Guard")
                else
                    let keyMap = dto.ProfileGuardKeys |> Map.ofList
                    let profileGuardIds =
                        guards
                        |> List.filter (fun guard -> guard.Origin = ProfileMaterialized)
                        |> List.map _.Id
                        |> Set.ofList
                    let persistedIds = keyMap |> Map.keys |> Set.ofSeq
                    let missing = Set.difference profileGuardIds persistedIds |> Set.toList
                    let stale = Set.difference persistedIds profileGuardIds |> Set.toList

                    if not missing.IsEmpty then
                        Error(InvalidInput $"profile guard key provenance is missing Guard '{missing.Head}'")
                    elif not stale.IsEmpty then
                        Error(InvalidInput $"profile guard key provenance contains stale Guard '{stale.Head}'")
                    elif keyMap |> Map.exists (fun _ key -> String.IsNullOrWhiteSpace key) then
                        Error(InvalidInput "profile guard key provenance values must be non-empty")
                    else
                        Ok keyMap
            let validEvidenceIds =
                evidence
                |> List.filter (fun record -> record.Validity = Valid)
                |> List.map (fun record -> record.Evidence.Id)
            let unknownRefs =
                allItems
                |> List.collect _.AcceptanceRefs
                |> List.filter (fun reference -> not (List.contains reference allAcceptanceIds))
            if not unknownRefs.IsEmpty then
                return! Error (InvalidInput $"WorkItem references unknown Acceptance Criterion '{unknownRefs.Head}'")
            let unknownItemEvidence =
                allItems
                |> List.collect _.EvidenceRefs
                |> List.filter (fun reference -> not (List.contains reference validEvidenceIds))
            if not unknownItemEvidence.IsEmpty then
                return!
                    Error (
                        InvalidInput
                            $"WorkItem references unknown or superseded Evidence '{unknownItemEvidence.Head}'"
                    )
            let unknownGuardTargets =
                guards
                |> List.choose (fun guard ->
                    match guard.Target with
                    | WorkItemTarget targetId when not (List.contains targetId allWorkItemIds) -> Some targetId
                    | _ -> None)
            if not unknownGuardTargets.IsEmpty then
                return! Error (InvalidInput $"Guard references unknown WorkItem '{unknownGuardTargets.Head}'")
            let invalidEvidenceRefs =
                acceptanceCriteria
                |> List.collect (fun criterion ->
                    match criterion.State with
                    | Verified refs -> refs
                    | Pending -> [])
                |> List.filter (fun reference -> not (List.contains reference validEvidenceIds))
            if not invalidEvidenceRefs.IsEmpty then
                return!
                    Error (
                        InvalidInput
                            $"Acceptance Criterion references unknown or superseded evidence '{invalidEvidenceRefs.Head}'"
                    )
            do! validateWorkTreeDependencies workItems
            let task =
                { Id = id
                  Title = title
                  Created = created
                  Kind = kind
                  Profile = dto.Profile
                  ProfileFingerprint = dto.ProfileFingerprint
                  Objective = dto.Objective
                  Scope = dto.Scope
                  NonGoals = dto.NonGoals
                  ContractState = contractState
                  ContractFingerprint = contractFingerprint
                  ContractRevision = dto.ContractRevision
                  StateRevision = dto.StateRevision
                  Lifecycle = dto.Lifecycle
                  Evidence = evidence
                  AcceptanceCriteria = acceptanceCriteria
                  Guards = guards
                  ProfileGuardKeys = profileGuardKeys
                  Decisions = decisions
                  Questions = questions
                  WorkItems = workItems
                  TerminalHandoff = terminalHandoff
                  CompletionHistory = completionHistory }

            do! validateDecisionGraph task
            return task
        }

    let rec private workItemFromSpec (acceptanceIds: string list) (spec: WorkItemSpec) =
        result {
            let! validId = workItemId spec.Id
            let! validTitle = nonEmpty "work item title" spec.Title
            let! dependsOn = spec.DependsOn |> List.map workItemId |> collectResults
            let! children = spec.Children |> List.map (workItemFromSpec acceptanceIds) |> collectResults
            let workItem : WorkItem =
                { Id = validId
                  Title = validTitle
                  State = PendingWork
                  Result = None
                  AcceptanceRefs = acceptanceIds
                  DependsOn = dependsOn
                  EvidenceRefs = []
                  Owner = None
                  Children = children }
            return workItem
        }

    let create (profile: EffectiveProfile) request =
        result {
            let! id = taskId request.Id
            let! title = nonEmpty "task title" request.Title
            if request.AcceptanceCriteria.IsEmpty then
                return! Error (InvalidInput "create requires at least one Acceptance Criterion")
            if request.WorkItems.IsEmpty then
                return! Error (InvalidInput "create requires at least one WorkItem")
            let! acceptanceCriteria =
                request.AcceptanceCriteria
                |> List.map (fun (criterionId, text) ->
                    result {
                        let! validId = acceptanceId criterionId
                        let! validText = nonEmpty "acceptance text" text
                        return { Id = validId; Text = validText; State = Pending }
                    })
                |> List.fold
                    (fun state item ->
                        result {
                            let! values = state
                            let! value = item
                            return value :: values
                        })
                    (Ok [])
                |> Result.map List.rev
            let acceptanceIds = acceptanceCriteria |> List.map _.Id
            let! workItems = request.WorkItems |> List.map (workItemFromSpec acceptanceIds) |> collectResults
            let allItems = flattenItems workItems
            let workIds = allItems |> List.map _.Id
            if Set.ofList acceptanceIds |> Set.count <> acceptanceIds.Length then
                return! Error (InvalidInput "Acceptance Criterion IDs must be unique")
            if Set.ofList workIds |> Set.count <> workIds.Length then
                return! Error (InvalidInput "WorkItem IDs must be unique")
            do! validateWorkTreeDependencies workItems
            let materialized = syncProfileGuards [] Map.empty profile request.Kind false
            let task =
                { Id = id
                  Title = title
                  Created = DateTimeOffset.UtcNow
                  Kind = request.Kind
                  Profile = profile.Definition.Id
                  ProfileFingerprint = profile.Fingerprint
                  Objective = ""
                  Scope = ""
                  NonGoals = ""
                  ContractState = Draft
                  ContractFingerprint = ""
                  ContractRevision = 1
                  StateRevision = 0
                  Lifecycle = "open"
                  Evidence = []
                  AcceptanceCriteria = acceptanceCriteria
                  Guards = materialized |> List.map _.Guard
                  ProfileGuardKeys =
                    materialized
                    |> List.choose (fun item -> item.Key |> Option.map (fun key -> item.Guard.Id, key))
                    |> Map.ofList
                  Decisions = []
                  Questions = []
                  WorkItems = workItems
                  TerminalHandoff = None
                  CompletionHistory = [] }

            do! validateDecisionGraph task
            return task
        }

    let private allWorkTerminal (task: TaskModel) =
        flattenItems task.WorkItems |> List.forall (fun item -> isTerminalState item.State)

    // Section 8.1 Guard scope: a WorkItemTarget Guard sees only Valid Evidence the
    // WorkItem explicitly references; a TaskTarget Guard sees task-level Evidence.
    let private guardEvidence (task: TaskModel) target =
        match target with
        | TaskTarget -> task.Evidence
        | WorkItemTarget id ->
            match findWorkItem id task.WorkItems with
            | None -> []
            | Some item ->
                item.EvidenceRefs
                |> List.choose (fun reference ->
                    task.Evidence |> List.tryFind (fun record -> record.Evidence.Id = reference))

    // Section 15.4: independent production needs concrete, distinct producer
    // identities. Independence is proven against the guarded WorkItem's effective
    // owner; a TaskTarget guard has no owner identity and stays unsatisfied.
    let private guardSatisfied (task: TaskModel) (guard: Guard) =
        match guard.Disposition with
        | GuardDisposition.NotApplicable reference
        | GuardDisposition.Waived reference -> dispositionSatisfied task guard reference
        | GuardDisposition.Applicable ->
            match guard.Requirement with
            | EvidenceRequired requirement ->
                let matching =
                    guardEvidence task guard.Target
                    |> List.filter (fun record ->
                        record.Validity = Valid
                        && record.Evidence.Kind = requirement.Kind
                        && (requirement.ProducerRole
                            |> Option.forall (fun role -> record.Evidence.ProducerRole = Some role)))
                    // Section 15.4: a WorkItem may reference the same Evidence more
                    // than once; each distinct Evidence counts once toward
                    // MinimumCount and independent-producer satisfaction.
                    |> List.distinctBy (fun record -> record.Evidence.Id)

                if requirement.RequireIndependentProducer then
                    match guard.Target with
                    | TaskTarget -> false
                    | WorkItemTarget targetId ->
                        match effectiveOwner task targetId |> Option.bind (fun owner -> owner.AgentId) with
                        | None -> false
                        | Some ownerAgentId ->
                            matching
                            |> List.filter (fun record ->
                                match record.Evidence.ProducerId with
                                | Some producerId -> producerId <> ownerAgentId
                                | None -> false)
                            |> List.length
                            >= requirement.MinimumCount
                else
                    matching.Length >= requirement.MinimumCount

    let private beforeStartGuards (task: TaskModel) (workItem: WorkItem) =
        task.Guards
        |> List.filter (fun guard ->
            guard.Checkpoint = BeforeStart
            && (match guard.Target with
                | TaskTarget -> true
                | WorkItemTarget id -> id = workItem.Id))

    let private beforeCompleteGuardsForWorkItem (task: TaskModel) (workItem: WorkItem) =
        task.Guards
        |> List.filter (fun guard ->
            guard.Checkpoint = BeforeComplete
            && (match guard.Target with
                | TaskTarget -> false
                | WorkItemTarget id -> id = workItem.Id))

    let readiness (task: TaskModel) (workItem: WorkItem) =
        match workItem.State with
        | PendingWork ->
            let unmet =
                workItem.DependsOn
                |> List.filter (fun dependency ->
                    match findWorkItem dependency task.WorkItems with
                    | Some target -> not (isTerminalState target.State)
                    | None -> true)

            let unmetGuards =
                beforeStartGuards task workItem
                |> List.filter (fun guard -> not (guardSatisfied task guard))
                |> List.map (fun guard -> UnmetGuard guard.Id)

            // Section 10: an open TaskWide question blocks every pending WorkItem;
            // a WorkItems-scoped question blocks only its listed WorkItems.
            let openQuestions =
                task.Questions
                |> List.filter (fun question ->
                    match question.State, question.Impact with
                    | Open, TaskWide -> true
                    | Open, WorkItems ids -> List.contains workItem.Id ids
                    | _ -> false)
                |> List.map (fun question -> OpenQuestion question.Id)

            let reasons = (unmet |> List.map UnmetDependency) @ unmetGuards @ openQuestions

            if reasons.IsEmpty then Ready else NotReady reasons
        | _ -> NotReady [ WorkItemNotPending ]

    let private describeReadiness reasons =
        reasons
        |> List.map (fun reason ->
            match reason with
            | WorkItemNotPending -> "work item is not pending"
            | UnmetDependency id -> $"dependency '{id}' is not satisfied"
            | UnmetGuard id -> $"guard '{id}' is not satisfied"
            | OpenQuestion id -> $"question '{id}' is open")
        |> String.concat "; "

    let private activatePath id (items: WorkItem list) =
        let pathIds =
            itemPath id items |> Option.map (List.map _.Id) |> Option.defaultValue [] |> Set.ofList

        let rec apply (items: WorkItem list) =
            items
            |> List.map (fun item ->
                let state =
                    if pathIds.Contains item.Id && item.State = PendingWork then ActiveWork else item.State

                { item with State = state; Children = apply item.Children })

        apply items

    let private allAcceptanceVerified (task: TaskModel) =
        task.AcceptanceCriteria
        |> List.forall (fun criterion ->
            match criterion.State with
            | Verified refs -> not refs.IsEmpty
            | Pending -> false)

    let private validEvidenceIds (task: TaskModel) =
        task.Evidence
        |> List.filter (fun record -> record.Validity = Valid)
        |> List.map (fun record -> record.Evidence.Id)

    let private normalizeGuardSpec (task: TaskModel) (spec: GuardSpec) : Result<GuardSpec, RuntimeError> =
        result {
            let! id = guardId spec.Id
            let! target =
                match spec.Target with
                | TaskTarget -> Ok TaskTarget
                | WorkItemTarget targetId ->
                    result {
                        let! validId = workItemId targetId

                        if findWorkItem validId task.WorkItems |> Option.isNone then
                            return! Error (InvalidInput $"guard '{spec.Id}' targets unknown WorkItem '{validId}'")

                        return WorkItemTarget validId
                    }
            let! requirement =
                match spec.Requirement with
                | EvidenceRequired value ->
                    result {
                        let! kind =
                            match value.Kind with
                            | EvidenceKind.Other custom when String.IsNullOrWhiteSpace custom ->
                                Error (InvalidInput "evidence kind 'other' requires a non-empty value")
                            | kind -> Ok kind

                        if value.MinimumCount < 1 then
                            return! Error (InvalidInput "guard minimumCount must be at least 1")

                        let! producerRole = optionalNonEmpty "guard producerRole" value.ProducerRole
                        return EvidenceRequired { value with Kind = kind; ProducerRole = producerRole }
                    }
            return { spec with Id = id; Target = target; Requirement = requirement }
        }

    let private validateGuardSpec (task: TaskModel) (spec: GuardSpec) =
        normalizeGuardSpec task spec
        |> Result.map (fun normalized ->
            { Id = normalized.Id
              Target = normalized.Target
              Checkpoint = normalized.Checkpoint
              Origin = TaskDesign
              Requirement = normalized.Requirement
              Applicability = normalized.Applicability
              Waiver = normalized.Waiver
              Disposition = GuardDisposition.Applicable })

    // Completion may attach only known, still-Valid Evidence to the WorkItem.
    let private validatedCompletionEvidence (task: TaskModel) (refs: string list) =
        result {
            let! validRefs = refs |> List.map evidenceId |> collectResults
            let knownIds = task.Evidence |> List.map _.Evidence.Id
            let missing = validRefs |> List.filter (fun reference -> not (List.contains reference knownIds))

            if not missing.IsEmpty then
                return! Error (InvalidInput $"completion references unknown Evidence '{missing.Head}'")

            let validIds = validEvidenceIds task
            let superseded = validRefs |> List.filter (fun reference -> not (List.contains reference validIds))

            if not superseded.IsEmpty then
                return! Error (InvalidInput $"completion references superseded Evidence '{superseded.Head}'")

            return validRefs
        }

    // Section 27: purely mechanical completion invariant. CONTRACT_DRIFT,
    // TaskWide Questions, requirement waivers, and child-Task correlation are
    // deferred to later slices; the Guard/AC/terminal checks here are canonical.
    let canCompleteTask (task: TaskModel) =
        allAcceptanceVerified task
        && allWorkTerminal task
        && not (hasOpenTaskWideQuestion task)
        && (task.Guards
            |> List.filter (fun guard -> guard.Checkpoint = BeforeComplete)
            |> List.forall (guardSatisfied task))

    // Section 5.2: reopening invalidates exactly the named targets, clears the
    // current handoff into immutable history, and returns the task to Open. Prior
    // Evidence/Decisions remain untouched history.
    let private applyReopen (task: TaskModel) (request: ReopenRequest) =
        let targets = request.Targets

        let acceptanceCriteria =
            task.AcceptanceCriteria
            |> List.map (fun criterion ->
                if
                    targets |> List.contains (ReopenTarget.AcceptanceCriterionTarget criterion.Id)
                    && criterion.State <> Pending
                then
                    { criterion with State = Pending }
                else
                    criterion)

        let rec applyItems (items: WorkItem list) =
            items
            |> List.map (fun item ->
                let reopened =
                    targets |> List.contains (ReopenTarget.WorkItemTarget item.Id) && isTerminalState item.State

                { item with
                    State = (if reopened then PendingWork else item.State)
                    Result = (if reopened then None else item.Result)
                    Children = applyItems item.Children })

        let guards =
            task.Guards
            |> List.map (fun guard ->
                if
                    targets |> List.contains (ReopenTarget.GuardTarget guard.Id)
                    && guard.Disposition <> GuardDisposition.Applicable
                then
                    { guard with Disposition = GuardDisposition.Applicable }
                else
                    guard)

        let completionHistory =
            match task.TerminalHandoff with
            | Some handoff -> task.CompletionHistory @ [ handoff ]
            | None -> task.CompletionHistory

        { task with
            Lifecycle = "open"
            AcceptanceCriteria = acceptanceCriteria
            WorkItems = applyItems task.WorkItems
            Guards = guards
            TerminalHandoff = None
            CompletionHistory = completionHistory }

    let private reopenInvalidWorkItems (task: TaskModel) =
        let rec apply (items: WorkItem list) =
            items
            |> List.map (fun item ->
                let children = apply item.Children

                let reopened =
                    item.State = DoneWork
                    && (task.Guards
                        |> List.filter (fun guard ->
                            guard.Checkpoint = BeforeComplete
                            && (match guard.Target with
                                | WorkItemTarget targetId -> targetId = item.Id
                                | TaskTarget -> false))
                        |> List.exists (fun guard -> not (guardSatisfied task guard)))

                { item with
                    State = (if reopened then PendingWork else item.State)
                    Result = (if reopened then None else item.Result)
                    Children = children })

        apply task.WorkItems

    let private reopenNonTerminalParents (items: WorkItem list) =
        // Explicit return type disambiguates WorkItem from WorkItemDto, which
        // share the State/Children labels used by the copy-and-update below.
        let rec fix (item: WorkItem) : WorkItem =
            let children = item.Children |> List.map fix
            let hasNonTerminalChild = children |> List.exists (fun child -> not (isTerminalState child.State))
            let reopened = item.State = DoneWork && hasNonTerminalChild

            { item with
                State = (if reopened then PendingWork else item.State)
                Result = (if reopened then None else item.Result)
                Children = children }

        items |> List.map fix

    // Section 8.2 steps 3-6: deterministic Evidence invalidation cascade.
    let private invalidationCascade (task: TaskModel) =
        let workItems =
            reopenNonTerminalParents (reopenInvalidWorkItems task)

        let next = { task with WorkItems = workItems }

        if next.Lifecycle = "complete" && not (canCompleteTask next) then
            { next with Lifecycle = "open" }
        else
            next

    // Section 9.2: one canonical authorization resolver. Every semantic operation
    // declares its exact typed target and minimum authority. With the #13 ingress
    // removed the only invocation authority is the Coordinator, so User-required
    // operations fail closed; a referenced Decision must still authorize the
    // exact target at the required authority.
    type private AuthorizationOutcome =
        | ReuseDecision of DecisionRef
        | NewDecision of Decision

    let private authorize
        (now: DateTimeOffset)
        (task: TaskModel)
        (operation: string)
        (decisionKind: DecisionKind)
        (operationTarget: DecisionTarget)
        (minimumAuthority: MinimumAuthority)
        (decisionRef: DecisionRef option)
        : Result<AuthorizationOutcome, RuntimeError> =
        result {
            let! target = validateDecisionTarget task operationTarget

            match decisionRef with
            | Some (DecisionRef referenceId) ->
                match findDecision referenceId task.Decisions with
                | None when minimumAuthority <> MinimumAuthority.CoordinatorAuthority ->
                    // W4/AC19: a User-required operation whose supplied DecisionRef
                    // does not exist is an absent authorization, not a text-only
                    // NotFound; report the exact missing authority so the caller can
                    // distinguish a missing ref from a mismatched one.
                    return!
                        Error(
                            AuthorityDenied(
                                { RequiredAuthority = minimumAuthority
                                  Operation = operation
                                  DecisionKind = Some decisionKind
                                  Target = Some target
                                  DecisionRefStatus = DecisionRefStatus.Absent },
                                $"operation requires {minimumAuthorityToString minimumAuthority} authority and Decision '{referenceId}' was not found; ordinary Coordinator invocation cannot authorize it"
                            )
                        )
                | None -> return! Error(NotFound $"Decision '{referenceId}' was not found")
                | Some decision when
                    decision.Kind = decisionKind
                    && decisionAuthorizes decision minimumAuthority target
                    ->
                    return ReuseDecision(DecisionRef referenceId)
                | Some decision ->
                    // Section 9.2: reuse must match both the exact typed target and
                    // the operation's Decision kind; a Decision for another kind
                    // (for example a waiver) never authorizes a different operation.
                    return!
                        Error(
                            AuthorityDenied(
                                { RequiredAuthority = minimumAuthority
                                  Operation = operation
                                  DecisionKind = Some decisionKind
                                  Target = Some target
                                  DecisionRefStatus = DecisionRefStatus.Mismatched },
                                $"Decision '{referenceId}' has kind {decisionKindToString decision.Kind} and does not authorize the exact operation {decisionTargetToString target} as kind {decisionKindToString decisionKind} at {minimumAuthorityToString minimumAuthority} authority"
                            )
                        )
            | None ->
                if minimumAuthority <> MinimumAuthority.CoordinatorAuthority then
                    return!
                        Error(
                            AuthorityDenied(
                                { RequiredAuthority = minimumAuthority
                                  Operation = operation
                                  DecisionKind = Some decisionKind
                                  Target = Some target
                                  DecisionRefStatus = DecisionRefStatus.Absent },
                                $"operation requires {minimumAuthorityToString minimumAuthority} authority; ordinary Coordinator invocation cannot authorize it"
                            )
                        )
                else
                    // Section 9.2: runtime creates the durable target-bound Decision
                    // atomically instead of requiring the model to manufacture it.
                    let decision: Decision =
                        { Id = nextDecisionId task
                          Authority = Coordinator
                          Kind = decisionKind
                          Targets = [ target ]
                          Rationale = $"coordinator authorization for {decisionTargetToString target}"
                          CreatedAt = now
                          ConfirmationRef = None }

                    return NewDecision decision
        }

    // Section 22: one operation-based patch model. Validation normalizes every
    // payload so evolve persists exactly the values decide authorized.
    let private validateContractPatch (task: TaskModel) (patch: ContractPatch) : Result<ContractPatch, RuntimeError> =
        match patch with
        | ContractPatch.AddAcceptanceCriterion draft ->
            result {
                let! id = acceptanceId draft.Id
                let! text = nonEmpty "acceptance text" draft.Text

                if findAcceptance id task.AcceptanceCriteria |> Option.isSome then
                    return! Error (InvalidInput $"Acceptance Criterion '{id}' already exists")

                return ContractPatch.AddAcceptanceCriterion { Id = id; Text = text }
            }
        | ContractPatch.AddGuard spec ->
            result {
                let! normalized = normalizeGuardSpec task spec

                if task.Guards |> List.exists (fun guard -> guard.Id = normalized.Id) then
                    return! Error (InvalidInput $"guard '{normalized.Id}' already exists")

                return ContractPatch.AddGuard normalized
            }
        | ContractPatch.RemoveGuard id ->
            result {
                let! valid = guardId id

                if task.Guards |> List.exists (fun guard -> guard.Id = valid) |> not then
                    return! Error (InvalidInput $"guard '{valid}' was not found")

                return ContractPatch.RemoveGuard valid
            }
        | ContractPatch.UpdateAcceptanceCriterion (id, text) ->
            result {
                let! validId = acceptanceId id

                if findAcceptance validId task.AcceptanceCriteria |> Option.isNone then
                    return! Error (InvalidInput $"Acceptance Criterion '{validId}' was not found")

                let! validText = nonEmpty "acceptance text" text
                return ContractPatch.UpdateAcceptanceCriterion(validId, validText)
            }
        | ContractPatch.RemoveAcceptanceCriterion id ->
            result {
                let! validId = acceptanceId id

                if findAcceptance validId task.AcceptanceCriteria |> Option.isNone then
                    return! Error (InvalidInput $"Acceptance Criterion '{validId}' was not found")

                return ContractPatch.RemoveAcceptanceCriterion validId
            }
        | ContractPatch.SetObjective text ->
            nonEmpty "contract objective" text |> Result.map ContractPatch.SetObjective
        | ContractPatch.SetScope text ->
            nonEmpty "contract scope" text |> Result.map ContractPatch.SetScope
        | ContractPatch.SetNonGoals text ->
            nonEmpty "contract nonGoals" text |> Result.map ContractPatch.SetNonGoals

    // Section 22.1: a Draft guard removal is limited to task-design guards;
    // profile-materialized obligations are never manually deleted.
    let private validateDraftGuardRemoval (task: TaskModel) (patch: ContractPatch) =
        match patch with
        | ContractPatch.RemoveGuard id ->
            match task.Guards |> List.tryFind (fun guard -> guard.Id = id) with
            | Some guard when guard.Origin = ProfileMaterialized ->
                Error(InvalidInput $"guard '{id}' is profile-materialized and cannot be removed from a draft contract")
            | _ -> Ok()
        | _ -> Ok()

    // Section 22.2: post-baseline authority is fixed per operation. RemoveGuard is
    // forbidden after baseline; weakening prose/AC operations require User.
    let private baselinedPatchAuthority (patch: ContractPatch) : Result<MinimumAuthority, RuntimeError> =
        match patch with
        | ContractPatch.AddAcceptanceCriterion _
        | ContractPatch.AddGuard _ -> Ok MinimumAuthority.CoordinatorAuthority
        | ContractPatch.RemoveGuard _ ->
            Error(
                InvalidInput
                    "a baselined guard cannot be physically removed; use a disposition or a profile/contract revision"
            )
        | ContractPatch.UpdateAcceptanceCriterion _
        | ContractPatch.RemoveAcceptanceCriterion _
        | ContractPatch.SetObjective _
        | ContractPatch.SetScope _
        | ContractPatch.SetNonGoals _ -> Ok MinimumAuthority.UserAuthority

    let private applyContractPatch (task: TaskModel) (patch: ContractPatch) =
        match patch with
        | ContractPatch.AddAcceptanceCriterion draft ->
            { task with
                AcceptanceCriteria =
                    task.AcceptanceCriteria @ [ { Id = draft.Id; Text = draft.Text; State = Pending } ] }
        | ContractPatch.AddGuard spec ->
            let guard =
                { Id = spec.Id
                  Target = spec.Target
                  Checkpoint = spec.Checkpoint
                  Origin = TaskDesign
                  Requirement = spec.Requirement
                  Applicability = spec.Applicability
                  Waiver = spec.Waiver
                  Disposition = GuardDisposition.Applicable }

            { task with Guards = task.Guards @ [ guard ] }
        | ContractPatch.RemoveGuard id ->
            { task with Guards = task.Guards |> List.filter (fun guard -> guard.Id <> id) }
        | ContractPatch.UpdateAcceptanceCriterion (id, text) ->
            // Section 22.2/8.2: a Verified criterion is bound to its prior text, so
            // a text change invalidates that verification and returns the criterion
            // to Pending. Evidence history itself is retained, not superseded.
            { task with
                AcceptanceCriteria =
                    task.AcceptanceCriteria
                    |> List.map (fun criterion ->
                        if criterion.Id = id then
                            if criterion.Text = text then
                                criterion
                            else
                                { criterion with Text = text; State = Pending }
                        else
                            criterion) }
        | ContractPatch.RemoveAcceptanceCriterion id ->
            // Section 7: acceptanceRefs are traceability only. Removing the
            // criterion must prune the now-dangling references from every
            // WorkItem; otherwise the persisted sidecar fails to re-read.
            let rec pruneAcceptanceRefs (items: WorkItem list) =
                items
                |> List.map (fun item ->
                    { item with
                        AcceptanceRefs = item.AcceptanceRefs |> List.filter (fun reference -> reference <> id)
                        Children = pruneAcceptanceRefs item.Children })

            { task with
                AcceptanceCriteria = task.AcceptanceCriteria |> List.filter (fun criterion -> criterion.Id <> id)
                WorkItems = pruneAcceptanceRefs task.WorkItems }
        | ContractPatch.SetObjective text -> { task with Objective = text }
        | ContractPatch.SetScope text -> { task with Scope = text }
        | ContractPatch.SetNonGoals text -> { task with NonGoals = text }

    let private decideCommand (now: DateTimeOffset) (task: TaskModel) command =
        match command with
        | StartWorkItem id ->
            match findWorkItem id task.WorkItems with
            | None -> Error [ NotFound $"WorkItem '{id}' was not found" ]
            | Some _ when task.Lifecycle <> "open" -> Error [ InvalidTransition "a complete task cannot start work" ]
            | Some item when item.State <> PendingWork -> Error [ InvalidTransition $"WorkItem '{id}' must be pending before it starts" ]
            | Some item ->
                // Section 12.2: a child may start only under a Pending/Active ancestry;
                // activatePath then promotes the still-Pending ancestors atomically.
                match readiness task item with
                | NotReady reasons -> Error [ InvalidTransition $"WorkItem '{id}' is not ready: {describeReadiness reasons}" ]
                | Ready ->
                    match
                        ancestorItems id task.WorkItems
                        |> List.tryFind (fun ancestor -> ancestor.State <> PendingWork && ancestor.State <> ActiveWork)
                    with
                    | Some ancestor ->
                        Error
                            [ InvalidTransition
                                  $"WorkItem '{id}' cannot start while ancestor '{ancestor.Id}' is {stateName ancestor.State}" ]
                    | None ->
                        // Section 12.2: activation promotes still-Pending ancestors, so a
                        // question-blocked ancestor must also reject the descendant start.
                        match
                            ancestorItems id task.WorkItems
                            |> List.tryFind (fun ancestor -> questionBlocksWorkItem task ancestor.Id)
                        with
                        | Some ancestor ->
                            Error
                                [ InvalidTransition
                                      $"WorkItem '{id}' cannot start while ancestor '{ancestor.Id}' is blocked by an open question" ]
                        | None ->
                            // Section 21.2: the first material start establishes the
                            // baseline and records the contract fingerprint immediately
                            // before the WorkItem starts.
                            match task.ContractState with
                            | Draft ->
                                Ok
                                    [ ContractBaselined(contractFingerprintOf (contractContentOf task))
                                      StartWorkItem id ]
                            | Baselined -> Ok [ StartWorkItem id ]
        | CompleteWorkItem (id, completion) ->
            match findWorkItem id task.WorkItems with
            | None -> Error [ NotFound $"WorkItem '{id}' was not found" ]
            | Some _ when task.Lifecycle <> "open" -> Error [ InvalidTransition "a complete task cannot complete work" ]
            | Some item when item.State <> ActiveWork -> Error [ InvalidTransition $"WorkItem '{id}' must be active before completion" ]
            | Some item ->
                // Section 14: a parent is never implicitly Done; it may complete only
                // once every direct child is terminal, which recursively clears the subtree.
                match item.Children |> List.tryFind (fun child -> not (isTerminalState child.State)) with
                | Some child ->
                    Error
                        [ InvalidTransition
                              $"WorkItem '{id}' cannot complete while child '{child.Id}' is not terminal" ]
                | None ->
                    match nonEmpty "work item result" completion.Result with
                    | Error error -> Error [ error ]
                    | Ok result ->
                        match validatedCompletionEvidence task completion.EvidenceRefs with
                        | Error error -> Error [ error ]
                        | Ok evidenceRefs ->
                            // Section 14: BeforeComplete Guards are evaluated against the
                            // completion payload, so the newly attached refs are visible.
                            let prospectiveItem = { item with EvidenceRefs = evidenceRefs }

                            let prospective =
                                { task with
                                    WorkItems = mapWorkItem id (fun _ -> prospectiveItem) task.WorkItems }

                            let unsatisfied =
                                beforeCompleteGuardsForWorkItem prospective prospectiveItem
                                |> List.filter (fun guard -> not (guardSatisfied prospective guard))

                            match unsatisfied with
                            | guard :: _ ->
                                Error
                                    [ InvalidTransition
                                          $"WorkItem '{id}' cannot complete while guard '{guard.Id}' is not satisfied" ]
                            | [] -> Ok [ CompleteWorkItem(id, { Result = result; EvidenceRefs = evidenceRefs }) ]
        | WaitWorkItem (id, condition) ->
            match findWorkItem id task.WorkItems with
            | None -> Error [ NotFound $"WorkItem '{id}' was not found" ]
            | Some _ when task.Lifecycle <> "open" -> Error [ InvalidTransition "a complete task cannot wait work" ]
            | Some item when item.State <> ActiveWork ->
                Error [ InvalidTransition $"WorkItem '{id}' must be active before it waits" ]
            | Some _ ->
                match validatedResumeCondition condition with
                | Ok valid -> Ok [ WaitWorkItem(id, valid) ]
                | Error error -> Error [ error ]
        | BlockWorkItem (id, blocker) ->
            match findWorkItem id task.WorkItems with
            | None -> Error [ NotFound $"WorkItem '{id}' was not found" ]
            | Some _ when task.Lifecycle <> "open" -> Error [ InvalidTransition "a complete task cannot block work" ]
            | Some item ->
                match item.State with
                | PendingWork
                | ActiveWork
                | WaitingWork _ ->
                    match validatedBlocker blocker with
                    | Ok valid -> Ok [ BlockWorkItem(id, valid) ]
                    | Error error -> Error [ error ]
                | _ -> Error [ InvalidTransition $"WorkItem '{id}' cannot block from state {stateName item.State}" ]
        | ResumeWorkItem (id, observation) ->
            match findWorkItem id task.WorkItems with
            | None -> Error [ NotFound $"WorkItem '{id}' was not found" ]
            | Some _ when task.Lifecycle <> "open" -> Error [ InvalidTransition "a complete task cannot resume work" ]
            | Some item ->
                match item.State with
                | WaitingWork _
                | BlockedWork _ ->
                    match observation with
                    | None -> Ok [ ResumeWorkItem(id, None) ]
                    | Some value ->
                        match validatedObservation value with
                        | Ok valid -> Ok [ ResumeWorkItem(id, Some valid) ]
                        | Error error -> Error [ error ]
                | _ ->
                    Error
                        [ InvalidTransition
                              $"WorkItem '{id}' must be waiting or blocked before it resumes" ]
        | RebindOwner (id, owner, reason) ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a terminal task cannot rebind a WorkItem owner" ]
            else
                match findWorkItem id task.WorkItems with
                | None -> Error [ NotFound $"WorkItem '{id}' was not found" ]
                | Some _ ->
                    match validateOwner owner, nonEmpty "owner rebind reason" reason with
                    | Error error, _
                    | _, Error error -> Error [ error ]
                    | Ok validOwner, Ok _ -> Ok [ OwnerRebound(id, validOwner) ]
        | OwnerRebound _ ->
            Error [ InvalidInput "owner rebind events are produced by decide and cannot be applied as commands" ]
        | ReopenTask request ->
            match task.Lifecycle with
            | ("complete" | "aborted") as lifecycle ->
                match validateReopenRequest task request with
                | Error error -> Error [ error ]
                | Ok validRequest ->
                    let target = ReopenTaskTarget(task.Id, validRequest.Targets)

                    let minimumAuthority =
                        if lifecycle = "aborted" then
                            MinimumAuthority.UserAuthority
                        else
                            MinimumAuthority.CoordinatorAuthority

                    // Section 5.2: reopening must leave the completion predicate
                    // false; a no-op reopen would silently rewrite terminal state.
                    if not (canCompleteTask (applyReopen task validRequest)) then
                        match
                            authorize
                                now
                                task
                                "task_apply.reopen"
                                DesignDecision
                                target
                                minimumAuthority
                                request.DecisionRef
                        with
                        | Error error -> Error [ error ]
                        | Ok (ReuseDecision _) -> Ok [ TaskReopened validRequest ]
                        | Ok (NewDecision decision) ->
                            Ok [ DecisionAdded decision; TaskReopened validRequest ]
                    else
                        Error [ InvalidTransition "reopening must invalidate at least one completion requirement" ]
            | _ -> Error [ InvalidTransition "only a terminal task can be reopened" ]
        | TaskReopened _ ->
            Error [ InvalidInput "task reopen events are produced by decide and cannot be applied as commands" ]
        | ContractBaselined _ ->
            Error [ InvalidInput "contract baseline events are produced by decide and cannot be applied as commands" ]
        | ApplyContractPatch (patch, decisionRef) ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a terminal task cannot apply a contract patch" ]
            else
                match validateContractPatch task patch with
                | Error error -> Error [ error ]
                | Ok validPatch ->
                    match task.ContractState with
                    | Draft ->
                        // Section 21.1: ordinary Coordinator invocation is sufficient
                        // before baseline; draft edits create no contractRevision history.
                        match validateDraftGuardRemoval task validPatch with
                        | Error error -> Error [ error ]
                        | Ok() -> Ok [ ContractPatchApplied(validPatch, false) ]
                    | Baselined ->
                        match baselinedPatchAuthority validPatch with
                        | Error error -> Error [ error ]
                        | Ok minimumAuthority ->
                            // Section 22.2: the exact patch payload is the authorization
                            // target, not the revision the patch will create.
                            let target = ContractPatchTarget(contractPatchIdOf validPatch)

                            match
                                authorize
                                    now
                                    task
                                    "task_apply.apply-contract-patch"
                                    ContractRevision
                                    target
                                    minimumAuthority
                                    decisionRef
                            with
                            | Error error -> Error [ error ]
                            | Ok (ReuseDecision _) -> Ok [ ContractPatchApplied(validPatch, true) ]
                            | Ok (NewDecision decision) ->
                                Ok [ DecisionAdded decision; ContractPatchApplied(validPatch, true) ]
        | ContractPatchApplied _ ->
            Error
                [ InvalidInput "contract patch events are produced by decide and cannot be applied as commands" ]
        | ReconcileContractDrift plan ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a terminal task cannot reconcile contract drift" ]
            elif not (contractDrift task) then
                Error [ InvalidTransition "contract reconciliation requires unresolved contract drift" ]
            else
                match plan with
                | ReconciliationPlan.RestoreCanonicalContract content ->
                    // Section 23: restoring the recorded canonical contract is
                    // Coordinator-authorized and fingerprint-verified.
                    if contractFingerprintOf content <> task.ContractFingerprint then
                        Error [ InvalidInput "restore payload does not match the recorded contract fingerprint" ]
                    else
                        let target = ContractPatchTarget(contractReconciliationId plan)

                        match
                            authorize
                                now
                                task
                                "task_apply.reconcile-contract-drift"
                                ContractRevision
                                target
                                MinimumAuthority.CoordinatorAuthority
                                None
                        with
                        | Error error -> Error [ error ]
                        | Ok (ReuseDecision _) -> Ok [ ContractDriftReconciled plan ]
                        | Ok (NewDecision decision) ->
                            Ok [ DecisionAdded decision; ContractDriftReconciled plan ]
                | ReconciliationPlan.AcceptExternalContract ->
                    // Section 23: accepting an out-of-band rewrite is a material
                    // User-authorized change; it fails closed until #13.
                    let target = ContractPatchTarget(contractReconciliationId plan)

                    match
                        authorize
                            now
                            task
                            "task_apply.reconcile-contract-drift"
                            ContractRevision
                            target
                            MinimumAuthority.UserAuthority
                            None
                    with
                    | Error error -> Error [ error ]
                    | Ok (ReuseDecision _) -> Ok [ ContractDriftReconciled plan ]
                    | Ok (NewDecision decision) -> Ok [ DecisionAdded decision; ContractDriftReconciled plan ]
        | ContractDriftReconciled _ ->
            Error
                [ InvalidInput "contract reconciliation events are produced by decide and cannot be applied as commands" ]
        | AddEvidence evidence ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot add evidence" ]
            else
                match validateEvidence evidence with
                | Error error -> Error [ error ]
                | Ok validEvidence ->
                    if task.Evidence |> List.exists (fun record -> record.Evidence.Id = validEvidence.Id) then
                        Error [ InvalidInput $"evidence '{validEvidence.Id}' already exists" ]
                    else
                        Ok [ AddEvidence validEvidence ]
        | SupersedeEvidence (id, reason) ->
            match task.Evidence |> List.tryFind (fun record -> record.Evidence.Id = id) with
            | None -> Error [ NotFound $"Evidence '{id}' was not found" ]
            | Some { Validity = Superseded _ } -> Error [ InvalidTransition $"Evidence '{id}' is already superseded" ]
            | Some _ ->
                match nonEmpty "supersede reason" reason with
                | Ok validReason -> Ok [ SupersedeEvidence(id, validReason) ]
                | Error error -> Error [ error ]
        | VerifyAcceptanceCriterion (id, evidenceRefs) ->
            match findAcceptance id task.AcceptanceCriteria with
            | None -> Error [ NotFound $"Acceptance Criterion '{id}' was not found" ]
            | Some _ when task.Lifecycle <> "open" -> Error [ InvalidTransition "a complete task cannot verify acceptance" ]
            | Some { State = Verified _ } -> Error [ InvalidTransition $"Acceptance Criterion '{id}' is already verified" ]
            | Some _ ->
                match validateEvidenceRefs evidenceRefs with
                | Error error -> Error [ error ]
                | Ok refs ->
                    let validIds = validEvidenceIds task
                    let invalidRefs = refs |> List.filter (fun reference -> not (List.contains reference validIds))

                    if not invalidRefs.IsEmpty then
                        Error
                            [ InvalidInput
                                  $"Acceptance Criterion '{id}' references unknown or superseded evidence '{invalidRefs.Head}'" ]
                    else
                        Ok [ VerifyAcceptanceCriterion(id, refs) ]
        | AddGuard spec ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot add a guard" ]
            else
                match validateGuardSpec task spec with
                | Error error -> Error [ error ]
                | Ok guard when task.Guards |> List.exists (fun existing -> existing.Id = guard.Id) ->
                    Error [ InvalidInput $"guard '{guard.Id}' already exists" ]
                | Ok guard -> Ok [ GuardAdded guard ]
        | GuardAdded _ ->
            Error [ InvalidInput "guard addition events are produced by decide and cannot be applied as commands" ]
        | GuardDispositionSet _ ->
            Error
                [ InvalidInput "guard disposition events are produced by decide and cannot be applied as commands" ]
        | MarkGuardNotApplicable (id, decisionRef) ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot dispose a guard" ]
            else
                match task.Guards |> List.tryFind (fun guard -> guard.Id = id) with
                | None -> Error [ NotFound $"Guard '{id}' was not found" ]
                | Some guard when guard.Disposition <> GuardDisposition.Applicable ->
                    Error [ InvalidTransition $"Guard '{id}' is already disposed" ]
                | Some guard ->
                    match guard.Applicability with
                    | Always ->
                        Error
                            [ InvalidTransition
                                  $"Guard '{id}' is always applicable and cannot be marked NotApplicable" ]
                    | ExplicitDecision minimumAuthority ->
                        match
                            authorize
                                now
                                task
                                "task_apply.mark-not-applicable"
                                ApplicabilityDecision
                                (GuardDispositionTarget id)
                                minimumAuthority
                                decisionRef
                        with
                        | Error error -> Error [ error ]
                        | Ok (ReuseDecision (DecisionRef reference)) ->
                            Ok [ GuardDispositionSet(id, GuardDisposition.NotApplicable reference) ]
                        | Ok (NewDecision decision) ->
                            Ok
                                [ DecisionAdded decision
                                  GuardDispositionSet(id, GuardDisposition.NotApplicable decision.Id) ]
        | WaiveGuard (id, decisionRef) ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot dispose a guard" ]
            else
                match task.Guards |> List.tryFind (fun guard -> guard.Id = id) with
                | None -> Error [ NotFound $"Guard '{id}' was not found" ]
                | Some guard when guard.Disposition <> GuardDisposition.Applicable ->
                    Error [ InvalidTransition $"Guard '{id}' is already disposed" ]
                | Some guard ->
                    match guard.Waiver with
                    | NotWaivable -> Error [ InvalidTransition $"Guard '{id}' is not waivable" ]
                    | WaivableBy minimumAuthority ->
                        match
                            authorize
                                now
                                task
                                "task_apply.waive-guard"
                                WaiverDecision
                                (GuardDispositionTarget id)
                                minimumAuthority
                                decisionRef
                        with
                        | Error error -> Error [ error ]
                        | Ok (ReuseDecision (DecisionRef reference)) ->
                            Ok [ GuardDispositionSet(id, GuardDisposition.Waived reference) ]
                        | Ok (NewDecision decision) ->
                            Ok
                                [ DecisionAdded decision
                                  GuardDispositionSet(id, GuardDisposition.Waived decision.Id) ]
        | AddDecision draft ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot add a decision" ]
            else
                // Section 9: ordinary input is Coordinator-only; the runtime assigns
                // Id/Authority/CreatedAt so authority is never self-declared.
                match validateDecisionDraft now task draft with
                | Error error -> Error [ error ]
                | Ok decision -> Ok [ DecisionAdded decision ]
        | DecisionAdded _ ->
            Error [ InvalidInput "decision addition events are produced by decide and cannot be applied as commands" ]
        | AddQuestion draft ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot add a question" ]
            else
                match validateQuestionDraft task draft with
                | Error error -> Error [ error ]
                | Ok question -> Ok [ QuestionOpened question ]
        | QuestionOpened _ ->
            Error [ InvalidInput "question opening events are produced by decide and cannot be applied as commands" ]
        | ResolveQuestion (id, reference) ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "a complete task cannot resolve a question" ]
            else
                match task.Questions |> List.tryFind (fun question -> question.Id = id) with
                | None -> Error [ NotFound $"Question '{id}' was not found" ]
                | Some { State = Resolved _ } -> Error [ InvalidTransition $"Question '{id}' is already resolved" ]
                | Some question ->
                    let (DecisionRef referenceId) = reference

                    // Section 10: resolution requires an existing Decision whose typed
                    // target exactly matches this question.
                    match findDecision referenceId task.Decisions with
                    | None -> Error [ NotFound $"Decision '{referenceId}' was not found" ]
                    | Some decision when decision.Targets |> List.contains (QuestionResolutionTarget question.Id) ->
                        Ok [ QuestionResolved(id, reference) ]
                    | Some _ ->
                        // W4/AC19: a present DecisionRef that does not target this
                        // question is a mismatch, not a syntax error; report the
                        // canonical User/UserDecision remediation (the disposition is
                        // coordinator-created today, but the signed User path is the
                        // durable authorization once #18 lands).
                        Error
                            [ AuthorityDenied(
                                  { RequiredAuthority = MinimumAuthority.UserAuthority
                                    Operation = "task_apply.resolve-question"
                                    DecisionKind = Some UserDecision
                                    Target = Some(QuestionResolutionTarget question.Id)
                                    DecisionRefStatus = DecisionRefStatus.Mismatched },
                                  $"Decision '{referenceId}' does not target Question '{id}'"
                              ) ]
        | QuestionResolved _ ->
            Error [ InvalidInput "question resolution events are produced by decide and cannot be applied as commands" ]
        | ReclassifyTask _ ->
            Error [ InvalidInput "reclassification commands are handled by the profile resolver" ]
        | TaskReclassified _ ->
            Error [ InvalidInput "task reclassification events are produced by decide and cannot be applied as commands" ]
        | ReconcileProfileDrift ->
            Error [ InvalidInput "profile reconciliation commands are handled by the profile resolver" ]
        | ProfileDriftReconciled _ ->
            Error [ InvalidInput "profile reconciliation events are produced by decide and cannot be applied as commands" ]
        | ProfileGuardsSynced _ ->
            Error [ InvalidInput "profile guard sync events are produced by decide and cannot be applied as commands" ]
        | CompleteTask handoff ->
            if task.Lifecycle <> "open" then
                Error [ InvalidTransition "only an open task can complete" ]
            else
                match validateTerminalHandoff handoff with
                | Error error -> Error [ error ]
                | Ok validHandoff ->
                    if not (allWorkTerminal task) then
                        Error [ InvalidTransition "all WorkItems must be done before task completion" ]
                    elif not (allAcceptanceVerified task) then
                        Error [ InvalidTransition "all Acceptance Criteria must be verified before task completion" ]
                    elif hasOpenTaskWideQuestion task then
                        Error [ InvalidTransition "task cannot complete while a TaskWide question is open" ]
                    elif not (canCompleteTask task) then
                        let unsatisfied =
                            task.Guards
                            |> List.filter (fun guard -> guard.Checkpoint = BeforeComplete)
                            |> List.tryFind (fun guard -> not (guardSatisfied task guard))

                        match unsatisfied with
                        | Some guard -> Error [ InvalidTransition $"guard '{guard.Id}' is not satisfied" ]
                        | None -> Error [ InvalidTransition "task completion requirements are not satisfied" ]
                    else
                        Ok [ CompleteTask validHandoff ]

    // Section 17.5/20: the registry is required for reclassification and profile
    // reconciliation; a task whose profile is no longer registered cannot mutate.
    // The bare messages are shared with read-only validation so the drift markers
    // stay a single reporting protocol; mutation errors append the remedy.
    let private profileUnavailableMessage (task: TaskModel) =
        $"PROFILE_DRIFT: profile '{task.Profile}' is not available in the current registry"

    let private profileDriftMessage (task: TaskModel) =
        $"PROFILE_DRIFT: profile '{task.Profile}' no longer matches the recorded fingerprint"

    let private contractDriftMessage =
        "CONTRACT_DRIFT: the persisted contract no longer matches its recorded fingerprint"

    let private profileUnavailableError (task: TaskModel) =
        InvalidTransition (profileUnavailableMessage task + "; restore the profile before mutating state")

    let private profileDriftError (task: TaskModel) =
        InvalidTransition (profileDriftMessage task + "; reconcile before mutating state")

    // Section 20/23: read-only validation findings. Structural parsing stays
    // lenient so getTask can always surface state; validateTask must still fail
    // explicitly on a missing/drifted profile and on contract drift. Empty means
    // the persisted state is consistent with the current registry and its own
    // recorded fingerprints.
    let validationFindings (profiles: Map<string, EffectiveProfile>) (task: TaskModel) =
        [ match Map.tryFind task.Profile profiles with
          | None -> yield profileUnavailableMessage task
          | Some profile when profile.Fingerprint <> task.ProfileFingerprint -> yield profileDriftMessage task
          | Some _ -> ()
          if contractDrift task then yield contractDriftMessage ]

    let private reclassificationTarget (kind: Kind) (profileIdText: string) =
        ReclassificationTarget($"{kindToString kind}|{profileIdText}")

    // Section 25.1: Draft reclassification is ordinary design; a baselined change
    // is Coordinator-only while it stays non-weakening, otherwise it needs User
    // authority (which has no trusted ingress yet and therefore fails closed).
    let private decideReclassification
        (profiles: Map<string, EffectiveProfile>)
        (now: DateTimeOffset)
        (task: TaskModel)
        (request: ReclassificationRequest)
        =
        result {
            let! _ = nonEmpty "reclassification reason" request.Reason
            let kind = request.Kind |> Option.defaultValue task.Kind
            let targetProfileId = request.Profile |> Option.defaultValue task.Profile

            if kind = task.Kind && targetProfileId = task.Profile then
                return! Error(InvalidInput "reclassification must change the task kind or profile")

            let! targetProfile =
                match Map.tryFind targetProfileId profiles with
                | Some profile -> Ok profile
                | None -> Error(InvalidInput $"unknown profile '{targetProfileId}'")

            let syncEvents =
                [ TaskReclassified(kind, targetProfileId, targetProfile.Fingerprint)
                  ProfileGuardsSynced(
                      syncProfileGuards
                          task.Guards
                          task.ProfileGuardKeys
                          targetProfile
                          kind
                          (task.ContractState = Baselined)
                  ) ]

            match task.ContractState with
            | Draft -> return syncEvents
            | Baselined ->
                let weakening =
                    kind <> task.Kind
                    || (match Map.tryFind task.Profile profiles with
                        | Some current -> profileWeakening current targetProfile
                        | None -> true)

                let minimumAuthority =
                    if weakening then
                        MinimumAuthority.UserAuthority
                    else
                        MinimumAuthority.CoordinatorAuthority

                match
                    authorize
                        now
                        task
                        "task_apply.reclassify-task"
                        ContractRevision
                        (reclassificationTarget kind targetProfileId)
                        minimumAuthority
                        None
                with
                | Error error -> return! Error error
                | Ok (ReuseDecision _) -> return syncEvents
                | Ok (NewDecision decision) -> return DecisionAdded decision :: syncEvents
        }

    // Section 20.2: baselined profile drift reconciles monotonically toward the
    // current registry profile; removed requirements are never deleted.
    let private decideProfileReconciliation
        (profiles: Map<string, EffectiveProfile>)
        (now: DateTimeOffset)
        (task: TaskModel)
        =
        if task.Lifecycle <> "open" then
            Error [ InvalidTransition "a terminal task cannot reconcile profile drift; reopen it instead" ]
        else
            match Map.tryFind task.Profile profiles with
            | None -> Error [ profileUnavailableError task ]
            | Some profile when profile.Fingerprint = task.ProfileFingerprint ->
                Error [ InvalidTransition "profile reconciliation requires unresolved profile drift" ]
            | Some profile ->
                match
                    authorize
                        now
                        task
                        "task_apply.reconcile-profile-drift"
                        ContractRevision
                        (ReclassificationTarget($"profileDrift:{task.Profile}"))
                        MinimumAuthority.CoordinatorAuthority
                        None
                with
                | Error error -> Error [ error ]
                | Ok (ReuseDecision _) ->
                    Ok
                        [ ProfileDriftReconciled profile.Fingerprint
                          ProfileGuardsSynced(
                              syncProfileGuards task.Guards task.ProfileGuardKeys profile task.Kind true
                          ) ]
                | Ok (NewDecision decision) ->
                    Ok
                        [ DecisionAdded decision
                          ProfileDriftReconciled profile.Fingerprint
                          ProfileGuardsSynced(
                              syncProfileGuards task.Guards task.ProfileGuardKeys profile task.Kind true
                          ) ]

    // Section 20: profile availability/drift gates every mutation. Draft absorbs
    // the current profile by replacement, baselined drift blocks until explicit
    // reconciliation, and reopening syncs atomically with the current profile.
    let private decideCore (profiles: Map<string, EffectiveProfile>) (now: DateTimeOffset) (task: TaskModel) command =
        match command with
        | ReconcileProfileDrift -> decideProfileReconciliation profiles now task
        | ReclassifyTask request ->
            decideReclassification profiles now task request |> Result.mapError List.singleton
        | _ ->
            match Map.tryFind task.Profile profiles with
            | None -> Error [ profileUnavailableError task ]
            | Some profile ->
                let drift = profile.Fingerprint <> task.ProfileFingerprint

                match command with
                | ReopenTask request when drift ->
                    // Section 20.3: reopen re-resolves the profile and materializes
                    // new/stronger requirements in the same apply as the reopen.
                    match decideCommand now task command with
                    | Error errors -> Error errors
                    | Ok events ->
                        let reopened = applyReopen task request

                        Ok(
                            events
                            @ [ ProfileGuardsSynced(
                                    syncProfileGuards
                                        reopened.Guards
                                        task.ProfileGuardKeys
                                        profile
                                        task.Kind
                                        true
                                )
                                ProfileDriftReconciled profile.Fingerprint ]
                        )
                | _ when drift && task.ContractState = Baselined ->
                    Error [ profileDriftError task ]
                | _ when drift ->
                    // Section 20.1/21.2: a Draft contract absorbs the current
                    // Profile, and its mandatory guards must be materialized before
                    // the command is validated so the command cannot bypass the
                    // newly materialized obligations.
                    let syncedGuards =
                        syncProfileGuards task.Guards task.ProfileGuardKeys profile task.Kind false

                    let prospective =
                        { task with
                            Guards =
                                (task.Guards |> List.filter (fun guard -> guard.Origin <> ProfileMaterialized))
                                @ (syncedGuards |> List.map _.Guard) }

                    match decideCommand now prospective command with
                    | Error errors -> Error errors
                    | Ok events ->
                        Ok(
                            ProfileGuardsSynced syncedGuards
                            :: ProfileDriftReconciled profile.Fingerprint
                            :: events
                        )
                | _ ->
                    match decideCommand now task command with
                    | Error errors -> Error errors
                    | Ok events -> Ok events

    // Section 23: while the persisted contract no longer matches its recorded
    // fingerprint, material mutations are rejected; only reconciliation may
    // proceed. Read-only analysis uses getTask/validateTask, not decide. The
    // decision instant is an explicit input so decide stays pure and
    // deterministic; the imperative apply boundary supplies it.
    let decide (profiles: Map<string, EffectiveProfile>) (now: DateTimeOffset) (task: TaskModel) command =
        match command with
        | ReconcileContractDrift _ -> decideCore profiles now task command
        | _ when contractDrift task ->
            Error [ InvalidTransition (contractDriftMessage + "; reconcile before mutating state") ]
        | _ -> decideCore profiles now task command

    let evolve (task: TaskModel) events =
        events
        |> List.fold
            (fun (current: TaskModel) event ->
                match event with
                | StartWorkItem id -> { current with WorkItems = activatePath id current.WorkItems }
                | ContractBaselined fingerprint ->
                    // Section 21.2: the fingerprint recorded at baseline anchors the
                    // canonical contract; the initial revision established at create
                    // is retained, so no revision bump occurs here.
                    { current with
                        ContractState = Baselined
                        ContractFingerprint = fingerprint }
                | CompleteWorkItem (id, completion) ->
                    { current with
                        WorkItems =
                            current.WorkItems
                            |> mapWorkItem id (fun item ->
                                { item with
                                    State = DoneWork
                                    Result = Some completion.Result
                                    // Persist the exact refs decide evaluated so target-scoped
                                    // Guards still see them after a reload or supersession cascade.
                                    EvidenceRefs = completion.EvidenceRefs }) }
                | WaitWorkItem (id, condition) ->
                    { current with
                        WorkItems =
                            current.WorkItems
                            |> mapWorkItem id (fun item -> { item with State = WaitingWork condition; Result = None }) }
                | BlockWorkItem (id, blocker) ->
                    { current with
                        WorkItems =
                            current.WorkItems
                            |> mapWorkItem id (fun item -> { item with State = BlockedWork blocker; Result = None }) }
                | ResumeWorkItem (id, _) ->
                    { current with
                        WorkItems =
                            current.WorkItems
                            |> mapWorkItem id (fun item -> { item with State = PendingWork; Result = None }) }
                | RebindOwner _ ->
                    // Command-only form; decide always normalizes it into OwnerRebound.
                    current
                | OwnerRebound (id, owner) ->
                    { current with
                        WorkItems =
                            current.WorkItems
                            |> mapWorkItem id (fun item -> { item with Owner = Some owner }) }
                | AddEvidence evidence ->
                    { current with Evidence = current.Evidence @ [ { Evidence = evidence; Validity = Valid } ] }
                | SupersedeEvidence (id, reason) ->
                    let evidence =
                        current.Evidence
                        |> List.map (fun record ->
                            if record.Evidence.Id = id then
                                { record with Validity = Superseded reason }
                            else
                                record)

                    let validIds =
                        evidence
                        |> List.filter (fun record -> record.Validity = Valid)
                        |> List.map (fun record -> record.Evidence.Id)

                    // Section 8.2 cascade step 2: a Verified AC keeps only still-Valid
                    // references and returns to Pending when none remain.
                    let acceptanceCriteria =
                        current.AcceptanceCriteria
                        |> List.map (fun criterion ->
                            match criterion.State with
                            | Verified refs ->
                                let remaining = refs |> List.filter (fun reference -> List.contains reference validIds)

                                if remaining.IsEmpty then
                                    { criterion with State = Pending }
                                else
                                    { criterion with State = Verified remaining }
                            | Pending -> criterion)

                    // Section 8.2 steps 3-6: the mechanical cascade reopens affected
                    // WorkItems/parents and the Task lifecycle after the Evidence flips.
                    // Superseded Evidence no longer counts as attached to a WorkItem,
                    // so stale refs are pruned before the cascade evaluates Guards.
                    let rec pruneEvidenceRefs (items: WorkItem list) =
                        items
                        |> List.map (fun item ->
                            { item with
                                EvidenceRefs =
                                    item.EvidenceRefs
                                    |> List.filter (fun reference -> List.contains reference validIds)
                                Children = pruneEvidenceRefs item.Children })

                    invalidationCascade
                        { current with
                            Evidence = evidence
                            AcceptanceCriteria = acceptanceCriteria
                            WorkItems = pruneEvidenceRefs current.WorkItems }
                | VerifyAcceptanceCriterion (id, refs) ->
                    { current with
                        AcceptanceCriteria =
                            current.AcceptanceCriteria
                            |> List.map (fun item ->
                                if item.Id = id then { item with State = Verified refs } else item) }
                | AddGuard _ ->
                    // Command-only form; decide always normalizes it into GuardAdded.
                    current
                | GuardAdded guard -> { current with Guards = current.Guards @ [ guard ] }
                | MarkGuardNotApplicable _
                | WaiveGuard _ ->
                    // Command-only forms; decide always normalizes them into
                    // GuardDispositionSet (plus any DecisionAdded).
                    current
                | GuardDispositionSet (id, disposition) ->
                    { current with
                        Guards =
                            current.Guards
                            |> List.map (fun guard ->
                                if guard.Id = id then
                                    { guard with Disposition = disposition }
                                else
                                    guard) }
                | AddDecision _ ->
                    // Command-only form; decide always normalizes it into DecisionAdded.
                    current
                | DecisionAdded decision -> { current with Decisions = current.Decisions @ [ decision ] }
                | AddQuestion _ ->
                    // Command-only form; decide always normalizes it into QuestionOpened.
                    current
                | QuestionOpened question -> { current with Questions = current.Questions @ [ question ] }
                | ResolveQuestion _ ->
                    // Command-only form; decide always normalizes it into QuestionResolved.
                    current
                | QuestionResolved (id, reference) ->
                    { current with
                        Questions =
                            current.Questions
                            |> List.map (fun question ->
                                if question.Id = id then
                                    { question with State = Resolved reference }
                                else
                                    question) }
                | ReopenTask _ ->
                    // Command-only form; decide always normalizes it into TaskReopened.
                    current
                | TaskReopened request -> applyReopen current request
                | ApplyContractPatch _ ->
                    // Command-only form; decide always normalizes it into ContractPatchApplied.
                    current
                | ContractPatchApplied (patch, baselined) ->
                    let next = applyContractPatch current patch

                    if baselined then
                        // Section 22.2: an accepted post-baseline patch creates a new
                        // contractRevision and refreshes the fingerprint.
                        { next with
                            ContractRevision = current.ContractRevision + 1
                            ContractFingerprint = contractFingerprintOf (contractContentOf next) }
                    else
                        next
                | ReconcileContractDrift _ ->
                    // Command-only form; decide always normalizes it into ContractDriftReconciled.
                    current
                | ContractDriftReconciled plan ->
                    match plan with
                    | ReconciliationPlan.RestoreCanonicalContract content ->
                        // Section 23: restore prose and AC ids/text, preserving the
                        // verification state of criteria that survive by id.
                        let acceptanceCriteria =
                            content.AcceptanceCriteria
                            |> List.map (fun (id, text) ->
                                match current.AcceptanceCriteria |> List.tryFind (fun criterion -> criterion.Id = id) with
                                | Some existing -> { existing with Text = text }
                                | None -> { Id = id; Text = text; State = Pending })

                        { current with
                            Objective = content.Objective
                            Scope = content.Scope
                            NonGoals = content.NonGoals
                            AcceptanceCriteria = acceptanceCriteria
                            ContractRevision = current.ContractRevision + 1 }
                    | ReconciliationPlan.AcceptExternalContract ->
                        // Section 23: adopt the current content as the new canonical
                        // contract, clearing drift without discarding history.
                        { current with
                            ContractFingerprint = contractFingerprintOf (contractContentOf current)
                            ContractRevision = current.ContractRevision + 1 }
                | ReclassifyTask _ ->
                    // Command-only form; decide always normalizes it into TaskReclassified.
                    current
                | TaskReclassified (kind, profile, fingerprint) ->
                    { current with
                        Kind = kind
                        Profile = profile
                        ProfileFingerprint = fingerprint }
                | ReconcileProfileDrift ->
                    // Command-only form; decide always normalizes it into ProfileDriftReconciled.
                    current
                | ProfileDriftReconciled fingerprint -> { current with ProfileFingerprint = fingerprint }
                | ProfileGuardsSynced synced ->
                    // Section 15.5: replace only profile-materialized guards so
                    // task-design guards and their dispositions survive, and
                    // persist the stable key provenance that sync correlated on.
                    let guards = synced |> List.map _.Guard

                    let keys =
                        synced
                        |> List.choose (fun item -> item.Key |> Option.map (fun key -> item.Guard.Id, key))
                        |> Map.ofList

                    { current with
                        Guards =
                            (current.Guards |> List.filter (fun guard -> guard.Origin <> ProfileMaterialized))
                            @ guards
                        ProfileGuardKeys = keys }
                | CompleteTask handoff ->
                    { current with
                        Lifecycle = "complete"
                        TerminalHandoff = Some handoff
                        CompletionHistory =
                            match current.TerminalHandoff with
                            | Some previous -> current.CompletionHistory @ [ previous ]
                            | None -> current.CompletionHistory })
            task

// Section 24: pure decide receives the decision instant explicitly. This public
// variant is the explicit-time entry used by the imperative apply boundary
// (see applyTask); passing time as an argument keeps decide deterministic with
// no ambient clock read inside the domain.
let decideAt (profiles: Map<string, EffectiveProfile>) (now: DateTimeOffset) (task: TaskModel) command =
    Domain.decide profiles now task command

// Deterministic 3-arg entry for callers/tests that already hold a task: it
// anchors runtime-created Decisions to the task creation instant, so replaying
// a domain model yields identical events with no ambient time read.
let decide (profiles: Map<string, EffectiveProfile>) (task: TaskModel) command =
    decideAt profiles task.Created task command

let evolve task events = Domain.evolve task events

let canCompleteTask task = Domain.canCompleteTask task

// Section 30.1: the #11 Assignment adapter consumes a WorkItem's durable
// effective owner (including inheritance) through this boundary instead of
// re-deriving ownership. A WorkItem with no effective owner cannot be assigned.
let resolveWorkItemOwner (task: TaskModel) (workItemIdText: string) : Result<WorkItem * Owner, RuntimeError> =
    match Domain.findWorkItem workItemIdText task.WorkItems with
    | None -> Error(NotFound $"WorkItem '{workItemIdText}' was not found")
    | Some item ->
        match Domain.effectiveOwner task workItemIdText with
        | Some owner -> Ok(item, owner)
        | None ->
            Error(
                InvalidInput
                    $"WorkItem '{workItemIdText}' has no effective owner; assign or inherit an owner before projecting an Assignment"
            )

let serialize task = task |> Dto.fromDomain |> Dto.toJson

let deserialize json =
    match Dto.fromJson json with
    | Error error -> Error error
    | Ok dto -> Domain.fromDto builtinProfiles true dto

// Runtime sidecars and locks must stay physically inside the project root. A
// junction/symlink planted at `.tasks`, the task directory, or the sidecar/lock
// name would otherwise redirect reads and writes outside the root, so every
// component from the root to the leaf is rejected when it is a reparse point.
module private PathSafety =
    let private comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase
        else StringComparison.Ordinal

    let private separator = string Path.DirectorySeparatorChar

    let private normalize (path: string) =
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    let private isWithin (root: string) (candidate: string) =
        candidate.Equals(root, comparison)
        || candidate.StartsWith(root + separator, comparison)

    /// True only for an existing file or directory whose final entry is a
    /// reparse point (a junction/symlink itself, not its target).
    let isReparseLeaf (path: string) =
        (File.Exists path || Directory.Exists path)
        && (File.GetAttributes path).HasFlag FileAttributes.ReparsePoint

    /// Resolve a runtime path, failing closed when it escapes the project root or
    /// traverses/lands on a reparse point. The root itself is the trust anchor and
    /// must not be a reparse point.
    let tryResolve (root: string) (candidate: string) : Result<string, RuntimeError> =
        try
            let rootPath = normalize root
            let full = normalize candidate

            if not (Directory.Exists rootPath) then
                Error(InvalidInput $"project root does not exist: {rootPath}")
            elif not (isWithin rootPath full) then
                Error(InvalidInput $"path escapes the project root: {full}")
            elif (File.GetAttributes rootPath).HasFlag FileAttributes.ReparsePoint then
                Error(InvalidInput "project root must not be a reparse point")
            else
                let mutable directory = DirectoryInfo(Path.GetDirectoryName full)
                let mutable reparse = None

                while not (isNull directory) && not (normalize(directory.FullName).Equals(rootPath, comparison)) do
                    if directory.Exists && directory.Attributes.HasFlag FileAttributes.ReparsePoint then
                        reparse <- Some directory.FullName

                    directory <- directory.Parent

                if isNull directory then
                    Error(InvalidInput $"path escapes the project root: {full}")
                elif reparse.IsSome then
                    Error(InvalidInput $"path must not traverse a reparse point: {reparse.Value}")
                elif isReparseLeaf full then
                    Error(InvalidInput $"file must not be a reparse point: {full}")
                else
                    Ok full
        with error ->
                Error(PersistenceFailure $"could not resolve runtime path: {error.Message}")

// A path check is not an ownership boundary: an attacker can replace an
// already-checked directory with a junction before an operation uses it. Keep
// opened handles to every directory in the root-to-task path for the complete
// lock critical section. Windows' directory handles deny deletion of the opened
// directories. The normal path checks remain useful diagnostics, but the handles
// close the check/use gap for both creation and existing-record operations.
module private DirectoryBoundary =
    [<Literal>]
    let private InvalidHandle = -1

    [<Literal>]
    let private FileShareRead = 0x00000001u

    [<Literal>]
    let private FileShareWrite = 0x00000002u

    [<Literal>]
    let private OpenExisting = 3u

    [<Literal>]
    let private BackupSemantics = 0x02000000u

    [<Literal>]
    let private OpenReparsePoint = 0x00200000u

    [<DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern IntPtr createFile(string path, uint32 desiredAccess, uint32 shareMode, IntPtr securityAttributes, uint32 creationDisposition, uint32 flagsAndAttributes, IntPtr templateFile)

    [<DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern uint32 getFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint32 capacity, uint32 flags)

    let private closeHandle (handle: SafeFileHandle) =
        if not (isNull handle) then handle.Dispose()

    let private windowsPathMatchesHandle (path: string) (handle: SafeFileHandle) =
        if not (OperatingSystem.IsWindows()) then
            true
        else
            let buffer = StringBuilder(1024)
            let length = getFinalPathNameByHandle(handle, buffer, uint32 buffer.Capacity, 0u)

            if length = 0u || length >= uint32 buffer.Capacity then
                false
            else
                let actual = buffer.ToString().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                let withoutDevicePrefix =
                    if actual.StartsWith("\\\\?\\", StringComparison.Ordinal) then actual.Substring(4)
                    else actual

                String.Equals(
                    Path.GetFullPath withoutDevicePrefix,
                    Path.GetFullPath path,
                    StringComparison.OrdinalIgnoreCase
                )

    let openExisting (path: string) : Result<SafeFileHandle, RuntimeError> =
        try
            let handle =
                if OperatingSystem.IsWindows() then
                    let nativeHandle =
                        createFile(
                            path,
                            0u,
                            (FileShareRead ||| FileShareWrite),
                            IntPtr.Zero,
                            OpenExisting,
                            (BackupSemantics ||| OpenReparsePoint),
                            IntPtr.Zero
                        )

                    if nativeHandle = IntPtr(InvalidHandle) then
                        Error(PersistenceFailure $"could not open directory boundary: {path}")
                    else
                        Ok(new SafeFileHandle(nativeHandle, true))
                else
                    Error(PersistenceFailure "directory handle boundaries are unsupported on this platform")

            match handle with
            | Error error -> Error error
            | Ok directoryHandle ->
                if not (windowsPathMatchesHandle path directoryHandle) then
                    closeHandle directoryHandle
                    Error(InvalidInput $"directory boundary changed before creation: {path}")
                elif PathSafety.isReparseLeaf path then
                    closeHandle directoryHandle
                    Error(InvalidInput $"path must not be a reparse point: {path}")
                else
                    Ok directoryHandle
        with error ->
            Error(PersistenceFailure $"could not open directory boundary: {error.Message}")

let private sidecarPath root requestedTaskId =
    match taskId requestedTaskId with
    | Error error -> Error error
    | Ok validId ->
        let fullRoot = Path.GetFullPath root
        let taskDirectory = Path.Combine(fullRoot, ".tasks", validId)

        result {
            let! safeTaskDirectory = PathSafety.tryResolve fullRoot taskDirectory
            let! sidecar = PathSafety.tryResolve fullRoot (Path.Combine(safeTaskDirectory, SidecarFileName))
            return sidecar, safeTaskDirectory
        }

// The coordination object is an OS-named mutex rather than a sidecar file. It
// therefore spans producer processes while remaining ephemeral and leaves the
// schema-v1 task directory free of runtime.lock artifacts.
let private coordinationName (path: string) =
    let canonical =
        let fullPath = Path.GetFullPath path
        if OperatingSystem.IsWindows() then fullPath.ToUpperInvariant() else fullPath

    let digest = SHA256.HashData(Encoding.UTF8.GetBytes canonical) |> Convert.ToHexString
    if OperatingSystem.IsWindows() then $"Local\\Mcp.Workflow.Runtime.{digest}" else $"Mcp.Workflow.Runtime.{digest}"

let private withLock (path: string) action =
    try
        use mutex = new Mutex(false, coordinationName path)
        let mutable acquired = false

        try
            try
                mutex.WaitOne() |> ignore
                acquired <- true
            with :? AbandonedMutexException ->
                // WaitOne reports abandonment after transferring ownership to
                // this process; the operation is still safe to continue.
                acquired <- true

            action ()
        finally
            if acquired then mutex.ReleaseMutex()
    with
    | :? UnauthorizedAccessException as error ->
        Error(PersistenceFailure $"could not coordinate runtime operation: {error.Message}")
    | :? IOException as error ->
        Error(PersistenceFailure $"could not coordinate runtime operation: {error.Message}")

let private withWindowsDirectoryBoundaries root taskDirectory createMissing action =
    let fullRoot = Path.GetFullPath root
    let tasksDirectory = Path.Combine(fullRoot, ".tasks")

    try
        // This boundary is intentionally Windows-only. Unix has no complete
        // descriptor-relative implementation here, so it uses the validated
        // pathname boundary below.
        match DirectoryBoundary.openExisting fullRoot with
        | Error error -> Error error
        | Ok rootHandle ->
            use rootHandle = rootHandle

            if createMissing then
                Directory.CreateDirectory tasksDirectory |> ignore

            match DirectoryBoundary.openExisting tasksDirectory with
            | Error error -> Error error
            | Ok tasksHandle ->
                use tasksHandle = tasksHandle

                if createMissing then
                    Directory.CreateDirectory taskDirectory |> ignore

                match DirectoryBoundary.openExisting taskDirectory with
                | Error error -> Error error
                | Ok taskHandle ->
                    use taskHandle = taskHandle
                    action ()
    with error ->
        Error(PersistenceFailure $"could not establish runtime directory boundary: {error.Message}")

// Read-only get path: lenient and registry-free so a task can still be inspected
// while its profile entry is missing or drifted.
let private readTaskLenient path =
    try
        if not (File.Exists path) then
            Error(NotFound $"runtime sidecar does not exist: {path}")
        elif PathSafety.isReparseLeaf path then
            Error(PersistenceFailure $"runtime sidecar must not be a reparse point: {path}")
        else
            match Dto.fromJson (File.ReadAllText path) with
            | Error error -> Error error
            | Ok dto -> Domain.fromDto Map.empty false dto
    with error ->
        Error(PersistenceFailure $"could not read runtime sidecar: {error.Message}")

// Registry-backed read used by apply/validate: a fingerprint mismatch is
// tolerated so decide can surface and reconcile drift.
let private readTaskWith profiles path =
    try
        if not (File.Exists path) then
            Error(NotFound $"runtime sidecar does not exist: {path}")
        elif PathSafety.isReparseLeaf path then
            Error(PersistenceFailure $"runtime sidecar must not be a reparse point: {path}")
        else
            match Dto.fromJson (File.ReadAllText path) with
            | Error error -> Error error
            | Ok dto -> Domain.fromDto profiles false dto
    with error ->
        Error(PersistenceFailure $"could not read runtime sidecar: {error.Message}")

let private loadTaskLenient path = readTaskLenient path

let private loadTaskWith (profiles: Map<string, EffectiveProfile>) path = readTaskWith profiles path

// A task directory has one supported layout: the runtime sidecar at its root.
// Historical TASK.md/references layouts and persisted locks are not inputs.
let private validateTaskDirectory (taskDirectory: string) : Result<unit, RuntimeError> =
    let comparison =
        if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal

    try
        if not (Directory.Exists taskDirectory) then
            Ok()
        else
            let entries = Directory.EnumerateFileSystemEntries taskDirectory |> Seq.toList

            let rec visit (remaining: string list) : Result<unit, RuntimeError> =
                match remaining with
                | [] -> Ok()
                | entry :: rest ->
                    let attributes = File.GetAttributes entry

                    if attributes.HasFlag FileAttributes.ReparsePoint then
                        Error(InvalidInput $"task directory must not contain a reparse point: {entry}")
                    elif String.Equals(Path.GetFileName entry, SidecarFileName, comparison)
                         && not (attributes.HasFlag FileAttributes.Directory) then
                        visit rest
                    else
                        Error(
                            InvalidInput
                                $"task directory contains unsupported entry; only '{SidecarFileName}' is permitted: {entry}"
                        )

            visit entries
    with error ->
        Error(PersistenceFailure $"could not inspect task directory: {error.Message}")

let private withExistingSidecar root taskDirectory (path: string) action =
    // A missing task directory fails closed before any Windows handle is opened so
    // the not-found contract is identical on every platform.
    if not (Directory.Exists taskDirectory) then
        Error(NotFound $"runtime sidecar does not exist: {path}")
    else
        withLock path (fun () ->
            let withinDirectoryBoundary action =
                if OperatingSystem.IsWindows() then
                    withWindowsDirectoryBoundaries root taskDirectory false action
                else
                    action ()

            withinDirectoryBoundary (fun () ->
                result {
                    do! validateTaskDirectory taskDirectory

                    if not (File.Exists path) then
                        return! Error(NotFound $"runtime sidecar does not exist: {path}")

                    let! result = action ()

                    if File.Exists path then
                        return result
                    else
                        return! Error(NotFound $"runtime sidecar does not exist: {path}")
                }))

let private atomicWrite (path: string) (content: string) =
    let directory = Path.GetDirectoryName path
    let temporary = Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")

    try
        if PathSafety.isReparseLeaf path then
            Error(PersistenceFailure $"runtime sidecar must not be a reparse point: {path}")
        else
            use stream =
                new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)

            let bytes = Encoding.UTF8.GetBytes content
            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true
            stream.Dispose()

            if File.Exists path then
                File.Replace(temporary, path, null, true)
            else
                File.Move(temporary, path)

            Ok()
    with error ->
        if File.Exists temporary then File.Delete temporary
        Error(PersistenceFailure $"could not atomically persist runtime sidecar: {error.Message}")

let private persist path task = atomicWrite path (serialize task)

// Section 17: the caller may name an explicit active profile; an omitted profile
// falls back to general and an unknown id fails before any sidecar is written.
let createTaskWithProfile root profileId request =
    result {
        let! path, taskDirectory = sidecarPath root request.Id

        let! profiles = resolveProfiles root
        let requestedProfile = profileId |> Option.defaultValue GeneralProfileId

        let! profile =
            match Map.tryFind requestedProfile profiles with
            | Some profile -> Ok profile
            | None -> Error(InvalidInput $"unknown profile '{requestedProfile}'")

        let! task = Domain.create profile request

        let outcome =
            try
                let createWithinBoundary action =
                    if OperatingSystem.IsWindows() then
                        withWindowsDirectoryBoundaries root taskDirectory true action
                    else
                        Directory.CreateDirectory taskDirectory |> ignore
                        action ()

                createWithinBoundary (fun () ->
                    withLock path (fun () ->
                        result {
                            do! validateTaskDirectory taskDirectory

                            if File.Exists path then
                                return!
                                    Error(
                                        InvalidInput
                                            $"task directory contains runtime state '{SidecarFileName}': {path}"
                                    )

                            return! persist path task
                        }))
            with error ->
                Error(PersistenceFailure $"could not create runtime sidecar: {error.Message}")

        match outcome with
        | Ok () -> return task
        | Error error -> return! Error error
    }

let createTask root request = createTaskWithProfile root None request

let getTask root id =
    result {
        let! path, taskDirectory = sidecarPath root id
        return! withExistingSidecar root taskDirectory path (fun () -> loadTaskLenient path)
    }

// Ordinary CLI/model apply boundary. Invocation is Coordinator-only: there is no
// authority parameter, receipt factory, or User ingress, so User-required
// operations fail closed inside decide until #13 supplies a signed-attestation
// bridge.
let applyTask root id expectedRevision command =
    result {
        let! path, taskDirectory = sidecarPath root id
        let! profiles = resolveProfiles root

        return!
            withExistingSidecar root taskDirectory path (fun () ->
                result {
                    let! task = loadTaskWith profiles path

                    if task.StateRevision <> expectedRevision then
                        return! Error(Conflict(expectedRevision, task.StateRevision))

                    let! events =
                        match decideAt profiles DateTimeOffset.UtcNow task command with
                        | Ok events -> Ok events
                        | Error errors ->
                            // W4/AC19: carry the structured authority remediation
                            // through unchanged instead of flattening it into a
                            // text-only InvalidInput. Every other error list keeps
                            // its existing flattened envelope.
                            match
                                errors
                                |> List.tryPick (fun error ->
                                    match error with
                                    | AuthorityDenied _ -> Some error
                                    | _ -> None)
                            with
                            | Some authorityError -> Error authorityError
                            | None -> Error(InvalidInput(errors |> List.map errorMessage |> String.concat "; "))

                    let next = evolve task events
                    let next = { next with StateRevision = task.StateRevision + 1 }
                    do! persist path next
                    return next
                })
    }

let validateTask root id =
    result {
        let! path, taskDirectory = sidecarPath root id
        let! profiles = resolveProfiles root
        let! task = withExistingSidecar root taskDirectory path (fun () -> loadTaskWith profiles path)

        // Section 20/23: validation is read-only but truthful. getTask stays
        // lenient; validate fails explicitly when the recorded profile is missing
        // or drifted, or when the persisted contract no longer matches its
        // recorded fingerprint.
        match Domain.validationFindings profiles task with
        | [] -> return task
        | findings -> return! Error(InvalidTransition(findings |> String.concat "; "))
    }

let renderError error = errorMessage error
