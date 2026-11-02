using DBSync.Connectors.Abstractions;
using DBSync.Domain;
using DBSync.SyncEngine.Cdc;
using DBSync.SyncEngine.Pipeline;
using DBSync.SyncEngine.Queue;
using DBSync.SyncEngine.Validation;
using Xunit;

namespace DBSync.UnitTests;

public class ValuesTests
{
    [Fact] public void Boolean_and_integer_normalize_equally() =>
        Assert.Equal(Values.Normalize(true, LogicalTypes.Boolean), Values.Normalize(1, LogicalTypes.Boolean));

    [Fact] public void Decimal_scale_is_ignored() =>
        Assert.Equal(Values.Normalize(5000000.50m, LogicalTypes.Decimal), Values.Normalize(5000000.5000m, LogicalTypes.Decimal));

    [Fact] public void Cdc_string_decimal_equals_typed_decimal() =>
        Assert.Equal(Values.Normalize(5000000.50m, LogicalTypes.Decimal), Values.Normalize("5000000.50", LogicalTypes.Decimal));

    [Fact] public void Cdc_string_integer_and_timestamp_equal_typed_values()
    {
        Assert.Equal(Values.Normalize(42, LogicalTypes.Integer), Values.Normalize("42", LogicalTypes.Integer));
        Assert.Equal(Values.Normalize(new DateTime(2026, 1, 1, 10, 0, 0), LogicalTypes.Timestamp), Values.Normalize("2026-01-01T10:00:00", LogicalTypes.Timestamp));
    }

    [Fact] public void Date_ignores_time_part() =>
        Assert.Equal(Values.Normalize(new DateTime(2020, 1, 15), LogicalTypes.Date), Values.Normalize(new DateTime(2020, 1, 15, 13, 0, 0), LogicalTypes.Date));

    [Fact] public void Json_key_order_is_canonical() =>
        Assert.Equal(Values.Normalize("{\"b\":1,\"a\":2}", LogicalTypes.Json), Values.Normalize("{\"a\":2,\"b\":1}", LogicalTypes.Json));

    [Fact] public void Null_differs_from_empty_string() =>
        Assert.NotEqual(Values.Normalize(null, LogicalTypes.Text), Values.Normalize("", LogicalTypes.Text));

    [Fact] public void Hash_distinguishes_field_boundaries() =>
        Assert.NotEqual(Values.Hash(new[] { "ab", "c" }), Values.Hash(new[] { "a", "bc" }));

    [Fact] public void Coerce_string_to_guid()
    {
        var col = new ColumnInfo("id", "uuid", LogicalTypes.Uuid, false, true, 1);
        var g = Guid.NewGuid();
        Assert.Equal(g, Values.Coerce(g.ToString(), col));
    }

    [Fact] public void Coerce_invalid_value_raises_permanent_error()
    {
        var col = new ColumnInfo("n", "int", LogicalTypes.Integer, false, false, 1);
        var ex = Assert.Throws<ConnectorException>(() => Values.Coerce("abc", col));
        Assert.Equal(ErrorClass.Permanent, ex.Class);
    }

    [Fact] public void Coerce_bool_to_integer() =>
        Assert.Equal(1, Values.Coerce(true, new ColumnInfo("n", "int", LogicalTypes.Integer, false, false, 1)));
}

public class StateMachineTests
{
    [Theory]
    [InlineData(EventStatus.Pending, EventStatus.Processing, true)]
    [InlineData(EventStatus.Processing, EventStatus.Success, true)]
    [InlineData(EventStatus.Success, EventStatus.Pending, false)]
    [InlineData(EventStatus.DeadLetter, EventStatus.Retry, true)]
    [InlineData(EventStatus.Skipped, EventStatus.Processing, false)]
    public void Transitions(string from, string to, bool ok) => Assert.Equal(ok, EventStatus.CanTransition(from, to));
}

public class BackoffTests
{
    [Fact] public void Delay_grows_exponentially_with_cap()
    {
        var d1 = Backoff.Delay(1, 2, 60).TotalSeconds; var d4 = Backoff.Delay(4, 2, 60).TotalSeconds; var d20 = Backoff.Delay(20, 2, 60).TotalSeconds;
        Assert.InRange(d1, 2, 2.6); Assert.InRange(d4, 16, 20.1); Assert.InRange(d20, 60, 75.1);
    }

