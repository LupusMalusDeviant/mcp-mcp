var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Voller Ausbau (MCP-Endpoint, REST-Fassade, AuthN) folgt in WP4/WP5.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
