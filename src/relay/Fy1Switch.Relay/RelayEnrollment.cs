using Fy1Switch.Contracts;

namespace Fy1Switch.Relay;

public static class RelayEnrollment
{
    public static void MapRelayEnrollment(this WebApplication app)
    {
        if (app.Services.GetRequiredService<RelaySettings>().Management is null) return;

        app.MapPost("/api/admin/registration-code", (RelayRegistry registry) => Results.Json(registry.GenerateRegistrationCode(), RelayJson.Options))
            .RequireAuthorization().AddEndpointFilter<RelayAntiforgeryFilter>();
        app.MapPost("/api/admin/import-legacy", (RelayRegistry registry, RelaySettings settings) => Results.Json(new { imported = registry.ImportLegacy(settings) }))
            .RequireAuthorization().AddEndpointFilter<RelayAntiforgeryFilter>();
        app.MapGet("/api/admin/devices", (RelayRegistry registry, RelayRouter router) => Results.Json(registry.Devices.Select(device => new
        {
            device.DeviceId, device.DeviceName, Connected = router.IsConnected(device.DeviceId), device.CreatedAt
        }), RelayJson.Options)).RequireAuthorization();
        app.MapDelete("/api/admin/devices/{deviceId:guid}", (Guid deviceId, RelayRegistry registry, RelayRouter router) =>
        {
            if (!registry.Revoke(deviceId)) return Results.NotFound();
            router.Disconnect(deviceId);
            return Results.NoContent();
        }).RequireAuthorization().AddEndpointFilter<RelayAntiforgeryFilter>();

        app.MapPost("/v1/relay/register", async (HttpContext context, RelayRegistry registry) =>
        {
            var request = await RelayApplication.ReadBody<RelayRegistrationRequest>(context);
            if (request is null) return;
            if (!registry.AdmitCodeAttempt()) { await RelayApplication.WriteError(context, 429, "pairing_attempt_limit"); return; }
            if (registry.Register(request) is not { } result) { await RelayApplication.WriteError(context, 401, "registration_rejected"); return; }
            await RelayApplication.WriteResult(context, new(200, result));
        }).RequireRateLimiting("bootstrap");

        app.MapPost("/v1/devices/{deviceId:guid}/pairing-code", async (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry) =>
        {
            if (!RelayApplication.Authenticate(context, settings, registry, deviceId, windows: true)) return;
            var code = await RelayApplication.ReadBody<RelayCode>(context);
            if (code is null) return;
            if (!registry.SetPhoneCode(deviceId, code)) { await RelayApplication.WriteError(context, 409, "pairing_code_unavailable"); return; }
            await RelayApplication.WriteResult(context, new(200, code));
        });
        app.MapDelete("/v1/devices/{deviceId:guid}/pairing-code", (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry) =>
        {
            if (!RelayApplication.Authenticate(context, settings, registry, deviceId, windows: true)) return;
            registry.CancelPhoneCode(deviceId);
            context.Response.StatusCode = 204;
        });
        app.MapPost("/v1/devices/{deviceId:guid}/controllers", async (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry) =>
        {
            if (!RelayApplication.Authenticate(context, settings, registry, deviceId, windows: true)) return;
            var request = await RelayApplication.ReadBody<RelayAuthorizeRequest>(context);
            if (request is null) return;
            if (registry.ControllerFor(deviceId, request.ClientId) is not { } grant) { await RelayApplication.WriteError(context, 403, "controller_rejected"); return; }
            await RelayApplication.WriteResult(context, new(200, grant));
        });
        app.MapPost("/v1/devices/{deviceId:guid}/controllers/synchronize", async (Guid deviceId, HttpContext context, RelaySettings settings, RelayRegistry registry) =>
        {
            if (!RelayApplication.Authenticate(context, settings, registry, deviceId, windows: true)) return;
            var request = await RelayApplication.ReadBody<RelayClientsRequest>(context);
            if (request is null) return;
            if (!registry.SynchronizeControllers(deviceId, request.ClientIds)) { await RelayApplication.WriteError(context, 400, "invalid_clients"); return; }
            context.Response.StatusCode = 204;
        });

        app.MapPost("/v1/relay/pair", async (HttpContext context, RelayRegistry registry, RelayRouter router) =>
        {
            var request = await RelayApplication.ReadBody<RelayJoinRequest>(context);
            if (request is null) return;
            if (!registry.AdmitCodeAttempt()) { await RelayApplication.WriteError(context, 429, "pairing_attempt_limit"); return; }
            if (!RelayRegistry.ValidName(request.ClientName) || registry.TakePhoneCode(request.Code) is not { } deviceId)
            { await RelayApplication.WriteError(context, 401, "pairing_rejected"); return; }
            var result = await router.ForwardAsync(deviceId, new(Guid.NewGuid(), "relay-pair", null, Pair: new(request.Code, request.ClientName)), context.RequestAborted);
            await CompleteBootstrap(context, registry, deviceId, result);
        }).RequireRateLimiting("bootstrap");
        app.MapPost("/v1/devices/{deviceId:guid}/authorize", async (Guid deviceId, HttpContext context, RelayRegistry registry, RelayRouter router) =>
        {
            var request = await RelayApplication.ReadBody<RelayAuthorizeRequest>(context);
            if (request is null) return;
            var tokens = context.Request.Headers["X-Device-Token"];
            if (request.ClientId == Guid.Empty || tokens.Count != 1 || tokens[0] is not { Length: 64 } token || !registry.Active(deviceId))
            { await RelayApplication.WriteError(context, 401, "authorization_rejected"); return; }
            var result = await router.ForwardAsync(deviceId, new(Guid.NewGuid(), "authorize", token, Authorize: request), context.RequestAborted);
            await CompleteBootstrap(context, registry, deviceId, result);
        }).RequireRateLimiting("bootstrap");
    }

    private static async Task CompleteBootstrap(HttpContext context, RelayRegistry registry, Guid deviceId, RelayResult result)
    {
        if (result.StatusCode is >= 200 and < 300 && result.Body is RelayBootstrapReply bootstrap)
        {
            if (registry.ControllerFor(deviceId, bootstrap.ClientId) is not { } grant)
            { await RelayApplication.WriteError(context, 403, "controller_rejected"); return; }
            // Recheck registration after the remote validation, since revocation can race it.
            if (!registry.Active(deviceId)) { await RelayApplication.WriteError(context, 401, "authorization_rejected"); return; }
            await RelayApplication.WriteResult(context, new(200, new RelayPairingReply(deviceId, bootstrap.ClientId, grant.ControllerToken,
                bootstrap.DeviceToken, bootstrap.Endpoint, bootstrap.CertificatePin, bootstrap.TailscaleEndpoint, bootstrap.DeviceName)));
        }
        else await RelayApplication.WriteResult(context, result);
    }
}
