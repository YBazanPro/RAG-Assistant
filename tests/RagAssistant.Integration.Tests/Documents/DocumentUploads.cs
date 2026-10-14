using System.Text;
using Npgsql;

namespace RagAssistant.Integration.Tests.Documents;

internal static class DocumentUploads
{
    public const int DefaultMaxFileSizeBytes = 1_048_576;

    /// <summary>Content that has never been imported before, so tests sharing one database cannot trip the duplicate guard.</summary>
    public static byte[] UniqueMarkdown() => Encoding.UTF8.GetBytes($"# Policy {Guid.NewGuid()}\n\nBody text.\n");

    public static string UniqueTitle() => $"Policy {Guid.NewGuid()}";

    /// <summary>A multipart import form. Pass <c>null</c> for <paramref name="bytes"/> to send no file part.</summary>
    public static MultipartFormDataContent Form(
        string fileName,
        byte[]? bytes,
        string? title = "Budget Policy",
        string? ownerName = "Finance",
        params (string Name, string Value)[] extraFields)
    {
        var form = new MultipartFormDataContent();
        if (bytes is not null)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new("text/markdown");
            form.Add(file, "file", fileName);
        }

        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        if (ownerName is not null)
        {
            form.Add(new StringContent(ownerName), "ownerName");
        }

        foreach (var (name, value) in extraFields)
        {
            form.Add(new StringContent(value), name);
        }

        return form;
    }

    public static async Task<T?> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql, params object[] parameters)
    {
        await using var command = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
