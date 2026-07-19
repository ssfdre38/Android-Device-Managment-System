using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Enable CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

// Configure Authentication & Authorization
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login.html";
        options.ExpireTimeSpan = TimeSpan.FromHours(24);
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// Custom Authentication Middleware to protect static files and endpoints
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower() ?? "";
    
    // Bypass auth for login page resources, registration, status reports, command polling/completion, download assets, and fonts
    if (path == "/login.html" || path == "/login.js" || path == "/styles.css" || 
        path == "/api/login" || path.StartsWith("/api/devices/register") || 
        path.Contains("/status") || path.Contains("/commands/pending") || 
        path.Contains("/complete") || path == "/dma-client.apk" || 
        path == "/haven-client.apk" || path == "/dma-agent-win-x64.exe" || 
        path.Contains("font"))
    {
        await next();
        return;
    }

    if (context.User?.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/login.html");
        return;
    }
    
    await next();
});

var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".apk"] = "application/vnd.android.package-archive";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider
});

// Database Connection String
const string DbPath = "C:\\Users\\admin\\android-device-manager-server\\device_manager.db";
const string ConnectionString = $"Data Source={DbPath}";

// Initialize Database
InitializeDatabase();

// ── Endpoints ────────────────────────────────────────────────────────────────

// Serve dashboard HTML at root
app.MapGet("/", async (HttpContext context) =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync("wwwroot/index.html");
});

// Device Registration / Heartbeat
app.MapPost("/api/devices/register", async (RegisterDeviceDto dto) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM devices WHERE id = @id", conn);
    checkCmd.Parameters.AddWithValue("@id", dto.Id);
    var exists = (long)(await checkCmd.ExecuteScalarAsync() ?? 0) > 0;
    
    if (exists)
    {
        var updateQuery = @"
            UPDATE devices 
            SET name = @name, model = @model, android_version = @android_version, 
                is_online = 1, last_seen = datetime('now')
            WHERE id = @id";
        using var updateCmd = new SqliteCommand(updateQuery, conn);
        updateCmd.Parameters.AddWithValue("@id", dto.Id);
        updateCmd.Parameters.AddWithValue("@name", dto.Name);
        updateCmd.Parameters.AddWithValue("@model", dto.Model);
        updateCmd.Parameters.AddWithValue("@android_version", dto.AndroidVersion);
        await updateCmd.ExecuteNonQueryAsync();
    }
    else
    {
        var insertQuery = @"
            INSERT INTO devices (id, name, model, android_version, battery, storage_used, storage_total, is_online, last_seen, app_list)
            VALUES (@id, @name, @model, @android_version, 100, 0, 0, 1, datetime('now'), '[]')";
        using var insertCmd = new SqliteCommand(insertQuery, conn);
        insertCmd.Parameters.AddWithValue("@id", dto.Id);
        insertCmd.Parameters.AddWithValue("@name", dto.Name);
        insertCmd.Parameters.AddWithValue("@model", dto.Model);
        insertCmd.Parameters.AddWithValue("@android_version", dto.AndroidVersion);
        await insertCmd.ExecuteNonQueryAsync();
    }
    
    return Results.Ok(new { success = true });
});

// Device Full Status Report
app.MapPost("/api/devices/{deviceId}/status", async (string deviceId, ReportStatusDto dto) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var query = @"
        UPDATE devices 
        SET battery = @battery, storage_used = @storage_used, storage_total = @storage_total, 
            app_list = @app_list, storage_volumes = @storage_volumes, system_info = @system_info, 
            is_online = 1, last_seen = datetime('now')
        WHERE id = @id";
    using var cmd = new SqliteCommand(query, conn);
    cmd.Parameters.AddWithValue("@id", deviceId);
    cmd.Parameters.AddWithValue("@battery", dto.Battery);
    cmd.Parameters.AddWithValue("@storage_used", dto.StorageUsed);
    cmd.Parameters.AddWithValue("@storage_total", dto.StorageTotal);
    cmd.Parameters.AddWithValue("@app_list", JsonSerializer.Serialize(dto.AppList));
    cmd.Parameters.AddWithValue("@storage_volumes", JsonSerializer.Serialize(dto.StorageVolumes ?? new()));
    cmd.Parameters.AddWithValue("@system_info", JsonSerializer.Serialize(dto.SystemInfo ?? new SystemInfo("", "", -1, 0, 0, "", 0, "", false)));
    await cmd.ExecuteNonQueryAsync();
    
    return Results.Ok(new { success = true });
});

