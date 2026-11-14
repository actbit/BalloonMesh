using System.Net.WebSockets;
using System.Text.Json;
using BalloonMesh.Server.Auth;
using BalloonMesh.Server.Data;
using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Services;
using SvelteSharp;
using SvelteSharp.AspNetCore;
using SvelteSharp.Jint;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

builder.Services.AddBalloonMeshAuth(builder.Configuration, builder.Environment);
builder.Services.AddAntiforgery();
var identityConnectionString = builder.Configuration.GetConnectionString("BalloonMesh")
    ?? Environment.GetEnvironmentVariable("BALLOONMESH_CONNECTION")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=127.0.0.1;Port=5432;Database=balloonmesh;Username=balloonmesh;Password=balloonmesh;Pooling=true;Maximum Pool Size=20";
builder.Services.AddDbContext<BalloonMeshIdentityDbContext>(options =>
    options.UseNpgsql(identityConnectionString));
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<BalloonMeshIdentityDbContext>()
    .AddSignInManager();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DataStore>();
builder.Services.AddSingleton<IGatewayStateStore>(serviceProvider =>
    serviceProvider.GetRequiredService<DataStore>());
builder.Services.AddSingleton<IGatewayTokenRepository>(serviceProvider =>
    serviceProvider.GetRequiredService<DataStore>());
builder.Services.AddSingleton<FleetService>();
builder.Services.AddSingleton<PositionSolver>();
builder.Services.AddSingleton<NeighborStore>();
builder.Services.AddSingleton<GatewayRegistry>();
builder.Services.AddSingleton<GatewayTokenStore>();
builder.Services.AddSingleton<MonitorNotifier>();
builder.Services.AddSingleton<IMonitorEvents>(serviceProvider => serviceProvider.GetRequiredService<MonitorNotifier>());
builder.Services.AddSingleton<FtmAggregator>();
builder.Services.AddSingleton<MeshReportRouter>();
builder.Services.AddSingleton<LedDispatchService>();
builder.Services.AddSingleton<CommandService>();
builder.Services.AddSingleton<MockProvisioningService>();
builder.Services.AddSingleton<SimulationService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<SimulationService>());
builder.Services.AddHostedService<AggregationWorker>();
builder.Services.AddHostedService<ShowRecoveryWorker>();
builder.Services.AddHostedService<LedDispatchWorker>();

var svelte = builder.Services.AddSvelteSharp(options =>
{
    options.ViewsPath = "Views";
});
svelte.UseJintForCompiler()
      .UseJintForSsr();

var app = builder.Build();

using (var identityScope = app.Services.CreateScope())
{
    var identityDb = identityScope.ServiceProvider
        .GetRequiredService<BalloonMeshIdentityDbContext>();
    await identityDb.Database.MigrateAsync();
    await IdentityBootstrapper.SeedAsync(identityScope.ServiceProvider,
        app.Configuration);
}

var dataStore = app.Services.GetRequiredService<DataStore>();
dataStore.Initialize();
dataStore.ResetRuntimeQueueState();

// Gateways authenticate with a per-gateway token issued from the admin UI.
// There is no shared secret and no anonymous path: with no token issued, the
// gateway endpoints reject every caller.
app.Services.GetRequiredService<GatewayRegistry>().LoadFromStore();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseAuthentication();
app.UseAuthorization();

app.MapSvelteSharpAssets();
app.MapAuthEndpoints();
app.MapHub<MonitorHub>("/hubs/monitor").RequireAuthorization(AuthRoles.Viewer);

app.Map("/ws/gateway", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket upgrade required.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var registry = context.RequestServices.GetRequiredService<GatewayRegistry>();
    var router = context.RequestServices.GetRequiredService<MeshReportRouter>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("GatewayWebSocket");
    await GatewayWebSocket.HandleAsync(socket, context, registry, router, logger,
        context.RequestServices.GetRequiredService<GatewayTokenStore>(),
        context.RequestAborted);
});

app.MapGet("/login", (HttpContext context, IConfiguration configuration,
    IAntiforgery antiforgery) =>
{
    var canView = context.User.Identity?.IsAuthenticated == true &&
                  (context.User.IsInRole(AuthRoles.Viewer) ||
                   context.User.IsInRole(AuthRoles.Operator) ||
                   context.User.IsInRole(AuthRoles.Admin));
    if (canView)
    {
        return Results.Redirect("/");
    }
    var error = context.Request.Query["error"].ToString();
    if (context.User.Identity?.IsAuthenticated == true && string.IsNullOrEmpty(error))
    {
        // Authenticated but without a usable role must not bounce to "/"
        // forever; show the login page with an explanation instead.
        error = "Your account has no access role.";
    }
    var antiforgeryToken = AuthSetup.LocalEnabled(configuration)
        ? antiforgery.GetAndStoreTokens(context).RequestToken
        : null;
    return Svelte.View(
        "Login",
        new LoginPageModel(
            "Sign in to Balloon Mesh",
            AuthSetup.OidcEnabled(configuration),
            AuthSetup.DiscordEnabled(configuration),
            AuthSetup.LocalEnabled(configuration),
            AuthSetup.MockEnabled(configuration),
            string.IsNullOrWhiteSpace(error) ? null : error,
            antiforgeryToken),
        SvelteRenderMode.Client,
        new PageMetadata("Sign in", new Dictionary<string, string>
        {
            ["viewport"] = "width=device-width, initial-scale=1"
        }));
}).AllowAnonymous();

