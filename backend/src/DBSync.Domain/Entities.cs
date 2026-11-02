using System.Text.Json;

namespace DBSync.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<UserRole> UserRoles { get; set; } = new();
}

public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public class UserRole
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public class DbConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ConnectionName { get; set; } = "";
    public string DbType { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string DatabaseName { get; set; } = "";
    public string Username { get; set; } = "";
    public bool SslEnabled { get; set; }
    public JsonDocument? ConnectionOptions { get; set; }
    public string Status { get; set; } = "UNKNOWN";
    public string? LastTestMessage { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ConnectionSecret? Secret { get; set; }
}

public class ConnectionSecret
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public string EncryptedPassword { get; set; } = "";
    public string EncryptionKeyVersion { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RotatedAt { get; set; }
}

public class SyncGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string GroupName { get; set; } = "";
    public string? Description { get; set; }
    public string SyncDirection { get; set; } = Directions.Bidirectional;
    public string SyncMode { get; set; } = SyncModes.Scheduled;
    public string ConflictStrategy { get; set; } = ConflictStrategies.Manual;
    public string Status { get; set; } = GroupStatus.Draft;
    public int ScheduleIntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 500;
    public int MaxRetries { get; set; } = 3;
    public int MaxParallelTables { get; set; } = 2;
    public int CommandTimeoutSeconds { get; set; } = 60;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SyncEndpoint> Endpoints { get; set; } = new();
    public List<SyncTableRule> TableRules { get; set; } = new();
}

public class SyncEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncGroupId { get; set; }
    public SyncGroup? SyncGroup { get; set; }
    public Guid ConnectionId { get; set; }
    public DbConnection? Connection { get; set; }
    public string EndpointRole { get; set; } = "A"; // A | B
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SchemaSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string SchemaHash { get; set; } = "";
    public JsonDocument SchemaDefinition { get; set; } = JsonDocument.Parse("{}");
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SyncTableRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncGroupId { get; set; }
    public SyncGroup? SyncGroup { get; set; }
    public string EndpointASchema { get; set; } = "";
    public string EndpointATable { get; set; } = "";
    public string EndpointBSchema { get; set; } = "";
    public string EndpointBTable { get; set; } = "";
    public bool SyncAToB { get; set; } = true;
    public bool SyncBToA { get; set; } = true;
    public bool SyncInsert { get; set; } = true;
    public bool SyncUpdate { get; set; } = true;
    public bool SyncDelete { get; set; } = true;
    public string WriteMode { get; set; } = "UPSERT"; // UPSERT | INSERT_ONLY
    public string InitialSyncStatus { get; set; } = "PENDING";
    public string ValidationStatus { get; set; } = "UNVALIDATED"; // UNVALIDATED|VALID|WARNING|INVALID
    public JsonDocument? ValidationResult { get; set; }
    public string? UpdatedAtColumn { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SyncColumnMapping> ColumnMappings { get; set; } = new();
}

public class SyncColumnMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TableRuleId { get; set; }
    public SyncTableRule? TableRule { get; set; }
    public string EndpointAColumn { get; set; } = "";
    public string EndpointBColumn { get; set; } = "";
    public string? DataTypeStrategy { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SyncCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncGroupId { get; set; }
    public Guid TableRuleId { get; set; }
    public string Direction { get; set; } = "";
    public JsonDocument? CapturePosition { get; set; }
    public JsonDocument? DurablePosition { get; set; }
    public JsonDocument? AppliedPosition { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Baseline hash per baris (tiga arah) untuk change detection, echo suppression dan loop prevention.</summary>
public class SyncRowState
{
    public Guid TableRuleId { get; set; }
    public string PkKey { get; set; } = "";
    public string RowHash { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SyncEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; } = Guid.NewGuid();
    public Guid SyncGroupId { get; set; }
    public Guid TableRuleId { get; set; }
    public Guid? RunId { get; set; }
    public string SourceRole { get; set; } = "A"; // endpoint asal perubahan
    public string Direction { get; set; } = "";
    public string Operation { get; set; } = "";
    public string PkKey { get; set; } = "";
    public JsonDocument PrimaryKey { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument? Payload { get; set; }
    public JsonDocument? SourcePosition { get; set; }
    public Guid? OriginEventId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string Status { get; set; } = EventStatus.Pending;
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
}

public class ProcessedEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    public string DestinationRole { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ResultHash { get; set; }
}

public class SyncConflict
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncGroupId { get; set; }
    public Guid TableRuleId { get; set; }
    public string PkKey { get; set; } = "";
    public JsonDocument PrimaryKey { get; set; } = JsonDocument.Parse("{}");
    public JsonDocument? EndpointAValue { get; set; }
    public JsonDocument? EndpointBValue { get; set; }
    public JsonDocument? EndpointAVersion { get; set; }
    public JsonDocument? EndpointBVersion { get; set; }
    public string ConflictType { get; set; } = "";
    public string Status { get; set; } = "PENDING"; // PENDING | RESOLVED
    public string? ResolutionStrategy { get; set; }
    public JsonDocument? ResolvedValue { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncGroupId { get; set; }
    public SyncGroup? SyncGroup { get; set; }
    public string RunType { get; set; } = RunTypes.Manual;
    public string Status { get; set; } = RunStatus.Queued;
    public JsonDocument? Options { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset QueuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long RecordsRead { get; set; }
    public long RecordsInserted { get; set; }
    public long RecordsUpdated { get; set; }
    public long RecordsDeleted { get; set; }
    public long RecordsSkipped { get; set; }
    public long RecordsFailed { get; set; }
    public long ConflictsDetected { get; set; }
    public string? ErrorSummary { get; set; }
    public bool CancelRequested { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public long FencingToken { get; set; }
    public List<SyncRunItem> Items { get; set; } = new();
}

public class SyncRunItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SyncRunId { get; set; }
    public Guid TableRuleId { get; set; }
    public string TableLabel { get; set; } = "";
    public int BatchNumber { get; set; }
    public string Status { get; set; } = RunStatus.Queued;
    public long? TotalRows { get; set; }
    public long RecordsRead { get; set; }
    public long RecordsWritten { get; set; }
    public long RecordsSkipped { get; set; }
    public long RecordsFailed { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

public class WorkerHeartbeat
{
    public string WorkerId { get; set; } = "";
    public string WorkerType { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Status { get; set; } = "RUNNING";
    public DateTimeOffset LastHeartbeatAt { get; set; } = DateTimeOffset.UtcNow;
    public JsonDocument? Metadata { get; set; }
}

public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string? Username { get; set; }
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string? ResourceId { get; set; }
    public JsonDocument? BeforeValue { get; set; }
    public JsonDocument? AfterValue { get; set; }
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
