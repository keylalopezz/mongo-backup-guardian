using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace Guardian;

public record HuellaColeccion(string Nombre, long Documentos, string Sha256);

/// <summary>
/// Datos de la biblioteca + puntos de restauración (respaldo lógico BSON comprimido en GridFS),
/// simulación de desastres, restauración verificada y registro de respaldos externos (CI).
/// </summary>
public class GuardianStore
{
    public static readonly string[] Colecciones = ["libros", "socios", "prestamos"];
    const int MaxSnapshots = 10;

    readonly IMongoClient _client;
    public IMongoDatabase Db { get; }
    public IMongoDatabase Meta { get; }
    readonly GridFSBucket _bucket;
    readonly IMongoCollection<BsonDocument> _snapshots, _respaldos, _eventos;

    public GuardianStore(IMongoClient client, string dbName, string metaName)
    {
        _client = client;
        Db = client.GetDatabase(dbName);
        Meta = client.GetDatabase(metaName);
        _bucket = new GridFSBucket(Meta, new GridFSBucketOptions { BucketName = "snapshots" });
        _snapshots = Meta.GetCollection<BsonDocument>("snapshots");
        _respaldos = Meta.GetCollection<BsonDocument>("respaldos_ci");
        _eventos = Meta.GetCollection<BsonDocument>("eventos");
    }

    IMongoCollection<BsonDocument> Col(string nombre) => Db.GetCollection<BsonDocument>(nombre);

