using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Hugo.Deterministic.SqlServer;

/// <summary>
/// Options controlling how <see cref="SqlServerDeterministicStateStore"/> persists deterministic records.
/// </summary>
public sealed class SqlServerDeterministicStateStoreOptions
{
    private string _schema = "dbo";
    private string _tableName = "DeterministicRecords";

    /// <summary>
    /// Gets or sets the connection string used to reach the SQL Server instance.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the schema that contains the deterministic records table. Defaults to <c>dbo</c>.
    /// </summary>
    public string Schema
    {
        get => _schema;
        set => _schema = ValidateIdentifier(value, nameof(Schema));
    }

    /// <summary>
    /// Gets or sets the table used to persist deterministic records. Defaults to <c>DeterministicRecords</c>.
    /// </summary>
    public string TableName
    {
        get => _tableName;
        set => _tableName = ValidateIdentifier(value, nameof(TableName));
    }

    /// <summary>
    /// Gets or sets a value indicating whether the store should create the backing table when it does not exist.
    /// </summary>
    public bool AutoCreateTable { get; set; } = true;

    private static string ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifier cannot be null, empty, or whitespace.", parameterName);
        }

        if (!IdentifierValidator.IsMatch(value))
        {
            throw new ArgumentException($"Identifier '{value}' contains invalid characters.", parameterName);
        }

        return value;
    }

    private static readonly Regex IdentifierValidator = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
}

/// <summary>
/// SQL Server implementation of <see cref="IDeterministicStateStore"/>.
/// </summary>
public sealed class SqlServerDeterministicStateStore : IDeterministicStateStore
{
    private readonly SqlServerDeterministicStateStoreOptions _options;
    private readonly string _qualifiedTable;
    private volatile bool _initialized;
    private readonly object _initializationLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlServerDeterministicStateStore"/> class.
    /// </summary>
    /// <param name="options">Options describing how to connect to SQL Server and locate the backing table.</param>
    public SqlServerDeterministicStateStore(SqlServerDeterministicStateStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("A connection string must be supplied.", nameof(options));
        }

