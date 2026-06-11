using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Postgres")!));

var app = builder.Build();

// GET /db/ping — checks if the connection is alive
app.MapGet("/db/ping", async (NpgsqlDataSource ds) =>
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version()";
        var version = await cmd.ExecuteScalarAsync();
        return Results.Ok(new { status = "ok", postgres_version = version?.ToString() });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "DB connection failed", statusCode: 500);
    }
});

// GET /db/tables — lists all user tables in the current database
app.MapGet("/db/tables", async (NpgsqlDataSource ds) =>
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name
            """;

        var tables = new List<object>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(new { schema = reader.GetString(0), table = reader.GetString(1) });

        return Results.Ok(new { count = tables.Count, tables });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "DB query failed", statusCode: 500);
    }
});

app.Run();
