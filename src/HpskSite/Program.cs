using HpskSite;
using HpskSite.Extensions;
using HpskSite.Middleware;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using System.Text;

// ⚠️⚠️ CP437 för SIE-exporten. .NET Core bär bara ett fåtal kodsidor som standard, så
//    Encoding.GetEncoding(437) KASTAR utan den här raden — och det gör den först när någon
//    klickar Exportera, inte vid start. SIE-standarden föreskriver CP437; en fil i UTF-8 läses
//    in med sönderslagna å, ä och ö i varje kontonamn, vilket ser ut som vårt fel även när allt
//    annat är rätt.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .Build();

// Register custom services
builder.Services.AddScoped<HpskSite.Services.PaymentService>();
builder.Services.AddScoped<HpskSite.Services.PushNotificationService>();
builder.Services.AddScoped<HpskSite.Services.DirektplaceringStartListService>();

// Register Handicap System services
builder.Services.Configure<HpskSite.Models.HandicapSettings>(builder.Configuration.GetSection("HandicapSettings"));
builder.Services.AddSingleton<HpskSite.CompetitionTypes.Precision.Services.IHandicapCalculator, HpskSite.CompetitionTypes.Precision.Services.HandicapCalculator>();
builder.Services.AddScoped<HpskSite.Services.IShooterStatisticsService, HpskSite.Services.ShooterStatisticsService>();

// Register Precision competition type services
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionScoringService>();
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionStartListService>();
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionResultsService>();
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Services.PrecisionRegistrationService>();

// Register common competition type interfaces
builder.Services.AddScoped<HpskSite.CompetitionTypes.Common.Interfaces.IScoringService>(sp =>
    sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionScoringService>());
builder.Services.AddScoped<HpskSite.CompetitionTypes.Common.Interfaces.IStartListService>(sp =>
    sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionStartListService>());
builder.Services.AddScoped<HpskSite.CompetitionTypes.Common.Interfaces.IResultsService>(sp =>
    sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionResultsService>());
builder.Services.AddScoped<HpskSite.CompetitionTypes.Common.Interfaces.IRegistrationService>(sp =>
    sp.GetRequiredService<HpskSite.CompetitionTypes.Precision.Services.PrecisionRegistrationService>());

// Register Precision StartList refactored classes
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Controllers.StartListRequestValidator>();
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Controllers.UmbracoStartListRepository>();
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Controllers.StartListGenerator>();
builder.Services.AddScoped<HpskSite.CompetitionTypes.Precision.Controllers.StartListHtmlRenderer>();

// Club-per-registration: resolving a member's clubs, and pushing a corrected club out to the
// start lists / patrol rows that already snapshotted the old one.
builder.Services.AddScoped<HpskSite.Services.MemberClubService>();
builder.Services.AddScoped<HpskSite.Services.RegistrationClubPropagationService>();

// ⚠️⚠️ DataProtection-nycklarna i DATABASEN (skalplanen D2, 2026-10-12). De signerar inloggningar,
// antiforgery och de utskrivna/mejlade länkarna; i App_Data dör alla vid en serverflytt och kan inte
// delas mellan instanser. App_Data-katalogen är kvar som källa för övergången (filerna kopieras in
// vid första läsningen) och som reserv om migreringen inte är körd — se SqlDataProtectionKeyRepository.
var keysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys");
var keysDir = new DirectoryInfo(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(keysDir)
    .SetApplicationName("HpskSite");
var dpConnection = builder.Configuration.GetConnectionString("umbracoDbDSN");
if (!string.IsNullOrWhiteSpace(dpConnection) && !dpConnection.Contains("|DataDirectory|"))
{
    builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
        .Configure<ILoggerFactory>((options, loggers) =>
        {
            var files = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(keysDir, loggers);
            options.XmlRepository = new HpskSite.Services.Hosting.SqlDataProtectionKeyRepository(
                dpConnection, keysDir, files, loggers.CreateLogger("HpskSite.DataProtection"));
        });
}

// Configure member authentication cookies specifically (not backoffice)
// This ensures RememberMe checkbox is properly respected for frontend members
builder.Services.ConfigureOptions<MemberCookieConfigureOptions>();

// Configure Security Stamp Validator to prevent 30-minute auto-logout
// By default, ASP.NET Core Identity validates security stamps every 30 minutes,
// which causes users to be logged out even with "Remember Me" checked
builder.Services.ConfigureOptions<MemberSecurityStampValidatorOptions>();

// Register SignalR for real-time training match communication
var signalR = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});
// Skalplanen D4: med flera instanser når ett meddelande annars bara de anslutna på den egna servern.
// Bara när ConnectionStrings:redis är satt — utan den som förut.
var redisConnection = builder.Configuration.GetConnectionString(HpskSite.Services.Hosting.ScaleOutComposer.RedisConnectionName);
if (!string.IsNullOrWhiteSpace(redisConnection))
{
    signalR.AddStackExchangeRedis(redisConnection, o =>
    {
        o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("hpsk-signalr");
        o.Configuration.AbortOnConnectFail = false;
    });
}

