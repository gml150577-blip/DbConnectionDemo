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
    var r2 = builder.Configuration.GetSection("R2");
    var creds = new BasicAWSCredentials(r2["AccessKeyId"], r2["SecretAccessKey"]);
    var config = new AmazonS3Config
    {
        ServiceURL = r2["Endpoint"],
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

// GET /storage/presign?key=filename.jpg — returns a short-lived presigned URL
app.MapGet("/storage/presign", (IAmazonS3 s3, IConfiguration config, string key) =>
{
    try
    {
        var bucket = config["R2:Bucket"]!;
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
