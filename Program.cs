using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
    NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Postgres")!));

builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var accessKey = Environment.GetEnvironmentVariable("R2_KEY") ?? builder.Configuration["R2:AccessKeyId"];
    var secretKey = Environment.GetEnvironmentVariable("R2_SECRET") ?? builder.Configuration["R2:SecretAccessKey"];
    var endpoint  = Environment.GetEnvironmentVariable("R2_ENDPOINT")  ?? builder.Configuration["R2:Endpoint"];
    var creds  = new BasicAWSCredentials(accessKey, secretKey);
    var config = new AmazonS3Config
    {
        ServiceURL = endpoint,
        ForcePathStyle = true,
        AuthenticationRegion = "auto"
    };
    return new AmazonS3Client(creds, config);
});

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()));

// Bind to Railway's PORT env var (falls back to 5000 locally)
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.UseCors();

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

// GET /debug/r2 — confirm env vars are loaded
app.MapGet("/debug/r2", () =>
{
    // Only expose key NAMES (not values) so we can see what Railway is injecting
    var r2Keys = Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .Where(e => e.Key.ToString()!.StartsWith("R2", StringComparison.OrdinalIgnoreCase))
        .Select(e => e.Key.ToString())
        .OrderBy(k => k)
        .ToList();

    return Results.Ok(new { r2Keys });
});

// GET /storage/presign?key=filename.jpg — returns a short-lived presigned URL
app.MapGet("/storage/presign", (IAmazonS3 s3, IConfiguration config, string key) =>
{
    try
    {
        var bucket = Environment.GetEnvironmentVariable("R2_BUCKET") ?? config["R2:Bucket"]!;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Expires = DateTime.UtcNow.AddMinutes(15),
            Verb = HttpVerb.GET
        };
        var url = s3.GetPreSignedURL(request);
        return Results.Ok(new { url });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, title: "Presign failed", statusCode: 500);
    }
});

app.Run();
