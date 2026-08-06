using Crawldad.Web;
using JasperFx;

var builder = WebApplication.CreateBuilder(args);
builder.AddCrawldadPlatform();

var app = builder.Build();
app.MapCrawldadPlatform();

// RunJasperFxCommands replaces app.Run(): with no args it serves; with args it runs CLI commands
// (db-apply, projections, codegen, ...) and returns their exit code.
return await app.RunJasperFxCommands(args);

// This file is a thin shell (all wiring is in HostConfiguration); it is excluded from coverage by file in
// the test project. On .NET 10 the generated Program type is already test-visible, so no boilerplate is needed.
