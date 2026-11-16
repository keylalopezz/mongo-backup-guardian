using Guardian;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StoreHolder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StoreHolder>());
var app = builder.Build();

var cfg = app.Configuration;
var version = cfg["APP_VERSION"] ?? "dev";
var tokenCi = cfg["GUARDIAN_TOKEN"];
var json = new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson };

IResult J(BsonValue v) => Results.Text(v.ToJson(json), "application/json; charset=utf-8");
IResult NoListo() => Results.Json(new { estado = "conectando", detalle = "La app aún está conectando con MongoDB" }, statusCode: 503);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", async (StoreHolder h) =>
{
    if (h.Store is null) return NoListo();
    return Results.Json(new { estado = "ok", latenciaMs = await h.Store.PingAsync() });
});

app.MapGet("/api/info", async (StoreHolder h) =>
{
    if (h.Store is null) return Results.Json(new { app = "mongo-backup-guardian", version, mongo = (string?)null });
    return Results.Json(new
    {
        app = "mongo-backup-guardian",
        version,
        mongo = await h.Store.VersionServidorAsync(),
        baseDatos = h.Store.Db.DatabaseNamespace.DatabaseName,
        latenciaMs = await h.Store.PingAsync(),
    });
});

app.MapGet("/api/estado", async (StoreHolder h) =>
{
    if (h.Store is not { } s) return NoListo();
    var huellas = await s.HuellasAsync();
    var ultimo = await s.SnapshotAsync("ultimo");
    var cols = new BsonArray();
    foreach (var x in huellas)
    {
        var enSnap = ultimo?["colecciones"].AsBsonArray.Select(c => c.AsBsonDocument).FirstOrDefault(c => c["coleccion"] == x.Nombre);
        cols.Add(new BsonDocument
        {
            ["coleccion"] = x.Nombre,
            ["documentos"] = x.Documentos,
            ["sha256"] = x.Sha256,
            ["coincideConUltimo"] = enSnap is null ? BsonNull.Value : enSnap["sha256"] == x.Sha256,
        });
    }
    return J(new BsonDocument
    {
        ["colecciones"] = cols,
        ["ultimoSnapshot"] = ultimo is null ? BsonNull.Value : ultimo["_id"],
        ["prestamosPorEstado"] = new BsonArray(await s.PrestamosPorEstadoAsync()),
    });
});

app.MapGet("/api/prestamos", async (StoreHolder h, int? limite) =>
    h.Store is { } s ? J(new BsonArray(await s.PrestamosRecientesAsync(Math.Clamp(limite ?? 12, 1, 100)))) : NoListo());

app.MapPost("/api/actividad", async (StoreHolder h, int? n) =>
{
    if (h.Store is not { } s) return NoListo();
    var total = await s.ActividadAsync(Math.Clamp(n ?? 5, 1, 50));
    return Results.Json(new { prestamosNuevos = total });
});

app.MapGet("/api/snapshots", async (StoreHolder h) =>
    h.Store is { } s ? J(new BsonArray(await s.SnapshotsAsync())) : NoListo());

app.MapPost("/api/snapshots", async (StoreHolder h, string? origen) =>
{
    if (h.Store is not { } s) return NoListo();
    var snap = await s.CrearSnapshotAsync(string.IsNullOrWhiteSpace(origen) ? "panel" : origen[..Math.Min(origen.Length, 40)]);
    return Results.Text(snap.ToJson(json), "application/json; charset=utf-8", statusCode: 201);
});

app.MapPost("/api/snapshots/{id}/restaurar", async (StoreHolder h, string id) =>
{
    if (h.Store is not { } s) return NoListo();
    var snap = await s.SnapshotAsync(id);
    if (snap is null) return Results.NotFound(new { error = "No existe ese punto de restauración" });
    return J(await s.RestaurarAsync(snap));
});

app.MapGet("/api/desastres", () => Results.Json(GuardianStore.Desastres));

app.MapPost("/api/desastre/{tipo}", async (StoreHolder h, string tipo) =>
{
    if (h.Store is not { } s) return NoListo();
    if (!GuardianStore.Desastres.ContainsKey(tipo)) return Results.BadRequest(new { error = "Tipo de desastre desconocido" });
    return J(await s.DesastreAsync(tipo));
});

app.MapPost("/api/semilla", async (StoreHolder h) =>
{
    if (h.Store is not { } s) return NoListo();
    await s.SembrarAsync();
    return Results.Json(new { ok = true });
});

app.MapGet("/api/respaldos", async (StoreHolder h) =>
    h.Store is { } s ? J(new BsonArray(await s.RespaldosAsync())) : NoListo());

app.MapPost("/api/respaldos", async (StoreHolder h, HttpRequest req) =>
{
    if (string.IsNullOrEmpty(tokenCi) || req.Headers["X-Guardian-Token"] != tokenCi) return Results.Unauthorized();
    if (h.Store is not { } s) return NoListo();
    using var lector = new StreamReader(req.Body);
    BsonDocument doc;
    try { doc = BsonDocument.Parse(await lector.ReadToEndAsync()); }
    catch (Exception) { return Results.BadRequest(new { error = "JSON inválido" }); }
    doc.Remove("_id");
    await s.RegistrarRespaldoAsync(doc);
    return Results.Created("/api/respaldos", new { ok = true });
});

app.MapGet("/api/eventos", async (StoreHolder h) =>
    h.Store is { } s ? J(new BsonArray(await s.EventosAsync())) : NoListo());

app.Run();

/// <summary>Conecta con MongoDB en segundo plano (con reintentos) para que la app arranque aunque la base tarde.</summary>
public class StoreHolder(IConfiguration cfg, ILogger<StoreHolder> log) : BackgroundService
{
    public GuardianStore? Store { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var uri = cfg["MONGODB_URI"] ?? "mongodb://localhost:27017";
        var db = cfg["MONGODB_DB"] ?? "biblioteca";
        var meta = cfg["MONGODB_META_DB"] ?? db + "_guardian";
        for (var intento = 1; !ct.IsCancellationRequested; intento++)
        {
            try
            {
                var settings = MongoClientSettings.FromConnectionString(uri);
                settings.ApplicationName = "mongo-backup-guardian";
                settings.ServerSelectionTimeout = TimeSpan.FromSeconds(15);
                var store = new GuardianStore(new MongoClient(settings), db, meta);
                await store.InicializarAsync();
                Store = store;
                log.LogInformation("Conectado a MongoDB (base {Db}) en el intento {Intento}", db, intento);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("MongoDB no disponible (intento {Intento}): {Error}", intento, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * intento)), ct);
            }
        }
    }
}

public partial class Program;
