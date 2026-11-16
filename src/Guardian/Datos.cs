using MongoDB.Bson;

namespace Guardian;

/// <summary>Datos de ejemplo de una biblioteca universitaria (deterministas).</summary>
public static class Datos
{
    static readonly (string Titulo, string[] Autores, int Anio, string Categoria)[] Catalogo =
    [
        ("Los ríos profundos", ["José María Arguedas"], 1958, "Literatura peruana"),
        ("La ciudad y los perros", ["Mario Vargas Llosa"], 1963, "Literatura peruana"),
        ("Conversación en La Catedral", ["Mario Vargas Llosa"], 1969, "Literatura peruana"),
        ("El mundo es ancho y ajeno", ["Ciro Alegría"], 1941, "Literatura peruana"),
        ("Tradiciones peruanas", ["Ricardo Palma"], 1872, "Literatura peruana"),
        ("Los heraldos negros", ["César Vallejo"], 1919, "Poesía"),
        ("Trilce", ["César Vallejo"], 1922, "Poesía"),
        ("Un mundo para Julius", ["Alfredo Bryce Echenique"], 1970, "Literatura peruana"),
        ("Redoble por Rancas", ["Manuel Scorza"], 1970, "Literatura peruana"),
        ("Cien años de soledad", ["Gabriel García Márquez"], 1967, "Literatura latinoamericana"),
        ("Pedro Páramo", ["Juan Rulfo"], 1955, "Literatura latinoamericana"),
        ("Rayuela", ["Julio Cortázar"], 1963, "Literatura latinoamericana"),
        ("Ficciones", ["Jorge Luis Borges"], 1944, "Literatura latinoamericana"),
        ("El Aleph", ["Jorge Luis Borges"], 1949, "Literatura latinoamericana"),
        ("Designing Data-Intensive Applications", ["Martin Kleppmann"], 2017, "Bases de datos"),
        ("MongoDB: The Definitive Guide", ["Shannon Bradshaw", "Eoin Brazil", "Kristina Chodorow"], 2019, "Bases de datos"),
        ("Database System Concepts", ["Abraham Silberschatz", "Henry F. Korth", "S. Sudarshan"], 2019, "Bases de datos"),
        ("NoSQL Distilled", ["Pramod J. Sadalage", "Martin Fowler"], 2012, "Bases de datos"),
        ("Seven Databases in Seven Weeks", ["Luc Perkins", "Eric Redmond", "Jim Wilson"], 2018, "Bases de datos"),
        ("Site Reliability Engineering", ["Betsy Beyer", "Chris Jones", "Jennifer Petoff", "Niall Richard Murphy"], 2016, "Operaciones"),
        ("The Pragmatic Programmer", ["David Thomas", "Andrew Hunt"], 2019, "Ingeniería de software"),
        ("Clean Code", ["Robert C. Martin"], 2008, "Ingeniería de software"),
        ("Refactoring", ["Martin Fowler"], 2018, "Ingeniería de software"),
        ("Domain-Driven Design", ["Eric Evans"], 2003, "Ingeniería de software"),
        ("Introduction to Algorithms", ["Thomas H. Cormen", "Charles E. Leiserson", "Ronald L. Rivest", "Clifford Stein"], 2022, "Algoritmos"),
        ("Computer Networks", ["Andrew S. Tanenbaum", "David J. Wetherall"], 2010, "Redes"),
        ("Operating System Concepts", ["Abraham Silberschatz", "Peter B. Galvin", "Greg Gagne"], 2018, "Sistemas operativos"),
        ("Accelerate", ["Nicole Forsgren", "Jez Humble", "Gene Kim"], 2018, "Operaciones"),
        ("Continuous Delivery", ["Jez Humble", "David Farley"], 2010, "Operaciones"),
        ("Terraform: Up & Running", ["Yevgeniy Brikman"], 2022, "Operaciones"),
    ];

