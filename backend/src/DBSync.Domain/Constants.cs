namespace DBSync.Domain;

public static class DbTypes
{
    public const string MySql = "MYSQL";
    public const string PostgreSql = "POSTGRESQL";
    public const string SqlServer = "SQLSERVER";
    public static readonly string[] All = { MySql, PostgreSql, SqlServer };
}

public static class Directions
{
    public const string AToB = "A_TO_B";
    public const string BToA = "B_TO_A";
    public const string Bidirectional = "BIDIRECTIONAL";
    public static readonly string[] All = { AToB, BToA, Bidirectional };
}

public static class SyncModes
{
    public const string Scheduled = "SCHEDULED";
    public const string Cdc = "CDC";
    public const string Hybrid = "HYBRID";
    public static readonly string[] All = { Scheduled, Cdc, Hybrid };
}

public static class ConflictStrategies
{
    public const string Manual = "MANUAL";
    public const string LastWriteWins = "LAST_WRITE_WINS";
    public const string APriority = "A_PRIORITY";
    public const string BPriority = "B_PRIORITY";
    public static readonly string[] All = { Manual, LastWriteWins, APriority, BPriority };
}

public static class GroupStatus
{
    public const string Draft = "DRAFT";
    public const string Validating = "VALIDATING";
    public const string Active = "ACTIVE";
    public const string Paused = "PAUSED";
    public const string Error = "ERROR";
    public const string Stopped = "STOPPED";
}

public static class EventStatus
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Success = "SUCCESS";
    public const string Retry = "RETRY";
    public const string Failed = "FAILED";
    public const string Conflict = "CONFLICT";
    public const string Skipped = "SKIPPED";
    public const string DeadLetter = "DEAD_LETTER";

    // State machine: transisi yang diizinkan.
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [Pending] = new[] { Processing, Skipped, Conflict, Success, Retry, DeadLetter },
        [Processing] = new[] { Success, Retry, Failed, Conflict, Skipped, DeadLetter },
        [Retry] = new[] { Processing, Success, DeadLetter, Retry, Failed, Conflict },
        [Failed] = new[] { Retry, DeadLetter, Pending },
        [Conflict] = new[] { Pending, Success, Skipped, DeadLetter },
        [DeadLetter] = new[] { Pending, Retry },
        [Success] = Array.Empty<string>(),
        [Skipped] = Array.Empty<string>(),
    };

    public static bool CanTransition(string from, string to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);
}

public static class RunStatus
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Success = "SUCCESS";
    public const string PartialSuccess = "PARTIAL";
    public const string Failed = "FAILED";
    public const string Cancelling = "CANCELLING";
    public const string Cancelled = "CANCELLED";
}

public static class RunTypes
{
    public const string Initial = "INITIAL";
    public const string Scheduled = "SCHEDULED";
    public const string Manual = "MANUAL";
}

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public const string Auditor = "Auditor";
    public static readonly string[] All = { SuperAdmin, Administrator, Operator, Viewer, Auditor };
}

public static class Ops
{
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
}