app.MapGet("/", (HttpContext context) =>
{
    var user = AuthenticatedUser.From(context.User);
    return Svelte.View(
        "Home",
        new HomePageModel(
            "Balloon Mesh",
            ServerInfo.Version,
            "ESP32-C3 RGB balloon fleet — live 2D map, FTM positioning and LED scheduling",
            user.Name,
            user.Provider,
            user.Avatar,
            user.CanOperate,
            user.CanAdmin),
        SvelteRenderMode.Client,
        new PageMetadata("Balloon Mesh", new Dictionary<string, string>
        {
            ["viewport"] = "width=device-width, initial-scale=1"
        }));
}).RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/api/me", (HttpContext context) =>
    Results.Ok(AuthenticatedUser.From(context.User)))
    .RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/favicon.ico", () => Results.NoContent()).AllowAnonymous();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    version = ServerInfo.Version,
    time = DateTimeOffset.UtcNow
})).AllowAnonymous();

app.MapGet("/api/state", (FleetService fleet, GatewayRegistry gateways, SimulationService simulation) =>
    Results.Ok(fleet.GetState(gateways.Snapshot(), simulation.Running, ServerInfo.Version)))
    .RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/api/balloons", (FleetService fleet) => Results.Ok(fleet.GetBalloons()))
    .RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/api/balloons/{address}", (string address, FleetService fleet) =>
{
    if (!TryParseAddress(address, out var meshAddress))
    {
        return Results.BadRequest(new { error = "Invalid balloon address." });
    }
    var balloon = fleet.GetBalloon(meshAddress);
    return balloon is null ? Results.NotFound() : Results.Ok(balloon);
}).RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/api/balloons/{address}/history", (string address, int? limit, FleetService fleet) =>
{
    if (!TryParseAddress(address, out var meshAddress))
    {
        return Results.BadRequest(new { error = "Invalid balloon address." });
    }
    return Results.Ok(fleet.GetHistory(meshAddress, limit ?? 100));
}).RequireAuthorization(AuthRoles.Viewer);

