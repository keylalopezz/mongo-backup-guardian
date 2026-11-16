using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Guardian;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Guardian.Tests;

/// <summary>Levanta la app contra un MongoDB real con una base aislada por prueba.</summary>
public sealed class AppGuardian : IAsyncDisposable
{
    public static readonly string Uri = Environment.GetEnvironmentVariable("MONGODB_URI") ?? "mongodb://localhost:27017";
    public const string Token = "token-de-prueba";
    public string Db { get; } = "t_" + Guid.NewGuid().ToString("N")[..12];
    readonly WebApplicationFactory<Program> _factory;
    public HttpClient Http { get; }

    AppGuardian()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("MONGODB_URI", Uri)
            .UseSetting("MONGODB_DB", Db)
            .UseSetting("GUARDIAN_TOKEN", Token)
            .UseSetting("APP_VERSION", "sha-de-prueba"));
        Http = _factory.CreateClient();
    }

    public static async Task<AppGuardian> IniciarAsync()
    {
        var app = new AppGuardian();
        for (var i = 0; i < 60; i++)
        {
            if ((await app.Http.GetAsync("/health")).IsSuccessStatusCode) return app;
            await Task.Delay(250);
        }
        throw new TimeoutException("La app no conectó con MongoDB");
    }

    public async Task<JsonElement> JsonAsync(HttpMethod metodo, string url)
    {
        var r = await Http.SendAsync(new HttpRequestMessage(metodo, url));
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<Dictionary<string, (long Docs, string Sha)>> EstadoAsync()
    {
        var e = await JsonAsync(HttpMethod.Get, "/api/estado");
        return e.GetProperty("colecciones").EnumerateArray().ToDictionary(
            c => c.GetProperty("coleccion").GetString()!,
            c => (c.GetProperty("documentos").GetInt64(), c.GetProperty("sha256").GetString()!));
    }

    public async ValueTask DisposeAsync()
    {
        var client = new MongoClient(Uri);
        await client.DropDatabaseAsync(Db);
        await client.DropDatabaseAsync(Db + "_guardian");
        Http.Dispose();
        await _factory.DisposeAsync();
    }
}

public class ApiTests
{
    [Fact]
    public async Task Health_e_info_responden_con_la_version()
    {
        await using var app = await AppGuardian.IniciarAsync();
        var info = await app.JsonAsync(HttpMethod.Get, "/api/info");
        Assert.Equal("sha-de-prueba", info.GetProperty("version").GetString());
        Assert.StartsWith("8.", info.GetProperty("mongo").GetString());
    }

    [Fact]
    public async Task La_semilla_carga_las_tres_colecciones()
    {
        await using var app = await AppGuardian.IniciarAsync();
        var estado = await app.EstadoAsync();
        Assert.Equal(Datos.TotalLibros, estado["libros"].Docs);
        Assert.Equal(Datos.TotalSocios, estado["socios"].Docs);
        Assert.Equal(Datos.TotalPrestamos, estado["prestamos"].Docs);
    }

    [Fact]
    public async Task Borrado_masivo_se_recupera_con_restauracion_verificada()
    {
        await using var app = await AppGuardian.IniciarAsync();
        var antes = await app.EstadoAsync();
        await app.JsonAsync(HttpMethod.Post, "/api/snapshots?origen=prueba");

        var d = await app.JsonAsync(HttpMethod.Post, "/api/desastre/borrado-masivo");
        Assert.Equal(Datos.TotalPrestamos, d.GetProperty("afectados").GetInt64());
        Assert.Equal(0, (await app.EstadoAsync())["prestamos"].Docs);

        var r = await app.JsonAsync(HttpMethod.Post, "/api/snapshots/ultimo/restaurar");
        Assert.True(r.GetProperty("verificado").GetBoolean());
        Assert.Equal(antes, await app.EstadoAsync());
    }

    [Fact]
    public async Task Corrupcion_silenciosa_mantiene_el_conteo_pero_cambia_la_huella()
    {
        await using var app = await AppGuardian.IniciarAsync();
        await app.JsonAsync(HttpMethod.Post, "/api/snapshots");
        var antes = (await app.EstadoAsync())["socios"];

        await app.JsonAsync(HttpMethod.Post, "/api/desastre/corrupcion");
        var despues = (await app.EstadoAsync())["socios"];
        Assert.Equal(antes.Docs, despues.Docs);
        Assert.NotEqual(antes.Sha, despues.Sha);

        var r = await app.JsonAsync(HttpMethod.Post, "/api/snapshots/ultimo/restaurar");
        Assert.True(r.GetProperty("verificado").GetBoolean());
        Assert.Equal(antes, (await app.EstadoAsync())["socios"]);
    }

