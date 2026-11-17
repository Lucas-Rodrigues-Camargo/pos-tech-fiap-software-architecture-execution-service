using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using Amazon;
using Amazon.SQS;
using Amazon.DynamoDBv2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Platform;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("oficina-execution"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddSource("Oficina.Workflow").AddOtlpExporter());
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key required (32+ characters).");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32) throw new InvalidOperationException("JWT key too short.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "OficinaMecanica",
        ValidAudiences = ["OficinaMecanica.Admin", "OficinaMecanica.Customer"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)), RoleClaimType = "role"
    };
});
builder.Services.AddAuthorization(o => {
    o.AddPolicy("Employee", p => p.RequireRole("Admin", "User"));
    o.AddPolicy("ClientOrEmployee", p => p.RequireRole("Customer", "Admin", "User"));
});
var endpoint = builder.Configuration["AWS:Endpoint"];
var region = builder.Configuration["AWS:Region"] ?? "us-east-1";
builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient(
    string.IsNullOrWhiteSpace(endpoint) ? new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region) }
        : new AmazonSQSConfig { ServiceURL = endpoint, AuthenticationRegion = region }));
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient(
    string.IsNullOrWhiteSpace(endpoint) ? new AmazonDynamoDBConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region) }
        : new AmazonDynamoDBConfig { ServiceURL = endpoint, AuthenticationRegion = region }));
builder.Services.AddSingleton<IStateStore>(p => new DynamoStore(p.GetRequiredService<IAmazonDynamoDB>(),
    builder.Configuration["DynamoDB:Table"] ?? "oficina-execution"));
builder.Services.AddSingleton<IWorkflow, ServiceWorkflow>();
builder.Services.AddSingleton<Processor>();

if (builder.Configuration["Messaging:Enabled"] != "false") builder.Services.AddHostedService<QueueWorker>();
var app = builder.Build();
app.Use(async (context, next) => {
    var incoming = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    var correlation = incoming != null && incoming.Length <= 64 && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        ? incoming : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlation;
    context.Response.Headers["X-Correlation-ID"] = correlation;
    using var scope = app.Logger.BeginScope(new Dictionary<string,object> { ["correlation_id"] = correlation });
    try { await next(); }
    catch (ArgumentException ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context); }
    catch (InvalidOperationException ex) { await Results.Problem(ex.Message, statusCode: 409).ExecuteAsync(context); }
    catch (ConflictException) { await Results.Problem("Concurrent modification. Retry with same Idempotency-Key.", statusCode: 409).ExecuteAsync(context); }
});
app.UseAuthentication(); app.UseAuthorization();
app.MapOpenApi().RequireAuthorization("Employee");
app.MapGet("/health", () => Results.Ok(new { service = "execution" }));
app.MapGet("/ready", async (IStateStore store, CancellationToken ct) => {
    await store.Get(Guid.Empty, ct); return Results.Ok();
});
app.MapGet("/api/jobs/{id:guid}", async (Guid id, IStateStore store, ClaimsPrincipal user, CancellationToken ct) => {
    var state = await store.Get(id, ct);
    if (state == null) return Results.NotFound();
    if (!Access.Owns(user, state.Data.Cpf)) return Results.Forbid();
    return Results.Ok(new { state.Id, state.Status, state.Data, state.History });
}).RequireAuthorization("Employee");

app.MapPost("/api/jobs/{id:guid}/diagnosis", async (Guid id, Diagnosis request, HttpContext context, Processor processor, CancellationToken ct) => {
    var state = await processor.Handle(Access.Command(context, id, "CompleteDiagnosis",
        new Payload(Diagnosis: request.Description, Amount: request.Amount)), ct);
    return Results.Ok(new { state.Id, state.Status });
}).RequireAuthorization("Employee");
app.MapGet("/api/jobs", async (Guid? after, IAmazonDynamoDB db, IConfiguration config, CancellationToken ct) => {
    var page = await db.ScanAsync(new Amazon.DynamoDBv2.Model.ScanRequest {
        TableName = config["DynamoDB:Table"] ?? "oficina-execution", Limit = 50, ConsistentRead = true,
        ExclusiveStartKey = after == null ? null : new() { ["id"] = new() { S = after.Value.ToString() } }
    }, ct);
    var jobs = (page.Items ?? []).Select(item => System.Text.Json.JsonSerializer.Deserialize<State>(
        item["document"].S, JsonDefaults.Options)!).Where(job => job.Status is not ("Finished" or "Canceled"))
        .OrderBy(job => job.History.FirstOrDefault()?.At ?? job.UpdatedAt)
        .Select(job => new { job.Id, job.Status, job.Data.Description, job.Data.Diagnosis });
    return Results.Ok(new { items = jobs, nextCursor = page.LastEvaluatedKey?.GetValueOrDefault("id")?.S });
}).RequireAuthorization("Employee");
app.MapPost("/api/jobs/{id:guid}/finish", async (Guid id, HttpContext context, Processor processor, CancellationToken ct) => {
    var state = await processor.Handle(Access.Command(context, id, "FinishRepair", new()), ct);
    return Results.Ok(new { state.Id, state.Status });
}).RequireAuthorization("Employee");


app.Run();
public partial class Program;
public static class Access
{
    public static bool Owns(ClaimsPrincipal user, string cpf) => user.IsInRole("Admin") || user.IsInRole("User")
        || (user.IsInRole("Customer") && !string.IsNullOrEmpty(cpf) && user.FindFirst("cpf")?.Value == cpf);
    public static Guid CommandId(HttpContext context)
        => Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var id) && id != Guid.Empty
            ? id : throw new ArgumentException("Idempotency-Key must be a nonempty UUID.");
    public static Message Command(HttpContext context, Guid orderId, string type, Payload data)
        => new(CommandId(context), orderId, type, "execution", context.TraceIdentifier, data);
}

public sealed record Diagnosis(string Description, decimal Amount);

