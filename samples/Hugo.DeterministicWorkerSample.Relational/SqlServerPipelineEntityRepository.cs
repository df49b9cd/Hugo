using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Hugo;
using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hugo.DeterministicWorkerSample.Relational;

/// <summary>
/// Options controlling how the relational pipeline repository connects to SQL Server.
/// </summary>
internal sealed class SqlServerPipelineOptions
{
    /// <summary>
    /// Gets or sets the connection string used for both migrations and data access.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the schema containing the pipeline projection table.
    /// </summary>
    public string Schema { get; set; } = "dbo";

    /// <summary>
    /// Gets or sets the table that stores pipeline projections.
    /// </summary>
    public string TableName { get; set; } = "PipelineEntities";
}

/// <summary>
/// Ensures the relational pipeline projection table exists before the worker runs.
/// </summary>
internal sealed class SqlServerPipelineSchemaMigrator
{
    private readonly SqlServerPipelineOptions _options;
    private readonly ILogger<SqlServerPipelineSchemaMigrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _initialized;

    public SqlServerPipelineSchemaMigrator(IOptions<SqlServerPipelineOptions> options, ILogger<SqlServerPipelineSchemaMigrator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using SqlConnection connection = new(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string ExistsSql =
                """
                SELECT 1
                FROM sys.tables AS t
                INNER JOIN sys.schemas AS s ON t.schema_id = s.schema_id
                WHERE t.name = @tableName AND s.name = @schemaName
                """;

            await using SqlCommand exists = new(ExistsSql, connection);
            exists.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = _options.TableName });
            exists.Parameters.Add(new SqlParameter("@schemaName", SqlDbType.NVarChar, 128) { Value = _options.Schema });

            object? result = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                string qualifiedTable = QualifiedTable();
                string createTableSql = string.Format(
                    CultureInfo.InvariantCulture,
                    """
                    CREATE TABLE {0} (
                        EntityId NVARCHAR(128) NOT NULL CONSTRAINT PK_{1}_{2}_Pipeline PRIMARY KEY,
                        RunningTotal FLOAT NOT NULL,
                        ProcessedCount INT NOT NULL,
                        LastAmount FLOAT NOT NULL,
                        LastUpdated DATETIMEOFFSET NOT NULL
                    );
                    """,
                    qualifiedTable,
                    _options.Schema,
                    _options.TableName);

                await using SqlCommand createTable = new(createTableSql, connection);
                await createTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Created pipeline entity table {Table}.", qualifiedTable);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string QualifiedTable() => $"[{_options.Schema}].[{_options.TableName}]";
}

/// <summary>
/// SQL Server implementation of <see cref="IPipelineEntityRepository"/>.
/// </summary>
internal sealed class SqlServerPipelineEntityRepository : IPipelineEntityRepository
{
    private readonly SqlServerPipelineOptions _options;
    private readonly SqlServerPipelineSchemaMigrator _migrator;
    private readonly ILogger<SqlServerPipelineEntityRepository> _logger;

    public SqlServerPipelineEntityRepository(
        IOptions<SqlServerPipelineOptions> options,
        SqlServerPipelineSchemaMigrator migrator,
        ILogger<SqlServerPipelineEntityRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<Result<PipelineEntity>> LoadAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        await _migrator.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string Sql =
            """
            SELECT RunningTotal, ProcessedCount, LastAmount, LastUpdated
            FROM {0}
            WHERE EntityId = @entityId;
            """;

        await using SqlCommand command = new(string.Format(CultureInfo.InvariantCulture, Sql, QualifiedTable()), connection);
        command.Parameters.Add(new SqlParameter("@entityId", SqlDbType.NVarChar, 128) { Value = entityId });

        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            double runningTotal = reader.GetDouble(0);
            int processedCount = reader.GetInt32(1);
            double lastAmount = reader.GetDouble(2);
            DateTimeOffset lastUpdated = reader.GetDateTimeOffset(3);
            return Result.Ok(new PipelineEntity(entityId, runningTotal, processedCount, lastAmount, lastUpdated));
        }

        return Result.Ok(PipelineEntity.Create(entityId));
    }

    public async ValueTask<Result<PipelineEntity>> SaveAsync(PipelineEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _migrator.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using SqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int updated = await ExecuteUpdateAsync(entity, connection, transaction, cancellationToken).ConfigureAwait(false);
            if (updated == 0)
            {
                await ExecuteInsertAsync(entity, connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Ok(entity);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(ex, "Failed to persist entity {EntityId}.", entity.EntityId);
            return Result.Fail<PipelineEntity>(Error.From(ex.Message, ErrorCodes.Exception, ex));
        }
    }

    public IReadOnlyList<PipelineEntity> Snapshot()
    {
        try
        {
            _migrator.EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();

            using SqlConnection connection = new(_options.ConnectionString);
            connection.Open();

            const string Sql =
                """
                SELECT EntityId, RunningTotal, ProcessedCount, LastAmount, LastUpdated
                FROM {0}
                ORDER BY EntityId ASC;
                """;

            using SqlCommand command = new(string.Format(CultureInfo.InvariantCulture, Sql, QualifiedTable()), connection);
            using SqlDataReader reader = command.ExecuteReader(CommandBehavior.Default);

            List<PipelineEntity> entities = [];
            while (reader.Read())
            {
                string entityId = reader.GetString(0);
                double runningTotal = reader.GetDouble(1);
                int processedCount = reader.GetInt32(2);
                double lastAmount = reader.GetDouble(3);
                DateTimeOffset lastUpdated = reader.GetDateTimeOffset(4);
                entities.Add(new PipelineEntity(entityId, runningTotal, processedCount, lastAmount, lastUpdated));
            }

            return entities;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to snapshot pipeline projections.");
            return [];
        }
    }

    private async Task<int> ExecuteUpdateAsync(
        PipelineEntity entity,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql =
            """
            UPDATE {0}
            SET RunningTotal = @runningTotal,
                ProcessedCount = @processedCount,
                LastAmount = @lastAmount,
                LastUpdated = @lastUpdated
            WHERE EntityId = @entityId;
            """;

        await using SqlCommand command = new(string.Format(CultureInfo.InvariantCulture, Sql, QualifiedTable()), connection, transaction);
        AddEntityParameters(command, entity);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteInsertAsync(
        PipelineEntity entity,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string Sql =
            """
            INSERT INTO {0} (EntityId, RunningTotal, ProcessedCount, LastAmount, LastUpdated)
            VALUES (@entityId, @runningTotal, @processedCount, @lastAmount, @lastUpdated);
            """;

        await using SqlCommand command = new(string.Format(CultureInfo.InvariantCulture, Sql, QualifiedTable()), connection, transaction);
        AddEntityParameters(command, entity);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddEntityParameters(SqlCommand command, PipelineEntity entity)
    {
        command.Parameters.Add(new SqlParameter("@entityId", SqlDbType.NVarChar, 128) { Value = entity.EntityId });
        command.Parameters.Add(new SqlParameter("@runningTotal", SqlDbType.Float) { Value = entity.RunningTotal });
        command.Parameters.Add(new SqlParameter("@processedCount", SqlDbType.Int) { Value = entity.ProcessedCount });
        command.Parameters.Add(new SqlParameter("@lastAmount", SqlDbType.Float) { Value = entity.LastAmount });
        command.Parameters.Add(new SqlParameter("@lastUpdated", SqlDbType.DateTimeOffset) { Value = entity.LastUpdated });
    }

    private string QualifiedTable() => $"[{_options.Schema}].[{_options.TableName}]";
}