    [Fact] public void Permanent_error_goes_straight_to_dead_letter()
    {
        var ev = new DBSync.Domain.Entities.SyncEvent();
        EventRecorder.UpdateFromOutcome(ev, new ConnectorException(ErrorClass.Permanent, "X", "bad"), 3);
        Assert.Equal(EventStatus.DeadLetter, ev.Status);
    }

    [Fact] public void Transient_error_retries_then_dead_letters()
    {
        var ev = new DBSync.Domain.Entities.SyncEvent();
        for (int i = 0; i < 3; i++) EventRecorder.UpdateFromOutcome(ev, new ConnectorException(ErrorClass.Transient, "T", "net"), 3);
        Assert.Equal(EventStatus.Retry, ev.Status);
        EventRecorder.UpdateFromOutcome(ev, new ConnectorException(ErrorClass.Transient, "T", "net"), 3);
        Assert.Equal(EventStatus.DeadLetter, ev.Status);
    }
}

public class ValidatorTests
{
    private static TableSchema T(string n, params ColumnInfo[] c) => new("public", n, c, Array.Empty<ForeignKeyInfo>());

    [Fact] public void Table_without_pk_is_invalid()
    {
        var a = T("t", new ColumnInfo("x", "int", LogicalTypes.Integer, false, false, 1));
        var rep = RuleValidator.Validate(a, a, ColumnMappingBuilder.AutoMap(a, a));
        Assert.Equal("INVALID", rep.Status);
        Assert.Contains(rep.Issues, i => i.Code == "PRIMARY_KEY_REQUIRED");
    }

    [Fact] public void Unsupported_type_is_error()
    {
        var a = T("t", new ColumnInfo("id", "int", LogicalTypes.Integer, false, true, 1), new ColumnInfo("g", "geometry", LogicalTypes.Other, true, false, 2));
        var rep = RuleValidator.Validate(a, a, ColumnMappingBuilder.AutoMap(a, a));
        Assert.Contains(rep.Issues, i => i.Code == "UNSUPPORTED_TYPE");
    }

    [Fact] public void Compatible_tables_are_valid()
    {
        var a = T("t", new ColumnInfo("id", "int", LogicalTypes.Integer, false, true, 1), new ColumnInfo("n", "varchar", LogicalTypes.Text, true, false, 2, 100));
        Assert.Equal("VALID", RuleValidator.Validate(a, a, ColumnMappingBuilder.AutoMap(a, a)).Status);
    }
}

public class DebeziumTests
{
    [Fact] public void Parses_update_envelope_with_date_and_timestamp()
    {
        const string json = """
        {"schema":{"type":"struct","fields":[
          {"field":"before","type":"struct","fields":[{"field":"id","type":"int32"},{"field":"hired","type":"int32","name":"io.debezium.time.Date"}]},
          {"field":"after","type":"struct","fields":[{"field":"id","type":"int32"},{"field":"hired","type":"int32","name":"io.debezium.time.Date"},{"field":"ts","type":"int64","name":"io.debezium.time.Timestamp"}]}]},
         "payload":{"before":{"id":1,"hired":18276},"after":{"id":1,"hired":18276,"ts":1767261600000},"op":"u","ts_ms":1,"source":{"db":"hr","schema":"public","table":"employees","lsn":42}}}
        """;
        var r = DebeziumEventNormalizer.Parse(json)!;
        Assert.Equal("u", r.Op);
        Assert.Equal("public", r.EffectiveSchema);
        Assert.Equal(new DateTime(2020, 1, 15), r.After!["hired"]);
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0), r.After["ts"]);
    }

    [Fact] public void Mysql_schema_falls_back_to_database_name()
    {
        const string json = """{"payload":{"before":null,"after":{"id":1},"op":"c","source":{"db":"hrga","table":"t"}}}""";
        Assert.Equal("hrga", DebeziumEventNormalizer.Parse(json)!.EffectiveSchema);
    }
}