// Get all devices
app.MapGet("/api/devices", async () =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    // Auto offline devices not seen in 30 seconds
    using var offlineCmd = new SqliteCommand("UPDATE devices SET is_online = 0 WHERE last_seen < datetime('now', '-30 seconds')", conn);
    await offlineCmd.ExecuteNonQueryAsync();

    var selectQuery = "SELECT id, name, model, android_version, battery, storage_used, storage_total, is_online, last_seen, app_list, storage_volumes, system_info FROM devices";
    using var cmd = new SqliteCommand(selectQuery, conn);
    using var reader = await cmd.ExecuteReaderAsync();
    
    var devices = new List<Device>();
    while (await reader.ReadAsync())
    {
        var rawVolumes = reader.IsDBNull(10) ? "[]" : reader.GetString(10);
        var volumes = JsonSerializer.Deserialize<List<StorageVolume>>(rawVolumes) ?? new();
        
        // Fallback if volumes list is empty but storage total is non-zero
        if (volumes.Count == 0 && reader.GetInt64(6) > 0)
        {
            volumes.Add(new StorageVolume("Internal Storage", reader.GetInt64(5), reader.GetInt64(6)));
        }

        var rawSysInfo = reader.IsDBNull(11) ? "{}" : reader.GetString(11);
        var sysInfo = JsonSerializer.Deserialize<SystemInfo>(rawSysInfo) ?? new SystemInfo("", "", -1, 0, 0, "", 0, "", false);

        devices.Add(new Device(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7) == 1,
            reader.GetString(8),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? new(),
            volumes,
            sysInfo
        ));
    }
    
    return Results.Json(devices);
});

// Enqueue remote command
app.MapPost("/api/devices/{deviceId}/commands", async (string deviceId, QueueCommandDto dto) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var query = @"
        INSERT INTO commands (id, device_id, command_type, payload, status, created_at, result)
        VALUES (@id, @device_id, @command_type, @payload, 'Pending', datetime('now'), '')";
    
    var commandId = Guid.NewGuid().ToString();
    using var cmd = new SqliteCommand(query, conn);
    cmd.Parameters.AddWithValue("@id", commandId);
    cmd.Parameters.AddWithValue("@device_id", deviceId);
    cmd.Parameters.AddWithValue("@command_type", dto.CommandType);
    cmd.Parameters.AddWithValue("@payload", dto.Payload);
    await cmd.ExecuteNonQueryAsync();
    
    return Results.Ok(new { success = true, commandId });
});

// Pending commands polling for Android Client
app.MapGet("/api/devices/{deviceId}/commands/pending", async (string deviceId) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var selectQuery = @"
        SELECT id, command_type, payload 
        FROM commands 
        WHERE device_id = @device_id AND status = 'Pending' 
        ORDER BY created_at ASC 
        LIMIT 1";
    
    using var cmd = new SqliteCommand(selectQuery, conn);
    cmd.Parameters.AddWithValue("@device_id", deviceId);
    using var reader = await cmd.ExecuteReaderAsync();
    
    if (await reader.ReadAsync())
    {
        var id = reader.GetString(0);
        var type = reader.GetString(1);
        var payload = reader.GetString(2);
        
        // Mark as Sent
        using var updateCmd = new SqliteCommand("UPDATE commands SET status = 'Sent' WHERE id = @id", conn);
        updateCmd.Parameters.AddWithValue("@id", id);
        await updateCmd.ExecuteNonQueryAsync();
        
        return Results.Ok(new { commandId = id, commandType = type, payload });
    }
    
    return Results.NotFound();
});