    static readonly string[] Nombres = ["Lucía", "Mateo", "Valeria", "Diego", "Camila", "Sebastián", "Ximena", "Thiago", "Daniela", "Rodrigo", "Fernanda", "Joaquín", "Mariana", "Gabriel", "Andrea", "Renato"];
    static readonly string[] Apellidos = ["Quispe", "Mamani", "Flores", "Condori", "Huamán", "Ticona", "Chambilla", "Cutipa", "Vargas", "Rojas", "Ramos", "Apaza", "Laura", "Copa"];
    static readonly string[] Carreras = ["Ingeniería de Sistemas", "Ingeniería Civil", "Derecho", "Medicina Humana", "Arquitectura", "Ingeniería Industrial", "Ciencias Contables"];

    public const int TotalLibros = 30;
    public const int TotalSocios = 40;
    public const int TotalPrestamos = 180;

    public static List<BsonDocument> Libros()
    {
        var rnd = new Random(2026);
        var lista = new List<BsonDocument>();
        for (var i = 0; i < Catalogo.Length; i++)
        {
            var (titulo, autores, anio, categoria) = Catalogo[i];
            var codigo = $"LIB-{i + 1:000}";
            var ejemplares = new BsonArray();
            for (var e = 1; e <= rnd.Next(1, 5); e++)
                ejemplares.Add(new BsonDocument { ["codigo"] = $"{codigo}-E{e}", ["sede"] = rnd.Next(2) == 0 ? "Campus Capanique" : "Campus Granada" });
            lista.Add(new BsonDocument
            {
                ["_id"] = codigo,
                ["titulo"] = titulo,
                ["autores"] = new BsonArray(autores),
                ["anio"] = anio,
                ["categoria"] = categoria,
                ["ejemplares"] = ejemplares,
            });
        }
        return lista;
    }

    public static List<BsonDocument> Socios()
    {
        var rnd = new Random(511);
        var lista = new List<BsonDocument>();
        for (var i = 1; i <= TotalSocios; i++)
        {
            var nombre = $"{Nombres[rnd.Next(Nombres.Length)]} {Apellidos[rnd.Next(Apellidos.Length)]} {Apellidos[rnd.Next(Apellidos.Length)]}";
            lista.Add(new BsonDocument
            {
                ["_id"] = $"SOC-{i:000}",
                ["nombre"] = nombre,
                ["carrera"] = Carreras[rnd.Next(Carreras.Length)],
                ["ciclo"] = rnd.Next(1, 11),
                ["correo"] = $"socio{i:000}@biblioteca.demo",
                ["activo"] = true,
            });
        }
        return lista;
    }

    public static List<BsonDocument> Prestamos(DateTime ahora)
    {
        var rnd = new Random(78690);
        var lista = new List<BsonDocument>();
        for (var i = 1; i <= TotalPrestamos; i++)
            lista.Add(NuevoPrestamo(rnd, i, ahora.AddHours(-rnd.Next(1, 24 * 90))));
        return lista;
    }

    public static BsonDocument NuevoPrestamo(Random rnd, int numero, DateTime fecha)
    {
        var libro = rnd.Next(1, TotalLibros + 1);
        var limite = fecha.AddDays(7);
        var doc = new BsonDocument
        {
            ["_id"] = $"PRE-{numero:00000}",
            ["socioId"] = $"SOC-{rnd.Next(1, TotalSocios + 1):000}",
            ["libroId"] = $"LIB-{libro:000}",
            ["fechaPrestamo"] = fecha,
            ["fechaLimite"] = limite,
        };
        if (limite < DateTime.UtcNow && rnd.Next(10) < 8)
        {
            doc["fechaDevolucion"] = fecha.AddDays(rnd.Next(1, 9));
            doc["estado"] = "devuelto";
        }
        else
        {
            doc["estado"] = limite < DateTime.UtcNow ? "vencido" : "activo";
        }
        return doc;
    }
}
