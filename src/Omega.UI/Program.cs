using Omega.UI.Api;
using Omega.UI.Components;
using Omega.UI.Presentation;

var builder = WebApplication.CreateBuilder(args);

// Interactive Server (ADR-016): the UI runs on the server, keeps a circuit per browser, and reads the system only
// through Omega.Api over HTTP. No project reference, no database, no exchange, no credentials.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var dashboard = builder.Configuration.GetSection(DashboardOptions.SectionName).Get<DashboardOptions>() ?? new DashboardOptions();
var errors = dashboard.Validate();
if (errors.Count > 0)
{
    throw new InvalidOperationException("Invalid Dashboard configuration: " + string.Join(" ", errors));
}

builder.Services.AddSingleton(dashboard);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<OmegaApiClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5080/");
    client.Timeout = dashboard.ApiTimeout;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