// Report command completion
app.MapPost("/api/devices/{deviceId}/commands/{commandId}/complete", async (string deviceId, string commandId, CompleteCommandDto dto) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var query = @"
        UPDATE commands 
        SET status = @status, completed_at = datetime('now'), result = @result 
        WHERE id = @id AND device_id = @device_id";
    using var cmd = new SqliteCommand(query, conn);
    cmd.Parameters.AddWithValue("@id", commandId);
    cmd.Parameters.AddWithValue("@device_id", deviceId);
    cmd.Parameters.AddWithValue("@status", dto.Success ? "Success" : "Failed");
    cmd.Parameters.AddWithValue("@result", dto.Result ?? "");
    await cmd.ExecuteNonQueryAsync();
    
    return Results.Ok(new { success = true });
});

// APK Upload Endpoint
app.MapPost("/api/devices/{deviceId}/upload-apk", async (string deviceId, IFormFile file, HttpRequest request) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded");
    
    var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
    if (!Directory.Exists(uploadDir))
        Directory.CreateDirectory(uploadDir);
    
    var fileName = $"pkg_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
    var filePath = Path.Combine(uploadDir, fileName);
    
    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }
    
    var hostUrl = $"{request.Scheme}://{request.Host}";
    var downloadUrl = $"{hostUrl}/uploads/{fileName}";
    
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var query = @"
        INSERT INTO commands (id, device_id, command_type, payload, status, created_at, result)
        VALUES (@id, @device_id, 'InstallApk', @payload, 'Pending', datetime('now'), '')";
    
    var commandId = Guid.NewGuid().ToString();
    using var cmd = new SqliteCommand(query, conn);
    cmd.Parameters.AddWithValue("@id", commandId);
    cmd.Parameters.AddWithValue("@device_id", deviceId);
    cmd.Parameters.AddWithValue("@payload", downloadUrl);
    await cmd.ExecuteNonQueryAsync();
    
    return Results.Ok(new { success = true, downloadUrl, commandId });
});

// Serves commands status list for dashboard
app.MapGet("/api/devices/{deviceId}/commands", async (string deviceId) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    var query = "SELECT id, command_type, payload, status, created_at, completed_at, result FROM commands WHERE device_id = @device_id ORDER BY created_at DESC LIMIT 50";
    using var cmd = new SqliteCommand(query, conn);
    cmd.Parameters.AddWithValue("@device_id", deviceId);
    using var reader = await cmd.ExecuteReaderAsync();
    
    var list = new List<object>();
    while (await reader.ReadAsync())
    {
        list.Add(new
        {
            Id = reader.GetString(0),
            CommandType = reader.GetString(1),
            Payload = reader.GetString(2),
            Status = reader.GetString(3),
            CreatedAt = reader.GetString(4),
            CompletedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
            Result = reader.IsDBNull(6) ? "" : reader.GetString(6)
        });
    }
    return Results.Json(list);
});

// Clear remote command log history
app.MapDelete("/api/devices/{deviceId}/commands", async (string deviceId) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    using var cmd = new SqliteCommand("DELETE FROM commands WHERE device_id = @device_id", conn);
    cmd.Parameters.AddWithValue("@device_id", deviceId);
    await cmd.ExecuteNonQueryAsync();
    
    return Results.Ok(new { success = true });
});

// Re-enqueue/Retry failed remote command
app.MapPost("/api/devices/{deviceId}/commands/{commandId}/retry", async (string deviceId, string commandId) =>
{
    using var conn = new SqliteConnection(ConnectionString);
    await conn.OpenAsync();
    
    using var findCmd = new SqliteCommand("SELECT command_type, payload FROM commands WHERE id = @id AND device_id = @device_id", conn);
    findCmd.Parameters.AddWithValue("@id", commandId);
    findCmd.Parameters.AddWithValue("@device_id", deviceId);
    using var reader = await findCmd.ExecuteReaderAsync();
    
    if (await reader.ReadAsync())
    {
        var commandType = reader.GetString(0);
        var payload = reader.GetString(1);
        
        var newId = Guid.NewGuid().ToString();
        var insertQuery = @"
            INSERT INTO commands (id, device_id, command_type, payload, status, created_at, result)
            VALUES (@id, @device_id, @command_type, @payload, 'Pending', datetime('now'), '')";
        using var insertCmd = new SqliteCommand(insertQuery, conn);
        insertCmd.Parameters.AddWithValue("@id", newId);
        insertCmd.Parameters.AddWithValue("@device_id", deviceId);
        insertCmd.Parameters.AddWithValue("@command_type", commandType);
        insertCmd.Parameters.AddWithValue("@payload", payload);
        await insertCmd.ExecuteNonQueryAsync();
        
        return Results.Ok(new { success = true, commandId = newId });
    }
    
    return Results.NotFound(new { error = "Command not found" });
});