        _qualifiedTable = $"[{options.Schema}].[{options.TableName}]";
    }

    /// <inheritdoc />
    public bool TryGet(string key, out DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureInitialized();

        using SqlConnection connection = new(_options.ConnectionString);
        connection.Open();

        using SqlCommand command = new(
            $"""
             SELECT TOP (1) [Kind], [Version], [RecordedAt], [Payload]
             FROM {_qualifiedTable}
             WHERE [Key] = @key
             """,
            connection);

        command.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 256) { Value = key });

        using SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow);
        if (!reader.Read())
        {
            record = null!;
            return false;
        }

        string kind = reader.GetString(0);
        int version = reader.GetInt32(1);
        DateTimeOffset recordedAt = reader.GetDateTimeOffset(2);
        byte[] payload = (byte[])reader.GetValue(3);

        record = new DeterministicRecord(kind, version, payload, recordedAt);
        return true;
    }

    /// <inheritdoc />
    public void Set(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);
        EnsureInitialized();

        using SqlConnection connection = new(_options.ConnectionString);
        connection.Open();

        using SqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            int rowsAffected = ExecuteUpdate(key, record, connection, transaction);
            if (rowsAffected == 0)
            {
                ExecuteInsert(key, record, connection, transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public bool TryAdd(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);
        EnsureInitialized();

        using SqlConnection connection = new(_options.ConnectionString);
        connection.Open();

        using SqlCommand insert = new(
            $"""
             INSERT INTO {_qualifiedTable} ([Key], [Kind], [Version], [RecordedAt], [Payload])
             VALUES (@key, @kind, @version, @recordedAt, @payload)
             """,
            connection);

        insert.Parameters.Add(CreateKeyParameter(key));
        AddRecordParameters(insert, record);

        try
        {
            return insert.ExecuteNonQuery() == 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return false;
        }
    }

    private int ExecuteUpdate(string key, DeterministicRecord record, SqlConnection connection, SqlTransaction transaction)
    {
        using SqlCommand update = new(
            $"""
             UPDATE {_qualifiedTable}
             SET [Kind] = @kind,
                 [Version] = @version,
                 [RecordedAt] = @recordedAt,
                 [Payload] = @payload
             WHERE [Key] = @key
             """,
            connection,
            transaction);

        update.Parameters.Add(CreateKeyParameter(key));
        AddRecordParameters(update, record);

        return update.ExecuteNonQuery();
    }

    private void ExecuteInsert(string key, DeterministicRecord record, SqlConnection connection, SqlTransaction transaction)
    {
        using SqlCommand insert = new(
            $"""
             INSERT INTO {_qualifiedTable} ([Key], [Kind], [Version], [RecordedAt], [Payload])
             VALUES (@key, @kind, @version, @recordedAt, @payload)
             """,
            connection,
            transaction);

        insert.Parameters.Add(CreateKeyParameter(key));
        AddRecordParameters(insert, record);

        insert.ExecuteNonQuery();
    }

    private static void AddRecordParameters(SqlCommand command, DeterministicRecord record)
    {
        command.Parameters.Add(new SqlParameter("@kind", SqlDbType.NVarChar, 128) { Value = record.Kind });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Int) { Value = record.Version });
        command.Parameters.Add(new SqlParameter("@recordedAt", SqlDbType.DateTimeOffset) { Value = record.RecordedAt });
        command.Parameters.Add(new SqlParameter("@payload", SqlDbType.VarBinary, -1)
        {
            Value = record.Payload.ToArray()
        });
    }

    private static SqlParameter CreateKeyParameter(string key) =>
        new("@key", SqlDbType.NVarChar, 256) { Value = key };

    private void EnsureInitialized()
    {
        if (_initialized || !_options.AutoCreateTable)
        {
            if (_initialized)
            {
                return;
            }

            lock (_initializationLock)
            {
                _initialized = true;
                return;
            }
        }

        if (_initialized)
        {
            return;
        }

        lock (_initializationLock)
        {
            if (_initialized)
            {
                return;
            }

            using SqlConnection connection = new(_options.ConnectionString);
            connection.Open();

            using SqlCommand exists = new(
                """
                SELECT 1
                FROM sys.tables AS t
                INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
                WHERE t.name = @tableName AND s.name = @schemaName
                """,
                connection);

            exists.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = _options.TableName });
            exists.Parameters.Add(new SqlParameter("@schemaName", SqlDbType.NVarChar, 128) { Value = _options.Schema });

            object? result = exists.ExecuteScalar();
            if (result is null)
            {
                string createTableSql = string.Format(
                    CultureInfo.InvariantCulture,
                    """
                    CREATE TABLE {0} (
                        [Key] NVARCHAR(256) NOT NULL CONSTRAINT PK_{1}_{2}_Deterministic PRIMARY KEY,
                        [Kind] NVARCHAR(128) NOT NULL,
                        [Version] INT NOT NULL,
                        [RecordedAt] DATETIMEOFFSET NOT NULL,
                        [Payload] VARBINARY(MAX) NOT NULL
                    );
                    """,
                    _qualifiedTable,
                    _options.Schema,
                    _options.TableName);

                using SqlCommand createTable = new(createTableSql, connection);
                createTable.ExecuteNonQuery();
            }

            _initialized = true;
        }
    }
}

/// <summary>
/// Service collection extensions for registering <see cref="SqlServerDeterministicStateStore"/>.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a SQL Server backed deterministic state store.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="connectionString">Connection string used to reach SQL Server.</param>
    /// <param name="configure">Optional callback to customise store options.</param>
    /// <returns>The <paramref name="services"/> instance so calls can be chained.</returns>
    public static IServiceCollection AddHugoDeterministicSqlServer(
        this IServiceCollection services,
        string connectionString,
        Action<SqlServerDeterministicStateStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        SqlServerDeterministicStateStoreOptions options = new()
        {
            ConnectionString = connectionString
        };

        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IDeterministicStateStore, SqlServerDeterministicStateStore>();
        return services;
    }
}
