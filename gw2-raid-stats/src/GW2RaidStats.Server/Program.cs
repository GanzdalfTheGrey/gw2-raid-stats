using Microsoft.Extensions.FileProviders;
using GW2RaidStats.Infrastructure;
using GW2RaidStats.Infrastructure.Configuration;

// Enable legacy timestamp behavior for Npgsql to properly handle DateTimeOffset with timezones
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel for long-running imports
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(30);
});

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Storage configuration
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

// Add Infrastructure services (database, import services)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddInfrastructure(connectionString);

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseWebAssemblyDebugging();
}

app.UseHttpsRedirection();

// Serve Blazor WebAssembly static files
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// Serve HTML reports from storage
var storageOptions = app.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var encountersPath = storageOptions.EncountersPath;

if (Directory.Exists(encountersPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Path.GetFullPath(encountersPath)),
        RequestPath = "/reports",
        ServeUnknownFileTypes = false,
        DefaultContentType = "text/html"
    });
}
else
{
    // Create directory if it doesn't exist (for first run)
    Directory.CreateDirectory(encountersPath);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Path.GetFullPath(encountersPath)),
        RequestPath = "/reports",
        ServeUnknownFileTypes = false,
        DefaultContentType = "text/html"
    });
}

app.UseRouting();

app.MapControllers();

// Fallback to index.html for Blazor routing
app.MapFallbackToFile("index.html");

app.Run();
