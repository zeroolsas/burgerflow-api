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
var supabaseApiKey = builder.Configuration["Supabase:Key"]
    ?? builder.Configuration["Supabase:AnonKey"]
    ?? throw new Exception("Не задан Supabase:Key");
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

// Регистрация через Supabase Auth с автосозданием пользователя при необходимости
app.MapPost("/api/auth/register", async (AuthDto model) =>
{
    if (string.IsNullOrWhiteSpace(model.Login) || string.IsNullOrWhiteSpace(model.Password))
        return Results.BadRequest(new { message = "Email and password are required" });

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("apikey", supabaseApiKey);

    var res = await http.PostAsJsonAsync($"{supabaseUrl}/auth/v1/signup", new
    {
        email = model.Login.Trim(),
        password = model.Password
    });

    var body = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
        return Results.BadRequest(new { message = $"Signup error: {body}" });

    using var doc = JsonDocument.Parse(body);
    var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;

    return Results.Ok(new { message = "Registered", token, user = model.Login });
});

// Вход через Supabase Auth; если пользователя ещё нет — создаём через signup и логинимся
app.MapPost("/api/auth/login", async (AuthDto model) =>
{
    if (string.IsNullOrWhiteSpace(model.Login) || string.IsNullOrWhiteSpace(model.Password))
        return Results.BadRequest(new { message = "Email and password are required" });

    var email = model.Login.Trim();
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("apikey", supabaseApiKey);

    var res = await http.PostAsJsonAsync($"{supabaseUrl}/auth/v1/token?grant_type=password", new
    {
        email,
        password = model.Password
    });

    var body = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode)
    {
        var signUpRes = await http.PostAsJsonAsync($"{supabaseUrl}/auth/v1/signup", new
        {
            email,
            password = model.Password
        });

        if (signUpRes.IsSuccessStatusCode)
        {
            var signUpBody = await signUpRes.Content.ReadAsStringAsync();
            using var signUpDoc = JsonDocument.Parse(signUpBody);
            if (signUpDoc.RootElement.TryGetProperty("access_token", out var newTokenEl) && newTokenEl.GetString() is { Length: > 0 } newToken)
                return Results.Ok(new { token = newToken, user = model.Login });

            res = await http.PostAsJsonAsync($"{supabaseUrl}/auth/v1/token?grant_type=password", new
            {
                email,
                password = model.Password
            });
            body = await res.Content.ReadAsStringAsync();
        }

        if (!res.IsSuccessStatusCode)
            return Results.BadRequest(new { message = $"Login error: {body}" });
    }

    using var doc = JsonDocument.Parse(body);
    var token = doc.RootElement.GetProperty("access_token").GetString();

    return Results.Ok(new { token, user = model.Login });
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
    var orders = await AsUser(db, user, () =>
        db.Orders.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .Take(50)
            .Select(o => new
            {
                o.Id,
                o.CreatedAt,
                Total = o.TotalAmount,
                Status = o.StatusHistory.OrderByDescending(h => h.ChangedAt).Select(h => h.Status!.Name).FirstOrDefault()
            })
            .ToListAsync());
    return Results.Ok(orders);
}).RequireAuthorization();

app.MapPost("/orders", async (NewOrder dto, ClaimsPrincipal user, AppDbContext db) =>
{
    if (dto.Items == null || dto.Items.Count == 0 || dto.Items.Any(i => i.Quantity < 1))
        return Results.BadRequest(new { message = "Корзина пуста" });

    var userId = Guid.Parse(user.FindFirst("sub")!.Value);
    var ids = dto.Items.Select(i => i.DishId).Distinct().ToList();
    var dishes = await db.Dishes.AsNoTracking()
        .Where(d => d.IsAvailable && ids.Contains(d.Id))
        .ToDictionaryAsync(d => d.Id);

    if (dishes.Count != ids.Count)
        return Results.BadRequest(new { message = "Какого-то блюда нет в меню" });

    var customer = await db.Customers.FirstOrDefaultAsync(c => c.UserId == userId);
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
        await AsUser(db, user, async () =>
        {
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return true;
        });
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "42501" })
    {
        return Results.StatusCode(403);
    }

    return Results.Created($"/orders/{order.Id}", new { order.Id });
}).RequireAuthorization();

app.Run();