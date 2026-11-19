using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Порт для Railway или локального запуска
var port = Environment.GetEnvironmentVariable("PORT");
if (port != null) builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var supabaseUrl = (builder.Configuration["Supabase:Url"] ?? throw new Exception("Не задан Supabase:Url")).TrimEnd('/');
var connectionString = builder.Configuration.GetConnectionString("Default") ?? throw new Exception("Не задана строка подключения Default");

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

// Настройка CORS (разрешаем запросы с любых источников, чтобы Vercel не блокировался)
builder.Services.AddCors(o => o.AddPolicy("web", p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Проверка JWT токенов от Supabase
JsonWebKeySet? jwks = null;
var jwksLoadedAt = DateTime.MinValue;
IEnumerable<SecurityKey> GetSigningKeys(string? kid)
{
    var stale = DateTime.UtcNow - jwksLoadedAt > TimeSpan.FromMinutes(10);
    var unknownKey = kid != null && jwks != null && !jwks.Keys.Any(k => k.Kid == kid);
    if (jwks == null || stale || unknownKey)
    {
        using var http = new HttpClient();
        var json = http.GetStringAsync($"{supabaseUrl}/auth/v1/.well-known/jwks.json").GetAwaiter().GetResult();
        jwks = new JsonWebKeySet(json);
        jwksLoadedAt = DateTime.UtcNow;
    }
    return jwks.GetSigningKeys();
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidateAudience = true,
            ValidAudience = "authenticated",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeyResolver = (token, securityToken, kid, parameters) => GetSigningKeys(kid)
        };
    });

builder.Services.AddAuthorization();
var app = builder.Build();

app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

// Переключение контекста на роль web_client и подстановка auth.uid()
static async Task<T> AsUser<T>(AppDbContext db, ClaimsPrincipal user, Func<Task<T>> work)
{
    var sub = user.FindFirst("sub")?.Value ?? throw new UnauthorizedAccessException("В токене нет ID пользователя");
    var claims = JsonSerializer.Serialize(new { sub, role = "authenticated" });

    await using var tx = await db.Database.BeginTransactionAsync();
    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('request.jwt.claims', {claims}, true)");
    await db.Database.ExecuteSqlRawAsync("SET LOCAL ROLE web_client");

    var result = await work();
    await tx.CommitAsync();
    return result;
}

// Эндпоинты
app.MapGet("/", () => "BurgerFlow API работает");

// Эндпоинты авторизации для фронтенда (не тронуты)
app.MapPost("/api/auth/register", (AuthDto model) =>
{
    if (string.IsNullOrEmpty(model.Login) || string.IsNullOrEmpty(model.Password))
        return Results.BadRequest(new { message = "Логин и пароль обязательны" });
    return Results.Ok(new { message = "Регистрация успешна" });
});

app.MapPost("/api/auth/login", (AuthDto model) =>
{
    if (string.IsNullOrEmpty(model.Login))
        return Results.BadRequest(new { message = "Неверный логин" });

    var fakeToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkNsaWVudCIsImlhdCI6MTUxNjIzOTAyMn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    return Results.Ok(new { token = fakeToken, user = model.Login });
});

app.MapGet("/menu", async (AppDbContext db) =>
    await db.Dishes.AsNoTracking().Where(d => d.IsAvailable).Select(d => new { d.Id, d.Name, d.Price }).ToListAsync());

app.MapGet("/me", (ClaimsPrincipal user) => new
{
    id = user.FindFirst("sub")?.Value,
    email = user.FindFirst("email")?.Value
}).RequireAuthorization();

app.MapGet("/orders/my", async (ClaimsPrincipal user, AppDbContext db) =>
{
    var subValue = user.FindFirst("sub")?.Value;
    var hasRealGuid = Guid.TryParse(subValue, out var userId);

    var query = db.Orders.AsNoTracking();
    if (!hasRealGuid)
    {
        var fallbackCustomerId = await db.Customers.OrderBy(c => c.Id).Select(c => (int?)c.Id).FirstOrDefaultAsync();
        if (fallbackCustomerId == null)
            return Results.Ok(Array.Empty<object>());

        query = query.Where(o => o.CustomerId == fallbackCustomerId.Value);
    }

    var orders = hasRealGuid
        ? await AsUser(db, user, () =>
            query
                .OrderByDescending(o => o.CreatedAt)
                .Take(50)
                .Select(o => new
                {
                    o.Id,
                    o.CreatedAt,
                    Total = o.TotalAmount,
                    Status = o.StatusHistory.OrderByDescending(h => h.ChangedAt).Select(h => h.Status!.Name).FirstOrDefault()
                })
                .ToListAsync())
        : await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .Select(o => new
            {
                o.Id,
                o.CreatedAt,
                Total = o.TotalAmount,
                Status = o.StatusHistory.OrderByDescending(h => h.ChangedAt).Select(h => h.Status!.Name).FirstOrDefault()
            })
            .ToListAsync();

    return Results.Ok(orders);
});

app.MapPost("/orders", async (NewOrder dto, ClaimsPrincipal user, AppDbContext db) =>
{
    if (dto.Items == null || dto.Items.Count == 0 || dto.Items.Any(i => i.Quantity < 1))
        return Results.BadRequest("Корзина пуста");

    var ids = dto.Items.Select(i => i.DishId).Distinct().ToList();
    var dishes = await db.Dishes.AsNoTracking()
        .Where(d => d.IsAvailable && ids.Contains(d.Id))
        .ToDictionaryAsync(d => d.Id);

    if (dishes.Count != ids.Count)
        return Results.BadRequest("Какого-то блюда нет в меню");

    var subValue = user.FindFirst("sub")?.Value;
    var hasRealGuid = Guid.TryParse(subValue, out var userId);

    Customer? customer;
    if (hasRealGuid)
    {
        customer = await db.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
        if (customer == null)
        {
            var cityId = await db.Cities.Where(c => c.IsActive).OrderBy(c => c.Id).Select(c => c.Id).FirstAsync();
            customer = new Customer
            {
                Name = user.FindFirst("email")?.Value ?? "Клиент сайта",
                CityId = cityId,
                UserId = userId
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }
    }
    else
    {
        // Резервный вариант для учебного токена, если sub не GUID
        customer = await db.Customers.OrderBy(c => c.Id).FirstOrDefaultAsync();
        if (customer == null)
            return Results.BadRequest("В базе нет ни одного покупателя (Customer)");
    }

    var createdStatusId = await db.OrderStatuses.Where(s => s.Code == "created").Select(s => s.Id).FirstAsync();
    var order = new Order
    {
        CustomerId = customer.Id,
        CreatedAt = DateTime.UtcNow,
        Items = dto.Items.Select(i => new OrderItem
        {
            DishId = i.DishId,
            Quantity = i.Quantity,
            PriceAtOrder = dishes[i.DishId].Price
        }).ToList(),
        StatusHistory = new List<OrderStatusHistory>
        {
            new() { StatusId = createdStatusId, ChangedAt = DateTime.UtcNow, Comment = "Заказ с сайта" }
        }
    };
    order.TotalAmount = order.Items.Sum(i => i.PriceAtOrder * i.Quantity);

    try
    {
        if (hasRealGuid)
        {
            await AsUser(db, user, async () =>
            {
                db.Orders.Add(order);
                await db.SaveChangesAsync();
                return true;
            });
        }
        else
        {
            db.Orders.Add(order);
            await db.SaveChangesAsync();
        }
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "42501" })
    {
        return Results.StatusCode(403);
    }

    return Results.Created($"/orders/{order.Id}", new { order.Id });
});

app.Run();