using Skladka;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["urls"] == null)
    builder.WebHost.UseUrls("http://127.0.0.1:8787");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = null);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST", "GET")));
var app = builder.Build();
app.UseCors();
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    await response.WriteAsJsonAsync(new { error = new { code = "http_error", message = $"HTTP {response.StatusCode}" } });
});
app.MapGet("/v1/health", () => new { status = "ok" });
app.MapGet("/v1/info", () => new { name = "Складка", version = "stable", project = "archive", specVersion = "v1" });
app.MapPost("/v1/compress", (HttpRequest request) => Transform(request, false));
app.MapPost("/v1/decompress", (HttpRequest request) => Transform(request, true));
app.Run();

static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = new { code, message } }, statusCode: status);

static async Task<IResult> Transform(HttpRequest request, bool extract)
{
    if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        return Error(415, "media_type", "Ожидается application/octet-stream.");
    try
    {
        var cancelled = request.HttpContext.RequestAborted;
        using var input = TemporaryFile();
        await request.Body.CopyToAsync(input, cancelled);
        input.Position = 0;
        var output = TemporaryFile();
        try
        {
            if (extract)
            {
                Archive.Unpack(input, output, _ => cancelled.ThrowIfCancellationRequested(), out bool folder);
                if(folder)
                {
                    var tree=FolderBundle.Read(output);
                    var zip=TemporaryFile();
                    try
                    {
                        tree.ToZip(output,zip);zip.Position=0;output.Dispose();
                        return Results.Stream(zip,"application/zip",tree.Root+".zip");
                    }
                    catch {zip.Dispose();throw;}
                }
            }
            else Archive.Pack(input, output, "", _ => cancelled.ThrowIfCancellationRequested());
            output.Position = 0;
            return Results.Stream(output, "application/octet-stream");
        }
        catch { output.Dispose(); throw; }
    }
    catch (BadHttpRequestException ex)
    {
        return Error(ex.StatusCode, "bad_request", "Не удалось прочитать тело запроса.");
    }
    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or DecoderFallbackException)
    {
        return Error(400, "invalid_archive", "Архив повреждён или формат не поддерживается.");
    }
}

static FileStream TemporaryFile()
{
    return new FileStream(Path.Combine(Path.GetTempPath(),Path.GetRandomFileName()), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 65536,
        FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
}
