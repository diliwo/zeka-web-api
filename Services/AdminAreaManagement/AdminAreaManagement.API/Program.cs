using AdminAreaManagement.API;
using AdminAreaManagement.Application;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebServices();

builder.Services.AddControllers();


var app = builder.Build();

app.UseMigrationsAndSeed();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("CorsPolicy");
app.UseAuthorization();

app.MapControllers();

app.Run();