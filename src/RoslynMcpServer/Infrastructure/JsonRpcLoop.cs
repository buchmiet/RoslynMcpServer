using System.Diagnostics;
using StreamJsonRpc;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynMcpServer.Infrastructure
{
    public sealed class JsonRpcLoop
    {
        public async Task RunAsync(Stream input, Stream output, object target, CancellationToken ct)
        {
            // 1) Formatter case-insensitive (ważne dla różnic w casing pól MCP)
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
            formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

            // 2) NDJSON handler dla stdio (każdy komunikat = jedna linia)
            // Uwaga: pierwszy parametr = WRITER (writable, STDOUT), drugi = READER (readable, STDIN)
            var handler = new NewLineDelimitedMessageHandler(output, input, formatter)
            {
                NewLine = NewLineDelimitedMessageHandler.NewLineStyle.Lf
            };

            // 3) JsonRpc + pełny tracing → STDERR (STDOUT musi pozostać sterylny)
            var rpc = new JsonRpc(handler, target);
            rpc.TraceSource.Switch.Level = SourceLevels.Verbose;
            rpc.TraceSource.Listeners.Add(new TextWriterTraceListener(Console.Error));
            rpc.TraceSource.TraceEvent(TraceEventType.Information, 0, "Trace enabled");

            // 4) Słuchaj i czekaj do końca sesji (zalecany wzorzec)
            rpc.StartListening();
            await rpc.Completion;
        }
    }
}
