using System.Net;
using System.Net.Sockets;
using System.Text;
using Velopack;
using Velopack.Sources;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: FeedApplyHarness <feed-directory> [data-directory]");
    return 2;
}

var feedDirectory = Path.GetFullPath(args[0]);
var dataDirectory = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
if (!File.Exists(Path.Combine(feedDirectory, "releases.win.json")))
{
    throw new FileNotFoundException("The Velopack stable feed manifest is missing.", Path.Combine(feedDirectory, "releases.win.json"));
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var cancellation = new CancellationTokenSource();
var server = ServeFeedAsync(listener, feedDirectory, cancellation.Token);
var feedUrl = $"http://127.0.0.1:{port}/";
var manager = new UpdateManager(new SimpleWebSource(feedUrl));

if (!manager.IsInstalled)
{
    throw new InvalidOperationException("The helper did not resolve the real Velopack installation from its current directory.");
}

var update = await manager.CheckForUpdatesAsync()
    ?? throw new InvalidOperationException("The installed v0.17.1 app did not see the candidate in the hosted local feed.");
Console.WriteLine($"Feed check selected {update.TargetFullRelease?.Version}.");
await manager.DownloadUpdatesAsync(update, progress => Console.WriteLine($"Feed download: {progress}%"));
var pending = manager.UpdatePendingRestart
    ?? throw new InvalidOperationException("Velopack did not stage the downloaded candidate.");
var restartArguments = dataDirectory is null ? Array.Empty<string>() : new[] { "--data-dir", dataDirectory };
Console.WriteLine(dataDirectory is null
    ? $"Applying downloaded {pending.Version} and restarting through the portable marker."
    : $"Applying downloaded {pending.Version} and restarting with --data-dir {dataDirectory}.");

manager.ApplyUpdatesAndRestart(pending, restartArguments);

cancellation.Cancel();
listener.Stop();
await server;
return 0;

static async Task ServeFeedAsync(TcpListener listener, string root, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        TcpClient client;
        try
        {
            client = await listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(cancellationToken);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
            {
            }

            var requestPath = request?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
            var relativePath = Uri.UnescapeDataString(requestPath.TrimStart('/').Split('?')[0]);
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = "releases.win.json";
            }
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                await WriteResponseAsync(stream, "404 Not Found", ReadOnlyMemory<byte>.Empty, "text/plain", cancellationToken);
                continue;
            }

            var contentType = Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "application/octet-stream";
            await WriteFileResponseAsync(stream, fullPath, contentType, cancellationToken);
        }
    }
}

static async Task WriteFileResponseAsync(Stream stream, string path, string contentType, CancellationToken cancellationToken)
{
    var file = new FileInfo(path);
    var header = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 200 OK\r\nContent-Length: {file.Length}\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(header, cancellationToken);
    await using var contents = File.OpenRead(path);
    await contents.CopyToAsync(stream, cancellationToken);
}

static async Task WriteResponseAsync(Stream stream, string status, ReadOnlyMemory<byte> body, string contentType, CancellationToken cancellationToken)
{
    var header = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(body, cancellationToken);
}
