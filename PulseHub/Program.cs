using Microsoft.EntityFrameworkCore;
using PulseHub; // make sure this matches your namespace

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

// --- ADD THIS: Register your DbContext ---
builder.Services.AddDbContext<PulseHubContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("LSDEV")));

// --- END OF ADDITION ---

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();