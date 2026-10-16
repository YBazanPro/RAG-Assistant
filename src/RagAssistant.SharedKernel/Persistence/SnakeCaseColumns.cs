using System.Text;
using Microsoft.EntityFrameworkCore;

namespace RagAssistant.SharedKernel.Persistence;

public static class SnakeCaseColumns
{
    /// <summary>
    /// Names every column in snake_case, so raw SQL and index filters read naturally; set here rather than by a naming-convention package.
    /// Call at the end of <c>OnModelCreating</c>, after the configurations are applied.
    /// </summary>
    public static ModelBuilder UseSnakeCaseColumns(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }

        return modelBuilder;
    }

    public static string ToSnakeCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