app.MapPost("/api/balloons/{address}/initial-position",
    (string address, InitialPositionRequest request, FleetService fleet) =>
    {
        if (!TryParseAddress(address, out var meshAddress))
        {
            return Results.BadRequest(new { error = "Invalid balloon address." });
        }
        try
        {
            return Results.Ok(fleet.SetInitialPosition(meshAddress, request.Lat, request.Lon));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }).RequireAuthorization(AuthRoles.Operator);

app.MapGet("/api/anchors", (FleetService fleet) => Results.Ok(fleet.GetAnchors()))
    .RequireAuthorization(AuthRoles.Viewer);

app.MapPut("/api/anchors/{id:int}", (int id, AnchorUpdateRequest request, FleetService fleet) =>
{
    if (id is < 1 or > 255)
    {
        return Results.BadRequest(new { error = "Anchor id must be 1..255." });
    }
    try
    {
        return Results.Ok(fleet.UpsertAnchor((byte)id, request.Name ?? string.Empty,
            request.Lat, request.Lon, request.AltM, request.TimeValid, request.PositionValid));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AuthRoles.Admin);

app.MapGet("/api/groups", (FleetService fleet) => Results.Ok(fleet.GetGroups()))
    .RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/api/gateways", (GatewayRegistry gateways) => Results.Ok(gateways.Snapshot()))
    .RequireAuthorization(AuthRoles.Viewer);

app.MapGet("/api/show", (DataStore store, int? limit) =>
    Results.Ok(store.GetShowSteps(limit ?? 50))).RequireAuthorization(AuthRoles.Viewer);

app.MapDelete("/api/show/{id:long}", async (long id, CommandService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CancelShowStepAsync(id, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AuthRoles.Operator);

app.MapGet("/api/onboarding/mock", (MockProvisioningService service) =>
    Results.Ok(service.Snapshot())).RequireAuthorization(AuthRoles.Admin);

app.MapPost("/api/onboarding/mock/{id}/provision", (string id, MockProvisioningService service) =>
    service.Provision(id)
        ? Results.Accepted(value: new { id, state = "provisioning" })
        : Results.BadRequest(new { error = "Unknown device or already provisioning/provisioned." }))
    .RequireAuthorization(AuthRoles.Admin);

app.MapPost("/api/onboarding/mock/reset", (MockProvisioningService service) =>
{
    service.Reset();
    return Results.Ok(new { reset = true });
}).RequireAuthorization(AuthRoles.Admin);

app.MapPost("/api/commands/led-set", async (LedSetRequest request, CommandService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ScheduleAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AuthRoles.Operator);

app.MapPost("/api/commands/now", async (LedNowRequest request, CommandService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.SetNowAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AuthRoles.Operator);

app.MapPost("/api/commands/cancel", async (LedCancelRequest request, CommandService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CancelAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AuthRoles.Operator);

app.MapPost("/api/commands/reset", async (LedResetRequest request, CommandService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ResetAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAuthorization(AuthRoles.Operator);

app.MapPost("/api/demo/start", async (SimulationService simulation, CancellationToken cancellationToken) =>
{
    await simulation.SetRunningAsync(true, cancellationToken);
    return Results.Ok(new { running = true });
}).RequireAuthorization(AuthRoles.Admin);

app.MapPost("/api/demo/stop", async (SimulationService simulation, CancellationToken cancellationToken) =>
{
    await simulation.SetRunningAsync(false, cancellationToken);
    return Results.Ok(new { running = false });
}).RequireAuthorization(AuthRoles.Admin);

app.MapGet("/api/gateway-tokens", (GatewayTokenStore tokens) =>
    Results.Ok(tokens.List().Select(GatewayTokenDto.From)))
    .RequireAuthorization(AuthRoles.Admin);

app.MapPost("/api/gateway-tokens",
    (HttpContext http, GatewayTokenRequest request, GatewayTokenStore tokens) =>
    {
        var gatewayId = request.GatewayId?.Trim();
        if (string.IsNullOrWhiteSpace(gatewayId))
        {
            return Results.BadRequest(new { error = "gatewayId is required." });
        }
        if (gatewayId.Length > 64)
        {
            return Results.BadRequest(new { error = "gatewayId is too long." });
        }
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? gatewayId
            : request.Label.Trim();
        if (label.Length > 128)
        {
            return Results.BadRequest(new { error = "label is too long." });
        }
        DateTimeOffset? expiresAt = null;
        if (request.ExpiresInDays is { } days)
        {
            if (days is < 1 or > 3650)
            {
                return Results.BadRequest(
                    new { error = "expiresInDays must be between 1 and 3650." });
            }
            expiresAt = DateTimeOffset.UtcNow.AddDays(days);
        }
        var issuer = AuthenticatedUser.From(http.User).Name;
        var issued = tokens.Issue(gatewayId, label, issuer, expiresAt);
        // The plain token is in this response only; it is not stored.
        return Results.Created($"/api/gateway-tokens/{issued.Record.Id}",
            new IssuedGatewayTokenDto(GatewayTokenDto.From(issued.Record),
                issued.PlainToken));
    }).RequireAuthorization(AuthRoles.Admin);

app.MapDelete("/api/gateway-tokens/{id:long}", (long id, GatewayTokenStore tokens) =>
    tokens.Revoke(id)
        ? Results.Ok(new { revoked = true })
        : Results.NotFound(new { error = "Token not found or already revoked." }))
    .RequireAuthorization(AuthRoles.Admin);

app.MapPost("/api/gateway/{id}/events",
    (HttpContext http, string id, GatewayEventsRequest request, GatewayRegistry registry,
        MeshReportRouter router, GatewayTokenStore tokens) =>
    {
        if (!tokens.AuthenticateRequest(http, id).Authorized)
        {
            return Results.Unauthorized();
        }
        registry.Touch(id, request.Firmware);
        registry.UpdateRuntimeState(id, request.Provisioned, request.SpoolPending);
        if (request.Events is null)
        {
            return Results.BadRequest(new { error = "events is required." });
        }
        var accepted = 0;
        foreach (var message in request.Events)
        {
            accepted += router.Handle(message, id);
        }
        return Results.Accepted(value: new { accepted });
    });

app.MapGet("/api/gateway/{id}/commands",
    async (HttpContext http, string id, int? waitMs, GatewayRegistry registry,
        GatewayTokenStore tokens, CancellationToken cancellationToken) =>
    {
        if (!tokens.AuthenticateRequest(http, id).Authorized)
        {
            return Results.Unauthorized();
        }
        registry.BeginPoll(id);
        // The firmware only polls after provisioning and model binding are
        // complete, so polling is a readiness signal.
        registry.MarkReady(id);
        try
        {
            var wait = Math.Clamp(waitMs ?? 10_000, 0, 30_000);
            var first = await registry.ReadCommandAsync(id, wait, cancellationToken);
            if (first is null)
            {
                return Results.NoContent();
            }

            var commands = new List<GatewayCommandDto> { first.ToDto() };
            while (commands.Count < 32 && registry.TryReadCommand(id, out var next) &&
                   next is not null)
            {
                commands.Add(next.ToDto());
            }
            return Results.Ok(new GatewayCommandsRequest(commands));
        }
        finally
        {
            registry.EndPoll(id);
        }
    });

app.Run();

static bool TryParseAddress(string text, out ushort address)
{
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out address);
    }
    return ushort.TryParse(text, out address);
}

public partial class Program;
