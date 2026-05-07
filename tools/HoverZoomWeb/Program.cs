using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
// 固定端口便于预览
builder.WebHost.UseUrls("http://localhost:5178");

var app = builder.Build();

// 静态文件 + 默认文件（index.html）
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();