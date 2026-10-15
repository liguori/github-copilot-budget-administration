using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace BudgetPortal.Web.Tests;

internal static class PreviewHost
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--preview"])
        {
            Console.Error.WriteLine("Use dotnet test for tests, or --preview for a loopback-only synthetic UI preview.");
            return 2;
        }

        await using var factory = new PortalFactory();
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 7443, endpoint => endpoint.UseHttps()));
        factory.StartServer();
        Console.WriteLine("TEST-ONLY preview: https://localhost:7443. Synthetic identity and budgets; no GitHub writes.");
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        return 0;
    }
}