    [Fact]
    public async Task Drop_de_coleccion_se_recupera_con_sus_indices()
    {
        await using var app = await AppGuardian.IniciarAsync();
        await app.JsonAsync(HttpMethod.Post, "/api/snapshots");
        await app.JsonAsync(HttpMethod.Post, "/api/desastre/drop-coleccion");
        Assert.Equal(0, (await app.EstadoAsync())["libros"].Docs);

        var r = await app.JsonAsync(HttpMethod.Post, "/api/snapshots/ultimo/restaurar");
        Assert.True(r.GetProperty("verificado").GetBoolean());
        Assert.Equal(Datos.TotalLibros, (await app.EstadoAsync())["libros"].Docs);

        var indices = await new MongoClient(AppGuardian.Uri).GetDatabase(app.Db).GetCollection<BsonDocument>("libros").Indexes.ListAsync();
        Assert.Contains((await indices.ToListAsync()), i => i["name"] == "categoria_1");
    }

    [Fact]
    public async Task Restaurar_un_punto_anterior_descarta_cambios_posteriores()
    {
        await using var app = await AppGuardian.IniciarAsync();
        var snap = await app.JsonAsync(HttpMethod.Post, "/api/snapshots");
        var id = snap.GetProperty("_id").GetString();
        await app.JsonAsync(HttpMethod.Post, "/api/actividad?n=7");
        Assert.Equal(Datos.TotalPrestamos + 7, (await app.EstadoAsync())["prestamos"].Docs);

        var r = await app.JsonAsync(HttpMethod.Post, $"/api/snapshots/{id}/restaurar");
        Assert.True(r.GetProperty("verificado").GetBoolean());
        Assert.Equal(Datos.TotalPrestamos, (await app.EstadoAsync())["prestamos"].Docs);
    }

    [Fact]
    public async Task Estado_indica_si_los_datos_difieren_del_ultimo_punto()
    {
        await using var app = await AppGuardian.IniciarAsync();
        await app.JsonAsync(HttpMethod.Post, "/api/snapshots");
        await app.JsonAsync(HttpMethod.Post, "/api/actividad?n=1");
        var e = await app.JsonAsync(HttpMethod.Get, "/api/estado");
        var cols = e.GetProperty("colecciones").EnumerateArray().ToDictionary(c => c.GetProperty("coleccion").GetString()!, c => c.GetProperty("coincideConUltimo").GetBoolean());
        Assert.True(cols["libros"]);
        Assert.False(cols["prestamos"]);
    }

    [Fact]
    public async Task Registrar_respaldo_exige_token()
    {
        await using var app = await AppGuardian.IniciarAsync();
        var cuerpo = """{"tipo":"mongodump diario","documentos":250,"verificado":true,"run":"42"}""";
        var sinToken = await app.Http.PostAsync("/api/respaldos", new StringContent(cuerpo, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, sinToken.StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/respaldos") { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Guardian-Token", AppGuardian.Token);
        Assert.Equal(HttpStatusCode.Created, (await app.Http.SendAsync(req)).StatusCode);

        var lista = await app.JsonAsync(HttpMethod.Get, "/api/respaldos");
        Assert.Equal("42", lista[0].GetProperty("run").GetString());
    }

    [Fact]
    public async Task Desastre_desconocido_y_punto_inexistente_devuelven_error()
    {
        await using var app = await AppGuardian.IniciarAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Http.PostAsync("/api/desastre/meteorito", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.PostAsync("/api/snapshots/no-existe/restaurar", null)).StatusCode);
    }
}

public class FormatoTests
{
    [Fact]
    public async Task Bson_gzip_ida_y_vuelta_conserva_documentos_y_hash()
    {
        var docs = Datos.Libros();
        using var ms = new MemoryStream();
        var (n, sha) = await GuardianStore.EscribirBsonAsync(new CursorEnMemoria(docs), ms);
        Assert.Equal(docs.Count, n);
        ms.Position = 0;
        var leidos = new List<BsonDocument>();
        await foreach (var d in GuardianStore.LeerBsonAsync(ms)) leidos.Add(d);
        Assert.Equal(docs, leidos);
        Assert.Equal(64, sha.Length);
    }

    [Fact]
    public void Los_datos_de_ejemplo_son_deterministas()
    {
        Assert.Equal(Datos.Socios(), Datos.Socios());
        Assert.Equal(Datos.TotalLibros, Datos.Libros().Count);
    }

    sealed class CursorEnMemoria(List<BsonDocument> docs) : IAsyncCursor<BsonDocument>
    {
        bool _leido;
        public IEnumerable<BsonDocument> Current => docs;
        public bool MoveNext(CancellationToken ct = default) { var r = !_leido; _leido = true; return r; }
        public Task<bool> MoveNextAsync(CancellationToken ct = default) => Task.FromResult(MoveNext(ct));
        public void Dispose() { }
    }
}