// Admin Login Authentication
app.MapPost("/api/login", async (HttpContext context, LoginRequest request) =>
{
    var configPassword = builder.Configuration.GetValue<string>("ServerConfig:AdminPassword") ?? "admin";
    if (request.Password == configPassword)
    {
        var claims = new List<Claim> { new Claim(ClaimTypes.Name, "Administrator") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Results.Ok(new { success = true });
    }
    return Results.BadRequest(new { error = "Invalid password" });
});

// Admin Logout
app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { success = true });
});

// Read server configuration from appsettings.json
var bindAddress = builder.Configuration.GetValue<string>("ServerConfig:BindAddress") ?? "0.0.0.0";
var port = builder.Configuration.GetValue<int>("ServerConfig:Port");
if (port == 0) port = 18800;

app.Run($"http://{bindAddress}:{port}");

// ── DB Helpers & Records ──────────────────────────────────────────────────────

void InitializeDatabase()
{
    var dir = Path.GetDirectoryName(DbPath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);

    using var conn = new SqliteConnection(ConnectionString);
    conn.Open();
    
    // Create Devices Table
    var devicesTable = @"
        CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            model TEXT NOT NULL,
            android_version TEXT NOT NULL,
            battery INTEGER NOT NULL,
            storage_used INTEGER NOT NULL,
            storage_total INTEGER NOT NULL,
            is_online INTEGER NOT NULL,
            last_seen TEXT NOT NULL,
            app_list TEXT NOT NULL
        )";
    using var devCmd = new SqliteCommand(devicesTable, conn);
    devCmd.ExecuteNonQuery();
    
    // Create Commands Table
    var commandsTable = @"
        CREATE TABLE IF NOT EXISTS commands (
            id TEXT PRIMARY KEY,
            device_id TEXT NOT NULL,
            command_type TEXT NOT NULL,
            payload TEXT NOT NULL,
            status TEXT NOT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT
        )";
    using var cmdCmd = new SqliteCommand(commandsTable, conn);
    cmdCmd.ExecuteNonQuery();

    // Self-healing: Alter table to add result if missing
    using var alterCmd = new SqliteCommand("ALTER TABLE commands ADD COLUMN result TEXT", conn);
    try { alterCmd.ExecuteNonQuery(); } catch { }

    // Self-healing: Alter table to add storage_volumes if missing
    using var alterDevCmd = new SqliteCommand("ALTER TABLE devices ADD COLUMN storage_volumes TEXT NOT NULL DEFAULT '[]'", conn);
    try { alterDevCmd.ExecuteNonQuery(); } catch { }

    // Self-healing: Alter table to add system_info if missing
    using var alterDevInfoCmd = new SqliteCommand("ALTER TABLE devices ADD COLUMN system_info TEXT NOT NULL DEFAULT '{}'", conn);
    try { alterDevInfoCmd.ExecuteNonQuery(); } catch { }
}

public record RegisterDeviceDto(string Id, string Name, string Model, string AndroidVersion);
public record StorageVolume(string Name, long Used, long Total);
public record SystemInfo(string IpAddress, string ConnectionType, int WifiSignal, long TotalRam, long AvailableRam, string CpuArch, long Uptime, string Resolution, bool KeepScreenAwake, string? UpdateStatus = null);
public record ReportStatusDto(int Battery, long StorageUsed, long StorageTotal, List<string> AppList, List<StorageVolume>? StorageVolumes, SystemInfo? SystemInfo);
public record QueueCommandDto(string CommandType, string Payload);
public record CompleteCommandDto(bool Success, string? Result);
public record Device(string Id, string Name, string Model, string AndroidVersion, int Battery, long StorageUsed, long StorageTotal, bool IsOnline, string LastSeen, List<string> AppList, List<StorageVolume> StorageVolumes, SystemInfo SystemInfo);
public record LoginRequest(string Password);
