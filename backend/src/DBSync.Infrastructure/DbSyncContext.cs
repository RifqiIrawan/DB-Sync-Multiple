using System.Text;
using System.Text.RegularExpressions;
using DBSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DBSync.Infrastructure;

public class DbSyncContext : DbContext
{
    public DbSyncContext(DbContextOptions<DbSyncContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<DbConnection> DbConnections => Set<DbConnection>();
    public DbSet<ConnectionSecret> ConnectionSecrets => Set<ConnectionSecret>();
    public DbSet<SyncGroup> SyncGroups => Set<SyncGroup>();
    public DbSet<SyncEndpoint> SyncEndpoints => Set<SyncEndpoint>();
    public DbSet<SchemaSnapshot> SchemaSnapshots => Set<SchemaSnapshot>();
    public DbSet<SyncTableRule> SyncTableRules => Set<SyncTableRule>();
    public DbSet<SyncColumnMapping> SyncColumnMappings => Set<SyncColumnMapping>();
    public DbSet<SyncCheckpoint> SyncCheckpoints => Set<SyncCheckpoint>();
    public DbSet<SyncRowState> SyncRowStates => Set<SyncRowState>();
    public DbSet<SyncEvent> SyncEvents => Set<SyncEvent>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<SyncConflict> SyncConflicts => Set<SyncConflict>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncRunItem> SyncRunItems => Set<SyncRunItem>();
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserRole>().HasKey(x => new { x.UserId, x.RoleId });
        b.Entity<UserRole>().HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId);
        b.Entity<UserRole>().HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        b.Entity<User>().HasIndex(x => x.Username).IsUnique();
        b.Entity<Role>().HasIndex(x => x.Name).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => x.TokenHash);

        b.Entity<DbConnection>().HasIndex(x => x.DbType);
        b.Entity<DbConnection>().HasOne(x => x.Secret).WithOne().HasForeignKey<ConnectionSecret>(s => s.ConnectionId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<SyncGroup>().HasIndex(x => x.Status);
        b.Entity<SyncEndpoint>().HasOne(x => x.SyncGroup).WithMany(g => g.Endpoints).HasForeignKey(x => x.SyncGroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SyncEndpoint>().HasOne(x => x.Connection).WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SyncEndpoint>().HasIndex(x => new { x.SyncGroupId, x.EndpointRole }).IsUnique();
        b.Entity<SyncEndpoint>().HasIndex(x => new { x.SyncGroupId, x.ConnectionId }).IsUnique();

        b.Entity<SchemaSnapshot>().HasIndex(x => new { x.ConnectionId, x.SchemaName, x.TableName });

        b.Entity<SyncTableRule>().HasOne(x => x.SyncGroup).WithMany(g => g.TableRules).HasForeignKey(x => x.SyncGroupId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SyncTableRule>().HasIndex(x => new { x.SyncGroupId, x.EndpointASchema, x.EndpointATable }).IsUnique();
        b.Entity<SyncColumnMapping>().HasOne(x => x.TableRule).WithMany(r => r.ColumnMappings).HasForeignKey(x => x.TableRuleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<SyncColumnMapping>().HasIndex(x => new { x.TableRuleId, x.EndpointAColumn, x.EndpointBColumn }).IsUnique();

        b.Entity<SyncCheckpoint>().HasIndex(x => new { x.TableRuleId, x.Direction }).IsUnique();
        b.Entity<SyncRowState>().HasKey(x => new { x.TableRuleId, x.PkKey });

        b.Entity<SyncEvent>().HasIndex(x => x.EventId).IsUnique();
        b.Entity<SyncEvent>().HasIndex(x => new { x.Status, x.NextRetryAt, x.CreatedAt });
        b.Entity<SyncEvent>().HasIndex(x => new { x.SyncGroupId, x.CreatedAt });
        b.Entity<SyncEvent>().HasIndex(x => new { x.TableRuleId, x.PkKey });
        b.Entity<ProcessedEvent>().HasIndex(x => new { x.EventId, x.DestinationRole }).IsUnique();

        b.Entity<SyncConflict>().HasIndex(x => new { x.TableRuleId, x.PkKey, x.Status });
        b.Entity<SyncRun>().HasIndex(x => new { x.SyncGroupId, x.Status });
        b.Entity<SyncRun>().HasIndex(x => x.QueuedAt);
        b.Entity<SyncRunItem>().HasOne<SyncRun>().WithMany(r => r.Items).HasForeignKey(x => x.SyncRunId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<WorkerHeartbeat>().HasKey(x => x.WorkerId);
        b.Entity<AuditLog>().HasIndex(x => x.CreatedAt);

        // snake_case untuk semua tabel/kolom/index/key
        foreach (var et in b.Model.GetEntityTypes())
        {
            et.SetTableName(Snake(et.GetTableName() ?? et.ClrType.Name, true));
            foreach (var p in et.GetProperties())
                p.SetColumnName(Snake(p.Name, false));
            foreach (var k in et.GetKeys()) k.SetName(Snake(k.GetName() ?? "", false));
            foreach (var i in et.GetIndexes()) i.SetDatabaseName(Snake(i.GetDatabaseName() ?? "", false));
            foreach (var f in et.GetForeignKeys()) f.SetConstraintName(Snake(f.GetConstraintName() ?? "", false));
        }
    }

    private static string Snake(string s, bool plural)
    {
        var r = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        if (!plural) return r;
        return r switch
        {
            "db_connection" => "db_connections",
            "sync_row_state" => "sync_row_states",
            "audit_log" => "audit_logs",
            _ => r.EndsWith("s") ? r : r + "s"
        };
    }
}