    public async Task<string> VersionServidorAsync()
    {
        var r = await Db.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));
        return r["version"].AsString;
    }

    public async Task<double> PingAsync()
    {
        var sw = Stopwatch.StartNew();
        await Db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
        return Math.Round(sw.Elapsed.TotalMilliseconds, 1);
    }

    public async Task AsegurarIndicesAsync()
    {
        await Col("prestamos").Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("socioId").Descending("fechaPrestamo")),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("estado")),
        ]);
        await Col("libros").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("categoria")));
        await _eventos.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Descending("fecha")));
    }

    public async Task InicializarAsync()
    {
        await AsegurarIndicesAsync();
        if (await Col("libros").EstimatedDocumentCountAsync() == 0 && await Col("prestamos").EstimatedDocumentCountAsync() == 0)
            await SembrarAsync();
    }

    public async Task SembrarAsync()
    {
        var ahora = DateTime.UtcNow;
        foreach (var (nombre, docs) in new[] { ("libros", Datos.Libros()), ("socios", Datos.Socios()), ("prestamos", Datos.Prestamos(ahora)) })
        {
            await Col(nombre).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await Col(nombre).InsertManyAsync(docs);
        }
        await AsegurarIndicesAsync();
        await EventoAsync("semilla", "Datos de ejemplo cargados", null);
    }

    // ---------- Huella (conteo + SHA-256 del contenido BSON ordenado por _id) ----------

    public async Task<HuellaColeccion> HuellaAsync(string nombre)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long n = 0;
        var opciones = new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending("_id"), BatchSize = 1000 };
        using var cursor = await Col(nombre).FindAsync(FilterDefinition<BsonDocument>.Empty, opciones);
        while (await cursor.MoveNextAsync())
            foreach (var doc in cursor.Current) { sha.AppendData(doc.ToBson()); n++; }
        return new HuellaColeccion(nombre, n, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    public async Task<List<HuellaColeccion>> HuellasAsync()
    {
        var r = new List<HuellaColeccion>();
        foreach (var c in Colecciones) r.Add(await HuellaAsync(c));
        return r;
    }

    // ---------- Formato del respaldo: documentos BSON concatenados (como mongodump) + gzip ----------

    public static async Task<(long Docs, string Sha256)> EscribirBsonAsync(IAsyncCursor<BsonDocument> cursor, Stream destino)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long n = 0;
        await using (var gz = new GZipStream(destino, CompressionLevel.Optimal, leaveOpen: true))
        {
            while (await cursor.MoveNextAsync())
                foreach (var doc in cursor.Current)
                {
                    var bytes = doc.ToBson();
                    sha.AppendData(bytes);
                    await gz.WriteAsync(bytes);
                    n++;
                }
        }
        return (n, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    public static async IAsyncEnumerable<BsonDocument> LeerBsonAsync(Stream origen)
    {
        await using var gz = new GZipStream(origen, CompressionMode.Decompress);
        var largo = new byte[4];
        while (true)
        {
            var leidos = await gz.ReadAtLeastAsync(largo, 4, throwOnEndOfStream: false);
            if (leidos == 0) yield break;
            if (leidos < 4) throw new InvalidDataException("Respaldo truncado");
            var total = BitConverter.ToInt32(largo, 0);
            var buffer = new byte[total];
            largo.CopyTo(buffer, 0);
            await gz.ReadExactlyAsync(buffer, 4, total - 4);
            yield return BsonSerializer.Deserialize<BsonDocument>(buffer);
        }
    }

    // ---------- Puntos de restauración ----------

    public async Task<BsonDocument> CrearSnapshotAsync(string origen)
    {
        var sw = Stopwatch.StartNew();
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var detalle = new BsonArray();
        long comprimido = 0;
        foreach (var c in Colecciones)
        {
            var opciones = new FindOptions<BsonDocument> { Sort = Builders<BsonDocument>.Sort.Ascending("_id"), BatchSize = 1000 };
            using var cursor = await Col(c).FindAsync(FilterDefinition<BsonDocument>.Empty, opciones);
            using var ms = new MemoryStream();
            var (docs, hash) = await EscribirBsonAsync(cursor, ms);
            ms.Position = 0;
            var fileId = await _bucket.UploadFromStreamAsync($"{id}/{c}.bson.gz", ms,
                new GridFSUploadOptions { Metadata = new BsonDocument { ["snapshot"] = id, ["coleccion"] = c } });
            comprimido += ms.Length;
            detalle.Add(new BsonDocument { ["coleccion"] = c, ["documentos"] = docs, ["sha256"] = hash, ["bytes"] = ms.Length, ["archivo"] = fileId });
        }
        var snap = new BsonDocument
        {
            ["_id"] = id,
            ["fecha"] = DateTime.UtcNow,
            ["origen"] = origen,
            ["colecciones"] = detalle,
            ["bytesComprimidos"] = comprimido,
            ["duracionMs"] = sw.ElapsedMilliseconds,
        };
        await _snapshots.InsertOneAsync(snap);
        await PodarSnapshotsAsync();
        await EventoAsync("snapshot", $"Punto de restauración {id} ({comprimido / 1024.0:0.0} KB)", id);
        return snap;
    }

    async Task PodarSnapshotsAsync()
    {
        var viejos = await _snapshots.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("fecha")).Skip(MaxSnapshots).ToListAsync();
        foreach (var s in viejos)
        {
            foreach (var c in s["colecciones"].AsBsonArray)
                try { await _bucket.DeleteAsync(c["archivo"].AsObjectId); } catch (GridFSFileNotFoundException) { }
            await _snapshots.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", s["_id"]));
        }
    }

    public Task<List<BsonDocument>> SnapshotsAsync(int limite = 10) =>
        _snapshots.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Descending("fecha")).Limit(limite).ToListAsync();

    public async Task<BsonDocument?> SnapshotAsync(string id) =>
        id == "ultimo"
            ? (await SnapshotsAsync(1)).FirstOrDefault()
            : await _snapshots.Find(Builders<BsonDocument>.Filter.Eq("_id", id)).FirstOrDefaultAsync();

    public async Task<BsonDocument> RestaurarAsync(BsonDocument snap)
    {
        var sw = Stopwatch.StartNew();
        var resultado = new BsonArray();
        var todoOk = true;
        foreach (var c in snap["colecciones"].AsBsonArray.Select(x => x.AsBsonDocument))
        {
            var nombre = c["coleccion"].AsString;
            var t = Stopwatch.StartNew();
            await Col(nombre).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            await using (var stream = await _bucket.OpenDownloadStreamAsync(c["archivo"].AsObjectId))
            {
                var lote = new List<BsonDocument>(500);
                await foreach (var doc in LeerBsonAsync(stream))
                {
                    lote.Add(doc);
                    if (lote.Count == 500) { await Col(nombre).InsertManyAsync(lote); lote.Clear(); }
                }
                if (lote.Count > 0) await Col(nombre).InsertManyAsync(lote);
            }
            var huella = await HuellaAsync(nombre);
            var ok = huella.Documentos == c["documentos"].ToInt64() && huella.Sha256 == c["sha256"].AsString;
            todoOk &= ok;
            resultado.Add(new BsonDocument
            {
                ["coleccion"] = nombre,
                ["esperados"] = c["documentos"],
                ["restaurados"] = huella.Documentos,
                ["sha256"] = huella.Sha256,
                ["verificado"] = ok,
                ["ms"] = t.ElapsedMilliseconds,
            });
        }
        await AsegurarIndicesAsync();
        var informe = new BsonDocument
        {
            ["snapshot"] = snap["_id"],
            ["verificado"] = todoOk,
            ["duracionMs"] = sw.ElapsedMilliseconds,
            ["colecciones"] = resultado,
        };
        await EventoAsync("restauracion", $"Restaurado desde {snap["_id"]} en {sw.ElapsedMilliseconds} ms — {(todoOk ? "verificado" : "NO coincide")}", snap["_id"].AsString);
        return informe;
    }

    // ---------- Desastres simulados ----------

    public static readonly Dictionary<string, string> Desastres = new()
    {
        ["borrado-masivo"] = "deleteMany({}) en prestamos (borrado accidental)",
        ["drop-coleccion"] = "drop() de la colección libros",
        ["corrupcion"] = "updateMany que sobrescribe el correo de todos los socios (mismo conteo, datos distintos)",
    };

    public async Task<BsonDocument> DesastreAsync(string tipo)
    {
        long afectados;
        switch (tipo)
        {
            case "borrado-masivo":
                afectados = (await Col("prestamos").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty)).DeletedCount;
                break;
            case "drop-coleccion":
                afectados = await Col("libros").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
                await Db.DropCollectionAsync("libros");
                break;
            case "corrupcion":
                afectados = (await Col("socios").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
                    Builders<BsonDocument>.Update.Set("correo", "sin-correo").Set("activo", false))).ModifiedCount;
                break;
            default:
                throw new ArgumentException($"Desastre desconocido: {tipo}");
        }
        await EventoAsync("desastre", $"{Desastres[tipo]} — {afectados} documentos afectados", null);
        return new BsonDocument { ["tipo"] = tipo, ["descripcion"] = Desastres[tipo], ["afectados"] = afectados };
    }

    // ---------- Actividad simulada (nuevos préstamos y devoluciones) ----------

    public async Task<int> ActividadAsync(int n)
    {
        var rnd = new Random();
        var ultimo = await Col("prestamos").Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("_id")).Limit(1).FirstOrDefaultAsync();
        var siguiente = ultimo is null ? 1 : int.Parse(ultimo["_id"].AsString[4..]) + 1;
        var nuevos = Enumerable.Range(0, n).Select(i => Datos.NuevoPrestamo(rnd, siguiente + i, DateTime.UtcNow)).ToList();
        foreach (var p in nuevos) { p["estado"] = "activo"; p.Remove("fechaDevolucion"); }
        await Col("prestamos").InsertManyAsync(nuevos);
        var devueltos = await Col("prestamos").UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("estado", "vencido") & Builders<BsonDocument>.Filter.Lt("fechaLimite", DateTime.UtcNow.AddDays(-3)),
            Builders<BsonDocument>.Update.Set("estado", "devuelto").Set("fechaDevolucion", DateTime.UtcNow));
        await EventoAsync("actividad", $"{n} préstamos nuevos, {devueltos.ModifiedCount} devoluciones", null);
        return n;
    }

    // ---------- Consultas del panel ----------

    public Task<List<BsonDocument>> PrestamosRecientesAsync(int limite) =>
        Col("prestamos").Aggregate()
            .Sort(Builders<BsonDocument>.Sort.Descending("fechaPrestamo"))
            .Limit(limite)
            .Lookup("libros", "libroId", "_id", "libro")
            .Lookup("socios", "socioId", "_id", "socio")
            .Project(new BsonDocument
            {
                ["_id"] = 1, ["estado"] = 1, ["fechaPrestamo"] = 1, ["fechaLimite"] = 1,
                ["libro"] = new BsonDocument("$first", "$libro.titulo"),
                ["socio"] = new BsonDocument("$first", "$socio.nombre"),
            })
            .ToListAsync();

    public Task<List<BsonDocument>> PrestamosPorEstadoAsync() =>
        Col("prestamos").Aggregate()
            .Group(new BsonDocument { ["_id"] = "$estado", ["total"] = new BsonDocument("$sum", 1) })
            .Sort(new BsonDocument("_id", 1))
            .ToListAsync();

    // ---------- Respaldos externos (registrados por GitHub Actions) y eventos ----------

    public async Task RegistrarRespaldoAsync(BsonDocument doc)
    {
        doc["fechaRegistro"] = DateTime.UtcNow;
        await _respaldos.InsertOneAsync(doc);
        var tipo = doc.GetValue("tipo", "respaldo").AsString;
        await EventoAsync("ci", $"GitHub Actions: {tipo} {(doc.GetValue("verificado", false).ToBoolean() ? "verificado" : "con diferencias")} (run {doc.GetValue("run", "-")})", null);
    }

    public Task<List<BsonDocument>> RespaldosAsync(int limite = 15) =>
        _respaldos.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Descending("fechaRegistro")).Limit(limite).ToListAsync();

    public Task EventoAsync(string tipo, string mensaje, string? referencia) =>
        _eventos.InsertOneAsync(new BsonDocument
        {
            ["fecha"] = DateTime.UtcNow, ["tipo"] = tipo, ["mensaje"] = mensaje,
            ["referencia"] = referencia is null ? BsonNull.Value : referencia,
        });

    public Task<List<BsonDocument>> EventosAsync(int limite = 25) =>
        _eventos.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Descending("fecha")).Limit(limite).ToListAsync();
}
