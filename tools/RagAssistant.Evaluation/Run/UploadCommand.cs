using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using RagAssistant.Evaluation.Api;

namespace RagAssistant.Evaluation.Run;

/// <summary>
/// <c>upload &lt;report&gt;</c> (spec 020): sends a report to the app's Evaluation module, so maintainers see and compare it in the
/// admin area. <c>run</c> and <c>judge</c> call it when they finish unless <c>--no-upload</c> is given. The newest
/// <c>judge-*.json</c> next to the report goes with it. Upload is idempotent by run id, so repeating it replaces the stored run.
/// </summary>
internal sealed class UploadCommand(RagAssistantApi api, IOptions<EvaluationOptions> options)
{
    /// <param name="reportPath">A run folder or its <c>report.json</c>.</param>
    /// <returns>0 when stored, 1 when the app refused it or could not be reached; the report on disk is never touched.</returns>
    public async Task<int> RunAsync(string reportPath, TextWriter output, CancellationToken cancellationToken)
    {
        var file = Directory.Exists(reportPath) ? Path.Combine(reportPath, "report.json") : reportPath;
        if (!File.Exists(file))
        {
            throw new HarnessException($"No report at {file}.");
        }

        var report = JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken))?.AsObject()
            ?? throw new HarnessException($"{file} is empty.");
        // The corpus passages are only for the offline sweep and judge; the app needs none of their text.
        report.Remove("passages");

        var judge = Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(file))!, "judge-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (judge is not null)
        {
            report["judge"] = JsonNode.Parse(await File.ReadAllTextAsync(judge, cancellationToken));
        }

        try
        {
            var response = await api.UploadReportAsync(options.Value.MaintainerUser, report, cancellationToken);
            if (!response.IsSuccess)
            {
                await output.WriteLineAsync($"Upload refused: {(int)response.Status} {response.Code} {response.Problem}".TrimEnd());
                return 1;
            }

            var verb = response.Status == System.Net.HttpStatusCode.Created ? "Uploaded" : "Replaced";
            await output.WriteLineAsync($"{verb} run {report["runId"]}{(judge is null ? "" : $" with {Path.GetFileName(judge)}")} in the app.");
            return 0;
        }
        catch (HarnessException ex)
        {
            // An app that is gone after a long run only costs the upload: the report is on disk, and 'upload' sends it later.
            await output.WriteLineAsync($"Upload skipped: {ex.Message}");
            return 1;
        }
    }
}
