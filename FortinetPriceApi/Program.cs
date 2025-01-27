using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Data;
using OfficeOpenXml;
using System.Text.RegularExpressions;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Configuración global
builder.Services.AddControllers();

var app = builder.Build();

// Conexión a la base de datos SQLite
string connectionString = "Data Source=fortinet_prices.db";

// Inicialización de la base de datos
app.MapGet("/init", () =>
{
    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    string createTableQuery = @"
        CREATE TABLE IF NOT EXISTS Prices (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Identifier TEXT,
            ProductFamilyGroup TEXT,
            Product TEXT,
            Item TEXT,
            SKU TEXT,
            Description1 TEXT,
            Description2 TEXT,
            Price REAL,
            Category TEXT
        );";

    using var command = new SqliteCommand(createTableQuery, connection);
    command.ExecuteNonQuery();

    return Results.Ok("Database initialized.");
});

// Endpoint para subir archivo Excel (antiforgery desactivado)
app.MapPost("/upload", async (HttpContext httpContext) =>
{
    Console.WriteLine("Endpoint /upload called.");

    var file = httpContext.Request.Form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
    {
        Console.WriteLine("No file uploaded or file is empty.");
        return Results.BadRequest("No file uploaded or file is empty.");
    }

    try
    {
        Console.WriteLine($"Processing file: {file.FileName}");

        using var stream = file.OpenReadStream();
        using var package = new ExcelPackage();
        await package.LoadAsync(stream);
        var worksheet = package.Workbook.Worksheets[0];

        Console.WriteLine("Excel file loaded successfully.");

        // Validación de columnas requeridas
        var requiredColumns = new[] { "Identifier", "Product Family Group", "Product", "Item", "SKU", "Description #1", "Description #2", "Price" };
        foreach (var colName in requiredColumns)
        {
            if (!worksheet.Cells[1, 1, 1, worksheet.Dimension.End.Column].Any(c => c.Text == colName))
            {
                Console.WriteLine($"Missing column: {colName}");
                return Results.BadRequest($"The column '{colName}' is missing in the Excel file.");
            }
        }

        Console.WriteLine("Columns validated successfully.");

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Limpiar la tabla antes de insertar nuevos datos
        Console.WriteLine("Clearing existing data in the database.");
        var clearCommand = new SqliteCommand("DELETE FROM Prices", connection);
        clearCommand.ExecuteNonQuery();

        using var transaction = connection.BeginTransaction(); // Inicia la transacción

        for (int row = 2; row <= worksheet.Dimension.End.Row; row++)
        {   
             // Leer la celda
            var priceCell = worksheet.Cells[row, 10];
            object rawValue = priceCell.Value; // Obtener el valor de la celda como objeto

            Console.WriteLine($"Row {row}, Raw Price Cell Value: {rawValue}");

            decimal price = 0; // Valor predeterminado

            // Verificar si el valor es numérico o "No Discount"
            if (rawValue is double || rawValue is decimal || rawValue is int)
            {
                price = Convert.ToDecimal(rawValue); // Convertir a decimal si es válido
            }
            else if (rawValue is string textValue && textValue.Trim().Equals("No Discount", StringComparison.OrdinalIgnoreCase))
            {
                price = 0; // Si el valor es "No Discount", asignar 0
                Console.WriteLine($"Row {row}, Price set to 0 for 'No Discount'");
            }
            else
            {
                Console.WriteLine($"Row {row}, Invalid or Unknown Value in Price Column: {rawValue}. Setting to 0.");
            }

            Console.WriteLine($"Row {row}, Final Price Value: {price}");

            var insertCommand = new SqliteCommand(@"
                INSERT INTO Prices (Identifier, ProductFamilyGroup, Product, Item, SKU, Description1, Description2, Price, Category)
                VALUES (@Identifier, @ProductFamilyGroup, @Product, @Item, @SKU, @Description1, @Description2, @Price, @Category);", connection, transaction);

            insertCommand.Parameters.AddWithValue("@Identifier", worksheet.Cells[row, 1].Text);
            insertCommand.Parameters.AddWithValue("@ProductFamilyGroup", worksheet.Cells[row, 2].Text);
            insertCommand.Parameters.AddWithValue("@Product", worksheet.Cells[row, 3].Text);
            insertCommand.Parameters.AddWithValue("@Item", worksheet.Cells[row, 4].Text);
            insertCommand.Parameters.AddWithValue("@SKU", worksheet.Cells[row, 5].Text);
            insertCommand.Parameters.AddWithValue("@Description1", worksheet.Cells[row, 6].Text);
            insertCommand.Parameters.AddWithValue("@Description2", worksheet.Cells[row, 7].Text);
            insertCommand.Parameters.AddWithValue("@Price", price);
            insertCommand.Parameters.AddWithValue("@Category", worksheet.Cells[row, 11].Text);

            insertCommand.ExecuteNonQuery();

            // Agrega un log para confirmar el progreso
            if ((row - 1) % 1000 == 0)
            {
                Console.WriteLine($"Inserted {row - 1} rows so far...");
            }
        }

        transaction.Commit(); // Confirmar todos los cambios en la base de datos
        Console.WriteLine("Data inserted successfully into the database.");


        return Results.Ok("File uploaded and processed successfully.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"An error occurred: {ex.Message}");
        return Results.BadRequest($"An error occurred: {ex.Message}");
    }
});

app.MapGet("/prices", () =>
{
    using var connection = new SqliteConnection("Data Source=fortinet_prices.db");
    connection.Open();

    var command = new SqliteCommand("SELECT * FROM Prices LIMIT 100;", connection);
    using var reader = command.ExecuteReader();

    var results = new List<object>();
    while (reader.Read())
    {
        results.Add(new
        {
            Identifier = reader["Identifier"],
            ProductFamilyGroup = reader["ProductFamilyGroup"],
            Product = reader["Product"],
            Item = reader["Item"],
            SKU = reader["SKU"],
            Description1 = reader["Description1"],
            Description2 = reader["Description2"],
            StandarPrice = reader["Price"]
        });
    }

    return Results.Ok(results);
});

// Endpoint para búsquedas con autosugerencias
app.MapGet("/search", (string? query) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return Results.BadRequest("The query parameter is required.");
    }

    using var connection = new SqliteConnection("Data Source=fortinet_prices.db");
    connection.Open();

    string searchQuery = @"
        SELECT Identifier, ProductFamilyGroup, Product, Item, SKU, Description1, Description2, Category, Price
        FROM Prices
        WHERE SKU LIKE @Query OR Product LIKE @Query OR Description1 LIKE @Query OR Description2 LIKE @Query";

    using var command = new SqliteCommand(searchQuery, connection);
    command.Parameters.AddWithValue("@Query", "%" + query + "%");

    using var reader = command.ExecuteReader();

    var results = new List<object>();
    var culture = new CultureInfo("es-UY"); // Cultura de Uruguay para el formato

    var discounts = new Dictionary<string, decimal>
    {
        { "A", 0.35m },
        { "B", 0.35m },
        { "N", 0.35m },
        { "F", 0.20m },
        { "C", 0.15m },
        { "E", 0.30m },
        { "S", 0.20m },
        { "D", 0.05m }
    };

    while (reader.Read())
    {
        // Obtener los valores y calcular el precio con descuento
        string category = reader["Category"]?.ToString()?.Trim() ?? "";
        decimal price = reader["Price"] != DBNull.Value ? Convert.ToDecimal(reader["Price"]) : 0;

        decimal discountedPrice = price;

        if (discounts.ContainsKey(category))
        {
            discountedPrice = Math.Round(price * (1 - discounts[category]), 0); // Aplicar descuento y redondear
        }

        results.Add(new
        {
            Identifier = reader["Identifier"],
            ProductFamilyGroup = reader["ProductFamilyGroup"],
            Product = reader["Product"],
            Item = reader["Item"],
            SKU = reader["SKU"],
            Description1 = reader["Description1"],
            Description2 = reader["Description2"],
            Category = category,
            StandardPrice = string.Format(culture, "{0:N0}", price), // Formato de precio original
            DiscountedPrice = string.Format(culture, "{0:N0}", discountedPrice) // Formato de precio con descuento
        });
    }

    return Results.Ok(results);
});

// Configuración para servir archivos estáticos (como upload.html)
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.Run();