// Skalplanen D4: bakom en lastbalanserare läses klientens adress och protokoll ur X-Forwarded-*,
// men bara från uttryckligt betrodda proxyer. Av som standard.
var scaleOut = builder.Configuration.GetSection(HpskSite.Services.Hosting.ScaleOutOptions.SectionName)
    .Get<HpskSite.Services.Hosting.ScaleOutOptions>() ?? new HpskSite.Services.Hosting.ScaleOutOptions();
if (scaleOut.ForwardedHeaders.Enabled)
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
            | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
        foreach (var p in scaleOut.ForwardedHeaders.KnownProxies)
            if (System.Net.IPAddress.TryParse(p, out var ip)) o.KnownProxies.Add(ip);
        foreach (var n in scaleOut.ForwardedHeaders.KnownNetworks)
            if (System.Net.IPNetwork.TryParse(n, out var net)) o.KnownIPNetworks.Add(net);
    });
}

// Add CORS for mobile app SignalR connections
builder.Services.AddCors(options =>
{
    options.AddPolicy("MobileApp", policy =>
    {
        policy.SetIsOriginAllowed(_ => true) // Allow any origin for mobile apps
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials(); // Required for SignalR
    });
});

// Configure JWT Authentication for Mobile API
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.AddSingleton<JwtTokenService>();

// Add controllers for API endpoints (Mobile app)
builder.Services.AddControllers();

// Add JWT Bearer authentication
var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>();
if (jwtSettings != null && jwtSettings.IsValid())
{
    var key = Encoding.UTF8.GetBytes(jwtSettings.Secret);

    builder.Services.AddAuthentication()
        .AddJwtBearer("JwtBearer", options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtSettings.Issuer,
                ValidAudience = jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ClockSkew = TimeSpan.Zero
            };

            // Support JWT tokens in SignalR connections
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    {
                        context.Token = accessToken;
                    }
                    return Task.CompletedTask;
                }
            };
        });

    builder.Services.AddAuthorization();
}

WebApplication app = builder.Build();

await app.BootUmbracoAsync();

// Först i kedjan, så att HTTPS-omdirigeringen och allt annat ser klientens riktiga protokoll.
if (scaleOut.ForwardedHeaders.Enabled)
{
    app.UseForwardedHeaders();
}

// Redirect HTTP to HTTPS in production
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    await next();
    if (context.Response.ContentType?.StartsWith("text/html") == true && !context.Response.Headers.ContainsKey("Content-Type"))
    {
        context.Response.Headers.Add("Content-Type", "text/html; charset=utf-8");
    }
});

// Member activity tracking middleware is now registered via MemberActivityTrackingComposer
// using PostRouting pipeline filter to ensure it runs after authentication

// Enable CORS for mobile app (must be before routing/endpoints)
app.UseCors("MobileApp");

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

// Map API controllers for mobile app
app.MapControllers();

// Map SignalR hub for training match real-time communication
// Note: CORS is handled globally via app.UseCors() - don't use RequireCors() here
// as it conflicts with Umbraco's internal routing pipeline
app.MapHub<HpskSite.Hubs.TrainingMatchHub>("/hubs/trainingmatch");

await app.RunAsync();

