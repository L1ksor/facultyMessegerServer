using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using FacultyMessenger.Api.Auth;
using FacultyMessenger.Api.Configuration;
using FacultyMessenger.Api.Data;
using FacultyMessenger.Api.Hubs;
using FacultyMessenger.Api.Services.Auth;
using FacultyMessenger.Api.Services.Chats;
using FacultyMessenger.Api.Services.Email;
using FacultyMessenger.Api.Services.Messages;
using FacultyMessenger.Api.Services.Polls;
using FacultyMessenger.Api.Services.Storage;
using FacultyMessenger.Api.Services.Users;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- Options (с валидацией при старте) ----------
builder.Services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<SmtpOptions>().Bind(config.GetSection(SmtpOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<FileStorageOptions>().Bind(config.GetSection(FileStorageOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.Configure<SeedAdminOptions>(config.GetSection(SeedAdminOptions.SectionName));

var jwt = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
          ?? throw new InvalidOperationException("Секция 'Jwt' не настроена.");
var storage = config.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>() ?? new FileStorageOptions();

// ---------- База данных ----------
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(config.GetConnectionString("Default"),
        npgsql => npgsql.EnableRetryOnFailure(3)));

// ---------- Аутентификация JWT ----------
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = AppClaims.UserId,
            RoleClaimType = "role"
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

// ---------- Авторизация (3-уровневая RBAC) ----------
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.AdminOnly, p => p.RequireRole(Roles.Admin))
    .AddPolicy(Policies.EmployeeOrAdmin, p => p.RequireRole(Roles.Employee, Roles.Admin));

// ---------- Прикладные сервисы ----------
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddHttpClient<IEiosClient, KurskSuEiosClient>();
builder.Services.AddScoped<IEmailService, MailKitEmailService>();
builder.Services.AddScoped<IUserImportService, UserImportService>();
builder.Services.AddSingleton<IFileStorageService, GarageS3FileStorageService>();
builder.Services.AddScoped<IChatAccessService, ChatAccessService>();
builder.Services.AddScoped<IMessageWorkflowService, MessageWorkflowService>();
builder.Services.AddScoped<IPollService, PollService>();

// ---------- SignalR ----------
builder.Services.AddSignalR(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    o.MaximumReceiveMessageSize = 64 * 1024;
}).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---------- MVC / загрузка файлов ----------
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var maxUpload = Math.Max(storage.MaxFileSizeBytes, storage.MaxAudioSizeBytes) + 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUpload);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxUpload);

builder.Services.AddProblemDetails();

// ---------- CORS ----------
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ---------- Swagger ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Faculty Messenger API",
        Version = "v1",
        Description = "Академический мессенджер факультета: 3-уровневый RBAC, старосты/кураторы групп, конструктор ролей чата, опросы, объявления и хранение файлов в Garage S3."
    });

    var scheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Вставьте JWT-токен (без префикса 'Bearer ').",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    c.AddSecurityDefinition("Bearer", scheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });

    var xml = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    if (File.Exists(xml)) c.IncludeXmlComments(xml);
});

var app = builder.Build();

// ---------- Миграции + сид ----------
await DbInitializer.InitializeAsync(app.Services);

// ---------- Pipeline ----------
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Faculty Messenger API v1"));
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
