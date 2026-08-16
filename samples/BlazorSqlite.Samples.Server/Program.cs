using BlazorSqlite.Samples.Data;
using BlazorSqlite.Samples.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await DemoData.SeedIfEmptyAsync(db);
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGet("/api/products", async (AppDbContext db, CancellationToken ct) =>
    await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct));

app.MapPost("/api/products", async (NewProduct body, AppDbContext db, CancellationToken ct) =>
{
    var categoryId = await db.Categories.Select(c => c.Id).FirstAsync(ct);
    db.Products.Add(new Product
    {
        Sku = "SRV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        Name = body.Name,
        Price = body.Price,
        CreatedUtc = DateTime.UtcNow,
        CategoryId = categoryId,
    });
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapFallbackToFile("index.html");
app.Run();

internal sealed record NewProduct(string Name, decimal Price);
