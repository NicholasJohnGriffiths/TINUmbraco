using Umbraco.Cms.Core.Models;

namespace TINUmbraco.Web.Migrations;

public sealed class WordPressMigrationPropertyMapper : IWordPressMigrationPropertyMapper
{
    public void ApplyValues(IContent content, IReadOnlyDictionary<string, object?> values)
    {
        foreach ((string alias, object? value) in values)
        {
            if (content.Properties.Any(p => string.Equals(p.Alias, alias, StringComparison.OrdinalIgnoreCase)))
            {
                content.SetValue(alias, value);
            }
        }
    }
}