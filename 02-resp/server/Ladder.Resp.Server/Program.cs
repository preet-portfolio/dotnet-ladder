using System.Globalization;
using System.Net;
using Ladder.Resp.Server;

// Usage: Ladder.Resp.Server [port]   (default 6380, so it can run beside a real server on 6379)
int port = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 6380;
await using var server = new RespServer(new IPEndPoint(IPAddress.Loopback, port));
server.Start();
Console.WriteLine($"ladder-resp listening on {server.LocalEndPoint}, pid {Environment.ProcessId}");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
